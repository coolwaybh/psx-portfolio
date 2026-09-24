using System.Globalization;
using System.Text.RegularExpressions;

namespace Psx.Api.Services;

public record MufapNavEntry(string FundName, decimal Nav, DateOnly AsOfDate);

// Fetches and parses MUFAP's industry-wide daily "NAVs and Sale Loads" page - the single
// authoritative source for every open-end fund's NAV, published once per business day (usually
// evening PKT), never intraday. Confirmed against a real fetch: MUFAP's own NAV figures match
// exactly what the same funds already show from user-uploaded UBL/Al-Ameen statements.
public static class MufapNavFetcher
{
    const string PageUrl = "https://www.mufap.com.pk/Industry/IndustryStatDaily?tab=3";

    // A direct server-to-server request gets a Cloudflare bot-challenge page (403, "Just a
    // moment...") from this host, same class of block PsxHtmlFetcher already works around for
    // PSX itself - r.jina.ai's Reader API renders the page (client-side DataTables and all) and
    // hands back clean markdown, which is what's parsed below. Unlike PsxHtmlFetcher this never
    // tries the direct request first: MUFAP has never been observed to let it through, so
    // skipping straight to the proxy avoids the guaranteed-failing attempt on every call.
    public static async Task<List<MufapNavEntry>> FetchAsync(HttpClient client)
    {
        var markdown = await client.GetStringAsync($"https://r.jina.ai/{PageUrl}");
        return Parse(markdown);
    }

    // Every fund row (~400/day) renders as one markdown table line:
    // "| Open-End Funds | [Fund Name](url) | Category | Inception Date | Offer | Repurchase |
    //  NAV | Validity Date | Front-end | Back-end | Contingent | Market | Trustee |"
    // Only the "Open-End Funds" sector is parsed - the page's default filter already narrows to
    // it (confirmed: "398 of 398 entries (filtered from 555 total)"), and it's the only fund
    // type this app has ever tracked. VPS/ETF/Pension rows are silently skipped, not an error -
    // a fund of one of those types just won't be matched by MufapNavSyncService, same as any
    // other not-found name.
    public static List<MufapNavEntry> Parse(string markdown)
    {
        var entries = new List<MufapNavEntry>();
        foreach (var rawLine in markdown.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("| Open-End Funds |", StringComparison.Ordinal)) continue;

            var cols = line.Split('|').Select(c => c.Trim()).ToArray();
            if (cols.Length < 9) continue;

            var nameMatch = Regex.Match(cols[2], @"^\[(?<name>.+)\]\(.+\)$");
            if (!nameMatch.Success) continue;
            var name = System.Net.WebUtility.HtmlDecode(nameMatch.Groups["name"].Value).Trim();
            if (name.Length == 0) continue;

            if (!decimal.TryParse(cols[7], NumberStyles.Number, CultureInfo.InvariantCulture, out var nav) || nav <= 0)
                continue;
            if (!DateTime.TryParseExact(cols[8], "MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf))
                continue;

            entries.Add(new MufapNavEntry(name, nav, DateOnly.FromDateTime(asOf)));
        }
        return entries;
    }

    // Strips everything but letters/digits so "Al-Ameen Islamic Energy Fund" (this app's own
    // stored name) and "Al Ameen Islamic Energy Fund" (MUFAP's spelling - space, not hyphen,
    // confirmed against a real fetch) compare equal. Used for both the cache lookup and the
    // per-user fund match in MufapNavSyncService, so the two never drift apart.
    public static string Normalize(string name) =>
        Regex.Replace(name, @"[^A-Za-z0-9]+", " ").Trim().ToLowerInvariant();
}
