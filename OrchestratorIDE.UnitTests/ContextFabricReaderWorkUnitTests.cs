// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// CF-6 first slice: artifact-staged Context Fabric reader work units. Covers the host-side staging +
/// template builder (CampaignTemplates) and the worker-side reader execution dispatch
/// (HiveNativeRoleExecutorAdapter.ExecuteContextFabricReaderAsync) driven by the scripted runtime.
/// The /hive/artifacts HTTP fetch in HiveWorkerAgent is exercised separately by integration tests;
/// here we verify the deterministic, listener-free pieces.
/// </summary>
[TestFixture]
public sealed class ContextFabricReaderWorkUnitTests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cf6-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Test]
    public void ComputeSha256_BytesAndStream_AgreeForSameContent()
    {
        var bytes = Encoding.UTF8.GetBytes("the orc context fabric");
        var path = Path.Combine(_root, "blob.txt");
        File.WriteAllBytes(path, bytes);

        var fromBytes = ContentAddressedStore.ComputeSha256(bytes);
        var fromStream = ContentAddressedStore.ComputeSha256Async(path).GetAwaiter().GetResult();

        Assert.That(fromBytes, Is.EqualTo(fromStream));
        Assert.That(fromBytes, Does.Match("^[0-9a-f]{64}$"));
    }

    [Test]
    public async Task StageReaderCorpus_WritesOneArtifactPerSegment_AsSingleSegmentCorpora()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));

        var refs = await CampaignTemplates.StageReaderCorpusAsync(corpus, store);

        Assert.That(refs, Has.Count.EqualTo(2));
        // Ordered by ordinal: seg-a (ordinal 1) then seg-b (ordinal 2).
        Assert.That(refs[0].Name, Is.EqualTo("seg-a.corpus.json"));
        Assert.That(refs[1].Name, Is.EqualTo("seg-b.corpus.json"));

        foreach (var (artifact, expectedSegment) in refs.Zip(corpus.Segments))
        {
            Assert.That(artifact.Kind, Is.EqualTo("input"));
            Assert.That(artifact.MediaType, Is.EqualTo("application/json"));
            Assert.That(store.Has(artifact.DigestSha256), Is.True, "staged bytes must be in the store");

            var stored = File.ReadAllBytes(store.GetPath(artifact.DigestSha256));
            Assert.That(ContentAddressedStore.ComputeSha256(stored), Is.EqualTo(artifact.DigestSha256));
            Assert.That(stored.Length, Is.EqualTo(artifact.SizeBytes));

            var rebuilt = JsonSerializer.Deserialize<FabricCorpus>(
                Encoding.UTF8.GetString(stored), FabricJson.Options)!;
            Assert.That(rebuilt.Segments, Has.Count.EqualTo(1), "each staged artifact is a single-segment corpus");
            Assert.That(rebuilt.Segments[0].SegmentId, Is.EqualTo(expectedSegment.SegmentId));
            Assert.That(rebuilt.CorpusId, Is.EqualTo(corpus.CorpusId));
            Assert.That(rebuilt.EstimatedSourceTokens, Is.EqualTo(expectedSegment.EstimatedTokens));
        }
    }

    [Test]
    public async Task StageReaderCorpus_IsContentAddressed_DuplicateSegmentsCollapseToSameDigest()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));

        var first = await CampaignTemplates.StageReaderCorpusAsync(corpus, store);
        var second = await CampaignTemplates.StageReaderCorpusAsync(corpus, store);

        Assert.That(second.Select(r => r.DigestSha256), Is.EqualTo(first.Select(r => r.DigestSha256)),
            "re-staging identical segments must reuse the same content-addressed digests");
    }

    [Test]
    public async Task ContextFabricReaders_BuildsOneReaderUnitPerStagedSegment()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));
        var refs = await CampaignTemplates.StageReaderCorpusAsync(corpus, store);

        var campaign = CampaignTemplates.ContextFabricReaders("cf6-read", refs, modelHash: "model-hash-abc");

        Assert.That(campaign.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(campaign.PackVersion, Is.EqualTo(CampaignPackCatalog.ContextFabricPackVersion));
        Assert.That(campaign.WorkUnits, Has.Count.EqualTo(2));

        var unit = campaign.WorkUnits[0];
        Assert.That(unit.ExecutionKind, Is.EqualTo(HiveExecutionKinds.NativeAgent));
        Assert.That(unit.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(unit.Inputs, Has.Count.EqualTo(1));
        Assert.That(unit.Inputs[0].DigestSha256, Is.EqualTo(refs[0].DigestSha256));
        Assert.That(unit.Requirements.NativeModelHash, Is.EqualTo("model-hash-abc"));
        Assert.That(unit.Requirements.RequiredPacks,
            Does.Contain($"{CampaignPackCatalog.ContextFabricPackId}@{CampaignPackCatalog.ContextFabricPackVersion}"));
    }

    [Test]
    public async Task ExecuteContextFabricReader_AcceptsSegment_AndWritesEvidenceCardArtifact()
    {
        var corpus = SingleSegmentCorpus();
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle
        {
            TaskId = "task-1",
            CampaignId = "camp-1",
            WorkUnitId = "read-00001",
            PackId = CampaignPackCatalog.ContextFabricPackId,
            ExecutionKind = HiveExecutionKinds.NativeAgent,
        };

        var execution = await adapter.ExecuteContextFabricReaderAsync(bundle, corpus, CancellationToken.None);

        Assert.That(execution.Output, Is.Not.Empty);
        Assert.That(execution.Steps, Is.EqualTo(1));
        Assert.That(execution.TraceDigest, Is.EqualTo(FabricHashing.Sha256(execution.Output)));

        var cardPath = Path.Combine(execution.OutputDirectory, "evidence-card.json");
        Assert.That(File.Exists(cardPath), Is.True, "the evidence card must be written for output-artifact upload");

        var card = FabricJson.ParseModelObject<FabricEvidenceCard>(File.ReadAllText(cardPath));
        Assert.That(card.SegmentId, Is.EqualTo("seg-a"));
        Assert.That(card.Claims, Is.Not.Empty);
        Assert.That(card.CorpusId, Is.EqualTo(corpus.CorpusId));
    }

    private static FabricCorpus SingleSegmentCorpus()
    {
        var fixture = DeterministicFabricCorpus.Create();
        const string text = "EVIDENCE: The reactor core runs at nine hundred kelvin.\n" +
                            "EVIDENCE: The backup loop stays cold until commanded.";
        var segment = new FabricSegment("seg-a", 1, "Reactor", text, FabricHashing.Sha256(text), 24);
        return fixture.Corpus with { Segments = [segment], EstimatedSourceTokens = 24 };
    }

    private static FabricCorpus TwoSegmentCorpus()
    {
        var fixture = DeterministicFabricCorpus.Create();
        const string textA = "EVIDENCE: The reactor core runs at nine hundred kelvin.";
        const string textB = "EVIDENCE: The archive ledger is sealed in cabinet forty-two.";
        var a = new FabricSegment("seg-a", 1, "Reactor", textA, FabricHashing.Sha256(textA), 12);
        var b = new FabricSegment("seg-b", 2, "Archive", textB, FabricHashing.Sha256(textB), 14);
        return fixture.Corpus with { Segments = [a, b], EstimatedSourceTokens = 26 };
    }
}
