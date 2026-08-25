namespace Psx.Api.Entities;

public enum TxType
{
    Buy,
    Sell,
    Split
}

public class LedgerEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public TxType Type { get; set; }
    public string Symbol { get; set; } = "";
    public string Sector { get; set; } = "";
    public decimal Shares { get; set; }
    public decimal Price { get; set; }
    public decimal Commission { get; set; }

    // NCCPL clearing/settlement charge - kept separate from Commission (Brokerage + SST,
    // the only two things a broker's own trade-confirmation memo lists) because it's a
    // distinct fee CDC deducts directly, proportional to trade value at a rate the user
    // configures (UserSettings.NccplChargeRatePct is only ever a pre-fill suggestion -
    // this field is what actually applied to THIS trade). Applies to Buy and Sell, never
    // a Split (same as Commission).
    public decimal NccplCharge { get; set; }

    public DateOnly TxDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Only set when Type == Split: new shares for every SplitRatioFrom old shares
    // (e.g. a 2:1 split is To=2, From=1). Shares/Price/Commission/NccplCharge are always
    // 0 on a split row - it's a marker, not a quantity/price event.
    public decimal? SplitRatioTo { get; set; }
    public decimal? SplitRatioFrom { get; set; }
}
