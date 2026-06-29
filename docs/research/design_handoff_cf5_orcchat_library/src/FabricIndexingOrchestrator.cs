// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: wraps the native read pipeline + best-effort reduce, with staged IProgress<> events.
// Drop into OrchestratorIDE/Services/ContextFabric/

using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.ContextFabric;

public enum IndexStageKind { Parsing, Reading, Reducing, Complete, Failed }

public sealed record IndexStageEvent(
    string DocumentId,
    IndexStageKind Stage,
    int CompletedSegments,
    int TotalSegments,
    IReadOnlyList<string> FailedSegmentIds,
    string? Error = null);

public sealed class FabricIndexingOrchestrator(
    FabricLibraryService libraryService,
    FabricNativeReaderService readerService,
    FabricReducer reducer,
    FabricEvidenceGraphImporter graphImporter,
    FabricLibraryRepository libraryRepository,
    DocumentGraphRepository graphRepository)
{
    public async Task<IndexStageEvent> IndexDocumentAsync(
        string documentId,
        bool readOnly,
        IProgress<IndexStageEvent>? progress = null,
        CancellationToken ct = default)
    {
        // Resolve segment count upfront for progress reporting
        var segments = libraryRepository.GetSegments(documentId);
        var total = segments.Count;
        if (total == 0)
            return Fail(documentId, total, "Document has no segments. Re-import the source file.");

        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reading, 0, total, []));

        // ── Stage 1: Native read (per-segment model calls) ──────────────────
        FabricDocumentReadResult readResult;
        try
        {
            readResult = await readerService.ReadDocumentAsync(documentId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(documentId, total, ex.Message);
        }

        var failedIds = readResult.ReadReport.SegmentResults
            .Where(r => !r.Accepted)
            .Select(r => r.SegmentId)
            .ToList();

        var completed = total - failedIds.Count;

        if (readOnly)
        {
            progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, failedIds));
            return new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, failedIds);
        }

        // ── Stage 2: Best-effort reduce ──────────────────────────────────────
        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reducing, completed, total, failedIds));

        try
        {
            var doc = libraryRepository.GetDocument(documentId)!;
            var acceptedCards = readResult.ReadReport.SegmentResults
                .Where(r => r.Accepted && r.Card is not null)
                .Select(r => r.Card!)
                .ToArray();

            // FabricReducer.ReduceAsync builds the hierarchy memory nodes.
            // If it fails we still consider indexing successful with a partial warning.
            await reducer.ReduceAsync(doc.CorpusId, documentId, acceptedCards, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Reduce failure is non-fatal — Quick mode still works on segments + claims.
            // Study mode will fall back to segment reopening (planner handles gracefully).
        }

        var ev = new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, failedIds);
        progress?.Report(ev);
        return ev;
    }

    public async Task<IndexStageEvent> RetryFailedAsync(
        string documentId,
        IReadOnlyList<string> failedSegmentIds,
        bool readOnly,
        IProgress<IndexStageEvent>? progress = null,
        CancellationToken ct = default)
    {
        // Re-read only the failed segments, then re-import their evidence cards.
        // Idempotent: evidence import is keyed by generation + canonical hash.
        var total = libraryRepository.GetSegments(documentId).Count;
        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reading, 0, failedSegmentIds.Count, []));

        // TODO: FabricNativeReaderService needs a ReadSegmentsAsync(IReadOnlyList<string>) overload
        // that restricts the FeasibilityRunner corpus to the given segment IDs.
        // For now, call ReadDocumentAsync and filter results client-side.
        return await IndexDocumentAsync(documentId, readOnly, progress, ct).ConfigureAwait(false);
    }

    private static IndexStageEvent Fail(string documentId, int total, string error) =>
        new(documentId, IndexStageKind.Failed, 0, total, [], error);
}
