using System.Collections.Concurrent;

namespace Psx.Api.Services;

// Today's intraday High/Low per symbol, scraped from PSX's per-symbol company page
// (dps.psx.com.pk/company/{symbol}) - the only remaining live source for this, now that
// both /market-watch and /timeseries/eod have gone the same way (genuinely retired, not
// a block - see PsxHtmlFetcher's comment). Unlike EodPrice, this is never persisted to
// the database: it's meaningless after the trading day ends anyway, so an in-memory
// cache that resets on app restart is the right amount of durability for it.
// PsxDayRangeRefreshWorker is the only writer (one symbol at a time, slowly staggered so
// this doesn't reproduce the same scraping pattern that likely got the old endpoints
// pulled); every reader just gets whatever's cached, however old - same "stale beats
// nothing" philosophy as PsxHistoricalPriceService's cache fallback.
public class PsxDayRangeCache
{
    readonly ConcurrentDictionary<string, (decimal High, decimal Low, DateTime FetchedAtUtc)> _cache = new();

    public void Set(string symbol, decimal high, decimal low) =>
        _cache[symbol] = (high, low, DateTime.UtcNow);

    public IReadOnlyDictionary<string, (decimal High, decimal Low, DateTime FetchedAtUtc)> Snapshot() =>
        _cache.ToDictionary(kv => kv.Key, kv => kv.Value);
}
