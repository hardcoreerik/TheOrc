// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime v2.0 Phase B addendum (docs/NATIVE_RUNTIME_V2_SPEC.md) — parses llama.cpp's
/// own load-time allocation log lines into real byte counts. The 2026-07-19 spike found these
/// lines are exact, per-component, and (unlike nvidia-smi's per-process query) unaffected by
/// WDDM — the Windows consumer-GPU driver mode where every process's VRAM usage reports
/// <c>[N/A]</c>, confirmed live on the reference box, which makes
/// <see cref="NativeVramProbe.TryQueryCurrentProcessVramBytes"/> a no-op there.
///
/// TheOrc already receives every native log line process-wide via
/// <see cref="NativeBackendBootstrap.EnsureConfigured"/>'s <c>nativeLogSink</c> callback — this
/// class only parses text already flowing through that existing sink, it does not add a new
/// hook into llama.cpp.
///
/// Observed line shapes (verbatim from the spike's captured output, real
/// Llama-3.2-3B-Q4_K_M load — not invented):
/// <code>
/// load_tensors:   CPU_Mapped model buffer size =   308.24 MiB
/// load_tensors:        CUDA0 model buffer size =  1918.36 MiB
/// llama_kv_cache:      CUDA0 KV buffer size =   224.00 MiB
/// sched_reserve:      CUDA0 compute buffer size =   284.90 MiB
/// sched_reserve:  CUDA_Host compute buffer size =    16.26 MiB
/// </code>
/// Only lines whose device is a bare CUDA device (<c>CUDA0</c>, <c>CUDA1</c>, ...) are real
/// VRAM. <c>CPU_Mapped</c> is host RAM (layers not offloaded to GPU); <c>CUDA_Host</c> is
/// pinned host-side staging memory for transfers, not GPU memory — confirmed by spike math:
/// summing only CUDA<i>N</i> lines matched the independently-measured whole-GPU delta within
/// ~20 MB, while including CPU_Mapped/CUDA_Host would have overcounted by exactly their size.
/// </summary>
public static class NativeLoadAllocationParser
{
    private static readonly Regex BufferSizeLine = new(
        @"(?<device>\S+)\s+(?<category>model|KV|compute) buffer size\s*=\s*(?<mib>[\d.]+)\s*Mi?B",
        RegexOptions.Compiled);

    private static readonly Regex CudaDevice = new(@"^CUDA\d+$", RegexOptions.Compiled);

    /// <summary>
    /// Matches one log line. Returns the category ("model"/"KV"/"compute") and byte count if
    /// the line is a recognized buffer-size allocation on a real CUDA device; null otherwise
    /// (non-matching line, or a matching line on a non-VRAM device like CPU_Mapped/CUDA_Host).
    /// </summary>
    public static (string Category, long Bytes)? TryParseVramLine(string line)
    {
        var m = BufferSizeLine.Match(line);
        if (!m.Success)
            return null;

        if (!CudaDevice.IsMatch(m.Groups["device"].Value))
            return null;

        if (!double.TryParse(m.Groups["mib"].Value, System.Globalization.CultureInfo.InvariantCulture, out var mib))
            return null;

        return (m.Groups["category"].Value, (long)(mib * 1024 * 1024));
    }
}

/// <summary>
/// Accumulates <see cref="NativeLoadAllocationParser"/> matches across one load/context-create
/// sequence into per-category totals. Multiple lines can share a category (e.g. two CUDA
/// devices each reporting a "model buffer size" line) — those are summed, matching how the
/// whole-GPU delta the spike cross-checked against naturally sums across devices too.
/// </summary>
public sealed class NativeLoadAllocationAccumulator
{
    private readonly object _gate = new();
    private long _modelBytes;
    private long _kvBytes;
    private long _computeBytes;
    private int _suppressDepth;

    /// <summary>Total measured VRAM across every recognized category. Null if nothing was ever
    /// observed (e.g. a non-NVIDIA backend, or the log sink wasn't active) — never a fabricated
    /// zero, same "null rather than a guess" convention as <see cref="RuntimeStats"/>.</summary>
    public long? TotalBytes
    {
        get
        {
            lock (_gate)
            {
                var total = _modelBytes + _kvBytes + _computeBytes;
                return total > 0 ? total : null;
            }
        }
    }

    /// <summary>Feed one native log line. Safe to call from the native log callback thread —
    /// llama.cpp's log callback is not guaranteed to run on the calling thread. A no-op while
    /// <see cref="Suppress"/> is active (see that method's doc for why).</summary>
    public void Observe(string line)
    {
        if (Volatile.Read(ref _suppressDepth) > 0)
            return;

        var parsed = NativeLoadAllocationParser.TryParseVramLine(line);
        if (parsed is not { } p)
            return;

        lock (_gate)
        {
            switch (p.Category)
            {
                case "model": _modelBytes += p.Bytes; break;
                case "KV": _kvBytes += p.Bytes; break;
                case "compute": _computeBytes += p.Bytes; break;
            }
        }
    }

    /// <summary>
    /// Suspends accumulation for the returned scope's lifetime — CodeRabbit finding on the
    /// first cut of this PR: <see cref="LLamaSharpRuntime.StreamCompletionAsync"/> creates and
    /// disposes a fresh <c>StatelessExecutor</c> (and its native context) on every call. Without
    /// this, its "KV buffer size"/"compute buffer size" allocation lines would ADD to the
    /// running total on every stateless call with no corresponding subtraction on disposal —
    /// unboundedly inflating <see cref="TotalBytes"/> the longer the process runs. Rather than
    /// parse llama.cpp's context-DESTRUCTION log lines (an unverified format this codebase has
    /// never captured, and a wrong subtraction would silently corrupt the total either way),
    /// suppression at the source is the more robust fix: nothing observed during an ephemeral
    /// context's lifetime is ever added in the first place, so there is nothing to net out.
    ///
    /// Reference-counted (not a bool) so overlapping <see cref="Suppress"/> scopes — e.g. two
    /// concurrent stateless calls — compose correctly: accumulation only resumes once EVERY
    /// active scope has disposed, regardless of the order they started or finish in.
    /// </summary>
    public IDisposable Suppress()
    {
        Interlocked.Increment(ref _suppressDepth);
        return new SuppressScope(this);
    }

    private sealed class SuppressScope(NativeLoadAllocationAccumulator owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Interlocked.Decrement(ref owner._suppressDepth);
        }
    }

    /// <summary>Resets all totals to zero — called at the start of each new load so a previous
    /// model's measurement can never leak into the next one's total.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _modelBytes = 0;
            _kvBytes = 0;
            _computeBytes = 0;
        }
    }
}
