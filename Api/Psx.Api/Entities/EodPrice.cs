namespace Psx.Api.Entities;

// A cached PSX end-of-day closing price. Global, not per-user - a closing price is a
// market fact, not user data, so caching it once benefits every user forever and is
// never re-fetched from PSX for a (Symbol, Date) pair already known.
public class EodPrice
{
    public int Id { get; set; }
    public string Symbol { get; set; } = "";
    public DateOnly Date { get; set; }
    public decimal Close { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
