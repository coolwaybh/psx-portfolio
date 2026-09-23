namespace Psx.Api.Entities;

public enum FundTxType
{
    Buy,
    Sell,
    DividendReinvest,
    DividendCash
}

public class FundTransaction
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int FundId { get; set; }
    public MutualFund Fund { get; set; } = null!;

    public FundTxType Type { get; set; }
    public DateOnly TxDate { get; set; }

    // Units bought/sold/added. Always 0 on a DividendCash row - it records income
    // without changing units held (contrast DividendReinvest, which adds units at the
    // reinvestment NAV, same as a Buy).
    public decimal Units { get; set; }
    public decimal Nav { get; set; }
    public decimal Amount { get; set; }
    public decimal FrontLoadPct { get; set; }
    public decimal BackLoadPct { get; set; }

    // CGT the AMC withholds on a Sell's realized gain before crediting proceeds - null
    // for every other type, same "null means not applicable" convention as
    // CashEntry.CgtAmount for stocks. Already netted out of Amount above (mirrors the
    // stock ledger's cash-side netting, not a separate un-netted disclosure figure).
    public decimal? CgtAmount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
