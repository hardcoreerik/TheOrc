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
    /// </summary>
    SchedulingDecision TryAdmit(RuntimeRoleBinding binding, VramBudget budget);
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
/// First real OrcScheduler capability (Phase 4): a VRAM-budget admission check using
/// RuntimeModelAsset.SizeBytes as the cost estimate. GGUF file size on disk is a reasonable
/// proxy for VRAM usage when loaded (not exact — quantization/context-size overhead vary it —
/// but precise enough for an admission decision, which is inherently a margin-of-safety check,
/// not a guarantee).
/// </summary>
public sealed class OrcScheduler : IOrcScheduler
{
    // LoRA adapters are small relative to base models (tens to a few hundred MB). When the
    // adapter's size is unknown — e.g. a PEFT directory, which AdapterManager doesn't even load
    // directly today (LoadLoraFromFile requires a GGUF path) — this is a deliberately
    // conservative fallback estimate, not zero cost and not a refusal for missing metadata.
    internal const long UnknownAdapterSizeEstimateBytes = 512L * 1024 * 1024; // 512 MB

    public SchedulingDecision TryAdmit(RuntimeRoleBinding binding, VramBudget budget)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(budget);

        var lane = binding.Role is RuntimeRole.Boss or RuntimeRole.Reviewer
            ? SchedulingLane.Interactive
            : SchedulingLane.Background;

        var requiredBytes = EstimateRequiredBytes(binding);
        if (requiredBytes <= budget.AvailableBytes)
            return new SchedulingDecision(Admitted: true, Lane: lane);

        return new SchedulingDecision(
            Admitted: false,
            Lane: lane,
            Reason: $"Requires ~{FormatGb(requiredBytes)}, only {FormatGb(budget.AvailableBytes)} available.");
    }

    // internal (not private): RuntimeOrchestrator needs the same estimate to maintain its own
    // active-reservation accounting across concurrent role admissions (see its ReservedBytes
    // doc) — duplicating the GGUF-size-as-VRAM-proxy heuristic in two places would let them
    // drift out of sync silently.
    internal static long EstimateRequiredBytes(RuntimeRoleBinding binding)
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
        return baseBytes + adapterBytes;
    }

    private static string FormatGb(long bytes) =>
        $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
