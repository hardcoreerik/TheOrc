// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE;

/// <summary>
/// Bridges the heavy, LLamaSharp-backed <see cref="IRoleRuntime"/> to the lightweight
/// <see cref="IHiveNativeRoleExecutor"/> contract that <c>HiveWorkerAgent</c> depends on.
/// This indirection keeps HiveWorkerAgent (shared with OrchestratorIDE.Daemon) free of
/// any reference to IRoleRuntime/RuntimeRole/ModelDepot/RuntimeOrchestrator, so the
/// headless Daemon binary doesn't need to compile or ship the native-runtime dependency
/// chain. Only the WPF/Avalonia host, which actually owns a NativeRoleRuntime, wraps it
/// with this adapter.
/// </summary>
internal sealed class NativeHiveRoleExecutorAdapter(IRoleRuntime inner) : IHiveNativeRoleExecutor
{
    internal static RuntimeRole MapHiveRoleToRuntimeRole(string? hiveRole) =>
        (hiveRole ?? "").Trim().ToLowerInvariant() switch
        {
            "researcher" => RuntimeRole.Researcher,
            _ => RuntimeRole.Worker,
        };

    public async IAsyncEnumerable<string> StreamRoleCompletionAsync(
        string hiveRole,
        IReadOnlyList<AgentMessage> messages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var role = MapHiveRoleToRuntimeRole(hiveRole);

        IAsyncEnumerator<string> enumerator;
        try
        {
            enumerator = inner.StreamRoleCompletionAsync(role, messages, ct: ct).GetAsyncEnumerator(ct);
        }
        catch (RuntimeAdmissionDeniedException ex)
        {
            throw new HiveNativeRoleAdmissionDeniedException(ex.Decision.Reason ?? ex.Message, ex);
        }

        await using (enumerator)
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (RuntimeAdmissionDeniedException ex)
                {
                    throw new HiveNativeRoleAdmissionDeniedException(ex.Decision.Reason ?? ex.Message, ex);
                }

                if (!hasNext) yield break;
                yield return enumerator.Current;
            }
        }
    }

    public string DescribeTelemetry(string hiveRole)
    {
        var role = MapHiveRoleToRuntimeRole(hiveRole);
        var health = inner.GetHealth(role);
        var stats = inner.GetStats(role);
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(health.ActiveModel))
            parts.Add($"model={health.ActiveModel}");
        if (stats.LastTimeToFirstToken is { } ttft)
            parts.Add($"ttft={ttft.TotalMilliseconds:F0}ms");
        if (stats.TokensPerSecond is { } tps)
            parts.Add($"tok/s={tps:F1}");
        if (stats.EstimatedVramBytes is { } bytes)
            parts.Add($"est_vram={bytes / (1024.0 * 1024 * 1024):F1}GB");
        if (!string.IsNullOrWhiteSpace(health.Message))
            parts.Add($"status={health.Message}");

        return parts.Count == 0 ? "" : $" ({string.Join(", ", parts)})";
    }

    public ValueTask DisposeAsync() =>
        inner is IAsyncDisposable asyncDisposable ? asyncDisposable.DisposeAsync() : ValueTask.CompletedTask;
}
