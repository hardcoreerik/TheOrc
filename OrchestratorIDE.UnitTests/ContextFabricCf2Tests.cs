// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.CodeGraph;
using OrchestratorIDE.Services.CodeGraph.Data;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricCf2Tests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort for pooled SQLite and antivirus handles on Windows.
            }
        }
        _tempRoots.Clear();
    }

    [Test]
    public void MigrationV10_Creates_DocumentGraph_Tables_And_ClaimFts()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        using var connection = store.Open();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 10"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_claims'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_claim_fts'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_relations'"), Is.EqualTo(1));
        });
    }

    [Test]
    public void MigrationV16_Creates_ContextFabric_Graph_Links_Table()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        using var connection = store.Open();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 16"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_graph_links'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'ix_fabric_graph_links_corpus'"), Is.EqualTo(1));
        });
    }

    [Test]
    public void DocumentGraphRepository_Stores_Searches_And_Links_Provisional_Graph_Data()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var now = DateTimeOffset.UtcNow;
        const string segmentText = "Natural selection preserves favorable variations.";

        var corpus = library.CreateCorpus("corpus-test", "Independent Mind");
        var document = new FabricDocumentEntry(
            "doc-test",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Darwin",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                "seg-test",
                0,
                "Alpha",
                0,
                segmentText.Length,
                9,
                "seg-digest",
                segmentText,
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);

        var claim = new FabricClaimEntry(
            "claim-1",
            corpus.CorpusId,
            document.DocumentId,
            "seg-test",
            "assertion",
            "Natural selection preserves favorable variations.",
            FabricVerificationStatus.Provisional,
            0.75,
            now,
            now);
        graph.UpsertClaim(claim,
        [
            new FabricClaimCitationEntry(
                claim.ClaimId,
                0,
                "seg-test",
                0,
                segmentText.Length,
                "quote-digest",
                segmentText)
        ]);

        var source = new FabricEntityEntry("entity-source", corpus.CorpusId, "natural selection", "concept", FabricVerificationStatus.Provisional, 0.7, now, now);
        var target = new FabricEntityEntry("entity-target", corpus.CorpusId, "variation", "concept", FabricVerificationStatus.Provisional, 0.7, now, now);
        graph.UpsertEntity(source);
        graph.UpsertEntity(target);
        graph.UpsertRelation(new FabricRelationEntry(
            "relation-1",
            corpus.CorpusId,
            source.EntityId,
            target.EntityId,
            "SUPPORTS",
            FabricVerificationStatus.Provisional,
            0.6,
            1,
            now,
            now));

        var claims = graph.SearchClaims("favorable variations", corpus.CorpusId, 10);
        var citations = graph.ListClaimCitations(claim.ClaimId);
        var entities = graph.ListEntities(corpus.CorpusId, 10);
        var relations = graph.ListRelations(corpus.CorpusId, source.EntityId, 10);

        Assert.Multiple(() =>
        {
            Assert.That(claims, Has.Count.EqualTo(1));
            Assert.That(claims[0].ClaimId, Is.EqualTo(claim.ClaimId));
            Assert.That(claims[0].VerificationStatus, Is.EqualTo(FabricVerificationStatus.Provisional));
            Assert.That(citations, Has.Count.EqualTo(1));
            Assert.That(citations[0].QuoteText, Does.Contain("favorable variations"));
            Assert.That(entities.Select(item => item.EntityId), Does.Contain(source.EntityId));
            Assert.That(relations, Has.Count.EqualTo(1));
            Assert.That(relations[0].RelationType, Is.EqualTo("SUPPORTS"));
            Assert.That(relations[0].VerificationStatus, Is.EqualTo(FabricVerificationStatus.Provisional));
        });
    }

    [Test]
    public void DocumentGraphRepository_Persists_CrossCorpus_And_CodeGraph_Links()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var codeGraph = new GraphRepository(store);
        var now = DateTimeOffset.UtcNow;

        var first = SeedClaim(library, graph, "corpus-link-a", "doc-link-a", "seg-link-a", "claim-link-a", "Alpha source anchors a claim.", now);
        var second = SeedClaim(library, graph, "corpus-link-b", "doc-link-b", "seg-link-b", "claim-link-b", "Beta source confirms a separate claim.", now);
        var nodeId = codeGraph.UpsertNode(new CodeNode(
            null,
            "TheOrc",
            "Method",
            "SearchWithVector",
            "OrchestratorIDE.Services.ContextFabric.FabricLibraryService.SearchWithVector",
            "OrchestratorIDE/Services/ContextFabric/FabricLibraryService.cs",
            47,
            77,
            null,
            null,
            null,
            null,
            null));

        graph.UpsertGraphLink(new FabricGraphLinkEntry(
            "link-cross-corpus",
            "claim",
            first.Claim.ClaimId,
            first.Corpus.CorpusId,
            "claim",
            second.Claim.ClaimId,
            second.Corpus.CorpusId,
            "supports",
            first.Claim.ClaimId,
            0.82,
            now,
            now));
        graph.UpsertGraphLink(new FabricGraphLinkEntry(
            "link-claim-code",
            "claim",
            first.Claim.ClaimId,
            first.Corpus.CorpusId,
            "codegraph_node",
            nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            null,
            "implemented-by",
            first.Claim.ClaimId,
            0.7,
            now,
            now));

        var firstCorpusLinks = graph.ListGraphLinksForCorpus(first.Corpus.CorpusId, includeInbound: true);
        var secondOutboundLinks = graph.ListGraphLinksForCorpus(second.Corpus.CorpusId, includeInbound: false);
        var codeLinks = graph.ListGraphLinksForObject("codegraph_node", nodeId.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.Multiple(() =>
        {
            Assert.That(firstCorpusLinks.Select(item => item.LinkId), Is.EquivalentTo(new[] { "link-cross-corpus", "link-claim-code" }));
            Assert.That(secondOutboundLinks, Is.Empty);
            Assert.That(codeLinks, Has.Count.EqualTo(1));
            Assert.That(codeLinks[0].SourceId, Is.EqualTo(first.Claim.ClaimId));
            Assert.That(codeLinks[0].TargetCorpusId, Is.Null);
        });

        library.DeleteCorpus(first.Corpus.CorpusId);

        Assert.Multiple(() =>
        {
            Assert.That(graph.ListGraphLinksForObject("claim", first.Claim.ClaimId), Is.Empty);
            Assert.That(graph.ListGraphLinksForCorpus(second.Corpus.CorpusId, includeInbound: true), Is.Empty);
        });

        var third = SeedClaim(library, graph, "corpus-link-c", "doc-link-c", "seg-link-c", "claim-link-c", "Gamma source points at code.", now);
        var secondNodeId = codeGraph.UpsertNode(new CodeNode(
            null,
            "TheOrc",
            "Method",
            "DeleteGraphLink",
            "OrchestratorIDE.Services.ContextFabric.DocumentGraphRepository.DeleteGraphLink",
            "OrchestratorIDE/Services/ContextFabric/DocumentGraphRepository.cs",
            380,
            382,
            null,
            null,
            null,
            null,
            null));
        var secondNodeIdText = secondNodeId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        graph.UpsertGraphLink(new FabricGraphLinkEntry(
            "link-code-clear",
            "claim",
            third.Claim.ClaimId,
            third.Corpus.CorpusId,
            "codegraph_node",
            secondNodeIdText,
            null,
            "implemented-by",
            third.Claim.ClaimId,
            0.6,
            now,
            now));

        codeGraph.ClearProject("TheOrc");

        Assert.That(graph.ListGraphLinksForObject("codegraph_node", secondNodeIdText), Is.Empty);
    }

    [Test]
    public void DocumentGraphRepository_Rejects_Link_With_Mismatched_Corpus()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var now = DateTimeOffset.UtcNow;
        var first = SeedClaim(library, graph, "corpus-mismatch-a", "doc-mismatch-a", "seg-mismatch-a", "claim-mismatch-a", "Alpha source.", now);
        var second = SeedClaim(library, graph, "corpus-mismatch-b", "doc-mismatch-b", "seg-mismatch-b", "claim-mismatch-b", "Beta source.", now);

        var ex = Assert.Throws<InvalidDataException>(() => graph.UpsertGraphLink(new FabricGraphLinkEntry(
            "link-bad-corpus",
            "claim",
            first.Claim.ClaimId,
            second.Corpus.CorpusId,
            "claim",
            second.Claim.ClaimId,
            second.Corpus.CorpusId,
            "supports",
            null,
            null,
            now,
            now)));

        Assert.That(ex!.Message, Does.Contain("does not match"));
    }

    [Test]
    public void EvidenceGraphImporter_Projects_Validated_Card_Into_Claims_And_Entities()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;
        const string segmentText = "The public good is disregarded in the conflicts of rival parties.";
        const string quoteText = "public good is disregarded in the conflicts of rival parties";

        var corpus = library.CreateCorpus("corpus-import", "Import lane");
        var document = new FabricDocumentEntry(
            "doc-import",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Federalist",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                "seg-import",
                0,
                "No. 10",
                0,
                segmentText.Length,
                14,
                "seg-digest",
                segmentText,
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);

        var imported = importer.ImportEvidenceCard(new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = document.DocumentId,
            SegmentId = "seg-import",
            Summary = "Faction harms the public good.",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "claim-faction",
                    Type = "assertion",
                    Text = "Faction can harm civic welfare.",
                    Confidence = 0.8,
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = "seg-import",
                            CharStart = 4,
                            CharEnd = 4 + quoteText.Length,
                            Quote = quoteText,
                            QuoteDigest = "quote-digest"
                        }
                    ]
                }
            ],
            Entities = ["public good", "rival parties"]
        });

        var claims = graph.ListClaims(corpus.CorpusId, limit: 10);
        var claimSearch = graph.SearchClaims("harm civic welfare", corpus.CorpusId, 10);
        var entities = graph.ListEntities(corpus.CorpusId, 10);

        Assert.Multiple(() =>
        {
            Assert.That(imported, Is.EqualTo(1));
            Assert.That(claims, Has.Count.EqualTo(1));
            Assert.That(claims[0].VerificationStatus, Is.EqualTo(FabricVerificationStatus.Provisional));
            Assert.That(claimSearch, Has.Count.EqualTo(1));
            Assert.That(claimSearch[0].ClaimId, Does.StartWith("claim-"));
            Assert.That(entities.Select(item => item.CanonicalName),
                Is.EquivalentTo(new[] { "public good", "rival parties" }));
        });
    }

    [Test]
    public void EvidenceGraphImporter_Rejects_Corpus_Id_Mismatch()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;

        var corpus = library.CreateCorpus("corpus-import-mismatch", "Import lane");
        var document = new FabricDocumentEntry(
            "doc-import-mismatch",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Federalist",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                "seg-import-mismatch",
                0,
                "No. 10",
                0,
                20,
                4,
                "seg-digest",
                "Faction harms union.",
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);

        Assert.That(
            () => importer.ImportEvidenceCard(new FabricEvidenceCard
            {
                CorpusId = "corpus-other",
                DocumentId = document.DocumentId,
                SegmentId = "seg-import-mismatch",
                Claims = [new FabricClaim { ClaimId = "claim-1", Text = "Faction harms union." }]
            }),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(graph.ListClaims(corpus.CorpusId, limit: 10), Is.Empty);
    }

    [Test]
    public void EvidenceGraphImporter_Rejects_Citation_Segment_From_Another_Document()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;

        var corpus = library.CreateCorpus("corpus-cross-doc-citation", "Import lane");
        var first = new FabricDocumentEntry(
            "doc-first",
            corpus.CorpusId,
            "source-digest-1",
            "normalized-digest-1",
            "Federalist 1",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        var second = new FabricDocumentEntry(
            "doc-second",
            corpus.CorpusId,
            "source-digest-2",
            "normalized-digest-2",
            "Federalist 2",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(first,
        [
            new FabricSegmentDraft("seg-first", 0, "No. 10", 0, "Faction harms union.".Length, 4, "seg-digest-1", "Faction harms union.", null, null, FabricIngestionVersions.Segmenter)
        ]);
        library.ReplaceDocument(second,
        [
            new FabricSegmentDraft("seg-second", 0, "No. 51", 0, "Ambition checks ambition.".Length, 4, "seg-digest-2", "Ambition checks ambition.", null, null, FabricIngestionVersions.Segmenter)
        ]);

        Assert.That(
            () => importer.ImportEvidenceCard(new FabricEvidenceCard
            {
                CorpusId = corpus.CorpusId,
                DocumentId = first.DocumentId,
                SegmentId = "seg-first",
                Claims =
                [
                    new FabricClaim
                    {
                        ClaimId = "claim-1",
                        Text = "Faction harms union.",
                        Citations = [new FabricCitation { SegmentId = "seg-second", CharStart = 0, CharEnd = "Ambition checks ambition.".Length, QuoteDigest = "quote-2", Quote = "Ambition checks ambition." }]
                    }
                ]
            }),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(graph.ListClaims(corpus.CorpusId, limit: 10), Is.Empty);
    }

    [Test]
    public void EvidenceGraphImporter_Assigns_Unique_Fallback_Ids_For_Blank_ClaimIds()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;

        var corpus = library.CreateCorpus("corpus-blank-claim-ids", "Import lane");
        var document = new FabricDocumentEntry(
            "doc-blank-ids",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Federalist",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft("seg-blank-ids", 0, "No. 10", 0, "Faction harms union.".Length, 4, "seg-digest", "Faction harms union.", null, null, FabricIngestionVersions.Segmenter)
        ]);

        var imported = importer.ImportEvidenceCard(new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = document.DocumentId,
            SegmentId = "seg-blank-ids",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "",
                    Text = "Faction harms union.",
                    Citations = [new FabricCitation { SegmentId = "seg-blank-ids", CharStart = 0, CharEnd = "Faction harms union.".Length, QuoteDigest = "quote-1", Quote = "Faction harms union." }]
                },
                new FabricClaim
                {
                    ClaimId = "",
                    Text = "Faction harms union differently.",
                    Citations = [new FabricCitation { SegmentId = "seg-blank-ids", CharStart = 0, CharEnd = "Faction harms union.".Length, QuoteDigest = "quote-2", Quote = "Faction harms union." }]
                }
            ]
        });

        var claims = graph.ListClaims(corpus.CorpusId, limit: 10);
        Assert.Multiple(() =>
        {
            Assert.That(imported, Is.EqualTo(2));
            Assert.That(claims, Has.Count.EqualTo(2));
            Assert.That(claims.Select(item => item.ClaimId).Distinct().Count(), Is.EqualTo(2));
        });
    }

    [Test]
    public void EvidenceGraphImporter_Scopes_Duplicate_Local_Claim_Ids_Per_Document()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;

        var corpus = library.CreateCorpus("corpus-duplicate-claims", "Import lane");
        var first = new FabricDocumentEntry(
            "doc-first",
            corpus.CorpusId,
            "source-digest-1",
            "normalized-digest-1",
            "Federalist 1",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        var second = new FabricDocumentEntry(
            "doc-second",
            corpus.CorpusId,
            "source-digest-2",
            "normalized-digest-2",
            "Federalist 2",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(first,
        [
            new FabricSegmentDraft("seg-first", 0, "No. 10", 0, 20, 4, "seg-digest-1", "Faction harms union.", null, null, FabricIngestionVersions.Segmenter)
        ]);
        library.ReplaceDocument(second,
        [
            new FabricSegmentDraft("seg-second", 0, "No. 51", 0, 24, 4, "seg-digest-2", "Ambition checks ambition.", null, null, FabricIngestionVersions.Segmenter)
        ]);

        importer.ImportEvidenceCard(new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = first.DocumentId,
            SegmentId = "seg-first",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "claim-local",
                    Text = "Faction harms union.",
                    Citations = [new FabricCitation { SegmentId = "seg-first", CharStart = 0, CharEnd = "Faction harms union.".Length, QuoteDigest = "quote-1", Quote = "Faction harms union." }]
                }
            ]
        });
        importer.ImportEvidenceCard(new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = second.DocumentId,
            SegmentId = "seg-second",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "claim-local",
                    Text = "Ambition checks ambition.",
                    Citations = [new FabricCitation { SegmentId = "seg-second", CharStart = 0, CharEnd = "Ambition checks ambition.".Length, QuoteDigest = "quote-2", Quote = "Ambition checks ambition." }]
                }
            ]
        });

        var claims = graph.ListClaims(corpus.CorpusId, limit: 10);
        Assert.That(claims, Has.Count.EqualTo(2));
        Assert.That(claims.Select(item => item.DocumentId), Is.EquivalentTo(new[] { first.DocumentId, second.DocumentId }));
        Assert.That(claims.Select(item => item.ClaimId).Distinct().Count(), Is.EqualTo(2));
        Assert.That(claims.SelectMany(item => graph.ListClaimCitations(item.ClaimId)).Count(), Is.EqualTo(2));
    }

    [Test]
    public void FabricSearchService_Adds_Claim_Expanded_Segment_Hits_When_Lexical_Search_Misses()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var search = new FabricSearchService(library, graph);
        var now = DateTimeOffset.UtcNow;
        const string segmentText = "The public good is disregarded in the conflicts of rival parties.";
        const string quoteText = "public good is disregarded in the conflicts of rival parties";

        var corpus = library.CreateCorpus("corpus-search", "Search lane");
        var document = new FabricDocumentEntry(
            "doc-search",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Federalist",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                "seg-search",
                0,
                "No. 10",
                0,
                segmentText.Length,
                14,
                "seg-digest",
                segmentText,
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);
        importer.ImportEvidenceCard(new FabricEvidenceCard
        {
            CorpusId = corpus.CorpusId,
            DocumentId = document.DocumentId,
            SegmentId = "seg-search",
            Summary = "Faction harms the public good.",
            Claims =
            [
                new FabricClaim
                {
                    ClaimId = "claim-search",
                    Type = "assertion",
                    Text = "Faction can harm civic welfare.",
                    Confidence = 0.8,
                    Citations =
                    [
                        new FabricCitation
                        {
                            SegmentId = "seg-search",
                            CharStart = 4,
                            CharEnd = 4 + quoteText.Length,
                            Quote = quoteText,
                            QuoteDigest = "quote-digest"
                        }
                    ]
                }
            ]
        });

        var lexical = library.Search("civic welfare", corpus.CorpusId, 10);
        var expanded = search.Search("civic welfare", corpus.CorpusId, 10);

        Assert.Multiple(() =>
        {
            Assert.That(lexical, Is.Empty);
            Assert.That(expanded, Has.Count.EqualTo(1));
            Assert.That(expanded[0].SegmentId, Is.EqualTo("seg-search"));
            Assert.That(expanded[0].CorpusId, Is.EqualTo(corpus.CorpusId));
            Assert.That(expanded[0].DocumentId, Is.EqualTo(document.DocumentId));
            Assert.That(expanded[0].DisplayName, Is.EqualTo(document.DisplayName));
            Assert.That(expanded[0].RetrievalPath, Is.EqualTo("claim"));
            Assert.That(expanded[0].ClaimId, Does.StartWith("claim-"));
        });
    }

    [Test]
    public void DocumentGraphRepository_Persists_Fts_Search_On_Disk_Across_Reopen()
    {
        var root = NewTempRoot();
        var dbRoot = Path.Combine(root, "workspace");
        Directory.CreateDirectory(dbRoot);
        var dbPath = Path.Combine(dbRoot, ".orc");
        Directory.CreateDirectory(dbPath);

        string corpusId;
        const string documentId = "doc-disk";

        using (var store = new SqliteStore(dbRoot))
        {
            store.Initialize();
            var library = new FabricLibraryRepository(store);
            var graph = new DocumentGraphRepository(store);
            var importer = new FabricEvidenceGraphImporter(library, graph);
            var now = DateTimeOffset.UtcNow;
            const string segmentText = "All legislative Powers herein granted shall be vested in a Congress.";

            var corpus = library.CreateCorpus("corpus-disk", "Disk lane");
            corpusId = corpus.CorpusId;
            var document = new FabricDocumentEntry(
                documentId,
                corpus.CorpusId,
                "source-digest",
                "normalized-digest",
                "Constitution",
                "text/plain",
                FabricIngestionVersions.TextMarkdownParser,
                FabricIngestionVersions.TextMarkdownParser,
                "ready",
                [],
                now,
                now);
            library.ReplaceDocument(document,
            [
                new FabricSegmentDraft(
                    "seg-disk",
                    0,
                    "Article I",
                    0,
                    segmentText.Length,
                    12,
                    "seg-digest",
                    segmentText,
                    null,
                    null,
                    FabricIngestionVersions.Segmenter)
            ]);

            importer.ImportEvidenceCard(new FabricEvidenceCard
            {
                CorpusId = corpus.CorpusId,
                DocumentId = document.DocumentId,
                SegmentId = "seg-disk",
                Summary = "Legislative powers vest in Congress.",
                Claims =
                [
                    new FabricClaim
                    {
                        ClaimId = "claim-disk",
                        Type = "assertion",
                        Text = "Legislative authority is vested in Congress.",
                        Confidence = 0.9,
                        Citations =
                        [
                            new FabricCitation
                            {
                                SegmentId = "seg-disk",
                                CharStart = 0,
                                CharEnd = segmentText.Length,
                                Quote = segmentText,
                                QuoteDigest = "quote-digest"
                            }
                        ]
                    }
                ]
            });
        }

        using (var store = new SqliteStore(dbRoot))
        {
            store.Initialize();
            var library = new FabricLibraryRepository(store);
            var graph = new DocumentGraphRepository(store);
            var search = new FabricSearchService(library, graph);

            var claims = graph.SearchClaims("vested in Congress", corpusId, 10);
            var expanded = search.Search("legislative authority", corpusId, 10);

            Assert.Multiple(() =>
            {
                Assert.That(claims, Has.Count.EqualTo(1));
                Assert.That(claims[0].DocumentId, Is.EqualTo(documentId));
                Assert.That(expanded, Has.Count.EqualTo(1));
                Assert.That(expanded[0].CorpusId, Is.EqualTo(corpusId));
                Assert.That(expanded[0].DocumentId, Is.EqualTo(documentId));
                Assert.That(expanded[0].SegmentId, Is.EqualTo("seg-disk"));
                Assert.That(expanded[0].RetrievalPath, Is.EqualTo("claim"));
            });
        }
    }

    [Test]
    public void FabricSearchService_Lexical_Hits_Always_Carry_Provenance()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var search = new FabricSearchService(library, graph);
        var now = DateTimeOffset.UtcNow;
        const string segmentText = "Variation under domestication appears everywhere.";

        var corpus = library.CreateCorpus("corpus-lexical", "Lexical lane");
        var document = new FabricDocumentEntry(
            "doc-lexical",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "Darwin",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                "seg-lexical",
                0,
                "Chapter I",
                0,
                segmentText.Length,
                8,
                "seg-digest",
                segmentText,
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);

        var hits = search.Search("domestication", corpus.CorpusId, 10);

        Assert.Multiple(() =>
        {
            Assert.That(hits, Has.Count.EqualTo(1));
            Assert.That(hits[0].CorpusId, Is.EqualTo(corpus.CorpusId));
            Assert.That(hits[0].DocumentId, Is.EqualTo(document.DocumentId));
            Assert.That(hits[0].SegmentId, Is.EqualTo("seg-lexical"));
            Assert.That(hits[0].DisplayName, Is.EqualTo(document.DisplayName));
            Assert.That(hits[0].RetrievalPath, Is.EqualTo("segment"));
        });
    }

    private static SeededClaim SeedClaim(
        FabricLibraryRepository library,
        DocumentGraphRepository graph,
        string corpusId,
        string documentId,
        string segmentId,
        string claimId,
        string text,
        DateTimeOffset now)
    {
        var corpus = library.CreateCorpus(corpusId, corpusId);
        var document = new FabricDocumentEntry(
            documentId,
            corpus.CorpusId,
            $"{documentId}-source",
            $"{documentId}-normalized",
            documentId,
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);
        library.ReplaceDocument(document,
        [
            new FabricSegmentDraft(
                segmentId,
                0,
                null,
                0,
                text.Length,
                8,
                $"{segmentId}-digest",
                text,
                null,
                null,
                FabricIngestionVersions.Segmenter)
        ]);

        var claim = new FabricClaimEntry(
            claimId,
            corpus.CorpusId,
            document.DocumentId,
            segmentId,
            "assertion",
            text,
            FabricVerificationStatus.Provisional,
            0.75,
            now,
            now);
        graph.UpsertClaim(claim,
        [
            new FabricClaimCitationEntry(
                claim.ClaimId,
                0,
                segmentId,
                0,
                text.Length,
                FabricHashing.Sha256(text),
                text)
        ]);

        return new SeededClaim(corpus, document, claim);
    }

    private sealed record SeededClaim(
        FabricCorpusEntry Corpus,
        FabricDocumentEntry Document,
        FabricClaimEntry Claim);

    private static int Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-cf2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }
}
