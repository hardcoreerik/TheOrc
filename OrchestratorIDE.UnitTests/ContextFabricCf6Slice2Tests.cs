// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// CF-6 second slice: reducer work-unit builder + dispatch, corpus-meta staging, and
/// generation-safe evidence import via FabricHiveCampaignImporter.
/// </summary>
[TestFixture]
public sealed class ContextFabricCf6Slice2Tests
{
    private string _root = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cf6-slice2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Corpus-meta staging ──────────────────────────────────────────────────

    [Test]
    public async Task StageReducerCorpusMeta_StripsSegmentText_AndNamesArtifactCorrectly()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));

        var artRef = await CampaignTemplates.StageReducerCorpusMetaAsync(corpus, store);

        Assert.That(artRef.Name, Is.EqualTo("corpus-meta.json"));
        Assert.That(artRef.Kind, Is.EqualTo("input"));
        Assert.That(artRef.MediaType, Is.EqualTo("application/json"));
        Assert.That(store.Has(artRef.DigestSha256), Is.True);

        var bytes = File.ReadAllBytes(store.GetPath(artRef.DigestSha256));
        var rebuilt = JsonSerializer.Deserialize<FabricCorpus>(
            Encoding.UTF8.GetString(bytes), FabricJson.Options)!;

        Assert.That(rebuilt.CorpusId, Is.EqualTo(corpus.CorpusId));
        Assert.That(rebuilt.DocumentId, Is.EqualTo(corpus.DocumentId));
        Assert.That(rebuilt.GenerationId, Is.EqualTo(corpus.GenerationId));
        Assert.That(rebuilt.EstimatedSourceTokens, Is.EqualTo(0), "text stripped → token count zeroed");
        foreach (var segment in rebuilt.Segments)
        {
            Assert.That(segment.Text, Is.Empty, "segment text must be stripped in corpus-meta");
            Assert.That(segment.TextDigest, Is.Empty, "text digest must be stripped");
        }
    }

    [Test]
    public async Task StageReducerCorpusMeta_IsContentAddressed_DuplicateCallsCollapseToSameDigest()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));

        var first = await CampaignTemplates.StageReducerCorpusMetaAsync(corpus, store);
        var second = await CampaignTemplates.StageReducerCorpusMetaAsync(corpus, store);

        Assert.That(second.DigestSha256, Is.EqualTo(first.DigestSha256));
    }

    // ── Reducer work-unit builder ─────────────────────────────────────────────

    [Test]
    public void ContextFabricReducer_BuildsOneUnit_WithCorrectNativeRoleAndDependsOn()
    {
        var corpus = TwoSegmentCorpus();
        var store = new ContentAddressedStore(Path.Combine(_root, "store"));
        var meta = CampaignTemplates.StageReducerCorpusMetaAsync(corpus, store).GetAwaiter().GetResult();
        var cardRefs = new[]
        {
            ArtifactRef("reader-out-a.evidence-card.json", "digest-a"),
            ArtifactRef("reader-out-b.evidence-card.json", "digest-b"),
        };
        var readerIds = new[] { "read-00001", "read-00002" };

        var campaign = CampaignTemplates.ContextFabricReducer(
            "cf6-reduce", meta, cardRefs, readerIds, modelHash: "model-hash-xyz");

        Assert.That(campaign.PackId, Is.EqualTo(CampaignPackCatalog.ContextFabricPackId));
        Assert.That(campaign.WorkUnits, Has.Count.EqualTo(1));

        var unit = campaign.WorkUnits[0];
        Assert.That(unit.WorkUnitId, Is.EqualTo("reduce-00001"));
        Assert.That(unit.NativeRole, Is.EqualTo(CampaignPackCatalog.ContextFabricReducerRole));
        Assert.That(unit.DependsOn, Is.EquivalentTo(readerIds));
        Assert.That(unit.Inputs, Has.Count.EqualTo(3), "corpus-meta + 2 evidence-card refs");
        Assert.That(unit.Inputs[0].Name, Is.EqualTo("corpus-meta.json"));
        Assert.That(unit.Requirements.NativeModelHash, Is.EqualTo("model-hash-xyz"));
        Assert.That(unit.Requirements.RequiredPacks,
            Does.Contain($"{CampaignPackCatalog.ContextFabricPackId}@{CampaignPackCatalog.ContextFabricPackVersion}"));
    }

    // ── Reducer execution dispatch ────────────────────────────────────────────

    [Test]
    public async Task ExecuteContextFabricReducer_WritesReductionNodesJson_WithNonZeroSteps()
    {
        var corpus = TwoSegmentCorpus();
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle
        {
            TaskId = "task-reduce-1",
            CampaignId = "camp-reduce",
            WorkUnitId = "reduce-00001",
            PackId = CampaignPackCatalog.ContextFabricPackId,
            ExecutionKind = HiveExecutionKinds.NativeAgent,
        };
        var cards = TwoEvidenceCards(corpus);

        var execution = await adapter.ExecuteContextFabricReducerAsync(bundle, corpus, cards, CancellationToken.None);

        Assert.That(execution.Output, Is.Not.Empty);
        Assert.That(execution.Steps, Is.GreaterThan(0));
        Assert.That(execution.TraceDigest, Is.EqualTo(FabricHashing.Sha256(execution.Output)));

        var nodesPath = Path.Combine(execution.OutputDirectory, "reduction-nodes.json");
        Assert.That(File.Exists(nodesPath), Is.True, "reduction-nodes.json must be written for artifact upload");

        var json = File.ReadAllText(nodesPath);
        using var doc = JsonDocument.Parse(json);
        Assert.That(doc.RootElement.GetProperty("nodeCount").GetInt32(), Is.GreaterThan(0));
        Assert.That(doc.RootElement.GetProperty("corpusId").GetString(), Is.EqualTo(corpus.CorpusId));
    }

    [Test]
    public async Task ExecuteContextFabricReducer_ThrowsInvalidOperation_WhenNoCardsProvided()
    {
        var corpus = TwoSegmentCorpus();
        await using var adapter = new HiveNativeRoleExecutorAdapter(new ScriptedFabricRuntime(), _root);
        var bundle = new HiveTaskBundle { TaskId = "t", CampaignId = "c", WorkUnitId = "r", PackId = CampaignPackCatalog.ContextFabricPackId };

        Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await adapter.ExecuteContextFabricReducerAsync(bundle, corpus, [], CancellationToken.None));
    }

    // ── FabricHiveCampaignImporter ────────────────────────────────────────────

    [Test]
    public void FabricHiveCampaignImporter_ImportCards_ImportsExpectedClaimCount()
    {
        using var db = BuildInMemoryDb(out var library, out var graph);
        var importer = new FabricHiveCampaignImporter(new FabricEvidenceGraphImporter(library, graph));
        var (_, cards) = BuildTestFixture(library);

        var summary = importer.ImportCards(cards, expectedGenerationId: null);

        Assert.That(summary.CardsImported, Is.EqualTo(cards.Count));
        Assert.That(summary.CardsSkipped, Is.EqualTo(0));
        Assert.That(summary.ClaimsImported, Is.GreaterThan(0));
    }

    [Test]
    public void FabricHiveCampaignImporter_ImportCards_IsIdempotent_ClaimsNotDuplicated()
    {
        using var db = BuildInMemoryDb(out var library, out var graph);
        var importer = new FabricHiveCampaignImporter(new FabricEvidenceGraphImporter(library, graph));
        var (corpusEntry, cards) = BuildTestFixture(library);

        importer.ImportCards(cards, expectedGenerationId: null);
        importer.ImportCards(cards, expectedGenerationId: null);

        var claims = graph.ListClaims(corpusEntry.CorpusId, limit: 100);
        Assert.That(claims.Count, Is.EqualTo(cards.Sum(card => card.Claims.Count)),
            "re-importing identical cards must not duplicate claims");
    }

    [Test]
    public void FabricHiveCampaignImporter_SkipsCard_WhenSegmentNotInLibrary()
    {
        using var db = BuildInMemoryDb(out var library, out var graph);
        var importer = new FabricHiveCampaignImporter(new FabricEvidenceGraphImporter(library, graph));
        var (corpusEntry, cards) = BuildTestFixture(library);

        var unknownCard = cards[0] with { SegmentId = "seg-does-not-exist" };
        var mixed = new[] { cards[0], unknownCard };

        var summary = importer.ImportCards(mixed, expectedGenerationId: null);

        Assert.That(summary.CardsSkipped, Is.EqualTo(1));
        Assert.That(summary.CardsImported, Is.EqualTo(1));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static FabricCorpus TwoSegmentCorpus()
    {
        var fixture = DeterministicFabricCorpus.Create();
        const string textA = "EVIDENCE: The station core runs at nine hundred kelvin.";
        const string textB = "EVIDENCE: The archive ledger is sealed in cabinet forty-two.";
        var a = new FabricSegment("seg-reduce-a", 1, "Core", textA, FabricHashing.Sha256(textA), 12);
        var b = new FabricSegment("seg-reduce-b", 2, "Archive", textB, FabricHashing.Sha256(textB), 14);
        return fixture.Corpus with { Segments = [a, b], EstimatedSourceTokens = 26 };
    }

    private static IReadOnlyList<FabricEvidenceCard> TwoEvidenceCards(FabricCorpus corpus)
    {
        return corpus.Segments.Select(segment => new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = corpus.DocumentId,
            SegmentId = segment.SegmentId,
            Summary = $"Summary for {segment.SegmentId}",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = $"{segment.SegmentId}-c1",
                    Text = segment.Text.Replace("EVIDENCE: ", ""),
                    Confidence = 1,
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = segment.SegmentId,
                            CharStart = -1,
                            CharEnd = -1,
                            Quote = segment.Text.Replace("EVIDENCE: ", ""),
                        },
                    ],
                },
            ],
        }).ToList();
    }

    private static ArtifactRef ArtifactRef(string name, string digest) => new()
    {
        Name = name,
        DigestSha256 = digest,
        SizeBytes = 1,
        MediaType = "application/json",
        Kind = "input",
    };

    private static SqliteStore BuildInMemoryDb(
        out FabricLibraryRepository library,
        out DocumentGraphRepository graph)
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        library = new FabricLibraryRepository(store);
        graph = new DocumentGraphRepository(store);
        return store;
    }

    private static (FabricCorpusEntry Corpus, List<FabricEvidenceCard> Cards) BuildTestFixture(
        FabricLibraryRepository library)
    {
        var now = DateTimeOffset.UtcNow;
        var corpus = library.CreateCorpus("cf6-importer-corpus", "CF-6 importer test");

        const string textA = "The station core runs at nine hundred kelvin.";
        const string textB = "The archive ledger is sealed in cabinet forty-two.";
        var document = new FabricDocumentEntry(
            "doc-cf6-imp",
            corpus.CorpusId,
            "src-digest",
            "norm-digest",
            "Test Doc",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft("seg-imp-a", 1, "Core", 0, textA.Length, 10,
                FabricHashing.Sha256(textA), textA, null, "seg-imp-b", FabricIngestionVersions.Segmenter),
            new FabricSegmentDraft("seg-imp-b", 2, "Archive", textA.Length + 1, textA.Length + 1 + textB.Length,
                12, FabricHashing.Sha256(textB), textB, "seg-imp-a", null, FabricIngestionVersions.Segmenter),
        ]);

        var cards = new List<FabricEvidenceCard>
        {
            new()
            {
                CorpusId = corpus.CorpusId,
                DocumentId = document.DocumentId,
                SegmentId = "seg-imp-a",
                Summary = "Core temperature claim.",
                Claims =
                [
                    new FabricClaim
                    {
                        ClaimId = "seg-imp-a-c1",
                        Text = "The station core runs at nine hundred kelvin.",
                        Confidence = 1,
                        Citations =
                        [
                            new FabricCitation
                            {
                                SegmentId = "seg-imp-a",
                                CharStart = 0,
                                CharEnd = textA.Length,
                                Quote = textA,
                            },
                        ],
                    },
                ],
            },
            new()
            {
                CorpusId = corpus.CorpusId,
                DocumentId = document.DocumentId,
                SegmentId = "seg-imp-b",
                Summary = "Archive claim.",
                Claims =
                [
                    new FabricClaim
                    {
                        ClaimId = "seg-imp-b-c1",
                        Text = "The archive ledger is sealed in cabinet forty-two.",
                        Confidence = 1,
                        Citations =
                        [
                            new FabricCitation
                            {
                                SegmentId = "seg-imp-b",
                                CharStart = 0,
                                CharEnd = textB.Length,
                                Quote = textB,
                            },
                        ],
                    },
                ],
            },
        };

        return (corpus, cards);
    }
}
