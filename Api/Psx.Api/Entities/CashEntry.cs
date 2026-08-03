namespace Psx.Api.Entities;

public enum CashType
{
    Deposit,
    Withdrawal,
    Dividend
}

// Uninvested "free cash" ledger — deposits, withdrawals, dividends, and auto-created
// entries linked to a stock buy/sell (see LedgerEntryId below).
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

    // Set ONLY when this entry is the auto-linked Deposit from a Sell (LedgerEntryId
    // below) that realized a gain — Pakistan's NCCPL withholds Capital Gains Tax at
    // settlement, on the gain, not the proceeds. Already netted out of Amount (same
    // convention as Commission); kept as its own field, not folded into
    // Commission/GrossAmount/TaxRatePct above, because those are Dividend-specific by
    // contract elsewhere in this codebase and CGT is computed from realized gain
    // (shares × (price − cost basis)), a different quantity than a dividend's gross.
    public decimal? CgtAmount { get; set; }

    // Set ONLY on a synthetic offsetting Withdrawal row created when a dividend was paid
    // directly to the user's bank rather than credited to tracked free cash — points at
    // the paired Dividend row's Id. One-directional: the Dividend row's own LinkedEntryId
    // always stays null. To find a row's pair given either side's id, check this field
    // first, then fall back to a reverse lookup (any row whose LinkedEntryId == this id).
    public int? LinkedEntryId { get; set; }
    public CashEntry? LinkedEntry { get; set; }

    // Set when this entry was auto-created from a stock buy/sell (Withdrawal for a buy,
    // Deposit for a sell) - null for manual deposits/withdrawals, dividends, and entries
    // from an Opening Position or bulk import (those don't touch free cash - they
    // represent shares the user already held or is backfilling, not a purchase made
    // through the app right now). Deleting the LedgerEntry deletes this row too, handled
    // in application code (not a DB cascade - CashEntries already cascades from Users,
    // and a second cascade path via LedgerEntry would hit SQL Server's multi-path error).
    public int? LedgerEntryId { get; set; }
    public LedgerEntry? LedgerEntry { get; set; }
}
