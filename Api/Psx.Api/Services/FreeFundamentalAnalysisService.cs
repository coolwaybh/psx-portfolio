using System.Globalization;

namespace Psx.Api.Services;

public record FreeFundamentalFactsDto(
    string? PeRatio,
    string? MarketCap,
    string? FreeFloatPct,
    string? OneYearChangePct,
    string? YtdChangePct,
    string? LatestEpsYear,
    decimal? LatestEps,
    decimal? EpsGrowthPct,
    decimal? ProfitGrowthPct,
    decimal? SalesGrowthPct
);

public record FreeFundamentalResult(
    string Outlook,  // "positive" | "neutral" | "negative"
    int Score,         // 0-100, 50 = neutral baseline
    List<string> Reasons,
    FreeFundamentalFactsDto Facts
);

// Deterministic, free fundamental read from PSX's own company-profile page (P/E,
// multi-year Sales/Profit/EPS - see PsxCompanyProfileParser), no AI call involved. Lives
// alongside, not instead of, FundamentalAnalysisService's paid AI research: this gives
// every user a baseline fundamental read for free and without the daily rate limit, and
// a user who wants the deeper qualitative view (analyst commentary, recent news, risk
// narrative) can still run that themselves per the existing refresh policy.
public static class FreeFundamentalAnalysisService
{
    public static FreeFundamentalResult? Analyze(PsxCompanyProfile profile)
    {
        var years = profile.AnnualFinancials; // newest-first, per PSX's own table ordering
        if (years.Count == 0) return null;

        var score = 50;
        var reasons = new List<string>();

        var epsGrowth = GrowthPct(years, r => r.Eps);
        var profitGrowth = GrowthPct(years, r => r.Profit);
        var salesGrowth = GrowthPct(years, r => r.Sales);

        ScoreGrowth(epsGrowth, "EPS", ref score, reasons);
        ScoreGrowth(profitGrowth, "Profit after taxation", ref score, reasons);
        ScoreGrowth(salesGrowth, "Sales", ref score, reasons);

        // P/E is reported here without any sector/peer benchmark to compare against (this
        // app doesn't have one), so it's deliberately a soft, low-weight nudge rather than
        // a strong directional score - purely a generic "statistically cheap/expensive"
        // heuristic, not a valuation call.
        if (ParseNumber(profile.PeRatio) is decimal pe)
        {
            if (pe <= 0)
            {
                score -= 5;
                reasons.Add($"P/E ratio is {pe:0.##} (zero or negative) - the company reported a loss over the trailing twelve months.");
            }
            else if (pe > 35)
            {
                score -= 3;
                reasons.Add($"P/E ratio is {pe:0.##} - high in absolute terms (no sector comparison available here).");
            }
            else if (pe is > 0 and < 8)
            {
                score += 2;
                reasons.Add($"P/E ratio is {pe:0.##} - statistically cheap in absolute terms, though this alone doesn't rule out a value trap (no sector comparison available here).");
            }
        }

        score = Math.Clamp(score, 0, 100);
        var outlook = score switch
        {
            >= 62 => "positive",
            <= 38 => "negative",
            _ => "neutral"
        };

        var latest = years[0];
        var facts = new FreeFundamentalFactsDto(
            profile.PeRatio, profile.MarketCap, profile.FreeFloatPct,
            profile.OneYearChangePct, profile.YtdChangePct,
            latest.Year, ParseNumber(latest.Eps),
            epsGrowth, profitGrowth, salesGrowth);

        return new FreeFundamentalResult(outlook, score, reasons, facts);
    }

    static void ScoreGrowth(decimal? growthPct, string label, ref int score, List<string> reasons)
    {
        if (growthPct is not decimal g) return;

        if (g >= 10)
        {
            score += 10;
            reasons.Add($"{label} grew {g:0.#}% year-over-year in the latest annual results.");
        }
        else if (g > 0)
        {
            score += 5;
            reasons.Add($"{label} grew {g:0.#}% year-over-year - modest but positive.");
        }
        else if (g > -10)
        {
            score -= 5;
            reasons.Add($"{label} declined {Math.Abs(g):0.#}% year-over-year.");
        }
        else
        {
            score -= 10;
            reasons.Add($"{label} declined {Math.Abs(g):0.#}% year-over-year - a significant drop.");
        }
    }

    // Latest year (index 0) vs the prior year (index 1) - PSX's annual table is newest-
    // first. Returns null if there's only one year of data, or either figure is missing
    // or unparseable.
    static decimal? GrowthPct(List<PsxAnnualFinancialRow> years, Func<PsxAnnualFinancialRow, string?> select)
    {
        if (years.Count < 2) return null;
        var latest = ParseNumber(select(years[0]));
        var prior = ParseNumber(select(years[1]));
        if (latest is null || prior is null || prior == 0) return null;
        return Math.Round((latest.Value - prior.Value) / Math.Abs(prior.Value) * 100m, 1);
    }

    // PSX renders figures like "432,410,443", "51.69", "(1,234)" for negatives, and
    // sometimes trailing footnote markers (e.g. "9.45 **") on the P/E stat specifically -
    // strip everything but digits/sign/decimal point before parsing.
    static decimal? ParseNumber(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var negative = s.StartsWith('(') && s.EndsWith(')');
        if (negative) s = s[1..^1];
        s = new string(s.Where(c => char.IsDigit(c) || c == '.' || c == '-').ToArray());
        if (s.Length == 0) return null;
        if (!decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }
}
