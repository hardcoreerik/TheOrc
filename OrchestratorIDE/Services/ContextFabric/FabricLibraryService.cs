// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricLibraryService
{
    private readonly FabricLibraryRepository _repository;
    private readonly ContentAddressedStore _artifacts;
    private readonly FabricDocumentParserRegistry _parsers;
    private readonly FabricLibraryOptions _options;

    public FabricLibraryService(
        FabricLibraryRepository repository,
        ContentAddressedStore artifacts,
        FabricDocumentParserRegistry? parsers = null,
        FabricLibraryOptions? options = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _artifacts = artifacts ?? throw new ArgumentNullException(nameof(artifacts));
        _parsers = parsers ?? new FabricDocumentParserRegistry();
        _options = options ?? new FabricLibraryOptions();
        _options.Validate();
    }

    public FabricCorpusEntry CreateCorpus(
        string name,
        string? description = null,
        string policyProfile = "default") =>
        _repository.CreateCorpus($"corpus-{Guid.NewGuid():N}", name, description, policyProfile);

    public IReadOnlyList<FabricCorpusEntry> ListCorpora() => _repository.ListCorpora();

    public IReadOnlyList<FabricDocumentEntry> ListDocuments(string corpusId) =>
        _repository.ListDocuments(corpusId);

    public IReadOnlyList<FabricSearchHit> Search(string query, string? corpusId = null, int limit = 50) =>
        _repository.Search(query, corpusId, limit);

    public async Task<FabricImportResult> ImportFileAsync(
        string corpusId,
        string path,
        string? mediaType = null,
        CancellationToken ct = default)
    {
        if (_repository.GetCorpus(corpusId) is null)
            throw new KeyNotFoundException($"Context Fabric corpus '{corpusId}' does not exist.");
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Source path is required.", nameof(path));

        var fullPath = Path.GetFullPath(path);
        var info = new FileInfo(fullPath);
        if (!info.Exists) throw new FileNotFoundException("Context Fabric source does not exist.", fullPath);
        EnsureBoundedSize(info.Length);

        var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
        return await ImportBytesAsync(
            corpusId,
            info.Name,
            mediaType ?? InferMediaType(info.Extension),
            bytes,
            ct).ConfigureAwait(false);
    }

    public async Task<FabricImportResult> RebuildDocumentAsync(
        string documentId,
        CancellationToken ct = default)
    {
        var existing = _repository.GetDocument(documentId)
            ?? throw new KeyNotFoundException($"Context Fabric document '{documentId}' does not exist.");
        var path = _artifacts.GetPath(existing.SourceDigest);
        var info = new FileInfo(path);
        EnsureBoundedSize(info.Length);
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        var digest = Digest(bytes);
        if (!digest.Equals(existing.SourceDigest, StringComparison.Ordinal))
            throw new InvalidDataException($"Stored source digest mismatch for document '{documentId}'.");

        return await ImportBytesAsync(
            existing.CorpusId,
            existing.DisplayName,
            existing.MediaType,
            bytes,
            ct,
            expectedParserId: existing.ParserId,
            expectedParserVersion: existing.ParserVersion).ConfigureAwait(false);
    }

    public bool DeleteCorpus(string corpusId) => _repository.DeleteCorpus(corpusId);

    private async Task<FabricImportResult> ImportBytesAsync(
        string corpusId,
        string displayName,
        string mediaType,
        byte[] source,
        CancellationToken ct,
        string? expectedParserId = null,
        string? expectedParserVersion = null)
    {
        EnsureBoundedSize(source.LongLength);
        var parser = expectedParserId is null
            ? _parsers.Resolve(mediaType)
            : _parsers.Resolve(expectedParserId, expectedParserVersion!);
        var parsed = parser.Parse(source, mediaType);
        var sourceDigest = Digest(source);
        var normalizedBytes = Encoding.UTF8.GetBytes(parsed.NormalizedText);
        var normalizedDigest = Digest(normalizedBytes);

        await StoreAsync(sourceDigest, source, ct).ConfigureAwait(false);
        await StoreAsync(normalizedDigest, normalizedBytes, ct).ConfigureAwait(false);

        var documentId = $"doc-{FabricHashing.Sha256($"{corpusId}|{sourceDigest}|{parsed.MediaType}|{parsed.ParserId}|{parsed.ParserVersion}")[..24]}";
        var segmenter = new FabricSegmenter(_options.EffectiveSegmenter);
        var segments = segmenter.Segment(documentId, parsed);
        var existing = _repository.GetDocument(documentId);
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            documentId,
            corpusId,
            sourceDigest,
            normalizedDigest,
            displayName,
            parsed.MediaType,
            parsed.ParserId,
            parsed.ParserVersion,
            "ready",
            parsed.Warnings,
            existing?.CreatedAt ?? now,
            now);
        _repository.ReplaceDocument(document, segments);

        return new FabricImportResult(
            _repository.GetDocument(documentId) ?? throw new InvalidOperationException("Imported document was not persisted."),
            _repository.GetSegments(documentId),
            existing is not null);
    }

    private async Task StoreAsync(string digest, byte[] bytes, CancellationToken ct)
    {
        if (_artifacts.Has(digest))
        {
            var existingDigest = await ContentAddressedStore
                .ComputeSha256Async(_artifacts.GetPath(digest), ct)
                .ConfigureAwait(false);
            if (!existingDigest.Equals(digest, StringComparison.Ordinal))
                throw new InvalidDataException($"Content-addressed object '{digest}' is corrupted.");
            return;
        }

        var offset = _artifacts.GetResumeOffset(digest);
        if (offset == bytes.LongLength)
        {
            _artifacts.Finalize(digest);
            return;
        }

        while (offset < bytes.LongLength)
        {
            var length = (int)Math.Min(ContentAddressedStore.MaxChunkBytes, bytes.LongLength - offset);
            await _artifacts.WriteChunkAsync(
                digest,
                offset,
                bytes.LongLength,
                bytes.AsMemory((int)offset, length),
                ct).ConfigureAwait(false);
            offset += length;
        }
    }

    private void EnsureBoundedSize(long length)
    {
        var maximum = Math.Min(_options.MaximumSourceBytes, int.MaxValue);
        if (length <= 0 || length > maximum)
            throw new InvalidDataException($"Document size must be between 1 and {maximum} bytes.");
    }

    private static string InferMediaType(string extension) => extension.ToLowerInvariant() switch
    {
        ".txt" => "text/plain",
        ".md" or ".markdown" => "text/markdown",
        ".pdf" => "application/pdf",
        _ => throw new NotSupportedException($"No Context Fabric media type is registered for '{extension}'."),
    };

    private static string Digest(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}
