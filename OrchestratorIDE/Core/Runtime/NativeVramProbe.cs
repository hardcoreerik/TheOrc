// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime v2.0 Phase B (docs/NATIVE_RUNTIME_V2_SPEC.md Phase B) — a LIVE VRAM
/// availability read, not a one-time install-detected total.
///
/// Before this, there was no "how much VRAM is free RIGHT NOW" query anywhere in the running
/// app: <c>OrchestratorSetup/Services/HardwareDetector.cs</c> (a different project/assembly,
/// not referenced by the running app) only probes TOTAL capacity, once, at install time.
/// That gap is exactly why <c>MainWindow.TryBuildNativeHiveBudget</c> and HiveService's
/// equivalent hardcoded <c>ReservedBytes: 0</c> — there was nothing else to put there.
///
/// Same subprocess-CSV pattern as <c>HardwareDetector.QueryNvidiaSmi</c> (a proven-safe,
/// already-shipped approach), but querying live <c>memory.used</c> instead of a one-time
/// <c>memory.total</c>-only read, and designed to be called repeatedly (once per admission
/// check), not just once at startup.
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
                Arguments = "--query-gpu=memory.total,memory.used --format=csv,noheader,nounits",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            var output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit((int)QueryTimeout.TotalMilliseconds))
            {
                // Best-effort: a hung nvidia-smi must not leak a zombie process, but a failure
                // killing it must not throw past this method either.
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
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
        catch
        {
            // Any I/O failure (missing exe, permission denied, process launch failure) is
            // best-effort here — this is a live-read convenience, not the sole admission path;
            // callers fall back to their own static budget when this returns null.
            return null;
        }
    }
}
