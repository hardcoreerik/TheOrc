// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Per-call sampling hints for the Native Runtime that the backend-neutral
/// <see cref="IModelRuntime"/> signature does not carry. Set with <see cref="WithRepeatPenalty"/>
/// around one generation; the role runtime reads <see cref="RepeatPenalty"/> when it builds the
/// sampler. Used by the Swarm Boss, whose plan degenerated into a repetition loop on Gemma 4
/// ("…the list of the list of the…") while chat and workers need no penalty. Flows with the
/// async call chain; other backends ignore it.
/// </summary>
public static class SamplingHints
{
    private static readonly AsyncLocal<float?> _repeatPenalty = new();

    /// <summary>1.0 = off. Null = no hint (the sampler default).</summary>
    public static float? RepeatPenalty => _repeatPenalty.Value;

    public static IDisposable WithRepeatPenalty(float penalty)
    {
        var previous = _repeatPenalty.Value;
        _repeatPenalty.Value = penalty;
        return new Scope(() => _repeatPenalty.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
