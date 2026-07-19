// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime v2.0 Phase B/C (docs/NATIVE_RUNTIME_V2_SPEC.md) — LIVE VRAM reads, not a
/// one-time install-detected total or a file-size guess.
///
/// Before this, there was no "how much VRAM is free RIGHT NOW" query anywhere in the running
/// app: <c>OrchestratorSetup/Services/HardwareDetector.cs</c> (a different project/assembly,
/// not referenced by the running app) only probes TOTAL capacity, once, at install time.
/// That gap is exactly why <c>MainWindow.TryBuildNativeHiveBudget</c> and HiveService's
/// equivalent hardcoded <c>ReservedBytes: 0</c> — there was nothing else to put there.
///
/// Same subprocess-CSV pattern as <c>HardwareDetector.QueryNvidiaSmi</c> (a proven-safe,
/// already-shipped approach), but querying live values instead of a one-time
/// <c>memory.total</c>-only read, and designed to be called repeatedly, not just once at startup.
/// </summary>
public static class NativeVramProbe
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Queries the first NVIDIA GPU's current total/used VRAM via nvidia-smi. Returns
    /// <see langword="null"/> on any failure — no nvidia-smi found, non-NVIDIA GPU, a parse
    /// failure, or a timeout — so callers fall back to their own static budget in that case.
    /// Never invents a number it can't back with a real reading, same philosophy as
    /// <see cref="RuntimeStats"/>/<see cref="RuntimeHealth"/> returning null rather than a
    /// guess.
    /// </summary>
    public static VramBudget? TryQueryLiveNvidiaBudget()
    {
        var output = RunNvidiaSmi("--query-gpu=memory.total,memory.used --format=csv,noheader,nounits");
        if (output is null)
            return null;

        // Multi-GPU boxes: nvidia-smi emits one CSV line per device. Take the first, matching
        // HardwareDetector's own "prefer the first discrete GPU" convention — this codebase
        // does not yet do multi-GPU-aware budget aggregation (a real gap, but a separate,
        // larger design question than this single-probe read).
        var firstLine = output.Split('\n')[0];
        var parts = firstLine.Split(',');
        if (parts.Length < 2 ||
            !long.TryParse(parts[0].Trim(), out var totalMib) ||
            !long.TryParse(parts[1].Trim(), out var usedMib))
            return null;

        return new VramBudget(
            TotalBytes: totalMib * 1024L * 1024L,
            ReservedBytes: usedMib * 1024L * 1024L);
    }

    /// <summary>
    /// Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3) — how much VRAM THIS
    /// PROCESS is actually using right now, via nvidia-smi's per-process accounting
    /// (<c>--query-compute-apps</c>), filtered to <see cref="Environment.ProcessId"/>.
    ///
    /// Replaces the old file-size proxy (<c>base+adapter GGUF size on disk</c>) that
    /// <c>RuntimeStats.EstimatedVramBytes</c> used to report — a guess, not a measurement.
    /// This is a genuine reading from the driver, consistent with
    /// <see cref="RuntimeStats.EstimatedVramBytes"/>'s own pre-existing doc comment ("null on
    /// Ollama — not exposed per-process"), which already framed the field as process-level, not
    /// per-role. All native roles in this process share one CUDA context (LLamaSharp exposes no
    /// per-process VRAM via its own managed API — see <c>LLamaSharpRuntime.GetStats</c>), so
    /// per-ROLE isolation is not obtainable at this layer; process-level is the honest ceiling.
    /// Returns null on any failure, including the legitimate case where this process currently
    /// holds no GPU allocation nvidia-smi can see yet (e.g. before any model has loaded).
    /// </summary>
    public static long? TryQueryCurrentProcessVramBytes()
    {
        var output = RunNvidiaSmi("--query-compute-apps=pid,used_memory --format=csv,noheader,nounits");
        if (output is null)
            return null;

        var pid = Environment.ProcessId;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split(',');
            if (parts.Length < 2)
                continue;
            if (!int.TryParse(parts[0].Trim(), out var linePid) || linePid != pid)
                continue;
            if (long.TryParse(parts[1].Trim(), out var usedMib))
                return usedMib * 1024L * 1024L;
        }

        return null;
    }

    /// <summary>
    /// Shared subprocess invocation for both query methods above — factored out so the
    /// bounded-timeout logic (see the inline comment below) exists in exactly one place rather
    /// than being duplicated per query mode, which would risk re-introducing the same deadlock
    /// bug in a second copy. Returns trimmed stdout on success, or null on any failure (missing
    /// exe, non-zero exit, empty output, or a timeout).
    /// </summary>
    private static string? RunNvidiaSmi(string arguments)
    {
        try
        {
            // Same candidate search order as HardwareDetector.QueryNvidiaSmi: nvidia-smi is on
            // PATH when the driver is installed, with two well-known fallback locations for
            // when it isn't (rare, but seen on some OEM images).
            var candidates = new[]
            {
                "nvidia-smi",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "nvidia-smi.exe"),
            };
            var exe = candidates.FirstOrDefault(File.Exists) ?? "nvidia-smi";

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // ReadToEnd() blocks until stdout closes, which only happens when the process exits
            // -- if nvidia-smi hangs, that call blocks indefinitely and WaitForExit's timeout
            // would never even be reached (CodeRabbit finding on the first cut of this method).
            // Bound the read AND the exit under one shared deadline instead, so a truly hung
            // process is killed within QueryTimeout regardless of which step it's stuck on.
            string output;
            using (var cts = new CancellationTokenSource(QueryTimeout))
            {
                try
                {
                    var readTask = process.StandardOutput.ReadToEndAsync(cts.Token);
                    var exitTask = process.WaitForExitAsync(cts.Token);
                    Task.WaitAll([readTask, exitTask], cts.Token);
                    output = readTask.Result.Trim();
                }
                catch (OperationCanceledException)
                {
                    // Best-effort: a hung nvidia-smi must not leak a zombie process, but a
                    // failure killing it must not throw past this method either.
                    try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                    return null;
                }
            }

            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output : null;
        }
        catch
        {
            // Any I/O failure (missing exe, permission denied, process launch failure) is
            // best-effort here — this is a live-read convenience, not the sole admission path;
            // callers fall back to their own behavior when this returns null.
            return null;
        }
    }
}
