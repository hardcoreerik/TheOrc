using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OrchestratorIDE.Core;

/// <summary>
/// Silently checks GitHub Releases for a newer version of TheOrc.
/// One HTTP call per 24 hours — result cached in AppSettings.
/// All failures are non-fatal; the UI never blocks on this.
/// </summary>
public static class UpdateChecker
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string ApiUrl  = "https://api.github.com/repos/hardcoreerik/TheOrc/releases/latest";
    private const string HtmlUrl = "https://github.com/hardcoreerik/TheOrc/releases";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

    // Shared HttpClient — do not dispose; lifetime tied to app
    private static readonly HttpClient _http = BuildClient();

    // ── Result ────────────────────────────────────────────────────────────────

    public sealed record UpdateResult(
        bool    UpdateAvailable,
        string  LatestVersion,   // e.g. "1.2.0"
        string  CurrentVersion,  // e.g. "1.0.0"
        string  ReleaseUrl,      // https://…/releases/tag/v1.2.0
        string  ReleaseName      // display name from GitHub release title
    );

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current assembly version as a dotted string (e.g. "1.0.0").
    /// </summary>
    public static string CurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        return $"{v.Major}.{v.Minor}.{v.Build}";
    }

    /// <summary>
    /// Checks GitHub for a newer release, respecting a 24-hour throttle.
    /// Returns null if:
    ///   - <see cref="AppSettings.CheckForUpdates"/> is false
    ///   - a check was already done in the last 24 hours
    ///   - the network call fails
    /// </summary>
    public static async Task<UpdateResult?> CheckAsync(AppSettings settings, bool force = false)
    {
        if (!settings.CheckForUpdates) return null;

        // ── Throttle: only hit the API once per day ───────────────────────────
        if (!force && settings.LastUpdateCheckUtc.HasValue)
        {
            var elapsed = DateTime.UtcNow - settings.LastUpdateCheckUtc.Value;
            if (elapsed < CheckInterval) return null;
        }

        try
        {
            var json = await _http.GetStringAsync(ApiUrl);
            var root = JsonNode.Parse(json);

            // Mark the check time regardless of result
            settings.LastUpdateCheckUtc = DateTime.UtcNow;

            string tagName = root?["tag_name"]?.GetValue<string>() ?? "";
            string htmlUrl  = root?["html_url"]?.GetValue<string>()  ?? HtmlUrl;
            string name     = root?["name"]?.GetValue<string>()      ?? tagName;
            bool   draft    = root?["draft"]?.GetValue<bool>()       ?? false;
            bool   prerel   = root?["prerelease"]?.GetValue<bool>()  ?? false;

            // Skip drafts and pre-releases
            if (draft || prerel) return null;

            // Strip leading "v" from tag
            string latestRaw = tagName.TrimStart('v', 'V');
            settings.LastKnownLatestVersion = latestRaw;
            settings.Save();

            if (!Version.TryParse(latestRaw, out var latest)) return null;
            if (!Version.TryParse(CurrentVersion(), out var current)) return null;

            return new UpdateResult(
                UpdateAvailable: latest > current,
                LatestVersion:   latestRaw,
                CurrentVersion:  CurrentVersion(),
                ReleaseUrl:      htmlUrl,
                ReleaseName:     name
            );
        }
        catch
        {
            // Network failure, parse failure — never crash the app
            return null;
        }
    }

    /// <summary>
    /// Fetches the latest release and returns the browser_download_url of the first
    /// .exe asset found, or null if none exists (build-from-source fallback).
    /// </summary>
    public static async Task<string?> GetReleaseAssetUrlAsync()
    {
        try
        {
            var json  = await _http.GetStringAsync(ApiUrl);
            var root  = JsonNode.Parse(json);
            var assets = root?["assets"]?.AsArray();
            if (assets is null) return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>() ?? "";
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return asset?["browser_download_url"]?.GetValue<string>();
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Opens the GitHub releases page in the default browser.
    /// </summary>
    public static void OpenReleasePage(string releaseUrl)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                string.IsNullOrEmpty(releaseUrl) ? HtmlUrl : releaseUrl)
            {
                UseShellExecute = true
            });
        }
        catch { /* non-fatal */ }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    private static HttpClient BuildClient()
    {
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(10);

        // GitHub API requires a User-Agent header
        var appVersion = CurrentVersion();
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TheOrc", appVersion));
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("(https://github.com/hardcoreerik/TheOrc)"));

        // Ask for GitHub v3 JSON format
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        return client;
    }
}
