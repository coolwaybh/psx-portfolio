namespace Psx.Api.Entities;

public enum TxType
{
    Buy,
    Sell
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
    public DateOnly TxDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
