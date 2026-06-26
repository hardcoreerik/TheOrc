// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using OrchestratorSetup.Models;

namespace OrchestratorSetup.Services;

/// <summary>
/// INSTALLER_REVAMP_SPEC.md §7 Phase 5 — macOS implementation of <see cref="IPlatformInstaller"/>.
/// Mirrors <see cref="LinuxPlatformInstaller"/>'s structure and, more importantly, its bug
/// fixes -- that class took four grok-review rounds to land clean (stdout/stderr pipe
/// deadlock, no timeout, uncaught Process.Start throw, a leading-space argv bug, a
/// cancellation/timeout conflation); this one applies every one of those fixes from the
/// start instead of re-discovering them.
///
/// HISTORICAL NOTE, now closed -- left for context, not as a current caveat: when this class
/// was first written (Phase 5), the gap below was real: the app-binary download and the
/// llama.cpp runtime resolver were both Windows-only, so nothing downstream of
/// <see cref="DetectHardwareAsync"/> could turn its result into a real install. That gap was
/// closed by `MULTI_OS_RELEASE_SPEC.md` Phases A-D (2026-06-21): `release.yml` publishes a
/// macOS leg, `Setup/model-manifest.json`'s `app` key is OS-keyed and
/// `InstallOrchestrator.ResolveAppUrl`/`InstallerState.AppExePath` resolve through
/// `PlatformInstaller.Current` instead of a hardcoded `.exe`, and <see cref="LlamaCppResolver"/>
/// is OS+arch-aware with a real `metal` variant verified against llama.cpp's actual release
/// assets. An end-to-end macOS install is code-complete as of that work -- it has been
/// cross-compiled and reviewed but, per `MULTI_OS_RELEASE_SPEC.md` Phase F, never actually run
/// on real Mac hardware. Don't reintroduce a hardcoded Windows assumption in either pipeline
/// without re-reading those phases first.
/// </summary>
public sealed class MacPlatformInstaller : IPlatformInstaller
{
    private static readonly string AppSupportDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support");

    // INSTALLER_REVAMP_SPEC.md §4.2 -- "TheOrc", this installer's own branding choice for the
    // directories it owns, deliberately distinct from "OrchestratorIDE" (see RemoveUserData's
    // comment below) -- same naming split Phase 4 uses for Linux.
    public string DefaultAppDir   => Path.Combine(AppSupportDir, "TheOrc");
    public string DefaultModelDir => Path.Combine(AppSupportDir, "TheOrc", "Models");

    private static readonly string ManifestPath =
        Path.Combine(AppSupportDir, "TheOrc", ".install-manifest");

    // No .app bundle yet (INSTALLER_REVAMP_SPEC.md §6: ".app bundling is a packaging
    // follow-up... not a blocker for a runnable installer") -- a symlink to the raw binary
    // under ~/Applications is the pragmatic stand-in, same role .desktop plays on Linux.
    private static readonly string LauncherLinkPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications", "TheOrc");

    // ── Hardware detection ────────────────────────────────────────────────────
    // system_profiler for GPU name (and VRAM on Intel Macs with discrete graphics --
    // Apple Silicon has unified memory, no separate VRAM figure to report), uname -m for
    // architecture, sysctl for system RAM.

    public async Task<HardwareDetector.HardwareInfo> DetectHardwareAsync(
        IProgress<string>? log, CancellationToken ct)
    {
        string gpuName = "Unknown";
        int    vramGb  = 0;
        string vendor  = "none";
        long   ramGb   = 0;

        bool isAppleSilicon = false;
        try
        {
            var (arch, _, _) = await RunAsync("uname", "-m", ct);
            isAppleSilicon = arch.Trim().Equals("arm64", StringComparison.OrdinalIgnoreCase);
            log?.Report($"  Architecture: {arch.Trim()}");
        }
        catch (Exception ex) { log?.Report($"uname query failed: {ex.Message}"); }

        try
        {
            var (name, vram) = await QuerySystemProfilerGpuAsync(ct);
            if (!string.IsNullOrEmpty(name))
            {
                gpuName = name;
                vramGb  = vram;
                vendor  = isAppleSilicon ? "apple" : InferVendor(name);
                log?.Report($"  system_profiler: {name}");
            }
        }
        catch (Exception ex) { log?.Report($"system_profiler query failed: {ex.Message}"); }

        try { ramGb = await QuerySysctlMemGbAsync(ct); }
        catch (Exception ex) { log?.Report($"sysctl query failed: {ex.Message}"); }

        // Metal is macOS's GPU compute API -- it's been near-universal across both Apple
        // Silicon and Intel Mac GPUs since macOS 10.14, so any detected GPU (or Apple Silicon
        // itself, which always has one) gets "metal". X86.Avx2.IsSupported is the
        // cross-platform-safe check (false, not a throw, on arm64) for the Intel-Mac-without-
        // a-usable-GPU fallback case.
        bool hasAvx2 = System.Runtime.Intrinsics.X86.Avx2.IsSupported;
        string variant = (isAppleSilicon || vendor is "amd" or "intel" or "apple")
            ? "metal"
            : hasAvx2 ? "avx2" : "cpu";

        log?.Report($"AVX2: {hasAvx2}  |  Apple Silicon: {isAppleSilicon}  |  Variant selected: {variant}");
        log?.Report("Detection complete.");

        return new HardwareDetector.HardwareInfo
        {
            GpuName        = gpuName,
            VramGb         = vramGb,
            Vendor         = vendor,
            CudaVersion    = "", // macOS has had no NVIDIA/CUDA support since 10.13.
            RuntimeVariant = variant,
            SystemRamGb    = ramGb,
        };
    }

    private static string InferVendor(string name)
    {
        var n = name.ToUpperInvariant();
        if (n.Contains("AMD") || n.Contains("RADEON")) return "amd";
        if (n.Contains("INTEL")) return "intel";
        if (n.Contains("APPLE")) return "apple";
        return "none";
    }

    private static async Task<(string Name, int VramGb)> QuerySystemProfilerGpuAsync(CancellationToken ct)
    {
        var (output, _, _) = await RunAsync("system_profiler", "SPDisplaysDataType", ct);
        string name = "";
        int    vramGb = 0;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.Trim();
            // "Chipset Model: Apple M2 Pro" / "VRAM (Total): 8 GB" / "VRAM (Dynamic, Max): 1536 MB"
            if (line.StartsWith("Chipset Model:", StringComparison.OrdinalIgnoreCase) && name == "")
            {
                name = line["Chipset Model:".Length..].Trim();
            }
            else if (line.StartsWith("VRAM", StringComparison.OrdinalIgnoreCase) && line.Contains(':'))
            {
                var value = line[(line.IndexOf(':') + 1)..].Trim();
                var digits = new string(value.Where(c => char.IsDigit(c) || c == '.').ToArray());
                if (double.TryParse(digits, out var num))
                    vramGb = value.Contains("MB", StringComparison.OrdinalIgnoreCase)
                        ? (int)(num / 1024) : (int)num;
            }
        }
        return (name, vramGb);
    }

    private static async Task<long> QuerySysctlMemGbAsync(CancellationToken ct)
    {
        var (output, _, _) = await RunAsync("sysctl", "-n hw.memsize", ct);
        return long.TryParse(output.Trim(), out var bytes) ? bytes / (1024L * 1024L * 1024L) : 0;
    }

    // ── Firewall ──────────────────────────────────────────────────────────────
    // INSTALLER_REVAMP_SPEC.md §4.3: macOS's Application Firewall is per-app, not per-port --
    // there's nothing to "open" unless it's on, and then it's the app binary that gets
    // allow-listed, not a port number. If the firewall is off, there's nothing to do.
    //
    // Elevation: raw `sudo` from a GUI app with no attached terminal just fails (sudo needs a
    // TTY or an askpass helper, neither of which exists here) -- this is the macOS-native
    // equivalent of the UAC/pkexec elevation prompt, not a missing TTY bug. `osascript ...
    // with administrator privileges` is what actually shows the native authentication dialog
    // from a GUI context, so that's used here instead of sudo directly.

    public async Task<bool> ConfigureFirewallAsync(string appExePath, Action<string>? log, CancellationToken ct)
    {
        var (state, _, code) = await RunAsync(
            "/usr/libexec/ApplicationFirewall/socketfilterfw", "--getglobalstate", ct);
        if (code != 0)
        {
            log?.Invoke("  Could not query the Application Firewall state -- assuming it's off.");
            return true;
        }
        if (!state.Contains("enabled", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("  Application Firewall is off -- nothing to configure.");
            return true;
        }

        if (string.IsNullOrWhiteSpace(appExePath) || !File.Exists(appExePath))
        {
            log?.Invoke($"  Firewall is on, but no app binary found at '{appExePath}' to allow-list yet.");
            return false;
        }

        // Two layers of quoting here, fixed in the same order they'd actually break (grok
        // review BLOCKER, 2026-06-21, on an earlier version that got both wrong):
        //   1. The shell command itself (passed to `do shell script`) needs appExePath
        //      single-quoted for the shell, with any embedded `'` escaped to `'\''` --
        //      otherwise a path containing an apostrophe breaks the inner shell command
        //      (grok MINOR, same pass).
        //   2. The AppleScript `do shell script "..."` string needs that shell command's own
        //      `"` escaped to `\"` so AppleScript's string literal doesn't end early.
        //   3. The OUTER process invocation (osascript -e <script>) must go through
        //      ArgumentList, not a flat Arguments string -- a flat string gets re-tokenized
        //      on whitespace/quotes by .NET's argv parser, which mangled the embedded quotes
        //      from steps 1-2 and made `-e`'s script argument arrive corrupted (same root
        //      cause as the Linux elevation-args leading-space bug from Phase 4, different
        //      shape). RunAsyncWithArgList below passes args as discrete argv entries instead.
        var quotedPath = ShellQuoteSingle(appExePath);
        var shellCommand = $"/usr/libexec/ApplicationFirewall/socketfilterfw --add {quotedPath} && " +
                            $"/usr/libexec/ApplicationFirewall/socketfilterfw --unblockapp {quotedPath}";
        var script = $"do shell script \"{shellCommand.Replace("\"", "\\\"")}\" with administrator privileges";

        var (_, error, scriptCode) = await RunAsyncWithArgList(
            "osascript", ["-e", script], ct, timeoutMs: 120_000);
        if (scriptCode == 0)
        {
            log?.Invoke("  ✓ Application Firewall: allow-listed the app binary.");
            return true;
        }

        log?.Invoke("  ⚠ Application Firewall: could not allow-list the app binary" +
            (string.IsNullOrWhiteSpace(error) ? "" : $" -- {error.Trim()}"));
        return false;
    }

    private static string ShellQuoteSingle(string s) => "'" + s.Replace("'", "'\\''") + "'";

    // ── Launchers ─────────────────────────────────────────────────────────────

    public void CreateLaunchers(InstallerState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LauncherLinkPath)!);
            // File.Delete throws on a real directory (not a symlink) at this path -- that's
            // not a hypothetical, ~/Applications/TheOrc is exactly the kind of place a user
            // could have a plain folder lying around. Without this, the outer best-effort
            // catch below would swallow that throw and silently never create a launcher again
            // on any subsequent repair-install (grok review MINOR, 2026-06-21).
            if (Directory.Exists(LauncherLinkPath) && !File.Exists(LauncherLinkPath))
                Directory.Delete(LauncherLinkPath, recursive: true);
            else if (File.Exists(LauncherLinkPath))
                File.Delete(LauncherLinkPath);
            File.CreateSymbolicLink(LauncherLinkPath, LaunchCommand(state.AppInstallPath));
        }
        catch { /* non-fatal -- matches Windows ProfileMerger.CreateShortcuts' own
                   best-effort framing (shortcuts are a convenience, not a hard requirement) */ }
    }

    // ── Uninstall registration / readback ────────────────────────────────────
    // No .pkg/installer-receipt integration -- this is a portable/manual install, not a
    // signed .pkg (that kind of OS-native packaging is Open Question territory in the spec).
    // A plain manifest file is this OS's equivalent of the Windows registry key.

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
        TryDelete(LauncherLinkPath);

        if (removeUserData)
        {
            Log("Removing user data…");
            // "OrchestratorIDE", not "TheOrc" -- must match the folder name the MAIN APP
            // actually writes to (MainWindow.axaml.cs / SettingsPanel.axaml.cs both resolve
            // Environment.SpecialFolder.ApplicationData + "OrchestratorIDE", which .NET maps
            // to ~/Library/Application Support on macOS, same root as DefaultAppDir/"TheOrc"
            // but a different leaf folder). Got this wrong on Linux's first pass (fixed
            // 2026-06-21, same session) -- applying that fix here from the start.
            TryDeleteDir(Path.Combine(AppSupportDir, "OrchestratorIDE"));
        }

        Log("Removing install manifest…");
        TryDelete(ManifestPath);

        // Like Linux (unlike Windows), macOS allows unlinking a directory entry for a binary
        // a running process still holds open -- no delayed-deletion trick needed.
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
    // Identical to LinuxPlatformInstaller.RunAsync -- see that class's XML doc for the full
    // reasoning (concurrent stdout/stderr read to avoid a pipe deadlock, internal timeout
    // since HardwareDetectPage calls DetectHardwareAsync with CancellationToken.None,
    // Process.Start wrapped so a missing tool degrades instead of throwing, and the
    // OperationCanceledException handler distinguishes its own timeout from real external
    // cancellation). Not shared as a common base/helper between the two classes: Phase 4 and
    // Phase 5 are reviewed independently and neither Linux nor macOS has a third sibling yet
    // to justify the abstraction -- duplication here is cheaper than a premature shared base.

    private static Task<(string Output, string Error, int ExitCode)> RunAsync(
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
        return RunAsync(psi, ct, timeoutMs);
    }

    /// <summary>
    /// Same as <see cref="RunAsync(string, string, CancellationToken, int)"/> but passes
    /// each argument as a discrete argv entry via <see cref="ProcessStartInfo.ArgumentList"/>
    /// instead of one flat string -- needed whenever an argument itself contains quotes or
    /// other characters .NET's flat-Arguments tokenizer would re-split on (grok review
    /// BLOCKER, 2026-06-21: ConfigureFirewallAsync's `osascript -e &lt;applescript-with-
    /// embedded-quotes&gt;` call got silently corrupted by exactly that re-tokenization when
    /// built as a flat string).
    /// </summary>
    private static Task<(string Output, string Error, int ExitCode)> RunAsyncWithArgList(
        string fileName, IEnumerable<string> arguments, CancellationToken ct, int timeoutMs = 5_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = fileName,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        foreach (var arg in arguments) psi.ArgumentList.Add(arg);
        return RunAsync(psi, ct, timeoutMs);
    }

    private static async Task<(string Output, string Error, int ExitCode)> RunAsync(
        ProcessStartInfo psi, CancellationToken ct, int timeoutMs)
    {
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
            if (ct.IsCancellationRequested) throw;
            return ("", $"'{psi.FileName}' timed out after {timeoutMs}ms", -1);
        }

        return (outTask.Result, errTask.Result, proc.ExitCode);
    }

    private static Process? StartProcessOrNull(ProcessStartInfo psi, out string error)
    {
        try { error = ""; return Process.Start(psi); }
        catch (Exception ex) { error = ex.Message; return null; }
    }
}
