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
    /// <paramref name="baseWeightsAlreadyResident"/> lets a caller that has ESTABLISHED (not
    /// guessed) that this admission will reuse loaded base weights be charged only its
    /// incremental context cost; defaults to false so every existing caller is unchanged.
    /// </summary>
    SchedulingDecision TryAdmit(
        RuntimeRoleBinding binding,
        VramBudget budget,
        RuntimeOptions? options = null,
        bool baseWeightsAlreadyResident = false);
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
/// Result of a TryAdmit call. <see cref="Reason"/> is populated when <see cref="Admitted"/> is
/// false (explains why, e.g. "requires 6.2 GB, only 3.1 GB available") OR when it is true but
/// <see cref="EffectiveGpuLayers"/> is set (explains the degradation) — a caller can surface
/// something more useful than a bare denial either way.
/// </summary>
/// <param name="EffectiveGpuLayers">
/// Null when the binding was admitted at the GpuLayers the caller requested (full offload, or
/// whatever <see cref="RuntimeOptions.GpuLayers"/> already said). Non-null means TryAdmit could
/// not fit the full request but found a smaller GPU-layer count that does — "still try to run,
/// slower and partly on CPU, instead of refusing outright" (hardcoreerik, 2026-08-01: a
/// VRAM-tight box should degrade, not hard-fail every chat turn). The caller is responsible for
/// actually loading with this reduced value; TryAdmit itself never touches the model.
/// </param>
public sealed record SchedulingDecision(
    bool Admitted, SchedulingLane Lane, string? Reason = null, int? EffectiveGpuLayers = null);

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

    /// <param name="baseWeightsAlreadyResident">
    /// See <see cref="EstimateRequiredBytes"/>. Defaults to false so every existing caller keeps
    /// its exact previous semantics — only a caller that has actually established reuse (i.e.
    /// RuntimeOrchestrator, via SessionManager's own predicate) should pass true.
    /// </param>
    public SchedulingDecision TryAdmit(
        RuntimeRoleBinding binding,
        VramBudget budget,
        RuntimeOptions? options = null,
        bool baseWeightsAlreadyResident = false)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(budget);

        var lane = binding.Role is RuntimeRole.Boss or RuntimeRole.Reviewer
            ? SchedulingLane.Interactive
            : SchedulingLane.Background;

        // Resolve the actual requested GPU-layer count (when resolvable) BEFORE the first
        // estimate, so the very first admission check evaluates against what the caller
        // actually asked for -- not unconditionally against full GPU residency (CodeRabbit
        // review, PR #100: the old code always estimated full residency first and only ever
        // searched DOWN from requestedLayers-1, so an explicit partial GpuLayers request that
        // already fit got needlessly reduced further, and GpuLayers=0 (explicit CPU-only)
        // could never be evaluated by the search loop at all -- its own upper bound,
        // requestedLayers-1 = -1, was already below its lower bound of 0 before the loop
        // started, so a CPU-only request that WOULD fit was denied outright).
        var header = options is not null ? GgufMetadataReader.TryRead(binding.BaseModel.Path) : null;
        var requestedLayers = header is { BlockCount: > 0 }
            ? options!.GpuLayers < 0 ? header.BlockCount : Math.Min(options.GpuLayers, header.BlockCount)
            : (int?)null;

        var requiredBytes = requestedLayers is { } rl
            ? EstimateRequiredBytes(binding, options, baseWeightsAlreadyResident, gpuLayerOverride: rl)
            : EstimateRequiredBytes(binding, options, baseWeightsAlreadyResident);
        if (requiredBytes <= budget.AvailableBytes)
            return new SchedulingDecision(Admitted: true, Lane: lane);

        // The requested residency doesn't fit. Rather than refuse outright, look for the
        // largest GPU-layer count BELOW what was requested that DOES fit and admit in degraded
        // (partial GPU + CPU) mode — "still try to run, slower, instead of a hard denial on
        // every turn" per hardcoreerik, 2026-08-01. Only possible when a real layer count was
        // resolved above; the legacy file-size-only estimate (no options, or an unreadable
        // header) has no notion of layers to reduce, so it keeps the original bare denial.
        // requestedLayers == 0 (explicit CPU-only) means the check just above WAS the
        // CPU-only check, so triedCpuOnly is already known without entering the loop below.
        var triedCpuOnly = requestedLayers == 0;
        if (header is { BlockCount: > 0 } h && requestedLayers is > 0 and var startLayers)
        {
            for (var layers = startLayers - 1; layers >= 0; layers--)
            {
                triedCpuOnly = layers == 0;
                var degradedBytes = EstimateRequiredBytes(
                    binding, options, baseWeightsAlreadyResident, gpuLayerOverride: layers);
                if (degradedBytes > budget.AvailableBytes)
                    continue;

                var reason = layers == 0
                    ? $"Full GPU residency needs ~{FormatGb(requiredBytes)}, only " +
                      $"{FormatGb(budget.AvailableBytes)} available — running all " +
                      $"{h.BlockCount} layers on CPU instead. Generation will be much slower."
                    : $"Full GPU residency needs ~{FormatGb(requiredBytes)}, only " +
                      $"{FormatGb(budget.AvailableBytes)} available — offloading {layers}/" +
                      $"{h.BlockCount} layers to GPU, the rest on CPU. Generation will be slower.";
                return new SchedulingDecision(
                    Admitted: true, Lane: lane, Reason: reason, EffectiveGpuLayers: layers);
            }
        }

        return new SchedulingDecision(
            Admitted: false,
            Lane: lane,
            Reason: $"Requires ~{FormatGb(requiredBytes)}, only {FormatGb(budget.AvailableBytes)} available" +
                    (triedCpuOnly
                        ? " (even running fully on CPU does not fit the fixed CUDA/compute overhead)."
                        : "."));
    }

    // internal (not private): RuntimeOrchestrator needs the same estimate to maintain its own
    // active-reservation accounting across concurrent role admissions (see its ReservedBytes
    // doc) — duplicating the estimate in two places would let them drift out of sync silently.
    /// <param name="baseWeightsAlreadyResident">
    /// True when the caller has established (via SessionManager's own reuse predicate, not a
    /// guess) that admitting this binding will REUSE base weights already in VRAM instead of
    /// loading a second copy. SessionManager keeps a single shared base load, so when two roles
    /// resolve to the same GGUF the second one costs only its own context — charging it a full
    /// fresh load is not conservatism, it is a wrong number, and on a card sized for one model it
    /// makes a concurrent second role permanently unadmittable even though it fits. Found by
    /// HV-3's concurrent-role phase (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md, 2026-07-25).
    ///
    /// Base weights and the one-time CUDA runtime overhead are dropped in that case; the KV
    /// cache, compute buffer, recurrent state and adapter are NOT, because AdapterManager builds
    /// a separate persistent executor (and therefore a separate context) per role.
    /// </param>
    /// <param name="gpuLayerOverride">
    /// Null (the default) estimates full GPU residency — every existing caller's exact previous
    /// behavior. A non-null value estimates admitting with only that many of the model's
    /// transformer blocks resident on the GPU (the rest run on CPU via llama.cpp's own partial
    /// offload): the base-weight and KV-cache terms scale down proportionally to
    /// <c>gpuLayerOverride / header.BlockCount</c>, since only the GPU-resident layers' weights
    /// and KV entries occupy VRAM — the CUDA runtime overhead and compute-buffer allowance stay
    /// fixed regardless of how few layers are offloaded (used by TryAdmit's degraded-admission
    /// search, see its doc; not exposed there as "CPU offload" to keep this method a pure
    /// estimator with no admission policy of its own).
    /// </param>
    internal static long EstimateRequiredBytes(
        RuntimeRoleBinding binding,
        RuntimeOptions? options = null,
        bool baseWeightsAlreadyResident = false,
        int? gpuLayerOverride = null)
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

        // Only the GPU-resident fraction of the base weights and their KV entries occupy VRAM;
        // layers left on CPU (partial offload) cost nothing here. 1.0 (no scaling) when the
        // caller didn't ask for a reduced layer count, matching every pre-existing caller exactly.
        var gpuFraction = gpuLayerOverride is { } layers
            ? Math.Clamp(layers, 0, header.BlockCount) / (double)header.BlockCount
            : 1.0;
        var gpuBaseBytes = (long)(baseBytes * gpuFraction);
        var gpuKvBytes = (long)(kvBytes * gpuFraction);

        // The reuse discount is applied ONLY here, on the context-aware path -- never on the two
        // legacy returns above. Those are file-size-only estimates with no term representing the
        // context, so dropping the base there would charge a reusing role literally nothing and
        // let roles over-commit VRAM without bound (caught by
        // EnsureAdmitted_TracksReservationsAcrossRoles_DeniesSecondRoleWhenBudgetExhausted, which
        // is exactly the protection that must not regress). Down here kvBytes and the compute
        // buffer price the real increment, so removing the shared base is a correction rather
        // than a giveaway. CUDA runtime overhead is likewise per-process, paid at the first load.
        var sharedBaseCredit = baseWeightsAlreadyResident ? gpuBaseBytes : 0;
        var cudaOverhead = baseWeightsAlreadyResident ? 0 : CudaRuntimeOverheadBytes;

        return gpuBaseBytes + adapterBytes - sharedBaseCredit + cudaOverhead + gpuKvBytes
               + ComputeBufferAllowanceBytes + recurrentBytes;
    }

    private static string FormatGb(long bytes) =>
        $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
