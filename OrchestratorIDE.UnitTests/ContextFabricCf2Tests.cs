// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
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
    public void DocumentGraphRepository_Stores_Searches_And_Links_Provisional_Graph_Data()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var now = DateTimeOffset.UtcNow;

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
                42,
                9,
                "seg-digest",
                "Natural selection preserves favorable variations.",
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
                42,
                "quote-digest",
                "Natural selection preserves favorable variations.")
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
    public void EvidenceGraphImporter_Projects_Validated_Card_Into_Claims_And_Entities()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var importer = new FabricEvidenceGraphImporter(library, graph);
        var now = DateTimeOffset.UtcNow;

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
                71,
                14,
                "seg-digest",
                "The public good is disregarded in the conflicts of rival parties.",
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
                            CharEnd = 53,
                            Quote = "public good is disregarded in the conflicts of rival parties",
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
                    Citations = [new FabricCitation { SegmentId = "seg-first", CharStart = 0, CharEnd = 20, QuoteDigest = "quote-1", Quote = "Faction harms union." }]
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
                    Citations = [new FabricCitation { SegmentId = "seg-second", CharStart = 0, CharEnd = 24, QuoteDigest = "quote-2", Quote = "Ambition checks ambition." }]
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
                71,
                14,
                "seg-digest",
                "The public good is disregarded in the conflicts of rival parties.",
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
                            CharEnd = 53,
                            Quote = "public good is disregarded in the conflicts of rival parties",
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
                    66,
                    12,
                    "seg-digest",
                    "All legislative Powers herein granted shall be vested in a Congress.",
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
                                CharEnd = 66,
                                Quote = "All legislative Powers herein granted shall be vested in a Congress.",
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
                48,
                8,
                "seg-digest",
                "Variation under domestication appears everywhere.",
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
