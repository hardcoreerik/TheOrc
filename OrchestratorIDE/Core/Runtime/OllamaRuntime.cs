// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// IModelRuntime adapter over the existing OllamaClient (Phase 0).
///
/// This is a thin passthrough — every method delegates to the inner OllamaClient.
/// Zero behavior change vs the prior direct usage.  The inner client remains
/// accessible for Ollama-specific calls (GetLoadedModelsAsync, EvictAndVerifyAsync)
/// that are not part of the shared interface.
///
/// Construction: new OllamaRuntime(new OllamaClient(host, backend))
/// Tests:        new OllamaRuntime(new FakeOllamaClient())
/// </summary>
public sealed class OllamaRuntime : IModelRuntime
{
    private readonly OllamaClient _inner;
    private volatile bool _lastKnownReachable;

    public string RuntimeName => "Ollama";

    /// <summary>
    /// Exposes the underlying OllamaClient for callers that need Ollama-specific
    /// methods (GetLoadedModelsAsync, EvictAndVerifyAsync, Host, Backend).
    /// Phase 0 only — migrate callers incrementally as phases progress.
    /// </summary>
    public OllamaClient Inner => _inner;

    public OllamaRuntime(OllamaClient inner) =>
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    public async Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        _lastKnownReachable = await _inner.IsReachableAsync(ct);
        return _lastKnownReachable;
    }

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        _inner.GetInstalledModelsAsync(ct);

    public IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default) =>
        _inner.StreamCompletionAsync(
            model, history, tools, temperature, maxTokens, onToolCall, onUsage, ct);

    /// <summary>
    /// Returns current reachability as health. Does not make a network call —
    /// callers who need a live check should call IsReachableAsync first.
    /// </summary>
    public RuntimeHealth GetHealth() =>
        new(IsAvailable: _lastKnownReachable, RuntimeName: RuntimeName,
            Message: $"Host={_inner.Host}");

    /// <summary>All perf fields null — Ollama does not expose per-process VRAM or throughput.</summary>
    public RuntimeStats GetStats() =>
        new(RuntimeName: RuntimeName);
}
