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
    /// CF-6: stages a stripped corpus-meta artifact (no segment text — structural metadata only) into
    /// <paramref name="store"/> for use as the reducer's structural input. Returns the content-addressed
    /// <see cref="ArtifactRef"/> that the reducer work unit lists as its "corpus-meta.json" input.
    /// </summary>
    public static async Task<ArtifactRef> StageReducerCorpusMetaAsync(
        FabricCorpus corpus, ContentAddressedStore store, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(store);

        // Strip segment text — the reducer only needs structural metadata (CorpusId, DocumentId, GenerationId).
        var stripped = corpus with
        {
            Segments = corpus.Segments
                .Select(s => s with { Text = "", TextDigest = "", EstimatedTokens = 0 })
                .ToArray(),
            EstimatedSourceTokens = 0,
        };
        var bytes = Encoding.UTF8.GetBytes(FabricJson.Serialize(stripped));
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
        return new ArtifactRef
        {
            DigestSha256 = digest,
            Name = "corpus-meta.json",
            SizeBytes = bytes.Length,
            MediaType = "application/json",
            Kind = "input",
        };
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
    /// <summary>
    /// CF-6: builds a single reduce work unit that fans in all reader output evidence-card artifacts and
    /// the staged corpus-meta artifact. The reduce unit lists all reader unit IDs in <see cref="WorkUnit.DependsOn"/>
    /// so the dependency barrier in <see cref="HiveTaskQueue"/> keeps it pending until every reader completes.
    /// </summary>
    public static CampaignDefinition ContextFabricReducer(
        string name,
        ArtifactRef corpusMetaArtifact,
        IReadOnlyList<ArtifactRef> readerOutputCardRefs,
        IReadOnlyList<string> readerWorkUnitIds,
        string modelHash,
        string adapterHash = "")
    {
        var unit = new WorkUnit
        {
            WorkUnitId = "reduce-00001",
            Title = $"Context Fabric reduce: {readerOutputCardRefs.Count} evidence card(s)",
            Role = "Researcher",
            NativeRole = CampaignPackCatalog.ContextFabricReducerRole,
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
            Inputs = [corpusMetaArtifact, .. readerOutputCardRefs],
            DependsOn = [.. readerWorkUnitIds],
            TimeoutMs = 3_600_000,
        };
        return new CampaignDefinition
        {
            Name = name,
            PackId = CampaignPackCatalog.ContextFabricPackId,
            PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
            WorkUnits = [unit],
        };
    }

    /// <summary>
    /// CF-6: one stitcher work unit per adjacent segment pair. Each unit receives the two single-segment
    /// corpus artifacts for the left and right segments and depends on both reader work units completing
    /// first. The stitcher resolves cross-boundary duplicate claims, pronouns, and heading transitions.
    /// </summary>
    public static CampaignDefinition ContextFabricStitchers(
        string name,
        IReadOnlyList<(ArtifactRef LeftCorpus, ArtifactRef RightCorpus, string LeftReaderId, string RightReaderId)> pairs,
        string modelHash,
        string adapterHash = "")
    {
        var units = pairs.Select((pair, index) => new WorkUnit
        {
            WorkUnitId = $"stitch-{index + 1:00000}",
            Title = $"Context Fabric stitch: {pair.LeftCorpus.Name} ↔ {pair.RightCorpus.Name}",
            Role = "Researcher",
            NativeRole = CampaignPackCatalog.ContextFabricStitcherRole,
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
            Inputs = [pair.LeftCorpus, pair.RightCorpus],
            DependsOn = [pair.LeftReaderId, pair.RightReaderId],
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

    /// <summary>
    /// CF-6: one verifier work unit per reader output evidence card. Each unit receives the evidence-card
    /// artifact and the corresponding source-corpus artifact and depends on the reader work unit.
    /// The verifier checks citation offsets, quote digests, and quote text against the original source —
    /// CPU-bound, no LLM required.
    /// </summary>
    public static CampaignDefinition ContextFabricVerifiers(
        string name,
        IReadOnlyList<(ArtifactRef EvidenceCard, ArtifactRef SourceCorpus, string ReaderWorkUnitId)> items,
        string modelHash,
        string adapterHash = "")
    {
        var units = items.Select((item, index) => new WorkUnit
        {
            WorkUnitId = $"verify-{index + 1:00000}",
            Title = $"Context Fabric verify: {item.EvidenceCard.Name}",
            Role = "Researcher",
            NativeRole = CampaignPackCatalog.ContextFabricVerifierRole,
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
            Inputs = [item.EvidenceCard, item.SourceCorpus],
            DependsOn = [item.ReaderWorkUnitId],
            TimeoutMs = 300_000,
        }).ToList();
        return new CampaignDefinition
        {
            Name = name,
            PackId = CampaignPackCatalog.ContextFabricPackId,
            PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
            WorkUnits = units,
        };
    }

    /// <summary>
    /// CF-6: one exhaustive-query work unit per segment corpus artifact. The question artifact is included
    /// as input alongside each segment corpus so the worker runs the question against one segment in isolation.
    /// No DependsOn — query units are independent of reader units; they work from source text directly.
    /// A separate reducer campaign fans in the query findings to produce the final exhaustive answer.
    /// </summary>
    public static async Task<(IReadOnlyList<ArtifactRef> QuestionRefs, CampaignDefinition Campaign)>
        ContextFabricExhaustiveQueryAsync(
            string name,
            string questionId,
            string questionText,
            IReadOnlyList<ArtifactRef> segmentCorpora,
            ContentAddressedStore store,
            string modelHash,
            string adapterHash = "",
            CancellationToken ct = default)
    {
        var questionJson = FabricJson.Serialize(new FabricQueryQuestion(questionId, questionText));
        var bytes = Encoding.UTF8.GetBytes(questionJson);
        var digest = ContentAddressedStore.ComputeSha256(bytes);
        if (!store.Has(digest))
        {
            for (long offset = 0; offset < bytes.Length;)
            {
                var len = (int)Math.Min(ContentAddressedStore.MaxChunkBytes, bytes.Length - offset);
                await store.WriteChunkAsync(digest, offset, bytes.Length, bytes.AsMemory((int)offset, len), ct)
                    .ConfigureAwait(false);
                offset += len;
            }
        }
        var questionRef = new ArtifactRef
        {
            DigestSha256 = digest,
            Name = $"question-{questionId}.json",
            SizeBytes = bytes.Length,
            MediaType = "application/json",
            Kind = "input",
        };

        var questionRefs = new List<ArtifactRef>(segmentCorpora.Count);
        var units = segmentCorpora.Select((corpus, index) =>
        {
            questionRefs.Add(questionRef);
            return new WorkUnit
            {
                WorkUnitId = $"query-{index + 1:00000}",
                Title = $"Context Fabric exhaustive query: {corpus.Name}",
                Role = "Researcher",
                NativeRole = CampaignPackCatalog.ContextFabricQueryRole,
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
                Inputs = [questionRef, corpus],
                TimeoutMs = 1_800_000,
            };
        }).ToList();

        return (questionRefs.Distinct().ToList(), new CampaignDefinition
        {
            Name = name,
            PackId = CampaignPackCatalog.ContextFabricPackId,
            PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
            WorkUnits = units,
        });
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
