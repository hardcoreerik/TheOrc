// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ExpandedFabricQuestionSplitterTests
{
    private static IReadOnlyList<FabricBenchmarkQuestion> BuildFullSuite()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var host = ExpandedFabricQuestionGenerator.GenerateHostTemplatedQuestions(fixture.Manifest);
        var grokLedger = ExpandedFabricLedgerExport.BuildGrokLedger(fixture.Manifest);
        var codexLedger = ExpandedFabricLedgerExport.BuildCodexLedger(fixture.Manifest);
        var paraphrase = ExpandedFabricAuthoredQuestionMerger.MergeParaphraseQuestions(
            grokLedger.ParaphraseTargets.Select(t => new FabricAuthoredQuestionDraft(t.FactId, "q")).ToArray(),
            grokLedger.ParaphraseTargets);
        var grokHop = ExpandedFabricAuthoredQuestionMerger.MergeMultiHopQuestions(
            grokLedger.MultiHopTargets.Select(t => new FabricAuthoredQuestionDraft(t.ChainId, "q")).ToArray(),
            grokLedger.MultiHopTargets);
        var codexHop = ExpandedFabricAuthoredQuestionMerger.MergeMultiHopQuestions(
            codexLedger.MultiHopTargets.Select(t => new FabricAuthoredQuestionDraft(t.ChainId, "q")).ToArray(),
            codexLedger.MultiHopTargets);
        var synthesis = ExpandedFabricAuthoredQuestionMerger.MergeGlobalSynthesisQuestions(
            codexLedger.GlobalSynthesisTargets.Select(t => new FabricAuthoredQuestionDraft(t.ThemeId, "q")).ToArray(),
            codexLedger.GlobalSynthesisTargets);
        return host.Concat(paraphrase).Concat(grokHop).Concat(codexHop).Concat(synthesis).ToArray();
    }

    [Test]
    public void Split_ProducesNoOverlap_AndCoversEveryQuestionExactlyOnce()
    {
        var all = BuildFullSuite();
        var split = ExpandedFabricQuestionSplitter.Split(all);

        Assert.That(split.Development.Count + split.HeldOut.Count, Is.EqualTo(all.Count));
        var devIds = split.Development.Select(q => q.QuestionId).ToHashSet(StringComparer.Ordinal);
        var heldOutIds = split.HeldOut.Select(q => q.QuestionId).ToHashSet(StringComparer.Ordinal);
        Assert.That(devIds.Intersect(heldOutIds), Is.Empty);
    }

    [Test]
    public void Split_IsStratified_EveryKindHasAtLeastOneDevelopmentQuestion()
    {
        var all = BuildFullSuite();
        var split = ExpandedFabricQuestionSplitter.Split(all);

        foreach (var kind in all.Select(q => q.Kind).Distinct())
            Assert.That(split.Development.Any(q => q.Kind == kind), Is.True, $"{kind} has no development question");
    }

    [Test]
    public void Split_IsWeightedTowardHeldOut()
    {
        var all = BuildFullSuite();
        var split = ExpandedFabricQuestionSplitter.Split(all);

        Assert.That(split.HeldOut.Count, Is.GreaterThan(split.Development.Count * 2));
    }

    [Test]
    public void Split_IsDeterministic_AcrossRepeatedCalls()
    {
        var all = BuildFullSuite();
        var first = ExpandedFabricQuestionSplitter.Split(all);
        var second = ExpandedFabricQuestionSplitter.Split(all);

        Assert.That(first.Development.Select(q => q.QuestionId), Is.EqualTo(second.Development.Select(q => q.QuestionId)));
        Assert.That(first.HeldOut.Select(q => q.QuestionId), Is.EqualTo(second.HeldOut.Select(q => q.QuestionId)));
    }
}
