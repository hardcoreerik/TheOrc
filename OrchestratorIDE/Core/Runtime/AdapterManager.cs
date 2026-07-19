// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using LLama.Batched;
using LLama.Native;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 3 — per-role persistent LoRA contexts. Design rationale and the
/// empirical spike behind it: docs/RUNTIME_PHASE0_SPEC.md §7 (hot-swap spike) and §7a (this design).
///
/// One persistent <see cref="BatchedExecutor"/> per <see cref="RuntimeRole"/>. The role's
/// adapter (if any) is loaded and attached exactly once, when that role's executor is first
/// created — never again on that executor instance. §7 confirmed empirically that swapping
/// adapters on a live context with populated KV cache is silent and unsafe; the only safe
/// path is teardown+rebuild, which is what <see cref="RebindRoleAsync"/> does explicitly.
///
/// Safety properties added across two Grok review passes on the implementation (not just the
/// design — several of these only surface once real exception/concurrency paths exist):
/// 1. Conversations are reference-counted per role (<see cref="TrackedConversation"/>).
///    RebindRoleAsync refuses (throws) to tear down a role's executor while any conversation
///    from it is still outstanding.
/// 2. A weights-generation check: LLamaSharpRuntime.LoadModelAsync always disposes the
///    previous LLamaWeights before loading new ones, which would dangle every existing
///    RoleEntry, not just whichever role is next requested. Every call compares the runtime's
///    current generation against the one last seen and invalidates all entries if it changed.
/// 3. The active-conversation counter only increments after executor.Create() succeeds, and
///    only decrements inside a finally — so a throw on either side can never leave the count
///    permanently elevated (which would otherwise lock a role out of RebindRoleAsync forever).
/// 4. Building a new executor (CreateBatchedExecutor + LoadLoraFromFile + SetLoraAdapters) is
///    wrapped so a failure partway through disposes/unloads whatever was already created
///    instead of leaking it.
/// 5. The internal gate (SemaphoreSlim) is intentionally never disposed. Disposing it from
///    DisposeAsync while another thread might be mid-WaitAsync (queued before disposal began)
///    risks that thread's own `finally { _gate.Release() }` throwing ObjectDisposedException
///    against a semaphore that was disposed out from under it. SemaphoreSlim has no unmanaged
///    wait-handle cost unless AvailableWaitHandle is requested (never is, here), so leaving it
///    for GC is the standard, safe tradeoff — not an oversight.
/// </summary>
public sealed class AdapterManager : IAsyncDisposable
{
    private readonly LLamaSharpRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    // ConcurrentDictionary, not Dictionary: Native Runtime v2.0 Phase C adds GetResidencySnapshot,
    // a synchronous, non-blocking telemetry read (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3) that must
    // never wait on _gate (the async minting/rebinding pipeline) -- the same "telemetry reads
    // don't block behind an in-flight operation" rule RuntimeOrchestrator already follows via its
    // own _telemetryGate. _gate still exclusively serializes the higher-level async mutation
    // FLOW (check-then-act recycle decisions, native object creation) among concurrent minting
    // callers -- that invariant is unchanged. ConcurrentDictionary only adds thread-safe
    // enumeration/lookup so a lock-free telemetry read can never see a torn Dictionary mid-write
    // (InvalidOperationException: Collection was modified) or corrupt internal state.
    private readonly ConcurrentDictionary<RuntimeRole, RoleEntry> _entries = new();
    private int _lastSeenWeightsGeneration = -1;
    private bool _disposed;

    // llama.cpp KV-cache sequence slots are finite (LLAMA_MAX_SEQ, 256 in current builds) and
    // LLamaSharp 0.27's BatchedExecutor mints sequence ids monotonically with no recycling
    // (GetNextSequenceId is a bare increment, even for disposed Conversations). Once an executor
    // has handed out its last slot, the NEXT Conversation faults the native runtime with a
    // GGML_ASSERT (llama-kv-cache.cpp seq_to_stream bounds) and kills the process — observed
    // live on the first 1.8M-token unattended benchmark run, at exactly the 257th reader
    // conversation (~45 min in). Recycle the role's executor at a safe idle point well before
    // the cap; rebuilding costs one context allocation, not a weights reload.
    //
    // This threshold bounds sequence-ID *count*, not KV-cache *memory* — a distinct exhaustion
    // mode that shares the same "disposed conversations aren't reclaimed" root cause. The
    // 2026-07-04 CF-7 gate run hit native NoKvSlot decode failures (docs/CONTEXT_FABRIC_BUG_HISTORY.md
    // §7) well under the old threshold of 128, because BuildEvidencePack's uncapped evidence
    // packs (up to ~26 segments/6.3K tokens per question, versus 1-4 before) consume far more of
    // the shared KV pool per conversation than this threshold was calibrated for. Lowered as a
    // conservative stopgap pending a real fix (recycling by cumulative prompt tokens instead of
    // conversation count, which is what actually correlates with KV-cache pressure now that
    // evidence-pack size varies per question). Do not raise this back toward 128 until that
    // token-based recycle trigger lands and is validated against a full gate run.
    internal const int SequenceRecycleThreshold = 24;

    // Absolute refusal point: if outstanding conversations have kept the executor from recycling
    // and it is now approaching the native slot cap, minting another conversation would trade a
    // recoverable managed exception for a process-killing native assert.
    //
    // Also used directly as ModelParams.SeqMax (LLamaSharpRuntime.cs) — the default n_seq_max=1
    // means the second-ever conversation on a fresh executor fails find_slot/init_batch outright
    // on recurrent/hybrid architectures (e.g. Qwen3.5's Gated Delta Net layers validate seq_id
    // strictly against n_seq_max; plain-transformer llama.cpp paths happened to tolerate seq_id
    // >= n_seq_max, which is why this was never noticed before). That makes this constant a
    // direct native VRAM reservation, not just a managed counter: llama.cpp allocates a
    // per-sequence recurrent-state ("rs cache") buffer sized by n_seq_max on hybrid models.
    // Confirmed live on a 16GB GPU with Qwen3.5-9B-Q8_0 (8.86GB weights): SeqMax=240 reserved
    // ~12GB for the rs cache alone (~50MB/slot) and OOM-crashed the native process
    // (cudaMalloc failed, 0xC0000005) before the managed hard-limit check ever got a chance to
    // throw. Lowered from 240 to keep the rs-cache reservation (~2GB at this value) affordable
    // on consumer GPUs while still giving SequenceRecycleThreshold plenty of grace for the
    // active-conversations-outstanding path below. Do not raise this without checking rs-cache
    // size against available VRAM for the largest hybrid-architecture model in use.
    internal const int SequenceHardLimit = 40;

    // Opt-in, zero-cost-by-default diagnostic for the open KV-cache exhaustion investigation
    // (docs/CONTEXT_FABRIC_BUG_HISTORY.md §7): a threshold change alone was tried and had no
    // measurable effect on the failure trace, so the next step is confirming or ruling out
    // whether ActiveCount is ever actually reaching zero (which would explain why recycling
    // never engages regardless of the threshold value). Set THEORC_KVCACHE_DIAGNOSTICS=1 to
    // print one line per recycle-eligibility check to stderr; unset, this is a single cached
    // bool read with no other behavior change.
    private static readonly bool s_kvDiagnosticsEnabled =
        Environment.GetEnvironmentVariable("THEORC_KVCACHE_DIAGNOSTICS") == "1";

    private static void LogKvDiagnostic(string message)
    {
        if (s_kvDiagnosticsEnabled)
            // stdout, not stderr: Run-CF7GateExpanded.ps1 pipes the benchmark exe through
            // `2>&1 | Tee-Object`, and PowerShell treats any native-process stderr output as a
            // NativeCommandError under $ErrorActionPreference = 'Stop', aborting the whole run
            // after the first diagnostic line (observed directly — fixed same session).
            Console.WriteLine($"[KvCacheDiag] {message}");
    }

    public AdapterManager(LLamaSharpRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// Resolves-or-creates the role's persistent executor, then returns a fresh, reference
    /// -counted conversation handle on it. Dispose the returned handle when done with it —
    /// until you do, RebindRoleAsync for this role will refuse to tear down the executor.
    ///
    /// <b>internal, not public</b> (Native Runtime v2.0 Phase A — see
    /// docs/NATIVE_RUNTIME_V2_SPEC.md §1.3): AdapterManager has no VRAM/scheduler awareness of
    /// its own — it will happily build a persistent executor for any role requested, regardless
    /// of whether there is room. <see cref="RuntimeOrchestrator.GetConversationForBindingAsync"/>
    /// is the only sanctioned caller; it gates every call through
    /// <see cref="RuntimeOrchestrator.EnsureAdmitted"/> first. Keeping this internal (rather than
    /// public) makes an admission-bypassing call path a compile error instead of a runtime hazard
    /// — same-assembly test code retains access via the existing InternalsVisibleTo entries.
    /// </summary>
    internal Task<TrackedConversation> CreateConversationAsync(
        RuntimeRoleBinding binding, CancellationToken ct = default) =>
        GetOrCreateConversationAsync(binding, forceRebuild: false, ct);

    /// <summary>
    /// Forces a teardown+rebuild of the role's executor even if the binding looks unchanged
    /// (e.g. the adapter file at the same path was retrained). Throws InvalidOperationException
    /// if any conversation from the current executor is still outstanding — callers must
    /// dispose their TrackedConversations before rebinding, there is no implicit drain/wait.
    ///
    /// <b>internal, not public</b> — same admission-bypass rationale as
    /// <see cref="CreateConversationAsync"/> above.
    /// </summary>
    internal Task<TrackedConversation> RebindRoleAsync(
        RuntimeRoleBinding newBinding, CancellationToken ct = default) =>
        GetOrCreateConversationAsync(newBinding, forceRebuild: true, ct);

    private async Task<TrackedConversation> GetOrCreateConversationAsync(
        RuntimeRoleBinding binding, bool forceRebuild, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            InvalidateIfRuntimeReloaded();

            if (!forceRebuild &&
                _entries.TryGetValue(binding.Role, out var existing) &&
                BindingMatches(existing.Binding, binding))
            {
                // Serve from the existing executor unless it is approaching the native
                // sequence-slot cap AND is idle enough to swap out. With outstanding
                // conversations we keep serving (recycle is retried on the next idle call);
                // the threshold's margin below the hard cap absorbs that delay — but only up
                // to SequenceHardLimit, where we fail closed with a managed exception rather
                // than letting the next mint trip the fatal native assert.
                //
                // ForceRecycle overrides the threshold entirely: a NoKvSlot on this role (see
                // MarkForRecycle) is evidence the pool is already degraded (100-question CF-7
                // runs showed near-universal NoKvSlot failure after the first one on an
                // executor, not a slow climb toward SequenceRecycleThreshold — consistent with
                // disposed conversations not fully returning their cells before the next one
                // claims them). Recycling immediately, rather than waiting up to 23 more
                // conversations, gives every subsequent conversation a genuinely empty pool
                // instead of one still degraded by the conversation that just failed.
                var minted = existing.ConversationsCreated;
                var recycleNow = existing.ForceRecycle
                    ? existing.ActiveCount == 0
                    : minted >= SequenceRecycleThreshold && existing.ActiveCount == 0;

                if (!recycleNow)
                {
                    if (minted >= SequenceHardLimit)
                        throw new InvalidOperationException(
                            $"Role {binding.Role}'s executor has minted {minted} native sequence slots " +
                            $"while {existing.ActiveCount} conversation(s) remain active, so it cannot " +
                            "recycle and is about to exhaust the native sequence-slot cap. Dispose " +
                            "outstanding conversations for this role and retry.");
                    LogKvDiagnostic(
                        $"role={binding.Role} served-without-recycle minted={minted} " +
                        $"activeCount={existing.ActiveCount} threshold={SequenceRecycleThreshold} " +
                        $"forceRecycle={existing.ForceRecycle} reason=" +
                        $"{(existing.ForceRecycle ? "force-recycle-deferred-active" : minted < SequenceRecycleThreshold ? "under-threshold" : "active-conversations-outstanding")}");
                    return existing.CreateTrackedConversation();
                }

                LogKvDiagnostic(
                    $"role={binding.Role} RECYCLING minted={minted} activeCount={existing.ActiveCount} " +
                    $"threshold={SequenceRecycleThreshold} forced={existing.ForceRecycle}");
                _entries.TryRemove(binding.Role, out _);
                // Best-effort, same contract as the stale-binding teardown below: the entry is
                // already untracked, so a disposal fault must not block the replacement build.
                try { existing.DisposeNative(); } catch { /* superseded entry — best effort only */ }
            }

            if (_entries.TryGetValue(binding.Role, out var stale))
            {
                if (stale.ActiveCount > 0)
                    throw new InvalidOperationException(
                        $"Cannot rebuild role {binding.Role}: {stale.ActiveCount} conversation(s) " +
                        "still active on its current executor. Dispose them before rebinding.");
                _entries.TryRemove(binding.Role, out _);
                // Best-effort: a failure disposing the SUPERSEDED entry must not block building
                // the NEW one the caller actually asked for. The old entry is already removed
                // from tracking either way, so a leak here is the worst case, not a crash.
                try { stale.DisposeNative(); } catch { /* superseded entry — best effort only */ }
            }

            var entry = BuildRoleEntry(binding);
            _entries[binding.Role] = entry;
            return entry.CreateTrackedConversation();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// A model reload (LLamaSharpRuntime.LoadModelAsync) disposes the previous LLamaWeights.
    /// Every existing RoleEntry was built from that now-dead weights instance, so all of them —
    /// not just whichever role is being requested — must be dropped. Entries with outstanding
    /// conversations are dropped from tracking anyway (their underlying native weights are
    /// already gone at this point; there is nothing left to safely wait for or drain), so this
    /// is best-effort cleanup, not a guarantee that in-flight callers won't fault. Callers are
    /// responsible for draining role usage before triggering a model reload elsewhere in the
    /// app — AdapterManager cannot retroactively make a dead native handle safe.
    /// </summary>
    private void InvalidateIfRuntimeReloaded()
    {
        var current = _runtime.WeightsGeneration;
        if (current == _lastSeenWeightsGeneration)
            return;

        // Best-effort (throwOnFailure: false): the caller here is mid-way through satisfying a
        // CreateConversationAsync/RebindRoleAsync request for a DIFFERENT, fresh binding. A
        // failure freeing the OLD, now-dangling entries must not block that request — the
        // weights backing those old entries are already gone regardless of whether this
        // cleanup succeeds, so there is nothing left to protect by surfacing the failure here.
        DisposeAllEntries(throwOnFailure: false);
        _lastSeenWeightsGeneration = current;
    }

    /// <summary>
    /// Disposes every entry's native state, continuing through the rest even if one faults —
    /// otherwise a single bad disposal could abort the loop and leave _entries non-empty.
    /// Always clears _entries when done. Only rethrows (after attempting every entry) when
    /// <paramref name="throwOnFailure"/> is true — used for explicit DisposeAsync, where the
    /// caller asked specifically for cleanup and deserves to know it didn't fully succeed.
    /// The reload-invalidation caller passes false: it's mid-way through a different request
    /// and a failure freeing superseded state must not block that request from completing.
    /// </summary>
    private void DisposeAllEntries(bool throwOnFailure)
    {
        List<Exception>? failures = null;
        foreach (var entry in _entries.Values)
        {
            try
            {
                entry.DisposeNative();
            }
            catch (Exception ex)
            {
                (failures ??= []).Add(ex);
            }
        }
        _entries.Clear();

        if (throwOnFailure && failures is not null)
            throw new AggregateException(
                "One or more AdapterManager role entries failed to dispose cleanly.", failures);
    }

    // internal (not private): unit-tested directly in AdapterManagerTests — this is the one
    // piece of AdapterManager's logic with no dependency on real LLamaSharp native objects.
    // Explicit null checks because internal makes this reachable by any same-assembly caller,
    // not just the one already-null-checked call site inside GetOrCreateConversationAsync.
    internal static bool BindingMatches(RuntimeRoleBinding a, RuntimeRoleBinding b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return BindingMatchesCore(a, b);
    }

    private static bool BindingMatchesCore(RuntimeRoleBinding a, RuntimeRoleBinding b) =>
        a.Role == b.Role &&
        string.Equals(a.BaseModel.Path, b.BaseModel.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Adapter?.Path, b.Adapter?.Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Marks the given role's current executor (if any) as degraded, so the next
    /// GetOrCreateConversationAsync call for it recycles as soon as the role is idle
    /// (ActiveCount == 0) instead of waiting for SequenceRecycleThreshold. Called by
    /// IRoleRuntime as soon as it observes a NoKvSlot on a role's conversation — see the
    /// ForceRecycle doc comment on RoleEntry for why. A no-op if the role has no tracked entry
    /// (already recycled/never built) or the binding has since changed; either way there is
    /// nothing degraded left to mark. Fire-and-forget from the caller's perspective is fine:
    /// this only ever narrows a future recycle decision, never anything already in flight.
    /// </summary>
    public async Task MarkForRecycle(RuntimeRole role)
    {
        // No CancellationToken by design: the observation "this executor produced a NoKvSlot"
        // remains true whether or not the observing request is being cancelled, and honoring a
        // cancelled token here would let the degraded executor be silently reused by the NEXT
        // request (CodeRabbit finding, 2026-07-06). The gate wait is bounded in practice by the
        // longest gate hold (one executor build, seconds at worst).
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_entries.TryGetValue(role, out var entry))
                entry.ForceRecycle = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3) — a read-only,
    /// point-in-time view of every currently-tracked role's residency and sequence-slot
    /// pressure. Synchronous and non-blocking by design: never waits on <see cref="_gate"/>
    /// (the async minting/rebinding pipeline), matching the same "telemetry reads don't block
    /// behind an in-flight operation" rule <c>RuntimeOrchestrator.GetReservationSnapshot</c>
    /// already follows. Safe to call concurrently with any in-flight mint/rebind/dispose —
    /// <see cref="_entries"/> is a <see cref="ConcurrentDictionary{TKey,TValue}"/> specifically
    /// so this never sees a torn read. Exposes only counts/status derived from
    /// <see cref="RoleEntry"/> — the native <see cref="BatchedExecutor"/>/<see cref="LoraAdapter"/>
    /// handles never leave this class.
    /// </summary>
    public IReadOnlyList<AdapterRoleResidency> GetResidencySnapshot()
    {
        ThrowIfDisposed();

        return _entries
            .Select(kv =>
            {
                // ActiveCount/ConversationsCreated/ForceRecycle are each an independent
                // Volatile.Read -- capturing each ONCE here (rather than letting the record
                // initializer and ComputeResidencyStatus each re-read RoleEntry separately) is
                // what makes the displayed count and the displayed status describe the SAME
                // instant. Without this, a concurrent mint between reads could show e.g.
                // ConversationsCreated=23 alongside Status=RecyclePending, which needs >= 24
                // (CodeRabbit finding on this PR).
                var activeCount = kv.Value.ActiveCount;
                var conversationsCreated = kv.Value.ConversationsCreated;
                var forceRecycle = kv.Value.ForceRecycle;
                return new AdapterRoleResidency(
                    Role: kv.Key,
                    Binding: kv.Value.Binding,
                    ActiveCount: activeCount,
                    ConversationsCreated: conversationsCreated,
                    Status: ComputeResidencyStatus(conversationsCreated, forceRecycle));
            })
            .ToArray();
    }

    // Independently computed for display, not literally shared with GetOrCreateConversationAsync's
    // live recycle-check branch above -- Phase C is read-only additive telemetry (per spec: "add
    // read-only accessors; do not change lifecycle logic"), so this deliberately does not refactor
    // the already-reviewed hot path to share a helper. AtHardLimit checks conversationsCreated
    // alone (not the live path's more nuanced "!recycleNow && minted >= SequenceHardLimit" throw
    // gate) -- a slightly earlier, simpler "at risk" signal is the right tradeoff for a status
    // display, which doesn't need to reproduce the exact throw condition. Takes captured values,
    // not a RoleEntry, so it can't accidentally re-read a volatile field a second time.
    private static AdapterRoleResidencyStatus ComputeResidencyStatus(int conversationsCreated, bool forceRecycle)
    {
        if (conversationsCreated >= SequenceHardLimit)
            return AdapterRoleResidencyStatus.AtHardLimit;
        if (forceRecycle)
            return AdapterRoleResidencyStatus.Degraded;
        if (conversationsCreated >= SequenceRecycleThreshold)
            return AdapterRoleResidencyStatus.RecyclePending;
        return AdapterRoleResidencyStatus.Healthy;
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;

            DisposeAllEntries(throwOnFailure: true);
        }
        finally
        {
            _gate.Release(); // _gate itself is never Dispose()'d — see class doc point 5.
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdapterManager));
    }

    /// <summary>
    /// One role's persistent native state. Not exposed outside AdapterManager — only
    /// TrackedConversation handles (which reference-count it) ever leave this class.
    /// </summary>
    private sealed class RoleEntry(BatchedExecutor executor, LoraAdapter? lora, RuntimeRoleBinding binding)
    {
        private int _activeCount;
        private int _conversationsCreated;
        private volatile bool _forceRecycle;

        public RuntimeRoleBinding Binding { get; } = binding;
        public int ActiveCount => Volatile.Read(ref _activeCount);

        /// <summary>Set by <see cref="AdapterManager.MarkForRecycle"/> when a NoKvSlot on this
        /// role's executor indicated the pool is already degraded. Read by the recycle check in
        /// GetOrCreateConversationAsync; never cleared explicitly — recycling replaces the whole
        /// RoleEntry, so a fresh one always starts false.</summary>
        public bool ForceRecycle
        {
            get => _forceRecycle;
            set => _forceRecycle = value;
        }

        /// <summary>Total conversations ever minted by this entry's executor — each consumed a
        /// native sequence slot that is never returned (see SequenceRecycleThreshold).</summary>
        public int ConversationsCreated => Volatile.Read(ref _conversationsCreated);

        public TrackedConversation CreateTrackedConversation()
        {
            // Create the conversation BEFORE incrementing: if Create() throws, the count must
            // stay untouched, or a role would be permanently locked out of RebindRoleAsync
            // (ActiveCount > 0 forever with no TrackedConversation able to decrement it).
            var conversation = executor.Create();
            Interlocked.Increment(ref _activeCount);
            Interlocked.Increment(ref _conversationsCreated);
            return new TrackedConversation(conversation, () => Interlocked.Decrement(ref _activeCount));
        }

        /// <summary>
        /// Disposes the executor and unloads the adapter (LoraAdapter is not IDisposable —
        /// it has an explicit Unload() method that must be called or the native handle leaks).
        /// Called unconditionally even with ActiveCount > 0 in reload/shutdown paths — there is
        /// no safe alternative to dropping a still-referenced handle in those cases (the
        /// underlying weights may already be gone), so this is intentionally not conditional.
        /// </summary>
        public void DisposeNative()
        {
            try
            {
                lora?.Unload();
            }
            finally
            {
                // Must run even if Unload() throws — otherwise a faulting adapter unload
                // leaks the (likely larger) executor/context on top of the adapter handle.
                executor.Dispose();
            }
        }
    }

    /// <summary>
    /// Builds a fresh RoleEntry against this manager's runtime. Instance method (not the
    /// earlier static placeholder) so it can call _runtime.CreateBatchedExecutor() directly.
    /// </summary>
    private RoleEntry BuildRoleEntry(RuntimeRoleBinding binding)
    {
        var executor = _runtime.CreateBatchedExecutor();
        LoraAdapter? lora = null;
        try
        {
            if (binding.Adapter is not null)
            {
                // Matches the §7 harness call path exactly: weights.NativeHandle.LoadLoraFromFile
                // + executor.Context.NativeHandle.SetLoraAdapters, attached once, at creation.
                lora = executor.Model.NativeHandle.LoadLoraFromFile(binding.Adapter.Path);
                executor.Context.NativeHandle.SetLoraAdapters(
                    new (LoraAdapter Adapter, float Scale)[] { (lora, 1.0f) });
            }
            return new RoleEntry(executor, lora, binding);
        }
        catch
        {
            // Whatever was created before the throw must not leak — including the LoRA native
            // handle if LoadLoraFromFile succeeded but SetLoraAdapters then threw. Nested
            // try/finally because Unload() itself throwing must not skip executor.Dispose() —
            // the executor is the larger resource of the two (a prior fix missed this nesting).
            try { lora?.Unload(); } finally { executor.Dispose(); }
            throw;
        }
    }
}

/// <summary>
/// One role's residency and sequence-slot pressure at the moment <see cref="AdapterManager.GetResidencySnapshot"/>
/// was called. <see cref="Binding"/> is already-public information (paths/display names, no
/// native handle) — included so a diagnostics consumer can tell WHAT is resident, not just how many.
/// </summary>
public sealed record AdapterRoleResidency(
    RuntimeRole Role,
    RuntimeRoleBinding Binding,
    int ActiveCount,
    int ConversationsCreated,
    AdapterRoleResidencyStatus Status);

/// <summary>Ordered roughly least-to-most concerning, though callers should treat each as its own state, not a strict severity ladder.</summary>
public enum AdapterRoleResidencyStatus
{
    /// <summary>Under the recycle threshold — normal operation.</summary>
    Healthy,
    /// <summary>At/over the recycle threshold but recycling is deferred because conversations
    /// are still outstanding — will recycle as soon as the role goes idle.</summary>
    RecyclePending,
    /// <summary>Marked degraded by <see cref="AdapterManager.MarkForRecycle"/> (a NoKvSlot was
    /// observed on this role) — recycling as soon as the role goes idle, regardless of the
    /// normal threshold.</summary>
    Degraded,
    /// <summary>At or past the native sequence-slot hard limit — the next mint attempt for this
    /// role may throw rather than risk the native slot-exhaustion crash.</summary>
    AtHardLimit,
}

/// <summary>
/// Reference-counted handle to a Conversation created by AdapterManager. Dispose this when
/// done with the conversation — RebindRoleAsync for the owning role will throw rather than
/// tear down the executor while any TrackedConversation from it is still undisposed.
/// Constructor is internal: only AdapterManager can mint one with a valid callback, preventing
/// external code from constructing a bogus instance with a null/wrong onDisposed action.
/// </summary>
public sealed class TrackedConversation : IDisposable
{
    private readonly Conversation _inner;
    private readonly Action _onDisposed;
    private bool _disposed;

    internal TrackedConversation(Conversation inner, Action onDisposed)
    {
        _inner = inner;
        _onDisposed = onDisposed;
    }

    public Conversation Inner => _inner;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _inner.Dispose();
        }
        finally
        {
            // Must always run, even if Inner.Dispose() throws — otherwise the role's
            // active-count never decrements and RebindRoleAsync is locked out forever.
            _onDisposed();
        }
    }
}
