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
/// adapters on a live context with populated KV cache is silent and unsafe (no exception, but
/// stale pre-adapter cache entries mix with post-adapter ones); the only safe path is
/// teardown+rebuild, which is what <see cref="RebindRoleAsync"/> does explicitly.
///
/// Never hands out the executor or context themselves — only fresh <see cref="Conversation"/>
/// instances, created atomically under the same lock that would tear one down. This means
/// nothing a caller holds can ever reference a disposed executor (the bug an earlier draft of
/// this design had, caught in review before implementation).
/// </summary>
public sealed class AdapterManager : IAsyncDisposable
{
    private readonly LLamaSharpRuntime _runtime;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<RuntimeRole, RoleEntry> _entries = new();
    private bool _disposed;

    public AdapterManager(LLamaSharpRuntime runtime) =>
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));

    /// <summary>
    /// Resolves-or-creates the role's persistent executor, then returns a fresh
    /// <see cref="Conversation"/> on it. If an executor already exists for this role with a
    /// matching binding (same base model + same adapter path), it is reused as-is — no rebuild.
    /// </summary>
    public Task<Conversation> CreateConversationAsync(
        RuntimeRoleBinding binding, CancellationToken ct = default) =>
        GetOrCreateConversationAsync(binding, forceRebuild: false, ct);

    /// <summary>
    /// Forces a teardown+rebuild of the role's executor even if the binding looks unchanged
    /// (e.g. the adapter file at the same path was retrained). Always disposes any existing
    /// executor for this role before building a fresh one — never calls SetLoraAdapters on
    /// existing state, per the §7 verdict.
    /// </summary>
    public Task<Conversation> RebindRoleAsync(
        RuntimeRoleBinding newBinding, CancellationToken ct = default) =>
        GetOrCreateConversationAsync(newBinding, forceRebuild: true, ct);

    private async Task<Conversation> GetOrCreateConversationAsync(
        RuntimeRoleBinding binding, bool forceRebuild, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();

            if (!forceRebuild &&
                _entries.TryGetValue(binding.Role, out var existing) &&
                BindingMatches(existing.Binding, binding))
            {
                return existing.Executor.Create();
            }

            if (_entries.Remove(binding.Role, out var stale))
                stale.Executor.Dispose();

            var executor = _runtime.CreateBatchedExecutor();
            if (binding.Adapter is not null)
            {
                // Matches the §7 harness call path exactly: weights.NativeHandle.LoadLoraFromFile
                // + executor.Context.NativeHandle.SetLoraAdapters, attached once, at creation.
                var lora = executor.Model.NativeHandle.LoadLoraFromFile(binding.Adapter.Path);
                executor.Context.NativeHandle.SetLoraAdapters(
                    new (LoraAdapter Adapter, float Scale)[] { (lora, 1.0f) });
            }

            _entries[binding.Role] = new RoleEntry(executor, binding);
            return executor.Create();
        }
        finally
        {
            _gate.Release();
        }
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
                entry.Executor.Dispose();
            _entries.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AdapterManager));
    }

    private sealed record RoleEntry(BatchedExecutor Executor, RuntimeRoleBinding Binding);
}
