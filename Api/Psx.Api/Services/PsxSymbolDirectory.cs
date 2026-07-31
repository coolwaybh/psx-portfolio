using System.Text.Json;
using System.Text.RegularExpressions;

namespace Psx.Api.Services;

public enum MatchConfidence { Exact, Partial, None }

public record SymbolMatchResult(string? Symbol, string? Sector, MatchConfidence Confidence);

// A backend-only copy of the symbol/company-name directory already embedded in
// wwwroot/index.html's PSX_SYMBOLS_FALLBACK, used only for resolving a PDF-extracted
// company name to a ticker during import. Deliberately duplicated rather than shared
// with the frontend - every match here is human-reviewed before being saved, so a
// stale/drifted copy just means a Partial/None result the user resolves manually,
// never a silently-wrong save. Not worth the risk of reshaping the frontend's
// already-working, hand-tuned symbol list into a shared file for this alone.
public class PsxSymbolDirectory
{
    record SymbolRecord(string Symbol, string Name, string Sector);

    // Ported as-is from index.html's PSX_SECTOR_MAP - keyword-substring match against
    // the raw PSX sector text, mapping to one of the fixed options the Sector <select>
    // (both the manual Add Transaction modal and the PDF-import review step) offers.
    static readonly (string[] Keys, string Sector)[] SectorMap =
    [
        (["BANK"], "Banks"),
        (["FERTILIZER"], "Fertilizer"),
        (["CEMENT"], "Cement"),
        (["OIL & GAS", "REFINERY"], "Oil & Gas"),
        (["TECHNOLOGY"], "Technology"),
        (["TEXTILE", "APPAREL", "JUTE", "SYNTHETIC", "WOOLLEN"], "Textile"),
        (["POWER"], "Power"),
        (["PHARMA"], "Pharma"),
        (["AUTOMOBILE"], "Auto"),
        (["INSURANCE"], "Insurance"),
        (["ENGINEERING"], "Steel"),
        (["SUGAR"], "Sugar"),
        (["CHEMICAL"], "Chemical"),
        (["FOOD", "VANASPATI"], "Food"),
    ];

    // Whole-word corporate-suffix/noise tokens stripped before comparing names. No
    // trailing-period variants needed - punctuation is stripped in a later pass, and
    // \bLTD\b already matches "LTD" inside "LTD." (the period isn't a word character).
    static readonly string[] SuffixTokens = ["LIMITED", "LTD", "CO", "COMPANY", "PAKISTAN"];

    readonly List<(SymbolRecord Record, string Normalized)> _entries;

    public PsxSymbolDirectory()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Data", "psx-symbols.json");
        var raw = File.ReadAllText(path);
        var records = JsonSerializer.Deserialize<List<SymbolRecord>>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        _entries = records.Select(r => (r, Normalize(r.Name))).ToList();
    }

    public static string Normalize(string s)
    {
        var t = (s ?? "").ToUpperInvariant().Replace("&", "AND");
        foreach (var token in SuffixTokens)
            t = Regex.Replace(t, $@"\b{token}\b", "");
        t = Regex.Replace(t, @"[^\w\s]", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t;
    }

    public static string MapSector(string rawPsxSector)
    {
        var s = (rawPsxSector ?? "").ToUpperInvariant();
        foreach (var (keys, sector) in SectorMap)
            if (keys.Any(k => s.Contains(k)))
                return sector;
        return "Other";
    }

    public SymbolMatchResult Match(string companyNameRaw)
    {
        var normalized = Normalize(companyNameRaw);
        if (string.IsNullOrWhiteSpace(normalized))
            return new SymbolMatchResult(null, null, MatchConfidence.None);

        var exact = _entries.FirstOrDefault(e => e.Normalized == normalized);
        if (exact.Record is not null)
            return new SymbolMatchResult(exact.Record.Symbol, MapSector(exact.Record.Sector), MatchConfidence.Exact);

        // Bidirectional substring containment, preferring the longest matching
        // directory name (least ambiguous) if more than one entry partially matches.
        var partial = _entries
            .Where(e => e.Normalized.Length > 0 &&
                        (normalized.Contains(e.Normalized) || e.Normalized.Contains(normalized)))
            .OrderByDescending(e => e.Normalized.Length)
            .FirstOrDefault();
        if (partial.Record is not null)
            return new SymbolMatchResult(partial.Record.Symbol, MapSector(partial.Record.Sector), MatchConfidence.Partial);

        return new SymbolMatchResult(null, null, MatchConfidence.None);
    }
}
