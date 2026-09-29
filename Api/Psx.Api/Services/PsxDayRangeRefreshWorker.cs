using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Psx.Api.Data;

namespace Psx.Api.Services;

// Refreshes PsxDayRangeCache one symbol at a time, several seconds apart, cycling
// through every symbol that appears in anyone's LedgerEntries (Buy or Sell - a symbol
// once traded is worth showing the range for even if fully sold, same reasoning as why
// PsxHistoricalPriceService backfills a symbol's whole EOD history on first use). This
// keeps PSX's own request rate constant regardless of how many symbols exist across all
// users combined - one request every SecondsBetweenSymbols, forever - rather than
// bursting N requests at once on some fixed interval, which is the same shape of
// scraping that /market-watch and /timeseries/eod apparently didn't survive.
public class PsxDayRangeRefreshWorker(
    IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory,
    PsxDayRangeCache cache, ILogger<PsxDayRangeRefreshWorker> logger) : BackgroundService
{
    static readonly TimeSpan SecondsBetweenSymbols = TimeSpan.FromSeconds(4);
    static readonly TimeSpan PauseBetweenFullCycles = TimeSpan.FromSeconds(30);

    static readonly Regex StatItemRegex = new(
        @"<div class=""stats_label"">\s*([^<]+?)\s*</div>\s*<div class=""stats_value"">\s*([^<]+?)\s*</div>",
        RegexOptions.Compiled);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the app a moment to finish starting before the first web request.
        try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); } catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            List<string> symbols;
            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<PsxDbContext>();
                symbols = await db.LedgerEntries
                    .Where(e => e.Symbol != "")
                    .Select(e => e.Symbol)
                    .Distinct()
                    .ToListAsync(stoppingToken);
            }

            foreach (var symbol in symbols)
            {
                if (stoppingToken.IsCancellationRequested) return;
                try
                {
                    var client = httpFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(15);
                    var html = await PsxHtmlFetcher.FetchAsync(client, $"company/{symbol}");
                    if (html is not null)
                    {
                        var stats = StatItemRegex.Matches(html)
                            .ToDictionary(m => m.Groups[1].Value.Trim(), m => m.Groups[2].Value.Trim(), StringComparer.OrdinalIgnoreCase);
                        if (stats.TryGetValue("High", out var highText) && stats.TryGetValue("Low", out var lowText)
                            && decimal.TryParse(highText, out var high) && decimal.TryParse(lowText, out var low)
                            && high > 0 && low > 0)
                        {
                            cache.Set(symbol, high, low);
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not refresh day-range for {Symbol}", symbol);
                }

                try { await Task.Delay(SecondsBetweenSymbols, stoppingToken); } catch (TaskCanceledException) { return; }
            }

            try { await Task.Delay(PauseBetweenFullCycles, stoppingToken); } catch (TaskCanceledException) { return; }
        }
    }
}
