// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using LLama.Batched;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Native Runtime Phase 3 — the single entry point connecting all three pieces:
/// <see cref="ModelDepot"/> (what GGUF/LoRA assets exist locally) resolves a role's binding,
/// <see cref="SessionManager"/> ensures the binding's base model is loaded into the shared
/// <see cref="ILocalModelRuntime"/> (reusing the current load if it already matches), and
/// <see cref="AdapterManager"/> returns a per-role, adapter-attached <see cref="Conversation"/>
/// on a persistent executor. Before this class, the three were standalone and nothing called
/// all of them together — this is what "Phase 3 is wired up" means.
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
/// </summary>
public sealed class RuntimeOrchestrator : IAsyncDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly AdapterManager _adapterManager;

    public RuntimeOrchestrator(SessionManager sessionManager, AdapterManager adapterManager)
    {
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
        _adapterManager = adapterManager ?? throw new ArgumentNullException(nameof(adapterManager));
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

        var loadResult = await _sessionManager.LoadRoleAsync(depot, role, options, ct).ConfigureAwait(false);
        if (!loadResult.Success || loadResult.Binding is null)
            throw new InvalidOperationException(
                $"Could not load base model for role {role}: {loadResult.Message}");

        return await _adapterManager.CreateConversationAsync(loadResult.Binding, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _adapterManager.DisposeAsync().ConfigureAwait(false);
        await _sessionManager.DisposeAsync().ConfigureAwait(false);
    }
}
