// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record FabricQuestionSplit(
    IReadOnlyList<FabricBenchmarkQuestion> Development,
    IReadOnlyList<FabricBenchmarkQuestion> HeldOut);

/// <summary>
/// Splits the verified question suite into development (prompt-tuning only) and held-out (the set
/// that actually gates CF-7) pools, per docs/The Orc Context Fabric.md:963: "Questions and expected
/// evidence are split into development and held-out sets. Prompt tuning uses only development
/// questions." The docs do not specify a ratio; this uses a small, deterministic 20% development
/// share, stratified per question kind so every category has development coverage, biased toward
/// held-out since a benchmark gate should mostly grade on questions its own prompts never tuned
/// against. The split is index-based (not random) so it is exactly reproducible from the same
/// verified question list.
/// </summary>
public static class ExpandedFabricQuestionSplitter
{
    public const int DevelopmentEveryNth = 5;

    public static FabricQuestionSplit Split(IReadOnlyList<FabricBenchmarkQuestion> verifiedQuestions)
    {
        ArgumentNullException.ThrowIfNull(verifiedQuestions);
        var development = new List<FabricBenchmarkQuestion>();
        var heldOut = new List<FabricBenchmarkQuestion>();

        foreach (var group in verifiedQuestions.GroupBy(q => q.Kind))
        {
            var ordered = group.OrderBy(q => q.QuestionId, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < ordered.Length; i++)
            {
                if (i % DevelopmentEveryNth == 0)
                    development.Add(ordered[i]);
                else
                    heldOut.Add(ordered[i]);
            }
        }

        return new FabricQuestionSplit(development, heldOut);
    }
}
