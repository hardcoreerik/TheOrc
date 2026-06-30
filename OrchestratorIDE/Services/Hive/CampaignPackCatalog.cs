// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Hive;

public static class CampaignPackCatalog
{
    /// <summary>CF-6: distributed Context Fabric readers. ExecutionKind is NativeAgent for
    /// capability-matching purposes (needs a native model, no container), but dispatch bypasses
    /// the generic agent/tool-call loop entirely -- see HiveWorkerAgent.ExecuteTaskAsync's
    /// PackId check, which routes straight into ContextFabricFeasibilityRunner.ReadCorpusAsync
    /// instead of HeadlessAgentLoop. The generic NativeAgent tool profile (read_file/write_file/
    /// grep_code) doesn't fit the reader's deterministic, schema-constrained per-segment
    /// evidence extraction.</summary>
    public const string ContextFabricPackId = "theorc.context-fabric";
    public const string ContextFabricPackVersion = "1.0.0";

    public static IReadOnlyList<PackManifest> All { get; } =
    [
        new()
        {
            PackId = "theorc.native-ai-eval",
            Version = "1.0.0",
            DisplayName = "Native AI Eval Factory",
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            MaxRuntimeSeconds = 1800,
            MaxOutputBytes = 64 * 1024 * 1024,
        },
        new()
        {
            PackId = ContextFabricPackId,
            Version = ContextFabricPackVersion,
            DisplayName = "Context Fabric Reader",
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            MaxRuntimeSeconds = 1800,
            MaxOutputBytes = 16 * 1024 * 1024,
        },
        new()
        {
            PackId = "theorc.alien-signal-search",
            Version = "1.0.0",
            DisplayName = "Alien Signal Search",
            ExecutionKind = HiveExecutionKinds.ContainerPack,
            // Filled by release automation after the repository-owned image is published.
            ImageDigest = "",
            AllowedArguments = ["--max-drift", "--snr", "--gpu"],
            MaxRuntimeSeconds = 7200,
            MaxOutputBytes = 256 * 1024 * 1024,
            NetworkDuringExecution = false,
        },
    ];

    public static PackManifest? Find(string packId, string version) => All.FirstOrDefault(p =>
        p.PackId.Equals(packId, StringComparison.OrdinalIgnoreCase) && p.Version == version);

    public static IReadOnlyList<PackManifest> ResolveInstalled(string? alienImageDigest) => All
        .Select(p => p.PackId == "theorc.alien-signal-search" && !string.IsNullOrWhiteSpace(alienImageDigest)
            ? p with { ImageDigest = alienImageDigest }
            : p)
        .Where(p => p.ExecutionKind != HiveExecutionKinds.ContainerPack || p.ImageDigest.Length > 0)
        .ToArray();
}
