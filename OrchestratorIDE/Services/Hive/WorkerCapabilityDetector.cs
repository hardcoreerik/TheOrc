// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.Hive;

public static class WorkerCapabilityDetector
{
    public static async Task<WorkerCapabilities> DetectAsync(string workerId, ModelDepot depot,
        long freeVramMb, ContentAddressedStore? artifactStore = null,
        IEnumerable<PackManifest>? installedPacks = null, CancellationToken ct = default,
        string verifiedNativeBackend = "cpu")
    {
        var bases = new List<string>();
        var adapters = new List<string>();
        foreach (var asset in depot.Assets.Where(a => File.Exists(a.Path)))
        {
            var digest = await ContentAddressedStore.ComputeSha256Async(asset.Path, ct).ConfigureAwait(false);
            if (asset.Kind == RuntimeAssetKind.BaseModelGguf) bases.Add(digest);
            if (asset.Kind == RuntimeAssetKind.LoraGguf) adapters.Add(digest);
        }

        var engine = await FindContainerEngineAsync(ct).ConfigureAwait(false);
        var kinds = new List<string> { HiveExecutionKinds.LegacyAgent, HiveExecutionKinds.NativeAgent };
        if (engine.Length > 0) kinds.Add(HiveExecutionKinds.ContainerPack);

        return new WorkerCapabilities
        {
            WorkerId = workerId,
            AvailableMemoryMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
            // Configured VRAM is not proof that llama.cpp loaded a GPU backend. GPU admission
            // remains disabled until a backend-specific startup smoke supplies attestation.
            FreeVramMb = verifiedNativeBackend is "cuda12" or "metal" ? freeVramMb : 0,
            NativeBackend = verifiedNativeBackend is "cuda12" or "metal" ? verifiedNativeBackend : "cpu",
            NativeModelHashes = [.. bases],
            NativeAdapterHashes = [.. adapters],
            ContainerEngine = engine,
            InstalledPacks = (installedPacks ?? CampaignPackCatalog.All)
                .Where(p => p.ExecutionKind != HiveExecutionKinds.ContainerPack || p.ImageDigest.Length > 0)
                .Select(p => $"{p.PackId}@{p.Version}").ToArray(),
            CachedArtifacts = artifactStore?.GetDigests().ToArray() ?? [],
            ExecutionKinds = [.. kinds],
        };
    }

    private static async Task<string> FindContainerEngineAsync(CancellationToken ct)
    {
        foreach (var name in new[] { "podman", "docker" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "version --format {{.Client.Version}}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var process = Process.Start(psi);
                if (process is null) continue;
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                if (process.ExitCode == 0) return name;
            }
            catch { }
        }
        return "";
    }
}
