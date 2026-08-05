// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Models;

public enum ModelDepotInstallState
{
    NotInstalled,
    InstalledLocally,
}

/// <summary>
/// One card's worth of data for Model Depot's browse grid. Wraps the existing
/// <see cref="ModelSearchResult"/> as-is (identity, curated/HF/Ollama provenance, hardware
/// profile, HF stats, and all of its computed display helpers — StarsDisplay, VramDisplay,
/// ContextDisplay, PrimaryRoleDisplay) rather than re-deriving those fields, plus the two things
/// it doesn't carry: real local install state from <see cref="ModelDepot"/>'s on-disk scan, and
/// the retired Model Wiki's merged local evidence (<see cref="ModelWikiEntry"/> — scores, GOBLIN
/// MIND probe results, swarm run history, capability-test results) when a ModelId match exists.
///
/// <see cref="Curated"/> is attached separately, not folded into <see cref="Result"/>, because
/// <see cref="ModelSearchResult"/> deliberately doesn't carry <c>Tags</c>/<c>ParametersB</c> —
/// this type must not force a change to that existing, tested type just to add two fields.
/// </summary>
public sealed class ModelDepotCardEntry
{
    public required ModelSearchResult Result { get; init; }
    public CuratedModelEntry? Curated { get; init; }
    public ModelDepotInstallState InstallState { get; init; }
    public string? LocalPath { get; init; }
    public long? LocalSizeBytes { get; init; }
    public GgufModelHeader? LocalHeader { get; init; }
    public ModelWikiEntry? Evidence { get; init; }

    /// <summary>
    /// Real GGUF quantization variants for this model, resolved live from HuggingFace's file
    /// listing (<see cref="ModelSearchService.GetVariantsAsync"/> — the same call the old
    /// downloader UI's detail view already made, just now surfaced on the card itself instead
    /// of hidden until a click-through). Empty until <c>EnrichWithQuantVariantsAsync</c> runs;
    /// mutable (not <c>init</c>) because it's populated as a second pass after the card is
    /// built, not at construction time — quant resolution is a real network call per model, not
    /// something the fast default browse list should block on.
    /// </summary>
    public List<GgufVariant> Quants { get; set; } = [];

    public string QuantsDisplay => Quants.Count switch
    {
        0 => "",
        1 => Quants[0].QuantLabel,
        _ => string.Join(" · ", Quants
            .OrderByDescending(v => v.IsRecommended)
            .ThenBy(v => v.SizeBytes)
            .Select(v => v.QuantLabel)),
    };

    /// <summary>
    /// Names of HIVE peers (from <c>Services.Hive.HiveHost.Name</c>) whose Ollama model list
    /// contains this card's model, populated by <c>ModelDepotBrowserService.AttachHiveAvailability</c>
    /// after a "Search PC/Network" scan. Empty until that scan runs — mutable for the same reason
    /// <see cref="Quants"/> is: a live network probe per peer, not something the initial local
    /// build should block on.
    /// </summary>
    public List<string> AvailableOnHivePeers { get; set; } = [];

    public bool IsAvailableOnHive => AvailableOnHivePeers.Count > 0;

    public string HiveAvailabilityDisplay => AvailableOnHivePeers.Count switch
    {
        0 => "",
        1 => $"🐝 {AvailableOnHivePeers[0]}",
        _ => $"🐝 {string.Join(", ", AvailableOnHivePeers)}",
    };

    // ── Convenience passthroughs for binding without reaching into Result/Curated ───────────

    public string DisplayName        => Result.Name;
    public string Publisher          => Result.Publisher;
    public string StarsDisplay       => Result.StarsDisplay;
    public string VramDisplay        => Result.VramDisplay;
    public string ContextDisplay     => Result.ContextDisplay;
    public string PrimaryRoleDisplay => Result.PrimaryRoleDisplay;

    /// <summary>"✔ Verified" for a hand-curated entry, "🌐 Community" for an uncurated live HF
    /// search hit — mirrors <see cref="CuratedModelEntry.HfRepoVerified"/>'s existing
    /// verified/unverified distinction, surfaced as a card badge.</summary>
    public string SourceBadge => Result.IsCurated ? "✔ Verified" : "🌐 Community";

    public string InstallBadge =>
        InstallState == ModelDepotInstallState.InstalledLocally ? "✅ Installed" : "⬇ Download";

    public string[] Tags => Curated?.Tags ?? [];

    public double ParametersB => Curated?.ParametersB ?? 0;

    public string ParameterDisplay => Curated?.ParameterDisplay ?? "";

    public bool HasEvidence => Evidence is not null;
}
