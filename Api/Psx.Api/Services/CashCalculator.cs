using Psx.Api.Entities;

namespace Psx.Api.Services;

public static class CashCalculator
{
    // Current free cash balance. Order doesn't affect this - straight aggregate.
    public static decimal Balance(IEnumerable<CashEntry> entries) =>
        entries.Sum(e => e.Type == CashType.Withdrawal ? -e.Amount : e.Amount);

    // Walks the chronologically-sorted entries (excluding `excludeId` if given) and
    // returns false if the running balance would ever go negative - mirrors
    // HoldingsCalculator.CanRemoveWithoutNegativeBalance's reasoning: a withdrawal can be
    // valid/invalid purely due to ORDERING relative to deposits, not just the final total.
    public static bool CanRemoveWithoutNegativeBalance(IEnumerable<CashEntry> entries, int? excludeId)
    {
        var sorted = entries
            .Where(e => e.Id != excludeId)
            .OrderBy(e => e.EntryDate)
            .ThenBy(e => e.CreatedAt);

        decimal running = 0;
        foreach (var e in sorted)
        {
            running += e.Type == CashType.Withdrawal ? -e.Amount : e.Amount;
            if (running < -0.000000001m) return false;
        }
        return true;
    }
}
