// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Optional native role-runtime hook for <see cref="HiveWorkerAgent"/>. Lets a host
/// with a local LLamaSharp-backed NativeRoleRuntime (the WPF/Avalonia app) execute
/// HIVE tasks without HiveWorkerAgent itself depending on the heavy native-runtime
/// dependency chain (LLamaSharp, ModelDepot, RuntimeOrchestrator). Lightweight hosts
/// such as OrchestratorIDE.Daemon simply never set <see cref="HiveWorkerAgent.NativeRoleExecutor"/>.
/// </summary>
public interface IHiveNativeRoleExecutor : IAsyncDisposable
{
    IAsyncEnumerable<string> StreamRoleCompletionAsync(
        string hiveRole,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct);

    /// <summary>Human-readable telemetry suffix (e.g. " (model=..., tok/s=...)"), or "" if unavailable.</summary>
    string DescribeTelemetry(string hiveRole);
}

/// <summary>
/// Thrown by an <see cref="IHiveNativeRoleExecutor"/> to signal the native runtime
/// explicitly refused the task (e.g. scheduler/VRAM admission denied) rather than
/// failed unexpectedly, so callers can log/fallback distinctly from other failures.
/// </summary>
public sealed class HiveNativeRoleAdmissionDeniedException : Exception
{
    public HiveNativeRoleAdmissionDeniedException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
