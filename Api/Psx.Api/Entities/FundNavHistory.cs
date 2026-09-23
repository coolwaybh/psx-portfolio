namespace Psx.Api.Entities;

// One row per NAV update (see POST /api/funds/nav-updates) - builds the history a fund's
// value-over-time chart and staleness check read from, separate from MutualFund.CurrentNav
// (today's value only), the same way EodPrice is separate from a stock's live price.
public class FundNavHistory
{
    public int Id { get; set; }
    public int FundId { get; set; }
    public MutualFund Fund { get; set; } = null!;

    public decimal Nav { get; set; }
    public DateOnly AsOfDate { get; set; }
}
