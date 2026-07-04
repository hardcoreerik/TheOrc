// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Covers the BuildEvidencePack fix in ContextFabricFeasibilityRunner (the real production
/// Context Fabric answering path, used by FabricNativeReaderService and
/// HiveNativeRoleExecutorAdapter): IDF-weighted, stopword-aware card scoring and greedy
/// budget-filling instead of a fixed per-question-kind card count.
/// </summary>
[TestFixture]
public sealed class ContextFabricEvidencePackTests
{
    // Unlike ContextFabricBaselineRunner's ComputeBudget (which subtracts AnswerMaxTokens),
    // BuildEvidencePack checks each candidate directly against ContextBudget.EvidenceLimit
    // (ContextLimit - ResponseReserve - SystemReserve). FabricContextBudget.Validate() requires
    // ContextLimit >= 2048, ResponseReserve/SystemReserve >= 128, and EvidenceLimit > 0, so tests
    // fix ContextLimit at the 2048 minimum and SystemReserve at its 128 minimum, then dial
    // ResponseReserve to land on the desired EvidenceLimit.
    private static FabricRunOptions Options(int evidenceLimit) =>
        new(new FabricContextBudget(ContextLimit: 2048, ResponseReserve: 2048 - 128 - evidenceLimit, SystemReserve: 128));

    private static FabricEvidenceCard Card(string segmentId, string summary, string claimText) => new()
    {
        SegmentId = segmentId,
        Summary = summary,
        Claims = [new FabricClaim { ClaimId = $"{segmentId}-c1", Text = claimText }],
    };

    [Test]
    public void BuildEvidencePack_PrefersCardWithRareDistinctiveTerm_OverCommonWordOverlap()
    {
        var target = Card("seg-target", "Checksum recorded.", "The archive recorded the checksum CK-991 for this shipment.");
        var distractor = Card("seg-distractor", "General narration.", "The archive and the depot were and the records were the same as the other archive.");
        var question = new FabricBenchmarkQuestion(
            "q-1", FabricQuestionKind.LocalFact, "What checksum was recorded for the shipment?",
            ["CK-991"], ["seg-target"]);

        // EvidenceLimit tuned so only one card's worth of serialized evidence fits -- forces the
        // ranking to actually decide, rather than both trivially fitting.
        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), Options(evidenceLimit: 200));
        var pack = runner.BuildEvidencePack(question, [target, distractor], null);

        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-target"));
        Assert.That(pack.IncludedSegmentIds, Does.Not.Contain("seg-distractor"));
    }

    [Test]
    public void BuildEvidencePack_SelectsMoreThanFourCards_WhenBudgetAllowsAndAllAreRelevant()
    {
        // Six cards, each sharing a distinctive term with the question. The old implementation
        // hard-capped everything outside LocalFact/MultiHop/Contradiction at 4 regardless of
        // budget; this must not.
        var cards = Enumerable.Range(1, 6)
            .Select(i => Card($"seg-{i}", $"Reading {i}.",
                $"Station Bravo logged a distinct reading labeled MARK-{i:D3} during this cycle."))
            .ToArray();
        var question = new FabricBenchmarkQuestion(
            "q-2", FabricQuestionKind.GlobalSynthesis,
            "What readings did Station Bravo log across the cycle?",
            ["MARK-001"], ["seg-1"]);

        // Generous default budget easily fits all six short cards.
        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, cards, null);

        Assert.That(pack.IncludedSegmentIds.Count, Is.GreaterThan(4));
    }

    [Test]
    public void BuildEvidencePack_ExcludesCard_WhenQuestionHasNoNonStopwordTerms()
    {
        var card = Card("seg-a", "Distinctive content.", "Some genuinely distinctive content ABC-123.");
        // Every word here is a stopword per the runner's list.
        var question = new FabricBenchmarkQuestion(
            "q-3", FabricQuestionKind.LocalFact, "What was this and that with the same?",
            ["ABC-123"], ["seg-a"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), Options(evidenceLimit: 500));
        var pack = runner.BuildEvidencePack(question, [card], null);

        Assert.That(pack.IncludedSegmentIds, Is.Empty);
    }

    [Test]
    public void BuildEvidencePack_SkipsOverBudgetCard_ButStillFitsShorterLowerRankedOne()
    {
        var big = Card("seg-big", "Checksum noted.",
            "checksum CK-777 checksum CK-777 checksum CK-777 padding padding padding padding padding padding padding padding padding padding");
        var small = Card("seg-small", "Checksum noted.", "checksum CK-777 noted briefly.");
        var question = new FabricBenchmarkQuestion(
            "q-4", FabricQuestionKind.LocalFact, "What checksum was noted?", ["CK-777"], ["seg-small"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), Options(evidenceLimit: 200));
        var pack = runner.BuildEvidencePack(question, [big, small], null);

        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-small"));
    }

    [Test]
    public void BuildExhaustiveAnswer_Path_IsUnaffectedByEvidencePackChange()
    {
        // FabricQuestionKind.Exhaustive bypasses BuildEvidencePack entirely (AnswerQuestionAsync
        // routes it to BuildExhaustiveAnswer instead) -- this fix must not touch that behavior.
        // BuildEvidencePack itself should still happily rank/select for an Exhaustive-kind
        // question if ever called directly (defensive: no kind-specific branching remains).
        var card = Card("seg-a", "Reading noted.", "Reading MARK-001 logged here.");
        var question = new FabricBenchmarkQuestion(
            "q-5", FabricQuestionKind.Exhaustive, "What readings were logged?", ["MARK-001"], ["seg-a"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [card], null);

        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-a"));
    }
}
