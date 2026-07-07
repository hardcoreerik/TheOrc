// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 3 — the single entry point connecting all three pieces:
/// <see cref="ModelDepot"/> (what GGUF/LoRA assets exist locally) resolves a role's binding,
/// <see cref="SessionManager"/> ensures the binding's base model is loaded into the shared
/// runtime (reusing the current load if it already matches), and <see cref="AdapterManager"/>
/// returns a per-role, adapter-attached <see cref="TrackedConversation"/> on a persistent
/// executor. Before this class, the three were standalone and nothing called all of them
/// together — this is what "Phase 3 is wired up" means.
///
/// <b>Constructs SessionManager and AdapterManager itself from one LLamaSharpRuntime, rather
/// than accepting independently-constructed instances of either.</b> The first draft took both
/// as constructor parameters — review caught that nothing then enforced they shared the same
/// underlying runtime. A caller could (accidentally) build a SessionManager over one
/// LLamaSharpRuntime and an AdapterManager over a different one: SessionManager would report a
/// successful load on instance A, then AdapterManager would call CreateBatchedExecutor on
/// instance B, which never had LoadModelAsync called on it, and throw "No model loaded" despite
/// the success check just above having passed. Owning construction from a single instance makes
/// that mismatch structurally impossible instead of relying on caller discipline.
///
/// <b>Known scope limitation, not a bug introduced here:</b> SessionManager manages a single
/// shared base model load (RUNTIME_PHASE0_SPEC.md §3 — "persistent base model", singular). If
/// two roles resolve to <i>different</i> base GGUF files, switching between them forces a real
/// reload (LLamaSharpRuntime.LoadModelAsync disposes the previous weights), which bumps
/// WeightsGeneration, which — correctly, per AdapterManager's own invalidation rule — tears
/// down every role's executor, not just the one being requested. For a warband where every
/// role shares one base model with different LoRAs (the documented common case), this never
/// triggers: SessionManager's CanReuseCurrentSession short-circuits on a matching base path with
/// no reload, no generation bump, no invalidation. It only becomes a real problem if/when the
/// warband needs multiple different base models loaded concurrently — at that point
/// SessionManager itself needs to become base-model-keyed instead of singular, which is out of
/// scope for this slice.
///
/// <b>Verification scope:</b> structurally guaranteed by construction (this class owns both
/// managers from one runtime) and Grok-reviewed for the wiring logic itself; the actual success
/// path — a real model load followed by a real adapter-attached generation — is not exercised
/// by an automated test, same precedent as AdapterManager and LLamaSharpRuntime (no mockable
/// seam for the native LLamaSharp objects involved). Verified by the §7 spike harness and manual
/// smoke-testing, not NUnit.
/// </summary>
public sealed class RuntimeOrchestrator : IAsyncDisposable
{
    private readonly LLamaSharpRuntime _runtime;
    private readonly SessionManager _sessionManager;
    private readonly AdapterManager _adapterManager;
    private readonly IOrcScheduler? _scheduler;
    private readonly Func<VramBudget>? _budgetProvider;

    // Active-reservation accounting (the gap flagged in OrcScheduler's review: a static budget
    // snapshot with no tracking lets concurrent role admissions over-admit). Two review passes
    // landed on this shape — the first draft (a single check-then-write lock plus a global
    // "last seen generation, clear everything if it changed" flag) had two bugs a second review
    // caught:
    //
    // 1. TOCTOU between the budget check and the load: admitting role A and admitting role B are
    //    separate async operations: a lock held only around the cheap check-and-record step does
    //    not stop both from observing "nothing reserved yet" and both passing TryAdmit when only
    //    one of them actually fits. Fix: _admissionGate is an async-compatible SemaphoreSlim held
    //    across the ENTIRE check -> load -> commit pipeline for one role, not just the check, so
    //    only one role's admission can be in flight at a time. (SessionManager only supports one
    //    persistent base model anyway, and AdapterManager already serializes its own build step
    //    internally, so this does not remove real parallelism that previously existed safely —
    //    see the class doc's "known scope limitation" above.)
    //
    // 2. Generation timing: WeightsGeneration bumps DURING the load this method gates, not
    //    before it. A scheme that reads/stores "last seen generation" at admission-CHECK time
    //    (pre-load) sees the pre-load value, then the very next role's check sees the post-load
    //    value as "changed" and wrongly invalidates the role that JUST succeeded. Fix: each
    //    reservation is tagged with the generation observed AFTER its own load succeeded, not a
    //    single shared "last seen" scalar read before any load happens. A later admission only
    //    counts another role's reservation if its tagged generation still matches the runtime's
    //    current generation — which is automatically false once some load tears that role's
    //    executor down (mirrors AdapterManager's own per-call generation check), with no separate
    //    "clear everything" step needed.
    //
    // A consequence of committing only after success: there is nothing to roll back on failure.
    // No entry is written until the load and the conversation build both succeed, so a failed
    // admission costs nothing and a successful one is recorded with the generation it actually
    // landed under.
    private readonly Dictionary<RuntimeRole, (long Bytes, int Generation)> _reservedByRole = new();
    private readonly SemaphoreSlim _admissionGate = new(1, 1);
    // Separate from _admissionGate: that semaphore serializes the async admission DECISION
    // pipeline (check -> load -> commit), but Dictionary itself is not thread-safe even for a
    // read concurrent with a write, and GetReservationSnapshot is a synchronous telemetry read
    // that deliberately does NOT wait on _admissionGate (it must never block behind an in-flight
    // model load). Without a separate guard, a UI thread calling GetReservationSnapshot while
    // GetConversationForBindingAsync commits or DisposeAsync clears could throw
    // InvalidOperationException ("Collection was modified") — a review pass caught this missing
    // from the first telemetry draft. Held only for the brief, synchronous dictionary touch in
    // each of the four call sites below, never across an await.
    private readonly object _telemetryGate = new();
    private bool _disposed;

    /// <param name="runtime">
    /// Owned by both managers this constructs. Pass <paramref name="disposeRuntime"/> = true if
    /// this RuntimeOrchestrator should own the runtime's lifetime too (disposing it alongside
    /// SessionManager/AdapterManager on DisposeAsync); false if some other owner disposes it.
    /// </param>
    public RuntimeOrchestrator(
        LLamaSharpRuntime runtime,
        bool disposeRuntime = false,
        IOrcScheduler? scheduler = null,
        Func<VramBudget>? budgetProvider = null)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _sessionManager = new SessionManager(runtime, disposeRuntime);
        _adapterManager = new AdapterManager(runtime);
        _scheduler = scheduler;
        _budgetProvider = budgetProvider;
    }

    /// <summary>
    /// Resolves <paramref name="role"/> against <paramref name="depot"/>, ensures the resolved
    /// base model is loaded (a no-op if it's already the currently loaded base), then returns a
    /// reference-counted conversation on that role's persistent, adapter-attached executor.
    /// Dispose the returned handle when done with it.
    /// </summary>
    public async Task<TrackedConversation> GetConversationForRoleAsync(
        ModelDepot depot,
        RuntimeRole role,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(depot);

        var binding = depot.ResolveRole(role);
        if (binding is null)
            throw new InvalidOperationException($"No base GGUF resolved for runtime role {role}.");

        return await GetConversationForBindingAsync(binding, options, ct).ConfigureAwait(false);
    }

    public async Task<TrackedConversation> GetConversationForBindingAsync(
        RuntimeRoleBinding binding,
        RuntimeOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ThrowIfDisposed();

        await _admissionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Re-check after the wait: a concurrent DisposeAsync could have started (and set
            // _disposed) while this call was queued on the gate. Without this, a caller that
            // raced disposal would proceed to load/create against managers already mid-teardown
            // (same hazard class AdapterManager/SessionManager/NativeRoleRuntime already guard
            // against on their own gates — this class was missing the equivalent check).
            ThrowIfDisposed();

            EnsureAdmitted(binding);

            var loadResult = await _sessionManager.LoadBindingAsync(binding, options, ct).ConfigureAwait(false);
            if (!loadResult.Success || loadResult.Binding is null)
                throw new InvalidOperationException(
                    $"Could not load base model for role {binding.Role}: {loadResult.Message}");

            var conversation = await _adapterManager
                .CreateConversationAsync(loadResult.Binding, ct)
                .ConfigureAwait(false);

            // Commit only now, with the generation observed AFTER the load — never the pre-load
            // generation EnsureAdmitted saw. The load above may have been the one that bumped
            // WeightsGeneration (first-ever load) or left it untouched (same-base-model reuse);
            // tagging with whatever it actually is right now is what makes a LATER call's
            // generation-match filter correct in both cases.
            if (_scheduler is not null && _budgetProvider is not null)
            {
                var requiredBytes = OrcScheduler.EstimateRequiredBytes(binding);
                lock (_telemetryGate)
                    _reservedByRole[binding.Role] = (requiredBytes, _runtime.WeightsGeneration);
            }

            return conversation;
        }
        finally
        {
            _admissionGate.Release();
        }
    }

    /// <summary>
    /// Forwards to <see cref="AdapterManager.MarkForRecycle"/> — see that method's doc comment.
    /// Exposed here because callers (IRoleRuntime) only hold a RuntimeOrchestrator reference,
    /// not the AdapterManager directly.
    /// </summary>
    public Task MarkRoleDegraded(RuntimeRole role) =>
        _adapterManager.MarkForRecycle(role);

    /// <summary>
    /// Pure check, no bookkeeping write — the caller (<see cref="GetConversationForBindingAsync"/>,
    /// holding <see cref="_admissionGate"/> for the whole pipeline) only commits a reservation
    /// after the load and conversation build both succeed. Throws
    /// <see cref="RuntimeAdmissionDeniedException"/> if denied.
    /// </summary>
    private void EnsureAdmitted(RuntimeRoleBinding binding)
    {
        if (_scheduler is null || _budgetProvider is null)
            return;

        var currentGeneration = _runtime.WeightsGeneration;
        var baseline = _budgetProvider()
            ?? throw new InvalidOperationException(
                "Native Runtime budget provider returned null; cannot evaluate admission.");

        // The provider's own ReservedBytes (if any) plus every OTHER role's footprint — but only
        // entries whose tagged generation still matches the runtime's current generation. A
        // mismatch means that role's executor was torn down by some load that happened since
        // (mirrors AdapterManager's own per-call generation check), so counting it would
        // under-report what's actually available. Never this role's own prior footprint:
        // admitting it again either reuses the existing executor (no new memory) or tears the old
        // one down before building the replacement (old footprint already gone by the time the
        // new one would exist) — counting it here would double-charge a role against itself.
        long otherRolesReserved;
        lock (_telemetryGate)
            otherRolesReserved = _reservedByRole
                .Where(kv => kv.Key != binding.Role && kv.Value.Generation == currentGeneration)
                .Sum(kv => kv.Value.Bytes);
        var budget = baseline with { ReservedBytes = baseline.ReservedBytes + otherRolesReserved };

        var decision = _scheduler.TryAdmit(binding, budget);
        if (!decision.Admitted)
            throw new RuntimeAdmissionDeniedException(binding, budget, decision);
    }

    public async ValueTask DisposeAsync()
    {
        // Wait for the gate the same way every admission does, so disposal cannot interleave
        // with an in-flight GetConversationForBindingAsync call (which would otherwise drive a
        // load/create against managers already mid-teardown). Once acquired, _disposed is set
        // before release so any call that was queued behind this wait sees it on its own
        // post-wait ThrowIfDisposed() check instead of proceeding.
        await _admissionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                await _adapterManager.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                // Must run even if AdapterManager's disposal faults — otherwise a failure tearing
                // down per-role executors would leak the SessionManager (and, if disposeRuntime
                // was true, the runtime/weights it owns) entirely.
                await _sessionManager.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_telemetryGate)
                _reservedByRole.Clear();
            // _admissionGate is intentionally never Dispose()'d here, same rationale as
            // AdapterManager's own gate (see its class doc point 5): a thread already queued on
            // WaitAsync before disposal began must not have its own `finally { Release() }` throw
            // against a semaphore that was disposed out from under it.
            _admissionGate.Release();
        }
    }

    /// <summary>
    /// Read-only snapshot of current VRAM admission state — RUNTIME_PHASE0_SPEC.md §3's
    /// "surface SessionManager/AdapterManager-backed telemetry" item. Deliberately does NOT wait
    /// on <see cref="_admissionGate"/>: this is a status read for UI/diagnostics, not a decision,
    /// so it never blocks behind an in-flight model load. It still takes the short, synchronous
    /// <see cref="_telemetryGate"/> lock around the dictionary touch (see that field's doc) —
    /// without it, this read could throw a concurrent-modification exception against a write or
    /// clear happening on another thread, not just return a stale value. With the lock, the
    /// remaining race is benign: a snapshot taken concurrently with disposal may reflect either
    /// just-before or just-after the clear, never a torn read, and
    /// <see cref="EnsureAdmitted"/> never trusts this snapshot — it always re-reads the live
    /// state itself under both gates when it makes an actual admission decision.
    /// Returns null if no scheduler/budget provider is configured (admission control is a no-op,
    /// so there is nothing meaningful to report) or if the budget provider throws/returns null.
    /// </summary>
    public RuntimeReservationSnapshot? GetReservationSnapshot()
    {
        ThrowIfDisposed();

        if (_scheduler is null || _budgetProvider is null)
            return null;

        VramBudget? baseline;
        try
        {
            baseline = _budgetProvider();
        }
        catch
        {
            // Best-effort telemetry: a misbehaving provider must not crash a status display.
            // EnsureAdmitted is the path that actually enforces correctness and will surface a
            // clear failure there instead.
            return null;
        }

        if (baseline is null)
            return null;

        var currentGeneration = _runtime.WeightsGeneration;
        List<RuntimeRoleReservation> active;
        lock (_telemetryGate)
            active = _reservedByRole
                .Where(kv => kv.Value.Generation == currentGeneration)
                .Select(kv => new RuntimeRoleReservation(kv.Key, kv.Value.Bytes))
                .ToList();
        var reservedBytes = baseline.ReservedBytes + active.Sum(r => r.Bytes);

        return new RuntimeReservationSnapshot(
            active,
            baseline.TotalBytes,
            reservedBytes,
            AvailableBytes: Math.Max(0, baseline.TotalBytes - reservedBytes));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(RuntimeOrchestrator));
    }
}

/// <summary>One role's current VRAM footprint as tracked by <see cref="RuntimeOrchestrator"/>.</summary>
public sealed record RuntimeRoleReservation(RuntimeRole Role, long Bytes);

/// <summary>
/// Point-in-time view of <see cref="RuntimeOrchestrator"/>'s admission state. <see cref="Reservations"/>
/// lists only roles whose reservation generation still matches the runtime's current generation —
/// stale entries (torn down by an intervening reload) are excluded, same filter <see cref="RuntimeOrchestrator"/>
/// itself uses for admission decisions.
/// </summary>
public sealed record RuntimeReservationSnapshot(
    IReadOnlyList<RuntimeRoleReservation> Reservations,
    long TotalBytes,
    long ReservedBytes,
    long AvailableBytes);

public sealed class RuntimeAdmissionDeniedException : InvalidOperationException
{
    public RuntimeAdmissionDeniedException(
        RuntimeRoleBinding binding,
        VramBudget budget,
        SchedulingDecision decision)
        : base(BuildMessage(binding, budget, decision))
    {
        Binding = binding ?? throw new ArgumentNullException(nameof(binding));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Decision = decision ?? throw new ArgumentNullException(nameof(decision));
    }

    public RuntimeRoleBinding Binding { get; }

    public VramBudget Budget { get; }

    public SchedulingDecision Decision { get; }

    private static string BuildMessage(
        RuntimeRoleBinding binding,
        VramBudget budget,
        SchedulingDecision decision)
    {
        var adapterLabel = binding.Adapter is null ? "" : $" + {binding.Adapter.DisplayName}";
        return $"Runtime admission denied for {binding.Role} ({binding.BaseModel.DisplayName}{adapterLabel}, lane {decision.Lane}): {decision.Reason ?? "scheduler denied the request."} " +
               $"Budget total={FormatGb(budget.TotalBytes)}, reserved={FormatGb(budget.ReservedBytes)}, available={FormatGb(budget.AvailableBytes)}.";
    }

    private static string FormatGb(long bytes) => $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
}
