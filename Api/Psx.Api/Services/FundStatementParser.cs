using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Psx.Api.Services;

// UnitPrice is whatever price should be used as the opening position's cost basis -
// today's NAV for a plain Consolidated Statement (MarketPrice null, since that format
// has no cost data), or the weighted-average purchase price for an Investment Cost
// statement (MarketPrice set to today's actual market price alongside it - see
// FundCostStatementParser). A non-null MarketPrice tells the frontend to also push a
// NAV update after the opening position is created, so unrealized gain/loss shows
// correctly right away instead of reading zero until the next manual NAV refresh.
public record ParsedFundHoldingCandidate(
    string SchemeNameRaw, string SchemeAbbr, string UnitType,
    decimal Units, decimal UnitPrice, decimal InvestmentValue, string AsOfDate,
    decimal? MarketPrice = null);

public record FundStatementParseResult(List<ParsedFundHoldingCandidate> Candidates, List<string> Warnings);

// Hardcoded to UBL Fund Managers / Al-Ameen Funds' "Consolidated Portfolio Statement"
// PDF ("Portfolio Summary" table). A different AMC's statement yields zero recognized
// rows (see Warnings) - an expected outcome, not an error, same contract as
// PdfConfirmationParser for Arif Habib's broker memo.
//
// Confirmed against a real sample: PdfPig's page.GetWords() (space-joined) reproduces
// the table in clean left-to-right, row-by-row order - EXCEPT a unit type that wraps
// across two lines in its cell (e.g. "INCOME" / "UNITS") gets its second line ("UNITS")
// pushed after that row's three number columns instead of staying with "INCOME". The
// row regex's trailing optional "UNITS" absorbs that stray token. Also confirmed: the
// "(As on <date>)" for the Portfolio Summary table appears BEFORE the "Portfolio
// Summary" title in the text stream, not after.
public static class FundStatementParser
{
    static readonly Regex AsOfDateRegex = new(@"\(As on ([A-Za-z]+ \d{1,2}, \d{4})\)\s*Portfolio Summary", RegexOptions.Compiled);
    static readonly Regex TableHeaderRegex = new(@"Scheme Name\s+Units\s+of Units\s+\(Rs\.\)\s+Value \(Rs\.\)", RegexOptions.Compiled);
    static readonly Regex FooterRegex = new(@"Note:\s*For investment valuation", RegexOptions.Compiled);
    static readonly Regex RowRegex = new(
        @"(?<scheme>[A-Z][A-Za-z0-9&,.\-\s]+?\((?<abbr>[A-Z0-9]+)\))\s+" +
        @"(?<unittype>[A-Za-z\-]+(?:\s+[A-Za-z\-]+)?)\s+" +
        @"(?<units>[\d,]+\.\d+)\s+(?<price>[\d,]+\.\d+)\s+(?<value>[\d,]+)(?:\s+UNITS)?",
        RegexOptions.Compiled);

    public static FundStatementParseResult Parse(Stream pdfStream)
    {
        var candidates = new List<ParsedFundHoldingCandidate>();
        var warnings = new List<string>();

        string fullText;
        try
        {
            using var document = PdfDocument.Open(pdfStream);
            var sb = new System.Text.StringBuilder();
            foreach (var page in document.GetPages())
                sb.Append(string.Join(" ", page.GetWords().Select(w => w.Text))).Append(' ');
            fullText = sb.ToString();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read this PDF — please check the file and try again.", ex);
        }

        var headerMatch = TableHeaderRegex.Match(fullText);
        if (!headerMatch.Success)
        {
            warnings.Add("Couldn't recognize this PDF — no UBL/Al-Ameen Portfolio Summary table found.");
            return new FundStatementParseResult(candidates, warnings);
        }

        var asOfMatch = AsOfDateRegex.Match(fullText);
        var asOfDate = "";
        if (asOfMatch.Success && DateTime.TryParse(asOfMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            asOfDate = parsed.ToString("yyyy-MM-dd");
        else
            warnings.Add("Couldn't find the statement date — please set it manually for each row.");

        var afterHeader = fullText[(headerMatch.Index + headerMatch.Length)..];
        var footerMatch = FooterRegex.Match(afterHeader);
        var tableRegion = footerMatch.Success ? afterHeader[..footerMatch.Index] : afterHeader;

        var rowMatches = RowRegex.Matches(tableRegion);
        if (rowMatches.Count == 0)
        {
            warnings.Add("Found the Portfolio Summary table but couldn't parse any fund rows.");
            return new FundStatementParseResult(candidates, warnings);
        }

        foreach (Match m in rowMatches)
        {
            candidates.Add(new ParsedFundHoldingCandidate(
                m.Groups["scheme"].Value.Trim(),
                m.Groups["abbr"].Value,
                Regex.Replace(m.Groups["unittype"].Value.Trim(), @"\s*-\s*", "-"),
                ParseDecimal(m.Groups["units"].Value),
                ParseDecimal(m.Groups["price"].Value),
                ParseDecimal(m.Groups["value"].Value),
                asOfDate));
        }

        return new FundStatementParseResult(candidates, warnings);
    }

    static decimal ParseDecimal(string s) =>
        decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
}
