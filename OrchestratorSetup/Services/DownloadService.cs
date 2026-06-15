// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OrchestratorSetup.Services;

/// <summary>
/// Resilient HTTP downloader with:
///   • Range-header resume   — resumes interrupted downloads from where they left off
///   • SHA-256 streaming     — verifies integrity without a second pass over the file
///   • Exponential backoff   — retries transient failures up to <see cref="MaxRetries"/> times
///   • Progress events       — fires <see cref="OnProgress"/> on the calling context (use
///                             Dispatcher.Invoke in WPF event handlers)
///
/// Thread safety: each <see cref="DownloadFileAsync"/> call is independent. The single
/// internal HttpClient is shared across calls but is safe for concurrent use.
/// </summary>
public sealed class DownloadService : IDisposable
{
    // ── Configuration ─────────────────────────────────────────────────────────

    public int    MaxRetries        { get; set; } = 3;
    public int    ChunkBytes        { get; set; } = 81_920;  // 80 KB
    public int    SpeedWindowMs     { get; set; } = 800;     // speed averaged over ~1 s

    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fired roughly every <see cref="SpeedWindowMs"/> ms and on completion / error.
    /// The event fires on the async continuation thread — marshal to UI if needed.
    /// </summary>
    public event Action<DownloadProgress>? OnProgress;

    // ── Internals ─────────────────────────────────────────────────────────────

    private readonly HttpClient _http;

    public DownloadService()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.None, // do NOT decompress — we're streaming raw bytes
        })
        {
            Timeout = Timeout.InfiniteTimeSpan, // long-running download; per-chunk timeout managed via CT
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OrchestratorSetup/1.0");
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="destPath"/>.
    /// If a <c>.partial</c> file exists from a prior interrupted download it will be resumed.
    /// </summary>
    /// <param name="itemName">Display name shown in progress events.</param>
    /// <param name="expectedTotalBytes">
    ///   Known file size from the manifest (used when the server omits Content-Length).
    /// </param>
    /// <param name="expectedSha256">
    ///   Lowercase hex SHA-256. When non-null the download is verified and
    ///   <see cref="InvalidDataException"/> is thrown on mismatch.
    /// </param>
    public async Task DownloadFileAsync(
        string            url,
        string            destPath,
        string            itemName,
        long?             expectedTotalBytes = null,
        string?           expectedSha256     = null,
        CancellationToken ct                 = default)
    {
        // Already done from a previous run?
        if (File.Exists(destPath))
        {
            if (expectedSha256 is null || await VerifySha256Async(destPath, expectedSha256, ct))
            {
                Fire(new DownloadProgress
                {
                    ItemName      = itemName,
                    BytesReceived = new FileInfo(destPath).Length,
                    TotalBytes    = new FileInfo(destPath).Length,
                    IsComplete    = true,
                });
                return;
            }
            // Hash mismatch on existing file — delete and re-download
            File.Delete(destPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        int attempt = 0;
        while (true)
        {
            try
            {
                await AttemptDownloadAsync(url, destPath, itemName, expectedTotalBytes, expectedSha256, ct);
                return; // success
            }
            catch (OperationCanceledException) { throw; }
            catch (InvalidDataException)       { throw; } // SHA mismatch — don't retry
            catch (Exception ex)
            {
                attempt++;
                if (attempt > MaxRetries)
                {
                    Fire(new DownloadProgress { ItemName = itemName, Error = ex.Message });
                    throw;
                }

                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt)); // 2s, 4s, 8s
                Fire(new DownloadProgress
                {
                    ItemName      = itemName,
                    BytesReceived = GetPartialBytes(destPath),
                    TotalBytes    = expectedTotalBytes ?? 0,
                    Error         = $"Retry {attempt}/{MaxRetries} after {ex.Message.Truncate(80)} (waiting {delay.TotalSeconds:F0}s…)",
                });

                await Task.Delay(delay, ct);
            }
        }
    }

    // ── Core download attempt ─────────────────────────────────────────────────

    private async Task AttemptDownloadAsync(
        string            url,
        string            destPath,
        string            itemName,
        long?             knownTotal,
        string?           expectedSha256,
        CancellationToken ct)
    {
        var partialPath = destPath + ".partial";
        long existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existingBytes > 0)
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        // 416 = Range Not Satisfiable → server doesn't support resume; start fresh
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            existingBytes = 0;
            if (File.Exists(partialPath)) File.Delete(partialPath);
            using var req2 = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp2 = await _http.SendAsync(req2, HttpCompletionOption.ResponseHeadersRead, ct);
            resp2.EnsureSuccessStatusCode();
            await StreamToFileAsync(resp2, partialPath, destPath, itemName,
                                    0, knownTotal, expectedSha256, ct);
            return;
        }

        response.EnsureSuccessStatusCode();

        // Determine if the server honoured the Range request
        bool isPartial = response.StatusCode == HttpStatusCode.PartialContent;
        if (!isPartial) existingBytes = 0; // server sent full file

        long serverReported = response.Content.Headers.ContentLength ?? 0;
        long totalBytes = isPartial
            ? existingBytes + serverReported
            : serverReported > 0 ? serverReported : (knownTotal ?? 0);

        await StreamToFileAsync(response, partialPath, destPath, itemName,
                                existingBytes, totalBytes > 0 ? totalBytes : knownTotal,
                                expectedSha256, ct, isPartial);
    }

    private async Task StreamToFileAsync(
        HttpResponseMessage resp,
        string              partialPath,
        string              destPath,
        string              itemName,
        long                existingBytes,
        long?               totalBytes,
        string?             expectedSha256,
        CancellationToken   ct,
        bool                appending = false)
    {
        using var sha = expectedSha256 is not null ? SHA256.Create() : null;

        // If resuming and hashing, process the already-downloaded bytes first
        if (appending && existingBytes > 0 && sha is not null && File.Exists(partialPath))
        {
            using var existing = new FileStream(partialPath, FileMode.Open, FileAccess.Read,
                                                FileShare.None, ChunkBytes, true);
            var buf = new byte[ChunkBytes];
            int n;
            while ((n = await existing.ReadAsync(buf, ct)) > 0)
                sha.TransformBlock(buf, 0, n, null, 0);
        }

        await using var fs = new FileStream(
            partialPath,
            appending && existingBytes > 0 ? FileMode.Append : FileMode.Create,
            FileAccess.Write, FileShare.None, ChunkBytes, useAsync: true);

        await using var content = await resp.Content.ReadAsStreamAsync(ct);

        var buffer       = new byte[ChunkBytes];
        long received    = existingBytes;
        long windowBytes = 0;
        var  sw          = Stopwatch.StartNew();
        double speed     = 0;
        var   lastFire   = DateTime.UtcNow;

        while (true)
        {
            int bytesRead = await content.ReadAsync(buffer, ct);
            if (bytesRead == 0) break;

            await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
            sha?.TransformBlock(buffer, 0, bytesRead, null, 0);

            received     += bytesRead;
            windowBytes  += bytesRead;

            // Update speed and fire progress at ~SpeedWindowMs cadence
            if (sw.ElapsedMilliseconds >= SpeedWindowMs)
            {
                speed        = windowBytes / sw.Elapsed.TotalSeconds;
                windowBytes  = 0;
                sw.Restart();

                Fire(new DownloadProgress
                {
                    ItemName         = itemName,
                    BytesReceived    = received,
                    TotalBytes       = totalBytes ?? 0,
                    SpeedBytesPerSec = speed,
                });
            }
        }

        await fs.FlushAsync(ct);

        // Verify SHA-256
        if (sha is not null && expectedSha256 is not null)
        {
            sha.TransformFinalBlock([], 0, 0);
            var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                fs.Close();
                File.Delete(partialPath);
                throw new InvalidDataException(
                    $"SHA-256 mismatch for {Path.GetFileName(destPath)}.\n" +
                    $"  Expected: {expectedSha256}\n  Got:      {actual}");
            }
        }

        fs.Close();

        // Atomically rename .partial → final
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(partialPath, destPath);

        Fire(new DownloadProgress
        {
            ItemName         = itemName,
            BytesReceived    = received,
            TotalBytes       = totalBytes ?? received,
            SpeedBytesPerSec = 0,
            IsComplete       = true,
        });
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<bool> VerifySha256Async(string path, string expected, CancellationToken ct)
    {
        try
        {
            using var sha = SHA256.Create();
            await using var fs = File.OpenRead(path);
            var hash = await sha.ComputeHashAsync(fs, ct);
            return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static long GetPartialBytes(string destPath)
    {
        var partial = destPath + ".partial";
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    private void Fire(DownloadProgress p) => OnProgress?.Invoke(p);

    public void Dispose() => _http.Dispose();
}

file static class StringEx
{
    public static string Truncate(this string s, int max)
        => s.Length <= max ? s : s[..max] + "…";
}
