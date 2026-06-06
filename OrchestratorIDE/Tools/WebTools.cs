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
                    var html = await _http.GetStringAsync(url, ct);

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
