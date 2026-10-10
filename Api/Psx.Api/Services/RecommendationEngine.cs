using Psx.Api.Entities;

namespace Psx.Api.Services;

public record RecommendationResult(
    string Symbol,
    string Verdict,              // "BUY" | "HOLD" | "WATCH" | "SELL"
    int CombinedScore,           // 0-100
    string Confidence,           // "high" | "medium" | "low"
    string TechnicalOutlook,     // "bullish" | "neutral" | "bearish"
    int TechnicalScore,
    string? FreeFundamentalOutlook,  // "positive" | "neutral" | "negative" - null if PSX's company page couldn't be fetched/parsed
    int? FreeFundamentalScore,
    FreeFundamentalFactsDto? FreeFundamentalFacts,
    string? AiFundamentalOutlook,    // "buy" | "hold" | "sell" | "trim" - null if never researched (paid, user-triggered)
    int? AiFundamentalScore,
    List<string> Reasons,
    TechnicalIndicatorsDto Indicators,
    string? AiFundamentalNote,
    string? AiFundamentalGeneratedAtUtc
);

// Combines three independent reads into one verdict:
//   1. TechnicalAnalysisService - deterministic, from price history, always available.
//   2. FreeFundamentalAnalysisService - deterministic, from PSX's own company-profile
//      page (P/E, multi-year Sales/Profit/EPS), free and unlimited, usually available.
//   3. FundamentalAnalysisService - AI-researched (Claude + live web search), paid and
//      rate-limited, only present once a user has triggered a refresh for this symbol.
// The combination itself is a fixed formula with an explicit lookup table, not a second
// AI call - every number here traces back to a rule in this file, so the result is
// reproducible and the Reasons list is a complete explanation, not a summary of one.
public static class RecommendationEngine
{
    // How stale a cached AI fundamental view can be before it's treated as a data-quality
    // concern rather than fresh input - fundamentals (earnings, balance sheet) don't
    // shift week to week, but a company can have material news in 30+ days that this
    // snapshot wouldn't reflect. FreeFundamentalAnalysisService needs no equivalent check
    // - it's recomputed live from PSX on every request, same as the technical read.
    static readonly TimeSpan AiFundamentalStaleAfter = TimeSpan.FromDays(30);

    public static RecommendationResult Combine(string symbol, TechnicalAnalysisResult technical, FreeFundamentalResult? freeFundamental, FundamentalView? aiFundamental)
    {
        var reasons = new List<string>(technical.Reasons);

        // weighted: (score, weight) pairs for every source that's actually available.
        var parts = new List<(int Score, decimal Weight)> { (technical.Score, 1m) };

        int? freeFundamentalScore = null;
        if (freeFundamental is not null)
        {
            freeFundamentalScore = freeFundamental.Score;
            parts.Add((freeFundamental.Score, 1m));
            reasons.AddRange(freeFundamental.Reasons);
        }
        else
        {
            reasons.Add("Couldn't read PSX's company-profile page for a free fundamental read right now - this verdict is missing that input.");
        }

        int? aiFundamentalScore = null;
        var aiStale = false;
        if (aiFundamental is not null)
        {
            aiFundamentalScore = AiFundamentalScoreOf(aiFundamental.Signal, aiFundamental.Confidence);
            parts.Add((aiFundamentalScore.Value, 1m));
            reasons.Add($"AI fundamental view ({aiFundamental.Confidence} confidence): {aiFundamental.Note}");

            var age = DateTime.UtcNow - aiFundamental.GeneratedAtUtc;
            if (age > AiFundamentalStaleAfter)
            {
                aiStale = true;
                reasons.Add($"The AI fundamental view is {age.Days} days old - consider refreshing it before acting on this.");
            }
        }
        else
        {
            reasons.Add("No AI-researched fundamental view is cached yet for this symbol - run one (rate-limited) for a deeper qualitative read.");
        }

        var totalWeight = parts.Sum(p => p.Weight);
        var combinedScore = (int)Math.Round(parts.Sum(p => p.Score * p.Weight) / totalWeight);
        combinedScore = Math.Clamp(combinedScore, 0, 100);

        var verdict = combinedScore switch
        {
            >= 72 => "BUY",
            >= 55 => "HOLD",
            >= 40 => "WATCH",
            _ => "SELL"
        };

        var confidence = DetermineConfidence(technical.Score, freeFundamentalScore, aiFundamentalScore, aiFundamental?.Confidence, aiStale, reasons);

        return new RecommendationResult(
            symbol, verdict, combinedScore, confidence,
            technical.Signal, technical.Score,
            freeFundamental?.Outlook, freeFundamentalScore, freeFundamental?.Facts,
            aiFundamental?.Signal, aiFundamentalScore,
            reasons, technical.Indicators,
            aiFundamental?.Note, aiFundamental?.GeneratedAtUtc.ToString("o"));
    }

    static string DetermineConfidence(int technicalScore, int? freeFundamentalScore, int? aiFundamentalScore, string? aiConfidence, bool aiStale, List<string> reasons)
    {
        var available = new List<int> { technicalScore };
        if (freeFundamentalScore is int f) available.Add(f);
        if (aiFundamentalScore is int a) available.Add(a);

        var up = available.Any(s => s >= 55);
        var down = available.Any(s => s <= 45);
        if (up && down)
        {
            reasons.Add("The available signals disagree with each other - treat this as a lower-confidence read until one side confirms the other.");
            return "low";
        }

        if (aiStale) return "low";

        // Only the always-on technical read, nothing fundamental could be read at all.
        if (available.Count == 1) return "low";

        // Technical + free fundamental agree, but no AI research has deepened the view yet.
        if (aiFundamentalScore is null) return "medium";

        // All three sources available and agree - as confident as the AI leg itself allows.
        return aiConfidence == "high" ? "high" : "medium";
    }

    static int AiFundamentalScoreOf(string signal, string confidence) => (signal.ToLowerInvariant(), confidence.ToLowerInvariant()) switch
    {
        ("buy", "high") => 90,
        ("buy", "medium") => 78,
        ("buy", "low") => 65,
        ("hold", "high") => 52,
        ("hold", "medium") => 50,
        ("hold", "low") => 48,
        ("trim", "high") => 25,
        ("trim", "medium") => 32,
        ("trim", "low") => 40,
        ("sell", "high") => 8,
        ("sell", "medium") => 18,
        ("sell", "low") => 30,
        _ => 50,
    };
}
