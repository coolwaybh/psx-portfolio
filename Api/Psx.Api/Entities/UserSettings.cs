namespace Psx.Api.Entities;

public class UserSettings
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string CostMethod { get; set; } = "weightedAvg";
    public bool IncludeCommission { get; set; } = false;
    public string ManualPricesJson { get; set; } = "{}";

    // Default withholding tax rate applied to new dividend entries (PSX default is
    // currently 15% for filers). Just a pre-fill default - each CashEntry stores the
    // rate that actually applied to it, so changing this later never rewrites history.
    public decimal DividendTaxRatePct { get; set; } = 15m;

    // Default Capital Gains Tax rate applied when suggesting the CGT to withhold on a
    // new Sell (Pakistan's standard CGT rate on securities gains for active/filer
    // taxpayers is currently 15%). Same "pre-fill default only" contract as
    // DividendTaxRatePct above - each Sell's linked CashEntry stores the actual PKR
    // amount applied (CashEntry.CgtAmount), so changing this later never rewrites history.
    public decimal CgtRatePct { get; set; } = 15m;

    // Default NCCPL clearing/settlement charge rate applied when suggesting the NCCPL
    // charge on a new Buy/Sell - a separate, proportional-to-trade-value fee that never
    // appears on a broker's own trade-confirmation memo (unlike Commission), only on
    // CDC's ledger. Default (0.007%) is measured from a real August 2026 CDC customer
    // ledger (~0.00698% averaged across 11 trades) - not an official published NCCPL
    // rate, so the user should confirm/adjust against their own broker's numbers. Same
    // "pre-fill default only" contract as DividendTaxRatePct/CgtRatePct above - each
    // entry stores its own NccplCharge, so changing this later never rewrites history.
    public decimal NccplChargeRatePct { get; set; } = 0.007m;

    // Display name shown on the dashboard (e.g. "Sarfraz's Portfolio") - distinct from
    // Username (the login handle), purely cosmetic.
    public string? OwnerName { get; set; }
}
