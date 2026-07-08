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

    // ── Tier 1 anchor-phrase retrieval (CF_RETRIEVAL_IMPROVEMENT_PLAN.md) ────────────────────

    [Test]
    public void BuildEvidencePack_PrefersPhraseMatch_OverUnigramEntityCollision()
    {
        // The measured production failure mode: 63/105 cards contained both "station" and
        // "alpha" as separate words for a question about Station Alpha, tying on unigram score
        // with the 3 cards containing the contiguous phrase. Segment IDs are chosen so the OLD
        // ordinal tie-break would pick a collision card -- only phrase-aware ranking passes.
        var collision1 = Card("seg-aaa", "Routine checks.",
            "Station Cairn was inspected and no unusual activity was noted near Chapter Alpha.");
        var collision2 = Card("seg-bbb", "Routine checks.",
            "Station Kestrel closed its entry while Beacon Alpha reported scheduled maintenance.");
        var target = Card("seg-zzz", "Designation noted.",
            "The recorded designation for Station Alpha was BR-048.");
        var question = new FabricBenchmarkQuestion(
            "q-anchor-1", FabricQuestionKind.LocalFact,
            "What was the recorded designation for Station Alpha during this cycle?",
            ["BR-048"], ["seg-zzz"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [collision1, collision2, target], null);

        Assert.That(pack.IncludedSegmentIds[0], Is.EqualTo("seg-zzz"),
            "the card containing the literal phrase 'Station Alpha' must rank first");
    }

    [Test]
    public void BuildEvidencePack_PrefersVerbatimIdentifier_OverIdentifierFragments()
    {
        // The tokenizer splits "BR-048" into {br, 048}, so a card mentioning a DIFFERENT BR
        // identifier plus the number 048 in another role ties with the real match on unigrams.
        // Segment IDs chosen so the old tie-break picks the fragment card.
        var fragments = Card("seg-aaa", "Side note.",
            "Registry BR-121 logged the value 048 in a side note for the recorded designation file.");
        var target = Card("seg-zzz", "Designation noted.",
            "The recorded designation was BR-048 for this cycle.");
        var question = new FabricBenchmarkQuestion(
            "q-anchor-2", FabricQuestionKind.LocalFact,
            "What was recorded for designation BR-048?", ["BR-048"], ["seg-zzz"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [fragments, target], null);

        Assert.That(pack.IncludedSegmentIds[0], Is.EqualTo("seg-zzz"),
            "the card containing the verbatim identifier 'BR-048' must rank first");
    }

    [Test]
    public void BuildEvidencePack_CoversSecondEntity_BeforeStackingDuplicatesOfFirst()
    {
        // Coverage-aware fill: a MultiHop question naming two identifiers must include a card
        // for EACH before a near-duplicate of the higher-scoring one. IncludedSegmentIds is in
        // pick order, so the second pick must be the second entity's card even though the
        // duplicate of the first entity has a higher standalone score.
        var ck1 = Card("seg-ck1", "Checksum.", "Checksum CK-086 matched the shipment ledger record.");
        var ck2 = Card("seg-ck2", "Checksum.", "Checksum CK-086 was archived after the shipment closed.");
        var rpt = Card("seg-rpt", "Report.", "Report RPT-064 was filed covering the shipment.");
        var question = new FabricBenchmarkQuestion(
            "q-anchor-3", FabricQuestionKind.MultiHop,
            "How does report RPT-064 relate to checksum CK-086?", ["RPT-064", "CK-086"],
            ["seg-rpt", "seg-ck1"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [ck1, ck2, rpt], null);

        Assert.That(pack.IncludedSegmentIds.Take(2), Is.EquivalentTo(new[] { "seg-ck1", "seg-rpt" }),
            "the first two picks must cover both question entities, not duplicate one");
    }

    [Test]
    public void ExtractAnchorPhrases_FindsIdentifiersAndProperNounRuns_TrimmingQuestionOpeners()
    {
        var anchors = ContextFabricFeasibilityRunner.ExtractAnchorPhrases(
            "Which Depot Fathom entries reference case-ledger-01 or CHN-112-0-1-2?");

        Assert.Multiple(() =>
        {
            Assert.That(anchors, Does.Contain("Depot Fathom"),
                "the question opener 'Which' must be trimmed, not swallow the entity run");
            Assert.That(anchors, Does.Contain("case-ledger-01"));
            Assert.That(anchors, Does.Contain("CHN-112-0-1-2"));
            Assert.That(anchors, Does.Not.Contain("Which Depot Fathom"));
        });
    }

    // ── Tier 1.5 proximity pairs (CF_RETRIEVAL_IMPROVEMENT_PLAN.md) ──────────────────────────

    [Test]
    public void ExtractProximityPairs_PairsMidSentenceCapitalizedWord_WithNearestNeighbor()
    {
        // The measured Tier-1 residual failure shape: only "Meridian" is capitalized, so no
        // proper-noun run exists; the pair (meridian, relay) is the recoverable signal.
        var pairs = ContextFabricFeasibilityRunner.ExtractProximityPairs(
            "What code was assigned to the Meridian relay point in the current interval?");

        Assert.Multiple(() =>
        {
            Assert.That(pairs, Does.Contain(("meridian", "relay")));
            Assert.That(pairs.Select(p => p.A).Concat(pairs.Select(p => p.B)), Does.Not.Contain("what"),
                "sentence-initial question words must not become pair members");
        });
    }

    [Test]
    public void BuildEvidencePack_MatchesInvertedEntityWordOrder_ViaProximityPair()
    {
        // Question says "the Meridian relay point"; the corpus entity is "Relay Meridian".
        // The distractor contains both words far apart (and shares every question unigram the
        // target does), with segment IDs chosen so the old ordering would pick it first.
        // Distractor shares MORE question unigrams than the target (code, assigned, relay,
        // current, meridian, interval vs the target's five) so plain unigram IDF provably
        // prefers it -- only the proximity pair can rescue the target.
        var distractor = Card("seg-aaa", "Routine.",
            "The relay tower code was assigned during the current maintenance and the weather near Ledger Meridian was unremarkable for the interval.");
        var target = Card("seg-zzz", "Designation.",
            "The recorded code for Relay Meridian in the current interval was BR-868.");
        var question = new FabricBenchmarkQuestion(
            "q-pair-1", FabricQuestionKind.Paraphrased,
            "What code was assigned to the Meridian relay point in the current interval?",
            ["BR-868"], ["seg-zzz"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [distractor, target], null);

        Assert.That(pack.IncludedSegmentIds[0], Is.EqualTo("seg-zzz"),
            "the card where 'relay' and 'meridian' are adjacent must rank first");
    }

    [Test]
    public void BuildEvidencePack_VerbatimPhrase_OutranksProximityOnlyMatch()
    {
        // Both cards match the pair (relay, meridian); only one contains the contiguous phrase
        // the question uses. Verbatim must win (proximity pairs carry half weight).
        var proximityOnly = Card("seg-aaa", "Inverted.",
            "The Meridian relay checkpoint recorded designation BR-111 for the interval.");
        var verbatim = Card("seg-zzz", "Exact.",
            "The recorded designation for Relay Meridian was BR-868 in this interval.");
        var question = new FabricBenchmarkQuestion(
            "q-pair-2", FabricQuestionKind.LocalFact,
            "What designation was recorded for Relay Meridian in the interval?",
            ["BR-868"], ["seg-zzz"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [proximityOnly, verbatim], null);

        Assert.That(pack.IncludedSegmentIds[0], Is.EqualTo("seg-zzz"));
    }

    [Test]
    public void BuildEvidencePack_WithNoExtractableAnchors_StillRanksByUnigramIdf()
    {
        // Questions with no identifiers or proper-noun runs must degrade exactly to the prior
        // unigram-IDF behavior (anchor keys all zero).
        var target = Card("seg-target", "Checksum recorded.",
            "The archive recorded the checksum for this unusual shipment yesterday.");
        var distractor = Card("seg-distractor", "General narration.",
            "The depot log was reviewed and the records were unchanged.");
        var question = new FabricBenchmarkQuestion(
            "q-anchor-4", FabricQuestionKind.LocalFact,
            "What checksum was recorded for the unusual shipment?", ["checksum"], ["seg-target"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [target, distractor], null);

        Assert.That(pack.IncludedSegmentIds[0], Is.EqualTo("seg-target"));
    }

    // ── Tier 2.5: reference chasing ───────────────────────────────────────────────────────────

    [Test]
    public void BuildEvidencePack_ChasesTrackedIdentifier_IntoLinkedSegmentTheQuestionNeverNames()
    {
        // The question names only "Outpost Alpha". The custody segment mentions RPT-064; the
        // checksum segment (which the verifier requires) shares that identifier but matches no
        // question anchor or term. The chase must follow RPT-064 into it.
        var custody = Card("seg-custody", "Custody transfer.",
            "Outpost Alpha passed custody of submission RPT-064 to the bureau.");
        var checksum = Card("seg-checksum", "Checksum confirmation.",
            "Bureau confirmed RPT-064 matched checksum CK-086.");
        var unrelated = Card("seg-noise", "Weather notes.",
            "Routine weather observation logged, skies clear, winds calm.");
        var question = new FabricBenchmarkQuestion(
            "q-chase-1", FabricQuestionKind.MultiHop,
            "What matching checksum was recorded once Outpost Alpha had passed custody of its submission?",
            ["RPT-064", "CK-086"], ["seg-custody", "seg-checksum"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [custody, checksum, unrelated], null);

        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-custody"));
        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-checksum"));
    }

    [Test]
    public void BuildEvidencePack_ChasesMultiLinkChain_AcrossSuccessiveHops()
    {
        // 3-hop chain: question names only the first station; each hop shares one identifier
        // with the next. Every hop must be walked (rounds > 1).
        var hop1 = Card("seg-hop1", "Dispatch.", "Station Vantage dispatched parcel RPT-100 downstream.");
        var hop2 = Card("seg-hop2", "Relay.", "Relay received RPT-100 and forwarded it as RPT-200.");
        var hop3 = Card("seg-hop3", "Receipt.", "Terminal logged RPT-200 with final checksum CK-300.");
        var question = new FabricBenchmarkQuestion(
            "q-chase-2", FabricQuestionKind.MultiHop,
            "What final checksum did the parcel dispatched by Station Vantage receive?",
            ["CK-300"], ["seg-hop1", "seg-hop2", "seg-hop3"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [hop1, hop2, hop3], null);

        Assert.That(pack.IncludedSegmentIds, Is.EquivalentTo(new[] { "seg-hop1", "seg-hop2", "seg-hop3" }));
    }

    [Test]
    public void BuildEvidencePack_DoesNotChaseHighFrequencyFillerIdentifiers()
    {
        // An identifier appearing in many cards is corpus filler, not a chain link: chasing it
        // would flood the pack. Doc frequency above the cap must stop the chase.
        var seed = Card("seg-seed", "Seed.", "Station Harrow logged the shared code FILL-01 today.");
        var fillers = Enumerable.Range(1, 6)
            .Select(i => Card($"seg-fill-{i}", $"Filler {i}.", $"Routine note {i} also carries FILL-01 in passing."))
            .ToArray();
        var question = new FabricBenchmarkQuestion(
            "q-chase-3", FabricQuestionKind.LocalFact,
            "What did Station Harrow log today?", ["FILL-01"], ["seg-seed"]);

        var runner = new ContextFabricFeasibilityRunner(new ScriptedFabricRuntime(), FabricRunOptions.Default);
        var pack = runner.BuildEvidencePack(question, [seed, .. fillers], null);

        Assert.That(pack.IncludedSegmentIds, Does.Contain("seg-seed"));
        // Filler cards may enter via normal term scoring, but the chase must not add all of
        // them: the identifier occurs in 7 cards, above the cap of 4.
        Assert.That(pack.IncludedSegmentIds.Count, Is.LessThan(7));
    }
}
