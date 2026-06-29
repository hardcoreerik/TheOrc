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
    public void Reducer_Uses_All_Claims_Up_To_Contract_Cap()
    {
        using var harness = NewHarness(["Base segment text."]);
        var manyClaims = Enumerable.Range(0, 1_501)
            .Select(index => $"Claim {index:0000} survives the reducer cap.")
            .ToArray();
        foreach (var claim in manyClaims)
            harness.SeedClaims("seg-0", claim);

        var result = new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 200_000))
            .ReduceDocument(harness.Document.DocumentId);

        Assert.That(result.Nodes.Single(node => node.NodeId == result.RootNodeId).SummaryText, Does.Contain("Claim 1500"));
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
    public void Reducer_Root_Span_Covers_Full_Child_Range()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");
        harness.SeedClaims("seg-1", "Emergency frequency is 17.4 MHz.");
        harness.SeedClaims("seg-2", "Scouts favor the ridge route at dusk.");
        harness.SeedClaims("seg-3", "The harbor route is safer by day.");

        var result = new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 120))
            .ReduceDocument(harness.Document.DocumentId);

        Assert.That(harness.Graph.GetMemoryNode(result.RootNodeId)!.Title, Does.Contain("0-3"));
    }

    [Test]
    public void EvidencePackBuilder_Respects_8k_Budget_And_Reserves_Response_Tokens()
    {
        var minifiedJson = string.Concat(Enumerable.Repeat("{\"route\":\"ridge\",\"status\":\"safe\",\"notes\":[1,2,3,4,5]}", 80));
        using var harness = NewHarness([minifiedJson, minifiedJson, minifiedJson, minifiedJson]);
        harness.SeedClaims("seg-0", "ridge route safe");

        new FabricReducer(harness.Library, harness.Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 80))
            .ReduceDocument(harness.Document.DocumentId);

        var plan = new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan(
            "compare ridge route and harbor route",
            harness.Corpus.CorpusId,
            FabricQueryMode.Study,
            new FabricQueryPlannerOptions(MaxPromptTokens: 1_800, ResponseTokenReserve: 256, MaxSourceOpens: 4));

        var pack = new EvidencePackBuilder(harness.Library, harness.Graph).Build(plan);

        Assert.Multiple(() =>
        {
            Assert.That(pack.WithinBudget, Is.True);
            Assert.That(pack.UsedPromptTokens + pack.ResponseTokenReserve, Is.LessThanOrEqualTo(1_800));
            Assert.That(plan.TriggeredSourceReopen, Is.True);
            Assert.That(pack.Included.First().FromSource, Is.True);
            Assert.That(pack.Excluded, Is.Not.Empty);
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
    public void QueryPlanner_Rejects_Unknown_Explicit_Mode()
    {
        using var harness = NewHarness();

        Assert.That(
            () => new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan("LANTERN", harness.Corpus.CorpusId, "exhaustive-ish"),
            Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [Test]
    public void QueryPlanner_Trims_Query_And_CorpusId_Before_Search()
    {
        using var harness = NewHarness();
        harness.SeedClaims("seg-0", "LANTERN is the assigned call sign.");

        var plan = new FabricQueryPlanner(harness.Search, harness.Graph).BuildPlan(
            "   LANTERN   ",
            $"   {harness.Corpus.CorpusId}   ");

        Assert.Multiple(() =>
        {
            Assert.That(plan.Query, Is.EqualTo("LANTERN"));
            Assert.That(plan.CorpusId, Is.EqualTo(harness.Corpus.CorpusId));
            Assert.That(plan.SeedHits, Is.Not.Empty);
        });
    }

    [Test]
    public void QueryPlanner_Classifies_Study_Keywords_By_Token()
    {
        using var harness = NewHarness();
        var planner = new FabricQueryPlanner(harness.Search, harness.Graph);

        Assert.Multiple(() =>
        {
            Assert.That(planner.BuildPlan("show me LANTERN", harness.Corpus.CorpusId).Mode, Is.EqualTo(FabricQueryMode.Quick));
            Assert.That(planner.BuildPlan("exchange frequency", harness.Corpus.CorpusId).Mode, Is.EqualTo(FabricQueryMode.Quick));
            Assert.That(planner.BuildPlan("how does X compare", harness.Corpus.CorpusId).Mode, Is.EqualTo(FabricQueryMode.Study));
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

    [Test]
    public void CitationVerifier_Rejects_Symmetric_Negation_Mismatches_As_Contradicted()
    {
        using var harness = NewHarness(["The route is not safe in high wind."]);
        var verifier = new FabricCitationVerifier(harness.Library);
        var quote = "The route is not safe in high wind.";

        var result = verifier.VerifyClaim(
            "The route is safe in high wind.",
            [
                new FabricCitation
                {
                    SegmentId = "seg-0",
                    CharStart = 0,
                    CharEnd = quote.Length,
                    Quote = quote,
                    QuoteDigest = FabricHashing.Sha256(quote)
                }
            ]);

        Assert.That(result.Label, Is.EqualTo(FabricCitationVerificationLabel.Contradicted));
    }

    [Test]
    public void ReplaceMemoryNodesForDocument_Rejects_Foreign_Segment_Membership()
    {
        using var harness = NewHarness();
        harness.AddDocument("cf4-doc-b", ["Foreign segment."], "cf4b-seg");

        var parent = NewNode(harness, "node-parent", 0, "Parent", 1, 1, FabricCoverageStatus.Complete);

        Assert.That(
            () => harness.Graph.ReplaceMemoryNodesForDocument(
                harness.Document.DocumentId,
                [parent],
                new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal)
                {
                    [parent.NodeId] =
                    [
                        new FabricMemoryMembershipEntry(parent.NodeId, "segment", "cf4b-seg-0", 0, true)
                    ]
                }),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ReplaceMemoryNodesForDocument_Rejects_Missing_Memory_Child()
    {
        using var harness = NewHarness();
        var parent = NewNode(harness, "node-parent", 0, "Parent", 1, 1, FabricCoverageStatus.Complete);

        Assert.That(
            () => harness.Graph.ReplaceMemoryNodesForDocument(
                harness.Document.DocumentId,
                [parent],
                new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal)
                {
                    [parent.NodeId] =
                    [
                        new FabricMemoryMembershipEntry(parent.NodeId, "memory", "missing-child", 0, true)
                    ]
                }),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ReplaceMemoryNodesForDocument_Rejects_Mismatched_Parent()
    {
        using var harness = NewHarness();
        var parent = NewNode(harness, "node-parent", 0, "Parent", 1, 1, FabricCoverageStatus.Complete);

        Assert.That(
            () => harness.Graph.ReplaceMemoryNodesForDocument(
                harness.Document.DocumentId,
                [parent],
                new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal)
                {
                    [parent.NodeId] =
                    [
                        new FabricMemoryMembershipEntry("wrong-parent", "segment", "seg-0", 0, true)
                    ]
                }),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void ReplaceMemoryNodesForDocument_Does_Not_Partially_Replace_On_Invalid_Membership()
    {
        using var harness = NewHarness();
        var existingParent = NewNode(harness, "existing-parent", 0, "Existing", 1, 1, FabricCoverageStatus.Complete);
        harness.Graph.ReplaceMemoryNodesForDocument(
            harness.Document.DocumentId,
            [existingParent],
            new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal)
            {
                [existingParent.NodeId] =
                [
                    new FabricMemoryMembershipEntry(existingParent.NodeId, "segment", "seg-0", 0, true)
                ]
            });

        var replacementParent = NewNode(harness, "replacement-parent", 0, "Replacement", 1, 1, FabricCoverageStatus.Complete);

        Assert.That(
            () => harness.Graph.ReplaceMemoryNodesForDocument(
                harness.Document.DocumentId,
                [replacementParent],
                new Dictionary<string, IReadOnlyList<FabricMemoryMembershipEntry>>(StringComparer.Ordinal)
                {
                    [replacementParent.NodeId] =
                    [
                        new FabricMemoryMembershipEntry(replacementParent.NodeId, "memory", "missing-child", 0, true)
                    ]
                }),
            Throws.TypeOf<InvalidDataException>());

        Assert.Multiple(() =>
        {
            Assert.That(harness.Graph.GetMemoryNode(existingParent.NodeId), Is.Not.Null);
            Assert.That(harness.Graph.GetMemoryNode(replacementParent.NodeId), Is.Null);
        });
    }

    private static Harness NewHarness(IEnumerable<string>? segments = null, string corpusId = "cf4-corpus", string documentId = "cf4-doc")
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var corpus = library.CreateCorpus(corpusId, "CF-4 Lane");
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            documentId,
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

        var texts = (segments ?? [
            "LANTERN is the assigned call sign.",
            "Emergency frequency is 17.4 MHz.",
            "Scouts favor the ridge route at dusk.",
            "The harbor route is safer by day."
        ]).ToArray();

        var start = 0;
        library.ReplaceDocument(document, texts.Select((text, index) =>
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
                index < texts.Length - 1 ? $"seg-{index + 1}" : null,
                FabricIngestionVersions.Segmenter);
            start += text.Length + 1;
            return draft;
        }).ToArray());

        return new Harness(store, library, graph, new FabricSearchService(library, graph), corpus, document);
    }

    private static FabricMemoryNodeEntry NewNode(
        Harness harness,
        string nodeId,
        int generation,
        string summary,
        int expected,
        int covered,
        string coverageStatus)
    {
        var now = DateTimeOffset.UtcNow;
        return new FabricMemoryNodeEntry(
            nodeId,
            harness.Corpus.CorpusId,
            harness.Document.DocumentId,
            "summary",
            summary,
            summary,
            generation,
            2,
            expected,
            covered,
            coverageStatus,
            "test",
            now,
            now);
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

        public void AddDocument(string documentId, IReadOnlyList<string> segments, string segmentPrefix)
        {
            var now = DateTimeOffset.UtcNow;
            var document = new FabricDocumentEntry(
                documentId,
                Corpus.CorpusId,
                $"{documentId}-source-digest",
                $"{documentId}-normalized-digest",
                documentId,
                "text/plain",
                FabricIngestionVersions.TextMarkdownParser,
                FabricIngestionVersions.TextMarkdownParser,
                "ready",
                [],
                now,
                now);

            var start = 0;
            Library.ReplaceDocument(document, segments.Select((text, index) =>
            {
                var draft = new FabricSegmentDraft(
                    $"{segmentPrefix}-{index}",
                    index,
                    $"{documentId}-{index}",
                    start,
                    start + text.Length,
                    Math.Max(6, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                    FabricHashing.Sha256(text),
                    text,
                    index > 0 ? $"{segmentPrefix}-{index - 1}" : null,
                    index < segments.Count - 1 ? $"{segmentPrefix}-{index + 1}" : null,
                    FabricIngestionVersions.Segmenter);
                start += text.Length + 1;
                return draft;
            }).ToArray());
        }

        public void Dispose() => Store.Dispose();
    }
}
