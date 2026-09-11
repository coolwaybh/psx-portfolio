namespace Psx.Api.Services;

// Shared fetch-and-fallback logic for the market-watch/indices endpoints. Direct
// server-to-server fetches of PSX's HTML pages started getting rejected outright from
// this host's IP - confirmed by PsxHistoricalPriceService's JSON /timeseries/eod calls
// still working fine from the very same server while these consistently came back as
// errors, so it's specific to full-page HTML scrapes (likely PSX's edge/WAF treating
// them more strictly than its data API), not a blanket block. r.jina.ai's Reader API
// fetches from its own infrastructure and, asked for X-Return-Format: html, hands back
// the raw page - used as a fallback so this doesn't depend on a client-side CORS proxy.
public static class PsxHtmlFetcher
{
    // Returns the raw HTML on success, or null if both the direct fetch and the
    // r.jina.ai fallback failed.
    public static async Task<string?> FetchAsync(HttpClient client, string psxPath)
    {
        var url = $"https://dps.psx.com.pk/{psxPath}";
        try
        {
            return await client.GetStringAsync(url);
        }
        catch (Exception)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, $"https://r.jina.ai/{url}");
                req.Headers.Add("X-Return-Format", "html");
                var res = await client.SendAsync(req);
                if (res.IsSuccessStatusCode)
                    return await res.Content.ReadAsStringAsync();
            }
            catch (Exception) { /* fall through to null below */ }
            return null;
        }
    }
}
