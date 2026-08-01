// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Stand-in "fallback" runtime for <see cref="NativeWithFallbackRuntime"/> when Ollama fallback
/// is disabled by policy (Native Runtime v2.0 testing phase: Ollama is no longer a silent safety
/// net for the main chat/agent loop — see docs/NATIVE_RUNTIME_V2_SPEC.md). Every call throws
/// immediately instead of completing the turn on Ollama, so a native failure surfaces as a
/// visible chat error — a gap to fix — rather than a silent success on a backend the user has
/// explicitly opted out of.
/// </summary>
public sealed class NoFallbackRuntime : IModelRuntime
{
    public string RuntimeName => "NoFallback (Ollama disabled)";

    public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(false);

    public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<string>());

    public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
        Task.FromResult<int?>(null);

    public IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        double? topP = null,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            $"Native Runtime failed to serve model '{model}' and Ollama fallback is disabled by " +
            "policy. This is a Native Runtime gap that needs to be fixed, not silently papered " +
            "over — see the Activity Log for the underlying native failure reason.");

    public RuntimeHealth GetHealth() =>
        new(IsAvailable: false, RuntimeName: RuntimeName, Message: "Ollama fallback disabled by policy.");

    public RuntimeStats GetStats() => new(RuntimeName);
}
