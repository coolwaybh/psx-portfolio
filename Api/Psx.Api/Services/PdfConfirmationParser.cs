using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Psx.Api.Services;

public record ParsedTradeCandidate(
    string Type, string CompanyNameRaw, string? Symbol, string? Sector, string MatchConfidence,
    decimal Shares, decimal Price, decimal Commission, decimal BrokerageAmount, decimal SstAmount,
    string Date, string? ContractNumber, string Notes);

public record PdfParseResult(List<ParsedTradeCandidate> Candidates, List<string> Warnings);

// Hardcoded to Arif Habib Limited's "MEMO OF CONFIRMATION" trade-confirmation PDF
// format. A different broker's PDF will simply yield zero recognized sections (see
// Warnings on the result) - this is an expected outcome, not an error. Extending to
// another broker means adding a new anchor/regex set alongside this one, not
// modifying it in place.
//
// PdfPig's default page.Text concatenates words with NO inter-word spaces wherever the
// PDF positions text via coordinates rather than literal space glyphs (confirmed by
// testing against a real sample: "...LIMITED5024.1200..." with no separators at all).
// page.GetWords() returns correctly-segmented individual word tokens instead - joining
// those with single spaces is what makes the regexes below workable.
//
// Also confirmed by testing against a real sample: each section's Trade Date/Contract#
// appear BEFORE that section's "We confirm the execution..." sentence in the text
// stream (not after, as the visual reading order might suggest), so splitting on that
// phrase misattributes Contract#/Trade Date to the wrong section. "MEMO OF
// CONFIRMATION" (the title at the top of each section) is the correct anchor - every
// field belonging to one transaction falls between one such title and the next.
public static class PdfConfirmationParser
{
    static readonly Regex SectionAnchor = new(@"(?=MEMO OF CONFIRMATION)", RegexOptions.Compiled);
    static readonly Regex DirectionRegex = new(@"We confirm the execution of your (Sale|Purchase) orders", RegexOptions.Compiled);
    static readonly Regex TradeDateRegex = new(@"Trade Date:\s*(\d{1,2}/[A-Za-z]{3}/\d{4})", RegexOptions.Compiled);
    static readonly Regex ContractRegex = new(@"Contract#:\s*([A-Za-z0-9]+)", RegexOptions.Compiled);
    static readonly Regex TableHeaderRegex = new(@"Company\s+Quantity\s+Rate\s+Gross\s+Amount\s+Brok\.\s+Rate\s+Brok\.\s+Amount\s+Amount", RegexOptions.Compiled);
    static readonly Regex TotalRegex = new(@"\bTotal\b", RegexOptions.Compiled);
    static readonly Regex RowRegex = new(@"(?<company>(?!Total\b)[A-Za-z&,.\-() ]+?)\s+(?<qty>[\d,]+)\s+(?<rate>[\d,]+\.\d+)\s+(?<gross>[\d,]+\.\d+)\s+(?<brokRate>[\d,]+\.\d+)\s+(?<brokAmt>[\d,]+\.\d+)\s+(?<amount>[\d,]+\.\d+)", RegexOptions.Compiled);
    static readonly Regex SstRegex = new(@"S\.S\.T:\s*([\d,]+\.\d+)", RegexOptions.Compiled);
    static readonly Regex NetAmountRegex = new(@"Net Amount:\s*([\d,]+\.\d+)", RegexOptions.Compiled);

    public static PdfParseResult Parse(Stream pdfStream, PsxSymbolDirectory symbols)
    {
        var candidates = new List<ParsedTradeCandidate>();
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

        // Leading boilerplate before the first "MEMO OF CONFIRMATION" (or the whole
        // file, for an unrecognized format) never contains the title text itself -
        // drop it silently rather than reporting it as a failed section.
        var sections = SectionAnchor.Split(fullText)
            .Where(c => c.Contains("MEMO OF CONFIRMATION"))
            .ToList();

        if (sections.Count == 0)
        {
            warnings.Add("Couldn't recognize this PDF — no Arif Habib Memo of Confirmation sections found.");
            return new PdfParseResult(candidates, warnings);
        }

        for (int i = 0; i < sections.Count; i++)
        {
            try
            {
                candidates.AddRange(ParseSection(sections[i], symbols, i + 1, warnings));
            }
            catch (Exception ex)
            {
                warnings.Add($"Section {i + 1}: {ex.Message}");
            }
        }

        return new PdfParseResult(candidates, warnings);
    }

    static List<ParsedTradeCandidate> ParseSection(string section, PsxSymbolDirectory symbols, int sectionNumber, List<string> warnings)
    {
        var directionMatch = DirectionRegex.Match(section);
        if (!directionMatch.Success)
            throw new InvalidOperationException("couldn't determine buy/sell direction");
        var type = directionMatch.Groups[1].Value == "Sale" ? "sell" : "buy";

        var tradeDateMatch = TradeDateRegex.Match(section);
        if (!tradeDateMatch.Success)
            throw new InvalidOperationException("couldn't find a Trade Date");
        var tradeDate = DateTime.ParseExact(tradeDateMatch.Groups[1].Value,
            new[] { "d/MMM/yyyy", "dd/MMM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None);
        var dateStr = tradeDate.ToString("yyyy-MM-dd");

        var contractMatch = ContractRegex.Match(section);
        var contractNumber = contractMatch.Success ? contractMatch.Groups[1].Value : null;

        var headerMatch = TableHeaderRegex.Match(section);
        if (!headerMatch.Success)
            throw new InvalidOperationException("couldn't find the transaction table");

        var afterHeader = section[(headerMatch.Index + headerMatch.Length)..];
        var totalMatch = TotalRegex.Match(afterHeader);
        var rowsRegion = totalMatch.Success ? afterHeader[..totalMatch.Index] : afterHeader;

        var rowMatches = RowRegex.Matches(rowsRegion);
        if (rowMatches.Count == 0)
            throw new InvalidOperationException("couldn't find any transaction rows");

        var sstMatch = SstRegex.Match(section);
        var sectionSst = sstMatch.Success ? ParseDecimal(sstMatch.Groups[1].Value) : 0m;

        var rows = rowMatches.Select(m => new
        {
            Company = m.Groups["company"].Value.Trim(),
            Qty = ParseDecimal(m.Groups["qty"].Value),
            Rate = ParseDecimal(m.Groups["rate"].Value),
            Gross = ParseDecimal(m.Groups["gross"].Value),
            BrokAmt = ParseDecimal(m.Groups["brokAmt"].Value),
        }).ToList();

        // One S.S.T total per section, not per row - when a section has more than one
        // company row (not seen in the real sample, but not assumed impossible),
        // allocate proportionally by each row's share of the section's total brokerage.
        // This is a starting value only - the user can correct commission per row in
        // the review step regardless.
        var totalBrokAmt = rows.Sum(r => r.BrokAmt);

        var notes = contractNumber is not null
            ? $"Imported from PDF — Contract# {contractNumber}"
            : "Imported from PDF";

        var result = new List<ParsedTradeCandidate>();
        foreach (var row in rows)
        {
            var rowSst = totalBrokAmt > 0 ? sectionSst * row.BrokAmt / totalBrokAmt : sectionSst / rows.Count;
            var commission = Math.Round(row.BrokAmt + rowSst, 4);
            var match = symbols.Match(row.Company);

            result.Add(new ParsedTradeCandidate(
                type, row.Company, match.Symbol, match.Sector, match.Confidence.ToString().ToLowerInvariant(),
                row.Qty, row.Rate, commission, row.BrokAmt, Math.Round(rowSst, 4),
                dateStr, contractNumber, notes));
        }

        // Cross-check only - reconciliation mismatches don't block returning the
        // candidate, just flag it for extra scrutiny in review.
        var netAmountMatch = NetAmountRegex.Match(section);
        if (netAmountMatch.Success)
        {
            var parsedNet = ParseDecimal(netAmountMatch.Groups[1].Value);
            var grossTotal = rows.Sum(r => r.Gross);
            var computedNet = type == "buy" ? grossTotal + totalBrokAmt + sectionSst : grossTotal - totalBrokAmt - sectionSst;
            if (Math.Abs(computedNet - parsedNet) > 0.05m)
                warnings.Add($"Section {sectionNumber}: amounts didn't fully reconcile, please double-check.");
        }

        return result;
    }

    static decimal ParseDecimal(string s) =>
        decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
}
