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
/// Two safety properties added after the first Grok review pass on the implementation:
/// 1. Conversations are reference-counted per role. RebindRoleAsync refuses (throws) to tear
///    down a role's executor while any conversation from it is still outstanding — the first
///    draft only guarded the lookup-or-create decision, not concurrent *use* of an already
///    -returned Conversation racing a rebind on another thread.
/// 2. A weights-generation check: LLamaSharpRuntime.LoadModelAsync always disposes the
///    previous LLamaWeights before loading new ones. If that happens while AdapterManager
///    holds executors built from the old weights, every one of them is now dangling — not
///    just whichever role is next requested. Every call compares the runtime's current
///    generation against the one this manager last saw and invalidates all entries if it
///    changed, rather than relying on per-role path comparison (which can't detect a same
///    -path reload).
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
                stale.DisposeNative();
            }

            var executor = _runtime.CreateBatchedExecutor();
            LoraAdapter? lora = null;
            if (binding.Adapter is not null)
            {
                // Matches the §7 harness call path exactly: weights.NativeHandle.LoadLoraFromFile
                // + executor.Context.NativeHandle.SetLoraAdapters, attached once, at creation.
                lora = executor.Model.NativeHandle.LoadLoraFromFile(binding.Adapter.Path);
                executor.Context.NativeHandle.SetLoraAdapters(
                    new (LoraAdapter Adapter, float Scale)[] { (lora, 1.0f) });
            }

            var entry = new RoleEntry(executor, lora, binding);
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
    /// is logged as a best-effort cleanup, not a guarantee that in-flight callers won't fault.
    /// Callers are responsible for draining role usage before triggering a model reload
    /// elsewhere in the app — AdapterManager cannot retroactively make a dead native handle safe.
    /// </summary>
    private void InvalidateIfRuntimeReloaded()
    {
        var current = _runtime.WeightsGeneration;
        if (current == _lastSeenWeightsGeneration)
            return;

        foreach (var entry in _entries.Values)
            entry.DisposeNative(skipIfActive: true);
        _entries.Clear();
        _lastSeenWeightsGeneration = current;
    }

    private static bool BindingMatches(RuntimeRoleBinding a, RuntimeRoleBinding b) =>
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

            foreach (var entry in _entries.Values)
                entry.DisposeNative(skipIfActive: true);
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
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
            Interlocked.Increment(ref _activeCount);
            return new TrackedConversation(executor.Create(), () => Interlocked.Decrement(ref _activeCount));
        }

        /// <summary>
        /// Disposes the executor and unloads the adapter (LoraAdapter is not IDisposable —
        /// it has an explicit Unload() method that must be called or the native handle leaks).
        /// skipIfActive: true means "best effort" — used for reload invalidation and manager
        /// shutdown, where there is no safe alternative to dropping a still-referenced handle.
        /// </summary>
        public void DisposeNative(bool skipIfActive = false)
        {
            if (skipIfActive && ActiveCount > 0)
            {
                // Nothing safe to do here — the underlying weights may already be gone
                // (reload case) or the manager is shutting down regardless of callers.
                // Dispose anyway; an in-flight caller's next native call will fault, which
                // is the same outcome a dangling pointer would produce either way.
            }
            lora?.Unload();
            executor.Dispose();
        }
    }
}

/// <summary>
/// Reference-counted handle to a Conversation created by AdapterManager. Dispose this when
/// done with the conversation — RebindRoleAsync for the owning role will throw rather than
/// tear down the executor while any TrackedConversation from it is still undisposed.
/// </summary>
public sealed class TrackedConversation(Conversation inner, Action onDisposed) : IDisposable
{
    private bool _disposed;

    public Conversation Inner { get; } = inner;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Inner.Dispose();
        onDisposed();
    }
}
