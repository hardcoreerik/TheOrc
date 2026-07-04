// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record FabricAuthoredQuestionDraft(string TargetId, string QuestionText);

public sealed record FabricQuestionVerificationFailure(string QuestionId, string Reason);

/// <summary>
/// Merges externally-authored question drafts (Grok's paraphrase/two-hop questions, Codex's
/// global-synthesis/long-chain questions) back onto their manifest ground truth, and mechanically
/// verifies every resulting question before it is trusted -- the same check proven against the
/// host-templated 85 in ExpandedFabricQuestionGeneratorTests. A hallucinated or mismatched
/// authored question is rejected here, never silently accepted.
/// </summary>
public static class ExpandedFabricAuthoredQuestionMerger
{
    public static IReadOnlyList<FabricAuthoredQuestionDraft> ParseDrafts(string jsonArrayText)
    {
        if (string.IsNullOrWhiteSpace(jsonArrayText))
            throw new JsonException("Authored question output was empty.");
        var start = jsonArrayText.IndexOf('[');
        var end = jsonArrayText.LastIndexOf(']');
        if (start < 0 || end < start)
            throw new JsonException("Authored question output did not contain a JSON array.");
        var json = jsonArrayText[start..(end + 1)];
        return JsonSerializer.Deserialize<List<FabricAuthoredQuestionDraft>>(json, FabricJson.Options)
            ?? throw new JsonException("Authored question output parsed to null.");
    }

    public static IReadOnlyList<FabricBenchmarkQuestion> MergeParaphraseQuestions(
        IReadOnlyList<FabricAuthoredQuestionDraft> drafts, IReadOnlyList<FabricParaphraseTarget> targets)
    {
        var byId = targets.ToDictionary(t => t.FactId, StringComparer.Ordinal);
        return drafts
            .Where(draft => byId.ContainsKey(draft.TargetId))
            .Select(draft =>
            {
                var target = byId[draft.TargetId];
                return new FabricBenchmarkQuestion(
                    $"paraphrase-{target.FactId}", FabricQuestionKind.Paraphrased,
                    draft.QuestionText, target.ExpectedTerms, [target.SegmentId]);
            })
            .ToArray();
    }

    public static IReadOnlyList<FabricBenchmarkQuestion> MergeMultiHopQuestions(
        IReadOnlyList<FabricAuthoredQuestionDraft> drafts, IReadOnlyList<FabricMultiHopTarget> targets)
    {
        var byId = targets.ToDictionary(t => t.ChainId, StringComparer.Ordinal);
        return drafts
            .Where(draft => byId.ContainsKey(draft.TargetId))
            .Select(draft =>
            {
                var target = byId[draft.TargetId];
                return new FabricBenchmarkQuestion(
                    $"multihop-{target.ChainId}", FabricQuestionKind.MultiHop,
                    draft.QuestionText, target.DerivedAnswerTerms, target.HopSegmentIds);
            })
            .ToArray();
    }

    public static IReadOnlyList<FabricBenchmarkQuestion> MergeGlobalSynthesisQuestions(
        IReadOnlyList<FabricAuthoredQuestionDraft> drafts, IReadOnlyList<FabricGlobalSynthesisTarget> targets)
    {
        var byId = targets.ToDictionary(t => t.ThemeId, StringComparer.Ordinal);
        return drafts
            .Where(draft => byId.ContainsKey(draft.TargetId))
            .Select(draft =>
            {
                var target = byId[draft.TargetId];
                // Global synthesis is rubric-graded, not exact-term matched (see remediation-scope.md);
                // ExpectedTerms carries the theme facts as rubric hints rather than required substrings.
                return new FabricBenchmarkQuestion(
                    $"synthesis-{target.ThemeId}", FabricQuestionKind.GlobalSynthesis,
                    draft.QuestionText, target.ThemeFacts, target.SegmentIds);
            })
            .ToArray();
    }

    /// <summary>Rejects any question whose ExpectedTerms don't actually appear, verbatim, somewhere
    /// in the combined text of its ExpectedSegmentIds. Global synthesis questions are exempt --
    /// their ExpectedTerms are rubric hints describing a section range, not exact-match ground
    /// truth, since a correct synthesis answer legitimately paraphrases them.</summary>
    public static (IReadOnlyList<FabricBenchmarkQuestion> Verified, IReadOnlyList<FabricQuestionVerificationFailure> Failures)
        Verify(IReadOnlyList<FabricBenchmarkQuestion> candidates, IReadOnlyList<FabricSegment> segments)
    {
        var textBySegment = segments.ToDictionary(s => s.SegmentId, s => s.Text);
        var verified = new List<FabricBenchmarkQuestion>();
        var failures = new List<FabricQuestionVerificationFailure>();

        foreach (var question in candidates)
        {
            if (string.IsNullOrWhiteSpace(question.Question))
            {
                failures.Add(new FabricQuestionVerificationFailure(question.QuestionId, "empty question text"));
                continue;
            }

            if (question.ExpectAbstention)
            {
                verified.Add(question);
                continue;
            }

            if (question.ExpectedSegmentIds.Any(id => !textBySegment.ContainsKey(id)))
            {
                failures.Add(new FabricQuestionVerificationFailure(question.QuestionId, "references an unknown segment"));
                continue;
            }

            if (question.Kind == FabricQuestionKind.GlobalSynthesis)
            {
                verified.Add(question);
                continue;
            }

            var combinedText = string.Join(" ", question.ExpectedSegmentIds.Select(id => textBySegment[id]));
            var missingTerm = question.ExpectedTerms.FirstOrDefault(term => !combinedText.Contains(term, StringComparison.Ordinal));
            if (missingTerm is not null)
            {
                failures.Add(new FabricQuestionVerificationFailure(question.QuestionId,
                    $"expected term '{missingTerm}' does not appear in its expected segments"));
                continue;
            }

            verified.Add(question);
        }

        return (verified, failures);
    }
}
