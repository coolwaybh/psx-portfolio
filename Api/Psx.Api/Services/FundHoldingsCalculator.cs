using Psx.Api.Entities;

namespace Psx.Api.Services;

// Mirrors HoldingsCalculator's chronological-walk approach for stocks - no split event
// for funds, so the walk is simpler, but still ordered (not a flat aggregate) so a delete
// mid-history is validated at every point in time, not just at the end.
public static class FundHoldingsCalculator
{
    public static decimal UnitsHeld(IEnumerable<FundTransaction> entries, int fundId)
    {
        var sorted = entries
            .Where(t => t.FundId == fundId)
            .OrderBy(t => t.TxDate)
            .ThenBy(t => t.CreatedAt);

        decimal running = 0;
        foreach (var t in sorted)
        {
            running = t.Type switch
            {
                FundTxType.Buy => running + t.Units,
                FundTxType.DividendReinvest => running + t.Units,
                FundTxType.Sell => running - t.Units,
                _ => running
            };
        }
        return Math.Max(0, running);
    }

    public static bool CanRemoveWithoutNegativeBalance(IEnumerable<FundTransaction> entries, int fundId, int? excludeId)
    {
        var sorted = entries
            .Where(t => t.FundId == fundId && t.Id != excludeId)
            .OrderBy(t => t.TxDate)
            .ThenBy(t => t.CreatedAt);

        decimal running = 0;
        foreach (var t in sorted)
        {
            running = t.Type switch
            {
                FundTxType.Buy => running + t.Units,
                FundTxType.DividendReinvest => running + t.Units,
                FundTxType.Sell => running - t.Units,
                _ => running
            };
            if (running < -0.000000001m) return false;
        }
        return true;
    }
}
