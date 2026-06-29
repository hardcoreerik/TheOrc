// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record FabricDocumentReadResult(
    FabricDocumentEntry Document,
    FabricCorpusReadReport ReadReport,
    int ImportedClaims);

public sealed class FabricNativeReaderService
{
    private readonly FabricLibraryRepository _libraryRepository;
    private readonly FabricEvidenceGraphImporter _graphImporter;
    private readonly ContextFabricFeasibilityRunner _runner;

    public FabricNativeReaderService(
        FabricLibraryRepository libraryRepository,
        DocumentGraphRepository graphRepository,
        IRoleRuntime runtime,
        FabricRunOptions? options = null)
    {
        _libraryRepository = libraryRepository ?? throw new ArgumentNullException(nameof(libraryRepository));
        ArgumentNullException.ThrowIfNull(graphRepository);
        ArgumentNullException.ThrowIfNull(runtime);
        _graphImporter = new FabricEvidenceGraphImporter(_libraryRepository, graphRepository);
        _runner = new ContextFabricFeasibilityRunner(runtime, options);
    }

    public async Task<FabricDocumentReadResult> ReadDocumentAsync(
        string documentId,
        CancellationToken ct = default)
    {
        var document = _libraryRepository.GetDocument(documentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{documentId}' does not exist.");
        var segments = _libraryRepository.GetSegments(documentId);
        if (segments.Count == 0)
            throw new InvalidDataException($"Context Fabric document '{documentId}' has no segments.");

        var corpus = BuildCorpus(document, segments);
        var readReport = await _runner.ReadCorpusAsync(corpus, ct).ConfigureAwait(false);

        var cards = readReport.SegmentResults
            .Where(item => item.Accepted && item.Card is not null)
            .Select(item => item.Card!)
            .ToArray();
        var importedClaims = _graphImporter.ReplaceDocumentEvidenceCards(document.DocumentId, cards);

        return new FabricDocumentReadResult(document, readReport, importedClaims);
    }

    /// <summary>
    /// Re-reads only the given segments and upserts their cards without touching
    /// claims already imported for the rest of the document (unlike ReadDocumentAsync,
    /// which replaces the full document's claim set).
    /// </summary>
    public async Task<FabricCorpusReadReport> ReadSegmentsAsync(
        string documentId,
        IReadOnlyList<string> segmentIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segmentIds);
        var document = _libraryRepository.GetDocument(documentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{documentId}' does not exist.");
        var requested = segmentIds.ToHashSet(StringComparer.Ordinal);
        var scopedSegments = _libraryRepository.GetSegments(documentId)
            .Where(segment => requested.Contains(segment.SegmentId))
            .ToArray();
        if (scopedSegments.Length == 0)
            throw new InvalidDataException($"None of the requested segment ids belong to document '{documentId}'.");

        var corpus = BuildCorpus(document, scopedSegments);
        var readReport = await _runner.ReadCorpusAsync(corpus, ct).ConfigureAwait(false);

        foreach (var card in readReport.SegmentResults
                     .Where(item => item.Accepted && item.Card is not null)
                     .Select(item => item.Card!))
        {
            _graphImporter.ImportEvidenceCard(card);
        }

        return readReport;
    }

    internal static FabricCorpus BuildCorpus(
        FabricDocumentEntry document,
        IReadOnlyList<FabricSegmentEntry> segments)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(segments);

        var orderedSegments = segments
            .OrderBy(segment => segment.Ordinal)
            .Select(segment => new FabricSegment(
                segment.SegmentId,
                segment.Ordinal,
                segment.HeadingPath ?? "",
                segment.Text,
                segment.TextDigest,
                Math.Max(1, segment.TokenCount)))
            .ToArray();

        return new FabricCorpus(
            document.CorpusId,
            document.DocumentId,
            $"fabric-doc-{document.DocumentId}",
            document.SourceDigest,
            FabricSchemaVersions.Corpus,
            orderedSegments,
            orderedSegments.Sum(segment => segment.EstimatedTokens));
    }
}
