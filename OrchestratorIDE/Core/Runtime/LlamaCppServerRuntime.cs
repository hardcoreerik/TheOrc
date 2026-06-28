// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// IModelRuntime for the llama.cpp server backend (Phase 1).
///
/// Manages the LlamaServerManager process lifecycle and routes inference
/// through an OllamaClient pointed at the server's local HTTP endpoint.
/// The OllamaClient is configured for InferenceBackend.LlamaCpp so it uses
/// /v1/models for discovery and /v1/chat/completions for streaming.
///
/// Construction:
///   var mgr = new LlamaServerManager { RuntimePath = ..., ModelPath = ... };
///   var runtime = new LlamaCppServerRuntime(mgr);
///   await runtime.StartAsync();   // launches the process, waits for /health
///   _loop = new AgentLoop(runtime, ...);
/// </summary>
public sealed class LlamaCppServerRuntime : IModelRuntime, IDisposable
{
    private readonly LlamaServerManager _server;
    private readonly OllamaClient _client;

    public string RuntimeName => "LlamaCpp";

    /// <summary>
    /// Exposes the underlying server manager for callers that need lifecycle
    /// control (StartAsync, Stop, OnLog, OnStatusChanged).
    /// </summary>
    public LlamaServerManager Server => _server;

    private string? ActiveModelName => string.IsNullOrEmpty(_server.ModelPath)
        ? null
        : Path.GetFileName(_server.ModelPath);

    public LlamaCppServerRuntime(LlamaServerManager server)
    {
        _server = server ?? throw new ArgumentNullException(nameof(server));
        _client = new OllamaClient(server.BaseUrl, InferenceBackend.LlamaCpp);
    }

    public Task<bool> IsReachableAsync(CancellationToken ct = default)
    {
        // Fast-path: if the process isn't even running, skip the network probe.
        if (!_server.IsRunning) return Task.FromResult(false);
        return _client.IsReachableAsync(ct);
    }

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default)
    {
        // Mirror the IsReachableAsync fast-path: OllamaClient has a 300 s timeout, so calling
        // it against a stopped server would block the caller for up to 5 minutes.
        if (!_server.IsRunning) return Task.FromResult<List<string>>([]);
        return _client.GetInstalledModelsAsync(ct);
    }

    public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default)
    {
        if (!_server.IsRunning) return Task.FromResult<int?>(null);
        return _client.GetContextLengthAsync(model, ct);
    }

    public IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        double? topP = null,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default)
    {
        if (!_server.IsRunning)
            throw new InvalidOperationException(
                "LlamaCpp server is not running. Call StartAsync() before streaming.");
        return _client.StreamCompletionAsync(
            model, history, tools, temperature, topP, maxTokens, onToolCall, onUsage, ct);
    }

    /// <summary>
    /// Process-level health — no network call. IsAvailable reflects whether
    /// the llama-server.exe process is alive, not whether it has finished loading.
    /// Call IsReachableAsync() for a live /health probe after StartAsync().
    /// </summary>
    public RuntimeHealth GetHealth() => new(
        IsAvailable: _server.IsRunning,
        RuntimeName: RuntimeName,
        ActiveModel: ActiveModelName,
        Message: _server.IsRunning ? $"Port={_server.Port}" : "Not running");

    /// <summary>All perf fields null — llama.cpp does not expose per-process VRAM via the HTTP API.</summary>
    public RuntimeStats GetStats() => new(
        RuntimeName: RuntimeName,
        ActiveModel: ActiveModelName);

    /// <summary>
    /// Convenience passthrough: start the managed llama-server.exe and wait until healthy.
    /// Returns false if the process crashed or the timeout elapsed.
    /// </summary>
    public Task<bool> StartAsync(TimeSpan? loadTimeout = null, CancellationToken ct = default) =>
        _server.StartAsync(loadTimeout, ct);

    /// <summary>Stops the managed llama-server.exe process.</summary>
    public void Stop() => _server.Stop();

    public void Dispose() => _server.Dispose();
}
