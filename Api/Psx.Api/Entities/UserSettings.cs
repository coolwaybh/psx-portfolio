namespace Psx.Api.Entities;

public class UserSettings
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string CostMethod { get; set; } = "weightedAvg";
    public bool IncludeCommission { get; set; } = true;
    public string ManualPricesJson { get; set; } = "{}";

    // Default withholding tax rate applied to new dividend entries (PSX default is
    // currently 15% for filers). Just a pre-fill default - each CashEntry stores the
    // rate that actually applied to it, so changing this later never rewrites history.
    public decimal DividendTaxRatePct { get; set; } = 15m;

    // Display name shown on the dashboard (e.g. "Sarfraz's Portfolio") - distinct from
    // Username (the login handle), purely cosmetic.
    public string? OwnerName { get; set; }
}
