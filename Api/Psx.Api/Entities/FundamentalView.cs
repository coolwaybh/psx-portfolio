namespace Psx.Api.Entities;

// One cached AI-researched fundamental snapshot per symbol, shared across all users -
// the underlying research (company fundamentals, analyst sentiment) doesn't depend on
// who's asking, so there's no per-user row here (contrast with the per-user refresh
// cooldown tracked on User.LastFundamentalRefreshUtc).
public class FundamentalView
{
    public string Symbol { get; set; } = "";
    public string Signal { get; set; } = "";
    public string Confidence { get; set; } = "";
    public string Note { get; set; } = "";

    // JSON-serialized List<FundamentalSourceDto> ({title, url}) - citations from the
    // web search tool backing this analysis, so a user can verify it themselves.
    public string SourcesJson { get; set; } = "[]";

    public DateTime GeneratedAtUtc { get; set; } = DateTime.UtcNow;
}
