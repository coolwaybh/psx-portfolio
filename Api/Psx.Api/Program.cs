using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Psx.Api.Data;
using Psx.Api.Entities;
using Psx.Api.Services;

var builder = WebApplication.CreateBuilder(args);

// MonsterASP's Premium-tier SQL Server fails the pre-login TLS handshake outright under the
// default Encrypt=Mandatory (confirmed after this account's DB was upgraded to Premium - same
// failure mode already seen and fixed on the sibling MIS project). Forced here on the parsed
// connection string, not just in appsettings.Production.json, because whatever the host's own
// panel provides (env var, JSON, or otherwise) is not guaranteed to survive a re-deploy or a
// panel-side connection-string reset the way a JSON edit alone would - this can't be silently
// overridden by any single config source.
var connectionStringBuilder = new SqlConnectionStringBuilder(builder.Configuration.GetConnectionString("Default"))
{
    Encrypt = false,
    TrustServerCertificate = true,
};

builder.Services.AddDbContext<PsxDbContext>(options =>
    options.UseSqlServer(connectionStringBuilder.ConnectionString,
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null)));

builder.Services.AddSingleton<PsxSymbolDirectory>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<FundamentalAnalysisService>();
builder.Services.AddScoped<MufapNavSyncService>();

builder.Services.AddHttpClient<PsxHistoricalPriceService>(client =>
{
    client.BaseAddress = new Uri("https://dps.psx.com.pk/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

// Bare client factory for the market-watch proxy below - a third-party CORS proxy
// relaying that ~470KB page to the browser was silently truncating it (confirmed by
// direct testing: symbols present in the real page were missing from what the client
// received), so this fetches it server-to-server instead, sidestepping both CORS and
// any third-party proxy's size/reliability limits.
builder.Services.AddHttpClient();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "psx_auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        options.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
    });
builder.Services.AddAuthorization(options =>
{
    // Gates the small "reset another user's password" admin surface (see /api/admin
    // below) - checked via a login-time claim rather than a per-request DB lookup, same
    // tradeoff the cookie itself already makes for UserId/Username. Only ever true for
    // the account seeded as admin by the AddIsAdmin migration - there's no self-service
    // way to become an admin (no promote-user endpoint exists, intentionally).
    options.AddPolicy("Admin", policy => policy.RequireClaim("IsAdmin", "true"));
});

builder.Services.AddRateLimiter(options =>
{
    // Partitioned per client IP - AddFixedWindowLimiter (no partition key) would share
    // ONE global counter across every visitor, letting any anonymous caller exhaust the
    // whole site's login/register budget and lock everyone else out for the rest of the
    // window. In-process ANCM hosting (see web.config hostingModel="inprocess") means
    // IIS and Kestrel share one process, so RemoteIpAddress already reflects the real
    // client - no ForwardedHeaders middleware needed here.
    options.AddPolicy("auth", httpContext => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 10,
            QueueLimit = 0
        }));
});

var app = builder.Build();

// Baseline hardening headers. The CSP allows 'unsafe-inline' for script/style because
// this app is a single-file page built entirely with inline onclick/oninput handlers and
// style attributes - a strict CSP would break every interactive element without a much
// larger refactor (converting all of them to addEventListener wiring). Still worth
// setting: it blocks loading script/frame/object content from any origin except the
// two CDNs this page actually uses, and frame-ancestors backs up X-Frame-Options against
// clickjacking. Tighten further (nonces, drop unsafe-inline) if the app ever moves away
// from inline handlers.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.Append("X-Content-Type-Options", "nosniff");
    headers.Append("X-Frame-Options", "DENY");
    headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
    headers.Append("Content-Security-Policy",
        "default-src 'self'; " +
        "script-src 'self' 'unsafe-inline' https://cdn.jsdelivr.net; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' https://fonts.gstatic.com; " +
        "img-src 'self' data:; " +
        "connect-src 'self' https://proxy.cors.sh https://api.allorigins.win; " +
        "object-src 'none'; " +
        "frame-ancestors 'self'; " +
        "base-uri 'self'");
    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

// ── AUTH ──────────────────────────────────────────────────────────────
var auth = app.MapGroup("/api/auth").RequireRateLimiting("auth");

auth.MapPost("/register", async (AuthRequest req, PsxDbContext db) =>
{
    var username = req.Username?.Trim() ?? "";
    if (username.Length < 3 || username.Length > 50 || !IsValidUsername(username))
        return Results.BadRequest(new { error = "Username must be 3-50 characters: letters, numbers, underscore, or dash only." });
    if (string.IsNullOrEmpty(req.Password) || req.Password.Length < 8 || req.Password.Length > 128)
        return Results.BadRequest(new { error = "Password must be 8-128 characters." });

    if (await db.Users.AnyAsync(u => u.Username == username))
        return Results.Conflict(new { error = "Username already taken." });

    var user = new User { Username = username, PasswordHash = PasswordHasher.Hash(req.Password) };
    user.Settings = new UserSettings { UserId = 0 };
    db.Users.Add(user);
    await db.SaveChangesAsync();

    return Results.Created($"/api/auth/{user.Id}", new { id = user.Id, username = user.Username });
});

auth.MapPost("/login", async (AuthRequest req, PsxDbContext db, HttpContext ctx) =>
{
    var username = req.Username?.Trim() ?? "";
    var user = await db.Users.FirstOrDefaultAsync(u => u.Username == username);

    // Always verify against a real hash - even for an unknown username - so response
    // timing can't reveal whether the account exists.
    var ok = PasswordHasher.Verify(req.Password ?? "", user?.PasswordHash ?? PasswordHasher.DummyHash);
    if (user is null || !ok)
        return Results.Json(new { error = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);

    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new(ClaimTypes.Name, user.Username),
    };
    if (user.IsAdmin)
        claims.Add(new Claim("IsAdmin", "true"));
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Ok(new { id = user.Id, username = user.Username, isAdmin = user.IsAdmin });
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
}).RequireAuthorization();

auth.MapGet("/me", (ClaimsPrincipal user) =>
    Results.Ok(new { id = user.GetUserId(), username = user.Identity!.Name, isAdmin = user.HasClaim("IsAdmin", "true") })
).RequireAuthorization();

auth.MapPost("/change-password", async (ChangePasswordRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    if (user is null) return Results.Unauthorized();

    if (!PasswordHasher.Verify(req.CurrentPassword ?? "", user.PasswordHash))
        return Results.Json(new { error = "Current password is incorrect." }, statusCode: StatusCodes.Status400BadRequest);

    if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 8 || req.NewPassword.Length > 128)
        return Results.BadRequest(new { error = "New password must be 8-128 characters." });

    user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
    await db.SaveChangesAsync();
    return Results.Ok();
}).RequireAuthorization();

// Re-verifies the current user's own password without touching the session or
// anything else - the confirmation step for Clear All Data (see confirmClearAll /
// confirmClearFunds), so a mis-click can't silently wipe a portfolio the way a plain
// confirm() dialog could.
auth.MapPost("/verify-password", async (AuthRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
    var ok = PasswordHasher.Verify(req.Password ?? "", user?.PasswordHash ?? PasswordHasher.DummyHash);
    if (!ok)
        return Results.Json(new { error = "Incorrect password." }, statusCode: StatusCodes.Status401Unauthorized);
    return Results.Ok();
}).RequireAuthorization();

// ── LEDGER ────────────────────────────────────────────────────────────
var ledger = app.MapGroup("/api/ledger").RequireAuthorization();

ledger.MapGet("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entries = await db.LedgerEntries
        .Where(t => t.UserId == userId)
        .OrderByDescending(t => t.TxDate)
        .ToListAsync();
    return Results.Ok(entries.Select(LedgerDto.From));
});

ledger.MapPost("/", async (LedgerCreateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    if (!TryBuildEntry(req, userId, out var entry, out var error))
        return Results.BadRequest(new { error });

    if (entry.Type == TxType.Sell)
    {
        var existing = await db.LedgerEntries.Where(t => t.UserId == userId).ToListAsync();
        var available = HoldingsCalculator.AvailableShares(existing, entry.Symbol);
        if (entry.Shares > available + 0.000000001m)
            return Results.BadRequest(new { error = $"Cannot sell {entry.Shares} shares — only {available} available." });
    }

    // CGT only ever applies to a Sell - like Shares/Price/Commission on a Split, ignore
    // whatever the client sent for any other type rather than trust it blindly.
    var cgtAmount = entry.Type == TxType.Sell ? req.CgtAmount : 0;
    if (cgtAmount < 0)
        return Results.BadRequest(new { error = "CGT amount cannot be negative." });

    // EnableRetryOnFailure() switches the DbContext to a retrying execution strategy,
    // which doesn't allow a bare db.Database.BeginTransactionAsync() - the strategy needs
    // to own the whole retriable unit (transaction + operations) so a transient failure
    // mid-transaction can be retried from the start instead of resuming a half-open one.
    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.LedgerEntries.Add(entry);
        await db.SaveChangesAsync();

        // A regular buy/sell auto-debits/credits free cash. Opening positions and bulk
        // imports skip this (req.SkipCashEntry) - those represent shares already held or
        // being backfilled, not a purchase made through the app right now. A Split is
        // structurally cash-neutral regardless of what the client sends - it never
        // creates a cash entry, since no money changes hands on a stock split. No check
        // against available free cash here - a negative balance is allowed, just tracked.
        if (entry.Type != TxType.Split && !req.SkipCashEntry)
        {
            var total = entry.Shares * entry.Price;
            var cashAmount = entry.Type == TxType.Buy
                ? total + entry.Commission + entry.NccplCharge
                : total - entry.Commission - entry.NccplCharge - cgtAmount;
            // Amount must stay positive (CashCalculator assumes magnitude, sign comes from
            // Type) - skip in the pathological case where a sell's commission (+ CGT) alone
            // would wipe out or exceed the proceeds, rather than fabricate a zero/negative row.
            if (cashAmount > 0)
            {
                var cashEntry = new CashEntry
                {
                    UserId = userId,
                    Type = entry.Type == TxType.Buy ? CashType.Withdrawal : CashType.Deposit,
                    Amount = cashAmount,
                    EntryDate = entry.TxDate,
                    Notes = $"{entry.Type} {entry.Shares} {entry.Symbol} @ {entry.Price}",
                    Symbol = entry.Symbol,
                    LedgerEntryId = entry.Id,
                    CgtAmount = cgtAmount > 0 ? cgtAmount : null,
                };
                db.CashEntries.Add(cashEntry);
                await db.SaveChangesAsync();
            }
        }
        await tx.CommitAsync();
    });

    return Results.Created($"/api/ledger/{entry.Id}", LedgerDto.From(entry));
});

// Narrow, deliberately scoped edit - only Commission/NccplCharge/Notes, never
// Shares/Price/Type/Symbol/Date. Those drive holdings and negative-balance validation
// (see HoldingsCalculator/CashCalculator), so changing them after the fact would need
// the same batch-replay checks as a delete+re-add; charges alone never affect share
// count, so they can be corrected in place with no revalidation beyond recomputing the
// one linked cash amount below. Exists specifically so a real broker-charge correction
// (e.g. backfilling the actual NCCPL clearing fee once known) doesn't require deleting
// and recreating a trade, which would also lose its original CreatedAt ordering.
ledger.MapPut("/{id:int}/charges", async (int id, LedgerChargesUpdateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.LedgerEntries.FirstOrDefaultAsync(t => t.Id == id);
    if (entry is null || entry.UserId != userId) return Results.NotFound();
    if (entry.Type == TxType.Split)
        return Results.BadRequest(new { error = "A split has no charges to edit." });
    if (req.Commission < 0 || req.NccplCharge < 0)
        return Results.BadRequest(new { error = "Charges cannot be negative." });
    if ((req.Notes?.Length ?? 0) > 1000)
        return Results.BadRequest(new { error = "Notes must be 1000 characters or fewer." });

    entry.Commission = req.Commission;
    entry.NccplCharge = req.NccplCharge;
    entry.Notes = req.Notes;

    var linkedCash = await db.CashEntries.FirstOrDefaultAsync(c => c.LedgerEntryId == id && c.UserId == userId);
    if (linkedCash is not null)
    {
        var total = entry.Shares * entry.Price;
        var cashAmount = entry.Type == TxType.Buy
            ? total + entry.Commission + entry.NccplCharge
            : total - entry.Commission - entry.NccplCharge - (linkedCash.CgtAmount ?? 0);
        if (cashAmount <= 0)
            return Results.BadRequest(new { error = "These charges would wipe out or exceed the trade's proceeds." });
        linkedCash.Amount = cashAmount;
    }

    await db.SaveChangesAsync();
    return Results.Ok(LedgerDto.From(entry));
});

ledger.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.LedgerEntries.FirstOrDefaultAsync(t => t.Id == id);
    // 404 (not 403) for entries owned by someone else, so we don't confirm another user's row exists.
    if (entry is null || entry.UserId != userId) return Results.NotFound();

    // Sell deletions (and reverse-split deletions, ratio<1) can only ever increase the
    // running balance retroactively - provably safe. Buy deletions and forward-split
    // deletions (ratio>1) can retroactively invalidate a later sell that depended on
    // them, so both need the negative-balance replay check.
    if (entry.Type != TxType.Sell)
    {
        var existing = await db.LedgerEntries.Where(t => t.UserId == userId).ToListAsync();
        if (!HoldingsCalculator.CanRemoveWithoutNegativeBalance(existing, entry.Symbol, entry.Id))
            return Results.BadRequest(new { error = "Cannot delete — would cause negative shares on a later sell." });
    }

    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        // Remove the auto-linked cash entry too, if this transaction created one - not
        // re-validated against the cash balance (same "allow negative" tradeoff as adding).
        var linkedCash = await db.CashEntries.FirstOrDefaultAsync(c => c.LedgerEntryId == id && c.UserId == userId);
        if (linkedCash is not null) db.CashEntries.Remove(linkedCash);

        db.LedgerEntries.Remove(entry);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    });
    return Results.Ok();
});

ledger.MapDelete("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var strategy = db.Database.CreateExecutionStrategy();
    int count = 0;
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        // Ledger entries can have a linked CashEntry (LedgerEntryId FK is NoAction, not
        // Cascade - see PsxDbContext.cs) - remove those first, mirroring the single-entry
        // delete endpoint above, or this bulk delete fails with a foreign-key violation
        // for any user with real buy/sell history. Only removes cash rows actually linked
        // to a ledger entry - manually-added cash entries (deposits, unlinked dividends)
        // are left untouched, same as deleting one transaction at a time would leave them.
        await db.CashEntries.Where(c => c.UserId == userId && c.LedgerEntryId != null).ExecuteDeleteAsync();
        count = await db.LedgerEntries.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    });
    return Results.Ok(new { deleted = count });
});

ledger.MapPost("/import", async (ImportRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var toAdd = new List<LedgerEntry>();
    var skipped = new List<string>();

    foreach (var item in req.Transactions)
    {
        if (!TryBuildEntry(item, userId, out var entry, out var error))
        {
            skipped.Add($"{item.Symbol} {item.Date}: {error}");
            continue;
        }
        toAdd.Add(entry);
    }

    // Validate the whole batch's running balance together (by symbol), not row-by-row
    // against already-committed state, since the import is all-or-mostly-new rows.
    foreach (var group in toAdd.GroupBy(e => e.Symbol))
    {
        decimal running = 0;
        foreach (var e in group.OrderBy(e => e.TxDate))
        {
            running = e.Type switch
            {
                TxType.Buy => running + e.Shares,
                TxType.Sell => running - e.Shares,
                TxType.Split => running * HoldingsCalculator.SplitRatio(e),
                _ => running
            };
            if (running < -0.000000001m)
            {
                skipped.Add($"{e.Symbol}: batch would go negative — skipping remaining {group.Key} entries");
                toAdd.RemoveAll(x => x.Symbol == group.Key);
                break;
            }
        }
    }

    db.LedgerEntries.AddRange(toAdd);
    await db.SaveChangesAsync();
    return Results.Ok(new { imported = toAdd.Count, skipped });
});

// Parses an Arif Habib "Memo of Confirmation" trade-confirmation PDF into candidate
// transactions for the user to review one-by-one - this endpoint never saves anything
// itself. The frontend posts each confirmed candidate to POST /api/ledger (above) the
// same way a manually-typed transaction is saved. The uploaded file is never persisted
// to disk - read into memory, parsed, and discarded within this request.
ledger.MapPost("/import/pdf", async (IFormFile file, PsxSymbolDirectory symbols) =>
{
    const long MaxFileSizeBytes = 5 * 1024 * 1024;
    if (file.Length > MaxFileSizeBytes)
        return Results.BadRequest(new { error = "File too large — please upload a PDF under 5MB." });

    PdfParseResult result;
    try
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        result = PdfConfirmationParser.Parse(buffer, symbols);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Ok(new { candidates = result.Candidates, warnings = result.Warnings });
})
// Minimal APIs auto-require antiforgery validation for any IFormFile-binding endpoint,
// even with no antiforgery middleware configured anywhere in this app (confirmed by
// testing - the endpoint throws a 500 without this call). This app's auth is cookie +
// SameSite=Lax, same as every other POST endpoint here.
.DisableAntiforgery();

// ── SETTINGS ──────────────────────────────────────────────────────────
var settings = app.MapGroup("/api/settings").RequireAuthorization();

settings.MapGet("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId);
    s ??= new UserSettings { UserId = userId };
    return Results.Ok(SettingsDto.From(s));
});

settings.MapPut("/", async (SettingsDto dto, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId);
    if (s is null)
    {
        s = new UserSettings { UserId = userId };
        db.UserSettings.Add(s);
    }
    s.CostMethod = dto.CostMethod;
    s.IncludeCommission = dto.IncludeCommission;
    s.ManualPricesJson = JsonSerializer.Serialize(dto.ManualPrices ?? new Dictionary<string, decimal>());
    if (dto.DividendTaxRatePct >= 0 && dto.DividendTaxRatePct <= 100)
        s.DividendTaxRatePct = dto.DividendTaxRatePct;
    if (dto.CgtRatePct >= 0 && dto.CgtRatePct <= 100)
        s.CgtRatePct = dto.CgtRatePct;
    if (dto.NccplChargeRatePct >= 0 && dto.NccplChargeRatePct <= 100)
        s.NccplChargeRatePct = dto.NccplChargeRatePct;
    s.OwnerName = string.IsNullOrWhiteSpace(dto.OwnerName) ? null : dto.OwnerName.Trim()[..Math.Min(dto.OwnerName.Trim().Length, 100)];
    await db.SaveChangesAsync();
    return Results.Ok(SettingsDto.From(s));
});

// ── HISTORICAL PRICES ─────────────────────────────────────────────────
var prices = app.MapGroup("/api/prices").RequireAuthorization();

prices.MapPost("/historical", async (HistoricalPriceRequest req, PsxHistoricalPriceService svc) =>
{
    if (!DateOnly.TryParse(req.Date, out var targetDate))
        return Results.BadRequest(new { error = "Date is invalid." });
    if (targetDate > DateOnly.FromDateTime(DateTime.UtcNow))
        return Results.BadRequest(new { error = "Date cannot be in the future." });

    var pricesOut = new Dictionary<string, decimal?>();
    var asOfOut = new Dictionary<string, string>();
    foreach (var sym in (req.Symbols ?? new List<string>()).Select(s => s.Trim().ToUpperInvariant()).Distinct())
    {
        var (close, actualDate) = await svc.GetPriceAsOf(sym, targetDate);
        pricesOut[sym] = close;
        if (close is not null && actualDate is not null)
            asOfOut[sym] = actualDate.Value.ToString("yyyy-MM-dd");
    }
    return Results.Ok(new { prices = pricesOut, asOfDates = asOfOut });
});

// Full per-symbol EOD series (not just one date) - feeds the Cumulative P&L chart, which
// values past holdings at what they were actually worth on each historical date rather
// than today's price.
prices.MapPost("/history", async (PriceHistoryRequest req, PsxHistoricalPriceService svc) =>
{
    var result = new Dictionary<string, List<EodPricePointDto>>();
    foreach (var sym in (req.Symbols ?? new List<string>()).Select(s => s.Trim().ToUpperInvariant()).Distinct())
    {
        var series = await svc.GetFullHistory(sym);
        result[sym] = series.Select(p => new EodPricePointDto(p.Date.ToString("yyyy-MM-dd"), p.Close)).ToList();
    }
    return Results.Ok(result);
});

// Server-side pass-through for PSX's live market-watch page - see the AddHttpClient()
// comment above for why this exists instead of the client fetching it directly via a
// third-party CORS proxy. Returns the raw HTML unchanged; the frontend's existing
// DOMParser-based table parsing (fetchPSXMarketWatch) is untouched, only the URL it
// fetches from changed. Fetch/fallback logic lives in PsxHtmlFetcher.
prices.MapGet("/market-watch", async (IHttpClientFactory httpFactory) =>
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(20);
    var html = await PsxHtmlFetcher.FetchAsync(client, "market-watch");
    return html is not null ? Results.Content(html, "text/html") : Results.StatusCode(StatusCodes.Status502BadGateway);
});

// Same server-to-server pass-through, for PSX's /indices page (KSE100 and friends) -
// a separate page from market-watch, with its own server-rendered table.
prices.MapGet("/indices", async (IHttpClientFactory httpFactory) =>
{
    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(20);
    var html = await PsxHtmlFetcher.FetchAsync(client, "indices");
    return html is not null ? Results.Content(html, "text/html") : Results.StatusCode(StatusCodes.Status502BadGateway);
});

// Total market-wide shares/value traded today - not on dps.psx.com.pk (neither
// /indices nor /market-watch expose an aggregate, only per-index/per-symbol rows), but
// the main www.psx.com.pk homepage's "Market Highlights" widget has it in a small
// <table id="activity"> with stable td ids (confirmed against the live page 2026-09-23):
// id="volume" -> total shares traded, id="value" -> total PKR value traded. (PSX's own
// markup reuses id="volume" a second time, by mistake, for the "Previous Close" row -
// harmless here since a regex Match takes the first occurrence, same document order as
// the real Volume row.) Parsed server-side with a small regex rather than pulling in an
// HTML parser package for three numbers, and cached briefly so the 15s client poll
// doesn't hit PSX's homepage that often.
prices.MapGet("/market-summary", async (IHttpClientFactory httpFactory, IMemoryCache cache) =>
{
    const string cacheKey = "market-summary";
    if (cache.TryGetValue(cacheKey, out MarketSummaryDto? cached) && cached is not null)
        return Results.Ok(cached);

    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(20);
    var html = await PsxHtmlFetcher.FetchUrlAsync(client, "https://www.psx.com.pk");
    if (html is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

    var volume = ExtractPsxStatValue(html, "volume");
    var value = ExtractPsxStatValue(html, "value");
    if (volume is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

    var result = new MarketSummaryDto(volume.Value, value);
    cache.Set(cacheKey, result, TimeSpan.FromSeconds(20));
    return Results.Ok(result);
});

// PSX's per-company profile page (Business Description, Key People, P/E, Market Cap, EPS/
// Sales/Profit financials) - feeds the Share Information overlay. Cached server-side
// (6h) unlike market-watch/indices: fundamentals don't move intraday, so re-scraping PSX
// on every click (from every user, every reopen) would be pure waste against an edge
// that's already shown flakiness under load. Symbol is restricted to alphanumerics -
// this proxies directly to a PSX path built from it, so an unvalidated value would make
// this an open fetch-anything-from-dps.psx.com.pk proxy.
prices.MapGet("/company/{symbol}", async (string symbol, IHttpClientFactory httpFactory, IMemoryCache cache) =>
{
    if (!Regex.IsMatch(symbol, "^[A-Za-z0-9]+$")) return Results.BadRequest();
    var sym = symbol.ToUpperInvariant();

    var cacheKey = $"company-profile:{sym}";
    if (cache.TryGetValue(cacheKey, out string? cached) && cached is not null)
        return Results.Content(cached, "text/html");

    var client = httpFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(20);
    var html = await PsxHtmlFetcher.FetchAsync(client, $"company/{sym}");
    if (html is null) return Results.StatusCode(StatusCodes.Status502BadGateway);

    cache.Set(cacheKey, html, TimeSpan.FromHours(6));
    return Results.Content(html, "text/html");
});

// ── FUNDAMENTAL VIEW ──────────────────────────────────────────────────
// One AI-researched snapshot per symbol (see FundamentalAnalysisService), cached in
// FundamentalViews and shared by every user - only the refresh action is per-user-rate-
// limited, reading a cached view is free and unrestricted for any logged-in user.
var fundamental = app.MapGroup("/api/fundamental").RequireAuthorization();

fundamental.MapGet("/{symbol}", async (string symbol, PsxDbContext db) =>
{
    var sym = symbol.Trim().ToUpperInvariant();
    if (!IsValidSymbol(sym)) return Results.BadRequest(new { error = "Invalid symbol." });

    var view = await db.FundamentalViews.FirstOrDefaultAsync(f => f.Symbol == sym);
    if (view is null) return Results.NotFound(new { cached = false });

    return Results.Ok(FundamentalViewDto.From(view));
});

fundamental.MapPost("/{symbol}/refresh", async (string symbol, ClaimsPrincipal principal, PsxDbContext db, FundamentalAnalysisService svc) =>
{
    var sym = symbol.Trim().ToUpperInvariant();
    if (!IsValidSymbol(sym)) return Results.BadRequest(new { error = "Invalid symbol." });

    var isAdmin = principal.HasClaim("IsAdmin", "true");
    User? user = null;
    if (!isAdmin)
    {
        var userId = principal.GetUserId();
        user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return Results.Unauthorized();

        if (user.LastFundamentalRefreshUtc is { } last && DateTime.UtcNow - last < TimeSpan.FromHours(24))
        {
            var retryAt = last.AddHours(24);
            return Results.Json(
                new { error = "You've used today's fundamental-analysis refresh. Try again later.", retryAfterUtc = retryAt },
                statusCode: StatusCodes.Status429TooManyRequests);
        }
    }

    FundamentalAnalysisResult result;
    try
    {
        result = await svc.AnalyzeAsync(sym);
    }
    catch (Exception)
    {
        // Rate-limit timestamp is only touched after a successful analysis (below) - a
        // failed attempt (AI/network error) must not burn the user's one refresh for the day.
        return Results.Json(new { error = "Fundamental analysis is temporarily unavailable — try again shortly." },
            statusCode: StatusCodes.Status502BadGateway);
    }

    var view = await db.FundamentalViews.FirstOrDefaultAsync(f => f.Symbol == sym);
    if (view is null)
    {
        view = new FundamentalView { Symbol = sym };
        db.FundamentalViews.Add(view);
    }
    view.Signal = result.Signal;
    view.Confidence = result.Confidence;
    view.Note = result.Note;
    view.SourcesJson = JsonSerializer.Serialize(result.Sources);
    view.GeneratedAtUtc = DateTime.UtcNow;

    if (user is not null) user.LastFundamentalRefreshUtc = DateTime.UtcNow;

    await db.SaveChangesAsync();
    return Results.Ok(FundamentalViewDto.From(view));
});

// ── CASH LEDGER ───────────────────────────────────────────────────────
var cash = app.MapGroup("/api/cash").RequireAuthorization();

cash.MapGet("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entries = await db.CashEntries
        .Where(c => c.UserId == userId)
        .OrderByDescending(c => c.EntryDate)
        .ToListAsync();
    return Results.Ok(new
    {
        balance = CashCalculator.Balance(entries),
        entries = entries.Select(CashDto.From)
    });
});

cash.MapPost("/", async (CashCreateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    if (!TryBuildCashEntry(req, userId, out var entry, out var error))
        return Results.BadRequest(new { error });

    if (entry.Type == CashType.Withdrawal)
    {
        var existing = await db.CashEntries.Where(c => c.UserId == userId).ToListAsync();
        var balance = CashCalculator.Balance(existing);
        if (entry.Amount > balance + 0.000000001m)
            return Results.BadRequest(new { error = $"Cannot withdraw {entry.Amount} — only {balance} available." });
    }

    // A dividend paid straight to the user's bank (common for PSX payouts) is recorded as
    // TWO real rows rather than a boolean exclusion-flag: the Dividend itself (counts
    // toward this script's total regardless of what happened to the cash) plus an
    // offsetting Withdrawal so free cash is correctly left unchanged. Both in one
    // transaction; dividend inserted first so the withdrawal's own overwithdraw check
    // (trivially satisfied, since the amounts are equal) sees its contribution already applied.
    if (entry.Type == CashType.Dividend && !req.CreditToCash)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            db.CashEntries.Add(entry);
            await db.SaveChangesAsync();

            var offset = new CashEntry
            {
                UserId = userId,
                Type = CashType.Withdrawal,
                Amount = entry.Amount,
                EntryDate = entry.EntryDate,
                Notes = $"Dividend offset — {entry.Symbol}",
                LinkedEntryId = entry.Id,
            };
            db.CashEntries.Add(offset);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });

        return Results.Created($"/api/cash/{entry.Id}", CashDto.From(entry));
    }

    db.CashEntries.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/api/cash/{entry.Id}", CashDto.From(entry));
});

cash.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.CashEntries.FirstOrDefaultAsync(c => c.Id == id);
    if (entry is null || entry.UserId != userId) return Results.NotFound();

    // Resolve the linked pair, if any, in either direction.
    var pair = entry.LinkedEntryId is int linkedId
        ? await db.CashEntries.FirstOrDefaultAsync(c => c.Id == linkedId && c.UserId == userId)
        : await db.CashEntries.FirstOrDefaultAsync(c => c.LinkedEntryId == entry.Id && c.UserId == userId);

    var idsToRemove = pair is null ? new[] { entry.Id } : new[] { entry.Id, pair.Id };

    var existing = await db.CashEntries.Where(c => c.UserId == userId).ToListAsync();
    if (!CashCalculator.CanRemoveWithoutNegativeBalance(existing, idsToRemove))
        return Results.BadRequest(new { error = "Cannot delete — would cause negative cash balance on a later withdrawal." });

    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.CashEntries.Remove(entry);
        if (pair is not null) db.CashEntries.Remove(pair);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    });
    return Results.Ok();
});

cash.MapDelete("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var strategy = db.Database.CreateExecutionStrategy();
    int count = 0;
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        // Break self-referencing dividend<->withdrawal links first (LinkedEntryId FK is
        // NoAction) before the bulk delete - avoids depending on exactly how SQL Server
        // orders constraint checks within a single multi-row DELETE against a
        // self-referencing table, same defensive reasoning as the ledger bulk delete.
        await db.CashEntries.Where(c => c.UserId == userId && c.LinkedEntryId != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LinkedEntryId, (int?)null));
        count = await db.CashEntries.Where(c => c.UserId == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    });
    return Results.Ok(new { deleted = count });
});

// ── MUTUAL FUNDS ──────────────────────────────────────────────────────
// Deliberately has no link to LedgerEntries/CashEntries - a bug here can't corrupt the
// stock side of the portfolio. Buy/Sell/Dividend amounts are always server-computed from
// Units/Nav/FrontLoadPct/BackLoadPct (never trusted from the client), same trust boundary
// as CashCreateRequest's dividend/CDC-hold branches above.
var funds = app.MapGroup("/api/funds").RequireAuthorization();

funds.MapGet("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var userFunds = await db.MutualFunds.Where(f => f.UserId == userId).ToListAsync();
    var fundIds = userFunds.Select(f => f.Id).ToList();
    var transactions = await db.FundTransactions
        .Where(t => t.UserId == userId)
        .OrderByDescending(t => t.TxDate)
        .ToListAsync();
    var history = await db.FundNavHistories
        .Where(h => fundIds.Contains(h.FundId))
        .OrderBy(h => h.AsOfDate)
        .ToListAsync();

    return Results.Ok(new
    {
        funds = userFunds.Select(FundDto.From),
        transactions = transactions.Select(FundTransactionDto.From),
        navHistory = history
            .GroupBy(h => h.FundId)
            .ToDictionary(g => g.Key.ToString(), g => g.Select(h => new FundNavPointDto(h.AsOfDate.ToString("yyyy-MM-dd"), h.Nav)))
    });
});

funds.MapPost("/funds", async (FundCreateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var name = (req.Name ?? "").Trim();
    if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
        return Results.BadRequest(new { error = "Name must be 1-200 characters." });
    if ((req.Amc?.Length ?? 0) > 100)
        return Results.BadRequest(new { error = "AMC must be 100 characters or fewer." });
    if ((req.Category?.Length ?? 0) > 50)
        return Results.BadRequest(new { error = "Category must be 50 characters or fewer." });
    if (req.CurrentNav <= 0)
        return Results.BadRequest(new { error = "Current NAV must be positive." });
    var navDate = DateOnly.FromDateTime(DateTime.UtcNow);
    if (!string.IsNullOrWhiteSpace(req.NavDate) && !DateOnly.TryParse(req.NavDate, out navDate))
        return Results.BadRequest(new { error = "NAV date is invalid." });

    var fund = new MutualFund
    {
        UserId = userId,
        Name = name,
        Amc = req.Amc ?? "",
        Category = req.Category ?? "",
        CurrentNav = req.CurrentNav,
        NavUpdatedAt = navDate,
    };

    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        db.MutualFunds.Add(fund);
        await db.SaveChangesAsync();
        db.FundNavHistories.Add(new FundNavHistory { FundId = fund.Id, Nav = req.CurrentNav, AsOfDate = navDate });
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    });

    return Results.Created($"/api/funds/funds/{fund.Id}", FundDto.From(fund));
});

funds.MapPost("/transactions/buy", async (FundTxBuyRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var (fund, date, error) = await TryBuildFundTx(req.FundId, req.Date, req.Notes, userId, db);
    if (fund is null) return Results.BadRequest(new { error });
    if (req.Units <= 0) return Results.BadRequest(new { error = "Units must be positive." });
    if (req.Nav <= 0) return Results.BadRequest(new { error = "NAV must be positive." });
    if (req.FrontLoadPct < 0 || req.FrontLoadPct > 100)
        return Results.BadRequest(new { error = "Front load % must be between 0 and 100." });

    var amount = Math.Round(req.Units * req.Nav * (1 + req.FrontLoadPct / 100m), 2, MidpointRounding.AwayFromZero);
    var entry = new FundTransaction
    {
        UserId = userId,
        FundId = fund!.Id,
        Type = FundTxType.Buy,
        TxDate = date,
        Units = req.Units,
        Nav = req.Nav,
        Amount = amount,
        FrontLoadPct = req.FrontLoadPct,
        Notes = req.Notes,
    };
    db.FundTransactions.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/api/funds/transactions/{entry.Id}", FundTransactionDto.From(entry));
});

funds.MapPost("/transactions/sell", async (FundTxSellRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var (fund, date, error) = await TryBuildFundTx(req.FundId, req.Date, req.Notes, userId, db);
    if (fund is null) return Results.BadRequest(new { error });
    if (req.Units <= 0) return Results.BadRequest(new { error = "Units must be positive." });
    if (req.Nav <= 0) return Results.BadRequest(new { error = "NAV must be positive." });
    if (req.BackLoadPct < 0 || req.BackLoadPct > 100)
        return Results.BadRequest(new { error = "Back load % must be between 0 and 100." });
    if (req.CgtAmount < 0)
        return Results.BadRequest(new { error = "CGT amount cannot be negative." });

    var existing = await db.FundTransactions.Where(t => t.UserId == userId).ToListAsync();
    var available = FundHoldingsCalculator.UnitsHeld(existing, fund!.Id);
    if (req.Units > available + 0.000000001m)
        return Results.BadRequest(new { error = $"Cannot sell {req.Units} units — only {available} available." });

    // The AMC withholds CGT on the realized gain before crediting redemption proceeds,
    // same as NCCPL does on a stock sell (see POST /api/ledger) - net it out of Amount
    // rather than track it as an un-netted disclosure figure, so realized P&L (Amount -
    // avgCost * units, see computeFundHoldings) already reads net of CGT.
    var grossAmount = req.Units * req.Nav * (1 - req.BackLoadPct / 100m);
    if (req.CgtAmount > grossAmount)
        return Results.BadRequest(new { error = "CGT amount cannot exceed the sale proceeds." });
    var amount = Math.Round(grossAmount - req.CgtAmount, 2, MidpointRounding.AwayFromZero);
    var entry = new FundTransaction
    {
        UserId = userId,
        FundId = fund.Id,
        Type = FundTxType.Sell,
        TxDate = date,
        Units = req.Units,
        Nav = req.Nav,
        Amount = amount,
        BackLoadPct = req.BackLoadPct,
        CgtAmount = req.CgtAmount > 0 ? req.CgtAmount : null,
        Notes = req.Notes,
    };
    db.FundTransactions.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/api/funds/transactions/{entry.Id}", FundTransactionDto.From(entry));
});

funds.MapPost("/transactions/dividend", async (FundDividendRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var (fund, date, error) = await TryBuildFundTx(req.FundId, req.Date, req.Notes, userId, db);
    if (fund is null) return Results.BadRequest(new { error });

    FundTransaction entry;
    if (string.Equals(req.Type, "reinvest", StringComparison.OrdinalIgnoreCase))
    {
        if (req.Units is not decimal units || units <= 0)
            return Results.BadRequest(new { error = "Units must be positive." });
        if (req.Nav is not decimal nav || nav <= 0)
            return Results.BadRequest(new { error = "NAV must be positive." });
        entry = new FundTransaction
        {
            UserId = userId,
            FundId = fund!.Id,
            Type = FundTxType.DividendReinvest,
            TxDate = date,
            Units = units,
            Nav = nav,
            Amount = Math.Round(units * nav, 2, MidpointRounding.AwayFromZero),
            Notes = req.Notes,
        };
    }
    else if (string.Equals(req.Type, "cash", StringComparison.OrdinalIgnoreCase))
    {
        if (req.Amount is not decimal amount || amount <= 0)
            return Results.BadRequest(new { error = "Amount must be positive." });
        entry = new FundTransaction
        {
            UserId = userId,
            FundId = fund!.Id,
            Type = FundTxType.DividendCash,
            TxDate = date,
            Amount = amount,
            Notes = req.Notes,
        };
    }
    else
    {
        return Results.BadRequest(new { error = "Type must be 'reinvest' or 'cash'." });
    }

    db.FundTransactions.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/api/funds/transactions/{entry.Id}", FundTransactionDto.From(entry));
});

// Narrow, deliberately scoped edit - only Date/Notes, never Units/Nav/Type/FundId. Those
// drive units-held and negative-balance validation (see FundHoldingsCalculator), so
// changing them after the fact would need the same batch-replay checks as a delete +
// re-add; same reasoning as ledger.MapPut("/{id:int}/charges") above.
funds.MapPut("/transactions/{id:int}", async (int id, FundTxUpdateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.FundTransactions.FirstOrDefaultAsync(t => t.Id == id);
    if (entry is null || entry.UserId != userId) return Results.NotFound();
    if (!DateOnly.TryParse(req.Date, out var date))
        return Results.BadRequest(new { error = "Date is invalid." });
    if ((req.Notes?.Length ?? 0) > 1000)
        return Results.BadRequest(new { error = "Notes must be 1000 characters or fewer." });

    entry.TxDate = date;
    entry.Notes = req.Notes;
    await db.SaveChangesAsync();
    return Results.Ok(FundTransactionDto.From(entry));
});

funds.MapDelete("/transactions/{id:int}", async (int id, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.FundTransactions.FirstOrDefaultAsync(t => t.Id == id);
    if (entry is null || entry.UserId != userId) return Results.NotFound();

    var existing = await db.FundTransactions.Where(t => t.UserId == userId).ToListAsync();
    if (!FundHoldingsCalculator.CanRemoveWithoutNegativeBalance(existing, entry.FundId, entry.Id))
        return Results.BadRequest(new { error = "Cannot delete — would cause negative units held on a later sell." });

    db.FundTransactions.Remove(entry);
    await db.SaveChangesAsync();
    return Results.Ok();
});

// Wipes every fund the user tracks - the Mutual Funds half of Settings' Clear All Data
// (see ledger.MapDelete("/") for the stocks half), gated client-side behind its own
// confirmation + password re-entry so it can never fire as a side effect of clearing
// the other data set.
funds.MapDelete("/", async (ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var strategy = db.Database.CreateExecutionStrategy();
    int count = 0;
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        // FundTransactions.FundId -> MutualFunds has no cascade (EF drops the second
        // cascade path once Users -> MutualFunds already cascades - see the
        // AddMutualFunds migration), so transactions must go first or the fund delete
        // below hits a foreign-key violation. FundNavHistories does cascade, but it's
        // removed explicitly too so this endpoint stays correct even if that changes.
        await db.FundTransactions.Where(t => t.UserId == userId).ExecuteDeleteAsync();
        var fundIds = await db.MutualFunds.Where(f => f.UserId == userId).Select(f => f.Id).ToListAsync();
        await db.FundNavHistories.Where(h => fundIds.Contains(h.FundId)).ExecuteDeleteAsync();
        count = await db.MutualFunds.Where(f => f.UserId == userId).ExecuteDeleteAsync();
        await tx.CommitAsync();
    });
    return Results.Ok(new { deleted = count });
});

// Parses a UBL/Al-Ameen portfolio statement PDF into candidate opening positions for
// the user to review one-by-one - this endpoint never saves anything itself, same
// contract as POST /api/ledger/import/pdf. Two statement formats are recognized (see
// FundStatementParser and FundCostStatementParser) - tried in turn, first one to
// return any candidates wins, since a PDF can only match one format's anchor text. The
// frontend posts each confirmed candidate as a normal Buy (0% load - see
// setFundOpeningModeUI), creating the fund first if it doesn't exist yet, and - when
// the candidate carries a MarketPrice (the Investment Cost format only) - also pushes
// a NAV update so unrealized gain/loss is correct immediately. The uploaded file is
// never persisted to disk.
funds.MapPost("/import/pdf", async (IFormFile file) =>
{
    const long MaxFileSizeBytes = 5 * 1024 * 1024;
    if (file.Length > MaxFileSizeBytes)
        return Results.BadRequest(new { error = "File too large — please upload a PDF under 5MB." });

    FundStatementParseResult result;
    try
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        buffer.Position = 0;
        result = FundStatementParser.Parse(buffer);
        if (result.Candidates.Count == 0)
        {
            buffer.Position = 0;
            var costResult = FundCostStatementParser.Parse(buffer);
            if (costResult.Candidates.Count > 0) result = costResult;
        }
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }

    return Results.Ok(new { candidates = result.Candidates, warnings = result.Warnings });
})
.DisableAntiforgery();

funds.MapPost("/nav-updates", async (FundNavBulkUpdateRequest req, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    if (req.Updates is null || req.Updates.Count == 0)
        return Results.BadRequest(new { error = "At least one NAV update is required." });

    var fundIds = req.Updates.Select(u => u.FundId).Distinct().ToList();
    var userFunds = await db.MutualFunds.Where(f => f.UserId == userId && fundIds.Contains(f.Id)).ToListAsync();
    if (userFunds.Count != fundIds.Count)
        return Results.BadRequest(new { error = "One or more funds were not found." });

    var parsed = new List<(MutualFund Fund, decimal Nav, DateOnly Date)>();
    foreach (var u in req.Updates)
    {
        if (u.Nav <= 0)
            return Results.BadRequest(new { error = "NAV must be positive." });
        if (!DateOnly.TryParse(u.Date, out var date))
            return Results.BadRequest(new { error = "Date is invalid." });
        parsed.Add((userFunds.First(f => f.Id == u.FundId), u.Nav, date));
    }

    var strategy = db.Database.CreateExecutionStrategy();
    await strategy.ExecuteAsync(async () =>
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        foreach (var (fund, nav, date) in parsed)
        {
            var row = await db.FundNavHistories.FirstOrDefaultAsync(h => h.FundId == fund.Id && h.AsOfDate == date);
            if (row is null)
                db.FundNavHistories.Add(new FundNavHistory { FundId = fund.Id, Nav = nav, AsOfDate = date });
            else
                row.Nav = nav;

            // Only advance CurrentNav forward - a backdated correction shouldn't clobber
            // a more recent NAV already on record.
            if (date >= fund.NavUpdatedAt)
            {
                fund.CurrentNav = nav;
                fund.NavUpdatedAt = date;
            }
        }
        await db.SaveChangesAsync();
        await tx.CommitAsync();
    });

    return Results.Ok(new { updated = parsed.Count });
});

// Pulls today's NAV for every fund this user tracks from MUFAP's daily industry-wide publish
// (see MufapNavSyncService) - called once automatically after the frontend loads (see
// syncMufapNavs() in index.html) and available as a manual "Sync NAVs" button. The underlying
// scrape is shared and cached across every user, so calling this often is cheap - MUFAP itself
// is only actually fetched once per calendar day regardless of how many users trigger it.
funds.MapPost("/sync-mufap-navs", async (ClaimsPrincipal principal, MufapNavSyncService svc) =>
{
    var userId = principal.GetUserId();
    var result = await svc.SyncAsync(userId);
    return Results.Ok(new { updated = result.Updated, totalFunds = result.TotalFunds, unmatched = result.Unmatched });
});

// ── ADMIN ─────────────────────────────────────────────────────────────
// No self-service email/token password reset in this app (small trusted user base, no
// email sending set up) - instead the single seeded admin account (see AddIsAdmin
// migration) can set a forgotten password directly. Deliberately narrow: this can only
// ever touch PasswordHash, never any other user's ledger/cash/settings data.
var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin");

admin.MapGet("/users", async (PsxDbContext db) =>
{
    var users = await db.Users
        .OrderBy(u => u.Username)
        .Select(u => new { u.Id, u.Username, u.CreatedAt })
        .ToListAsync();
    return Results.Ok(users);
});

admin.MapPost("/users/{id:int}/reset-password", async (int id, AdminResetPasswordRequest req, PsxDbContext db) =>
{
    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id);
    if (user is null) return Results.NotFound();

    if (string.IsNullOrEmpty(req.NewPassword) || req.NewPassword.Length < 8 || req.NewPassword.Length > 128)
        return Results.BadRequest(new { error = "New password must be 8-128 characters." });

    user.PasswordHash = PasswordHasher.Hash(req.NewPassword);
    await db.SaveChangesAsync();
    return Results.Ok(new { username = user.Username });
});

app.Run();

static long? ExtractPsxStatValue(string html, string tdId)
{
    var m = Regex.Match(html, $@"id=""{tdId}""[^>]*>\s*([\d,]+)");
    return m.Success && long.TryParse(m.Groups[1].Value.Replace(",", ""), out var v) ? v : null;
}

static bool IsValidUsername(string s) => Regex.IsMatch(s, @"^[A-Za-z0-9_\-]+$");
static bool IsValidSymbol(string s) => Regex.IsMatch(s, @"^[A-Z0-9\-]+$");

static bool TryBuildEntry(LedgerCreateRequest req, int userId, out LedgerEntry entry, out string error)
{
    entry = null!;
    error = "";

    if (!Enum.TryParse<TxType>(req.Type, ignoreCase: true, out var type))
    {
        error = "Type must be 'buy', 'sell', or 'split'.";
        return false;
    }
    var symbol = (req.Symbol ?? "").Trim().ToUpperInvariant();
    if (string.IsNullOrWhiteSpace(symbol) || symbol.Length > 20 || !IsValidSymbol(symbol))
    {
        error = "Symbol must be 1-20 characters: letters, numbers, or dash only.";
        return false;
    }
    if ((req.Sector?.Length ?? 0) > 50)
    {
        error = "Sector must be 50 characters or fewer.";
        return false;
    }
    if ((req.Notes?.Length ?? 0) > 1000)
    {
        error = "Notes must be 1000 characters or fewer.";
        return false;
    }
    if (!DateOnly.TryParse(req.Date, out var date))
    {
        error = "Date is invalid.";
        return false;
    }

    // A Split is a marker, not a quantity/price event - Shares/Price/Commission/
    // NccplCharge are always forced to 0 regardless of what the client sends, and the
    // ratio is validated in its own two fields instead.
    decimal shares = 0, price = 0, commission = 0, nccplCharge = 0;
    decimal? splitRatioTo = null, splitRatioFrom = null;

    if (type == TxType.Split)
    {
        if (req.SplitRatioTo is not decimal to || to <= 0)
        {
            error = "Split ratio (new shares) must be positive.";
            return false;
        }
        if (req.SplitRatioFrom is not decimal from || from <= 0)
        {
            error = "Split ratio (old shares) must be positive.";
            return false;
        }
        splitRatioTo = to;
        splitRatioFrom = from;
    }
    else
    {
        if (req.Shares <= 0)
        {
            error = "Shares must be positive.";
            return false;
        }
        if (req.Price <= 0)
        {
            error = "Price must be positive.";
            return false;
        }
        shares = req.Shares;
        price = req.Price;
        commission = req.Commission;
        nccplCharge = req.NccplCharge;
    }

    entry = new LedgerEntry
    {
        UserId = userId,
        Type = type,
        Symbol = symbol,
        Sector = req.Sector ?? "",
        Shares = shares,
        Price = price,
        Commission = commission,
        NccplCharge = nccplCharge,
        TxDate = date,
        Notes = req.Notes,
        SplitRatioTo = splitRatioTo,
        SplitRatioFrom = splitRatioFrom,
    };
    return true;
}

static bool TryBuildCashEntry(CashCreateRequest req, int userId, out CashEntry entry, out string error)
{
    entry = null!;
    error = "";

    if (!Enum.TryParse<CashType>(req.Type, ignoreCase: true, out var type))
    {
        error = "Type must be 'deposit', 'withdrawal', or 'dividend'.";
        return false;
    }
    if (!DateOnly.TryParse(req.Date, out var date))
    {
        error = "Date is invalid.";
        return false;
    }
    if ((req.Notes?.Length ?? 0) > 1000)
    {
        error = "Notes must be 1000 characters or fewer.";
        return false;
    }

    string? symbol = null;
    decimal amount;
    decimal? grossAmount = null;
    decimal? taxRatePct = null;
    decimal? cdcHoldAmount = null;

    if (type == CashType.Dividend)
    {
        var sym = (req.Symbol ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(sym) || sym.Length > 20 || !IsValidSymbol(sym))
        {
            error = "Symbol must be 1-20 characters: letters, numbers, or dash only.";
            return false;
        }
        symbol = sym;

        if (req.GrossAmount is not decimal gross || gross <= 0)
        {
            error = "Gross amount must be positive.";
            return false;
        }
        var rate = req.TaxRatePct ?? 0;
        if (rate < 0 || rate > 100)
        {
            error = "Tax rate must be between 0 and 100.";
            return false;
        }

        // Server computes the NET (post-tax) amount itself - never trusts a
        // client-supplied Amount for a dividend, so Gross/Rate/Amount can't drift
        // out of sync with each other. GrossAmount keeps the precise (possibly
        // fractional) declared figure, but the actual cash movement - tax withheld
        // and net credited - is always whole rupees in practice (CDC/bank transfers
        // don't move paisas), so both are rounded to 0dp here. Rounding the tax
        // first and deriving Amount = Gross - tax (rather than rounding each
        // independently) keeps them tied to Gross exactly: whole-rupee rounding is
        // shift-invariant, so (Gross - tax) rounds to (round(Gross) - tax) - meaning
        // tax + Amount always equals Gross rounded to the nearest rupee, and any
        // downstream display that re-derives tax as GrossAmount - Amount and rounds
        // it will reconstruct this same whole tax value.
        grossAmount = gross;
        taxRatePct = rate;
        var taxWhole = Math.Round(gross * rate / 100m, 0, MidpointRounding.AwayFromZero);
        amount = Math.Round(gross - taxWhole, 0, MidpointRounding.AwayFromZero);
    }
    else if (type == CashType.Deposit && req.CdcHoldAmount is decimal hold)
    {
        // CDC top-up flow: client sends the pre-hold deposit as GrossAmount and the
        // held-back amount separately - server derives the actually-credited Amount
        // itself so it can never drift from Gross - Hold (same reasoning as the
        // dividend branch above never trusting a client-computed net).
        if (req.GrossAmount is not decimal depositGross || depositGross <= 0)
        {
            error = "Deposit amount must be positive.";
            return false;
        }
        if (hold < 0 || hold >= depositGross)
        {
            error = "CDC hold amount must be between 0 and the deposit amount.";
            return false;
        }
        grossAmount = depositGross;
        cdcHoldAmount = hold;
        amount = depositGross - hold;
    }
    else
    {
        if (req.Amount is not decimal a || a <= 0)
        {
            error = "Amount must be positive.";
            return false;
        }
        amount = a;
    }

    entry = new CashEntry
    {
        UserId = userId,
        Type = type,
        Amount = amount,
        EntryDate = date,
        Notes = req.Notes,
        Symbol = symbol,
        GrossAmount = grossAmount,
        TaxRatePct = taxRatePct,
        CdcHoldAmount = cdcHoldAmount,
    };
    return true;
}

// Shared setup for the three fund-transaction endpoints (buy/sell/dividend): resolves and
// authorizes the fund and parses the date. Can't use the out-param TryBuild* shape the
// cash/ledger helpers above use, since it needs an async DB lookup and async methods
// can't declare out parameters - returns a tuple instead, with Fund == null meaning
// validation failed and Error explaining why.
static async Task<(MutualFund? Fund, DateOnly Date, string? Error)> TryBuildFundTx(
    int fundId, string? dateStr, string? notes, int userId, PsxDbContext db)
{
    var fund = await db.MutualFunds.FirstOrDefaultAsync(f => f.Id == fundId && f.UserId == userId);
    if (fund is null) return (null, default, "Fund not found.");
    if (!DateOnly.TryParse(dateStr, out var date)) return (null, default, "Date is invalid.");
    if ((notes?.Length ?? 0) > 1000) return (null, default, "Notes must be 1000 characters or fewer.");
    return (fund, date, null);
}

record AuthRequest(string? Username, string? Password);
record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
record AdminResetPasswordRequest(string? NewPassword);
record LedgerCreateRequest(string Type, string Symbol, string? Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes, bool SkipCashEntry = false, decimal? SplitRatioTo = null, decimal? SplitRatioFrom = null, decimal CgtAmount = 0, decimal NccplCharge = 0);
record ImportRequest(List<LedgerCreateRequest> Transactions);
record LedgerChargesUpdateRequest(decimal Commission, decimal NccplCharge, string? Notes);
record SettingsDto(string CostMethod, bool IncludeCommission, Dictionary<string, decimal> ManualPrices, decimal DividendTaxRatePct, string? OwnerName, decimal CgtRatePct = 15m, decimal NccplChargeRatePct = 0.007m)
{
    public static SettingsDto From(UserSettings s) => new(
        s.CostMethod,
        s.IncludeCommission,
        JsonSerializer.Deserialize<Dictionary<string, decimal>>(s.ManualPricesJson) ?? new(),
        s.DividendTaxRatePct,
        s.OwnerName,
        s.CgtRatePct,
        s.NccplChargeRatePct
    );
}
record LedgerDto(int Id, string Type, string Symbol, string Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes, decimal? SplitRatioTo, decimal? SplitRatioFrom, decimal NccplCharge)
{
    public static LedgerDto From(LedgerEntry e) => new(
        e.Id, e.Type.ToString().ToLowerInvariant(), e.Symbol, e.Sector, e.Shares, e.Price, e.Commission,
        e.TxDate.ToString("yyyy-MM-dd"), e.Notes, e.SplitRatioTo, e.SplitRatioFrom, e.NccplCharge
    );
}
record CashCreateRequest(string Type, string Date, string? Notes, decimal? Amount = null, string? Symbol = null, bool CreditToCash = true, decimal? GrossAmount = null, decimal? TaxRatePct = null, decimal? CdcHoldAmount = null);
record HistoricalPriceRequest(string Date, List<string>? Symbols);
record PriceHistoryRequest(List<string>? Symbols);
record EodPricePointDto(string Date, decimal Close);
record MarketSummaryDto(long Volume, long? Value);
record FundamentalSourceOut(string Title, string Url);
record FundamentalViewDto(string Symbol, string Signal, string Confidence, string Note, List<FundamentalSourceOut> Sources, string GeneratedAtUtc)
{
    public static FundamentalViewDto From(FundamentalView f) => new(
        f.Symbol, f.Signal, f.Confidence, f.Note,
        JsonSerializer.Deserialize<List<FundamentalSourceOut>>(f.SourcesJson) ?? new(),
        f.GeneratedAtUtc.ToString("o")
    );
}
record CashDto(int Id, string Type, decimal Amount, string Date, string? Notes, string? Symbol, int? LinkedEntryId, decimal? GrossAmount, decimal? TaxRatePct, int? LedgerEntryId, decimal? CgtAmount, decimal? CdcHoldAmount)
{
    public static CashDto From(CashEntry e) => new(
        e.Id, e.Type.ToString().ToLowerInvariant(), e.Amount, e.EntryDate.ToString("yyyy-MM-dd"), e.Notes,
        e.Symbol, e.LinkedEntryId, e.GrossAmount, e.TaxRatePct, e.LedgerEntryId, e.CgtAmount, e.CdcHoldAmount
    );
}

record FundCreateRequest(string Name, string? Amc, string? Category, decimal CurrentNav, string? NavDate);
record FundTxBuyRequest(int FundId, string Date, decimal Units, decimal Nav, decimal FrontLoadPct, string? Notes);
record FundTxSellRequest(int FundId, string Date, decimal Units, decimal Nav, decimal BackLoadPct, string? Notes, decimal CgtAmount = 0);
record FundDividendRequest(int FundId, string Date, string Type, decimal? Units, decimal? Nav, decimal? Amount, string? Notes);
record FundTxUpdateRequest(string Date, string? Notes);
record FundNavUpdateItem(int FundId, decimal Nav, string Date);
record FundNavBulkUpdateRequest(List<FundNavUpdateItem> Updates);
record FundNavPointDto(string Date, decimal Nav);

record FundDto(int Id, string Name, string Amc, string Category, decimal CurrentNav, string NavUpdatedAt)
{
    public static FundDto From(MutualFund f) => new(
        f.Id, f.Name, f.Amc, f.Category, f.CurrentNav, f.NavUpdatedAt.ToString("yyyy-MM-dd")
    );
}

record FundTransactionDto(int Id, int FundId, string Type, string Date, decimal Units, decimal Nav, decimal Amount, decimal FrontLoadPct, decimal BackLoadPct, string? Notes, decimal? CgtAmount)
{
    public static FundTransactionDto From(FundTransaction t) => new(
        t.Id, t.FundId, t.Type.ToString().ToLowerInvariant(), t.TxDate.ToString("yyyy-MM-dd"), t.Units, t.Nav, t.Amount,
        t.FrontLoadPct, t.BackLoadPct, t.Notes, t.CgtAmount
    );
}

static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
