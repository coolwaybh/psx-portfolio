using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Psx.Api.Data;
using Psx.Api.Entities;
using Psx.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<PsxDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default"),
        sql => sql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorNumbersToAdd: null)));

builder.Services.AddSingleton<PsxSymbolDirectory>();

builder.Services.AddHttpClient<PsxHistoricalPriceService>(client =>
{
    client.BaseAddress = new Uri("https://dps.psx.com.pk/");
    client.Timeout = TimeSpan.FromSeconds(10);
});

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
builder.Services.AddAuthorization();

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
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

    return Results.Ok(new { id = user.Id, username = user.Username });
});

auth.MapPost("/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Ok();
}).RequireAuthorization();

auth.MapGet("/me", (ClaimsPrincipal user) =>
    Results.Ok(new { id = user.GetUserId(), username = user.Identity!.Name })
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
            var cashAmount = entry.Type == TxType.Buy ? total + entry.Commission : total - entry.Commission;
            // Amount must stay positive (CashCalculator assumes magnitude, sign comes from
            // Type) - skip in the pathological case where a sell's commission alone would
            // wipe out or exceed the proceeds, rather than fabricate a zero/negative row.
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
                };
                db.CashEntries.Add(cashEntry);
                await db.SaveChangesAsync();
            }
        }
        await tx.CommitAsync();
    });

    return Results.Created($"/api/ledger/{entry.Id}", LedgerDto.From(entry));
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
    var count = await db.LedgerEntries.Where(t => t.UserId == userId).ExecuteDeleteAsync();
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
    var count = await db.CashEntries.Where(c => c.UserId == userId).ExecuteDeleteAsync();
    return Results.Ok(new { deleted = count });
});

app.Run();

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

    // A Split is a marker, not a quantity/price event - Shares/Price/Commission are
    // always forced to 0 regardless of what the client sends, and the ratio is
    // validated in its own two fields instead.
    decimal shares = 0, price = 0, commission = 0;
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
        // out of sync with each other.
        grossAmount = gross;
        taxRatePct = rate;
        amount = Math.Round(gross * (1 - rate / 100m), 4, MidpointRounding.AwayFromZero);
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
    };
    return true;
}

record AuthRequest(string? Username, string? Password);
record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);
record LedgerCreateRequest(string Type, string Symbol, string? Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes, bool SkipCashEntry = false, decimal? SplitRatioTo = null, decimal? SplitRatioFrom = null);
record ImportRequest(List<LedgerCreateRequest> Transactions);
record SettingsDto(string CostMethod, bool IncludeCommission, Dictionary<string, decimal> ManualPrices, decimal DividendTaxRatePct, string? OwnerName)
{
    public static SettingsDto From(UserSettings s) => new(
        s.CostMethod,
        s.IncludeCommission,
        JsonSerializer.Deserialize<Dictionary<string, decimal>>(s.ManualPricesJson) ?? new(),
        s.DividendTaxRatePct,
        s.OwnerName
    );
}
record LedgerDto(int Id, string Type, string Symbol, string Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes, decimal? SplitRatioTo, decimal? SplitRatioFrom)
{
    public static LedgerDto From(LedgerEntry e) => new(
        e.Id, e.Type.ToString().ToLowerInvariant(), e.Symbol, e.Sector, e.Shares, e.Price, e.Commission,
        e.TxDate.ToString("yyyy-MM-dd"), e.Notes, e.SplitRatioTo, e.SplitRatioFrom
    );
}
record CashCreateRequest(string Type, string Date, string? Notes, decimal? Amount = null, string? Symbol = null, bool CreditToCash = true, decimal? GrossAmount = null, decimal? TaxRatePct = null);
record HistoricalPriceRequest(string Date, List<string>? Symbols);
record CashDto(int Id, string Type, decimal Amount, string Date, string? Notes, string? Symbol, int? LinkedEntryId, decimal? GrossAmount, decimal? TaxRatePct, int? LedgerEntryId)
{
    public static CashDto From(CashEntry e) => new(
        e.Id, e.Type.ToString().ToLowerInvariant(), e.Amount, e.EntryDate.ToString("yyyy-MM-dd"), e.Notes,
        e.Symbol, e.LinkedEntryId, e.GrossAmount, e.TaxRatePct, e.LedgerEntryId
    );
}

static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
