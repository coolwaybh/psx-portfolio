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

    // Only set (and meaningful) when Type == Dividend — which script paid it.
    public string? Symbol { get; set; }

    // Set ONLY on a synthetic offsetting Withdrawal row created when a dividend was paid
    // directly to the user's bank rather than credited to tracked free cash — points at
    // the paired Dividend row's Id. One-directional: the Dividend row's own LinkedEntryId
    // always stays null. To find a row's pair given either side's id, check this field
    // first, then fall back to a reverse lookup (any row whose LinkedEntryId == this id).
    public int? LinkedEntryId { get; set; }
    public CashEntry? LinkedEntry { get; set; }
}
