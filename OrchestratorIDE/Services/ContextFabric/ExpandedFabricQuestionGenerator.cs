// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

/// <summary>
/// Generates the four host-templated question categories (Needle/local fact, Exhaustive
/// enumeration, Unanswerable, Contradiction/change -- 85 of the docs' 150-question spec) directly
/// from <see cref="FabricExpandedManifest"/>. These categories are structurally regular enough
/// to generate deterministically with exact ground truth; the remaining three categories
/// (Paraphrased retrieval, Multi-hop, Global synthesis -- 65 questions) need natural-language
/// phrasing diversity and are authored externally (see remediation-scope.md).
/// </summary>
public static class ExpandedFabricQuestionGenerator
{
    /// <summary>Of the manifest's 20 generated contradiction pairs, only this many become scored
    /// questions here -- matching the docs' Contradiction/change minimum of 10 exactly. The
    /// remaining 10 stay in the manifest as held-out/dev-set candidates for later expansion.</summary>
    public const int ScoredContradictionCount = 10;

    public static IReadOnlyList<FabricBenchmarkQuestion> GenerateHostTemplatedQuestions(
        FabricExpandedManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var questions = new List<FabricBenchmarkQuestion>();

        foreach (var fact in manifest.LocalFacts)
            questions.Add(new FabricBenchmarkQuestion(
                $"local-{fact.FactId}",
                FabricQuestionKind.LocalFact,
                fact.QuestionText,
                fact.KeyTerms,
                [fact.SegmentId]));

        foreach (var category in manifest.ExhaustiveCategories)
            questions.Add(new FabricBenchmarkQuestion(
                $"exhaustive-{category.CategoryId}",
                FabricQuestionKind.Exhaustive,
                category.QuestionText,
                category.OccurrenceIds,
                category.OccurrenceSegmentIds));

        foreach (var gap in manifest.UnanswerableGaps)
            questions.Add(new FabricBenchmarkQuestion(
                $"unanswerable-{gap.GapId}",
                FabricQuestionKind.Unanswerable,
                gap.QuestionText,
                [],
                [],
                ExpectAbstention: true));

        foreach (var contradiction in manifest.Contradictions.Take(ScoredContradictionCount))
            questions.Add(new FabricBenchmarkQuestion(
                $"contradiction-{contradiction.ContradictionId}",
                FabricQuestionKind.Contradiction,
                contradiction.QuestionText,
                [contradiction.EarlierTerm, contradiction.LaterTerm],
                [contradiction.EarlierSegmentId, contradiction.LaterSegmentId]));

        return questions;
    }
}
