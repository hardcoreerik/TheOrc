// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrchestratorIDE.Services.Hive;

public static class HiveExecutionKinds
{
    public const string LegacyAgent  = "legacy_agent";
    public const string NativeAgent  = "native_agent";
    public const string ContainerPack = "container_pack";
}

public static class CampaignStates
{
    public const string Draft     = "draft";
    public const string Running   = "running";
    public const string Paused    = "paused";
    public const string Verifying = "verifying";
    public const string Completed = "completed";
    public const string Failed    = "failed";
    public const string Cancelled = "cancelled";
}

public sealed record ArtifactRef
{
    public string DigestSha256 { get; init; } = "";
    public string Name         { get; init; } = "";
    public long   SizeBytes    { get; init; }
    public string MediaType    { get; init; } = "application/octet-stream";
    public string? SourceUri   { get; init; }
    public string Kind         { get; init; } = "output";
}

public sealed record ApprovedModelAsset(string DigestSha256, long SizeBytes);

public sealed record ResourceRequirements
{
    public int MinCpuCores       { get; init; } = 1;
    public long MinMemoryMb      { get; init; }
    public long MinVramMb        { get; init; }
    public string Os             { get; init; } = "";
    public string Architecture   { get; init; } = "";
    public string NativeModelHash { get; init; } = "";
    public string NativeAdapterHash { get; init; } = "";
    public string ContainerEngine { get; init; } = "";
    public string[] RequiredPacks { get; init; } = [];
    public string[] ExcludedWorkerIds { get; init; } = [];
}

public sealed record VerificationPolicy
{
    public string Mode              { get; init; } = "hash_only";
    public int RequiredIndependentRuns { get; init; } = 1;
    public bool RequireDifferentNode   { get; init; }
    public string? JsonSchema          { get; init; }
}

public sealed record ExecutionAttestation
{
    public string RuntimeName      { get; init; } = "";
    public string Backend          { get; init; } = "";
    public string ModelHash        { get; init; } = "";
    public string AdapterHash      { get; init; } = "";
    public string ContainerDigest  { get; init; } = "";
    public string ToolTraceDigest  { get; init; } = "";
    public Dictionary<string, string> InputDigests { get; init; } = [];
}

public sealed record PackManifest
{
    public string PackId            { get; init; } = "";
    public string Version           { get; init; } = "";
    public string DisplayName       { get; init; } = "";
    public string ExecutionKind     { get; init; } = HiveExecutionKinds.ContainerPack;
    public string ImageDigest       { get; init; } = "";
    public string[] AllowedArguments { get; init; } = [];
    public int MaxRuntimeSeconds    { get; init; } = 3600;
    public long MaxOutputBytes      { get; init; } = 64 * 1024 * 1024;
    public bool NetworkDuringExecution { get; init; }
    public bool BuiltIn             { get; init; } = true;
}

public sealed record WorkUnit
{
    public string WorkUnitId       { get; init; } = Guid.NewGuid().ToString("N");
    public string CampaignId       { get; init; } = "";
    public string Title            { get; init; } = "";
    public string Role             { get; init; } = "Worker";
    /// <summary>Fine-grained dispatch hint within a pack — used to route to sub-paths that share a PackId.
    /// For example CampaignPackCatalog.ContextFabricReducerRole routes the reducer within theorc.context-fabric.
    /// Empty means the pack's default execution path.</summary>
    public string NativeRole       { get; init; } = "";
    public string Spec             { get; init; } = "";
    public string ExecutionKind    { get; init; } = HiveExecutionKinds.NativeAgent;
    public string PackId           { get; init; } = "";
    public string PackVersion      { get; init; } = "";
    public ResourceRequirements Requirements { get; init; } = new();
    public VerificationPolicy Verification { get; init; } = new();
    public Dictionary<string, JsonElement> Parameters { get; init; } = [];
    public List<ArtifactRef> Inputs { get; init; } = [];
    public int TimeoutMs           { get; init; } = 600_000;
    public int MaxAttempts         { get; init; } = 3;

    /// <summary>
    /// WorkUnitIds (within the same campaign) that must reach "completed" before this unit
    /// becomes eligible for lease/claim. Stage/dependency-barrier support for CF-6 -- see
    /// HiveTaskQueue.AreDependenciesSatisfied for the dispatch-side check.
    /// </summary>
    public string[] DependsOn      { get; init; } = [];
}

public sealed record CampaignDefinition
{
    public string CampaignId       { get; init; } = Guid.NewGuid().ToString("N");
    public string Name             { get; init; } = "";
    public string PackId           { get; init; } = "";
    public string PackVersion      { get; init; } = "";
    public string Status           { get; init; } = CampaignStates.Draft;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<WorkUnit> WorkUnits { get; init; } = [];
}

public sealed record CampaignResult
{
    public string CampaignId { get; init; } = "";
    public string Status     { get; init; } = CampaignStates.Verifying;
    public string Summary    { get; init; } = "";
    public Dictionary<string, double> Metrics { get; init; } = [];
    public List<ArtifactRef> Artifacts { get; init; } = [];
    public bool AcceptedByWarchief { get; init; }
}

public sealed record WorkerCapabilities
{
    public string WorkerId           { get; init; } = "";
    public string Os                 { get; init; } = RuntimeInformation.OSDescription;
    public string Architecture       { get; init; } = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public int CpuCores              { get; init; } = Environment.ProcessorCount;
    public long AvailableMemoryMb    { get; init; }
    public long FreeVramMb           { get; init; }
    public string NativeBackend      { get; init; } = "cpu";
    public string[] NativeModelHashes { get; init; } = [];
    public string[] NativeAdapterHashes { get; init; } = [];
    public string ContainerEngine    { get; init; } = "";
    public string[] InstalledPacks   { get; init; } = [];
    public string[] CachedArtifacts  { get; init; } = [];
    public string[] ExecutionKinds   { get; init; } = [HiveExecutionKinds.LegacyAgent, HiveExecutionKinds.NativeAgent];
    public int ActiveSlots           { get; init; }
    public int MaxSlots              { get; init; } = 1;
}

public sealed record HiveLeaseRequest
{
    public string WorkerId    { get; init; } = "";
    public string WorkerUrl   { get; init; } = "";
    public string[] Lanes     { get; init; } = [];
    public WorkerCapabilities Capabilities { get; init; } = new();
}

public sealed record HiveLeaseResponse
{
    public HiveTaskBundle Bundle { get; init; } = new();
    public string ClaimToken     { get; init; } = "";
}

public sealed record CampaignStatusSnapshot
{
    public string CampaignId { get; init; } = "";
    public string Name       { get; init; } = "";
    public string Status     { get; init; } = "";
    public int Total         { get; init; }
    public int Pending       { get; init; }
    public int Running       { get; init; }
    public int Verifying     { get; init; }
    public int Completed     { get; init; }
    public int Failed        { get; init; }
    public int Cancelled     { get; init; }
}

public static class CampaignCapabilityMatcher
{
    public static bool IsEligible(HiveTaskBundle bundle, WorkerCapabilities worker)
    {
        if (!worker.ExecutionKinds.Contains(bundle.ExecutionKind, StringComparer.OrdinalIgnoreCase))
            return false;

        var r = bundle.Requirements;
        if (worker.CpuCores < Math.Max(1, r.MinCpuCores) || worker.AvailableMemoryMb < r.MinMemoryMb || worker.FreeVramMb < r.MinVramMb)
            return false;
        if (r.Os.Length > 0 && !worker.Os.Contains(r.Os, StringComparison.OrdinalIgnoreCase))
            return false;
        if (r.Architecture.Length > 0 && !worker.Architecture.Equals(r.Architecture, StringComparison.OrdinalIgnoreCase))
            return false;
        if (r.NativeModelHash.Length > 0 && !worker.NativeModelHashes.Contains(r.NativeModelHash, StringComparer.OrdinalIgnoreCase))
            return false;
        if (r.NativeAdapterHash.Length > 0 && !worker.NativeAdapterHashes.Contains(r.NativeAdapterHash, StringComparer.OrdinalIgnoreCase))
            return false;
        if (r.ContainerEngine.Length > 0 && !worker.ContainerEngine.Equals(r.ContainerEngine, StringComparison.OrdinalIgnoreCase))
            return false;
        if (r.ExcludedWorkerIds.Contains(worker.WorkerId, StringComparer.OrdinalIgnoreCase))
            return false;
        return r.RequiredPacks.All(p => worker.InstalledPacks.Contains(p, StringComparer.OrdinalIgnoreCase));
    }

    public static int Score(HiveTaskBundle bundle, WorkerCapabilities worker)
    {
        var cachedInputs = bundle.InputArtifacts.Count(a => worker.CachedArtifacts.Contains(a.DigestSha256, StringComparer.OrdinalIgnoreCase));
        var modelLoaded = bundle.Requirements.NativeModelHash.Length > 0 &&
                          worker.NativeModelHashes.Contains(bundle.Requirements.NativeModelHash, StringComparer.OrdinalIgnoreCase);
        return cachedInputs * 1000 + (modelLoaded ? 500 : 0) + (int)Math.Min(worker.FreeVramMb, 100_000) - worker.ActiveSlots * 100;
    }
}
