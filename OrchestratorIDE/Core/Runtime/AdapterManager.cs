// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using LLama.Batched;
using LLama.Native;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 3 — per-role persistent LoRA contexts. Design rationale and the
/// empirical spike behind it: RUNTIME_PHASE0_SPEC.md §7 (hot-swap spike) and §7a (this design).
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
    private readonly Dictionary<RuntimeRole, RoleEntry> _entries = new();
    private int _lastSeenWeightsGeneration = -1;
    private bool _disposed;

    public AdapterManager(LLamaSharpRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// Resolves-or-creates the role's persistent executor, then returns a fresh, reference
    /// -counted conversation handle on it. Dispose the returned handle when done with it —
    /// until you do, RebindRoleAsync for this role will refuse to tear down the executor.
    /// </summary>
    public Task<TrackedConversation> CreateConversationAsync(
        RuntimeRoleBinding binding, CancellationToken ct = default) =>
        GetOrCreateConversationAsync(binding, forceRebuild: false, ct);

    /// <summary>
    /// Forces a teardown+rebuild of the role's executor even if the binding looks unchanged
    /// (e.g. the adapter file at the same path was retrained). Throws InvalidOperationException
    /// if any conversation from the current executor is still outstanding — callers must
    /// dispose their TrackedConversations before rebinding, there is no implicit drain/wait.
    /// </summary>
    public Task<TrackedConversation> RebindRoleAsync(
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
                return existing.CreateTrackedConversation();
            }

            if (_entries.TryGetValue(binding.Role, out var stale))
            {
                if (stale.ActiveCount > 0)
                    throw new InvalidOperationException(
                        $"Cannot rebuild role {binding.Role}: {stale.ActiveCount} conversation(s) " +
                        "still active on its current executor. Dispose them before rebinding.");
                _entries.Remove(binding.Role);
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
    internal static bool BindingMatches(RuntimeRoleBinding a, RuntimeRoleBinding b) =>
        a.Role == b.Role &&
        string.Equals(a.BaseModel.Path, b.BaseModel.Path, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Adapter?.Path, b.Adapter?.Path, StringComparison.OrdinalIgnoreCase);

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

        public RuntimeRoleBinding Binding { get; } = binding;
        public int ActiveCount => Volatile.Read(ref _activeCount);

        public TrackedConversation CreateTrackedConversation()
        {
            // Create the conversation BEFORE incrementing: if Create() throws, the count must
            // stay untouched, or a role would be permanently locked out of RebindRoleAsync
            // (ActiveCount > 0 forever with no TrackedConversation able to decrement it).
            var conversation = executor.Create();
            Interlocked.Increment(ref _activeCount);
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
