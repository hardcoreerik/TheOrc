// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Models;

/// <summary>
/// Downloads a GGUF model file, optionally verifies SHA-256, then
/// auto-registers it with Ollama (if running) and updates AppSettings.
/// </summary>
public sealed class ModelDownloadService : IDisposable
{
    private readonly HttpClient _http;

    public ModelDownloadService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromHours(4) };
        _http.DefaultRequestHeaders.Add("User-Agent", "OrchestratorIDE/1.0");
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads the model file from <paramref name="url"/> to <paramref name="destPath"/>,
    /// reporting progress via <paramref name="onProgress"/>. Automatically retries on
    /// transient network failures (connection drops, read timeouts), resuming from the
    /// partial file each time via the existing Range-request support below -- a multi-GB
    /// GGUF download is exactly the case where flaky wifi shouldn't mean starting over, or
    /// silently failing without the user noticing until they come back to a stalled bar.
    /// Does not retry on user-initiated cancellation (<paramref name="ct"/>) or a SHA-256
    /// mismatch (caller's responsibility, not transient).
    ///
    /// Progress tuple: (bytesDownloaded, totalBytes, speedBytesPerSec, etaSeconds)
    /// </summary>
    public async Task DownloadAsync(
        string url,
        string destPath,
        IProgress<(long done, long total, double speed, int eta)>? onProgress = null,
        CancellationToken ct = default,
        int maxRetries = 3,
        IProgress<string>? onRetry = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DownloadOnceAsync(url, destPath, onProgress, ct);
                return;
            }
            catch (Exception ex) when (
                attempt < maxRetries &&
                !ct.IsCancellationRequested &&
                ex is HttpRequestException or IOException or TaskCanceledException)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s
                onRetry?.Report(
                    $"Connection issue ({ex.Message}) -- retrying in {delay.TotalSeconds:F0}s " +
                    $"({attempt + 1}/{maxRetries})…");
                await Task.Delay(delay, ct);
            }
        }
    }

    private async Task DownloadOnceAsync(
        string url,
        string destPath,
        IProgress<(long done, long total, double speed, int eta)>? onProgress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Support resuming a partial download
        var startOffset = 0L;
        var done = 0L;
        if (File.Exists(destPath))
        {
            var existing = new FileInfo(destPath).Length;
            if (existing > 0)
            {
                startOffset = existing;
                done = existing;
            }
        }

        using var response = await SendDownloadRequestAsync(url, startOffset, ct);
        response.EnsureSuccessStatusCode();

        if (startOffset > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            startOffset = 0;
            done = 0;
        }

        var total = response.Content.Headers.ContentRange?.Length
            ?? (response.Content.Headers.ContentLength is long contentLength ? contentLength + startOffset : 0L);
        var sw = Stopwatch.StartNew();
        var buffer = new byte[81_920];
        var lastReport = sw.ElapsedMilliseconds;

        var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
        await using var dest = new FileStream(destPath, fileMode, FileAccess.Write, FileShare.None);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;

            await dest.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;

            var now = sw.ElapsedMilliseconds;
            if (now - lastReport >= 250 && onProgress is not null)
            {
                var elapsed = sw.Elapsed.TotalSeconds;
                var speed   = elapsed > 0 ? done / elapsed : 0;
                var eta     = speed > 0 && total > 0 ? (int)((total - done) / speed) : 0;
                onProgress.Report((done, total, speed, eta));
                lastReport = now;
            }
        }

        onProgress?.Report((done, total, 0, 0));
    }

    // ── SHA-256 verify ────────────────────────────────────────────────────────

    public async Task<bool> VerifySha256Async(
        string filePath, string expectedHex, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return true;   // no hash to check
        await using var fs = File.OpenRead(filePath);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash).Equals(expectedHex, StringComparison.OrdinalIgnoreCase);
    }

    // ── Ollama registration ───────────────────────────────────────────────────

    /// <summary>
    /// Creates a Modelfile pointing at the downloaded GGUF and runs
    /// <c>ollama create {modelName} -f {modelfile}</c>.
    /// Returns true on success, false if Ollama is not installed or the command fails.
    /// </summary>
    public async Task<bool> RegisterWithOllamaAsync(
        string ggufPath, string modelName,
        IProgress<string>? onLog = null, CancellationToken ct = default)
    {
        if (!IsOllamaAvailable())
        {
            onLog?.Report("Ollama not found — skipping registration.");
            return false;
        }

        var modelfilePath = Path.Combine(Path.GetTempPath(), $"Modelfile_{modelName}.tmp");
        await File.WriteAllTextAsync(modelfilePath,
            $"FROM \"{ggufPath.Replace("\\", "/")}\"\n", ct);

        onLog?.Report($"Registering with Ollama as '{modelName}'…");

        var result = await RunOllamaAsync(
            $"create \"{modelName}\" -f \"{modelfilePath}\"", onLog, ct);

        try { File.Delete(modelfilePath); } catch { }

        if (result) onLog?.Report($"✓ Ollama model '{modelName}' ready.");
        else        onLog?.Report("⚠ Ollama registration failed — model still usable via llama.cpp.");

        return result;
    }

    // ── AppSettings update ────────────────────────────────────────────────────

    /// <summary>
    /// Applies the newly downloaded model to AppSettings for the given role.
    /// Role: "worker", "boss", or "researcher".
    /// </summary>
    public static void ApplyToSettings(
        AppSettings settings, string modelIdentifier, string role)
    {
        switch (role.ToLowerInvariant())
        {
            case "worker":     settings.LastWorkerModel     = modelIdentifier; break;
            case "boss":       settings.LastSwarmModel      = modelIdentifier; break;
            case "researcher": settings.LastResearcherModel = modelIdentifier; break;
            default:
                // No role assigned — update DefaultModel if nothing is set
                if (string.IsNullOrEmpty(settings.DefaultModel))
                    settings.DefaultModel = modelIdentifier;
                break;
        }
        settings.Save();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsOllamaAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("ollama", "version")
            {
                RedirectStandardOutput = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            p?.WaitForExit(3000);
            return p?.ExitCode == 0;
        }
        catch { return false; }
    }

    private static async Task<bool> RunOllamaAsync(
        string args, IProgress<string>? onLog, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo("ollama", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi)!;

            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) onLog?.Report(e.Data); };
            proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) onLog?.Report(e.Data); };
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            await proc.WaitForExitAsync(ct);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(
        string url, long startOffset, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (startOffset > 0)
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(startOffset, null);

        var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (startOffset > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            response.Dispose();
            return await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }

        return response;
    }

    public void Dispose() => _http.Dispose();
}
