namespace Psx.Api.Entities;

public class UserSettings
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public string CostMethod { get; set; } = "weightedAvg";
    public bool IncludeCommission { get; set; } = true;
    public string ManualPricesJson { get; set; } = "{}";
}
