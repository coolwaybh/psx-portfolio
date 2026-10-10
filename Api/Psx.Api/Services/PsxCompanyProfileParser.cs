using AngleSharp;
using AngleSharp.Dom;

namespace Psx.Api.Services;

public record PsxAnnualFinancialRow(string Year, string? Sales, string? Profit, string? Eps);

public record PsxCompanyProfile(
    string Name,
    string? PeRatio,
    string? MarketCap,
    string? FreeFloatPct,
    string? OneYearChangePct,
    string? YtdChangePct,
    List<PsxAnnualFinancialRow> AnnualFinancials
);

// Server-side port of the frontend's parseCompanyProfileHtml (wwwroot/index.html) - same
// PSX company-profile page, same fields, so FreeFundamentalAnalysisService can score off
// structured data without a second, divergent parser. Kept as a line-by-line mirror of
// the JS version deliberately: if PSX changes this page's markup, both need the same fix,
// and keeping the selectors identical makes that fix obvious to port across.
public static class PsxCompanyProfileParser
{
    static readonly IBrowsingContext Context = BrowsingContext.New(Configuration.Default);

    public static async Task<PsxCompanyProfile?> ParseAsync(string html)
    {
        using var doc = await Context.OpenAsync(req => req.Content(html));

        var name = doc.QuerySelector("meta[property='og:title']")?.GetAttribute("content")
            ?? doc.QuerySelector("title")?.TextContent;
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        // "Free Float" legitimately appears twice (a share count, then a %) - disambiguate
        // by whether the value contains '%', same as the JS version's isPct parameter.
        var stats = doc.QuerySelectorAll(".stats_item")
            .Select(item => (
                Label: item.QuerySelector(".stats_label")?.TextContent.Trim() ?? "",
                Value: item.QuerySelector(".stats_value")?.TextContent.Trim()))
            .Where(s => s.Label.Length > 0 && s.Value is not null)
            .ToList();

        string? FindStat(string needle, bool? isPct = null) => stats
            .FirstOrDefault(s => s.Label.Contains(needle, StringComparison.OrdinalIgnoreCase)
                && (isPct is null || s.Value!.Contains('%') == isPct))
            .Value;

        var annual = new List<PsxAnnualFinancialRow>();
        var annualTable = doc.QuerySelector(".tabs__panel[data-name='Annual'] table");
        if (annualTable is not null)
        {
            var years = annualTable.QuerySelectorAll("thead th").Skip(1).Select(th => th.TextContent.Trim()).ToList();
            var rows = annualTable.QuerySelectorAll("tbody tr")
                .Select(tr => tr.QuerySelectorAll("td").Select(td => td.TextContent.Trim()).ToList())
                .Where(cells => cells.Count > 0)
                .ToDictionary(cells => cells[0], cells => cells.Skip(1).ToList());

            rows.TryGetValue("Sales", out var sales);
            rows.TryGetValue("Profit after Taxation", out var profit);
            rows.TryGetValue("EPS", out var eps);

            for (var i = 0; i < years.Count; i++)
            {
                annual.Add(new PsxAnnualFinancialRow(
                    years[i],
                    sales is not null && i < sales.Count ? sales[i] : null,
                    profit is not null && i < profit.Count ? profit[i] : null,
                    eps is not null && i < eps.Count ? eps[i] : null));
            }
        }

        return new PsxCompanyProfile(
            name,
            FindStat("P/E Ratio"),
            FindStat("Market Cap"),
            FindStat("Free Float", isPct: true),
            FindStat("1-Year Change"),
            FindStat("YTD Change"),
            annual);
    }
}
