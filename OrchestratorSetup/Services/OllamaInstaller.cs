// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;

namespace OrchestratorSetup.Services;

/// <summary>
/// Handles the fully-automated Ollama installation path.
///
/// Windows: download OllamaSetup.exe → run it silently (/S, NSIS default) → poll
/// http://localhost:11434 → <c>ollama pull {modelTag}</c>.
///
/// Linux/macOS (added MULTI_OS_RELEASE_SPEC.md Phase E, 2026-06-22): run Ollama's own
/// official install script (<c>curl -fsSL https://ollama.com/install.sh | sh</c>) elevated,
/// then the same poll-and-pull steps. Verified against the script's actual current content
/// (not assumed) before writing this: on Linux it requires root for nearly everything
/// (systemd service, a dedicated `ollama` system user, /usr/local/bin) and checks `id -u`
/// itself to decide whether it even needs its own internal `sudo` calls; on macOS it tries
/// `/usr/local/bin` unprivileged first and only falls back to `sudo` if that fails. Running
/// the WHOLE script already-elevated (pkexec/osascript, same pattern
/// LinuxPlatformInstaller/MacPlatformInstaller already use for firewall rules) means the
/// script's own root check passes immediately and it never needs to prompt internally --
/// which matters because this process has no TTY for an interactive sudo password prompt to
/// use, and the script makes several separate `$SUDO` calls throughout its body, not one
/// upfront sudo a credential cache would obviously cover.
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
    /// Full automated flow: download/run installer → wait → pull model.
    /// Throws <see cref="OperationCanceledException"/> if cancelled.
    /// Throws <see cref="Exception"/> with a user-readable message on failure.
    /// </summary>
    public async Task InstallAsync(string modelTag, CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows())
            await InstallWindowsAsync(ct);
        else
            await InstallUnixAsync(ct);

        // ── Wait for service (OS-agnostic -- just an HTTP poll) ─────────────────
        Log($"  Waiting for Ollama service to start (up to {ServiceWaitSeconds}s)…");
        await WaitForServiceAsync(ct);
        Log("  ✓ Ollama service is running.");

        // ── Pull model (OS-agnostic -- FindOllamaExe resolves the right binary) ─
        Log($"  Pulling model: ollama pull {modelTag}");
        Log("  This may take a while depending on model size…");
        await RunOllamaPullAsync(modelTag, ct);
        Log($"  ✓ Model '{modelTag}' ready.");
    }

    // ── Windows install path (unchanged behavior) ───────────────────────────────

    private async Task InstallWindowsAsync(CancellationToken ct)
    {
        var setupExe = Path.Combine(Path.GetTempPath(), "OllamaSetup.exe");

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

        Log("  Running OllamaSetup.exe (silent)…");
        await RunSilentInstallAsync(setupExe, ct);
        Log("  ✓ Ollama installer finished.");

        try { File.Delete(setupExe); } catch { }
    }

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

        using var proc = Process.Start(psi)
                   ?? throw new Exception("Failed to start OllamaSetup.exe");

        // Wait up to 3 minutes for installation to complete
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);

        // Without the Kill on OCE, cancelling the installer (or hitting the 3-minute timeout)
        // would leave OllamaSetup.exe running as an orphan instead of actually stopping it --
        // same fix RunOllamaPullAsync already has, applied here too for consistency (grok
        // review MINOR, 2026-06-22).
        try
        {
            await proc.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0 && proc.ExitCode != 1)   // 1 = already installed / no-op
            throw new Exception($"OllamaSetup.exe exited with code {proc.ExitCode}.");
    }

    // ── Linux/macOS install path ─────────────────────────────────────────────────

    private async Task InstallUnixAsync(CancellationToken ct)
    {
        Log("  Running Ollama's official install script (elevated, may show a password prompt)…");
        Log("  curl -fsSL https://ollama.com/install.sh | sh");

        const string shellCommand = "curl -fsSL https://ollama.com/install.sh | sh";

        var ok = OperatingSystem.IsMacOS()
            ? await RunElevatedMacAsync(shellCommand, ct)
            : await RunElevatedLinuxAsync(shellCommand, ct);

        if (!ok)
            throw new Exception(
                "Ollama's install script failed or elevation was declined. " +
                "Install manually: curl -fsSL https://ollama.com/install.sh | sh");

        Log("  ✓ Ollama installer finished.");
    }

    /// <summary>
    /// Same pkexec-then-sudo preference as LinuxPlatformInstaller.ResolveElevationCommandAsync
    /// -- pkexec shows the desktop polkit dialog (the Linux UAC equivalent); sudo is the
    /// fallback for boxes without a polkit agent (e.g. a headless server).
    /// </summary>
    private async Task<bool> RunElevatedLinuxAsync(string shellCommand, CancellationToken ct)
    {
        var elevateExe = await CommandExistsAsync("pkexec", ct) ? "pkexec"
                        : await CommandExistsAsync("sudo", ct)   ? "sudo"
                        : null;
        if (elevateExe is null) return false;

        // sh -c "<command>" as the elevated target -- pkexec/sudo both take the program to
        // run plus ITS OWN arguments verbatim, no flags of their own needed here.
        var (_, error, code) = await RunAsync(elevateExe, $"sh -c \"{shellCommand}\"", ct,
            timeoutMs: 180_000); // generous: the script itself downloads + may compile nothing,
                                  // but does install a systemd service and could be slow on a
                                  // loaded box; this is also where an interactive password
                                  // prompt's wait time lives.
        if (code != 0)
        {
            Log($"  ⚠ install.sh exited {code}" + (string.IsNullOrWhiteSpace(error) ? "" : $": {error.Trim()}"));
            return false;
        }
        return true;
    }

    /// <summary>
    /// Same osascript-over-raw-sudo reasoning as MacPlatformInstaller.ConfigureFirewallAsync:
    /// this process has no TTY for sudo's own password prompt, but `do shell script ... with
    /// administrator privileges` shows the native macOS auth dialog instead. Passed via
    /// ArgumentList (not a flat Arguments string) for the same reason that class's firewall
    /// call was -- a flat string gets re-tokenized by .NET's argv parser, which corrupts
    /// embedded quotes (grok review BLOCKER on that class, 2026-06-21; applying the lesson
    /// here from the start instead of re-discovering it).
    /// </summary>
    private async Task<bool> RunElevatedMacAsync(string shellCommand, CancellationToken ct)
    {
        // Escape for the AppleScript string literal the shell command sits inside.
        var escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var script  = $"do shell script \"{escaped}\" with administrator privileges";

        var psi = new ProcessStartInfo
        {
            FileName               = "osascript",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        var (_, error, code) = await RunAsyncCore(psi, ct, timeoutMs: 180_000);
        if (code != 0)
        {
            Log($"  ⚠ install.sh exited {code}" + (string.IsNullOrWhiteSpace(error) ? "" : $": {error.Trim()}"));
            return false;
        }
        return true;
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken ct)
    {
        var (_, _, code) = await RunAsync("which", command, ct, timeoutMs: 5_000);
        return code == 0;
    }

    // ── Shared process helpers ───────────────────────────────────────────────────

    private static Task<(string Output, string Error, int ExitCode)> RunAsync(
        string fileName, string arguments, CancellationToken ct, int timeoutMs)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            Arguments              = arguments,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        return RunAsyncCore(psi, ct, timeoutMs);
    }

    /// <summary>
    /// Reads stdout AND stderr concurrently via Task.WhenAll -- a process that writes enough
    /// to stderr while only stdout is being awaited fills the OS pipe buffer and deadlocks
    /// (same fix already applied in LinuxPlatformInstaller.RunAsync/MacPlatformInstaller.RunAsync
    /// during INSTALLER_REVAMP_SPEC.md Phases 4-5; applying it here from the start).
    /// </summary>
    private static async Task<(string Output, string Error, int ExitCode)> RunAsyncCore(
        ProcessStartInfo psi, CancellationToken ct, int timeoutMs)
    {
        Process? proc;
        try { proc = Process.Start(psi); }
        catch (Exception ex) { return ("", ex.Message, -1); }
        if (proc is null) return ("", "", -1);

        using (proc)
        {
            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            var outTask = proc.StandardOutput.ReadToEndAsync(linked.Token);
            var errTask = proc.StandardError.ReadToEndAsync(linked.Token);

            try
            {
                await Task.WhenAll(outTask, errTask);
                await proc.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested) throw;
                return ("", $"'{psi.FileName}' timed out after {timeoutMs}ms", -1);
            }

            return (outTask.Result, errTask.Result, proc.ExitCode);
        }
    }

    // ── Shared: wait for service + pull model ────────────────────────────────────

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
        // FindOllamaExe is OS-aware internally -- "ollama.exe" via where.exe/%LOCALAPPDATA%
        // on Windows, plain "ollama" via which/`/usr/local/bin/ollama` elsewhere. Everything
        // below (ProcessStartInfo, exit-code check) is already OS-agnostic: CreateNoWindow
        // is simply a no-op on platforms with no window concept, not something that needed
        // branching.
        var ollamaExe = FindOllamaExe()
                        ?? throw new Exception(
                            "ollama not found. " +
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

        using var proc = Process.Start(psi)
                   ?? throw new Exception("Failed to launch ollama.");

        // Model pulls can take a long time — no fixed timeout, but respect cancellation.
        // Without the explicit Kill on OCE, cancelling the installer would leave `ollama
        // pull` running as an orphaned background process instead of actually stopping the
        // download (grok review MINOR, 2026-06-22).
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        if (proc.ExitCode != 0)
            throw new Exception(
                $"'ollama pull {modelTag}' exited with code {proc.ExitCode}. " +
                "The model may not have downloaded fully — try running it manually.");
    }

    private static string? FindOllamaExe()
    {
        if (OperatingSystem.IsWindows())
        {
            // 1. Check PATH via where.exe
            try
            {
                using var result = Process.Start(new ProcessStartInfo
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

        // Linux/macOS: install.sh symlinks the binary into /usr/local/bin, which is
        // virtually always already on PATH for an interactively-launched GUI app.
        try
        {
            using var result = Process.Start(new ProcessStartInfo
            {
                FileName               = "which",
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

        const string fallback = "/usr/local/bin/ollama";
        return File.Exists(fallback) ? fallback : null;
    }

    private void Log(string msg) => OnLog?.Invoke(msg);

    public void Dispose() => _dl.Dispose();
}
