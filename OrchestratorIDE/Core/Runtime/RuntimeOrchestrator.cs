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
/// shared base model load (docs/RUNTIME_PHASE0_SPEC.md §3 — "persistent base model", singular). If
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
    private readonly Func<VramBudget?>? _budgetProvider;

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
    // Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3): a lifetime counter of
    // every admission denial (both the fail-closed "no scheduler/budget configured" case and a
    // real capacity denial), plus the most recent reason -- guarded by _telemetryGate like
    // _reservedByRole, for the same reason (EnsureAdmitted writes it from the async admission
    // flow; GetReservationSnapshot reads it synchronously without waiting on _admissionGate).
    // Not cleared on dispose -- ThrowIfDisposed already blocks reads after disposal, so there is
    // nothing left to protect by resetting it.
    private long _rejectedAdmissionCount;
    private string? _lastRejectionReason;
    // Same shape and guard as the rejection counters above, for the "admitted but degraded to
    // partial GPU + CPU offload" case (hardcoreerik, 2026-08-01) — a distinct lifetime counter
    // rather than folding it into _rejectedAdmissionCount because this path is NOT a denial: the
    // role loads and runs, just slower. Conflating the two would make "how many admissions
    // actually failed" impossible to read off the snapshot.
    private long _degradedAdmissionCount;
    private string? _lastDegradedReason;
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

    private readonly bool _allowUnbudgetedExecution;

    /// <param name="runtime">
    /// Owned by both managers this constructs. Pass <paramref name="disposeRuntime"/> = true if
    /// this RuntimeOrchestrator should own the runtime's lifetime too (disposing it alongside
    /// SessionManager/AdapterManager on DisposeAsync); false if some other owner disposes it.
    /// </param>
    /// <param name="allowUnbudgetedExecution">
    /// Native Runtime v2.0 Phase A (docs/NATIVE_RUNTIME_V2_SPEC.md §1.3): when
    /// <paramref name="scheduler"/> or <paramref name="budgetProvider"/> is null, admission is
    /// otherwise unenforceable. Default <see langword="false"/> means that condition now fails
    /// CLOSED — <see cref="EnsureAdmitted"/> throws rather than silently loading unadmitted, the
    /// fail-open behavior this parameter replaces. Pass <see langword="true"/> only for an
    /// explicit, caller-acknowledged opt-out (e.g. a benchmark harness deliberately running
    /// without scheduler wiring) — the caller must still surface that choice to the user/log
    /// itself; this constructor does not log it, it only honors it.
    /// </param>
    public RuntimeOrchestrator(
        LLamaSharpRuntime runtime,
        bool disposeRuntime = false,
        IOrcScheduler? scheduler = null,
        Func<VramBudget?>? budgetProvider = null,
        bool allowUnbudgetedExecution = false)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        _runtime = runtime;
        _sessionManager = new SessionManager(runtime, disposeRuntime);
        _adapterManager = new AdapterManager(runtime);
        _scheduler = scheduler;
        _budgetProvider = budgetProvider;
        _allowUnbudgetedExecution = allowUnbudgetedExecution;
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

            var (admittedBytes, effectiveOptions, degradedReason) = EnsureAdmitted(binding, options);

            var loadResult = await _sessionManager.LoadBindingAsync(binding, effectiveOptions, ct).ConfigureAwait(false);
            if (!loadResult.Success || loadResult.Binding is null)
                throw new InvalidOperationException(
                    $"Could not load base model for role {binding.Role}: {loadResult.Message}");

            var conversation = await _adapterManager
                .CreateConversationAsync(loadResult.Binding, ct)
                .ConfigureAwait(false);

            // Record the degraded-admission telemetry only now, after the load and conversation
            // create this admission was granted for have actually succeeded (CodeRabbit review,
            // PR #100: recording it inside EnsureAdmitted, before either of those ran, meant a
            // load or conversation-create failure still left the counter claiming a successful
            // degraded admission that never happened). Beside the reservation commit below for
            // the same reason that commit itself waits until here.
            if (degradedReason is not null)
                RecordDegradation(degradedReason);

            // Commit only now, with the generation observed AFTER the load — never the pre-load
            // generation EnsureAdmitted saw. The load above may have been the one that bumped
            // WeightsGeneration (first-ever load) or left it untouched (same-base-model reuse);
            // tagging with whatever it actually is right now is what makes a LATER call's
            // generation-match filter correct in both cases. Same options as the admission
            // check above — the ledger entry must be the same number TryAdmit was checked with,
            // or a later role's admission would be judged against a different-sized footprint.
            // That number is now RETURNED by EnsureAdmitted rather than recomputed here: the
            // estimate depends on whether the base weights were already resident, and the load
            // that just happened is exactly what changes that answer, so recomputing would record
            // a first-ever load as though it had reused weights it actually paid for.
            if (_scheduler is not null && _budgetProvider is not null)
            {
                lock (_telemetryGate)
                {
                    // NEVER let this role's own ledger entry shrink within the same generation.
                    // `admittedBytes` is `EnsureAdmitted`'s returned requiredBytes, which is the
                    // INCREMENTAL cost when this call reuses the base already resident from an
                    // earlier call by this same role, or the FULL footprint on a fresh load. A
                    // role that already paid the full cost once, then later reuses its own
                    // resident base, would otherwise have its entry silently overwritten with
                    // the smaller reuse number here -- even though nothing was actually freed on
                    // the GPU. In the static-budget fallback (`ReservedBytes: 0`, no live probe),
                    // this ledger is the ONLY signal EnsureAdmitted has for a later role's
                    // admission check, so an artificially shrunk entry under-counts real VRAM
                    // usage and can over-admit into an actual OOM. Found by Grok's review of this
                    // PR, not by any HV run -- the shrink needs two calls from the same role
                    // across a reuse transition, which this session's evidence never happened to
                    // exercise in that order.
                    //
                    // A genuine decrease must still be honoured, and can only ever be legitimate
                    // alongside a generation change (a real recycle actually freed memory) --
                    // hence the floor is generation-scoped, the same way EnsureAdmitted already
                    // scopes `thisRolePriorReserved` a few lines above. A stale-generation prior
                    // entry describes a footprint that no longer exists and must not pin anything.
                    var newGeneration = _runtime.WeightsGeneration;
                    var floorBytes = _reservedByRole.TryGetValue(binding.Role, out var prior)
                                      && prior.Generation == newGeneration
                        ? prior.Bytes
                        : 0L;
                    _reservedByRole[binding.Role] = (Math.Max(floorBytes, admittedBytes), newGeneration);
                }
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
    /// Forwards to <see cref="AdapterManager.GetResidencySnapshot"/> — Native Runtime v2.0
    /// Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3). Exposed here for the same reason as
    /// <see cref="MarkRoleDegraded"/>: callers (IRoleRuntime) only hold a RuntimeOrchestrator
    /// reference, not the AdapterManager directly. ThrowIfDisposed at this level for a
    /// consistent exception (same pattern as <see cref="GetReservationSnapshot"/>), even though
    /// AdapterManager's own accessor would eventually throw too once disposed.
    /// </summary>
    public IReadOnlyList<AdapterRoleResidency> GetResidencySnapshot()
    {
        ThrowIfDisposed();
        return _adapterManager.GetResidencySnapshot();
    }

    /// <summary>
    /// Pure check, no bookkeeping write — the caller (<see cref="GetConversationForBindingAsync"/>,
    /// holding <see cref="_admissionGate"/> for the whole pipeline) only commits a reservation
    /// after the load and conversation build both succeed. Throws
    /// <see cref="RuntimeAdmissionDeniedException"/> if denied — including when no
    /// scheduler/budget is configured and <see cref="_allowUnbudgetedExecution"/> is false (the
    /// Phase A fail-closed fix: this used to be a silent no-op, letting native execution proceed
    /// with zero admission control whenever the caller didn't wire a budget — see
    /// docs/NATIVE_RUNTIME_V2_SPEC.md §1.2 Gap 2). Deliberately reuses
    /// <see cref="RuntimeAdmissionDeniedException"/> rather than a new exception type for this
    /// case too: <see cref="NativeWithFallbackRuntime.ShouldFallback"/> already excludes this
    /// exact type from its fallback-eligible set, so an unconfigured budget fails closed the same
    /// way a real capacity denial does, instead of being silently rerouted to Ollama.
    ///
    /// internal (not private): unit-tested directly in RuntimeOrchestratorTests, same rationale
    /// as AdapterManager.BindingMatches — this method touches no native LLamaSharp objects (only
    /// LLamaSharpRuntime.WeightsGeneration, a managed counter), so it can be exercised in
    /// isolation without a real model load, unlike the rest of GetConversationForBindingAsync.
    /// </summary>
    /// <returns>
    /// RequiredBytes: the footprint this admission was granted against, for the caller to record
    /// verbatim in the reservation ledger. Zero when admission is bypassed via
    /// allowUnbudgetedExecution (there is no budget to account against in that mode).
    /// EffectiveOptions: the options the caller must actually load with — identical to
    /// <paramref name="options"/> unless the scheduler could not fit the full request and
    /// degraded to a smaller GpuLayers (partial GPU + CPU), in which case this carries that
    /// reduced value. Loading with the original <paramref name="options"/> instead would silently
    /// re-attempt full GPU residency and defeat the whole point of the degraded admission.
    /// DegradedReason: non-null exactly when a degraded admission happened (mirrors
    /// EffectiveGpuLayers being set). The caller records this via RecordDegradation itself, only
    /// AFTER the load and conversation-create this admission was granted for actually succeed
    /// (CodeRabbit review, PR #100: recording it here, before either of those run, meant a load
    /// or conversation-create failure still left the telemetry counter claiming a successful
    /// degraded admission that never actually happened).
    /// </returns>
    internal (long RequiredBytes, RuntimeOptions? EffectiveOptions, string? DegradedReason) EnsureAdmitted(
        RuntimeRoleBinding binding, RuntimeOptions? options = null)
    {
        if (_scheduler is null || _budgetProvider is null)
        {
            if (_allowUnbudgetedExecution)
                return (0, options, null);

            var unavailableDecision = new SchedulingDecision(
                Admitted: false,
                Lane: binding.Role is RuntimeRole.Boss or RuntimeRole.Reviewer
                    ? SchedulingLane.Interactive
                    : SchedulingLane.Background,
                Reason: "No VRAM scheduler/budget is configured for native execution, so admission " +
                        "cannot be evaluated. Failing closed rather than loading unadmitted " +
                        "(Native Runtime v2.0 Phase A). Construct the runtime with a scheduler + " +
                        "budgetProvider, or pass allowUnbudgetedExecution: true to explicitly opt out.");
            RecordRejection(unavailableDecision.Reason);
            throw new RuntimeAdmissionDeniedException(
                binding, new VramBudget(TotalBytes: 0, ReservedBytes: 0), unavailableDecision);
        }

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
        //
        // The same exclusion must extend to the provider's OWN ReservedBytes. When that provider
        // is a live whole-GPU probe (NativeVramProbe / nvidia-smi), baseline.ReservedBytes already
        // includes this role's resident model — and TryAdmit is about to charge a full fresh-load
        // EstimateRequiredBytes for it AGAIN. Re-admitting this role loads nothing new (reuse) or
        // frees the old footprint before building the replacement (rebind), so its resident bytes
        // must be credited back out of the baseline, or the one resident model is counted twice —
        // once as used (probe) and once as needed (estimate). Omitting this denied every job after
        // the first on a card too tight to hold two phantom copies (HardcorePC, RTX 3050 6 GB:
        // docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-1, 2026-07-21). Credit only up to what the
        // baseline actually reports (a probe that under-counts must not drive the budget negative).
        long otherRolesReserved;
        long thisRolePriorReserved;
        lock (_telemetryGate)
        {
            otherRolesReserved = _reservedByRole
                .Where(kv => kv.Key != binding.Role && kv.Value.Generation == currentGeneration)
                .Sum(kv => kv.Value.Bytes);
            thisRolePriorReserved =
                _reservedByRole.TryGetValue(binding.Role, out var own) && own.Generation == currentGeneration
                    ? own.Bytes
                    : 0;
        }
        // MAX, not SUM -- the same overlap the reporting snapshot had, and the reason HV-3's
        // concurrent-role phase could never pass. `baseline.ReservedBytes` from a live whole-GPU
        // probe already counts EVERY resident model, this role's and every other role's. Adding
        // otherRolesReserved on top charges the others twice: on HardcorePC (6 GB) admitting a
        // second role while the first was resident produced "reserved=10.3 GB" against a 6.0 GB
        // card and denied every concurrent role outright
        // (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-3, 2026-07-25).
        //
        // Subtracting this role's own prior footprint from the probe stays (that is the HV-1
        // fix, and it is why the two operands are not simply symmetric): re-admitting a role
        // loads nothing new, so its already-counted bytes must come back out before the estimate
        // is charged. The ledger arm is the floor for the case where no live probe exists --
        // the static VramBudget(total, ReservedBytes: 0) fallback -- where it is the only signal.
        var effectiveReserved = Math.Max(
            Math.Max(0, baseline.ReservedBytes - thisRolePriorReserved),
            otherRolesReserved);
        var budget = baseline with { ReservedBytes = effectiveReserved };

        // Ask SessionManager whether this admission will actually load weights or reuse the ones
        // already resident, rather than assuming a fresh load. Same predicate the loader itself
        // uses, so the estimate cannot disagree with what happens next.
        var reusesBaseWeights = _sessionManager.WouldReuseLoadedBaseWeights(binding, options);
        var requiredBytes = OrcScheduler.EstimateRequiredBytes(binding, options, reusesBaseWeights);

        var decision = _scheduler.TryAdmit(binding, budget, options, reusesBaseWeights);
        if (!decision.Admitted)
        {
            RecordRejection(decision.Reason);
            throw new RuntimeAdmissionDeniedException(binding, budget, decision);
        }

        var effectiveOptions = options;
        string? degradedReason = null;
        if (decision.EffectiveGpuLayers is { } reducedLayers)
        {
            // TryAdmit could not fit the full-offload request but found a smaller GPU-layer
            // count that does (hardcoreerik, 2026-08-01: degrade to CPU-assisted rather than
            // hard-deny every chat turn on a VRAM-tight box). Recompute the estimate against
            // THAT reduced footprint -- not the full-offload requiredBytes above -- so the
            // reservation ledger reflects what was actually admitted, and rebuild the options
            // the caller loads with so the reduced GpuLayers actually takes effect (options is
            // never null here: TryAdmit only sets EffectiveGpuLayers when it was given options
            // to search against). RecordDegradation itself is NOT called here -- see
            // DegradedReason's doc above; the caller records it after the load it's granting
            // actually succeeds.
            requiredBytes = OrcScheduler.EstimateRequiredBytes(
                binding, options, reusesBaseWeights, gpuLayerOverride: reducedLayers);
            effectiveOptions = options! with { GpuLayers = reducedLayers };
            degradedReason = decision.Reason;
        }

        // requiredBytes is returned so the caller commits the EXACT number that was admitted.
        // Recomputing it after the load would silently use a different residency answer -- the
        // first role's own load makes its base resident, so a post-load recompute would record
        // it as if it had reused weights it actually paid for, under-reporting it to every later
        // role. effectiveOptions is the original options unless TryAdmit degraded the request
        // (see above) -- the caller must load with THIS, not the original, or a reduced-GpuLayers
        // admission would silently attempt full GPU residency anyway.
        return (requiredBytes, effectiveOptions, degradedReason);
    }

    private void RecordRejection(string? reason)
    {
        lock (_telemetryGate)
        {
            _rejectedAdmissionCount++;
            _lastRejectionReason = reason;
        }
    }

    private void RecordDegradation(string? reason)
    {
        lock (_telemetryGate)
        {
            _degradedAdmissionCount++;
            _lastDegradedReason = reason;
        }
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
    /// Read-only snapshot of current VRAM admission state — docs/RUNTIME_PHASE0_SPEC.md §3's
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
        long rejectedCount;
        string? lastRejectionReason;
        long degradedCount;
        string? lastDegradedReason;
        lock (_telemetryGate)
        {
            active = _reservedByRole
                .Where(kv => kv.Value.Generation == currentGeneration)
                .Select(kv => new RuntimeRoleReservation(kv.Key, kv.Value.Bytes))
                .ToList();
            rejectedCount = _rejectedAdmissionCount;
            lastRejectionReason = _lastRejectionReason;
            degradedCount = _degradedAdmissionCount;
            lastDegradedReason = _lastDegradedReason;
        }
        // MAX, not SUM. The two inputs measure overlapping things, so adding them double-counts
        // every resident model — the same trap EnsureAdmitted documents above, in the reporting
        // path this time. When the provider is a live whole-GPU probe (NativeVramProbe /
        // nvidia-smi, which is what the daemon and the app both use),
        // baseline.ReservedBytes ALREADY includes every model currently loaded, and the ledger
        // entries below are this orchestrator's own accounting of those same models. Summing them
        // produced physically impossible telemetry: HV-3's first real run on HardcorePC (RTX 3050,
        // 6 GB) reported reservedBytes = 11.04 GB against totalBytes = 6.44 GB with
        // availableBytes pinned to 0, the excess matching the job's est_vram exactly
        // (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-3, 2026-07-25).
        //
        // Taking the larger keeps both providers honest: with a live probe the probe wins (it is
        // authoritative for what is physically in use, including non-TheOrc consumers on the same
        // card), and with the static fallback budget — VramBudget(total, ReservedBytes: 0), used
        // when nvidia-smi is unavailable — the ledger wins, which is the only signal there is in
        // that case. Known imprecision, accepted deliberately: a role that has been RESERVED but
        // whose model is not yet resident is not additive on top of the probe here, so this can
        // under-report during the window between reservation and load. Under-reporting a
        // telemetry read is strictly safer than reporting a number the hardware cannot produce,
        // and admission correctness does not depend on this value — EnsureAdmitted does its own
        // accounting and is unchanged by this fix.
        var reservedBytes = Math.Max(baseline.ReservedBytes, active.Sum(r => r.Bytes));

        return new RuntimeReservationSnapshot(
            active,
            baseline.TotalBytes,
            reservedBytes,
            AvailableBytes: Math.Max(0, baseline.TotalBytes - reservedBytes),
            RejectedAdmissionCount: rejectedCount,
            LastRejectionReason: lastRejectionReason,
            DegradedAdmissionCount: degradedCount,
            LastDegradedReason: lastDegradedReason);
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
/// itself uses for admission decisions. <see cref="RejectedAdmissionCount"/>/<see cref="LastRejectionReason"/>
/// and <see cref="DegradedAdmissionCount"/>/<see cref="LastDegradedReason"/> are both lifetime
/// counters (Native Runtime v2.0 Phase C, the latter added 2026-08-01) — every denial or
/// GPU-layer degradation since construction, not just ones still "active"; unlike
/// <see cref="Reservations"/> they are never generation-filtered since a past event doesn't
/// become stale the way a live reservation does. Degraded is distinct from rejected: a degraded
/// admission still loaded and is running, just on fewer GPU layers than requested.
/// </summary>
public sealed record RuntimeReservationSnapshot(
    IReadOnlyList<RuntimeRoleReservation> Reservations,
    long TotalBytes,
    long ReservedBytes,
    long AvailableBytes,
    long RejectedAdmissionCount,
    string? LastRejectionReason,
    long DegradedAdmissionCount = 0,
    string? LastDegradedReason = null);

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
