// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricIndexingOrchestratorTests
{
    [Test]
    public async Task IndexDocumentAsync_Reads_Then_Reduces_And_Reports_Complete()
    {
        using var harness = NewHarness();
        var orchestrator = harness.NewOrchestrator();
        var events = new List<IndexStageEvent>();
        var progress = new Progress<IndexStageEvent>(events.Add);

        var result = await orchestrator.IndexDocumentAsync(harness.Document.DocumentId, readOnly: false, progress);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stage, Is.EqualTo(IndexStageKind.Complete));
            Assert.That(result.CompletedSegments, Is.EqualTo(2));
            Assert.That(result.FailedSegmentIds, Is.Empty);
            Assert.That(events.Select(e => e.Stage), Does.Contain(IndexStageKind.Reading));
            Assert.That(events.Select(e => e.Stage), Does.Contain(IndexStageKind.Reducing));
            // Reduce actually ran and persisted memory nodes for the document.
            Assert.That(harness.Graph.ListMemoryNodes(harness.Corpus.CorpusId, harness.Document.DocumentId), Is.Not.Empty);
        });
    }

    [Test]
    public async Task IndexDocumentAsync_ReadOnly_Skips_Reduce_Stage()
    {
        using var harness = NewHarness();
        var orchestrator = harness.NewOrchestrator();
        var events = new List<IndexStageEvent>();
        var progress = new Progress<IndexStageEvent>(events.Add);

        var result = await orchestrator.IndexDocumentAsync(harness.Document.DocumentId, readOnly: true, progress);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stage, Is.EqualTo(IndexStageKind.Complete));
            Assert.That(events.Select(e => e.Stage), Does.Not.Contain(IndexStageKind.Reducing));
            Assert.That(harness.Graph.ListMemoryNodes(harness.Corpus.CorpusId, harness.Document.DocumentId), Is.Empty);
        });
    }

    [Test]
    public void IndexDocumentAsync_Fails_Fast_When_Document_Has_No_Segments()
    {
        using var harness = NewHarness();
        var orchestrator = harness.NewOrchestrator();

        // FabricLibraryRepository.ReplaceDocument rejects zero-segment documents outright, so
        // the orchestrator's own guard is only reachable for a document id with no repository
        // row at all (GetSegments returns empty rather than throwing) — exercise that path.
        var result = orchestrator.IndexDocumentAsync("missing-doc", readOnly: true).GetAwaiter().GetResult();

        Assert.Multiple(() =>
        {
            Assert.That(result.Stage, Is.EqualTo(IndexStageKind.Failed));
            Assert.That(result.Error, Does.Contain("no segments"));
        });
    }

    [Test]
    public async Task RetryFailedAsync_With_No_Failed_Segments_Delegates_To_Full_Index()
    {
        using var harness = NewHarness();
        var orchestrator = harness.NewOrchestrator();

        var result = await orchestrator.RetryFailedAsync(harness.Document.DocumentId, [], readOnly: false);

        Assert.That(result.Stage, Is.EqualTo(IndexStageKind.Complete));
        Assert.That(harness.Graph.ListMemoryNodes(harness.Corpus.CorpusId, harness.Document.DocumentId), Is.Not.Empty);
    }

    [Test]
    public async Task RetryFailedAsync_Reimports_Only_The_Given_Segments_Without_Wiping_Others()
    {
        using var harness = NewHarness();
        var orchestrator = harness.NewOrchestrator();

        // Full read first — both segments' claims land in the graph.
        await orchestrator.IndexDocumentAsync(harness.Document.DocumentId, readOnly: true);
        var claimsBefore = harness.Graph.ListClaimsForDocument(harness.Document.DocumentId, limit: 64);
        Assert.That(claimsBefore, Has.Count.GreaterThanOrEqualTo(2));

        // Retry only seg-0 — seg-1's previously-imported claims must survive untouched
        // (RetryFailedAsync uses FabricNativeReaderService.ReadSegmentsAsync + ImportEvidenceCard,
        // not the document-wide ReplaceDocumentEvidenceCards the full read path uses).
        var result = await orchestrator.RetryFailedAsync(harness.Document.DocumentId, ["seg-0"], readOnly: true);

        var claimsAfter = harness.Graph.ListClaimsForDocument(harness.Document.DocumentId, limit: 64);

        Assert.Multiple(() =>
        {
            Assert.That(result.Stage, Is.EqualTo(IndexStageKind.Complete));
            Assert.That(claimsAfter.Select(c => c.SegmentId), Does.Contain("seg-1"));
            Assert.That(claimsAfter.Select(c => c.SegmentId), Does.Contain("seg-0"));
        });
    }

    private static Harness NewHarness(string[]? segments = null, string corpusId = "cf5-corpus", string documentId = "cf5-doc")
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var corpus = library.CreateCorpus(corpusId, "CF-5 indexing lane");
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            documentId,
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "CF-5 Notes",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);

        var texts = segments ?? new[]
        {
            "Section about call signs.\nEVIDENCE: LANTERN is the assigned call sign.\n",
            "Section about frequencies.\nEVIDENCE: Emergency frequency is 17.4 MHz.\n",
        };

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

        return new Harness(store, library, graph, corpus, document);
    }

    private sealed class Harness(
        SqliteStore store,
        FabricLibraryRepository library,
        DocumentGraphRepository graph,
        FabricCorpusEntry corpus,
        FabricDocumentEntry document) : IDisposable
    {
        public FabricLibraryRepository Library { get; } = library;
        public DocumentGraphRepository Graph { get; } = graph;
        public FabricCorpusEntry Corpus { get; } = corpus;
        public FabricDocumentEntry Document { get; } = document;

        public FabricIndexingOrchestrator NewOrchestrator()
        {
            var reader = new FabricNativeReaderService(Library, Graph, new ScriptedFabricRuntime());
            var reducer = new FabricReducer(Library, Graph, new FabricReducerOptions(FanIn: 2, MaxSummaryChars: 200));
            return new FabricIndexingOrchestrator(reader, reducer, Library);
        }

        public void Dispose() => store.Dispose();
    }
}
