namespace Psx.Api.Entities;

public enum CashType
{
    Deposit,
    Withdrawal,
    Dividend
}

// Uninvested "free cash" ledger — deposits, withdrawals, dividends. Deliberately NOT
// linked to LedgerEntry buys/sells (those don't auto-debit/credit cash) — this is a
// standalone running balance the user maintains themselves, same spirit as the stock
// ledger but simpler.
public class CashEntry
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public CashType Type { get; set; }
    public decimal Amount { get; set; }
    public DateOnly EntryDate { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
