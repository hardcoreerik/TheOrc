// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Covers the BuildExhaustiveAnswer fix in ContextFabricFeasibilityRunner. All 12 Exhaustive
/// failures in the real CF-7 gate run hit the identical error: "answer claim ... contains more
/// than N citations" -- the old filter (any word overlap with the question) pulled in claims
/// from every unrelated ledger in the corpus because generic filler words ("ledger", "recorded")
/// appear almost everywhere, not just in the ledger actually asked about.
///
/// These tests reproduce the exact real-world identifier pattern that made the naive fix
/// (IDF-weighted scoring alone) insufficient: identifiers like "case-ledger-01" split on the
/// hyphen into "case", "ledger", "01" -- and the shared Tokenize() helper drops anything under 3
/// characters, silently discarding "01", the one token that actually distinguishes ledger 01 from
/// ledger 09. TokenizeForScoring (2-character minimum, used only by the scoring path) is what
/// makes the fix work in practice, not just in a simplified example with longer identifiers.
/// </summary>
[TestFixture]
public sealed class ContextFabricExhaustiveAnswerTests
{
    private static FabricCorpus Corpus(params string[] segmentIds)
    {
        var segments = segmentIds.Select((id, i) => new FabricSegment(id, i + 1, $"Section {i + 1}", "", "", 10)).ToArray();
        return new FabricCorpus("corpus-exhaustive", "doc-exhaustive", "gen-1", "digest-1", "1.0", segments, 100);
    }

    private static FabricEvidenceCard Card(string segmentId, string claimText) => new()
    {
        SegmentId = segmentId,
        Summary = claimText,
        Claims = [new FabricClaim { ClaimId = $"{segmentId}-c1", Text = claimText }],
    };

    [Test]
    public void BuildExhaustiveAnswer_ExcludesUnrelatedLedger_DespiteSharedGenericWords()
    {
        // Exact real-world identifier shape: "case-ledger-01" tokenizes (on hyphens) to
        // "case"/"ledger"/"01" -- only "01" actually distinguishes it from "case-ledger-09". Enough
        // unrelated ledgers are included that ledger-01's 2 cards are a clear minority (not exactly
        // half), mirroring the real ~15-ledger corpus scale rather than sitting on a 50% boundary.
        var target1 = Card("seg-l01-a", "Ledger case-ledger-01 lists entry CASE-01-0 as an open case file.");
        var target2 = Card("seg-l01-b", "Ledger case-ledger-01 lists entry CASE-01-1 as an open case file.");
        var otherLedgers = Enumerable.Range(2, 8)
            .Select(i => Card($"seg-l{i:00}-a", $"Ledger case-ledger-{i:00} lists entry CASE-{i:00}-0 as an open case file."))
            .ToArray();
        var allCards = new[] { target1, target2 }.Concat(otherLedgers).ToArray();
        var corpus = Corpus(allCards.Select(c => c.SegmentId).ToArray());
        var question = new FabricBenchmarkQuestion(
            "q-1", FabricQuestionKind.Exhaustive,
            "List every case-file ID recorded under ledger case-ledger-01, in any order.",
            ["CASE-01-0", "CASE-01-1"], ["seg-l01-a", "seg-l01-b"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var result = runner.BuildExhaustiveAnswer(corpus, question, allCards);

        Assert.Multiple(() =>
        {
            Assert.That(result.Answer?.Answer, Does.Contain("CASE-01-0"));
            Assert.That(result.Answer?.Answer, Does.Contain("CASE-01-1"));
            Assert.That(result.Answer?.Answer, Does.Not.Contain("CASE-02-0"));
            Assert.That(result.Answer?.Answer, Does.Not.Contain("CASE-09-0"));
        });
    }

    [Test]
    public void BuildExhaustiveAnswer_IncludesAllGenuinelyMatchingEntries_NotJustOne()
    {
        // Proves no artificial single-card cap remains: every entry under the asked-about ledger
        // should appear, however many there are.
        var cards = Enumerable.Range(0, 5)
            .Select(i => Card($"seg-l01-{i}", $"Ledger case-ledger-01 lists entry CASE-01-{i} as an open case file."))
            .ToArray();
        var segmentIds = cards.Select(c => c.SegmentId).ToArray();
        var corpus = Corpus(segmentIds);
        // Expectations cover all 5 entries, not just the first -- a fixture that only declares
        // one expected answer/segment can't catch a regression that silently drops entries 2-5
        // (CodeRabbit finding, 2026-07-04).
        var expectedAnswers = Enumerable.Range(0, 5).Select(i => $"CASE-01-{i}").ToArray();
        var question = new FabricBenchmarkQuestion(
            "q-2", FabricQuestionKind.Exhaustive,
            "List every case-file ID recorded under ledger case-ledger-01, in any order.",
            expectedAnswers, segmentIds);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var result = runner.BuildExhaustiveAnswer(corpus, question, cards);

        Assert.Multiple(() =>
        {
            Assert.That(result.IncludedSegmentIds, Is.EquivalentTo(segmentIds));
            foreach (var expected in expectedAnswers)
                Assert.That(result.Answer?.Answer, Does.Contain(expected));
        });
    }

    [Test]
    public void BuildExhaustiveAnswer_ExcludesCardsWithNoRelevantMatch()
    {
        // Background cards give "recorded" a realistic document frequency (appears in many
        // unrelated claims, same as in the real corpus), so it doesn't accidentally tie with "01"
        // (a genuinely rare, ledger-01-specific term) for rarest-term status. A too-small fixture
        // makes every word "rare" by accident; this mirrors the real ~90-card corpus scale instead.
        var target = Card("seg-l01-a", "Ledger case-ledger-01 lists entry CASE-01-0 as an open case file.");
        var irrelevant = Card("seg-other", "Vessel Alpha's approved rating was recorded as grade-3.");
        var background = Enumerable.Range(0, 6)
            .Select(i => Card($"seg-bg-{i}", $"Station Bravo-{i}'s approved rating was recorded as grade-{i}."))
            .ToArray();
        var allCards = new[] { target, irrelevant }.Concat(background).ToArray();
        var corpus = Corpus(allCards.Select(c => c.SegmentId).ToArray());
        var question = new FabricBenchmarkQuestion(
            "q-3", FabricQuestionKind.Exhaustive,
            "List every case-file ID recorded under ledger case-ledger-01, in any order.",
            ["CASE-01-0"], ["seg-l01-a"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var result = runner.BuildExhaustiveAnswer(corpus, question, allCards);

        Assert.That(result.IncludedSegmentIds, Is.EquivalentTo(new[] { "seg-l01-a" }));
    }

    [Test]
    public void BuildExhaustiveAnswer_IncludesEveryMatchingClaim_WhenOneCardListsMultipleEntries()
    {
        // A single card whose claims list two distinct case IDs under the same ledger -- the
        // prior FirstOrDefault() kept only the highest-scoring claim and silently dropped the
        // rest (CodeRabbit finding, 2026-07-04). Both must appear now.
        var card = new FabricEvidenceCard
        {
            SegmentId = "seg-l01-multi",
            Summary = "Ledger case-ledger-01 entries.",
            Claims =
            [
                new FabricClaim { ClaimId = "seg-l01-multi-c1", Text = "Ledger case-ledger-01 lists entry CASE-01-0 as an open case file." },
                new FabricClaim { ClaimId = "seg-l01-multi-c2", Text = "Ledger case-ledger-01 lists entry CASE-01-1 as an open case file." },
            ],
        };
        var corpus = Corpus(card.SegmentId);
        var question = new FabricBenchmarkQuestion(
            "q-4", FabricQuestionKind.Exhaustive,
            "List every case-file ID recorded under ledger case-ledger-01, in any order.",
            ["CASE-01-0", "CASE-01-1"], [card.SegmentId]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var result = runner.BuildExhaustiveAnswer(corpus, question, [card]);

        Assert.Multiple(() =>
        {
            Assert.That(result.Answer?.Answer, Does.Contain("CASE-01-0"));
            Assert.That(result.Answer?.Answer, Does.Contain("CASE-01-1"));
        });
    }
}
