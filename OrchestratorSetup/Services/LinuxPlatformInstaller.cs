// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// INSTALLER_REVAMP_SPEC.md §7 Phase 4 — Linux implementation of <see cref="IPlatformInstaller"/>.
/// First non-Windows implementation; establishes the pattern Phase 5 (macOS) follows.
///
/// Deliberately out of scope here (matches §4.3's Linux bullet, which is firewall-only --
/// not a port of every Windows-specific HiveEnroller step): Windows' URL ACL reservation
/// (http.sys-specific; Linux has no equivalent restriction -- any user process can already
/// bind 0.0.0.0) and the OLLAMA_HOST persistent user environment variable (Windows persists
/// this via the registry through <see cref="Environment.SetEnvironmentVariable"/> with
/// <c>EnvironmentVariableTarget.User</c>; Linux/.NET has no OS-level equivalent -- doing this
/// properly means writing to ~/.profile or a systemd user environment.d file, which is a
/// separate, deliberately deferred piece of work, not silently skipped without note).
///
/// IMPORTANT — does NOT make an end-to-end Linux install possible yet (grok review BLOCKER,
/// 2026-06-21, confirmed by reading the surrounding pipeline rather than just the diff):
/// <see cref="LaunchCommand"/> and <see cref="CreateLaunchers"/> correctly target a
/// `OrchestratorIDE` binary with no extension (what a `dotnet publish -r linux-x64` of the
/// main app would actually produce), but every step that ACQUIRES that binary --
/// `InstallerState.AppExePath`/`PortableAppExePath` (hardcoded "OrchestratorIDE.exe"),
/// `InstallOrchestrator`'s download/copy logic, the single OS-unaware `app.download_url` key
/// in Setup/model-manifest.json, and `.github/workflows/release.yml` (win-x64-only, no Linux
/// publish job, no Linux release asset) -- is entirely Windows-only and untouched by this
/// phase. A real Linux install today would fail at the download step before ever reaching
/// this class's launcher/firewall/uninstall logic. Closing that gap is release-engineering
/// work on the main app (OrchestratorIDE.Avalonia), not something Phase 4 of the INSTALLER
/// revamp can complete alone -- intentionally left for a separate, explicitly scoped task.
/// </summary>
public sealed class LinuxPlatformInstaller : IPlatformInstaller
{
    private static readonly string XdgDataHome =
        Environment.GetEnvironmentVariable("XDG_DATA_HOME")
        is { Length: > 0 } xdgData
            ? xdgData
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");

    private static readonly string XdgConfigHome =
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
        is { Length: > 0 } xdgConfig
            ? xdgConfig
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

    // INSTALLER_REVAMP_SPEC.md §4.2.
    public string DefaultAppDir   => Path.Combine(XdgDataHome, "theorc");
    public string DefaultModelDir => Path.Combine(XdgDataHome, "theorc", "models");

    private static readonly string ManifestPath = Path.Combine(XdgDataHome, "theorc", ".install-manifest");
    private static readonly string DesktopFilePath = Path.Combine(
        XdgDataHome, "applications", "theorc.desktop");

    // ── Hardware detection ────────────────────────────────────────────────────
    // INSTALLER_REVAMP_SPEC.md §2.3/§4.1: nvidia-smi for NVIDIA (same tool, same query, as
    // HardwareDetector's Windows fallback path -- it's cross-platform, just under-used on
    // Windows where WMI is tried first), lspci for a general vendor string on AMD/Intel,
    // /proc/meminfo for system RAM. No CUDA-Toolkit-registry equivalent check (that's a
    // Windows-registry-specific signal); nvidia-smi's own banner line carries a CUDA Version
    // field, used here instead.

    public async Task<HardwareDetector.HardwareInfo> DetectHardwareAsync(
        IProgress<string>? log, CancellationToken ct)
    {
        string gpuName = "Unknown";
        long   vramBytes = 0;
        string vendor  = "none";
        string cuda    = "";
        long   ramGb   = 0;

        try
        {
            var (name, vram, cudaVersion) = await QueryNvidiaSmiAsync(log, ct);
            if (!string.IsNullOrEmpty(name))
            {
                gpuName = name;
                vramBytes = vram;
                vendor = "nvidia";
                cuda = cudaVersion;
                log?.Report($"  nvidia-smi: {name}");
            }
        }
        catch (Exception ex) { log?.Report($"nvidia-smi query failed: {ex.Message}"); }

        if (gpuName == "Unknown")
        {
            try
            {
                var lspciName = await QueryLspciGpuNameAsync(ct);
                if (!string.IsNullOrEmpty(lspciName))
                {
                    gpuName = lspciName;
                    vendor  = InferVendor(lspciName);
                    log?.Report($"  lspci: {lspciName}");
                }
            }
            catch (Exception ex) { log?.Report($"lspci query failed: {ex.Message}"); }
        }

        try { ramGb = await QueryMemTotalGbAsync(ct); }
        catch (Exception ex) { log?.Report($"/proc/meminfo read failed: {ex.Message}"); }

        // X86.Avx2.IsSupported is itself the cross-platform-safe check -- it's false (not a
        // throw) on non-x86 hardware like arm64, so this correctly falls through to "cpu"
        // there rather than needing a separate architecture guard (grok review MINOR,
        // 2026-06-21: flagged for review, confirmed correct rather than changed).
        bool hasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        int  vramGb  = vramBytes > 0 ? (int)(vramBytes / (1024L * 1024L * 1024L)) : 0;
        string variant = vendor switch
        {
            "nvidia" when cuda.StartsWith("12") => "cuda12",
            "nvidia" when cuda.StartsWith("11") => "cuda11",
            "nvidia" => "cuda11", // detected but version unknown -- safer pick, matches Windows PickVariant
            "amd" or "intel" => "vulkan",
            _ => hasAvx2 ? "avx2" : "cpu",
        };

        log?.Report($"AVX2: {hasAvx2}  |  Variant selected: {variant}");
        log?.Report("Detection complete.");

        return new HardwareDetector.HardwareInfo
        {
            GpuName        = gpuName,
            VramGb         = vramGb,
            Vendor         = vendor,
            CudaVersion    = cuda,
            RuntimeVariant = variant,
            SystemRamGb    = ramGb,
        };
    }

    private static string InferVendor(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("NVIDIA") || n.Contains("GEFORCE")) return "nvidia";
        if (n.Contains("AMD") || n.Contains("RADEON")) return "amd";
        if (n.Contains("INTEL")) return "intel";
        return "none";
    }

    private static async Task<(string Name, long VramBytes, string Cuda)> QueryNvidiaSmiAsync(
        IProgress<string>? log, CancellationToken ct)
    {
        var (output, _, _) = await RunAsync("nvidia-smi",
            "--query-gpu=name,memory.total --format=csv,noheader,nounits", ct);
        if (string.IsNullOrWhiteSpace(output)) return ("", 0, "");

        var parts = output.Trim().Split(',');
        string name = parts.Length >= 1 ? parts[0].Trim() : "";
        long vramBytes = 0;
        if (parts.Length >= 2 && long.TryParse(parts[1].Trim(), out long mib))
            vramBytes = mib * 1024L * 1024L;

        // The plain (non-query) banner output has a "CUDA Version: XX.X" field -- a second,
        // cheap call rather than trying to shoehorn it into the CSV query above.
        string cuda = "";
        try
        {
            var (banner, _, _) = await RunAsync("nvidia-smi", "", ct);
            var idx = banner.IndexOf("CUDA Version:", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var tail = banner[(idx + "CUDA Version:".Length)..].TrimStart();
                var end = tail.IndexOfAny([' ', '\n', '\r']);
                cuda = end > 0 ? tail[..end] : tail.Trim();
            }
        }
        catch { /* non-fatal -- name/VRAM already obtained above */ }

        return (name, vramBytes, cuda);
    }

    private static async Task<string> QueryLspciGpuNameAsync(CancellationToken ct)
    {
        var (output, _, _) = await RunAsync("lspci", "", ct);
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r'); // grok review MINOR, 2026-06-21: stray \r survives
                                               // a CRLF-terminated tool output otherwise.
            // Lines look like "01:00.0 VGA compatible controller: NVIDIA Corporation ..."
            if (line.Contains("VGA compatible controller", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("3D controller", StringComparison.OrdinalIgnoreCase))
            {
                var colon = line.IndexOf(':', line.IndexOf(':') + 1);
                if (colon > 0) return line[(colon + 1)..].Trim();
            }
        }
        return "";
    }

    private static async Task<long> QueryMemTotalGbAsync(CancellationToken ct)
    {
        const string path = "/proc/meminfo";
        if (!File.Exists(path)) return 0;
        var lines = await File.ReadAllLinesAsync(path, ct);
        foreach (var line in lines)
        {
            // "MemTotal:       16384000 kB"
            if (!line.StartsWith("MemTotal:", StringComparison.OrdinalIgnoreCase)) continue;
            var digits = new string(line.Where(char.IsDigit).ToArray());
            if (long.TryParse(digits, out var kb)) return kb / (1024L * 1024L);
        }
        return 0;
    }

    // ── Firewall ──────────────────────────────────────────────────────────────
    // INSTALLER_REVAMP_SPEC.md §4.3: detect ufw then firewalld; pkexec for elevation (the
    // desktop-Linux UAC equivalent), falling back to sudo if pkexec/a polkit agent isn't
    // present. Neither firewall present -> treat as "no active firewall" and succeed
    // silently, same as the spec's stated default for desktop Linux.

    private static readonly (string Name, int Port, string Proto)[] HivePorts =
    [
        ("Ollama",  HiveEnroller.OllamaPort,    "tcp"),
        ("Beacon",  HiveEnroller.HivePort,      "udp"),
        ("API",     HiveEnroller.HiveApiPort,   "tcp"),
        ("Queue",   HiveEnroller.TaskQueuePort, "tcp"),
        ("RPC",     HiveEnroller.RpcPort,       "tcp"),
    ];

    // appExePath is unused here -- ufw/firewalld open ports system-wide, not per-app, so
    // Linux's firewall step never needed a binary path (only macOS's per-app Application
    // Firewall does; see IPlatformInstaller.ConfigureFirewallAsync's doc comment).
    public async Task<bool> ConfigureFirewallAsync(string appExePath, Action<string>? log, CancellationToken ct)
    {
        if (await CommandExistsAsync("ufw", ct))
            return await ConfigureUfwAsync(log, ct);
        if (await CommandExistsAsync("firewall-cmd", ct))
            return await ConfigureFirewalldAsync(log, ct);

        log?.Invoke("  No ufw or firewalld detected -- assuming no active firewall on this host.");
        return true;
    }

    private async Task<bool> ConfigureUfwAsync(Action<string>? log, CancellationToken ct)
    {
        var elevate = await ResolveElevationCommandAsync(ct);
        if (elevate is null)
        {
            log?.Invoke("  No pkexec or sudo available -- cannot configure ufw automatically.");
            return false;
        }

        var ok = true;
        foreach (var (name, port, proto) in HivePorts)
        {
            // Elevation timeout (120s, not the 5s detection default) -- pkexec/sudo may show
            // an interactive password prompt, which is expected blocking, not a hang
            // (grok review BLOCKER, 2026-06-21: the original RunAsync had no timeout at all,
            // which froze HardwareDetectPage on a genuinely hung tool -- detection calls now
            // default to 5s, but elevation needs room for the user to actually type).
            var (_, error, code) = await RunAsync(elevate,
                $"ufw allow {port}/{proto}", ct, timeoutMs: 120_000);
            if (code == 0) log?.Invoke($"  ✓ ufw: allowed {port}/{proto} ({name})");
            else
            {
                log?.Invoke($"  ⚠ ufw: failed to allow {port}/{proto} ({name})" +
                    (string.IsNullOrWhiteSpace(error) ? "" : $" -- {error.Trim()}"));
                ok = false;
            }
        }
        return ok;
    }

    private async Task<bool> ConfigureFirewalldAsync(Action<string>? log, CancellationToken ct)
    {
        var elevate = await ResolveElevationCommandAsync(ct);
        if (elevate is null)
        {
            log?.Invoke("  No pkexec or sudo available -- cannot configure firewalld automatically.");
            return false;
        }

        var ok = true;
        foreach (var (name, port, proto) in HivePorts)
        {
            var (_, error, code) = await RunAsync(elevate,
                $"firewall-cmd --permanent --add-port={port}/{proto}", ct, timeoutMs: 120_000);
            if (code == 0) log?.Invoke($"  ✓ firewalld: allowed {port}/{proto} ({name})");
            else
            {
                log?.Invoke($"  ⚠ firewalld: failed to allow {port}/{proto} ({name})" +
                    (string.IsNullOrWhiteSpace(error) ? "" : $" -- {error.Trim()}"));
                ok = false;
            }
        }
        var (_, reloadError, reloadCode) = await RunAsync(
            elevate, "firewall-cmd --reload", ct, timeoutMs: 120_000);
        if (reloadCode != 0)
        {
            log?.Invoke("  ⚠ firewalld: reload failed" +
                (string.IsNullOrWhiteSpace(reloadError) ? "" : $" -- {reloadError.Trim()}"));
            ok = false;
        }
        return ok;
    }

    // No "Args" prefix here -- it was always empty (pkexec/sudo both take the target command
    // and its args verbatim, no flags needed), and concatenating an always-empty string ahead
    // of the real arguments produced a leading space that broke argv tokenization for the
    // elevated process (grok review BLOCKER, 2026-06-21: every ufw/firewall-cmd call silently
    // failed because of it). Just return the elevation executable name.
    private static async Task<string?> ResolveElevationCommandAsync(CancellationToken ct)
    {
        if (await CommandExistsAsync("pkexec", ct)) return "pkexec";
        if (await CommandExistsAsync("sudo", ct))    return "sudo";
        return null;
    }

    private static async Task<bool> CommandExistsAsync(string command, CancellationToken ct)
    {
        var (_, _, code) = await RunAsync("which", command, ct);
        return code == 0;
    }

    // ── Launchers ─────────────────────────────────────────────────────────────

    public void CreateLaunchers(InstallerState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DesktopFilePath)!);
            var exePath = LaunchCommand(state.AppInstallPath);
            File.WriteAllText(DesktopFilePath,
                $"""
                [Desktop Entry]
                Type=Application
                Name=TheOrc
                Comment=AI coding assistant
                Exec="{exePath}"
                Icon="{Path.Combine(state.AppInstallPath, "icon.png")}"
                Terminal=false
                Categories=Development;
                """);
            // chmod +x -- a .desktop file without the executable bit is shown by some file
            // managers as "untrusted" and refuses to launch on double-click.
            File.SetUnixFileMode(DesktopFilePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        catch { /* non-fatal -- matches Windows ProfileMerger.CreateShortcuts' own
                   best-effort framing (shortcuts are a convenience, not a hard requirement) */ }
    }

    // ── Uninstall registration / readback ────────────────────────────────────
    // No package-manager integration (apt/dpkg) -- this is a portable/manual install, not a
    // .deb (that kind of OS-native packaging is Open Question territory in the spec, not
    // Phase 4). A plain manifest file is this OS's equivalent of the Windows registry key:
    // just enough for ReadInstallPath to find its way back at uninstall time.

    public void RegisterUninstall(InstallerState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            File.WriteAllText(ManifestPath, state.AppInstallPath);
        }
        catch { /* non-fatal, same framing as Windows UninstallService.Register */ }
    }

    public string? ReadInstallPath()
    {
        try { return File.Exists(ManifestPath) ? File.ReadAllText(ManifestPath).Trim() : null; }
        catch { return null; }
    }

    public void Uninstall(string installPath, bool removeUserData, Action<string>? log)
    {
        void Log(string msg) => log?.Invoke(msg);

        Log("Removing application launcher…");
        TryDelete(DesktopFilePath);

        if (removeUserData)
        {
            Log("Removing user data…");
            // "OrchestratorIDE", not "theorc" -- this must match the folder name the MAIN APP
            // actually writes to (MainWindow.axaml.cs / SettingsPanel.axaml.cs both resolve
            // Environment.SpecialFolder.ApplicationData + "OrchestratorIDE", which .NET maps
            // to $XDG_CONFIG_HOME (~/.config) on Linux). "theorc" is this installer's OWN
            // branding choice for the install/model directories it owns (DefaultAppDir/
            // DefaultModelDir, per INSTALLER_REVAMP_SPEC.md §4.2) -- a deliberately different,
            // unrelated name. Using "theorc" here too (an earlier version of this line did)
            // silently no-ops on every real install, since that directory never exists --
            // found while researching the macOS equivalent, not by a review pass.
            TryDeleteDir(Path.Combine(XdgConfigHome, "OrchestratorIDE"));
        }

        Log("Removing install manifest…");
        TryDelete(ManifestPath);

        // Unlike Windows (where an open file handle on the running exe blocks deleting its
        // own directory, hence the delayed cmd.exe trick in UninstallService), Linux allows
        // unlinking a directory entry for a file a running process still holds open -- the
        // inode is freed once the process exits. No delay needed.
        if (!string.IsNullOrWhiteSpace(installPath) && Directory.Exists(installPath))
        {
            Log("Removing install directory…");
            TryDeleteDir(installPath);
        }

        Log("Done.");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }

    // ── Launch ────────────────────────────────────────────────────────────────

    public string LaunchCommand(string installPath) => Path.Combine(installPath, "OrchestratorIDE");

    // ── Process helper ────────────────────────────────────────────────────────

    /// <summary>
    /// Runs an external tool and returns stdout, stderr, and exit code.
    ///
    /// Reads both streams concurrently via Task.WhenAll, never one at a time -- a process
    /// that writes enough to stderr while only stdout is being awaited fills the OS pipe
    /// buffer and deadlocks, since the child then blocks writing and never reaches exit
    /// (classic .NET Process gotcha; grok review BLOCKER, 2026-06-21 on an earlier version
    /// of this method that only read StandardOutput despite redirecting StandardError too).
    ///
    /// <paramref name="timeoutMs"/> defaults to 5s, sized for the read-only detection probes
    /// (nvidia-smi/lspci/which/proc) that should never need more -- a hang there means a
    /// broken tool, not legitimate work, and HardwareDetectPage calls this with
    /// CancellationToken.None, so an internal bound is the only thing that can ever stop it
    /// (grok review BLOCKER, 2026-06-21: the original had no timeout at all). Elevation calls
    /// (pkexec/sudo) pass a much longer explicit timeout instead, since blocking on a password
    /// prompt there is expected, not a hang -- mirrors how Windows' UAC elevation also blocks.
    /// </summary>
    private static async Task<(string Output, string Error, int ExitCode)> RunAsync(
        string fileName, string arguments, CancellationToken ct, int timeoutMs = 5_000)
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

        using Process? proc = StartProcessOrNull(psi, out var startError);
        if (proc is null) return ("", startError, -1);

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
            try { proc.Kill(entireProcessTree: true); } catch { /* already exited */ }

            // Distinguish OUR timeout from the CALLER's own cancellation -- collapsing both
            // into a fake "timed out" error string hid genuine external cancellation (e.g. the
            // user closing the wizard mid-detection) behind a misleading message instead of
            // propagating it (grok review BLOCKER, 2026-06-21).
            if (ct.IsCancellationRequested) throw;
            return ("", $"'{fileName}' timed out after {timeoutMs}ms", -1);
        }

        return (outTask.Result, errTask.Result, proc.ExitCode);
    }

    /// <summary>
    /// Process.Start throws (Win32Exception) when fileName doesn't resolve on PATH -- e.g. a
    /// minimal/container Linux image without `which` itself, or without ufw/firewall-cmd/
    /// pkexec/sudo. CommandExistsAsync's whole purpose is to degrade gracefully in exactly
    /// that case, so RunAsync must return a failure tuple, not let the exception propagate
    /// (grok review BLOCKER, 2026-06-21: an uncaught throw here faulted ConfigureFirewallAsync's
    /// CommandExistsAsync("ufw"/"firewall-cmd") probes instead of falling through to "no
    /// firewall detected").
    /// </summary>
    private static Process? StartProcessOrNull(ProcessStartInfo psi, out string error)
    {
        try { error = ""; return Process.Start(psi); }
        catch (Exception ex) { error = ex.Message; return null; }
    }
}
