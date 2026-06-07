using System.Diagnostics;
using System.Net.Http;

namespace OrchestratorSetup.Services;

/// <summary>
/// Handles the fully-automated Ollama installation path:
///   1. Download OllamaSetup.exe to a temp location.
///   2. Run it silently (the official installer is silent by default).
///   3. Poll http://localhost:11434 until the Ollama service responds.
///   4. Run <c>ollama pull {modelTag}</c> to download the selected model.
/// </summary>
public sealed class OllamaInstaller : IDisposable
{
    private const string OllamaDownloadUrl =
        "https://ollama.com/download/OllamaSetup.exe";

    private const int ServiceWaitSeconds  = 90;   // max wait for Ollama to start
    private const int PollIntervalSeconds =  3;

    private readonly DownloadService _dl;

    public event Action<string>? OnLog;
    public event Action<DownloadProgress>? OnProgress;

    public OllamaInstaller()
    {
        _dl = new DownloadService();
        _dl.OnProgress += p => OnProgress?.Invoke(p);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Full automated flow: download → silent install → wait → pull model.
    /// Throws <see cref="OperationCanceledException"/> if cancelled.
    /// Throws <see cref="Exception"/> with a user-readable message on failure.
    /// </summary>
    public async Task InstallAsync(string modelTag, CancellationToken ct = default)
    {
        var setupExe = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

        // ── 1. Download ───────────────────────────────────────────────────────
        Log($"  Downloading Ollama installer…");
        Log($"  URL : {OllamaDownloadUrl}");
        Log($"  Dest: {setupExe}");

        await _dl.DownloadFileAsync(
            OllamaDownloadUrl,
            setupExe,
            "OllamaSetup",
            null,   // size unknown
            null,   // no SHA-256
            ct);

        Log("  ✓ Download complete.");

        // ── 2. Silent install ─────────────────────────────────────────────────
        Log("  Running OllamaSetup.exe (silent)…");
        await RunSilentInstallAsync(setupExe, ct);
        Log("  ✓ Ollama installer finished.");

        // Clean up installer to save space
        try { File.Delete(setupExe); } catch { }

        // ── 3. Wait for service ───────────────────────────────────────────────
        Log($"  Waiting for Ollama service to start (up to {ServiceWaitSeconds}s)…");
        await WaitForServiceAsync(ct);
        Log("  ✓ Ollama service is running.");

        // ── 4. Pull model ─────────────────────────────────────────────────────
        Log($"  Pulling model: ollama pull {modelTag}");
        Log("  This may take a while depending on model size…");
        await RunOllamaPullAsync(modelTag, ct);
        Log($"  ✓ Model '{modelTag}' ready.");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task RunSilentInstallAsync(string exePath, CancellationToken ct)
    {
        // OllamaSetup.exe is an NSIS installer that accepts /S for silent mode.
        var psi = new ProcessStartInfo
        {
            FileName        = exePath,
            Arguments       = "/S",
            UseShellExecute = true,   // needed for elevated UAC prompt if required
            Verb            = "runas",
        };

        var proc = Process.Start(psi)
                   ?? throw new Exception("Failed to start OllamaSetup.exe");

        // Wait up to 3 minutes for installation to complete
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        await proc.WaitForExitAsync(linked.Token);

        if (proc.ExitCode != 0 && proc.ExitCode != 1)   // 1 = already installed / no-op
            throw new Exception($"OllamaSetup.exe exited with code {proc.ExitCode}.");
    }

    private static async Task WaitForServiceAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var deadline = DateTime.UtcNow.AddSeconds(ServiceWaitSeconds);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var resp = await http.GetAsync("http://localhost:11434/api/tags", ct);
                if (resp.IsSuccessStatusCode) return;
            }
            catch { /* still starting up */ }

            await Task.Delay(TimeSpan.FromSeconds(PollIntervalSeconds), ct);
        }

        throw new Exception(
            "Ollama service did not start within the expected time. " +
            "Try launching Ollama manually, then re-run the installer.");
    }

    private static async Task RunOllamaPullAsync(string modelTag, CancellationToken ct)
    {
        // Find ollama.exe — it's added to PATH by the installer, or in %LOCALAPPDATA%\Programs\Ollama
        var ollamaExe = FindOllamaExe()
                        ?? throw new Exception(
                            "ollama.exe not found. " +
                            "Ollama may not have installed correctly — try running 'ollama pull " +
                            modelTag + "' manually after setup completes.");

        var psi = new ProcessStartInfo
        {
            FileName               = ollamaExe,
            Arguments              = $"pull {modelTag}",
            UseShellExecute        = false,
            RedirectStandardOutput = false,
            RedirectStandardError  = false,
            CreateNoWindow         = false,   // show window so user can see pull progress
        };

        var proc = Process.Start(psi)
                   ?? throw new Exception("Failed to launch ollama.exe");

        // Model pulls can take a long time — no fixed timeout, but respect cancellation
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
            throw new Exception(
                $"'ollama pull {modelTag}' exited with code {proc.ExitCode}. " +
                "The model may not have downloaded fully — try running it manually.");
    }

    private static string? FindOllamaExe()
    {
        // 1. Check PATH via where.exe
        try
        {
            var result = Process.Start(new ProcessStartInfo
            {
                FileName               = "where",
                Arguments              = "ollama",
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                CreateNoWindow         = true,
            });
            if (result is not null)
            {
                var path = result.StandardOutput.ReadLine()?.Trim();
                result.WaitForExit();
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    return path;
            }
        }
        catch { }

        // 2. Default install location
        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Ollama", "ollama.exe");

        return File.Exists(candidate) ? candidate : null;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose() => _dl.Dispose();
}
