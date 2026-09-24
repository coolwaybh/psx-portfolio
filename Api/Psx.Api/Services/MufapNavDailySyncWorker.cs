using Microsoft.EntityFrameworkCore;
using Psx.Api.Data;

namespace Psx.Api.Services;

// Guarantees a NAV gets recorded for every fund on every day the app process is alive at all,
// not just days someone happens to log in - MufapNavSyncService alone only syncs when a
// request triggers it (POST /api/funds/sync-mufap-navs, called once per page load), so a day
// with zero visitors was previously a permanent gap in FundNavHistory (MUFAP has no retrievable
// historical/date-range data - confirmed the daily table's own date filter gets Cloudflare-
// blocked - so a missed day can never be backfilled after the fact).
//
// Deliberately checks hourly rather than firing at one fixed clock time: on MonsterASP's shared
// hosting the app pool can idle-recycle after inactivity, so a "run at 6pm" timer could simply
// never fire on a quiet day. Checking every hour instead means that whenever the process next
// wakes for ANY reason (a real visitor, a health check, anything), it catches up within the
// hour rather than waiting for the next day's slot - self-healing regardless of exactly how
// aggressively this host recycles idle processes.
public class MufapNavDailySyncWorker(IServiceScopeFactory scopeFactory, ILogger<MufapNavDailySyncWorker> logger) : BackgroundService
{
    static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first check well clear of app startup, so a deploy/restart doesn't pile
        // this onto every other service's own startup work.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); }
        catch (TaskCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Never let one bad tick kill the loop - same best-effort contract as every
                // other MUFAP-facing call in this app (MufapNavSyncService.RefreshCacheIfStaleAsync).
                logger.LogWarning(ex, "MUFAP daily NAV background sync failed");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (TaskCanceledException) { break; }
        }
    }

    async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PsxDbContext>();

        // Cheap early-exit before touching per-user data or MUFAP at all - mirrors
        // MufapNavSyncService's own "already fetched today" check, so an hourly tick on an
        // already-synced day costs one MAX() query and nothing else.
        var today = DateTime.UtcNow.Date;
        var latestFetch = await db.MufapNavs.MaxAsync(m => (DateTime?)m.FetchedAtUtc, ct);
        if (latestFetch is not null && latestFetch.Value.Date == today) return;

        var userIds = await db.MutualFunds.Select(f => f.UserId).Distinct().ToListAsync(ct);
        if (userIds.Count == 0) return; // nobody tracks a fund - nothing to sync, don't fetch MUFAP for no one

        var svc = scope.ServiceProvider.GetRequiredService<MufapNavSyncService>();
        foreach (var userId in userIds)
        {
            try
            {
                await svc.SyncAsync(userId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "MUFAP sync failed for user {UserId}", userId);
            }
        }
    }
}
