// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 4 — capability + VRAM + lane-aware dispatch (docs/RUNTIME_PHASE0_SPEC.md §6).
///
/// Not to be confused with <see cref="OrchestratorIDE.Services.Hive.HiveScheduler"/>, which
/// picks a remote HIVE node for a task based on free VRAM + model-hint matching — a distributed
/// concern. OrcScheduler is the opposite scope: given AdapterManager has no VRAM awareness today
/// (it will happily try to build a persistent executor for every role requested, regardless of
/// whether there's actually room), OrcScheduler decides whether a role's request can be admitted
/// right now against a single process's VRAM budget, or must be queued.
///
/// Pure decision logic, no live GPU dispatch — no actual VRAM measurement (the implementation
/// estimates from RuntimeModelAsset.SizeBytes, see <see cref="OrcScheduler"/>), no real
/// queueing/pipeline execution. IS wired into <see cref="RuntimeOrchestrator.EnsureAdmitted"/>
/// (verified 2026-06-24, not assumed -- this comment previously said "not yet wired" after that
/// integration had already landed): every role admission goes through TryAdmit with generation-
/// tagged per-role reservation accounting, denying with <see cref="RuntimeAdmissionDeniedException"/>
/// on a real budget miss.
/// </summary>
public interface IOrcScheduler
{
    /// <summary>
    /// Decides whether <paramref name="binding"/> can be admitted into
    /// <paramref name="budget"/> right now. Does not mutate the budget or reserve anything —
    /// callers that admit a role are responsible for updating their own tracked
    /// <see cref="VramBudget.ReservedBytes"/> for subsequent calls (this interface is a pure
    /// decision function, not a stateful reservation system, so it stays trivially testable).
    /// <paramref name="options"/> (Phase B addendum) lets the estimate account for the actual
    /// context size the caller will create; null preserves the legacy file-size-only estimate.
    /// </summary>
    SchedulingDecision TryAdmit(RuntimeRoleBinding binding, VramBudget budget, RuntimeOptions? options = null);
}

/// <summary>
/// A single process's VRAM accounting. <see cref="ReservedBytes"/> is whatever the caller has
/// already committed to currently-active role executors — OrcScheduler does not track this
/// itself; the caller passes the current snapshot in on every TryAdmit call.
/// <see cref="RuntimeOrchestrator"/> is that caller: it layers its own per-role active-reservation
/// total (one estimate per currently-admitted role, keyed by <see cref="RuntimeRole"/>) on top of
/// whatever baseline <see cref="ReservedBytes"/> the budget provider reports, so a second role
/// admitted concurrently with a first is checked against the first's actual footprint instead of
/// always seeing the provider's static snapshot.
/// </summary>
public sealed record VramBudget(long TotalBytes, long ReservedBytes)
{
    public long AvailableBytes => Math.Max(0, TotalBytes - ReservedBytes);
}

/// <summary>
/// Boss/Reviewer are latency-sensitive (a human or another role is waiting on the response);
/// Worker/Researcher can tolerate being queued behind other admissions. This is the "lane"
/// half of "capability + VRAM + lane-aware dispatch" — used to decide queue priority when VRAM
/// is contended, not to change whether something is admitted at all.
/// </summary>
public enum SchedulingLane
{
    Interactive,
    Background,
}

/// <summary>
/// Result of a TryAdmit call. <see cref="Reason"/> is populated only when
/// <see cref="Admitted"/> is false — explains why (e.g. "requires 6.2 GB, only 3.1 GB available")
/// so a caller can surface something more useful than a bare denial.
/// </summary>
public sealed record SchedulingDecision(bool Admitted, SchedulingLane Lane, string? Reason = null);

/// <summary>
/// First real OrcScheduler capability (Phase 4): a VRAM-budget admission check. With
/// <see cref="RuntimeOptions"/> supplied (Phase B addendum, docs/NATIVE_RUNTIME_V2_SPEC.md),
/// the estimate is context-aware: file size (weights — measured within ~0.5% of the actual
/// CUDA model buffer on the reference box) + CUDA runtime overhead allowance + the KV-cache
/// formula the 2026-07-19 spike validated byte-exactly against llama.cpp's own allocator
/// (224.00/896.00/1792.00 MiB at n_ctx 2048/8192/16384 for Llama-3.2-3B) + a compute-buffer
/// allowance (measured 260-285 MB; it SHRANK with larger n_ctx, so a flat allowance is the
/// honest choice over a fake formula) + an architecture-gated recurrent-state term. Without
/// options, the legacy file-size-only estimate is preserved.
/// </summary>
public sealed class OrcScheduler : IOrcScheduler
{
    // LoRA adapters are small relative to base models (tens to a few hundred MB). When the
    // adapter's size is unknown — e.g. a PEFT directory, which AdapterManager doesn't even load
    // directly today (LoadLoraFromFile requires a GGUF path) — this is a deliberately
    // conservative fallback estimate, not zero cost and not a refusal for missing metadata.
    internal const long UnknownAdapterSizeEstimateBytes = 512L * 1024 * 1024; // 512 MB

    // Spike-measured allowances (reference box, 2026-07-19 — see the Phase B addendum):
    // whole-GPU weights delta exceeded file size by ~211 MB (CUDA runtime/context overhead),
    // and the scheduler compute buffer ranged 260-285 MB across n_ctx 2048-16384. Both are
    // allowances with headroom, not exact predictions — the future calibration cache replaces
    // them with per-machine measurements.
    internal const long CudaRuntimeOverheadBytes = 256L * 1024 * 1024;  // 256 MB
    internal const long ComputeBufferAllowanceBytes = 384L * 1024 * 1024; // 384 MB

    // Architectures with per-sequence recurrent state ("rs cache") — llama.cpp allocates
    // ~50 MB/slot × n_seq_max for these (measured live: SeqMax=240 reserved ~12 GB and OOM'd a
    // 16 GB GPU on Qwen3.5-9B — see AdapterManager.SequenceHardLimit's doc). Prefix-matched
    // against the GGUF header's general.architecture. Deliberately conservative-and-known-only:
    // an unknown hybrid arch under-estimates here, but admission is still bounded by the live
    // free-VRAM read (Phase B) and the NoKvSlot/recycle defenses — while putting the term on
    // every unknown arch would over-reserve for the overwhelmingly common plain-transformer
    // case, the exact hazard that deferred this work. Qwen3.5's llama.cpp arch id is added
    // when a real hybrid GGUF is available to verify against (the calibration cache covers
    // hybrids regardless, by measuring instead of predicting).
    internal static readonly string[] KnownRecurrentArchitecturePrefixes =
        ["mamba", "rwkv", "jamba"];

    internal const long RecurrentStatePerSlotBytes = 50L * 1024 * 1024; // 50 MB, measured

    public SchedulingDecision TryAdmit(RuntimeRoleBinding binding, VramBudget budget, RuntimeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(budget);

        var lane = binding.Role is RuntimeRole.Boss or RuntimeRole.Reviewer
            ? SchedulingLane.Interactive
            : SchedulingLane.Background;

        var requiredBytes = EstimateRequiredBytes(binding, options);
        if (requiredBytes <= budget.AvailableBytes)
            return new SchedulingDecision(Admitted: true, Lane: lane);

        return new SchedulingDecision(
            Admitted: false,
            Lane: lane,
            Reason: $"Requires ~{FormatGb(requiredBytes)}, only {FormatGb(budget.AvailableBytes)} available.");
    }

    // internal (not private): RuntimeOrchestrator needs the same estimate to maintain its own
    // active-reservation accounting across concurrent role admissions (see its ReservedBytes
    // doc) — duplicating the estimate in two places would let them drift out of sync silently.
    internal static long EstimateRequiredBytes(RuntimeRoleBinding binding, RuntimeOptions? options = null)
    {
        // BaseModel.SizeBytes is null only if ModelDepot ever classified a directory as
        // BaseModelGguf, which its own scan logic never does (BaseModelGguf is always a single
        // .gguf file) — defensive fallback to 0 rather than throwing on a value that shouldn't
        // occur, consistent with this class being a pure decision function that doesn't assume
        // its inputs are perfectly well-formed.
        var baseBytes = binding.BaseModel.SizeBytes ?? 0;
        var adapterBytes = binding.Adapter is null
            ? 0
            : binding.Adapter.SizeBytes ?? UnknownAdapterSizeEstimateBytes;
        var legacy = baseBytes + adapterBytes;

        // No options => legacy file-size-only behavior, unchanged (existing callers/tests that
        // never had a context size to offer keep their exact semantics).
        if (options is null)
            return legacy;

        // Header unreadable (non-GGUF path, corrupt file, exotic version) => legacy fallback.
        // Estimation must never make admission WORSE than the pre-addendum behavior.
        var header = GgufMetadataReader.TryRead(binding.BaseModel.Path);
        if (header is null)
            return legacy;

        // Byte-exact for plain transformers (spike-validated): K + V, f16, per layer per KV
        // head per token. checked() so a hostile/corrupt header overflows loudly into the
        // caller's own estimate fallback rather than silently wrapping negative.
        long kvBytes;
        try
        {
            kvBytes = checked(
                (long)header.BlockCount * options.ContextLength * header.HeadCountKv
                * (header.KeyLength + header.ValueLength) * 2);
        }
        catch (OverflowException)
        {
            return legacy;
        }

        var recurrentBytes = KnownRecurrentArchitecturePrefixes.Any(p =>
                header.Architecture.StartsWith(p, StringComparison.OrdinalIgnoreCase))
            ? RecurrentStatePerSlotBytes * AdapterManager.SequenceHardLimit
            : 0;

        return legacy + CudaRuntimeOverheadBytes + kvBytes + ComputeBufferAllowanceBytes + recurrentBytes;
    }

    private static string FormatGb(long bytes) =>
        $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
