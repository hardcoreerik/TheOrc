// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.Services.Hive;

/// <summary>
/// Optional native role-runtime hook for <see cref="HiveWorkerAgent"/>. Lets a host
/// with a local LLamaSharp-backed NativeRoleRuntime execute HIVE tasks. Both Avalonia
/// and the headless Warband use this contract; Phase 3B does not provide an Ollama fallback.
/// </summary>
public interface IHiveNativeRoleExecutor : IAsyncDisposable
{
    IAsyncEnumerable<string> StreamRoleCompletionAsync(
        string hiveRole,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct);

    /// <summary>Human-readable telemetry suffix (e.g. " (model=..., tok/s=...)"), or "" if unavailable.</summary>
    string DescribeTelemetry(string hiveRole);

    Task<HiveNativeAgentExecution> ExecuteAgentAsync(
        HiveTaskBundle bundle,
        IReadOnlyList<AgentMessage> messages,
        CancellationToken ct);

    /// <summary>
    /// CF-6: runs a single-segment Context Fabric read (ContextFabricFeasibilityRunner.ReadCorpusAsync)
    /// instead of the generic agent/tool-call loop -- see CampaignPackCatalog.ContextFabricPackId's
    /// doc comment for why ExecuteAgentAsync's tool profile doesn't fit this pack.
    /// </summary>
    Task<HiveNativeAgentExecution> ExecuteContextFabricReaderAsync(
        HiveTaskBundle bundle,
        FabricCorpus corpus,
        CancellationToken ct);

    /// <summary>
    /// CF-6: runs the hierarchical reduction tree (ContextFabricFeasibilityRunner.ReduceEvidenceCardsAsync)
    /// over pre-read evidence cards supplied as input artifacts -- the distributed fan-in step that follows
    /// the reader fan-out. Outputs a serialized FabricCorpusReadReport to the output directory.
    /// </summary>
    Task<HiveNativeAgentExecution> ExecuteContextFabricReducerAsync(
        HiveTaskBundle bundle,
        FabricCorpus corpusMeta,
        IReadOnlyList<FabricEvidenceCard> cards,
        CancellationToken ct);
}

public sealed record HiveNativeAgentExecution(
    string Output,
    string OutputDirectory,
    int Steps,
    int PromptTokens,
    int CompletionTokens,
    string TraceDigest);

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
