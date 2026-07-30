using System.Security.Claims;
using System.Text.Json;
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
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

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
    options.AddFixedWindowLimiter("auth", opt =>
    {
        opt.Window = TimeSpan.FromMinutes(1);
        opt.PermitLimit = 10;
        opt.QueueLimit = 0;
    });
});

var app = builder.Build();

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
    if (username.Length < 3 || string.IsNullOrEmpty(req.Password) || req.Password.Length < 8)
        return Results.BadRequest(new { error = "Username must be at least 3 characters and password at least 8." });

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

    db.LedgerEntries.Add(entry);
    await db.SaveChangesAsync();
    return Results.Created($"/api/ledger/{entry.Id}", LedgerDto.From(entry));
});

ledger.MapDelete("/{id:int}", async (int id, ClaimsPrincipal principal, PsxDbContext db) =>
{
    var userId = principal.GetUserId();
    var entry = await db.LedgerEntries.FirstOrDefaultAsync(t => t.Id == id);
    // 404 (not 403) for entries owned by someone else, so we don't confirm another user's row exists.
    if (entry is null || entry.UserId != userId) return Results.NotFound();

    if (entry.Type == TxType.Buy)
    {
        var existing = await db.LedgerEntries.Where(t => t.UserId == userId).ToListAsync();
        if (!HoldingsCalculator.CanRemoveWithoutNegativeBalance(existing, entry.Symbol, entry.Id))
            return Results.BadRequest(new { error = "Cannot delete — would cause negative shares on a later sell." });
    }

    db.LedgerEntries.Remove(entry);
    await db.SaveChangesAsync();
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
            running += e.Type == TxType.Buy ? e.Shares : -e.Shares;
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
    await db.SaveChangesAsync();
    return Results.Ok(SettingsDto.From(s));
});

app.Run();

static bool TryBuildEntry(LedgerCreateRequest req, int userId, out LedgerEntry entry, out string error)
{
    entry = null!;
    error = "";

    if (!Enum.TryParse<TxType>(req.Type, ignoreCase: true, out var type))
    {
        error = "Type must be 'buy' or 'sell'.";
        return false;
    }
    if (string.IsNullOrWhiteSpace(req.Symbol))
    {
        error = "Symbol is required.";
        return false;
    }
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
    if (!DateOnly.TryParse(req.Date, out var date))
    {
        error = "Date is invalid.";
        return false;
    }

    entry = new LedgerEntry
    {
        UserId = userId,
        Type = type,
        Symbol = req.Symbol.Trim().ToUpperInvariant(),
        Sector = req.Sector ?? "",
        Shares = req.Shares,
        Price = req.Price,
        Commission = req.Commission,
        TxDate = date,
        Notes = req.Notes,
    };
    return true;
}

record AuthRequest(string? Username, string? Password);
record LedgerCreateRequest(string Type, string Symbol, string? Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes);
record ImportRequest(List<LedgerCreateRequest> Transactions);
record SettingsDto(string CostMethod, bool IncludeCommission, Dictionary<string, decimal> ManualPrices)
{
    public static SettingsDto From(UserSettings s) => new(
        s.CostMethod,
        s.IncludeCommission,
        JsonSerializer.Deserialize<Dictionary<string, decimal>>(s.ManualPricesJson) ?? new()
    );
}
record LedgerDto(int Id, string Type, string Symbol, string Sector, decimal Shares, decimal Price, decimal Commission, string Date, string? Notes)
{
    public static LedgerDto From(LedgerEntry e) => new(
        e.Id, e.Type.ToString().ToLowerInvariant(), e.Symbol, e.Sector, e.Shares, e.Price, e.Commission,
        e.TxDate.ToString("yyyy-MM-dd"), e.Notes
    );
}

static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal principal) =>
        int.Parse(principal.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
