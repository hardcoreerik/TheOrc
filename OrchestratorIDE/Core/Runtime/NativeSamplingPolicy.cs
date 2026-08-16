// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Single construction path for native decoding/sampling parameters (temperature, top-p, and
/// the ORCISH TONGUE tool-name grammar), used by BOTH native completion paths:
///   - the stateless path (<see cref="LLamaSharpRuntime.StreamCompletionAsync"/>)
///   - the persistent per-role path (<see cref="NativeRoleRuntime"/> in IRoleRuntime.cs, backed
///     by AdapterManager's per-role <see cref="LLama.Batched.BatchedExecutor"/>)
///
/// Extracted after finding the persistent path built its own <see cref="LLama.Sampling.DefaultSamplingPipeline"/>
/// with only Temperature set, never attaching <see cref="ToolCallGrammarBuilder"/>'s tool-name
/// grammar the stateless path already had — meaning a persistent-role tool call could name any
/// string the model felt like generating, not just a live registered tool, while
/// LLamaSharpRuntime.cs's own docstring claimed native tool generation was "grammar-constrained"
/// as if that applied universally. It didn't. Both paths now go through this one method so a
/// future change to sampling/grammar policy can't silently diverge between them again.
/// </summary>
public static class NativeSamplingPolicy
{
    /// <summary>
    /// Builds the sampling pipeline for one completion call. Caller owns disposal (the returned
    /// pipeline owns native sampler-chain resources, including the grammar sampler when a tool
    /// grammar was attached — leaving it undisposed leaks native memory, same as before this
    /// extraction).
    /// </summary>
    public static LLama.Sampling.DefaultSamplingPipeline BuildPipeline(
        double temperature, double? topP, IReadOnlyList<object>? tools)
    {
        var grammar = TryBuildToolGrammar(tools);
        return topP is { } p
            ? new LLama.Sampling.DefaultSamplingPipeline { Temperature = (float)temperature, TopP = (float)p, Grammar = grammar }
            : new LLama.Sampling.DefaultSamplingPipeline { Temperature = (float)temperature, Grammar = grammar };
    }

    /// <summary>
    /// When a live tool list is present, constrain decoding so the model cannot emit a tool-call
    /// JSON naming anything outside that list — structurally, not probabilistically (ORCISH
    /// TONGUE Phase 2). Returns null (unconstrained decoding) when there are no tools, when
    /// <see cref="ToolCallGrammarBuilder.Build"/> can't extract any tool names, or when the GBNF
    /// string it returns fails to construct into a native <see cref="LLama.Sampling.Grammar"/> —
    /// a malformed grammar must degrade to unconstrained decoding for that call, not abort it.
    /// </summary>
    public static LLama.Sampling.Grammar? TryBuildToolGrammar(IReadOnlyList<object>? tools)
    {
        if (tools is not { Count: > 0 } || ToolCallGrammarBuilder.Build(tools) is not { } gbnf)
            return null;

        try
        {
            return new LLama.Sampling.Grammar(gbnf, "root");
        }
        catch (Exception ex)
        {
            // Matches the existing lightweight [Tag] Console logging convention used around
            // native completion (no ILogger is injected at this layer).
            Console.Error.WriteLine($"[ORCISH TONGUE] failed to construct GBNF grammar, " +
                $"falling back to unconstrained decoding for this call: {ex.Message}");
            return null;
        }
    }
}
