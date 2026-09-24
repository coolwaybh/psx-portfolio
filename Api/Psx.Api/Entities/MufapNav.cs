namespace Psx.Api.Entities;

// Cached daily NAV snapshot from MUFAP's industry-wide "NAVs and Sale Loads" page - shared
// across every user (MUFAP publishes once a day for the whole industry, not per investor), so
// this is fetched at most once per calendar day regardless of how many users are active, not
// per user. FundName is MUFAP's own spelling verbatim; matching it against a user's
// MutualFund.Name is MufapNavSyncService's job (normalized comparison), not assumed identical -
// confirmed the two disagree even for funds already tracked here (MUFAP: "Al Ameen Islamic
// Energy Fund", this app: "Al-Ameen Islamic Energy Fund" - hyphen vs space).
public class MufapNav
{
    public int Id { get; set; }
    public string FundName { get; set; } = "";
    public decimal Nav { get; set; }
    public DateOnly AsOfDate { get; set; }
    public DateTime FetchedAtUtc { get; set; }
}
