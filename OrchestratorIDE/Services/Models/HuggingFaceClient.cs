// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Wraps the public HuggingFace Hub API.
/// No authentication required — all endpoints used are fully public.
///
/// Key endpoints:
///   Search:  GET https://huggingface.co/api/models?search={q}&filter=gguf&sort=downloads&limit=N
///   Detail:  GET https://huggingface.co/api/models/{id}         (includes siblings / file list)
///   README:  GET https://huggingface.co/{id}/resolve/main/README.md
///   Download: https://huggingface.co/{id}/resolve/main/{filename}
/// </summary>
public sealed class HuggingFaceClient : IDisposable
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public HuggingFaceClient(string? accessToken = null, AppSettings? settings = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://huggingface.co/"),
            Timeout     = TimeSpan.FromSeconds(20),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", "OrchestratorIDE/1.0");

        var resolvedToken = HuggingFaceAccessTokenResolver.Resolve(accessToken, settings);
        if (!string.IsNullOrWhiteSpace(resolvedToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", resolvedToken);
    }

    // ── Search ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Search HuggingFace for GGUF models matching <paramref name="query"/>.
    /// Returns up to <paramref name="limit"/> results sorted by download count.
    /// </summary>
    public async Task<List<HfModelSearchResult>> SearchGgufAsync(
        string query, int limit = 30, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var url = $"api/models?search={Uri.EscapeDataString(query)}" +
                  $"&filter=gguf&sort=downloads&direction=-1&limit={limit}";
        try
        {
            var results = await _http.GetFromJsonAsync<List<HfModelSearchResult>>(url, _json, ct);
            return results ?? [];
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    // ── Model detail + file listing ───────────────────────────────────────────

    /// <summary>
    /// Fetch full model detail including the siblings (file list with sizes).
    /// </summary>
    public async Task<HfModelDetail?> GetModelDetailAsync(
        string hfId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hfId)) return null;
        try
        {
            return await _http.GetFromJsonAsync<HfModelDetail>(
                $"api/models/{hfId}", _json, ct);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns only the GGUF file siblings for a given repo, parsed into
    /// <see cref="GgufVariant"/> records with quant labels and size estimates.
    /// </summary>
    public async Task<List<GgufVariant>> GetGgufVariantsAsync(
        string hfId, string? recommendedQuant, int userVramGb,
        CancellationToken ct = default)
    {
        var detail = await GetModelDetailAsync(hfId, ct);
        if (detail?.Siblings is null) return [];

        var ggufFiles = detail.Siblings
            .Where(s => s.Filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Filename)
            .ToList();

        var variants = ggufFiles.Select(f => BuildVariant(hfId, f, recommendedQuant, userVramGb))
                                .Where(v => v is not null)
                                .Cast<GgufVariant>()
                                .ToList();

        // If nothing was pre-marked recommended, pick the best fit for the user's hardware
        if (!variants.Any(v => v.IsRecommended) && variants.Any())
            AutoRecommend(variants, userVramGb);

        // Best-effort SHA-256 lookup -- a separate API call, so failure here must not lose the
        // variant list itself (download still works, just without integrity verification).
        var hashes = await GetFileHashesAsync(hfId, ct);
        foreach (var v in variants)
            if (hashes.TryGetValue(v.Filename, out var sha))
                v.Sha256 = sha;

        return variants;
    }

    /// <summary>
    /// Maps filename -> SHA-256 via HF's tree API (lfs.oid; LFS uses SHA-256 for oid by
    /// default -- verified live against a real repo, not assumed). Returns an empty dictionary
    /// on any failure; callers should treat a missing hash as "nothing to verify", not an error.
    /// </summary>
    private async Task<Dictionary<string, string>> GetFileHashesAsync(
        string hfId, CancellationToken ct)
    {
        try
        {
            var entries = await _http.GetFromJsonAsync<List<HfTreeEntry>>(
                $"api/models/{hfId}/tree/main", _json, ct);
            if (entries is null) return [];

            return entries
                .Where(e => !string.IsNullOrEmpty(e.Lfs?.Oid))
                .ToDictionary(e => e.Path, e => e.Lfs!.Oid, StringComparer.OrdinalIgnoreCase);
        }
        catch { return []; }
    }

    // ── README / description ──────────────────────────────────────────────────

    /// <summary>
    /// Fetches the model card README.md and extracts a plain-text summary.
    /// Returns null if the README is unavailable or unparseable.
    /// </summary>
    public async Task<string?> GetReadmeSummaryAsync(
        string hfId, CancellationToken ct = default)
    {
        try
        {
            var md = await _http.GetStringAsync(
                $"{hfId}/resolve/main/README.md", ct);
            return ParseReadmeSummary(md);
        }
        catch { return null; }
    }

    // ── Verify repo exists ────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight HEAD check — returns false if the HF repo is gone/private.
    /// Used by the stale-catalog detection logic.
    /// </summary>
    public async Task<bool> RepoExistsAsync(string hfId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.SendAsync(
                new HttpRequestMessage(HttpMethod.Head, $"{hfId}"), ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static GgufVariant? BuildVariant(
        string hfId, HfFileSibling sibling,
        string? recommendedQuant, int userVramGb)
    {
        var fn    = sibling.Filename;
        var quant = ExtractQuantLabel(fn);
        if (string.IsNullOrEmpty(quant)) return null;   // skip non-quant files

        var size     = sibling.Size ?? 0;
        var vramEst  = EstimateVram(size);
        var isRec    = !string.IsNullOrEmpty(recommendedQuant) &&
                       quant.Equals(recommendedQuant, StringComparison.OrdinalIgnoreCase);

        return new GgufVariant
        {
            Filename        = fn,
            QuantLabel      = quant,
            SizeBytes       = size,
            VramEstimateGb  = vramEst,
            IsRecommended   = isRec,
            DownloadUrl     = $"https://huggingface.co/{hfId}/resolve/main/{fn}",
        };
    }

    private static string ExtractQuantLabel(string filename)
    {
        // Match patterns like Q4_K_M, Q5_K_S, Q8_0, F16, IQ4_XS, etc.
        var m = Regex.Match(filename,
            @"(?i)(IQ\d_\w+|Q\d+_K_[A-Z]+|Q\d+_\d+|Q\d+_K|F16|BF16)",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }

    /// <summary>
    /// Rough VRAM estimate: file size × 1.15 overhead (KV cache + context buffers),
    /// rounded up to the next GB.
    /// </summary>
    private static int EstimateVram(long sizeBytes) =>
        (int)Math.Ceiling(sizeBytes / 1_073_741_824.0 * 1.15);

    private static void AutoRecommend(List<GgufVariant> variants, int userVramGb)
    {
        // Prefer the highest quality quant that fits in VRAM
        var fitting = variants
            .Where(v => v.VramEstimateGb <= Math.Max(userVramGb, 1))
            .OrderByDescending(v => v.SizeBytes)
            .FirstOrDefault();

        if (fitting is not null)
        {
            fitting.IsRecommended = true;
            return;
        }

        // If nothing fits, recommend the smallest quant
        var smallest = variants.OrderBy(v => v.SizeBytes).FirstOrDefault();
        if (smallest is not null) smallest.IsRecommended = true;
    }

    private static string? ParseReadmeSummary(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;

        // Strip YAML front matter
        if (markdown.StartsWith("---"))
        {
            var end = markdown.IndexOf("---", 3);
            if (end > 0) markdown = markdown[(end + 3)..].TrimStart();
        }

        // Look for a Description, Overview, or Introduction section
        var sectionPatterns = new[]
        {
            @"##\s+(Overview|Description|Introduction|About|Summary)[^\n]*\n+([\s\S]{50,500}?)(?:\n##|\z)",
        };
        foreach (var pattern in sectionPatterns)
        {
            var m = Regex.Match(markdown, pattern, RegexOptions.IgnoreCase);
            if (m.Success)
                return CleanMarkdown(m.Groups[2].Value.Trim());
        }

        // Fall back: first non-header paragraph of 50+ chars
        var paragraphs = markdown.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
        foreach (var para in paragraphs)
        {
            var clean = CleanMarkdown(para).Trim();
            if (clean.Length >= 50 && !clean.StartsWith("#"))
                return clean.Length > 400 ? clean[..400] + "…" : clean;
        }

        return null;
    }

    private static string CleanMarkdown(string md) =>
        Regex.Replace(md, @"[*_`#\[\]()>]|!\[.*?\]\(.*?\)|\[.*?\]\(.*?\)", "").Trim();

    public void Dispose() => _http.Dispose();
}
