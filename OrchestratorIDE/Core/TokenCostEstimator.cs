// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Core;

/// <summary>
/// Estimated token cost for the next request (v1.4 roadmap item).
/// Local models bill in time, not dollars — so "cost" here is tokens and a
/// rough wall-clock projection at an observed/assumed generation speed.
/// Pure math over the chars/4 heuristic; pinned by T13 tests.
/// </summary>
public record TokenCostEstimate(
    int  ContextTokens,       // already in the conversation
    int  InputTokens,         // pending user input not yet sent
    int  CompletionTokens,    // assumed response budget
    int  MaxTokens)
{
    public int  PromptTokens => ContextTokens + InputTokens;
    public int  TotalTokens  => PromptTokens + CompletionTokens;
    public bool FitsContext  => TotalTokens <= MaxTokens;

    /// <summary>Rough wall-clock seconds at the given generation speed.</summary>
    public double EtaSeconds(double tokensPerSecond) =>
        tokensPerSecond <= 0 ? 0 : CompletionTokens / tokensPerSecond;

    /// <summary>One-line human summary for a status badge tooltip.</summary>
    public string Summary(double tokensPerSecond)
    {
        var eta = EtaSeconds(tokensPerSecond);
        string etaTxt = eta <= 0 ? "" :
            eta < 90 ? $" · ~{eta:F0}s response" : $" · ~{eta / 60:F1}m response";
        return $"context {ContextTokens:N0} + input {InputTokens:N0} + " +
               $"response ≈{CompletionTokens:N0} = {TotalTokens:N0} / {MaxTokens:N0} tokens" +
               etaTxt + (FitsContext ? "" : "  ⚠ EXCEEDS CONTEXT — history will be trimmed");
    }
}

public static class TokenCostEstimator
{
    /// <summary>Default response budget when the model hasn't told us better.</summary>
    public const int DefaultCompletionBudget = 700;

    public static TokenCostEstimate Estimate(
        int contextTokens, int maxTokens, string pendingInput,
        int completionBudget = DefaultCompletionBudget)
        => new(
            ContextTokens:    Math.Max(0, contextTokens),
            InputTokens:      string.IsNullOrEmpty(pendingInput)
                                  ? 0 : ContextManager.EstimateTokens(pendingInput),
            CompletionTokens: Math.Max(0, completionBudget),
            MaxTokens:        Math.Max(1, maxTokens));
}
