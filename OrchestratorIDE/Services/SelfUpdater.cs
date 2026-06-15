// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
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

    private const string RepoUrl          = "https://github.com/hardcoreerik/TheOrc.git";
    private const string DotnetInstallUrl = "https://dot.net/v1/dotnet-install.ps1";
    private const string RequiredSdkMajor = "10";

    // ── Configuration ─────────────────────────────────────────────────────────

    public CancellationToken CancellationToken { get; set; } = default;

    // ── .NET SDK ──────────────────────────────────────────────────────────────

    /// <summary>Returns true if a .NET 10+ SDK is available on PATH.</summary>
    public async Task<bool> CheckDotNetSdkAsync()
    {
        try
        {
            var output = await CaptureAsync("dotnet", "--version", null, TimeSpan.FromSeconds(10));
            var major  = output.Trim().Split('.')[0];
            return major == RequiredSdkMajor;
        }
        catch { return false; }
    }

    /// <summary>
    /// Downloads and runs the official dotnet-install.ps1 to install the .NET 10 SDK.
    /// Requires internet access; may need admin rights depending on install path.
    /// </summary>
    public async Task InstallDotNetSdkAsync(IProgress<string> progress)
    {
        progress.Report("Downloading dotnet-install.ps1…");

        var tempScript = Path.Combine(Path.GetTempPath(), "dotnet-install.ps1");
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            var text = await http.GetStringAsync(DotnetInstallUrl, CancellationToken);
            await File.WriteAllTextAsync(tempScript, text, CancellationToken);
        }

        progress.Report("Installing .NET 10 SDK — this takes 1–3 minutes…");

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{tempScript}\" " +
                   $"-Channel 10.0 -InstallDir \"$env:ProgramFiles\\dotnet\"";
        await StreamAsync("powershell.exe", args, null, progress, TimeSpan.FromMinutes(10));

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
    /// Runs dotnet publish on the main project, producing a self-contained win-x64 exe
    /// in <paramref name="outputDir"/>.
    /// </summary>
    public async Task BuildAndPublishAsync(string sourceDir, string outputDir, IProgress<string> progress)
    {
        var projPath = Path.Combine(sourceDir, "OrchestratorIDE", "OrchestratorIDE.csproj");
        if (!File.Exists(projPath))
            throw new FileNotFoundException($"Project not found at: {projPath}");

        Directory.CreateDirectory(outputDir);

        progress.Report("Building TheOrc — this may take a minute…");

        var args = $"publish \"{projPath}\" " +
                   $"-c Release -r win-x64 --self-contained true " +
                   $"-p:PublishSingleFile=true " +
                   $"-o \"{outputDir}\"";

        await StreamAsync("dotnet", args, sourceDir, progress, TimeSpan.FromMinutes(15));

        progress.Report("✓ Build complete.");
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
        var destPath = Path.Combine(stagingDir, "OrchestratorIDE.exe");

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
        while ((read = await src.ReadAsync(buf, CancellationToken)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read), CancellationToken);
            downloaded += read;
            if (total.HasValue && total > 0)
                progress.Report($"  {downloaded / 1024:N0} KB / {total / 1024:N0} KB");
        }

        progress.Report($"✓ Downloaded to {destPath}");
        return destPath;
    }

    // ── Relaunch ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes _orc_update.ps1 next to the running exe, then launches it hidden.
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

        var installDir = Path.GetDirectoryName(actualExe)!;
        var scriptPath = Path.Combine(installDir, "_orc_update.ps1");

        var ps = new StringBuilder();
        ps.AppendLine($"$orcPid    = {Environment.ProcessId}");
        ps.AppendLine($"$oldExe    = '{EscPs(actualExe)}'");
        ps.AppendLine($"$newExe    = '{EscPs(stagedExePath)}'");
        ps.AppendLine($"$stagingDir = '{EscPs(Path.GetDirectoryName(stagedExePath)!)}'");
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

        Process.Start(new ProcessStartInfo
        {
            FileName        = "powershell.exe",
            Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow  = true,
        });
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    private static string EscPs(string path) => path.Replace("'", "''");

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

        using var proc = Process.Start(psi)!;
        var stdout = await proc.StandardOutput.ReadToEndAsync(CancellationToken);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(CancellationToken);
        cts.CancelAfter(timeout);
        await proc.WaitForExitAsync(cts.Token);
        return stdout;
    }

    private async Task StreamAsync(
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

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start process: {exe}");

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
            throw new InvalidOperationException($"{exe} exited {proc.ExitCode}.");
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
