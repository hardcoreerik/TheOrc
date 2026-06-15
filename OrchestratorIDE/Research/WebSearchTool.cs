// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace OrchestratorIDE.Research;

/// <summary>
/// Web search via DuckDuckGo Lite — no API key required.
/// Falls back gracefully if the endpoint is unreachable.
/// </summary>
public class WebSearchTool
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    static WebSearchTool()
    {
        // Browser-like User-Agent avoids bot-detection blocks
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public async Task<SearchResult[]> SearchAsync(
        string query,
        int maxResults = 6,
        CancellationToken ct = default)
    {
        try
        {
            // DuckDuckGo Lite: simple HTML table, easy to parse, no JS
            var url = $"https://lite.duckduckgo.com/lite/?q={Uri.EscapeDataString(query)}";
            var html = await _http.GetStringAsync(url, ct);
            return ParseDdgLite(html, maxResults);
        }
        catch (Exception ex)
        {
            // Surface the error as a single "result" so the model can report it
            return
            [
                new SearchResult
                {
                    Title   = "Search unavailable",
                    Url     = "",
                    Snippet = $"Web search failed: {ex.Message}. Try fetch_page with a direct URL instead.",
                }
            ];
        }
    }

    // ── DDG Lite HTML parser ──────────────────────────────────────────────────

    private static SearchResult[] ParseDdgLite(string html, int max)
    {
        var results = new List<SearchResult>();

        // DDG Lite structures results as table rows.
        // Pattern: <a class="result-link" href="URL">TITLE</a>
        //          <td class="result-snippet">SNIPPET</td>
        //
        // We extract all result-link anchors, pair with following snippets.

        var titleMatches = Regex.Matches(html,
            @"<a[^>]+class=""result-link""[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        var snippetMatches = Regex.Matches(html,
            @"<td[^>]+class=""result-snippet""[^>]*>(.*?)</td>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        for (int i = 0; i < Math.Min(titleMatches.Count, max); i++)
        {
            var rawUrl     = HtmlDecode(titleMatches[i].Groups[1].Value.Trim());
            var rawTitle   = HtmlDecode(StripTags(titleMatches[i].Groups[2].Value));
            var rawSnippet = i < snippetMatches.Count
                ? HtmlDecode(StripTags(snippetMatches[i].Groups[1].Value))
                : "";

            // DDG Lite sometimes wraps results in redirect URLs — unwrap them
            var finalUrl = TryUnwrapUrl(rawUrl);

            if (!string.IsNullOrWhiteSpace(rawTitle) && !string.IsNullOrWhiteSpace(finalUrl))
            {
                results.Add(new SearchResult
                {
                    Title   = NormaliseWhitespace(rawTitle),
                    Url     = finalUrl,
                    Snippet = NormaliseWhitespace(rawSnippet),
                });
            }
        }

        // Fallback: if DDG Lite format changed, try the older table cell pattern
        if (results.Count == 0)
            results.AddRange(ParseDdgFallback(html, max));

        return results.Take(max).ToArray();
    }

    /// <summary>Older DDG Lite format uses plain &lt;a&gt; cells in result tables.</summary>
    private static IEnumerable<SearchResult> ParseDdgFallback(string html, int max)
    {
        var links = Regex.Matches(html,
            @"<a[^>]+href=""(https?://[^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);

        foreach (Match m in links.Take(max * 3))
        {
            var url   = m.Groups[1].Value.Trim();
            var title = HtmlDecode(StripTags(m.Groups[2].Value)).Trim();
            if (title.Length < 5) continue;
            if (url.Contains("duckduckgo.com")) continue;
            yield return new SearchResult { Title = title, Url = url, Snippet = "" };
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StripTags(string html)
        => Regex.Replace(html, @"<[^>]+>", " ");

    private static string HtmlDecode(string s)
        => HttpUtility.HtmlDecode(s ?? "");

    private static string NormaliseWhitespace(string s)
        => Regex.Replace(s.Trim(), @"\s+", " ");

    /// <summary>
    /// DDG Lite sometimes wraps URLs as /l/?uddg=ENCODED. Unwrap them.
    /// </summary>
    private static string TryUnwrapUrl(string url)
    {
        if (!url.Contains("/l/?") && !url.Contains("//duckduckgo.com/l/"))
            return url;

        var m = Regex.Match(url, @"[?&]uddg=([^&]+)");
        if (m.Success)
        {
            try { return Uri.UnescapeDataString(m.Groups[1].Value); } catch { }
        }
        return url;
    }
}

// ── Result model ──────────────────────────────────────────────────────────────

public record SearchResult
{
    public string Title   { get; init; } = "";
    public string Url     { get; init; } = "";
    public string Snippet { get; init; } = "";

    public override string ToString()
        => $"**{Title}**\n{Url}\n{Snippet}";
}
