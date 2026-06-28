// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Aggregates model search results from two sources:
///   1. HuggingFace live API   — public GGUF search, no auth
///   2. Curated catalog        — hand-written metadata for ~40 known-good models
///
/// Both run in parallel. Results are merged and deduplicated by HF repo ID.
/// Curated entries are enriched with descriptions, role recommendations, and tool-use notes.
/// Uncurated HF results get basic computed metadata (VRAM estimate from size).
///
/// Stale catalog detection:
///   Curated entries whose HF repo no longer exists are hidden and logged
///   (never crash) — the live search still shows newer alternatives.
/// </summary>
public sealed class ModelSearchService : IDisposable
{
    private readonly HuggingFaceClient _hf;

    public ModelSearchService(string? accessToken = null, AppSettings? settings = null)
    {
        _hf = new HuggingFaceClient(accessToken, settings);
    }

    // ── Main search entry point ───────────────────────────────────────────────

    /// <summary>
    /// Search for models matching <paramref name="query"/>.
    /// Runs HF API and catalog search in parallel, merges results.
    /// Progress is reported via <paramref name="onStatus"/> for UI feedback.
    /// </summary>
    public async Task<List<ModelSearchResult>> SearchAsync(
        string query,
        int userVramGb = 0,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        onStatus?.Invoke("Searching HuggingFace…");

        // Run both sources in parallel
        var hfTask       = _hf.SearchGgufAsync(query, limit: 30, ct);
        var curatedTask  = Task.Run(() => CuratedModelCatalog.Search(query).ToList(), ct);

        await Task.WhenAll(hfTask, curatedTask);

        var hfResults   = hfTask.Result;
        var curated     = curatedTask.Result;

        onStatus?.Invoke("Merging results…");

        return Merge(hfResults, curated, userVramGb);
    }

    // ── Detail view (quantization variants) ──────────────────────────────────

    /// <summary>
    /// Fetch the GGUF variant list for a specific result (shown in the detail view).
    /// Resolves live from HF — never cached pre-emptively.
    /// </summary>
    public Task<List<GgufVariant>> GetVariantsAsync(
        ModelSearchResult result,
        int userVramGb = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(result.HuggingFaceId))
            return Task.FromResult<List<GgufVariant>>([]);

        return _hf.GetGgufVariantsAsync(
            result.HuggingFaceId,
            result.RecommendedQuant,
            userVramGb,
            ct);
    }

    // ── Merging logic ─────────────────────────────────────────────────────────

    private static List<ModelSearchResult> Merge(
        List<HfModelSearchResult> hfResults,
        List<CuratedModelEntry> curated,
        int userVramGb)
    {
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged  = new List<ModelSearchResult>();

        // ── 1. Curated-first pass ────────────────────────────────────────────
        // Add all curated entries (in quality order) that match the query.
        // If the same entry also appeared in HF results, it gets the HF download stats.
        foreach (var ce in curated.OrderByDescending(c => c.QualityStars))
        {
            if (!seen.Add(ce.HuggingFaceId.ToLowerInvariant())) continue;

            // Find matching HF hit (if any) for stats
            var hfHit = hfResults.FirstOrDefault(h =>
                string.Equals(h.Id, ce.HuggingFaceId, StringComparison.OrdinalIgnoreCase));

            merged.Add(CuratedToResult(ce, hfHit));
        }

        // ── 2. HF-only pass ──────────────────────────────────────────────────
        // Add HF results that aren't in our curated list.
        foreach (var hf in hfResults.OrderByDescending(h => h.Downloads))
        {
            if (!seen.Add(hf.Id.ToLowerInvariant())) continue;
            merged.Add(HfToResult(hf, userVramGb));
        }

        return merged;
    }

    private static ModelSearchResult CuratedToResult(
        CuratedModelEntry ce, HfModelSearchResult? hfHit)
    {
        return new ModelSearchResult
        {
            Id               = ce.Id,
            Name             = ce.Name,
            HuggingFaceId    = ce.HuggingFaceId,
            OllamaName       = ce.OllamaName,
            Publisher        = ce.Publisher,
            Architecture     = ce.Architecture,
            IsCurated        = true,
            IsFromHuggingFace = !string.IsNullOrEmpty(ce.HuggingFaceId),
            IsFromOllama     = !string.IsNullOrEmpty(ce.OllamaName),
            Description      = ce.Description,
            IntendedUse      = ce.IntendedUse,
            ToolUseNotes     = ce.ToolUse,
            SwarmRoles       = ce.SwarmRoles,
            SwarmCapable     = ce.SwarmCapable,
            QualityStars     = ce.QualityStars,
            RecommendedQuant = ce.RecommendedQuant,
            VramMinGb        = ce.VramMinGb,
            VramRecommendedGb = ce.VramRecommendedGb,
            CpuOk            = ce.CpuOk,
            ContextK         = ce.ContextK,
            HfDownloads      = hfHit?.Downloads ?? 0,
            HfLikes          = hfHit?.Likes ?? 0,
            LastUpdated      = hfHit?.LastModified,
        };
    }

    private static ModelSearchResult HfToResult(
        HfModelSearchResult hf, int userVramGb)
    {
        // Try to estimate VRAM from largest GGUF sibling if present
        var ggufFiles = hf.Siblings?
            .Where(s => s.Filename.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? [];

        var typicalSize = ggufFiles.Count > 0
            ? ggufFiles.OrderBy(f => f.Size ?? 0).Skip(ggufFiles.Count / 2).FirstOrDefault()?.Size ?? 0
            : 0L;

        var vramEst = typicalSize > 0
            ? (int)Math.Ceiling(typicalSize / 1_073_741_824.0 * 1.15)
            : 0;

        // Extract friendly name from the repo ID (remove -GGUF suffix)
        var name = hf.Id.Split('/').Last()
                         .Replace("-GGUF", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("-gguf", "", StringComparison.OrdinalIgnoreCase)
                         .Replace("-", " ");

        return new ModelSearchResult
        {
            Id                = hf.Id,
            Name              = name,
            HuggingFaceId     = hf.Id,
            OllamaName        = "",
            Publisher         = hf.Author,
            Architecture      = "",
            IsCurated         = false,
            IsFromHuggingFace = true,
            IsFromOllama      = false,
            Description       = "",   // fetched lazily when user opens detail view
            IntendedUse       = "",
            ToolUseNotes      = "",
            SwarmRoles        = [],
            SwarmCapable      = false,
            QualityStars      = 0,
            RecommendedQuant  = "",
            VramMinGb         = vramEst,
            VramRecommendedGb = vramEst,
            CpuOk             = false,
            ContextK          = 0,
            HfDownloads       = hf.Downloads,
            HfLikes           = hf.Likes,
            LastUpdated       = hf.LastModified,
        };
    }

    // ── Stale detection ───────────────────────────────────────────────────────

    /// <summary>
    /// Background verify pass — checks all curated entries with an HF ID
    /// to make sure the repo still exists. Returns IDs of any that are gone.
    /// Called on downloader open; non-blocking (fire-and-forget is fine).
    /// </summary>
    public async Task<List<string>> VerifyCuratedReposAsync(CancellationToken ct = default)
    {
        var stale = new System.Collections.Concurrent.ConcurrentBag<string>();
        var tasks = CuratedModelCatalog.All
            .Where(e => !string.IsNullOrEmpty(e.HuggingFaceId))
            .Select(async e =>
            {
                var exists = await _hf.RepoExistsAsync(e.HuggingFaceId, ct);
                if (!exists) stale.Add(e.Id);
            });

        await Task.WhenAll(tasks);
        return stale.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void Dispose() => _hf.Dispose();
}
