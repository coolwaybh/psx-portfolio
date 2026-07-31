using Psx.Api.Entities;

namespace Psx.Api.Services;

// Ports the exact validation logic from the frontend's getAvailableShares() and
// deleteTransaction() guard (psx-portfolio.html), so a direct API request can't
// corrupt the ledger in ways the client-only version already prevented.
public static class HoldingsCalculator
{
    // A Split row never changes historical Buy/Sell rows - it's a multiplier applied
    // when replaying the chronologically-sorted event log. Malformed ratio (null/zero)
    // is treated as a 1x no-op rather than thrown, since this must never crash a
    // sell/delete validation path over bad historical data.
    public static decimal SplitRatio(LedgerEntry t) =>
        t.SplitRatioFrom is decimal from && from != 0 && t.SplitRatioTo is decimal to ? to / from : 1m;

    // Total shares currently held for a symbol. Must be a chronological walk (not a
    // flat aggregate) because a Split row rescales everything accumulated before it -
    // e.g. buy 100, split 2:1, and available shares must read 200, not 100.
    public static decimal AvailableShares(IEnumerable<LedgerEntry> entries, string symbol)
    {
        var sorted = entries
            .Where(t => t.Symbol == symbol)
            .OrderBy(t => t.TxDate)
            .ThenBy(t => t.CreatedAt);

        decimal running = 0;
        foreach (var t in sorted)
        {
            running = t.Type switch
            {
                TxType.Buy => running + t.Shares,
                TxType.Sell => running - t.Shares,
                TxType.Split => running * SplitRatio(t),
                _ => running
            };
        }
        return Math.Max(0, running);
    }

    // Walks the chronologically-sorted entries for `symbol` (excluding `excludeId` if
    // given) and returns false if the running balance would ever go negative at any
    // point in time - not just at the end. A delete can be invalid purely because of
    // ORDERING (a later sell that's no longer covered by an earlier buy - or a later
    // split that rescaled it - being removed), which a simple aggregate check would miss.
    public static bool CanRemoveWithoutNegativeBalance(IEnumerable<LedgerEntry> entries, string symbol, int? excludeId)
    {
        var sorted = entries
            .Where(t => t.Symbol == symbol && t.Id != excludeId)
            .OrderBy(t => t.TxDate)
            .ThenBy(t => t.CreatedAt);

        decimal running = 0;
        foreach (var t in sorted)
        {
            running = t.Type switch
            {
                TxType.Buy => running + t.Shares,
                TxType.Sell => running - t.Shares,
                TxType.Split => running * SplitRatio(t),
                _ => running
            };
            if (running < -0.000000001m) return false;
        }
        return true;
    }
}
