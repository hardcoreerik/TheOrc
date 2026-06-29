// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricCf4Tests
{
    [Test]
    public void MigrationV11_Creates_Hierarchy_Tables()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        using var connection = store.Open();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 11"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_memory_nodes'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_memory_memberships'"), Is.EqualTo(1));
        });
    }

    [Test]
    public void Reducer_Persists_Expected_And_Covered_Child_Counts_And_Memberships()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");
        harness.SeedClaims("seg-2", "Scouts favor the ridge route at dusk.");

        var result = new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 120))
            .ReduceDocument(harness.Document.DocumentId);

        var root = harness.Graph.GetMemoryNode(result.RootNodeId)!;
        var memberships = harness.Graph.ListMemoryMemberships(result.RootNodeId);

        Assert.Multiple(() =>
        {
            Assert.That(result.Nodes, Has.Count.GreaterThanOrEqualTo(3));
            Assert.That(root.ExpectedChildCount, Is.EqualTo(memberships.Count));
            Assert.That(root.CoveredChildCount, Is.EqualTo(memberships.Count(item => item.IsCovered)));
            Assert.That(memberships.Select(item => item.ChildKind), Has.All.EqualTo("memory"));
            Assert.That(memberships.Select(item => item.Ordinal), Is.EqualTo(new[] { 0, 1 }));
        });
    }

    [Test]
    public void Reducer_Leaves_Incomplete_Coverage_Visible_And_Not_Complete()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");

        var result = new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 120))
            .ReduceDocument(harness.Document.DocumentId);

        var root = harness.Graph.GetMemoryNode(result.RootNodeId)!;

        Assert.Multiple(() =>
        {
            Assert.That(root.ExpectedChildCount, Is.GreaterThan(root.CoveredChildCount));
            Assert.That(root.CoverageStatus, Is.EqualTo(FabricCoverageStatus.Incomplete));
            Assert.That(result.Nodes.Any(node => node.CoverageStatus == FabricCoverageStatus.Incomplete), Is.True);
        });
    }

    [Test]
    public void Reducer_FanIn_Stays_Bounded()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");
        harness.SeedClaims("seg-2", "Scouts favor the ridge route at dusk.");
        harness.SeedClaims("seg-3", "The harbor route is safer by day.");

        var result = new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 120))
            .ReduceDocument(harness.Document.DocumentId);

        Assert.That(result.Nodes, Has.All.Matches<FabricMemoryNodeEntry>(node => node.ExpectedChildCount <= 2));
    }

    [Test]
    public void EvidencePackBuilder_Respects_8k_Budget_And_Reserves_Response_Tokens()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");
        harness.SeedClaims("seg-2", "Scouts favor the ridge route at dusk.");
        harness.SeedClaims("seg-3", "The harbor route is safer by day.");

        new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 80))
            .ReduceDocument(harness.Document.DocumentId);

        var plan = new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan(
            "compare ridge route and harbor route",
            harness.Corpus.CorpusId,
            FabricQueryMode.Study,
            new FabricQueryPlannerOptions(MaxPromptTokens: 8_192, ResponseTokenReserve: 1_024, MaxSourceOpens: 3));

        var pack = new EvidencePackBuilder(harness.Library, harness.Graph).Build(plan);

        Assert.Multiple(() =>
        {
            Assert.That(pack.WithinBudget, Is.True);
            Assert.That(pack.UsedPromptTokens + pack.ResponseTokenReserve, Is.LessThanOrEqualTo(8_192));
            Assert.That(pack.Included.First().FromSource, Is.True);
            Assert.That(pack.Excluded.Count, Is.GreaterThanOrEqualTo(0));
        });
    }

    [Test]
    public void Quick_Mode_Works_For_Direct_Source_Backed_Questions()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");

        var plan = new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan(
            "LANTERN",
            harness.Corpus.CorpusId,
            null,
            new FabricQueryPlannerOptions());

        var pack = new EvidencePackBuilder(harness.Library, harness.Graph).Build(plan);

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(FabricQueryMode.Quick));
            Assert.That(plan.TriggeredSourceReopen, Is.False);
            Assert.That(pack.Included, Has.Some.Matches<FabricEvidenceItem>(item => item.Kind == "source" && item.Text.Contains("LANTERN", StringComparison.Ordinal)));
        });
    }

    [Test]
    public void Study_Mode_Triggers_Source_Reopen_When_Summaries_Are_Insufficient()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");

        new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 64))
            .ReduceDocument(harness.Document.DocumentId);

        var plan = new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan(
            "how do the ridge route and harbor route compare",
            harness.Corpus.CorpusId,
            FabricQueryMode.Study,
            new FabricQueryPlannerOptions(MaxSourceOpens: 4));

        Assert.Multiple(() =>
        {
            Assert.That(plan.Mode, Is.EqualTo(FabricQueryMode.Study));
            Assert.That(plan.TriggeredSourceReopen, Is.True);
            Assert.That(plan.ReopenedSegmentIds, Is.Not.Empty);
        });
    }

    [Test]
    public void CitationVerifier_Accepts_Exact_Source_Backed_Citations()
    {
        using var harness = NewHarness();
        var verifier = new FabricCitationVerifier(harness.Library);
        var segment = harness.Library.GetSegment("seg-0")!;
        var quote = "LANTERN is the assigned call sign.";
        var start = segment.Text.IndexOf(quote, StringComparison.Ordinal);

        var result = verifier.VerifyClaim(
            quote,
        [
            new FabricCitation
            {
                SegmentId = segment.SegmentId,
                CharStart = start,
                CharEnd = start + quote.Length,
                Quote = quote,
                QuoteDigest = FabricHashing.Sha256(quote)
            }
        ]);

        Assert.That(result.Label, Is.EqualTo(FabricCitationVerificationLabel.Supported));
    }

    [Test]
    public void CitationVerifier_Rejects_Citation_Mismatches()
    {
        using var harness = NewHarness();
        var verifier = new FabricCitationVerifier(harness.Library);

        var result = verifier.VerifyClaim(
            "LANTERN is the assigned call sign.",
        [
            new FabricCitation
            {
                SegmentId = "seg-0",
                CharStart = 0,
                CharEnd = 7,
                Quote = "LANTERN",
                QuoteDigest = "wrong-digest"
            }
        ]);

        Assert.That(result.Label, Is.EqualTo(FabricCitationVerificationLabel.CitationMismatch));
    }

    private static Harness NewHarness()
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var corpus = library.CreateCorpus("cf4-corpus", "CF-4 Lane");
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            "cf4-doc",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "CF-4 Notes",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);

        var segments = new[]
        {
            "LANTERN is the assigned call sign.",
            "Emergency frequency is 17.4 MHz.",
            "Scouts favor the ridge route at dusk.",
            "The harbor route is safer by day."
        };

        var start = 0;
        library.ReplaceDocument(document, segments.Select((text, index) =>
        {
            var draft = new FabricSegmentDraft(
                $"seg-{index}",
                index,
                $"Section {index}",
                start,
                start + text.Length,
                Math.Max(6, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                FabricHashing.Sha256(text),
                text,
                index > 0 ? $"seg-{index - 1}" : null,
                index < segments.Length - 1 ? $"seg-{index + 1}" : null,
                FabricIngestionVersions.Segmenter);
            start += text.Length + 1;
            return draft;
        }).ToArray());

        return new Harness(store, library, graph, new FabricSearchService(library, graph), corpus, document);
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(command.ExecuteScalar() ?? 0L);
    }

    private sealed class Harness(
        SqliteStore store,
        FabricLibraryRepository library,
        DocumentGraphRepository graph,
        FabricSearchService search,
        FabricCorpusEntry corpus,
        FabricDocumentEntry document) : IDisposable
    {
        public SqliteStore Store { get; } = store;
        public FabricLibraryRepository Library { get; } = library;
        public DocumentGraphRepository Graph { get; } = graph;
        public FabricSearchService Search { get; } = search;
        public FabricCorpusEntry Corpus { get; } = corpus;
        public FabricDocumentEntry Document { get; } = document;

        public void SeedClaims(string segmentId, string claimText)
        {
            var now = DateTimeOffset.UtcNow;
            Graph.UpsertClaim(
                new FabricClaimEntry(
                    $"claim-{segmentId}-{FabricHashing.Sha256(claimText)[..8]}",
                    Corpus.CorpusId,
                    Document.DocumentId,
                    segmentId,
                    "assertion",
                    claimText,
                    FabricVerificationStatus.Provisional,
                    0.9,
                    now,
                    now),
                []);
        }

        public void Dispose() => Store.Dispose();
    }
}
