namespace Psx.Api.Entities;

// One row per user per day: what the portfolio was actually worth when the app last priced it
// that day. The Cumulative P&L chart used to RECONSTRUCT past values from cached daily closes,
// which are missing for recently bought symbols and go stale if the price source stops
// responding; a recorded snapshot is the figure as it really was, so history from the day this
// table starts is exact. Written by the client (it already holds the live quotes and the cost
// method) via PUT /api/snapshots/today - the server only validates and stores it.
public class PortfolioSnapshot
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public DateOnly SnapshotDate { get; set; }

    // Stocks: market value of everything held, the cost basis of that same holding, and the
    // realized profit booked so far - cumulative P&L = RealizedCum + (StocksValue - StocksCost).
    public decimal StocksValue { get; set; }
    public decimal StocksCost { get; set; }
    public decimal RealizedCum { get; set; }

    // Mutual funds (value at the latest NAV, and what was invested) and free cash.
    public decimal FundsValue { get; set; }
    public decimal FundsCost { get; set; }
    public decimal Cash { get; set; }

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
