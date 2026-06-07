using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace OrchestratorIDE.Research;

/// <summary>
/// Fetches a URL and returns clean, readable text + extracted hyperlinks.
/// Pure HttpClient + inline HTML stripper — no browser required.
///
/// Handles:
///  - Charset detection from Content-Type header or meta tag
///  - Boilerplate removal (nav, header, footer, aside, scripts, styles)
///  - Entity decoding
///  - Link extraction with anchor text
/// </summary>
public class FetchPageTool
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    static FetchPageTool()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/124.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept",
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "en-US,en;q=0.9");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <param name="url">Full URL to fetch.</param>
    /// <param name="mode">"text" = clean body text (default) | "links" = extracted hyperlinks</param>
    /// <param name="maxChars">Truncate output to this many chars to keep context manageable.</param>
    public async Task<PageResult> FetchAsync(
        string url,
        string mode      = "text",
        int    maxChars  = 8000,
        CancellationToken ct = default)
    {
        try
        {
            // Normalise URL
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html") && !contentType.Contains("text"))
            {
                return new PageResult
                {
                    Url   = url,
                    Title = "(binary content)",
                    Text  = $"Cannot extract text from content-type: {contentType}",
                    Links = [],
                };
            }

            // Read as bytes so we can detect charset
            var bytes   = await response.Content.ReadAsByteArrayAsync(ct);
            var charset = DetectCharset(
                response.Content.Headers.ContentType?.CharSet, bytes);
            var html    = charset.GetString(bytes);

            var title = ExtractTitle(html);
            var links = ExtractLinks(html, url);
            var text  = mode == "links"
                ? FormatLinks(links, maxChars)
                : ExtractText(html, maxChars);

            return new PageResult
            {
                Url   = url,
                Title = title,
                Text  = text,
                Links = links,
            };
        }
        catch (Exception ex)
        {
            return new PageResult
            {
                Url   = url,
                Title = "Fetch failed",
                Text  = $"Could not fetch page: {ex.Message}",
                Links = [],
            };
        }
    }

    // ── HTML processing ───────────────────────────────────────────────────────

    private static string ExtractTitle(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(.*?)</title>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        return m.Success
            ? HttpUtility.HtmlDecode(StripTags(m.Groups[1].Value)).Trim()
            : "";
    }

    private static string ExtractText(string html, int maxChars)
    {
        // 1. Remove entire boilerplate sections
        html = RemoveBlock(html, "script");
        html = RemoveBlock(html, "style");
        html = RemoveBlock(html, "nav");
        html = RemoveBlock(html, "header");
        html = RemoveBlock(html, "footer");
        html = RemoveBlock(html, "aside");
        html = RemoveBlock(html, "noscript");
        html = RemoveBlock(html, "iframe");
        html = RemoveBlock(html, "figure");  // image containers

        // 2. Remove comments
        html = Regex.Replace(html, @"<!--.*?-->", "", RegexOptions.Singleline);

        // 3. Replace block-level tags with newlines to preserve paragraph structure
        html = Regex.Replace(html,
            @"<(p|div|section|article|h[1-6]|li|br|tr|blockquote)[^>]*>",
            "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html,
            @"</(p|div|section|article|h[1-6]|li|tr|blockquote)>",
            "\n", RegexOptions.IgnoreCase);

        // 4. Strip remaining tags
        html = StripTags(html);

        // 5. Decode entities
        html = HttpUtility.HtmlDecode(html);

        // 6. Normalise whitespace: collapse runs of spaces, keep meaningful newlines
        html = Regex.Replace(html, @"[ \t]+", " ");
        html = Regex.Replace(html, @"\n[ \t]+", "\n");
        html = Regex.Replace(html, @"\n{3,}", "\n\n");
        html = html.Trim();

        // 7. Truncate
        if (html.Length > maxChars)
            html = html[..maxChars] + "\n\n[… content truncated]";

        return html;
    }

    private static List<PageLink> ExtractLinks(string html, string baseUrl)
    {
        var links  = new List<PageLink>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var baseUri = Uri.TryCreate(baseUrl, UriKind.Absolute, out var b) ? b : null;

        foreach (Match m in Regex.Matches(html,
            @"<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
            RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var href = m.Groups[1].Value.Trim();
            var text = HttpUtility.HtmlDecode(StripTags(m.Groups[2].Value)).Trim();

            // Resolve relative URLs
            if (baseUri != null && !href.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (Uri.TryCreate(baseUri, href, out var abs))
                    href = abs.ToString();
                else
                    continue;
            }

            if (!href.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;
            if (href.Contains("javascript:")) continue;
            if (string.IsNullOrWhiteSpace(text) || text.Length < 2) continue;
            if (!seen.Add(href)) continue;

            links.Add(new PageLink { Text = NormaliseWhitespace(text), Url = href });
            if (links.Count >= 50) break;
        }

        return links;
    }

    private static string FormatLinks(List<PageLink> links, int maxChars)
    {
        var sb = new StringBuilder();
        foreach (var l in links)
        {
            sb.AppendLine($"- [{l.Text}]({l.Url})");
            if (sb.Length > maxChars) break;
        }
        return sb.ToString();
    }

    // ── Charset detection ─────────────────────────────────────────────────────

    private static Encoding DetectCharset(string? httpCharset, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(httpCharset))
        {
            try { return Encoding.GetEncoding(httpCharset); } catch { }
        }

        // Sniff from meta charset tag in first 2KB of HTML
        var sniff = Encoding.ASCII.GetString(bytes, 0, Math.Min(2048, bytes.Length));
        var m = Regex.Match(sniff,
            @"charset\s*=\s*[""']?([A-Za-z0-9\-]+)",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            try { return Encoding.GetEncoding(m.Groups[1].Value); } catch { }
        }

        return Encoding.UTF8;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string RemoveBlock(string html, string tag)
        => Regex.Replace(html, $@"<{tag}[^>]*>.*?</{tag}>",
            "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static string StripTags(string html)
        => Regex.Replace(html, @"<[^>]+>", "");

    private static string NormaliseWhitespace(string s)
        => Regex.Replace(s.Trim(), @"\s+", " ");
}

// ── Result model ──────────────────────────────────────────────────────────────

public record PageResult
{
    public string          Url   { get; init; } = "";
    public string          Title { get; init; } = "";
    public string          Text  { get; init; } = "";
    public List<PageLink>  Links { get; init; } = [];
}

public record PageLink
{
    public string Text { get; init; } = "";
    public string Url  { get; init; } = "";
}
