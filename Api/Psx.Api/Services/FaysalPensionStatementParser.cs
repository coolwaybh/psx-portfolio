using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Psx.Api.Services;

// Hardcoded to Faysal Asset Management's own "Statement of Account" for a Faysal Islamic
// Pension Fund (VPS) registration - a third, unrelated statement template alongside the two
// UBL/Al-Ameen ones (FundStatementParser, FundCostStatementParser). A different AMC's
// statement, or a non-pension Faysal statement, yields zero rows (see Warnings) - expected,
// not an error, same contract as those two parsers.
//
// Confirmed against a real sample: PdfPig's page.GetWords() (space-joined, no Y-clustering -
// same flat-text approach the other two parsers use) reproduces the "Summary of Investment"
// table's row content correctly, but with each row's own Investment Value column pulled to
// BEFORE that row's Fund Name/Type/Units/NAV instead of after it - a column-order quirk of
// this specific PDF's internal text stream (distinct from, but same class of issue as, the
// wrapped-cell reordering seen in the UBL SABER certificate parser). The regex below matches
// value-first to account for it.
public static class FaysalPensionStatementParser
{
    static readonly Regex RowRegex = new(
        @"(?<value>[\d,]+\.\d+)\s+(?<name>[A-Za-z][A-Za-z\s\-]+?Sub Fund)\s+(?<unittype>CLASS\s+[A-Za-z0-9]+)\s+(?<units>[\d,]+\.\d+)\s+(?<nav>[\d,]+\.\d+)",
        RegexOptions.Compiled);

    static readonly Regex AsOfDateRegex = new(@"Summary of Investment\s*-\s*As of\s*(?<date>\d{1,2}-[A-Za-z]{3}-\d{4})", RegexOptions.Compiled);

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

        if (!fullText.Contains("IPA Number") || !fullText.Contains("Summary of Investment"))
        {
            warnings.Add("Couldn't recognize this PDF — no Faysal Pension Fund Statement of Account found.");
            return new FundStatementParseResult(candidates, warnings);
        }

        var asOfMatch = AsOfDateRegex.Match(fullText);
        var asOfDate = "";
        if (asOfMatch.Success && DateTime.TryParseExact(asOfMatch.Groups["date"].Value, "d-MMM-yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
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
                m.Groups["name"].Value.Trim(),
                SchemeAbbr: "",
                UnitType: m.Groups["unittype"].Value.Trim(),
                Units: ParseDecimal(m.Groups["units"].Value),
                UnitPrice: ParseDecimal(m.Groups["nav"].Value),
                InvestmentValue: ParseDecimal(m.Groups["value"].Value),
                AsOfDate: asOfDate));
        }

        return new FundStatementParseResult(candidates, warnings);
    }

    static decimal ParseDecimal(string s) =>
        decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
}
