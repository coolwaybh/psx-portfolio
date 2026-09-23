using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;

namespace Psx.Api.Services;

public record FundamentalSourceDto(string Title, string Url);
public record FundamentalAnalysisResult(string Signal, string Confidence, string Note, List<FundamentalSourceDto> Sources);

// Live-researches one PSX symbol's fundamentals via Claude's web search tool - a single
// Messages API call where Claude searches the web itself and cites what it found, rather
// than answering from training data (which would be stale/hallucinated for prices and
// recent news). Called only from POST /api/fundamental/{symbol}/refresh, which is
// rate-limited - this class does no caching or throttling itself.
public class FundamentalAnalysisService(IConfiguration config)
{
    public async Task<FundamentalAnalysisResult> AnalyzeAsync(string symbol, CancellationToken ct = default)
    {
        var apiKey = config["Ai:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Ai:ApiKey is not configured.");

        var client = new AnthropicClient { APIKey = apiKey };

        var prompt = $"Research {symbol}, a company listed on the Pakistan Stock Exchange (PSX), using web search for current information (recent price action, latest earnings/results, analyst commentary, sector news). Then give an independent fundamental view.\n\n" +
            """
            Respond with ONLY a single JSON object (no markdown fences, no extra text) in
            exactly this shape:
            {
              "signal": "buy" | "hold" | "sell" | "trim",
              "confidence": "high" | "medium" | "low",
              "note": "2-3 sentence rationale, specific to this company, citing what you found",
              "sources": [{"title": "...", "url": "..."}]
            }
            """;

        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = "claude-haiku-4-5",
            MaxTokens = 2048,
            Tools = [new ToolUnion(new WebSearchTool20250305 { MaxUses = 5 })],
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, cancellationToken: ct);

        var text = string.Join("\n", response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        return Parse(text);
    }

    static FundamentalAnalysisResult Parse(string text)
    {
        // Claude occasionally wraps JSON in a ```json fence despite the instruction not
        // to - strip it defensively rather than failing the whole refresh over formatting.
        var match = Regex.Match(text, @"\{.*\}", RegexOptions.Singleline);
        if (!match.Success)
            throw new InvalidOperationException("AI response did not contain a JSON object.");

        using var doc = JsonDocument.Parse(match.Value);
        var root = doc.RootElement;

        var signal = root.GetProperty("signal").GetString() ?? "hold";
        var confidence = root.GetProperty("confidence").GetString() ?? "medium";
        var note = root.GetProperty("note").GetString() ?? "";

        var sources = new List<FundamentalSourceDto>();
        if (root.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sourcesEl.EnumerateArray())
            {
                var title = s.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var url = s.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(url))
                    sources.Add(new FundamentalSourceDto(title, url));
            }
        }

        return new FundamentalAnalysisResult(signal.ToLowerInvariant(), confidence.ToLowerInvariant(), note, sources);
    }
}
