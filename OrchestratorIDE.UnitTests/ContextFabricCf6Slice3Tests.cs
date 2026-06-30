// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// CF-6 third slice: stitcher, verifier, and exhaustive-query work-unit builders and execution dispatch.
/// </summary>
[TestFixture]
public sealed class ContextFabricCf6Slice3Tests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cf6-slice3-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Stitcher work-unit builder ────────────────────────────────────────────

    [Test]
    public void ContextFabricStitchers_BuildsOneUnitPerPair_WithDependsOnAndNativeRole()
    {
        var leftRef = CorpusRef("seg-a.corpus.json", "digest-a");
        var rightRef = CorpusRef("seg-b.corpus.json", "digest-b");
        var pairs = new[] { (leftRef, rightRef, "read-00001", "read-00002") };

        var campaign = CampaignTemplates.ContextFabricStitchers("cf6-stitch", pairs, modelHash: "model-hash");

        Assert.That(campaign.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(campaign.WorkUnits, Has.Count.EqualTo(1));

        var unit = campaign.WorkUnits[0];
        Assert.That(unit.WorkUnitId, Is.EqualTo("stitch-00001"));
        Assert.That(unit.NativeRole, Is.EqualTo(CampaignPackCatalog.ContextFabricStitcherRole));
        Assert.That(unit.DependsOn, Is.EquivalentTo(new[] { "read-00001", "read-00002" }));
        Assert.That(unit.Inputs, Has.Count.EqualTo(2));
        Assert.That(unit.Inputs[0].Name, Is.EqualTo("seg-a.corpus.json"));
        Assert.That(unit.Inputs[1].Name, Is.EqualTo("seg-b.corpus.json"));
    }

    [Test]
    public async Task ExecuteContextFabricStitcher_WritesStitchResultJson_WithSummary()
    {
        var (left, right) = TwoAdjacentCorpora();
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle
        {
            TaskId = "task-stitch-1",
            CampaignId = "camp-stitch",
            WorkUnitId = "stitch-00001",
            PackId = CampaignPackCatalog.ContextFabricPackId,
        };

        var execution = await adapter.ExecuteContextFabricStitcherAsync(bundle, left, right, CancellationToken.None);

        Assert.That(execution.Output, Is.Not.Empty);
        Assert.That(execution.Steps, Is.EqualTo(1));

        var resultPath = Path.Combine(execution.OutputDirectory, "stitch-result.json");
        Assert.That(File.Exists(resultPath), Is.True);

        var json = File.ReadAllText(resultPath);
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("leftSegmentId").GetString(), Is.EqualTo("seg-stitch-a"));
        Assert.That(doc.RootElement.GetProperty("rightSegmentId").GetString(), Is.EqualTo("seg-stitch-b"));
        Assert.That(doc.RootElement.GetProperty("summary").GetString(), Is.Not.Empty);
    }

    [Test]
    public async Task ExecuteContextFabricStitcher_ThrowsOnMultiSegmentCorpus()
    {
        var fixture = DeterministicFabricCorpus.Create();
        var multiSeg = fixture.Corpus;
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "t", CampaignId = "c", WorkUnitId = "s", PackId = CampaignPackCatalog.ContextFabricPackId };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await adapter.ExecuteContextFabricStitcherAsync(bundle, multiSeg, multiSeg, CancellationToken.None));
    }

    // ── Verifier work-unit builder ────────────────────────────────────────────

    [Test]
    public void ContextFabricVerifiers_BuildsOneUnitPerCard_WithDependsOnAndNativeRole()
    {
        var cardRef = EvidenceCardRef("seg-a.evidence-card.json", "digest-card-a");
        var corpusRef = CorpusRef("seg-a.corpus.json", "digest-a");
        var items = new[] { (cardRef, corpusRef, "read-00001") };

        var campaign = CampaignTemplates.ContextFabricVerifiers("cf6-verify", items, modelHash: "model-hash");

        Assert.That(campaign.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(campaign.WorkUnits, Has.Count.EqualTo(1));

        var unit = campaign.WorkUnits[0];
        Assert.That(unit.WorkUnitId, Is.EqualTo("verify-00001"));
        Assert.That(unit.NativeRole, Is.EqualTo(CampaignPackCatalog.ContextFabricVerifierRole));
        Assert.That(unit.DependsOn, Is.EquivalentTo(new[] { "read-00001" }));
        Assert.That(unit.Inputs, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task ExecuteContextFabricVerifier_PassesWhenQuoteFoundInSource()
    {
        const string text = "The reactor core runs at nine hundred kelvin.";
        var corpus = SingleSegmentCorpusFromText("seg-v", text);
        var card = EvidenceCardWithQuote(corpus, "seg-v", text, charStart: 0);

        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "tv", CampaignId = "c", WorkUnitId = "verify-00001", PackId = CampaignPackCatalog.ContextFabricPackId };

        var execution = await adapter.ExecuteContextFabricVerifierAsync(bundle, card, corpus, CancellationToken.None);

        Assert.That(execution.Output, Is.Not.Empty);
        Assert.That(execution.Steps, Is.EqualTo(1), "one claim verified");

        var resultPath = Path.Combine(execution.OutputDirectory, "verification-result.json");
        Assert.That(File.Exists(resultPath), Is.True);

        var report = JsonSerializer.Deserialize<FabricHiveVerificationReport>(
            File.ReadAllText(resultPath), FabricJson.Options)!;
        Assert.That(report.AllPassed, Is.True);
        Assert.That(report.Items, Has.Count.EqualTo(1));
        Assert.That(report.Items[0].Passed, Is.True);
    }

    [Test]
    public async Task ExecuteContextFabricVerifier_FailsWhenQuoteNotInSource()
    {
        const string text = "The reactor core runs at nine hundred kelvin.";
        var corpus = SingleSegmentCorpusFromText("seg-vf", text);
        var card = EvidenceCardWithQuote(corpus, "seg-vf", "Completely different text that is not in source.", charStart: 0);

        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "tvf", CampaignId = "c", WorkUnitId = "verify-00002", PackId = CampaignPackCatalog.ContextFabricPackId };

        var execution = await adapter.ExecuteContextFabricVerifierAsync(bundle, card, corpus, CancellationToken.None);

        var report = JsonSerializer.Deserialize<FabricHiveVerificationReport>(
            File.ReadAllText(Path.Combine(execution.OutputDirectory, "verification-result.json")), FabricJson.Options)!;
        Assert.That(report.AllPassed, Is.False);
        Assert.That(report.Items[0].Errors, Is.Not.Empty);
    }

    // ── Exhaustive-query work-unit builder ────────────────────────────────────

    [Test]
    public async Task ContextFabricExhaustiveQuery_BuildsOneQueryUnitPerSegment()
    {
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));
        var segRefs = new[]
        {
            CorpusRef("seg-a.corpus.json", "digest-a"),
            CorpusRef("seg-b.corpus.json", "digest-b"),
        };

        var (_, campaign) = await CampaignTemplates.ContextFabricExhaustiveQueryAsync(
            "cf6-query", "q-001", "What is the core temperature?", segRefs, store, modelHash: "model-hash");

        Assert.That(campaign.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(campaign.WorkUnits, Has.Count.EqualTo(2));
        foreach (var unit in campaign.WorkUnits)
        {
            Assert.That(unit.NativeRole, Is.EqualTo(CampaignPackCatalog.ContextFabricQueryRole));
            Assert.That(unit.DependsOn, Is.Empty, "query units are independent");
            Assert.That(unit.Inputs, Has.Count.EqualTo(2), "question + corpus");
            Assert.That(unit.Inputs.Any(i => i.Name.StartsWith("question-")), Is.True);
        }
    }

    [Test]
    public async Task ContextFabricExhaustiveQuery_StagesQuestionArtifact_ContentAddressed()
    {
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));
        var segRef = CorpusRef("seg-a.corpus.json", "digest-a");

        var (questionRefs, _) = await CampaignTemplates.ContextFabricExhaustiveQueryAsync(
            "cf6-query", "q-001", "What is the core temperature?", [segRef], store, modelHash: "model-hash");

        Assert.That(questionRefs, Has.Count.EqualTo(1));
        Assert.That(store.Has(questionRefs[0].DigestSha256), Is.True);
    }

    [Test]
    public async Task ExecuteContextFabricQuery_ReturnsRelevant_WhenEvidencePresent()
    {
        const string text = "EVIDENCE: The core runs at nine hundred kelvin.";
        var corpus = SingleSegmentCorpusFromText("seg-qa", text);
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "tq", CampaignId = "c", WorkUnitId = "query-00001", PackId = CampaignPackCatalog.ContextFabricPackId };

        var execution = await adapter.ExecuteContextFabricQueryAsync(
            bundle, "q-temp", "What is the core temperature?", corpus, CancellationToken.None);

        Assert.That(execution.Output, Is.Not.Empty);
        Assert.That(execution.Steps, Is.EqualTo(1), "relevant = true → steps = 1");

        var resultPath = Path.Combine(execution.OutputDirectory, "query-finding.json");
        Assert.That(File.Exists(resultPath), Is.True);

        using var doc = JsonDocument.Parse(File.ReadAllText(resultPath));
        Assert.That(doc.RootElement.GetProperty("relevant").GetBoolean(), Is.True);
    }

    [Test]
    public async Task ExecuteContextFabricQuery_ReturnsNotRelevant_WhenNoEvidence()
    {
        const string text = "This segment contains no EVIDENCE lines, just prose text.";
        var corpus = SingleSegmentCorpusFromText("seg-qb", text);
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "tq2", CampaignId = "c", WorkUnitId = "query-00002", PackId = CampaignPackCatalog.ContextFabricPackId };

        var execution = await adapter.ExecuteContextFabricQueryAsync(
            bundle, "q-temp", "What is the core temperature?", corpus, CancellationToken.None);

        Assert.That(execution.Steps, Is.EqualTo(0), "relevant = false → steps = 0");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (FabricCorpus Left, FabricCorpus Right) TwoAdjacentCorpora()
    {
        var fixture = DeterministicFabricCorpus.Create();
        const string textA = "The archive crew sealed the blue ledger. They documented the transfer.";
        const string textB = "Before leaving the records office, the crew signed the register.";
        var a = new FabricSegment("seg-stitch-a", 1, "Archive", textA, FabricHashing.Sha256(textA), 16);
        var b = new FabricSegment("seg-stitch-b", 2, "Records", textB, FabricHashing.Sha256(textB), 14);
        var left = fixture.Corpus with { Segments = [a], EstimatedSourceTokens = 16 };
        var right = fixture.Corpus with { Segments = [b], EstimatedSourceTokens = 14 };
        return (left, right);
    }

    private static FabricCorpus SingleSegmentCorpusFromText(string segmentId, string text)
    {
        var fixture = DeterministicFabricCorpus.Create();
        var seg = new FabricSegment(segmentId, 1, "Section", text, FabricHashing.Sha256(text), 12);
        return fixture.Corpus with { Segments = [seg], EstimatedSourceTokens = 12 };
    }

    private static FabricEvidenceCard EvidenceCardWithQuote(
        FabricCorpus corpus, string segmentId, string quote, int charStart)
    {
        return new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = corpus.DocumentId,
            SegmentId = segmentId,
            Summary = "Test claim.",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "c1",
                    Text = "Test claim text.",
                    Confidence = 1,
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = segmentId,
                            CharStart = charStart,
                            CharEnd = charStart + quote.Length,
                            Quote = quote,
                        },
                    ],
                },
            ],
        };
    }

    private static ArtifactRef CorpusRef(string name, string digest) => new()
    {
        Name = name, DigestSha256 = digest, SizeBytes = 1, MediaType = "application/json", Kind = "input",
    };

    private static ArtifactRef EvidenceCardRef(string name, string digest) => new()
    {
        Name = name, DigestSha256 = digest, SizeBytes = 1, MediaType = "application/json", Kind = "input",
    };
}
