// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Covers the B2 "conventional top-k RAG" baseline rewrite: IDF-weighted, stopword-aware segment
/// scoring, and greedy budget-filling selection instead of a fixed segment count.
/// </summary>
[TestFixture]
public sealed class ContextFabricB2TopKRagTests
{
    // FabricContextBudget enforces ContextLimit >= 2048 and EvidenceLimit (ContextLimit minus its
    // own 1536/512 reserves) > 0, so tests must keep ContextLimit comfortably above 2048 and
    // instead tune AnswerMaxTokens (a separate FabricRunOptions field the runner's own budget
    // math actually subtracts) to get a tight or exhausted effective budget.
    private static FabricRunOptions Options(int answerMaxTokens) =>
        new(new FabricContextBudget(ContextLimit: 2200), AnswerMaxTokens: answerMaxTokens);

    [Test]
    public void BuildTopKText_PrefersSegmentWithRareDistinctiveTerm_OverCommonWordOverlap()
    {
        // "checksum" and "CK-991" are rare/distinctive; "the", "was", "and" appear everywhere and
        // must not drive the ranking.
        var target = new FabricSegment("seg-target", 1, "Target",
            "The archive recorded the checksum CK-991 for this shipment.",
            FabricHashing.Sha256("target"), 20);
        var distractor = new FabricSegment("seg-distractor", 2, "Distractor",
            "The archive and the depot were and the records were the same as the other archive.",
            FabricHashing.Sha256("distractor"), 20);
        var corpus = new FabricCorpus("corpus-1", "doc-1", "gen-1", "digest-1", "1.0",
            [target, distractor], 40);
        var question = new FabricBenchmarkQuestion(
            "q-1", FabricQuestionKind.LocalFact, "What checksum was recorded for the shipment?",
            ["CK-991"], ["seg-target"]);
        var fixture = new FabricBenchmarkFixture(corpus, [question]);

        // AnswerMaxTokens tuned so the effective budget (~25 tokens) fits one 20-token segment
        // but not both (40 total) -- forces the ranking to actually decide, rather than both
        // segments trivially fitting.
        var runner = new ContextFabricBaselineRunner(new ScriptedFabricRuntime(), Options(answerMaxTokens: 1652));
        var text = runner.BuildTopKText(fixture, question);

        Assert.That(text, Does.Contain("CK-991"));
        Assert.That(text, Does.Not.Contain(distractor.Text));
    }

    [Test]
    public void BuildTopKText_SelectsMoreThanFourSegments_WhenBudgetAllowsAndAllAreRelevant()
    {
        // Six short segments, each sharing a distinctive term with the question. The old
        // implementation hard-capped at 4 regardless of budget; this must not.
        var segments = Enumerable.Range(1, 6)
            .Select(i => new FabricSegment($"seg-{i}", i, $"Section {i}",
                $"Station Bravo logged a distinct reading labeled MARK-{i:D3} during this cycle.",
                FabricHashing.Sha256($"seg-{i}"), 20))
            .ToArray();
        var corpus = new FabricCorpus("corpus-2", "doc-2", "gen-2", "digest-2", "1.0", segments, 120);
        var question = new FabricBenchmarkQuestion(
            "q-2", FabricQuestionKind.Exhaustive,
            "What readings did Station Bravo log across the cycle?",
            ["MARK-001"], ["seg-1"]);
        var fixture = new FabricBenchmarkFixture(corpus, [question]);

        // Generous budget: 8192 limit easily fits all six ~20-token segments plus overhead.
        var runner = new ContextFabricBaselineRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var text = runner.BuildTopKText(fixture, question);

        var includedCount = segments.Count(s => text.Contains(s.Text, StringComparison.Ordinal));
        Assert.That(includedCount, Is.GreaterThan(4));
    }

    [Test]
    public void BuildTopKText_ReturnsEmpty_WhenQuestionHasNoNonStopwordTerms()
    {
        var segment = new FabricSegment("seg-a", 1, "A", "Some genuinely distinctive content ABC-123.",
            FabricHashing.Sha256("a"), 10);
        var corpus = new FabricCorpus("corpus-3", "doc-3", "gen-3", "digest-3", "1.0", [segment], 10);
        // Every word here is a stopword per the runner's list.
        var question = new FabricBenchmarkQuestion(
            "q-3", FabricQuestionKind.LocalFact, "What was this and that with the same?",
            ["ABC-123"], ["seg-a"]);
        var fixture = new FabricBenchmarkFixture(corpus, [question]);

        var runner = new ContextFabricBaselineRunner(new ScriptedFabricRuntime(), Options(answerMaxTokens: 1500));
        var text = runner.BuildTopKText(fixture, question);

        Assert.That(text, Is.Empty);
    }

    [Test]
    public void BuildTopKText_ReturnsEmpty_WhenBudgetIsExhausted()
    {
        var segment = new FabricSegment("seg-a", 1, "A", "A checksum CK-500 was recorded here.",
            FabricHashing.Sha256("a"), 20);
        var corpus = new FabricCorpus("corpus-4", "doc-4", "gen-4", "digest-4", "1.0", [segment], 20);
        var question = new FabricBenchmarkQuestion(
            "q-4", FabricQuestionKind.LocalFact, "What checksum was recorded?",
            ["CK-500"], ["seg-a"]);
        var fixture = new FabricBenchmarkFixture(corpus, [question]);

        // AnswerMaxTokens tuned so ContextLimit(2200) - AnswerMaxTokens - question - 512 is negative.
        var runner = new ContextFabricBaselineRunner(new ScriptedFabricRuntime(), Options(answerMaxTokens: 1700));
        var text = runner.BuildTopKText(fixture, question);

        Assert.That(text, Is.Empty);
    }

    [Test]
    public void BuildTopKText_SkipsOverBudgetSegment_ButStillFitsShorterLowerRankedOne()
    {
        // The highest-scoring segment is too large to fit; a shorter, lower-scoring but still
        // relevant segment should still be included rather than leaving the budget unused.
        var big = new FabricSegment("seg-big", 1, "Big",
            "checksum CK-777 checksum CK-777 checksum CK-777 padding padding padding padding padding padding padding",
            FabricHashing.Sha256("big"), 500);
        var small = new FabricSegment("seg-small", 2, "Small", "checksum CK-777 noted briefly.",
            FabricHashing.Sha256("small"), 15);
        var corpus = new FabricCorpus("corpus-5", "doc-5", "gen-5", "digest-5", "1.0", [big, small], 515);
        var question = new FabricBenchmarkQuestion(
            "q-5", FabricQuestionKind.LocalFact, "What checksum was noted?", ["CK-777"], ["seg-small"]);
        var fixture = new FabricBenchmarkFixture(corpus, [question]);

        // AnswerMaxTokens tuned so the effective budget (~50 tokens) fits "small" (15 tokens) but
        // not "big" (500 tokens), even though "big" scores higher (three checksum mentions).
        var runner = new ContextFabricBaselineRunner(new ScriptedFabricRuntime(), Options(answerMaxTokens: 1632));
        var text = runner.BuildTopKText(fixture, question);

        Assert.That(text, Does.Contain("noted briefly"));
        Assert.That(text, Does.Not.Contain("padding"));
    }
}
