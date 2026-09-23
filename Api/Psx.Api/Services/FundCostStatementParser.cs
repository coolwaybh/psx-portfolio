using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Psx.Api.Services;

// Hardcoded to UBL Fund Managers / Al-Ameen Funds' "Portfolio Statement with Investment
// Cost" PDF - a different report from the "Consolidated Portfolio Statement" that
// FundStatementParser handles (no per-fund abbreviation in parens, and it carries actual
// cost data instead of just today's holding value). A different AMC's statement, or this
// same AMC's other report, yields zero rows (see Warnings) - expected, not an error, same
// contract as the other parsers.
//
// Confirmed against a real sample: PdfPig's page.GetWords() (space-joined) keeps each
// row in clean left-to-right order (scheme name, then the 6 numeric columns in their
// true visual order: Units, Market Price, Market Value, Weighted Price, Investment
// Cost, Unrealized Gain/Loss) - UNLIKE the table's own two-line-wrapped header cells
// ("Weighted" / "Price (Rs.)" etc.), which get reordered by column across the header
// row and so can't be used to anchor the table region. The document title
// ("Portfolio Statement with Investment Cost") is used instead, purely to recognize
// the format - not to bound where rows are searched, since the row regex is already
// anchored tightly enough (scheme name ending in "FUND" immediately followed by
// exactly 6 numbers) to never false-match the boilerplate/disclaimer text.
public static class FundCostStatementParser
{
    static readonly Regex TitleRegex = new(@"Portfolio Statement with Investment Cost", RegexOptions.Compiled);
    static readonly Regex AsOfDateRegex = new(@"As on Date\s*:\s*([A-Za-z]+ \d{1,2}, \d{4})", RegexOptions.Compiled);
    static readonly Regex RowRegex = new(
        @"(?<scheme>[A-Z][A-Z\-&]*(?:\s[A-Z\-&]+)*\sFUND)\s+" +
        @"(?<units>[\d,]+\.\d+)\s+(?<marketPrice>[\d,]+\.\d+)\s+(?<marketValue>[\d,]+)\s+" +
        @"(?<weightedPrice>[\d,]+\.\d+)\s+(?<investmentCost>[\d,]+)\s+(?<unrealizedGL>-?[\d,]+)",
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

        if (!TitleRegex.IsMatch(fullText))
        {
            warnings.Add("Couldn't recognize this PDF — no UBL/Al-Ameen Portfolio Statement with Investment Cost found.");
            return new FundStatementParseResult(candidates, warnings);
        }

        var asOfMatch = AsOfDateRegex.Match(fullText);
        var asOfDate = "";
        if (asOfMatch.Success && DateTime.TryParse(asOfMatch.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            asOfDate = parsed.ToString("yyyy-MM-dd");
        else
            warnings.Add("Couldn't find the statement date — please set it manually for each row.");

        var rowMatches = RowRegex.Matches(fullText);
        if (rowMatches.Count == 0)
        {
            warnings.Add("Recognized this statement but couldn't parse any fund rows.");
            return new FundStatementParseResult(candidates, warnings);
        }

        foreach (Match m in rowMatches)
        {
            candidates.Add(new ParsedFundHoldingCandidate(
                m.Groups["scheme"].Value.Trim(),
                SchemeAbbr: "",
                UnitType: "",
                Units: ParseDecimal(m.Groups["units"].Value),
                // The opening position's cost basis is the actual weighted-average
                // purchase price, not today's market price - that's the whole point of
                // this format over the plain Consolidated Statement.
                UnitPrice: ParseDecimal(m.Groups["weightedPrice"].Value),
                InvestmentValue: ParseDecimal(m.Groups["investmentCost"].Value),
                AsOfDate: asOfDate,
                MarketPrice: ParseDecimal(m.Groups["marketPrice"].Value)));
        }

        return new FundStatementParseResult(candidates, warnings);
    }

    static decimal ParseDecimal(string s) =>
        decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
}
