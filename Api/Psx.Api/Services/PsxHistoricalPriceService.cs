using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Psx.Api.Data;
using Psx.Api.Entities;

namespace Psx.Api.Services;

record PsxEodResponse(int Status, string? Message, List<List<decimal>>? Data);

// Fetches and caches PSX end-of-day closing prices. dps.psx.com.pk's
// /timeseries/eod/{symbol} endpoint is public/unauthenticated but undocumented and not
// an official API - calls are made server-side (not from the browser) both because the
// page's CSP would otherwise block them, and so the resulting EodPrice cache benefits
// every user, not just whoever happened to trigger the first fetch for a symbol.
public class PsxHistoricalPriceService(HttpClient http, PsxDbContext db)
{
    // Returns the closing price for `symbol` on the nearest trading day on or before
    // `targetDate`, along with that actual date (which may differ from targetDate on a
    // weekend/holiday/thinly-traded day). Returns (null, null) if nothing is cached and
    // PSX has no data (or is unreachable) for this symbol.
    public async Task<(decimal? Close, DateOnly? ActualDate)> GetPriceAsOf(string symbol, DateOnly targetDate)
    {
        var cached = await FindCached(symbol, targetDate);
        if (cached is not null) return (cached.Close, cached.Date);

        try
        {
            var resp = await http.GetFromJsonAsync<PsxEodResponse>($"timeseries/eod/{symbol}");
            if (resp is null || resp.Status != 1 || resp.Data is null || resp.Data.Count == 0)
                return (null, null);

            var rows = resp.Data
                .Where(row => row.Count >= 2)
                .Select(row => (Date: UnixSecondsToTradingDate((long)row[0]), Close: row[1]))
                .ToList();

            await UpsertRows(symbol, rows);

            var match = rows.Where(r => r.Date <= targetDate).OrderByDescending(r => r.Date).FirstOrDefault();
            return match.Date == default ? (null, null) : (match.Close, match.Date);
        }
        catch (Exception)
        {
            // PSX unreachable, timed out, or returned something we couldn't parse -
            // genuinely unavailable for this symbol right now, not a crash.
            return (null, null);
        }
    }

    async Task<EodPrice?> FindCached(string symbol, DateOnly targetDate) =>
        await db.EodPrices
            .Where(p => p.Symbol == symbol && p.Date <= targetDate)
            .OrderByDescending(p => p.Date)
            .FirstOrDefaultAsync();

    async Task UpsertRows(string symbol, List<(DateOnly Date, decimal Close)> rows)
    {
        var existingDates = (await db.EodPrices
            .Where(p => p.Symbol == symbol)
            .Select(p => p.Date)
            .ToListAsync())
            .ToHashSet();

        var newRows = rows
            .Where(r => !existingDates.Contains(r.Date))
            .Select(r => new EodPrice { Symbol = symbol, Date = r.Date, Close = r.Close });

        db.EodPrices.AddRange(newRows);
        await db.SaveChangesAsync();
    }

    static DateOnly UnixSecondsToTradingDate(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime);
}
