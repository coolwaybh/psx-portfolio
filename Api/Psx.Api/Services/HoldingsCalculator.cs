using Psx.Api.Entities;

namespace Psx.Api.Services;

// Ports the exact validation logic from the frontend's getAvailableShares() and
// deleteTransaction() guard (psx-portfolio.html), so a direct API request can't
// corrupt the ledger in ways the client-only version already prevented.
public static class HoldingsCalculator
{
    // Total shares currently held for a symbol. Order doesn't affect this - it's a
    // straight aggregate (SUM(buys) - SUM(sells)), clamped at zero.
    public static decimal AvailableShares(IEnumerable<LedgerEntry> entries, string symbol) =>
        Math.Max(0, entries
            .Where(t => t.Symbol == symbol)
            .Sum(t => t.Type == TxType.Buy ? t.Shares : -t.Shares));

    // Walks the chronologically-sorted entries for `symbol` (excluding `excludeId` if
    // given) and returns false if the running balance would ever go negative at any
    // point in time - not just at the end. A delete can be invalid purely because of
    // ORDERING (a later sell that's no longer covered by an earlier buy being removed),
    // which a simple aggregate check would miss.
    public static bool CanRemoveWithoutNegativeBalance(IEnumerable<LedgerEntry> entries, string symbol, int? excludeId)
    {
        var sorted = entries
            .Where(t => t.Symbol == symbol && t.Id != excludeId)
            .OrderBy(t => t.TxDate)
            .ThenBy(t => t.CreatedAt);

        decimal running = 0;
        foreach (var t in sorted)
        {
            running += t.Type == TxType.Buy ? t.Shares : -t.Shares;
            if (running < -0.000000001m) return false;
        }
        return true;
    }
}
