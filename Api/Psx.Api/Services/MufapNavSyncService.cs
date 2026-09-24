using Microsoft.EntityFrameworkCore;
using Psx.Api.Data;
using Psx.Api.Entities;

namespace Psx.Api.Services;

public record MufapSyncResult(int Updated, int TotalFunds, List<string> Unmatched);

// Keeps a user's mutual fund NAVs current from MUFAP's daily publish, without a background
// timer - shared hosting (MonsterASP) can idle-recycle the app pool overnight, so a "wait until
// 6pm" timer isn't reliable here. Instead this runs on demand (called once per page load, plus
// a manual "Sync NAVs" button - see POST /api/funds/sync-mufap-navs) and self-throttles the
// expensive part: the industry-wide scrape only happens once per calendar day, shared across
// every user, via the MufapNavs cache table - same "fetch on demand, cache, advance-only" shape
// as EodPrice and FundamentalView elsewhere in this app, chosen over a real cron for the same
// reason.
public class MufapNavSyncService(PsxDbContext db, IHttpClientFactory httpFactory)
{
    public async Task<MufapSyncResult> SyncAsync(int userId)
    {
        await RefreshCacheIfStaleAsync();
        return await ApplyToUserFundsAsync(userId);
    }

    async Task RefreshCacheIfStaleAsync()
    {
        var today = DateTime.UtcNow.Date;
        var latestFetch = await db.MufapNavs.MaxAsync(m => (DateTime?)m.FetchedAtUtc);
        if (latestFetch is not null && latestFetch.Value.Date == today) return;

        List<MufapNavEntry> entries;
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(45);
            entries = await MufapNavFetcher.FetchAsync(client);
        }
        catch
        {
            // Best-effort - MUFAP unreachable or r.jina.ai down just means today's sync
            // silently no-ops; the existing (however stale) cache is left as a fallback
            // rather than wiping it, and the next call tries again.
            return;
        }
        if (entries.Count == 0) return;

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var existing = await db.MufapNavs.ToDictionaryAsync(m => MufapNavFetcher.Normalize(m.FundName));
            var now = DateTime.UtcNow;
            foreach (var e in entries)
            {
                var key = MufapNavFetcher.Normalize(e.FundName);
                if (existing.TryGetValue(key, out var row))
                {
                    row.FundName = e.FundName;
                    row.Nav = e.Nav;
                    row.AsOfDate = e.AsOfDate;
                    row.FetchedAtUtc = now;
                }
                else
                {
                    var newRow = new MufapNav { FundName = e.FundName, Nav = e.Nav, AsOfDate = e.AsOfDate, FetchedAtUtc = now };
                    db.MufapNavs.Add(newRow);
                    existing[key] = newRow;
                }
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        });
    }

    async Task<MufapSyncResult> ApplyToUserFundsAsync(int userId)
    {
        var funds = await db.MutualFunds.Where(f => f.UserId == userId).ToListAsync();
        if (funds.Count == 0) return new MufapSyncResult(0, 0, []);

        var cacheByKey = await db.MufapNavs.ToDictionaryAsync(m => MufapNavFetcher.Normalize(m.FundName));

        var updated = 0;
        var unmatched = new List<string>();
        foreach (var fund in funds)
        {
            if (!cacheByKey.TryGetValue(MufapNavFetcher.Normalize(fund.Name), out var nav))
            {
                unmatched.Add(fund.Name);
                continue;
            }

            var history = await db.FundNavHistories.FirstOrDefaultAsync(h => h.FundId == fund.Id && h.AsOfDate == nav.AsOfDate);
            if (history is not null && history.Nav == nav.Nav) continue; // already recorded, nothing changed

            if (history is null)
                db.FundNavHistories.Add(new FundNavHistory { FundId = fund.Id, Nav = nav.Nav, AsOfDate = nav.AsOfDate });
            else
                history.Nav = nav.Nav;

            // Advance-only, same rule as the manual Update NAVs endpoint - a backdated
            // MUFAP entry (shouldn't happen, but never trust an external feed blindly)
            // must never clobber a more recent NAV already on record.
            if (nav.AsOfDate >= fund.NavUpdatedAt)
            {
                fund.CurrentNav = nav.Nav;
                fund.NavUpdatedAt = nav.AsOfDate;
            }
            updated++;
        }

        if (updated > 0) await db.SaveChangesAsync();
        return new MufapSyncResult(updated, funds.Count, unmatched);
    }
}
