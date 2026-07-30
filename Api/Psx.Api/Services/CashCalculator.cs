using Psx.Api.Entities;

namespace Psx.Api.Services;

public static class CashCalculator
{
    // Current free cash balance. Order doesn't affect this - straight aggregate.
    public static decimal Balance(IEnumerable<CashEntry> entries) =>
        entries.Sum(e => e.Type == CashType.Withdrawal ? -e.Amount : e.Amount);

    // Walks the chronologically-sorted entries (excluding any id in `excludeIds`) and
    // returns false if the running balance would ever go negative - mirrors
    // HoldingsCalculator.CanRemoveWithoutNegativeBalance's reasoning: a withdrawal can be
    // valid/invalid purely due to ORDERING relative to deposits, not just the final total.
    //
    // Takes a set of ids rather than one, because removing a linked dividend/withdrawal
    // pair together needs both excluded at once - same-date + single-request insertion
    // order does NOT guarantee it's safe to skip re-validating (a third same-day entry
    // can still land between the pair in CreatedAt tie-break order via clock-resolution
    // collisions or a genuinely concurrent request), so this check always actually runs.
    public static bool CanRemoveWithoutNegativeBalance(IEnumerable<CashEntry> entries, IEnumerable<int> excludeIds)
    {
        var excluded = excludeIds as ICollection<int> ?? excludeIds.ToList();
        var sorted = entries
            .Where(e => !excluded.Contains(e.Id))
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
