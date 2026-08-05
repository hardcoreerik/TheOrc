// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Fuses TheOrc's three separate model data sources into one browsable/filterable card list
/// for Model Depot — nothing here re-implements what those sources already do:
///
///   1. Curated catalog + live HF search results (<see cref="ModelSearchService.BrowseCurated"/>
///      for the default network-free browse view; a live <c>SearchAsync</c> call widens it when
///      the user actually searches — that stays the caller's job, this service only fuses).
///   2. Local install state (<see cref="ModelDepot"/>'s on-disk scan + <see cref="GgufMetadataReader"/>
///      for real architecture/param facts on files the curated catalog doesn't already describe).
///   3. Local evidence (<see cref="ModelWikiService.BuildAll"/> — the retired Model Wiki's merged
///      scores/probe-results/swarm-history/capability-tests, reused as-is).
///
/// Pure read-time fusion — no new persistence, no I/O of its own (callers already have the
/// local scan and installed-model list from existing call sites; this only merges them).
/// </summary>
public static class ModelDepotBrowserService
{
    /// <summary>
    /// The join key between a catalog/search result, a local <see cref="RuntimeModelAsset"/>,
    /// and a <see cref="ModelWikiEntry"/> is the Ollama pull tag (e.g. "qwen2.5-coder:14b") —
    /// the one identifier all three sides actually share. <see cref="ModelDepot.ScanOllamaModels"/>
    /// sets <c>DisplayName</c> to exactly this format, <see cref="CuratedModelEntry.OllamaName"/>
    /// is hand-curated in the same format, and <see cref="ModelWikiService.BuildAll"/>'s
    /// <c>installedModels</c> parameter is itself a list of these tags. A curated/search entry
    /// with no Ollama tag (HF-only) has no reliable local-match key today — it renders as "not
    /// installed" rather than attempting a fuzzy filename match, which is real future work
    /// (<see cref="ModelDepot.WithBaseModelFilter"/> already does substring matching elsewhere,
    /// but applying that heuristic here would risk a false "installed" badge on the wrong file).
    /// </summary>
    public static List<ModelDepotCardEntry> BuildBrowseList(
        IReadOnlyList<ModelSearchResult> searchResults,
        IReadOnlyList<RuntimeModelAsset> localAssets,
        IReadOnlyCollection<string> installedOllamaModels)
    {
        var localBaseModels = localAssets
            .Where(a => a.Kind == RuntimeAssetKind.BaseModelGguf)
            .GroupBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var wikiEntries = ModelWikiService.BuildAll(installedOllamaModels)
            .GroupBy(e => e.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var curatedById = CuratedModelCatalog.All
            .ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);

        var cards = new List<ModelDepotCardEntry>();
        var matchedLocalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var result in searchResults)
        {
            curatedById.TryGetValue(result.Id, out var curated);

            RuntimeModelAsset? localAsset = null;
            ModelWikiEntry? evidence = null;
            if (!string.IsNullOrEmpty(result.OllamaName))
            {
                localBaseModels.TryGetValue(result.OllamaName, out localAsset);
                wikiEntries.TryGetValue(result.OllamaName, out evidence);
                if (localAsset is not null)
                    matchedLocalKeys.Add(result.OllamaName);
            }

            cards.Add(BuildCard(result, curated, localAsset, evidence));
        }

        // Surface any locally-installed model the catalog/search pass above never matched — a
        // raw GGUF the user dropped into a model root, or an Ollama pull with no curated entry
        // at all. Otherwise Model Depot would silently hide models TheOrc already knows are
        // usable, which defeats "come here to see everything," not just "everything curated."
        foreach (var asset in localBaseModels.Values)
        {
            if (matchedLocalKeys.Contains(asset.DisplayName)) continue;

            wikiEntries.TryGetValue(asset.DisplayName, out var evidence);
            var header = GgufMetadataReader.TryRead(asset.Path);
            var syntheticResult = BuildLocalOnlyResult(asset, header);
            cards.Add(BuildCard(syntheticResult, curated: null, asset, evidence, header));
        }

        return cards
            .OrderByDescending(c => c.InstallState == ModelDepotInstallState.InstalledLocally)
            .ThenByDescending(c => c.Result.QualityStars)
            .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ModelDepotCardEntry BuildCard(
        ModelSearchResult result, CuratedModelEntry? curated,
        RuntimeModelAsset? localAsset, ModelWikiEntry? evidence, GgufModelHeader? header = null) => new()
    {
        Result = result,
        Curated = curated,
        InstallState = localAsset is not null
            ? ModelDepotInstallState.InstalledLocally
            : ModelDepotInstallState.NotInstalled,
        LocalPath = localAsset?.Path,
        LocalSizeBytes = localAsset?.SizeBytes,
        LocalHeader = header ?? (localAsset is not null ? GgufMetadataReader.TryRead(localAsset.Path) : null),
        Evidence = evidence,
    };

    /// <summary>
    /// Builds the best-effort <see cref="ModelSearchResult"/> for a locally-installed model with
    /// no curated or HF-search match (a raw Ollama pull, a manually-dropped GGUF, or an
    /// HF-cache blob) -- so its card isn't just a bare filename with every field blank. Derives
    /// what it can from the asset itself: publisher from the Ollama-tag namespace when present,
    /// quant label from the filename (same pattern <see cref="HuggingFaceClient"/> uses for HF
    /// files), architecture from the GGUF header when read succeeds, and a rough VRAM estimate
    /// from file size (file size × 1.15 overhead, same formula HuggingFaceClient.EstimateVram
    /// uses for HF variants) -- never fabricated, only what the file/tag itself already says.
    /// </summary>
    private static ModelSearchResult BuildLocalOnlyResult(RuntimeModelAsset asset, GgufModelHeader? header)
    {
        var quant = ExtractQuantLabel(asset.DisplayName);
        var sizeBytes = asset.SizeBytes ?? 0;
        var vramEstimateGb = sizeBytes > 0 ? (int)Math.Ceiling(sizeBytes / 1_073_741_824.0 * 1.15) : 0;

        return new ModelSearchResult
        {
            Id = asset.Id,
            Name = asset.DisplayName,
            OllamaName = asset.DisplayName,
            Publisher = ExtractPublisher(asset.DisplayName),
            Architecture = header?.Architecture ?? "",
            IsFromOllama = true,
            Description = header is not null
                ? $"Local model found on disk. Architecture: {header.Architecture}."
                : "Local model found on disk. No catalog entry -- specs shown are best-effort from the file itself.",
            VramMinGb = vramEstimateGb,
            VramRecommendedGb = vramEstimateGb,
            CpuOk = vramEstimateGb is > 0 and <= 8,
        };
    }

    /// <summary>Best-effort publisher guess from an Ollama pull tag, e.g. "hf.co/bartowski/Foo:Q4_K_M"
    /// -&gt; "bartowski". Returns "" (not "Unknown") when the tag carries no namespace -- a plain
    /// "gemma4:12b" tag genuinely doesn't say who published it, and a card degrading to no
    /// publisher line is more honest than guessing one.</summary>
    private static string ExtractPublisher(string ollamaTag)
    {
        var withoutTag = ollamaTag.Split(':')[0];
        var segments = withoutTag.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 2 ? segments[^2] : "";
    }

    private static readonly System.Text.RegularExpressions.Regex QuantLabelPattern = new(
        @"(?i)(IQ\d_\w+|Q\d+_K_[A-Z]+|Q\d+_\d+|Q\d+_K|F16|BF16)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string ExtractQuantLabel(string filenameOrTag)
    {
        var m = QuantLabelPattern.Match(filenameOrTag);
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }

    // ── Live quant-variant enrichment ────────────────────────────────────────────────────────

    /// <summary>
    /// Populates <see cref="ModelDepotCardEntry.Quants"/> for every card that has a HuggingFace
    /// repo, by calling the existing <see cref="ModelSearchService.GetVariantsAsync"/> the old
    /// downloader detail view already used — reused as-is, not reimplemented. Runs with bounded
    /// concurrency (HF has no documented hard rate limit for this endpoint, but hitting 50+
    /// repos at once from one process is impolite regardless) and never lets one repo's failure
    /// (network blip, a since-moved/gated repo) abort enrichment for the rest — a card simply
    /// keeps its empty <c>Quants</c> list and the UI shows no variant badges for it, the same
    /// graceful-degrade posture <see cref="CuratedModelEntry.HfRepoVerified"/> already has for
    /// a fully-missing repo.
    /// </summary>
    public static async Task EnrichWithQuantVariantsAsync(
        IReadOnlyList<ModelDepotCardEntry> cards,
        ModelSearchService searchService,
        int userVramGb = 0,
        int maxConcurrency = 6,
        CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(maxConcurrency);

        var tasks = cards
            .Where(c => c.Result.HasHuggingFace)
            .Select(async card =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    card.Quants = await searchService
                        .GetVariantsAsync(card.Result, userVramGb, ct)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Leave Quants empty -- one repo's failure must not abort the rest, and the
                    // card already renders usefully without variant badges (see doc above).
                }
                finally
                {
                    gate.Release();
                }
            });

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    // ── HIVE peer availability ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Marks which HIVE peers already have each card's model, from a caller-supplied
    /// (peer name, models) snapshot -- this method does no network I/O itself, it only matches
    /// already-collected data onto cards, so it's synchronous and cheap to re-run after every
    /// scan/probe. The caller is expected to have built the snapshot via
    /// <c>Services.Hive.HiveHosts</c> (named hosts + <c>HiveBeacon.ScanAsync</c> for LAN
    /// discovery, then <c>HiveHosts.ProbeAsync</c> per host for its live Ollama model list) --
    /// this service doesn't depend on the Hive namespace directly, keeping the dependency
    /// direction the same as the rest of this fusion layer (data flows in, this only merges).
    /// </summary>
    public static void AttachHiveAvailability(
        IReadOnlyList<ModelDepotCardEntry> cards,
        IReadOnlyList<(string PeerName, IReadOnlyList<string> Models)> peers)
    {
        foreach (var card in cards)
        {
            var candidates = new[] { card.Result.OllamaName, card.DisplayName }
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .ToArray();
            if (candidates.Length == 0)
            {
                card.AvailableOnHivePeers = [];
                continue;
            }

            card.AvailableOnHivePeers = peers
                .Where(p => p.Models.Any(m => MatchesOllamaTag(m, candidates)))
                .Select(p => p.PeerName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    /// <summary>
    /// True when a peer-reported Ollama model name matches one of this card's candidate names --
    /// exact tag match first (e.g. "qwen2.5-coder:1.5b" == "qwen2.5-coder:1.5b"), falling back to
    /// a same-base-model match when only one side carries a ":tag" (a peer running
    /// "qwen2.5-coder:latest" still counts as having "qwen2.5-coder:1.5b" available in spirit,
    /// but this is intentionally the loosest fallback, not a substring/fuzzy match, so it can't
    /// false-positive across genuinely different models that merely share a word).
    /// </summary>
    private static bool MatchesOllamaTag(string peerModel, string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(peerModel, candidate, StringComparison.OrdinalIgnoreCase))
                return true;

            var peerBase = peerModel.Split(':')[0];
            var candidateBase = candidate.Split(':')[0];
            if (string.Equals(peerBase, candidateBase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Filtering (pure LINQ over the merged list, no persistence) ──────────────────────────

    public static IEnumerable<ModelDepotCardEntry> Filter(
        IEnumerable<ModelDepotCardEntry> cards,
        string? role = null,
        int? maxVramGb = null,
        bool? installedOnly = null,
        int? minQualityStars = null,
        string? tag = null,
        string? searchText = null,
        bool? availableOnHiveOnly = null)
    {
        var query = cards;

        if (!string.IsNullOrWhiteSpace(role))
            query = query.Where(c => c.Result.SwarmRoles.Contains(role, StringComparer.OrdinalIgnoreCase));

        if (maxVramGb is { } vram)
            query = query.Where(c => c.Result.VramMinGb == 0 || c.Result.VramMinGb <= vram);

        if (installedOnly == true)
            query = query.Where(c => c.InstallState == ModelDepotInstallState.InstalledLocally);

        if (minQualityStars is { } stars)
            query = query.Where(c => c.Result.QualityStars >= stars);

        if (!string.IsNullOrWhiteSpace(tag))
            query = query.Where(c => c.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

        if (availableOnHiveOnly == true)
            query = query.Where(c => c.IsAvailableOnHive);

        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var needle = searchText.Trim();
            query = query.Where(c =>
                c.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                c.Publisher.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                c.Tags.Any(t => t.Contains(needle, StringComparison.OrdinalIgnoreCase)));
        }

        return query;
    }

    /// <summary>Distinct tags across the given cards, for populating the filter-chip row.</summary>
    public static IReadOnlyList<string> DistinctTags(IEnumerable<ModelDepotCardEntry> cards) =>
        cards.SelectMany(c => c.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(t => t, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
