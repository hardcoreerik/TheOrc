// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace OrchestratorIDE.Services;

/// <summary>
/// Orchestrates the full "update from GitHub source" flow:
///   1. Verify / install .NET 10 SDK
///   2. Clone or pull source
///   3. dotnet publish (self-contained, single-file)
///   4. Write relaunch PowerShell script
///   5. Launch script hidden, then caller shuts down the process
///
/// Long operations accept IProgress&lt;string&gt; for live UI log output.
/// Create a fresh instance per update attempt.
/// </summary>
public sealed class SelfUpdater
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RepoUrl            = "https://github.com/hardcoreerik/TheOrc.git";
    private const string DotnetInstallPsUrl = "https://dot.net/v1/dotnet-install.ps1";
    private const string DotnetInstallShUrl = "https://dot.net/v1/dotnet-install.sh";
    private const string RequiredSdkMajor   = "10";

    // ~/.dotnet is the dotnet-install.sh script's own documented per-user default location
    // (no -InstallDir override needed) -- unlike Windows' $env:ProgramFiles\dotnet, which IS
    // a system-wide location, matching the difference in how each platform conventionally
    // handles a per-user SDK install. Verified against the script's actual content (curl'd
    // and read, not assumed) before writing this: dotnet-install.sh needs NO sudo/elevation
    // at all -- it's a pure user-space download+extract, unlike Ollama's install.sh.
    private static readonly string DotnetUnixInstallDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");

    // ── Configuration ─────────────────────────────────────────────────────────

    public CancellationToken CancellationToken { get; set; } = default;

    // ── .NET SDK ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if a .NET 10+ SDK is available -- on PATH, or (Linux/macOS) at the
    /// per-user install location InstallDotNetSdkAsync uses below.
    /// </summary>
    public async Task<bool> CheckDotNetSdkAsync()
    {
        try
        {
            var output = await CaptureAsync(ResolveDotnetExe(), "--version", null, TimeSpan.FromSeconds(10));
            var major  = output.Trim().Split('.')[0];
            return major == RequiredSdkMajor;
        }
        catch { return false; }
    }

    /// <summary>
    /// "dotnet" if it's on PATH; otherwise (Linux/macOS only) the per-user location
    /// InstallDotNetSdkAsync's Unix branch installs to -- a PATH update made by a child
    /// script never propagates back to THIS already-running process, so a JUST-installed SDK
    /// would otherwise be invisible to the rest of this same update flow (BuildAndPublishAsync
    /// included) without this fallback.
    /// </summary>
    private static string ResolveDotnetExe()
    {
        if (OperatingSystem.IsWindows()) return "dotnet";
        var candidate = Path.Combine(DotnetUnixInstallDir, "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    /// <summary>
    /// Downloads and runs the official dotnet-install script to install the .NET 10 SDK --
    /// dotnet-install.ps1 (Windows, system-wide under $env:ProgramFiles) or dotnet-install.sh
    /// (Linux/macOS, per-user under ~/.dotnet, matching that script's own documented default).
    /// Requires internet access.
    ///
    /// Verified the Unix script's actual content (curl'd and read, not assumed) before using
    /// it: unlike Ollama's install.sh, dotnet-install.sh needs NO sudo/elevation at all -- a
    /// pure user-space download+extract, since the SDK doesn't touch any system-wide state.
    /// Invoked via "sh &lt;script&gt; ..." rather than exec'ing the script path directly, so
    /// this doesn't depend on the chmod below having succeeded (same lesson already applied
    /// to PrepareRelaunchUnix's script invocation).
    /// </summary>
    public async Task InstallDotNetSdkAsync(IProgress<string> progress)
    {
        if (OperatingSystem.IsWindows())
        {
            progress.Report("Downloading dotnet-install.ps1…");

            var tempScript = Path.Combine(Path.GetTempPath(), "dotnet-install.ps1");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var text = await http.GetStringAsync(DotnetInstallPsUrl, CancellationToken);
                await File.WriteAllTextAsync(tempScript, text, CancellationToken);
            }

            progress.Report("Installing .NET 10 SDK — this takes 1–3 minutes…");

            var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\" " +
                       $"-Channel 10.0 -InstallDir \"$env:ProgramFiles\\dotnet\"";
            await StreamAsync("powershell.exe", args, null, progress, TimeSpan.FromMinutes(10));
        }
        else
        {
            progress.Report("Downloading dotnet-install.sh…");

            var tempScript = Path.Combine(Path.GetTempPath(), "dotnet-install.sh");
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                var text = await http.GetStringAsync(DotnetInstallShUrl, CancellationToken);
                await File.WriteAllTextAsync(tempScript, text, CancellationToken);
            }

            progress.Report("Installing .NET 10 SDK — this takes 1–3 minutes…");

            var psi = new ProcessStartInfo
            {
                FileName        = "sh",
                UseShellExecute = false,
                CreateNoWindow  = true,
            };
            psi.ArgumentList.Add(tempScript);
            psi.ArgumentList.Add("--channel");
            psi.ArgumentList.Add("10.0");
            psi.ArgumentList.Add("--install-dir");
            psi.ArgumentList.Add(DotnetUnixInstallDir);
            await StreamAsync(psi, progress, TimeSpan.FromMinutes(10));
        }

        progress.Report("✓ .NET 10 SDK installed.");
    }

    // ── Source ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Clones the repo into <paramref name="sourceDir"/> or does git pull if it exists.
    /// </summary>
    public async Task PullSourceAsync(string sourceDir, IProgress<string> progress)
    {
        Directory.CreateDirectory(sourceDir);

        var hasGit = Directory.Exists(Path.Combine(sourceDir, ".git"));
        if (hasGit)
        {
            progress.Report("Pulling latest from GitHub/main…");
            await StreamAsync("git", "pull", sourceDir, progress, TimeSpan.FromMinutes(3));
        }
        else
        {
            progress.Report($"Cloning {RepoUrl}…");
            await StreamAsync("git", $"clone {RepoUrl} .", sourceDir, progress, TimeSpan.FromMinutes(10));
        }

        progress.Report("✓ Source up to date.");
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs dotnet publish on the main Avalonia project, producing a self-contained
    /// single-file exe for the current OS/architecture in <paramref name="outputDir"/>.
    /// </summary>
    public async Task BuildAndPublishAsync(string sourceDir, string outputDir, IProgress<string> progress)
    {
        var projPath = Path.Combine(sourceDir, "OrchestratorIDE.Avalonia", "OrchestratorIDE.Avalonia.csproj");
        if (!File.Exists(projPath))
            throw new FileNotFoundException($"Project not found at: {projPath}");

        Directory.CreateDirectory(outputDir);

        progress.Report("Building TheOrc — this may take a minute…");

        var rid = ResolveRid();
        // AssemblyName pinned to OrchestratorIDE (not the project's own OrchestratorIDE.Avalonia)
        // so the output binary name matches what UpdatePanel/HiveNodeServer hardcode when they
        // look for the staged exe afterward -- same convention release.yml uses for the
        // GitHub-release build. OutputType=WinExe only on Windows avoids a flashing console
        // window there; non-Windows has no such concern.
        var args = $"publish \"{projPath}\" " +
                   $"-c Release -r {rid} --self-contained true " +
                   $"-p:PublishSingleFile=true " +
                   $"-p:AssemblyName=OrchestratorIDE " +
                   (OperatingSystem.IsWindows() ? "-p:OutputType=WinExe " : "") +
                   $"-o \"{outputDir}\"";

        // ResolveDotnetExe(), not a bare "dotnet" -- a PATH update from InstallDotNetSdkAsync's
        // child script never propagates back to this already-running process, so a
        // just-installed Unix SDK at ~/.dotnet would otherwise be invisible here.
        await StreamAsync(ResolveDotnetExe(), args, sourceDir, progress, TimeSpan.FromMinutes(15));

        if (!OperatingSystem.IsWindows())
        {
            var builtExe = Path.Combine(outputDir, "OrchestratorIDE");
            if (File.Exists(builtExe))
            {
                // dotnet publish output carries no exec bit -- same fix as
                // DownloadReleaseAsync's equivalent step below, otherwise the build "succeeds"
                // but PrepareRelaunch's cp/exec step fails with permission denied. Best-effort,
                // same as the download path: surfaces later as "permission denied" on launch,
                // which is at least diagnosable.
                try
                {
                    File.SetUnixFileMode(builtExe,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch { /* best-effort, see comment above */ }
            }
        }

        progress.Report("✓ Build complete.");
    }

    /// <summary>
    /// Maps the running OS/architecture to a dotnet publish RID, matching the pattern in
    /// OrchestratorSetup's LlamaCppResolver/InstallOrchestrator — this build-from-source
    /// fallback must produce a binary for the machine it's actually running on, not always win-x64.
    /// </summary>
    private static string ResolveRid()
    {
        // Was "anything that isn't Arm64 is x64" -- silently wrong for x86/Arm32, which would
        // either fail or (worse) produce a binary for the wrong architecture rather than fail
        // loudly (grok review MINOR, 2026-06-22). Listing only the architectures this app
        // actually publishes for elsewhere (release.yml, INSTALLER_REVAMP_SPEC.md) and
        // throwing clearly for anything else.
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            var other => throw new PlatformNotSupportedException(
                $"SelfUpdater has no RID mapping for architecture '{other}'."),
        };
        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        throw new PlatformNotSupportedException("SelfUpdater has no RID mapping for this OS.");
    }

    // ── Download pre-built release asset ─────────────────────────────────────

    /// <summary>
    /// Downloads a pre-built exe from a GitHub release asset URL into
    /// <paramref name="stagingDir"/> and returns the full path.
    /// Returns null if the download fails.
    /// </summary>
    public async Task<string?> DownloadReleaseAsync(
        string assetUrl, string stagingDir, IProgress<string> progress)
    {
        if (string.IsNullOrEmpty(assetUrl)) return null;

        Directory.CreateDirectory(stagingDir);
        // UpdateChecker.GetReleaseAssetUrlAsync now only ever returns an asset URL matching
        // THIS OS (grok review BLOCKER, 2026-06-21, on that file -- a Mac client used to get
        // handed the Windows .exe's URL unconditionally), so the staged file's own name
        // should match too rather than relabeling a Mac binary as ".exe" once downloaded.
        var destFileName = OperatingSystem.IsWindows() ? "OrchestratorIDE.exe" : "OrchestratorIDE";
        var destPath = Path.Combine(stagingDir, destFileName);

        progress.Report("Downloading pre-built release from GitHub…");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TheOrc/updater");

        using var resp = await http.GetAsync(
            assetUrl, HttpCompletionOption.ResponseHeadersRead, CancellationToken);
        resp.EnsureSuccessStatusCode();

        var total    = resp.Content.Headers.ContentLength;
        using var src  = await resp.Content.ReadAsStreamAsync(CancellationToken);
        using var dest = File.Create(destPath);

        var buf        = new byte[65536];
        long downloaded = 0;
        int  read;
        var sw         = Stopwatch.StartNew();
        var lastReport = 0L;
        while ((read = await src.ReadAsync(buf, CancellationToken)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read), CancellationToken);
            downloaded += read;
            if (total.HasValue && total > 0 && sw.ElapsedMilliseconds - lastReport >= 200)
            {
                lastReport = sw.ElapsedMilliseconds;
                progress.Report($"  {downloaded / 1024:N0} KB / {total / 1024:N0} KB");
            }
        }

        // Close the handle before touching the file's permissions below -- dest is a
        // method-scoped `using var`, so without this explicit Dispose() it would stay open
        // through the SetUnixFileMode call.
        dest.Dispose();

        // Raw HTTP downloads carry no file-mode metadata -- without this, the downloaded
        // binary would be non-executable on Linux/macOS even though the update "succeeded"
        // (same class of bug as InstallOrchestrator.EnsureExecutable, grok review BLOCKER,
        // 2026-06-21, on the install-time path; this is the self-update path's equivalent).
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(destPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch { /* best-effort -- surfaces later as "permission denied" on launch, which
                       is at least diagnosable */ }
        }

        progress.Report($"✓ Downloaded to {destPath}");
        return destPath;
    }

    // ── Relaunch ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes _orc_update.ps1 (Windows) or _orc_update.sh (macOS/Linux) next to the running
    /// exe, then launches it hidden.
    ///
    /// The script:
    ///   1. Waits for this process to exit (up to 30 s)
    ///   2. Copies the new exe over the old one
    ///   3. Deletes the staging directory
    ///   4. Starts the new exe
    ///   5. Self-deletes the script
    ///
    /// Caller must call Application.Current.Shutdown() after this returns.
    /// </summary>
    public void PrepareRelaunch(string stagedExePath)
    {
        var actualExe = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? Assembly.GetExecutingAssembly().Location;

        var installDir = Path.GetDirectoryName(actualExe);
        var stagingDir  = Path.GetDirectoryName(stagedExePath);
        if (string.IsNullOrEmpty(installDir) || string.IsNullOrEmpty(stagingDir))
            throw new InvalidOperationException(
                $"Could not resolve directories for relaunch (exe='{actualExe}', staged='{stagedExePath}').");

        if (OperatingSystem.IsWindows())
        {
            PrepareRelaunchWindows(actualExe, stagedExePath, installDir, stagingDir);
        }
        else
        {
            PrepareRelaunchUnix(actualExe, stagedExePath, installDir, stagingDir);
        }
    }

    private static void PrepareRelaunchWindows(
        string actualExe, string stagedExePath, string installDir, string stagingDir)
    {
        var scriptPath = Path.Combine(installDir, "_orc_update.ps1");

        var ps = new StringBuilder();
        ps.AppendLine($"$orcPid    = {Environment.ProcessId}");
        ps.AppendLine($"$oldExe    = '{EscPs(actualExe)}'");
        ps.AppendLine($"$newExe    = '{EscPs(stagedExePath)}'");
        ps.AppendLine($"$stagingDir = '{EscPs(stagingDir)}'");
        ps.AppendLine();
        ps.AppendLine("# Wait for the current process to exit (up to 30s)");
        ps.AppendLine("$sw = [System.Diagnostics.Stopwatch]::StartNew()");
        ps.AppendLine("while ($true) {");
        ps.AppendLine("    $proc = Get-Process -Id $orcPid -ErrorAction SilentlyContinue");
        ps.AppendLine("    if ($proc -eq $null) { break }");
        ps.AppendLine("    if ($sw.Elapsed.TotalSeconds -gt 30) { break }");
        ps.AppendLine("    Start-Sleep -Milliseconds 400");
        ps.AppendLine("}");
        ps.AppendLine("Start-Sleep -Milliseconds 800");
        ps.AppendLine();
        ps.AppendLine("# Overwrite the running exe with the new build");
        ps.AppendLine("Copy-Item -Path $newExe -Destination $oldExe -Force");
        ps.AppendLine();
        ps.AppendLine("# Remove staging area");
        ps.AppendLine("Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue");
        ps.AppendLine();
        ps.AppendLine("# Launch the updated exe");
        ps.AppendLine("Start-Process -FilePath $oldExe");
        ps.AppendLine();
        ps.AppendLine("# Self-delete");
        ps.AppendLine("Start-Sleep -Milliseconds 500");
        ps.AppendLine("Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue");

        File.WriteAllText(scriptPath, ps.ToString(), new UTF8Encoding(false));

        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        })
            ?? throw new InvalidOperationException("Failed to launch the relaunch script (powershell.exe).");
    }

    private static void PrepareRelaunchUnix(
        string actualExe, string stagedExePath, string installDir, string stagingDir)
    {
        // Guard satisfies the CA1416 platform-compat analyzer for SetUnixFileMode below --
        // callers already only reach this on non-Windows, but the analyzer can't see across
        // the OperatingSystem.IsWindows() branch in the public PrepareRelaunch method.
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("PrepareRelaunchUnix called on Windows.");

        var scriptPath = Path.Combine(installDir, "_orc_update.sh");

        var sh = new StringBuilder();
        sh.AppendLine("#!/bin/sh");
        sh.AppendLine($"ORC_PID={Environment.ProcessId}");
        sh.AppendLine($"OLD_EXE='{EscSh(actualExe)}'");
        sh.AppendLine($"NEW_EXE='{EscSh(stagedExePath)}'");
        sh.AppendLine($"STAGING_DIR='{EscSh(stagingDir)}'");
        sh.AppendLine();
        sh.AppendLine("# Wait for the current process to exit (up to 30s)");
        sh.AppendLine("i=0");
        sh.AppendLine("while kill -0 \"$ORC_PID\" 2>/dev/null; do");
        sh.AppendLine("    i=$((i + 1))");
        sh.AppendLine("    if [ \"$i\" -gt 75 ]; then break; fi");
        sh.AppendLine("    sleep 0.4");
        sh.AppendLine("done");
        sh.AppendLine("sleep 0.8");
        sh.AppendLine();
        sh.AppendLine("# Overwrite the running exe with the new build");
        sh.AppendLine("cp -f \"$NEW_EXE\" \"$OLD_EXE\"");
        sh.AppendLine("chmod +x \"$OLD_EXE\"");
        sh.AppendLine();
        sh.AppendLine("# Remove staging area");
        sh.AppendLine("rm -rf \"$STAGING_DIR\"");
        sh.AppendLine();
        sh.AppendLine("# Launch the updated exe");
        sh.AppendLine("nohup \"$OLD_EXE\" >/dev/null 2>&1 &");
        sh.AppendLine();
        sh.AppendLine("# Self-delete");
        sh.AppendLine("sleep 0.5");
        sh.AppendLine("rm -f \"$0\"");

        File.WriteAllText(scriptPath, sh.ToString(), new UTF8Encoding(false));
        try
        {
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        // Unlike the other two SetUnixFileMode call sites in this file, this one used to be a
        // hard dependency: the invocation below directly exec'd the script path, which needs
        // both the shebang line AND the executable bit to work. Swallowing this the same
        // "best-effort" way those two do would have converted a loud failure now into a silent
        // "permission denied" failure later when nohup tries to exec a non-executable file.
        // Fixed at the actual dependency instead (invoke via `sh` explicitly below, which reads
        // and interprets the file regardless of its executable bit) -- so this catch is now
        // genuinely best-effort, matching the other two (grok review MINOR, 2026-06-22).
        catch { /* best-effort -- see comment above; the sh-explicit invocation below doesn't
                   actually need this to have succeeded */ }

        var psi = new ProcessStartInfo
        {
            FileName        = "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow  = true,
        };
        // Caller shuts this process down right after PrepareRelaunch returns -- without nohup +
        // background ('&'), the script (which waits up to 30s for our pid to exit before
        // copying) would die alongside us instead of outliving us, same as a SIGHUP would kill
        // a plain child. -c/nohup/& need real shell semantics, so this isn't a plain argv exec.
        // Invoking via "sh '<path>'" rather than exec'ing the path directly means this doesn't
        // depend on the SetUnixFileMode above having succeeded at all.
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add($"nohup sh '{EscSh(scriptPath)}' </dev/null >/dev/null 2>&1 &");

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to launch the relaunch script (sh).");
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string EscPs(string path) => path.Replace("'", "''");

    private static string EscSh(string path) => path.Replace("'", "'\\''");

    private async Task<string> CaptureAsync(
        string exe, string args, string? workDir, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory       = workDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        // ?? throw, not "!" -- this is reached by CheckDotNetSdkAsync's now-cross-platform
        // probe; the outer try/catch there happens to already convert any exception (NRE
        // included) into "false", but that's incidental, not a reason to skip a clear
        // exception in favor of a silent null-deref (grok review BLOCKER, 2026-06-22).
        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start process: {exe}");
        var stdout = await proc.StandardOutput.ReadToEndAsync(CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        cts.CancelAfter(timeout);
        await proc.WaitForExitAsync(cts.Token);
        return stdout;
    }

    private Task StreamAsync(
        string exe, string args, string? workDir,
        IProgress<string> progress, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory       = workDir ?? "",
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        return StreamAsync(psi, progress, timeout);
    }

    /// <summary>
    /// Same as the (exe, args, workDir) overload above, but takes a caller-built
    /// ProcessStartInfo -- needed when an argument needs ArgumentList rather than a flat,
    /// manually-quoted Arguments string (e.g. a path that could contain spaces).
    /// </summary>
    private async Task StreamAsync(
        ProcessStartInfo psi, IProgress<string> progress, TimeSpan timeout)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;
        psi.CreateNoWindow         = true;

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start process: {psi.FileName}");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        cts.CancelAfter(timeout);

        var outTask = PipeAsync(proc.StandardOutput, progress, cts.Token);
        var errTask = PipeAsync(proc.StandardError,  progress, cts.Token);

        try
        {
            await proc.WaitForExitAsync(cts.Token);
        }
        catch
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        await Task.WhenAll(outTask, errTask);

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"{psi.FileName} exited {proc.ExitCode}.");
    }

    private static async Task PipeAsync(
        StreamReader reader, IProgress<string> progress, CancellationToken ct)
    {
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
            if (!string.IsNullOrWhiteSpace(line))
                progress.Report(line.TrimEnd());
    }
}
