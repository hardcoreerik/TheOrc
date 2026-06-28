// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Models;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Backend-neutral inference surface. The orchestration layer (AgentLoop,
/// SwarmSession, HiveWorkerAgent, ChatEngine) depends on this instead of a
/// concrete client, enabling the backend to change without touching the swarm.
///
/// Phase 0: OllamaRuntime (wraps the existing OllamaClient — zero behavior change).
/// Phase 2: LLamaSharpRuntime (in-process GGUF — the "no Ollama dependency" win).
///
/// Interface contract copied verbatim from the working OllamaClient.StreamCompletionAsync
/// signature so FakeOllamaClient wraps without rework. See RUNTIME_PHASE0_SPEC.md §2.
/// </summary>
public interface IModelRuntime
{
    /// <summary>Human-readable backend name for logs/telemetry, e.g. "Ollama".</summary>
    string RuntimeName { get; }

    /// <summary>Quick connectivity check (≤ 3 s). Maps to OllamaClient.IsReachableAsync.</summary>
    Task<bool> IsReachableAsync(CancellationToken ct = default);

    /// <summary>List model IDs the backend can currently serve.</summary>
    Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Best-effort max context window for the given model. Returns null when the backend
    /// cannot report one honestly.
    /// </summary>
    Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default);

    /// <summary>
    /// Stream a completion. Text deltas are yielded; tool calls and token usage
    /// are delivered via callbacks — exactly matching OllamaClient.StreamCompletionAsync.
    ///
    /// This split is load-bearing: the UI streams text live while tool calls accumulate.
    /// Do not flatten tool calls into the yielded stream.
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        // Nullable, unlike temperature -- null means "use the backend's own default" rather
        // than a specific value to send. Only OllamaRuntime's underlying endpoint
        // (Ollama's OpenAI-compatible /v1/chat/completions) and LLamaSharpRuntime's
        // DefaultSamplingPipeline both support top_p; top_k is NOT exposed here because
        // Ollama's OpenAI-compat layer has no equivalent field for it (confirmed against
        // Ollama's own docs/GitHub issue #11325) -- exposing a parameter one backend can't
        // act on would silently do nothing on that backend, which is worse than not
        // exposing it at all.
        double? topP = null,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default);

    /// <summary>What the runtime knows about itself right now. Unknown fields = null.</summary>
    RuntimeHealth GetHealth();

    /// <summary>
    /// Perf telemetry. Every field is nullable — return null rather than invented numbers.
    /// OllamaRuntime returns null for per-process VRAM (Ollama does not expose it per-call).
    /// </summary>
    RuntimeStats GetStats();
}

// ── Supporting records ────────────────────────────────────────────────────────

public sealed record RuntimeHealth(
    bool    IsAvailable,
    string  RuntimeName,
    string? ActiveModel = null,
    string? Message     = null);

public sealed record RuntimeStats(
    string    RuntimeName,
    string?   ActiveModel         = null,
    double?   TokensPerSecond     = null,   // null until measured
    TimeSpan? LastTimeToFirstToken = null,  // null until measured
    long?     EstimatedVramBytes   = null); // null on Ollama (not exposed per-process)

// ── ILocalModelRuntime — native-only capabilities (Phase 2+) ─────────────────
// OllamaRuntime does NOT implement this. Callers that only generate depend on
// IModelRuntime. Only the future SessionManager depends on ILocalModelRuntime.

public interface ILocalModelRuntime : IModelRuntime, IAsyncDisposable
{
    /// <summary>Load a GGUF base model, optionally with a LoRA adapter path.</summary>
    Task<ModelLoadResult> LoadModelAsync(
        string baseGgufPath,
        string? adapterPath = null,
        RuntimeOptions? options = null,
        CancellationToken ct = default);

    // LoRA hot-swap requires a verification spike before roadmapping — see RUNTIME_PHASE0_SPEC.md §7.
    // SwapAdapterAsync is reserved here so the interface evolves cleanly, but is NOT implemented
    // until the spike confirms the KV-cache safety of adapter detach/reattach.
    Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default);
}

public sealed record RuntimeOptions(
    int  ContextLength = 8192,
    int  GpuLayers     = -1,
    bool PreferGpu     = true);

public sealed record ModelLoadResult(
    bool    Success,
    string  RuntimeName,
    string  ModelRef,
    string? Message = null);

public interface IContextLengthProvider
{
    Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default);
}
