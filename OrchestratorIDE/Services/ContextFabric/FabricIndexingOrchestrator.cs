// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: wraps the native read pipeline + best-effort reduce, with staged IProgress<> events.

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
    FabricNativeReaderService readerService,
    FabricReducer reducer,
    FabricLibraryRepository libraryRepository)
{
    public async Task<IndexStageEvent> IndexDocumentAsync(
        string documentId,
        bool readOnly,
        IProgress<IndexStageEvent>? progress = null,
        CancellationToken ct = default)
    {
        var segments = libraryRepository.GetSegments(documentId);
        var total = segments.Count;
        if (total == 0)
            return Fail(documentId, total, "Document has no segments. Re-import the source file.");

        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reading, 0, total, []));

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
            var doneEvent = new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, failedIds);
            progress?.Report(doneEvent);
            return doneEvent;
        }

        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reducing, completed, total, failedIds));

        try
        {
            reducer.ReduceDocument(documentId);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Reduce failure is non-fatal — Quick mode still works on segments + claims.
            // Study mode falls back to segment reopening (planner handles it).
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
        ArgumentNullException.ThrowIfNull(failedSegmentIds);
        var total = libraryRepository.GetSegments(documentId).Count;
        if (failedSegmentIds.Count == 0)
            return await IndexDocumentAsync(documentId, readOnly, progress, ct).ConfigureAwait(false);

        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reading, 0, failedSegmentIds.Count, []));

        FabricCorpusReadReport retryReport;
        try
        {
            retryReport = await readerService.ReadSegmentsAsync(documentId, failedSegmentIds, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Fail(documentId, total, ex.Message);
        }

        var stillFailedIds = retryReport.SegmentResults
            .Where(r => !r.Accepted)
            .Select(r => r.SegmentId)
            .ToList();
        var completed = total - stillFailedIds.Count;

        if (readOnly)
        {
            var doneEvent = new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, stillFailedIds);
            progress?.Report(doneEvent);
            return doneEvent;
        }

        progress?.Report(new IndexStageEvent(documentId, IndexStageKind.Reducing, completed, total, stillFailedIds));

        try
        {
            reducer.ReduceDocument(documentId);
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Non-fatal — see IndexDocumentAsync.
        }

        var ev = new IndexStageEvent(documentId, IndexStageKind.Complete, completed, total, stillFailedIds);
        progress?.Report(ev);
        return ev;
    }

    private static IndexStageEvent Fail(string documentId, int total, string error) =>
        new(documentId, IndexStageKind.Failed, 0, total, [], error);
}
