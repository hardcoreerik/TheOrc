// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: web-find import — search, candidate selection, download, import.
// Drop into OrchestratorIDE/Services/ContextFabric/
// POLICY: only call DownloadAsync for URLs the user explicitly selects.
//         Display the license/attribution note before downloading.
//         Never auto-download without user confirmation.

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record WebImportCandidate(
    string Url,
    string Title,
    string Format,       // "pdf" | "txt" | "md"
    long EstimatedBytes,
    string Attribution); // e.g. "Project Gutenberg · Public Domain"

public sealed record WebImportResult(
    FabricImportResult ImportResult,
    string SourceUrl,
    string DownloadedDigest);

public sealed class FabricWebImporter(
    FabricLibraryService libraryService,
    HttpClient httpClient)
{
    // Max download size: 50 MB. Mirrors FabricLibraryOptions.MaximumSourceBytes default.
    private const long MaxDownloadBytes = 50 * 1024 * 1024;

    // Search for downloadable copies. Uses the chat model's web_search tool if available,
    // or falls back to a simple Gutenberg/Archive.org URL pattern for known authors.
    // In the real impl, this method should call OrcChatToolCatalog.web_search via a
    // one-shot ChatEngine call, parse the results, and return structured candidates.
    public Task<IReadOnlyList<WebImportCandidate>> SearchAsync(
        string query,
        CancellationToken ct = default)
    {
        // TODO: implement via one-shot ChatEngine call with the web_search tool.
        // Return a list of WebImportCandidate items. Show these to the user for selection.
        // Never download automatically — require explicit user confirmation.
        throw new NotImplementedException("Web search integration pending CF-5 implementation.");
    }

    public async Task<WebImportResult> DownloadAndImportAsync(
        string corpusId,
        WebImportCandidate candidate,
        CancellationToken ct = default)
    {
        // 1. Stream download with size gate
        using var response = await httpClient.GetAsync(
            candidate.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength ?? 0;
        if (contentLength > MaxDownloadBytes)
            throw new InvalidDataException($"Remote file exceeds {MaxDownloadBytes / 1024 / 1024} MB limit.");

        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var memStream = new MemoryStream();
        var buf = new byte[65536];
        long total = 0;
        int read;
        while ((read = await stream.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidDataException("Download exceeded size limit mid-stream.");
            memStream.Write(buf, 0, read);
        }

        // 2. Write to temp file, then import via FabricLibraryService
        var ext = candidate.Format switch { "pdf" => ".pdf", "md" => ".md", _ => ".txt" };
        var tmpPath = Path.Combine(Path.GetTempPath(), $"orc-webimport-{Guid.NewGuid():N}{ext}");
        try
        {
            await File.WriteAllBytesAsync(tmpPath, memStream.ToArray(), ct).ConfigureAwait(false);
            var importResult = await libraryService.ImportFileAsync(corpusId, tmpPath, ct: ct).ConfigureAwait(false);
            var digest = importResult.Document.SourceDigest;
            return new WebImportResult(importResult, candidate.Url, digest);
        }
        finally
        {
            if (File.Exists(tmpPath)) File.Delete(tmpPath);
        }
    }
}
