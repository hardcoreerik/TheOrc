// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.Services.Hive;

public static class CampaignTemplates
{
    /// <summary>
    /// CF-6: stages each segment of <paramref name="corpus"/> into <paramref name="store"/> as its own
    /// single-segment corpus artifact and returns content-addressed <see cref="ArtifactRef"/>s suitable for
    /// <see cref="ContextFabricReaders"/>. One artifact per segment lets readers fan out across workers; the
    /// digest is the store key the worker later GETs via /hive/artifacts/{digest}.
    /// </summary>
    public static async Task<IReadOnlyList<ArtifactRef>> StageReaderCorpusAsync(
        FabricCorpus corpus, ContentAddressedStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(store);

        var refs = new List<ArtifactRef>(corpus.Segments.Count);
        foreach (var segment in corpus.Segments.OrderBy(s => s.Ordinal))
        {
            var single = corpus with
            {
                Segments = [segment],
                EstimatedSourceTokens = segment.EstimatedTokens,
            };
            var bytes = Encoding.UTF8.GetBytes(FabricJson.Serialize(single));
            var digest = ContentAddressedStore.ComputeSha256(bytes);
            if (!store.Has(digest))
            {
                for (long offset = 0; offset < bytes.Length;)
                {
                    var len = (int)Math.Min(ContentAddressedStore.MaxChunkBytes, bytes.Length - offset);
                    await store.WriteChunkAsync(digest, offset, bytes.Length,
                        bytes.AsMemory((int)offset, len), ct).ConfigureAwait(false);
                    offset += len;
                }
            }
            refs.Add(new ArtifactRef
            {
                DigestSha256 = digest,
                Name = $"{segment.SegmentId}.corpus.json",
                SizeBytes = bytes.Length,
                MediaType = "application/json",
                Kind = "input",
            });
        }
        return refs;
    }

    /// <summary>
    /// CF-6: one reader work unit per staged single-segment corpus (see <see cref="StageReaderCorpusAsync"/>).
    /// Each unit routes to the Context Fabric reader pack, which bypasses the generic agent loop and runs
    /// ContextFabricFeasibilityRunner.ReadCorpusAsync over its one segment -- see CampaignPackCatalog.
    /// </summary>
    public static CampaignDefinition ContextFabricReaders(
        string name, IReadOnlyList<ArtifactRef> stagedSegments, string modelHash, string adapterHash = "")
    {
        var units = stagedSegments.Select((segment, index) => new WorkUnit
        {
            WorkUnitId = $"read-{index + 1:00000}",
            Title = $"Context Fabric read: {segment.Name}",
            Role = "Researcher",
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            PackId = CampaignPackCatalog.ContextFabricPackId,
            PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
            Requirements = new ResourceRequirements
            {
                NativeModelHash = modelHash,
                NativeAdapterHash = adapterHash,
                RequiredPacks = [$"{CampaignPackCatalog.ContextFabricPackId}@{CampaignPackCatalog.ContextFabricPackVersion}"],
            },
            Verification = new VerificationPolicy { Mode = "independent_consensus", RequiredIndependentRuns = 1 },
            Inputs = [segment],
            TimeoutMs = 1_800_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = CampaignPackCatalog.ContextFabricPackId,
            PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
            WorkUnits = units,
        };
    }
    public static CampaignDefinition NativeAiEval(string name, IEnumerable<string> prompts,
        string modelHash, string adapterHash = "")
    {
        var units = prompts.Select((prompt, index) => new WorkUnit
        {
            WorkUnitId = $"eval-{index + 1:00000}",
            Title = $"Native eval case {index + 1}",
            Role = "Worker",
            Spec = prompt,
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            PackId = "theorc.native-ai-eval",
            PackVersion = "1.0.0",
            Requirements = new ResourceRequirements
            {
                NativeModelHash = modelHash,
                NativeAdapterHash = adapterHash,
                RequiredPacks = ["theorc.native-ai-eval@1.0.0"],
            },
            Verification = new VerificationPolicy { Mode = "independent_consensus", RequiredIndependentRuns = 1 },
            TimeoutMs = 900_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = "theorc.native-ai-eval",
            PackVersion = "1.0.0",
            WorkUnits = units,
        };
    }

    public static CampaignDefinition AlienSignalSearch(string name,
        IEnumerable<ArtifactRef> observations, double maxDrift = 4, double snr = 25, bool gpu = false)
    {
        var units = observations.Select((observation, index) => new WorkUnit
        {
            WorkUnitId = $"observation-{index + 1:00000}",
            Title = $"Technosignature search: {observation.Name}",
            Role = "Compute",
            ExecutionKind = HiveExecutionKinds.ContainerPack,
            PackId = "theorc.alien-signal-search",
            PackVersion = "1.0.0",
            Requirements = new ResourceRequirements
            {
                MinCpuCores = 1,
                MinMemoryMb = 2048,
                MinVramMb = gpu ? 2048 : 0,
                RequiredPacks = ["theorc.alien-signal-search@1.0.0"],
            },
            Verification = new VerificationPolicy
            {
                Mode = "independent_rerun",
                RequiredIndependentRuns = 2,
                RequireDifferentNode = true,
            },
            Inputs = [observation],
            Parameters = new Dictionary<string, JsonElement>
            {
                ["max-drift"] = JsonSerializer.SerializeToElement(maxDrift),
                ["snr"] = JsonSerializer.SerializeToElement(snr),
                ["gpu"] = JsonSerializer.SerializeToElement(gpu),
            },
            TimeoutMs = 7_200_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = "theorc.alien-signal-search",
            PackVersion = "1.0.0",
            WorkUnits = units,
        };
    }
}
