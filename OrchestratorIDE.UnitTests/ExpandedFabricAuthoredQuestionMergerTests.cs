// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ExpandedFabricAuthoredQuestionMergerTests
{
    [Test]
    public void ParseDrafts_ExtractsArray_EvenWithSurroundingProseOrFences()
    {
        var raw = "Here you go:\n```json\n[{\"targetId\":\"fact-020\",\"questionText\":\"q1\"}]\n```\nDone.";
        var drafts = ExpandedFabricAuthoredQuestionMerger.ParseDrafts(raw);
        Assert.That(drafts, Has.Count.EqualTo(1));
        Assert.That(drafts[0].TargetId, Is.EqualTo("fact-020"));
    }

    [Test]
    public void MergeParaphraseQuestions_CarriesGroundTruth_FromTarget()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var ledger = ExpandedFabricLedgerExport.BuildGrokLedger(fixture.Manifest);
        var drafts = ledger.ParaphraseTargets
            .Select(t => new FabricAuthoredQuestionDraft(t.FactId, $"What value does {t.FactId} carry?"))
            .ToArray();

        var merged = ExpandedFabricAuthoredQuestionMerger.MergeParaphraseQuestions(drafts, ledger.ParaphraseTargets);

        Assert.That(merged, Has.Count.EqualTo(20));
        foreach (var question in merged)
            Assert.That(question.Kind, Is.EqualTo(FabricQuestionKind.Paraphrased));
    }

    [Test]
    public void Verify_RejectsQuestion_WhenExpectedTermIsFabricated()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var realFact = fixture.Manifest.LocalFacts[0];
        var bogus = new FabricBenchmarkQuestion(
            "bogus-1", FabricQuestionKind.Paraphrased, "irrelevant",
            ["THIS-TERM-DOES-NOT-EXIST-ANYWHERE"], [realFact.SegmentId]);

        var (verified, failures) = ExpandedFabricAuthoredQuestionMerger.Verify([bogus], fixture.Corpus.Segments);

        Assert.That(verified, Is.Empty);
        Assert.That(failures, Has.Count.EqualTo(1));
        Assert.That(failures[0].Reason, Does.Contain("does not appear"));
    }

    [Test]
    public void Verify_AcceptsQuestion_WhenExpectedTermGenuinelyAppears()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var realFact = fixture.Manifest.LocalFacts[0];
        var good = new FabricBenchmarkQuestion(
            "good-1", FabricQuestionKind.Paraphrased, "irrelevant",
            realFact.KeyTerms, [realFact.SegmentId]);

        var (verified, failures) = ExpandedFabricAuthoredQuestionMerger.Verify([good], fixture.Corpus.Segments);

        Assert.That(verified, Has.Count.EqualTo(1));
        Assert.That(failures, Is.Empty);
    }

    [Test]
    public void Verify_RejectsQuestion_ReferencingUnknownSegment()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var bogus = new FabricBenchmarkQuestion(
            "bogus-2", FabricQuestionKind.MultiHop, "irrelevant", ["x"], ["not-a-real-segment"]);

        var (verified, failures) = ExpandedFabricAuthoredQuestionMerger.Verify([bogus], fixture.Corpus.Segments);

        Assert.That(verified, Is.Empty);
        Assert.That(failures[0].Reason, Does.Contain("unknown segment"));
    }

    [Test]
    public void Verify_AcceptsGlobalSynthesisQuestion_WithoutRequiringExactTermMatch()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var theme = fixture.Manifest.ThemeClusters[0];
        var synthesis = new FabricBenchmarkQuestion(
            "synthesis-1", FabricQuestionKind.GlobalSynthesis, "irrelevant",
            ["a rubric hint that will not literally appear verbatim"], theme.SegmentIds);

        var (verified, failures) = ExpandedFabricAuthoredQuestionMerger.Verify([synthesis], fixture.Corpus.Segments);

        Assert.That(verified, Has.Count.EqualTo(1));
        Assert.That(failures, Is.Empty);
    }
}
