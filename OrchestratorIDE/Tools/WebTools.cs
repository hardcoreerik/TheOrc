// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Tools;

/// <summary>
/// Web tools: fetch_url retrieves a URL and returns plain text (HTML tags stripped).
/// Useful for looking up documentation, GitHub issues, Stack Overflow answers.
/// </summary>
public static class WebTools
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "OrchestratorIDE/1.0 (documentation-lookup)" } }
    };

    // Patterns to strip
    private static readonly Regex _scripts  = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase);
    private static readonly Regex _styles   = new(@"<style[\s\S]*?</style>",  RegexOptions.IgnoreCase);
    private static readonly Regex _tags     = new(@"<[^>]+>",                 RegexOptions.None);
    private static readonly Regex _blanks   = new(@"\n{3,}",                  RegexOptions.None);
    private static readonly Regex _entities = new(@"&(amp|lt|gt|nbsp|quot);",  RegexOptions.None);

    // Block list — never fetch these
    private static readonly HashSet<string> BlockedDomains =
    [
        "localhost", "127.0.0.1", "0.0.0.0", "192.168.", "10.", "172."
    ];

    // Bot-protection / challenge page detection
    // These are checked against the raw HTML before stripping.
    // Returning challenge page content causes small models to hang.
    private static readonly string[] ChallengeSignals =
    [
        "cf-browser-verification",  // Cloudflare: classic IUAM
        "cf_chl_opt",               // Cloudflare: challenge options object
        "jschl-answer",             // Cloudflare: JS challenge answer field
        "cf-chl-bypass",            // Cloudflare: bypass token
        "cf-turnstile",             // Cloudflare: Turnstile CAPTCHA
        "Ray ID",                   // Cloudflare: footer Ray ID (combined with status check)
        "DDoS protection by",       // Cloudflare / generic CDN
        "__cf_chl_rt_tk",           // Cloudflare: retry token
        "Attention Required!",      // Cloudflare: access denied page title
        "Please Wait... | Cloudflare",
        "Just a moment...",         // Cloudflare: IUAM page title
        "Client Challenge",         // Cloudflare / generic challenge
        "verify you are human",
        "Checking your browser",
        "Enable JavaScript and cookies",
        "human verification",
        "bot protection",
    ];

    /// <summary>
    /// Returns true when the response looks like a bot-protection or CAPTCHA challenge page.
    /// Checking happens on raw HTML before tag-stripping so signal strings are preserved.
    /// </summary>
    private static bool IsBotChallengePage(int statusCode, string html)
    {
        // 403/503 + any Cloudflare fingerprint → definitely a challenge
        bool isCfStatus = statusCode is 403 or 503;

        foreach (var signal in ChallengeSignals)
        {
            if (html.Contains(signal, StringComparison.OrdinalIgnoreCase))
            {
                // "Ray ID" alone could appear in legitimate pages; require bad status or CF brand
                if (signal == "Ray ID")
                {
                    if (isCfStatus || html.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                else
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static void Register(ToolRegistry registry)
    {
        registry.Register(new ToolDefinition
        {
            Name = "fetch_url",
            Description = "Fetch a public URL and return its text content (HTML stripped). " +
                          "Use for documentation, GitHub issues, Stack Overflow answers, changelogs. " +
                          "Max 8000 chars returned. Do NOT use for internal/private URLs.",
            Parameters = new Dictionary<string, ToolParameter>
            {
                ["url"] = new("string", "The public URL to fetch (https:// only)"),
                ["max_chars"] = new("integer", "Maximum characters to return (default 6000, max 8000)")
            },
            Required = ["url"],
            RequiresApproval = false,  // Read-only, no side effects
            Handler = async (args, ct) =>
            {
                var url = args.TryGetValue("url", out var u) ? u?.ToString() ?? "" : "";
                var maxChars = args.TryGetValue("max_chars", out var m)
                    ? Math.Min(8000, int.TryParse(m?.ToString(), out var n) ? n : 6000)
                    : 6000;

                if (string.IsNullOrWhiteSpace(url))
                    return "[ERROR] url is required";

                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                    !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                    return "[ERROR] Only http/https URLs are supported";

                // Block internal URLs
                var host = new Uri(url).Host;
                if (BlockedDomains.Any(b => host.StartsWith(b, StringComparison.OrdinalIgnoreCase)))
                    return $"[ERROR] Blocked: {host} is an internal/private address";

                try
                {
                    var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct);
                    var html     = await response.Content.ReadAsStringAsync(ct);
                    var status   = (int)response.StatusCode;

                    // Detect bot-protection / CAPTCHA challenge pages BEFORE doing anything else.
                    // Returning challenge page content to a model causes it to hang trying to
                    // parse thousands of tokens of JavaScript and meta-redirects.
                    if (IsBotChallengePage(status, html))
                        return $"[FETCH_BLOCKED] Bot-protection challenge at {url} (HTTP {status}). " +
                               "This site blocks automated access. Use your existing knowledge instead of retrying this URL.";

                    if (!response.IsSuccessStatusCode)
                        return $"[ERROR] HTTP {status} from {url}";

                    // Strip noise
                    var text = _scripts.Replace(html, " ");
                    text = _styles.Replace(text, " ");
                    text = _tags.Replace(text, "\n");
                    text = _entities.Replace(text, m => m.Groups[1].Value switch
                    {
                        "amp"  => "&",
                        "lt"   => "<",
                        "gt"   => ">",
                        "nbsp" => " ",
                        "quot" => "\"",
                        _ => m.Value
                    });
                    text = _blanks.Replace(text.Trim(), "\n\n");

                    if (text.Length > maxChars)
                        text = text[..maxChars] + $"\n\n[…truncated at {maxChars} chars — URL: {url}]";

                    return text;
                }
                catch (OperationCanceledException)
                {
                    return "[ERROR] Request timed out or cancelled";
                }
                catch (HttpRequestException ex)
                {
                    return $"[ERROR] HTTP request failed: {ex.Message}";
                }
                catch (Exception ex)
                {
                    return $"[ERROR] {ex.Message}";
                }
            }
        });
    }
}
