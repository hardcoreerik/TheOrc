// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 4 — capability + VRAM + lane-aware dispatch (RUNTIME_PHASE0_SPEC.md §6).
///
/// Not to be confused with <see cref="OrchestratorIDE.Services.Hive.HiveScheduler"/>, which
/// picks a remote HIVE node for a task based on free VRAM + model-hint matching — a distributed
/// concern. OrcScheduler is the opposite scope: given AdapterManager has no VRAM awareness today
/// (it will happily try to build a persistent executor for every role requested, regardless of
/// whether there's actually room), OrcScheduler decides whether a role's request can be admitted
/// right now against a single process's VRAM budget, or must be queued.
///
/// Interface + data model only in this slice — pure decision logic, no live GPU dispatch (no
/// actual VRAM measurement, no real queueing/pipeline execution). The next slice
/// (RUNTIME_PHASE0_SPEC.md §6, Phase 4 admission check) implements TryAdmit's real logic against
/// RuntimeModelAsset.SizeBytes estimates; this one defines the contract it implements.
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
/// itself; the caller (eventually RuntimeOrchestrator or AdapterManager) owns that bookkeeping
/// and passes the current snapshot in on every TryAdmit call.
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
