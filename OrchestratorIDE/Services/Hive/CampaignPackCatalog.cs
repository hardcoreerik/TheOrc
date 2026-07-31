// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Services.Hive;

public static class CampaignPackCatalog
{
    public const string CaseForgePackId = "theorc.caseforge";
    public const string CaseForgePackVersion = "1.0.0";
    public const string CaseForgeCadRole = "cad-generate";
    public const string CaseForgeMeshRole = "mesh-generate";
    public const string CaseForgePortraitRole = "portrait-generate";
    public const string CaseForgeRepairRole = "mesh-repair";
    public const string CaseForgeVerifyRole = "mesh-verify";

    /// <summary>CF-6: distributed Context Fabric readers. ExecutionKind is NativeAgent for
    /// capability-matching purposes (needs a native model, no container), but dispatch bypasses
    /// the generic agent/tool-call loop entirely -- see HiveWorkerAgent.ExecuteTaskAsync's
    /// PackId check, which routes straight into ContextFabricFeasibilityRunner.ReadCorpusAsync
    /// instead of HeadlessAgentLoop. The generic NativeAgent tool profile (read_file/write_file/
    /// grep_code) doesn't fit the reader's deterministic, schema-constrained per-segment
    /// evidence extraction.</summary>
    public const string ContextFabricPackId = "theorc.context-fabric";
    public const string ContextFabricPackVersion = "1.0.0";

    /// <summary>NativeRole discriminators used inside the theorc.context-fabric pack to route tasks to
    /// specific execution paths rather than the default single-segment reader path.</summary>
    public const string ContextFabricReducerRole  = "cf-reducer";
    public const string ContextFabricStitcherRole = "cf-stitcher";
    public const string ContextFabricVerifierRole  = "cf-verifier";
    public const string ContextFabricQueryRole     = "cf-query";

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
            PackId = CaseForgePackId,
            Version = CaseForgePackVersion,
            DisplayName = "CaseForge Local 3D Studio",
            ExecutionKind = HiveExecutionKinds.ContainerPack,
            // A worker advertises this pack only after its pinned local image/model runtime is installed.
            ImageDigest = "",
            AllowedArguments = ["--role", "--job"],
            MaxRuntimeSeconds = 7200,
            MaxOutputBytes = 2L * 1024 * 1024 * 1024,
            NetworkDuringExecution = false,
            BuiltIn = false,
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
