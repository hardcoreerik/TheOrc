// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using LLama.Abstractions;
using LLama.Native;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Overrides LLamaSharp's own CUDA-candidate construction so it always falls back to the
/// packaged cuda12 backend (then cuda11) instead of only trying a folder matching whatever
/// CUDA *toolkit* version <see cref="SystemInfo.CudaMajorVersion"/> happens to detect via
/// CUDA_PATH/version.json.
///
/// Why this exists: <c>NativeLibraryWithCuda.Prepare</c> only takes the "try cuda12, then
/// cuda11" fallback path when its majorCudaVersion is exactly -1 (the driver-only, no-toolkit
/// case this fleet was built around — see <see cref="NativeBackendBootstrap"/>'s own class
/// doc). When a real CUDA toolkit is installed, LLamaSharp's toolkit detection succeeds and
/// returns that toolkit's major version instead, and the class then ONLY tries that one exact
/// version's folder with no fallback at all. We only ever ship a cuda12 backend, so a machine
/// with e.g. CUDA 13.3 installed (toolkit present, but only for other work — the driver alone
/// is sufficient for a statically-linked cuda12 backend) ends up trying a nonexistent cuda13
/// folder and failing outright, even though the working cuda12 folder is sitting right there.
/// Confirmed live on HARDCOREPC (RTX 3050, driver-only originally; installing the CUDA 13.3
/// SDK the same night broke native library loading entirely — LLamaSharp's own log showed it
/// trying "runtimes\win-x64\native\cuda13\ggml-base.dll" and failing, never attempting cuda12).
///
/// Forcing majorCudaVersion back to -1 for CUDA candidates makes toolkit version irrelevant —
/// exactly the "no user interaction, works regardless of what's installed" behavior wanted.
/// If a genuine cuda13 (or other version) backend is ever packaged, extend this policy to try
/// it first and fall back to cuda12, rather than relying on LLamaSharp's toolkit sniffing.
/// </summary>
internal sealed class Cuda12FallbackSelectingPolicy : INativeLibrarySelectingPolicy
{
    private readonly DefaultNativeLibrarySelectingPolicy _default = new();

    public IEnumerable<INativeLibrary> Apply(
        NativeLibraryConfig.Description description,
        SystemInfo systemInfo,
        NativeLogConfig.LLamaLogCallback? logCallback = null)
    {
        foreach (var library in _default.Apply(description, systemInfo, logCallback))
        {
            yield return library is NativeLibraryWithCuda
                ? new NativeLibraryWithCuda(-1, description.Library, description.AvxLevel, description.SkipCheck)
                : library;
        }
    }
}

/// <summary>
/// Result of the one-time native backend selection. <see cref="SelectedCuda"/> false while
/// <see cref="CudaCapableGpu"/> true is the loud "you are silently on CPU" signal every
/// consumer must surface (activity log / daemon log), not bury.
/// </summary>
public sealed record NativeBackendReport(
    bool CudaCapableGpu,
    bool DryRunSucceeded,
    bool SelectedCuda,
    string SelectedLlama,
    string SelectedMtmd,
    IReadOnlyList<string> Log)
{
    /// <summary>One-line human-readable verdict for activity logs.</summary>
    public string Verdict =>
        SelectedCuda
            ? $"CUDA backend selected ({SelectedLlama})"
            : CudaCapableGpu
                ? $"CPU FALLBACK despite CUDA-capable GPU — selected {SelectedLlama}"
                : $"CPU backend selected ({SelectedLlama}); no CUDA-capable GPU detected";
}

/// <summary>
/// One-time, process-wide LLamaSharp native backend configuration. Must run BEFORE the first
/// touch of <c>LLama.Native.NativeApi</c> — LLamaSharp freezes <see cref="NativeLibraryConfig"/>
/// once a native library is loaded.
///
/// Why this exists: LLamaSharp 0.27's default selection only tries the CUDA backend when
/// <c>SystemInfo.GetCudaMajorVersion()</c> succeeds, and on Windows that reads the CUDA
/// *toolkit*'s <c>CUDA_PATH</c>/version.json. A machine with only the NVIDIA *driver*
/// installed (every deployed fleet box) fails that check, so the packaged cuda12 backend is
/// skipped without a load attempt and inference lands on avx2 CPU — observed live on
/// hardcorelaptopmsi (RTX 4060, 1.7 tok/s, model in system RAM). The driver is all that the
/// statically-linked cuda12 backend actually needs, so when a CUDA-capable driver is present
/// AND a real pre-flight load of the packaged cuda12 DLL chain succeeds, we force CUDA with
/// <c>SkipCheck</c> (which requires disabling LLamaSharp's fallback — the two are mutually
/// exclusive); a failed forced selection reverts to the default fallback-enabled selection.
/// Either way the outcome is visible via the returned report, never silent.
/// </summary>
public static class NativeBackendBootstrap
{
    private static readonly object Gate = new();
    private static NativeBackendReport? _report;
    private static Action<string>? _ongoingSink;

    /// <summary>
    /// Configure the native backend selection (first call only) and return the cached report.
    /// <paramref name="nativeLogSink"/>, when non-null, replaces the process-wide sink that
    /// receives ALL subsequent native log lines (model-load progress included) — route it to
    /// a Debug-level logger, not a user-facing activity feed.
    /// </summary>
    public static NativeBackendReport EnsureConfigured(Action<string>? nativeLogSink = null)
    {
        lock (Gate)
        {
            if (nativeLogSink is not null)
                _ongoingSink = nativeLogSink;
            if (_report is not null)
                return _report;

            var log = new List<string>();
            var cudaCapable = HasCudaCapableDriver(log);
            // LLamaSharp forbids SkipCheck(true) together with WithAutoFallback(true)
            // ("Cannot skip the check when fallback is allowed" — thrown at selection time,
            // which poisons NativeApi's type initializer). So forcing CUDA means giving up
            // LLamaSharp's own fallback — only safe because we pre-flight the full cuda12
            // DLL chain with real NativeLibrary loads first, and revert to the default
            // fallback-enabled selection if the forced DryRun still fails.
            var forceCuda = cudaCapable && PreflightCudaBackend(log);

            try
            {
                NativeLibraryConfig.All
                    .WithLogCallback((level, message) =>
                    {
                        var line = $"[llama:{level}] {message.TrimEnd('\n')}";
                        lock (Gate)
                        {
                            // Bounded: selection produces tens of lines; model loads later
                            // stream through _ongoingSink only.
                            if (_report is null && log.Count < 256 && level != LLamaLogLevel.Debug)
                                log.Add(line);
                        }
                        _ongoingSink?.Invoke(line);
                    });

                if (forceCuda)
                    NativeLibraryConfig.All
                        .WithCuda(true).SkipCheck(true).WithAutoFallback(false)
                        .WithSelectingPolicy(new Cuda12FallbackSelectingPolicy());
                else
                    NativeLibraryConfig.All.WithAutoFallback(true);
            }
            catch (Exception ex)
            {
                // Config already frozen (something touched NativeApi first) — selection
                // already happened with defaults; report that instead of throwing.
                log.Add($"NativeLibraryConfig rejected configuration: {ex.Message}");
                forceCuda = false;
            }

            var (ok, selectedCuda, llamaDesc, mtmdDesc) = TryDryRun(log, forceCuda);

            if (!ok && forceCuda)
            {
                // Forced-CUDA selection failed despite the pre-flight. A failed DryRun does
                // not freeze the config, so restore the default fallback-enabled selection —
                // ending up on CPU (visibly) beats a dead native runtime.
                log.Add("Forced-CUDA selection failed — reverting to default selection with fallback.");
                try
                {
                    NativeLibraryConfig.All.SkipCheck(false).WithAutoFallback(true);
                    (ok, selectedCuda, llamaDesc, mtmdDesc) = TryDryRun(log, forcedCuda: false);
                }
                catch (Exception ex)
                {
                    log.Add($"Fallback reconfiguration rejected: {ex.Message}");
                }
            }

            _report = new NativeBackendReport(cudaCapable, ok, selectedCuda, llamaDesc, mtmdDesc, log);
            return _report;
        }
    }

    private static (bool Ok, bool SelectedCuda, string LlamaDesc, string MtmdDesc) TryDryRun(
        List<string> log, bool forcedCuda)
    {
        try
        {
            var ok = NativeLibraryConfig.All.DryRun(out var llama, out var mtmd);
            var lm = llama?.Metadata;
            // DryRun's out metadata can be null even on success (observed when the pre-flight
            // already loaded the DLLs). Under forced CUDA with fallback disabled, a successful
            // DryRun can only mean cuda12 — attribute accordingly instead of "<none>".
            var forcedDesc = ok && forcedCuda ? "cuda12 (forced selection; metadata unavailable)" : "<none>";
            return (ok,
                lm?.UseCuda ?? (ok && forcedCuda),
                lm?.ToString() ?? forcedDesc,
                mtmd?.Metadata?.ToString() ?? forcedDesc);
        }
        catch (Exception ex)
        {
            log.Add($"DryRun threw: {ex.GetType().Name}: {ex.Message}");
            return (false, false, "<none>", "<none>");
        }
    }

    /// <summary>
    /// Real load test of every DLL LLamaSharp's cuda12 selection will need, by absolute path,
    /// in dependency order. The handles are deliberately kept loaded — the selection is about
    /// to reuse them, and a loaded chain guarantees llama's imports resolve. Any miss means
    /// "do not force CUDA", not an error.
    /// </summary>
    private static bool PreflightCudaBackend(List<string> log)
    {
        string nativeRoot, prefix, ext;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            nativeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "win-x64", "native");
            (prefix, ext) = ("", ".dll");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            nativeRoot = Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native");
            (prefix, ext) = ("lib", ".so");
        }
        else
        {
            return false;
        }

        var cudaDir = Path.Combine(nativeRoot, "cuda12");
        if (!Directory.Exists(cudaDir))
        {
            log.Add($"cuda12 backend directory not packaged ({cudaDir}) — not forcing CUDA.");
            return false;
        }

        if (!TryLoadFrom(cudaDir, $"{prefix}ggml-base{ext}", log))
            return false;

        // cuda12's ggml links against ggml-cpu, which only the CPU backend dirs carry —
        // LLamaSharp's own cuda selection loads it from there too. Any variant satisfies
        // import resolution (matched by module name); prefer the highest the fleet baseline
        // supports and walk down.
        var cpuLoaded = false;
        foreach (var cpuVariant in new[] { "avx2", "avx512", "avx", "noavx" })
        {
            var cpuPath = Path.Combine(nativeRoot, cpuVariant, $"{prefix}ggml-cpu{ext}");
            if (File.Exists(cpuPath) && NativeLibrary.TryLoad(cpuPath, out _))
            {
                cpuLoaded = true;
                break;
            }
        }
        if (!cpuLoaded)
        {
            log.Add("cuda12 pre-flight: no CPU backend ggml-cpu could be loaded (needed by cuda12 ggml) — not forcing CUDA.");
            return false;
        }

        foreach (var name in new[] { "ggml-cuda", "ggml", "llama", "mtmd" })
        {
            if (!TryLoadFrom(cudaDir, $"{prefix}{name}{ext}", log))
                return false;
        }

        log.Add("cuda12 pre-flight: full backend DLL chain loaded — forcing CUDA selection.");
        return true;
    }

    private static bool TryLoadFrom(string dir, string file, List<string> log)
    {
        var path = Path.Combine(dir, file);
        if (!File.Exists(path))
        {
            log.Add($"cuda12 pre-flight: '{file}' missing — not forcing CUDA.");
            return false;
        }
        if (!NativeLibrary.TryLoad(path, out _))
        {
            log.Add($"cuda12 pre-flight: '{file}' failed to load — not forcing CUDA.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// CUDA-capable == the NVIDIA *driver* library loads. Deliberately NOT the toolkit check
    /// LLamaSharp performs — the fleet has drivers, not toolkits.
    /// </summary>
    private static bool HasCudaCapableDriver(List<string> log)
    {
        string[] candidates =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ["nvcuda.dll"] :
            RuntimeInformation.IsOSPlatform(OSPlatform.Linux)   ? ["libcuda.so.1", "libcuda.so"] :
            [];

        foreach (var name in candidates)
        {
            if (NativeLibrary.TryLoad(name, out var handle))
            {
                NativeLibrary.Free(handle);
                log.Add($"CUDA driver library '{name}' present — forcing CUDA backend attempt (SkipCheck).");
                return true;
            }
        }

        if (candidates.Length > 0)
            log.Add($"No CUDA driver library found ({string.Join(", ", candidates)}) — default backend selection.");
        return false;
    }
}
