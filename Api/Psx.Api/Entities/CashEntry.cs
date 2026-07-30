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

    // Only set (and meaningful) when Type == Dividend. PSX dividends have withholding
    // tax deducted at source before the investor ever sees the money - Amount above is
    // always the NET amount actually received/credited (so every existing balance/pairing
    // calculation keeps working unchanged); GrossAmount is the pre-tax declared amount,
    // and TaxRatePct is the rate actually applied to THIS entry (not the live settings
    // default - stored per-entry so a later change to the default never rewrites history).
    // Tax paid on this entry = GrossAmount - Amount (derived, not stored separately).
    public decimal? GrossAmount { get; set; }
    public decimal? TaxRatePct { get; set; }

    // Set ONLY on a synthetic offsetting Withdrawal row created when a dividend was paid
    // directly to the user's bank rather than credited to tracked free cash — points at
    // the paired Dividend row's Id. One-directional: the Dividend row's own LinkedEntryId
    // always stays null. To find a row's pair given either side's id, check this field
    // first, then fall back to a reverse lookup (any row whose LinkedEntryId == this id).
    public int? LinkedEntryId { get; set; }
    public CashEntry? LinkedEntry { get; set; }
}
