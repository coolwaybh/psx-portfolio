using System.Globalization;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace Psx.Api.Services;

// Hardcoded to UBL Fund Managers / Al-Ameen Funds' "Your Transaction Statement" PDF - a fourth,
// unrelated template alongside the two portfolio-snapshot ones (FundStatementParser,
// FundCostStatementParser) and Faysal's pension statement (FaysalPensionStatementParser). Unlike
// those three, which each describe a point-in-time holding, this one lists discrete dated
// transactions (Additional Purchase, Dividend Re-invest, ...) - one page per fund, each page
// carrying its own "For <FUND NAME>" header and a "The value of your investment in <CODE> is..."
// line with the fund's short code. A different AMC's statement, or a portfolio-snapshot statement
// from the same AMC, yields zero rows (see Warnings) - expected, not an error, same contract as
// the other three parsers.
//
// Only confirmed against one real sample (a single "Additional Purchase" row, no Load/Charges/CGT
// deducted). The row regex is written to tolerate the optional Load/(Rs.)-(%) /Charges(Rs.)/
// CGTax(Rs.) columns between Gross and Net Amount going unpopulated-and-therefore-absent from the
// text stream (as they were in the sample) by matching a flexible run of amount-like tokens there
// - but this is inference, not confirmed against a redemption/CGT row. A transaction type outside
// TypeMap is deliberately left OUT of Candidates (with a Warning naming it) rather than guessed as
// a Buy - the frontend's null-TxType fallback assumes "opening position", which would silently
// misfile a real Sell/dividend as a purchase.
public static class UblTransactionStatementParser
{
    // Longest/most specific phrase first - "Additional Purchase" must be tried before a bare
    // "Purchase" alternative would ever be added, so the more specific phrase wins the match.
    static readonly (string Phrase, string TxType)[] TypeMap =
    [
        ("Additional Purchase", "Buy"),
        ("Initial Purchase", "Buy"),
        ("Front End Load Purchase", "Buy"),
        ("Redemption", "Sell"),
        ("Dividend Re-invest", "DividendReinvest"),
        ("Dividend Reinvestment", "DividendReinvest"),
        ("Dividend Payout", "DividendCash"),
        ("Cash Dividend", "DividendCash"),
    ];

    static readonly Regex FundNameRegex = new(
        @"For\s+(?<name>[A-Z][A-Z0-9&,.\-\s]*?FUND(?:\s*-\s*Class\s*'[A-Za-z0-9]+')?)\s+[A-Z]",
        RegexOptions.Compiled);
    static readonly Regex SchemeAbbrRegex = new(
        @"value of your investment in (?<abbr>[A-Z0-9]+) is Rs\.", RegexOptions.Compiled);
    static readonly Regex ClosingUnitsRegex = new(
        @"Closing Units\s*\(As on [^)]+\):\s*(?<units>[\d,]+\.\d+)", RegexOptions.Compiled);

    static readonly Regex RowRegex = BuildRowRegex();

    static Regex BuildRowRegex()
    {
        var typeAlternation = string.Join("|", TypeMap.Select(t => Regex.Escape(t.Phrase)));
        return new Regex(
            $@"(?<type>{typeAlternation})\s+" +
            @"(?<gross>[\d,]+(?:\.\d+)?)\s+" +
            @"(?:[\d,]+(?:\.\d+)?\s+){0,3}" +          // optional Load/Charges/CGTax, unpopulated in the one sample seen
            @"(?<net>[\d,]+(?:\.\d+)?)\s+" +
            @"(?<nav>[\d,]+\.\d{2,4})\s+" +
            @"(?<pricedate>\d{1,2}-\d{1,2}-\d{4})\s+" +
            @"(?<units>[\d,]+\.\d{2,4})\s+" +
            @"(?<balance>[\d,]+\.\d{2,4})",
            RegexOptions.Compiled);
    }

    public static FundStatementParseResult Parse(Stream pdfStream)
    {
        var candidates = new List<ParsedFundHoldingCandidate>();
        var warnings = new List<string>();

        List<string> pageTexts;
        try
        {
            using var document = PdfDocument.Open(pdfStream);
            pageTexts = document.GetPages()
                .Select(page => string.Join(" ", page.GetWords().Select(w => w.Text)))
                .ToList();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Could not read this PDF — please check the file and try again.", ex);
        }

        if (!pageTexts.Any(t => t.Contains("Your Transaction Statement") && t.Contains("Transaction Details")))
        {
            warnings.Add("Couldn't recognize this PDF — no UBL/Al-Ameen Transaction Statement found.");
            return new FundStatementParseResult(candidates, warnings);
        }

        foreach (var pageText in pageTexts)
        {
            if (!pageText.Contains("Your Transaction Statement")) continue; // e.g. a cover/summary page

            var nameMatch = FundNameRegex.Match(pageText);
            if (!nameMatch.Success)
            {
                warnings.Add("Found a Transaction Statement page but couldn't identify which fund it's for — skipped.");
                continue;
            }
            var fundName = Regex.Replace(nameMatch.Groups["name"].Value.Trim(), @"\s+", " ");

            var abbrMatch = SchemeAbbrRegex.Match(pageText);
            var schemeAbbr = abbrMatch.Success ? abbrMatch.Groups["abbr"].Value : "";

            var rowMatches = RowRegex.Matches(pageText);
            if (rowMatches.Count == 0)
            {
                warnings.Add($"No transactions found on the '{fundName}' page for this statement period.");
                continue;
            }

            foreach (Match m in rowMatches)
            {
                var typeText = m.Groups["type"].Value;
                var txType = TypeMap.First(t => t.Phrase == typeText).TxType;

                var txDate = "";
                if (DateTime.TryParseExact(m.Groups["pricedate"].Value, "dd-MM-yyyy",
                        CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
                    txDate = parsedDate.ToString("yyyy-MM-dd");
                else
                {
                    warnings.Add($"Couldn't parse the date on a '{typeText}' row for '{fundName}' — skipped.");
                    continue;
                }

                var units = ParseDecimal(m.Groups["units"].Value);
                var nav = ParseDecimal(m.Groups["nav"].Value);
                var net = ParseDecimal(m.Groups["net"].Value);

                candidates.Add(new ParsedFundHoldingCandidate(
                    fundName, schemeAbbr, UnitType: "",
                    Units: units, UnitPrice: nav, InvestmentValue: net, AsOfDate: txDate,
                    TxType: txType, TxDate: txDate,
                    Notes: $"Imported from UBL transaction statement ({typeText}, {m.Groups["pricedate"].Value})"));
            }

            // Cross-check: the running Balance Units column on the last parsed row for this page
            // should match the page's own stated "Closing Units" figure. A mismatch means either a
            // row was missed (Load/CGT column shape different from the one confirmed sample) or an
            // opening-balance transaction predates this statement's period and isn't included here
            // - either way, worth a warning rather than a silent gap.
            var closingMatch = ClosingUnitsRegex.Match(pageText);
            if (closingMatch.Success && rowMatches.Count > 0)
            {
                var statedClosing = ParseDecimal(closingMatch.Groups["units"].Value);
                var lastRowBalance = ParseDecimal(rowMatches[^1].Groups["balance"].Value);
                if (Math.Abs(statedClosing - lastRowBalance) > 0.01m)
                    warnings.Add($"'{fundName}': statement shows closing balance {statedClosing:N4} but the last parsed row shows {lastRowBalance:N4} — please double-check this page's rows.");
            }
        }

        if (candidates.Count == 0 && warnings.Count == 0)
            warnings.Add("Recognized this statement but couldn't parse any transaction rows.");

        return new FundStatementParseResult(candidates, warnings);
    }

    static decimal ParseDecimal(string s) =>
        decimal.Parse(s.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture);
}
