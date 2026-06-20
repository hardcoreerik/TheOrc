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
    private readonly SessionManager _sessionManager;
    private readonly AdapterManager _adapterManager;
    private readonly IOrcScheduler? _scheduler;
    private readonly Func<VramBudget>? _budgetProvider;

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
        EnsureAdmitted(binding);

        var loadResult = await _sessionManager.LoadBindingAsync(binding, options, ct).ConfigureAwait(false);
        if (!loadResult.Success || loadResult.Binding is null)
            throw new InvalidOperationException(
                $"Could not load base model for role {binding.Role}: {loadResult.Message}");

        return await _adapterManager.CreateConversationAsync(loadResult.Binding, ct).ConfigureAwait(false);
    }

    private void EnsureAdmitted(RuntimeRoleBinding binding)
    {
        if (_scheduler is null || _budgetProvider is null)
            return;

        var budget = _budgetProvider();
        var decision = _scheduler.TryAdmit(binding, budget);
        if (!decision.Admitted)
            throw new RuntimeAdmissionDeniedException(binding, budget, decision);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _adapterManager.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            // Must run even if AdapterManager's disposal faults — otherwise a failure tearing
            // down per-role executors would leak the SessionManager (and, if disposeRuntime was
            // true, the runtime/weights it owns) entirely.
            await _sessionManager.DisposeAsync().ConfigureAwait(false);
        }
    }
}

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
