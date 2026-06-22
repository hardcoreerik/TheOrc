// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// IModelRuntime facade that tries native role-based generation first and falls back to a
/// configured IModelRuntime (Ollama, by convention) automatically when native fails. Drop-in for
/// any existing IModelRuntime call site — AgentLoop, SwarmSession, ChatEngine all already depend
/// on the interface rather than a concrete type (the Phase 0 migration), so no call-site changes
/// are needed beyond constructing this instead of the bare fallback runtime.
///
/// <b>Fallback only happens before the first token reaches the caller.</b> Once a token has been
/// yielded, a later failure propagates instead of triggering fallback — splicing a second
/// backend's output onto the first backend's partial turn would silently corrupt the
/// conversation, which is worse than surfacing the failure and letting the caller's own retry/
/// error handling take over. This mirrors the precedent in <see cref="NativeRuntimeFallbackCoordinator"/>
/// (manual Settings smoke test), generalized to streaming production traffic instead of a single
/// buffered test prompt with a user confirmation step.
/// </summary>
public sealed class NativeWithFallbackRuntime : IModelRuntime, IAsyncDisposable
{
    private readonly IRoleRuntime _native;
    private readonly RuntimeRole _role;
    private readonly IModelRuntime _fallback;
    private readonly Action<string>? _onFallback;

    // Tracks which backend actually served the most recent call, purely for GetHealth/GetStats —
    // those two methods report "right now," and "right now" means whichever backend the last
    // StreamCompletionAsync call actually used. Starts true (native) so a caller asking before
    // the first call ever runs gets the native-role health snapshot rather than the fallback's.
    private volatile bool _lastCallUsedNative = true;

    /// <param name="native">
    /// Depends on the interface, not the concrete <see cref="NativeRoleRuntime"/>, so this class
    /// is testable with a fake and so disposal (below) degrades gracefully for any future
    /// IRoleRuntime implementation that isn't itself disposable.
    /// </param>
    public NativeWithFallbackRuntime(
        IRoleRuntime native,
        RuntimeRole role,
        IModelRuntime fallback,
        Action<string>? onFallback = null)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _role = role;
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _onFallback = onFallback;
    }

    public string RuntimeName => "NativeWithFallback";

    /// <summary>
    /// Reachability is reported from the fallback only — the native role runtime has no
    /// equivalent cheap up-front probe (resolving a role and loading a base model is not a
    /// ≤3s connectivity check, it is the expensive operation this whole class exists to gate),
    /// and a caller using this method is asking "is there SOME way to generate right now,"
    /// which the fallback answers honestly without paying native's load cost just to find out.
    /// </summary>
    public Task<bool> IsReachableAsync(CancellationToken ct = default) => _fallback.IsReachableAsync(ct);

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        _fallback.GetInstalledModelsAsync(ct);

    public async IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        double? topP = null,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Materialize once: history may be a lazy/single-use enumerable, and a fallback pass
        // needs to enumerate it again after the native attempt already consumed it once.
        var historyList = history is IReadOnlyList<AgentMessage> list ? list : history.ToList();

        // A review pass caught that text tokens are not the only observable side effect: native
        // can invoke onToolCall/onUsage directly. If either fired even once, the caller has
        // already observed something derived from native's attempt — falling back at that point
        // would have the fallback backend invoke its OWN onToolCall/onUsage for the same logical
        // turn on top of native's, duplicating or conflicting side effects the caller has no way
        // to reconcile. So both callbacks are wrapped to also count as "observed," exactly like a
        // yielded text token, even though today's NativeRoleRuntime happens to only invoke them
        // after a clean finish — this class must not depend on that being permanently true for
        // every current and future IRoleRuntime implementation.
        var observedSideEffect = false;
        var guardedOnToolCall = onToolCall is null
            ? null
            : new Action<ToolCall>(tc => { observedSideEffect = true; onToolCall(tc); });
        var guardedOnUsage = onUsage is null
            ? null
            : new Action<int, int>((p, c) => { observedSideEffect = true; onUsage(p, c); });

        var yieldedAny = false;
        Exception? nativeFailure = null;

        // topP is not threaded into the native path here -- StreamRoleCompletionAsync belongs
        // to IRoleRuntime (the swarm/role-based dispatch interface), a separate surface from
        // IModelRuntime that this class wraps for ITS OWN signature compliance. No current
        // caller of this class passes a non-default topP, so extending IRoleRuntime too is
        // left for whenever that's actually needed rather than threading a parameter no path
        // exercises yet.
        await using (var enumerator = _native
            .StreamRoleCompletionAsync(_role, historyList, tools, temperature, maxTokens, guardedOnToolCall, guardedOnUsage, ct)
            .GetAsyncEnumerator(ct))
        {
            while (true)
            {
                string token;
                try
                {
                    if (!await enumerator.MoveNextAsync().ConfigureAwait(false))
                    {
                        _lastCallUsedNative = true;
                        yield break;
                    }

                    token = enumerator.Current;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                // Fallback eligibility requires BOTH: nothing observable has reached the caller
                // yet (no text token, no tool-call/usage callback), AND the failure itself looks
                // like "native couldn't serve this" rather than something that should fail
                // closed. A blanket catch-everything filter would silently retry on the fallback
                // for exception types that exist specifically to STOP a request — e.g. a future
                // approval/validation failure surfacing through this layer — which is the
                // opposite of what a fail-closed path needs. IsFallbackEligible (below) is the
                // single place that judgment call lives, kept narrow and named so it is easy to
                // extend deliberately rather than by widening this filter.
                catch (Exception ex) when (!yieldedAny && !observedSideEffect && IsFallbackEligible(ex))
                {
                    nativeFailure = ex;
                    break;
                }

                yieldedAny = true;
                _lastCallUsedNative = true;
                yield return token;
            }
        }

        _lastCallUsedNative = false;
        _onFallback?.Invoke(nativeFailure!.Message);

        await foreach (var token in _fallback
            .StreamCompletionAsync(model, historyList, tools, temperature, topP, maxTokens, onToolCall, onUsage, ct)
            .ConfigureAwait(false))
        {
            yield return token;
        }
    }

    /// <summary>
    /// Conservative allow-list, not a deny-list: only exception shapes that are recognizably
    /// "native infrastructure could not serve this request" trigger fallback. Everything else
    /// (argument errors, future security/approval exceptions, anything not explicitly recognized)
    /// propagates instead of being silently retried on a different backend.
    ///
    /// <see cref="RuntimeAdmissionDeniedException"/> is deliberately excluded even though it
    /// derives from <see cref="InvalidOperationException"/> — a review pass caught that the
    /// first draft's broad InvalidOperationException match would catch it too, which is the
    /// wrong call: admission denial is a deliberate "OrcScheduler decided native does not have
    /// room for this role right now" outcome (RUNTIME_PHASE0_SPEC.md §6 Phase 4), not a transient
    /// load failure. Silently rerouting every VRAM-constrained request to Ollama would mask a
    /// capacity problem indefinitely instead of surfacing it. Generic InvalidOperationException
    /// (covers "no base GGUF resolved," "no model loaded," "could not load base model" — the
    /// genuine first-run/setup gaps this whole class exists to tolerate) and
    /// ObjectDisposedException/TimeoutException remain eligible.
    /// </summary>
    private static bool IsFallbackEligible(Exception ex) => ex switch
    {
        RuntimeAdmissionDeniedException => false,
        InvalidOperationException or ObjectDisposedException or TimeoutException => true,
        _ => false,
    };

    public RuntimeHealth GetHealth() =>
        _lastCallUsedNative ? _native.GetHealth(_role) : _fallback.GetHealth();

    public RuntimeStats GetStats() =>
        _lastCallUsedNative ? _native.GetStats(_role) : _fallback.GetStats();

    /// <summary>
    /// Disposes the native role runtime only, if it is itself disposable. The fallback runtime's
    /// lifetime is the caller's responsibility — this class did not construct it and must not
    /// assume it owns it (the same fallback instance may be shared across multiple roles'
    /// NativeWithFallbackRuntime wrappers).
    /// </summary>
    public ValueTask DisposeAsync() =>
        _native is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
