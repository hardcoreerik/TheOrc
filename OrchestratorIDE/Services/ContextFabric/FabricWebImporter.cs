// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: web-find import — search, candidate selection, download, import.
// POLICY: only call DownloadAndImportAsync for URLs the user explicitly selects.
//         Display the license/attribution note before downloading.
//         Never auto-download without user confirmation.

using OrchestratorIDE.Research;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record WebImportCandidate(
    string Url,
    string Title,
    string Format,       // "pdf" | "txt" | "md"
    long EstimatedBytes,
    string Attribution);

public sealed record WebImportResult(
    FabricImportResult ImportResult,
    string SourceUrl,
    string DownloadedDigest);

public sealed class FabricWebImporter(
    FabricLibraryService libraryService,
    HttpClient httpClient,
    WebSearchTool? webSearch = null)
{
    // Max download size: 50 MB. Mirrors FabricLibraryOptions.MaximumSourceBytes default.
    private const long MaxDownloadBytes = 50 * 1024 * 1024;
    private const int MaxSearchResults = 12;

    private readonly WebSearchTool _webSearch = webSearch ?? new WebSearchTool();

    // The current parser registry only handles .txt/.md/.pdf (no EPUB/DOCX/OCR — out of
    // scope for this slice), so candidates whose URL doesn't resolve to one of those
    // extensions are filtered out rather than offered as a dead-end "Add to library".
    public async Task<IReadOnlyList<WebImportCandidate>> SearchAsync(
        string query,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var hits = await _webSearch.SearchAsync(query, MaxSearchResults, ct).ConfigureAwait(false);

        var candidates = new List<WebImportCandidate>();
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.Url))
                continue;

            var format = InferSupportedFormat(hit.Url);
            if (format is null)
                continue;

            candidates.Add(new WebImportCandidate(
                hit.Url,
                string.IsNullOrWhiteSpace(hit.Title) ? hit.Url : hit.Title,
                format,
                EstimatedBytes: 0,
                Attribution: InferAttribution(hit.Url)));
        }

        return candidates;
    }

    public async Task<WebImportResult> DownloadAndImportAsync(
        string corpusId,
        WebImportCandidate candidate,
        CancellationToken ct = default)
    {
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

    private static string? InferSupportedFormat(string url)
    {
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) return "pdf";
        if (path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "md";
        if (path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return "txt";
        return null;
    }

    private static string InferAttribution(string url)
    {
        try
        {
            var host = new Uri(url).Host;
            return host.Contains("gutenberg", StringComparison.OrdinalIgnoreCase)
                ? "Project Gutenberg · Public Domain"
                : host;
        }
        catch
        {
            return "Unknown source";
        }
    }
}
