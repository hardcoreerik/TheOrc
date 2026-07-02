// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Hive;

namespace Cf6AcceptanceRunner;

/// <summary>
/// CF-6 multi-node distribution + exhaustive-query-answer harness: stages the deterministic
/// synthetic-book corpus on a live Warchief, dispatches the full CF-6 pipeline (readers ->
/// verifiers -> stitchers -> reducer -> exhaustive query) across whatever real worker nodes are
/// enrolled in the HIVE, and records which WorkerId claimed each reader unit -- that's the actual
/// evidence for the 2-node/3-node distribution requirement; a single-process run would show every
/// reader claimed by the same id. It also validates the exhaustive-query answers against the
/// benchmark's expected terms/segments/abstention, the citation-verifier's per-claim verdicts
/// against an independently recomputed ground truth, the boundary-stitcher's output against
/// DeterministicFabricCorpus.CreateBoundaryStitchFixture()'s expected facts/forbidden terms, and
/// the reducer's coverage of every segment's and reader card's claims. The Ollama-absence proof
/// is a separate manual procedure this tool supports as a harness for, not something it runs
/// itself. Worker-death recovery IS runnable here via <c>--death-test</c> (see
/// <see cref="RunDeathTestAsync"/>): it submits a single reader unit, suspends the claiming
/// worker's process mid-execution through an operator-supplied fault script, and asserts the
/// CheckTimeouts watchdog re-queues the unit to a different node, the stale claim token is
/// rejected with 409, and the unit is accepted exactly once. Run this directly against the
/// Warchief's own loopback (no HMAC needed locally); the remote workers reached over the
/// LAN/Tailscale are the ones that supply the second/third node.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        var warchief = GetArg(args, "--warchief") ?? "http://localhost:7079";
        var outDir = GetArg(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, ".orc", "cf6-acceptance");
        var modelHash = GetArg(args, "--model-hash") ?? "";
        var minNodes = int.TryParse(GetArg(args, "--min-nodes"), out var mn) ? mn : 1;
        var resumeFrom = GetArg(args, "--resume-from");
        var deathTest = Array.IndexOf(args, "--death-test") >= 0;
        Directory.CreateDirectory(outDir);
        if (deathTest)
        {
            using var deathHttp = new HttpClient { BaseAddress = new Uri(warchief), Timeout = TimeSpan.FromMinutes(10) };
            var faultScript = GetArg(args, "--fault-script")
                ?? throw new InvalidOperationException(
                    "--death-test requires --fault-script <path to a script taking -Action suspend|resume -Worker <id>>.");
            return await RunDeathTestAsync(deathHttp, warchief, outDir, modelHash,
                GetArg(args, "--warchief-db"), faultScript);
        }
        if (minNodes <= 1)
            Console.WriteLine(
                "NOTE: --min-nodes not set above 1 -- this run can PASS without proving multi-node " +
                "distribution. Pass --min-nodes 2 or --min-nodes 3 for an actual CF-6 exit-gate run; " +
                "this invocation only smoke-tests the pipeline.");

        using var http = new HttpClient { BaseAddress = new Uri(warchief), Timeout = TimeSpan.FromMinutes(10) };

        var report = new AcceptanceReport { Warchief = warchief, StartedAt = DateTimeOffset.UtcNow };
        try
        {
            Console.WriteLine($"CF-6 acceptance run against {warchief}");
            var fixture = DeterministicFabricCorpus.Create();
            var corpus = fixture.Corpus;
            Console.WriteLine($"Corpus: {corpus.CorpusId} ({corpus.Segments.Count} segments, generation {corpus.GenerationId})");

            // ── Stage 0: upload artifacts ───────────────────────────────────────
            var segmentRefs = new List<ArtifactRef>(corpus.Segments.Count);
            foreach (var segment in corpus.Segments.OrderBy(s => s.Ordinal))
            {
                var single = corpus with { Segments = [segment], EstimatedSourceTokens = segment.EstimatedTokens };
                var bytes = Encoding.UTF8.GetBytes(FabricJson.Serialize(single));
                segmentRefs.Add(await StageArtifactAsync(http, bytes, $"{segment.SegmentId}.corpus.json", "input"));
            }

            // FetchReducerInputsAsync hard-rejects a non-empty Segments list (see
            // CampaignTemplates.StageReducerCorpusMetaAsync) -- per-segment identity for the
            // reducer comes from the evidence cards, not corpus-meta.
            var strippedMeta = corpus with { Segments = [], EstimatedSourceTokens = 0 };
            var corpusMetaRef = await StageArtifactAsync(
                http, Encoding.UTF8.GetBytes(FabricJson.Serialize(strippedMeta)), "corpus-meta.json", "input");

            Console.WriteLine($"Staged {segmentRefs.Count} segment artifacts + corpus-meta.");
            report.GateMode = minNodes > 1 ? "acceptance" : "smoke";
            report.CorpusGenerationId = corpus.GenerationId;
            report.ModelHash = modelHash;

            // Query work units are built per-segment in corpus order (see queryUnits below) --
            // this mirrors that same ordering so a question's ExpectedSegmentIds can be mapped to
            // the exact work unit that should (and the ones that should NOT) report Relevant=true.
            var segmentIdToQueryUnitId = corpus.Segments.OrderBy(s => s.Ordinal)
                .Select((s, index) => (s.SegmentId, WorkUnitId: $"query-{index + 1:00000}"))
                .ToDictionary(t => t.SegmentId, t => t.WorkUnitId);
            var queryUnitIdToSegmentId = segmentIdToQueryUnitId.ToDictionary(kv => kv.Value, kv => kv.Key);

            var readerCardRefs = new List<ArtifactRef>(segmentRefs.Count);
            var readerWorkUnitIds = new List<string>(segmentRefs.Count);
            var readerCards = new List<FabricEvidenceCard>(segmentRefs.Count);
            StageEvidence? readerStage = null;

            if (resumeFrom is not null)
            {
                // ── Resume mode: skip pipeline stages 1-5, replay evidence from a previous run ──
                // Re-upload the evidence cards from the prior run as artifacts so workers can fetch
                // them; everything else (verifiers, stitchers, reducer validation) is copied verbatim
                // from the previous report so the overall Passed gate still includes them.
                Console.WriteLine($"Resume mode: loading evidence cards from {resumeFrom}");
                var prevJson = await File.ReadAllTextAsync(resumeFrom);
                var prev = JsonSerializer.Deserialize<AcceptanceReport>(prevJson, new JsonSerializerOptions(JsonOptions) { PropertyNameCaseInsensitive = true })
                    ?? throw new InvalidOperationException("Failed to parse previous acceptance report.");

                // Fail closed on stale/mismatched evidence: the replayed readers/verifiers/
                // stitchers/reducer results are only meaningful for the SAME corpus generation and
                // model they were produced against. Refuse rather than let a resume attribute old
                // evidence to a corpus/model it didn't come from (Codex review BLOCKER, 2026-06-30).
                // An empty prev.CorpusGenerationId means the prior report predates this field — too
                // old to trust for replay, so it's rejected the same as an outright mismatch.
                if (!string.Equals(prev.CorpusGenerationId, corpus.GenerationId, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"--resume-from corpus generation '{prev.CorpusGenerationId}' does not match the current corpus " +
                        $"'{corpus.GenerationId}'. Replaying evidence across corpus generations is refused; run a fresh acceptance pass.");
                if (!string.Equals(prev.ModelHash, modelHash, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"--resume-from model-hash '{prev.ModelHash}' does not match --model-hash '{modelHash}'. " +
                        "Replaying evidence produced by a different model is refused; run a fresh acceptance pass.");

                var prevReaderStage = prev.Stages.FirstOrDefault(s => s.Stage == "readers")
                    ?? throw new InvalidOperationException("Previous report has no 'readers' stage.");
                var orderedSegments = corpus.Segments.OrderBy(s => s.Ordinal).ToList();
                for (var i = 0; i < orderedSegments.Count; i++)
                {
                    var seg = orderedSegments[i];
                    var unitId = $"read-{i + 1:00000}";
                    var prevUnit = prevReaderStage.Units.FirstOrDefault(u => u.WorkUnitId == unitId)
                        ?? throw new InvalidOperationException($"Previous report missing reader unit '{unitId}'.");
                    if (string.IsNullOrWhiteSpace(prevUnit.Result))
                        throw new InvalidOperationException($"Previous reader unit '{unitId}' has no result body.");
                    var card = JsonSerializer.Deserialize<FabricEvidenceCard>(prevUnit.Result, FabricJson.Options)
                        ?? throw new InvalidOperationException($"Previous reader unit '{unitId}' result did not parse as FabricEvidenceCard.");
                    var cardBytes = Encoding.UTF8.GetBytes(prevUnit.Result);
                    var cardRef = await StageArtifactAsync(http, cardBytes, $"{seg.SegmentId}.evidence-card.json", "output");
                    readerCardRefs.Add(cardRef);
                    readerWorkUnitIds.Add(unitId);
                    readerCards.Add(card);
                }
                // Copy pipeline validation results verbatim from the previous report (only the
                // replayed stages -- query stages are always run fresh below).
                report.Stages.AddRange(prev.Stages.Where(s =>
                    s.Stage is "readers" or "verifiers" or "stitchers" or "stitch-fixture" or "reducer"));
                report.Verifiers.AddRange(prev.Verifiers);
                report.StitchCases.AddRange(prev.StitchCases);
                report.ReducerSegmentsCovered = prev.ReducerSegmentsCovered;
                report.ReducerClaimsCovered = prev.ReducerClaimsCovered;
                report.ReducerValidated = prev.ReducerValidated;
                // Reader node proof carries over from the previous run -- REPLAYED, not freshly
                // demonstrated, so this run is stamped Resumed and cannot count as a clean
                // multi-node acceptance pass (surfaced in GateMode + the final verdict banner).
                var prevReaderWorkers = prevReaderStage.Units
                    .Select(u => u.ClaimedBy).Where(w => !string.IsNullOrWhiteSpace(w))
                    .Select(w => w!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                report.DistinctWorkerIds = prevReaderWorkers;
                report.NodeCount = prevReaderWorkers.Count;
                report.ReaderWorkerIds = prevReaderWorkers;
                report.ReaderNodeCount = prevReaderWorkers.Count;
                report.Resumed = true;
                report.GateMode = "resumed-query-only";
                Console.WriteLine($"Resumed: {readerCards.Count} evidence cards re-staged (reader fan-out proof REPLAYED, " +
                                  "not fresh). Skipping directly to query campaigns.");
            }
            else
            {
                // ── Stage 1: readers (one work unit per segment -- the fan-out that proves >1 node) ──
                var readersCampaign = CampaignTemplates.ContextFabricReaders(
                    "cf6-acceptance-readers", segmentRefs, modelHash);
                readerStage = await RunCampaignAsync(http, readersCampaign, report, "readers");

                foreach (var unit in readersCampaign.WorkUnits)
                {
                    var status = readerStage.Units.First(u => u.WorkUnitId == unit.WorkUnitId);
                    if (status.Status != "completed" || status.OutputArtifacts.Count == 0)
                        throw new InvalidOperationException(
                            $"Reader unit '{unit.WorkUnitId}' did not complete with an output artifact (status={status.Status}).");
                    readerCardRefs.Add(status.OutputArtifacts[0]);
                    readerWorkUnitIds.Add(unit.WorkUnitId);
                    if (string.IsNullOrWhiteSpace(status.Result))
                        throw new InvalidOperationException($"Reader unit '{unit.WorkUnitId}' completed without a result body.");
                    readerCards.Add(JsonSerializer.Deserialize<FabricEvidenceCard>(status.Result, FabricJson.Options)
                        ?? throw new InvalidOperationException($"Reader unit '{unit.WorkUnitId}' result did not parse as a FabricEvidenceCard."));
                }
            }

            if (resumeFrom is null)
            {
            // ── Stage 2: verifiers (CPU-only citation check, one per reader output) ──
            var verifierItems = readerCardRefs
                .Zip(segmentRefs, readerWorkUnitIds)
                .Select(t => (t.First, t.Second, t.Third))
                .ToList();
            var verifiersCampaign = StripDependsOn(CampaignTemplates.ContextFabricVerifiers(
                "cf6-acceptance-verifiers", verifierItems, modelHash));
            var verifierStage = await RunCampaignAsync(http, verifiersCampaign, report, "verifiers");

            // Validate each verifier's reported verdict against an independently recomputed ground
            // truth -- the verifier checks citation quote/offset/digest against source text, which is
            // a pure deterministic computation, so a "completed" unit that disagrees with the same
            // computation run here means the worker fabricated or corrupted its verdict.
            var orderedSegments = corpus.Segments.OrderBy(s => s.Ordinal).ToList();
            for (var i = 0; i < verifierStage.Units.Count; i++)
            {
                var unit = verifierStage.Units[i];
                var card = readerCards[i];
                var segment = orderedSegments[i];
                var recomputedItems = card.Claims.Select(claim =>
                {
                    var errors = RecomputeCitationErrors(claim, segment.Text);
                    return new FabricHiveVerificationItem(claim.ClaimId ?? "", segment.SegmentId, errors.Count == 0, errors);
                }).ToList();
                var recomputedAllPassed = recomputedItems.All(it => it.Passed);

                FabricHiveVerificationReport? reported = null;
                var parseOk = unit.Status == "completed" && !string.IsNullOrWhiteSpace(unit.Result);
                if (parseOk)
                {
                    try { reported = JsonSerializer.Deserialize<FabricHiveVerificationReport>(unit.Result!, FabricJson.Options); }
                    catch (JsonException) { parseOk = false; }
                }
                var itemsMatch = parseOk && reported is not null && reported.Items.Count == recomputedItems.Count &&
                    reported.Items.OrderBy(it => it.ClaimId, StringComparer.Ordinal)
                        .Zip(recomputedItems.OrderBy(it => it.ClaimId, StringComparer.Ordinal))
                        .All(p => string.Equals(p.First.ClaimId, p.Second.ClaimId, StringComparison.Ordinal)
                            && p.First.Passed == p.Second.Passed);
                var validated = parseOk && reported is not null && reported.AllPassed == recomputedAllPassed && itemsMatch;

                report.Verifiers.Add(new VerifierEvidence
                {
                    WorkUnitId = unit.WorkUnitId,
                    SegmentId = segment.SegmentId,
                    ReportedAllPassed = reported?.AllPassed ?? false,
                    RecomputedAllPassed = recomputedAllPassed,
                    Validated = validated,
                });
            }

            // ── Stage 3: stitchers (adjacent-segment boundary resolution) ──
            var pairs = new List<(ArtifactRef LeftCorpus, ArtifactRef RightCorpus, string LeftReaderId, string RightReaderId)>();
            for (var i = 0; i < segmentRefs.Count - 1; i++)
                pairs.Add((segmentRefs[i], segmentRefs[i + 1], readerWorkUnitIds[i], readerWorkUnitIds[i + 1]));
            var stitchersCampaign = StripDependsOn(CampaignTemplates.ContextFabricStitchers(
                "cf6-acceptance-stitchers", pairs, modelHash));
            await RunCampaignAsync(http, stitchersCampaign, report, "stitchers");

            // ── Stage 3b: boundary-stitch fixture validation ──
            // The stitchers stage above runs over real adjacent corpus segments, which have no known
            // expected summary/facts -- ExecuteContextFabricStitcherAsync builds its test case with
            // empty expectations for those, so its Passed flag is vacuously true. To actually check
            // semantic correctness we run the deterministic boundary-stitch fixture's own cases
            // (known LeftText/RightText with known ExpectedSummary/ExpectedLinkedFacts/ForbiddenTerms)
            // through the same stitcher work-unit shape and grade the result here.
            var stitchFixture = DeterministicFabricCorpus.CreateBoundaryStitchFixture();
            var stitchFixturePairs = new List<(ArtifactRef LeftCorpus, ArtifactRef RightCorpus, string LeftReaderId, string RightReaderId)>();
            foreach (var stitchCase in stitchFixture.Cases)
            {
                var leftRef = await StageArtifactAsync(
                    http,
                    Encoding.UTF8.GetBytes(FabricJson.Serialize(BuildSingleSegmentCorpus(stitchCase.CaseId, "left", stitchCase.LeftText))),
                    $"{stitchCase.CaseId}-left.corpus.json", "input");
                var rightRef = await StageArtifactAsync(
                    http,
                    Encoding.UTF8.GetBytes(FabricJson.Serialize(BuildSingleSegmentCorpus(stitchCase.CaseId, "right", stitchCase.RightText))),
                    $"{stitchCase.CaseId}-right.corpus.json", "input");
                stitchFixturePairs.Add((leftRef, rightRef, "", ""));
            }
            var stitchFixtureCampaign = StripDependsOn(CampaignTemplates.ContextFabricStitchers(
                "cf6-acceptance-stitch-fixture", stitchFixturePairs, modelHash));
            var stitchFixtureStage = await RunCampaignAsync(http, stitchFixtureCampaign, report, "stitch-fixture");

            for (var i = 0; i < stitchFixture.Cases.Count; i++)
            {
                var stitchCase = stitchFixture.Cases[i];
                var unit = stitchFixtureStage.Units[i];
                StitchUnitOutput? output = null;
                var parseOk = unit.Status == "completed" && !string.IsNullOrWhiteSpace(unit.Result);
                if (parseOk)
                {
                    try { output = JsonSerializer.Deserialize<StitchUnitOutput>(unit.Result!, FabricJson.Options); }
                    catch (JsonException) { parseOk = false; }
                }
                // Exact substring/equality against ExpectedSummary/ExpectedLinkedFacts rejects any
                // valid paraphrase ("resulting in" vs "which resulted in") even when the content is
                // fully correct -- observed against a real model. Compare on key-fact coverage
                // instead: every "anchor" word (>=5 chars, so function words like "the"/"was" don't
                // count) from the expected text must appear somewhere in the actual output. This
                // still catches genuine content loss (a missing fact's distinctive nouns/numbers
                // won't appear) without penalizing legitimate rewording.
                var combinedText = parseOk && output is not null
                    ? output.Summary + " " + string.Join(" ", output.LinkedFacts)
                    : "";
                var summaryPreserved = parseOk && AnchorWordsCovered(stitchCase.ExpectedSummary, combinedText);
                var linkedFactsCovered = parseOk && stitchCase.ExpectedLinkedFacts.All(fact =>
                    AnchorWordsCovered(fact, combinedText));
                var forbiddenAbsent = parseOk && stitchCase.ForbiddenTerms.All(term =>
                    !combinedText.Contains(term, StringComparison.OrdinalIgnoreCase));
                var stitchValidated = parseOk && summaryPreserved && linkedFactsCovered && forbiddenAbsent;

                report.StitchCases.Add(new StitchCaseEvidence
                {
                    CaseId = stitchCase.CaseId,
                    WorkUnitId = unit.WorkUnitId,
                    SummaryPreserved = summaryPreserved,
                    LinkedFactsCovered = linkedFactsCovered,
                    ForbiddenTermsAbsent = forbiddenAbsent,
                    Validated = stitchValidated,
                });
            }

            // ── Stage 4: reducer (fan-in over corpus-meta + every reader card) ──
            var reducerCampaign = StripDependsOn(CampaignTemplates.ContextFabricReducer(
                "cf6-acceptance-reducer", corpusMetaRef, readerCardRefs, readerWorkUnitIds, modelHash));
            var reducerStage = await RunCampaignAsync(http, reducerCampaign, report, "reducer");

            // Validate the reduction actually fans in every segment and every reader-emitted claim --
            // a "completed" reducer unit that silently dropped a segment or a claim must not pass.
            var reducerUnit = reducerStage.Units.Single();
            ReducerUnitOutput? reducerOutput = null;
            var reducerParseOk = reducerUnit.Status == "completed" && !string.IsNullOrWhiteSpace(reducerUnit.Result);
            if (reducerParseOk)
            {
                try { reducerOutput = JsonSerializer.Deserialize<ReducerUnitOutput>(reducerUnit.Result!, FabricJson.Options); }
                catch (JsonException) { reducerParseOk = false; }
            }
            var allSegmentIds = corpus.Segments.Select(s => s.SegmentId).ToHashSet(StringComparer.Ordinal);
            var allClaimIds = readerCards.SelectMany(c => c.Claims).Select(c => c.ClaimId)
                .Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.Ordinal);
            var coveredSegmentIds = reducerParseOk && reducerOutput is not null
                ? reducerOutput.Nodes.SelectMany(n => n.CoveredSegmentIds).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            var coveredClaimIds = reducerParseOk && reducerOutput is not null
                ? reducerOutput.Nodes.SelectMany(n => n.ClaimIds).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            report.ReducerSegmentsCovered = reducerParseOk && allSegmentIds.IsSubsetOf(coveredSegmentIds);
            report.ReducerClaimsCovered = reducerParseOk && allClaimIds.IsSubsetOf(coveredClaimIds);
            report.ReducerValidated = report.ReducerSegmentsCovered && report.ReducerClaimsCovered;
            } // end if (resumeFrom is null) — stages 2-4

            // ── Stage 5: exhaustive query fan-out, one benchmark question at a time ──
            // Mirrors CampaignTemplates.ContextFabricExhaustiveQueryAsync's shape, but stages the
            // question artifact via the remote /hive/artifacts upload (StageArtifactAsync) instead
            // of a local ContentAddressedStore -- workers fetch from the Warchief over HTTP, so the
            // artifact has to live there regardless of where this runner executes.
            foreach (var question in fixture.Questions)
            {
                var questionRef = await StageArtifactAsync(
                    http,
                    Encoding.UTF8.GetBytes(FabricJson.Serialize(new FabricQueryQuestion(question.QuestionId, question.Question))),
                    $"question-{question.QuestionId}.json", "input");
                var queryUnits = segmentRefs.Select((segCorpus, index) => new WorkUnit
                {
                    WorkUnitId = $"query-{index + 1:00000}",
                    Title = $"Context Fabric exhaustive query: {segCorpus.Name}",
                    Role = "Researcher",
                    NativeRole = CampaignPackCatalog.ContextFabricQueryRole,
                    ExecutionKind = HiveExecutionKinds.NativeAgent,
                    PackId = CampaignPackCatalog.ContextFabricPackId,
                    PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
                    Requirements = new ResourceRequirements
                    {
                        NativeModelHash = modelHash,
                        RequiredPacks = [$"{CampaignPackCatalog.ContextFabricPackId}@{CampaignPackCatalog.ContextFabricPackVersion}"],
                    },
                    Verification = new VerificationPolicy { Mode = "independent_consensus", RequiredIndependentRuns = 1 },
                    Inputs = [questionRef, segCorpus, readerCardRefs[index]],
                    TimeoutMs = 1_800_000,
                }).ToList();
                var queryCampaign = new CampaignDefinition
                {
                    Name = $"cf6-acceptance-query-{question.QuestionId}",
                    PackId = CampaignPackCatalog.ContextFabricPackId,
                    PackVersion = CampaignPackCatalog.ContextFabricPackVersion,
                    WorkUnits = queryUnits,
                };
                var stage = await RunCampaignAsync(http, queryCampaign, report, $"query:{question.QuestionId}");

                // Validate the actual answer content, not just that every per-segment query task
                // finished -- a task that "completed" with the wrong finding (or a false positive
                // on the unanswerable question, or a hit on the wrong segment) must not count as a
                // pass. Every ExpectedSegmentIds entry must come back Relevant; every OTHER segment
                // must NOT claim Relevant (catches a hallucinated/misgrounded answer); and the
                // combined text of the correctly-grounded findings must contain every expected term.
                var findingByUnit = new Dictionary<string, FabricQueryFinding?>();
                var contractBroken = false;
                foreach (var unit in stage.Units)
                {
                    if (unit.Status != "completed")
                        continue;
                    if (string.IsNullOrWhiteSpace(unit.Result))
                    {
                        // A "completed" unit with no result body is just as contract-broken as one
                        // that fails to parse -- both must not be indistinguishable from "no finding".
                        contractBroken = true;
                        continue;
                    }
                    try
                    {
                        findingByUnit[unit.WorkUnitId] =
                            JsonSerializer.Deserialize<FabricQueryFinding>(unit.Result, FabricJson.Options);
                    }
                    catch (JsonException)
                    {
                        // A "completed" task that didn't actually return the FabricQueryFinding
                        // contract is a real failure, not "no finding" -- on the abstention question
                        // those two look identical unless tracked separately, which would let a
                        // worker that returned garbage still pass as a correct abstention.
                        contractBroken = true;
                    }
                }
                var expectedUnitIds = question.ExpectedSegmentIds
                    .Select(id => segmentIdToQueryUnitId[id]).ToHashSet(StringComparer.Ordinal);
                // A finding is only trustworthy if it also self-identifies the segment it claims to
                // be about -- otherwise a worker returning Relevant=true with the WRONG SegmentId
                // would still satisfy plain unit-id-keyed coverage above.
                bool MatchesUnit(string unitId, FabricQueryFinding? f) =>
                    f is { Relevant: true } &&
                    string.Equals(f.SegmentId, queryUnitIdToSegmentId[unitId], StringComparison.Ordinal) &&
                    string.Equals(f.QuestionId, question.QuestionId, StringComparison.Ordinal);
                var anyRelevant = findingByUnit.Any(kv => kv.Value is { Relevant: true });
                var expectedCovered = expectedUnitIds.All(id =>
                    findingByUnit.TryGetValue(id, out var f) && MatchesUnit(id, f));
                var noFalsePositives = findingByUnit
                    .Where(kv => !expectedUnitIds.Contains(kv.Key))
                    .All(kv => !MatchesUnit(kv.Key, kv.Value));
                var combinedText = string.Join(" ", expectedUnitIds
                    .Select(id => findingByUnit.GetValueOrDefault(id))
                    .Where(f => f is not null)
                    .Select(f => f!.FindingText ?? string.Join(" ", f.Claims.Select(c => c.Text))));
                var termsMatched = question.ExpectedTerms.All(term =>
                    combinedText.Contains(term, StringComparison.OrdinalIgnoreCase));
                var answersValidated = !contractBroken && (question.ExpectAbstention
                    ? !anyRelevant
                    : expectedCovered && noFalsePositives && termsMatched);

                report.Questions.Add(new QuestionEvidence
                {
                    QuestionId = question.QuestionId,
                    Kind = question.Kind.ToString(),
                    ExpectedTerms = question.ExpectedTerms.ToList(),
                    ExpectAbstention = question.ExpectAbstention,
                    PerSegmentFindings = stage.Units.ToDictionary(u => u.WorkUnitId, u => u.Status),
                    AnswerValidated = answersValidated,
                });
            }

            // ── Distinct-node proof ──
            // Gated specifically on the READER stage's distinct claimants: that's the actual
            // per-segment fan-out CF-6's exit gate is about. A second node only ever claiming a
            // cheap verifier/query unit while every reader ran on one box must not count as having
            // proven multi-node distribution. In resume mode these were already populated from
            // the prior run's reader stage in the resume block above.
            if (resumeFrom is null)
            {
                var distinctWorkers = report.Stages.SelectMany(s => s.Units).Select(u => u.ClaimedBy)
                    .Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var readerWorkers = readerStage!.Units.Select(u => u.ClaimedBy)
                    .Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                report.DistinctWorkerIds = distinctWorkers;
                report.NodeCount = distinctWorkers.Count;
                report.ReaderWorkerIds = readerWorkers;
                report.ReaderNodeCount = readerWorkers.Count;
            }
            report.MinNodesRequired = minNodes;
            report.FinishedAt = DateTimeOffset.UtcNow;
            var allChecksValidated = report.Stages.All(s => s.Units.All(u => u.Status == "completed"))
                && report.ReaderNodeCount >= minNodes
                && report.Questions.All(q => q.AnswerValidated)
                && report.Verifiers.All(v => v.Validated)
                && report.StitchCases.All(c => c.Validated)
                && report.ReducerValidated;
            // Passed == a CLEAN acceptance-gate pass. A resumed run replays the reader fan-out
            // proof, so it can NEVER be that, regardless of the checks — and it must not exit 0,
            // or CI/operators would treat a query-only replay as a real CF-6 pass despite the
            // banner (Codex review BLOCKER, 2026-06-30). Resumed runs report a dedicated exit
            // code (3) distinct from both a clean pass (0) and a genuine gate failure (2).
            report.Passed = allChecksValidated && !report.Resumed;

            var outPath = Path.Combine(outDir, $"cf6-acceptance-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"Distinct worker nodes that claimed at least one unit: {report.NodeCount} ({string.Join(", ", report.DistinctWorkerIds)})");
            Console.WriteLine($"Distinct worker nodes that claimed a READER unit (the fan-out proof): {report.ReaderNodeCount} ({string.Join(", ", report.ReaderWorkerIds)})");
            if (report.Resumed)
            {
                var queriesOk = report.Questions.All(q => q.AnswerValidated);
                Console.WriteLine("⚠ RESUMED RUN — readers/verifiers/stitchers/reducer and the reader fan-out proof were " +
                                  "REPLAYED from a prior report; this validates query logic only and is NOT a clean CF-6 " +
                                  "multi-node acceptance pass. Run without --resume-from for a gate-eligible result.");
                Console.WriteLine($"Query-logic result (resumed): {(queriesOk ? "all questions validated" : "one or more questions FAILED")}.");
                Console.WriteLine("Verdict: RESUMED (query-logic only — not gate-eligible, exit 3)");
                Console.WriteLine($"Evidence written: {outPath}");
                return 3;
            }
            Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"Evidence written: {outPath}");
            return report.Passed ? 0 : 2;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.FinishedAt = DateTimeOffset.UtcNow;
            var outPath = Path.Combine(outDir, $"cf6-acceptance-FAILED-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            Console.Error.WriteLine($"Run failed: {ex.Message}");
            Console.Error.WriteLine($"Partial evidence written: {outPath}");
            return 1;
        }
    }

    /// <summary>Reader/verifier/stitcher/reducer templates set DependsOn against sibling
    /// WorkUnitIds in the SAME CampaignDefinition (CF-6's normal single-campaign fan-out/fan-in
    /// shape). This runner submits each stage as its own campaign only after the previous stage
    /// is already known-complete, so the dependency is satisfied by submission order, not by the
    /// barrier -- and the barrier would otherwise reject these units (their DependsOn ids belong
    /// to an earlier, separate campaign, not this one).</summary>
    private static CampaignDefinition StripDependsOn(CampaignDefinition campaign) =>
        campaign with { WorkUnits = campaign.WorkUnits.Select(u => u with { DependsOn = [] }).ToList() };

    private static async Task<ArtifactRef> StageArtifactAsync(HttpClient http, byte[] bytes, string name, string kind)
    {
        var digest = ContentAddressedStore.ComputeSha256(bytes);
        using var content = new ByteArrayContent(bytes);
        content.Headers.Add("X-Hive-Offset", "0");
        content.Headers.Add("X-Hive-Total-Bytes", bytes.Length.ToString());
        using var resp = await http.PutAsync($"/hive/artifacts/{digest}", content);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Failed to stage artifact '{name}': {(int)resp.StatusCode} {await resp.Content.ReadAsStringAsync()}");
        return new ArtifactRef { DigestSha256 = digest, Name = name, SizeBytes = bytes.Length, MediaType = "application/json", Kind = kind };
    }

    private static async Task<StageEvidence> RunCampaignAsync(
        HttpClient http, CampaignDefinition campaign, AcceptanceReport report, string stageName)
    {
        Console.WriteLine($"Submitting stage '{stageName}': {campaign.WorkUnits.Count} work unit(s)...");
        using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
            resp.EnsureSuccessStatusCode();

        var stage = new StageEvidence { Stage = stageName, CampaignId = campaign.CampaignId };
        foreach (var unit in campaign.WorkUnits)
        {
            var taskId = $"{campaign.CampaignId}-{unit.WorkUnitId}";
            var deadline = DateTime.UtcNow.AddMilliseconds(unit.TimeoutMs + 60_000);
            HiveTaskStatusEvidence last = new() { TaskId = taskId, WorkUnitId = unit.WorkUnitId, Status = "pending" };
            var seenClaimed = false;
            var consecutiveNotFound = 0;
            while (DateTime.UtcNow < deadline)
            {
                using var statusResp = await http.GetAsync($"/hive/tasks/{taskId}");
                if (statusResp.IsSuccessStatusCode)
                {
                    consecutiveNotFound = 0;
                    var body = await statusResp.Content.ReadFromJsonAsync<HiveTaskStatusWire>(JsonOptions);
                    if (body is not null)
                    {
                        if (body.Status is "claimed" or "running") seenClaimed = true;
                        last = new HiveTaskStatusEvidence
                        {
                            TaskId = taskId,
                            WorkUnitId = unit.WorkUnitId,
                            Status = body.Status,
                            ClaimedBy = body.ClaimedBy,
                            OutputArtifacts = body.OutputArtifacts ?? [],
                            ErrorMsg = body.ErrorMsg,
                            Result = body.Result,
                        };
                        if (body.Status is "completed" or "failed" or "timeout" or "cancelled")
                            break;
                    }
                }
                else if (statusResp.StatusCode == System.Net.HttpStatusCode.NotFound && seenClaimed)
                {
                    // HiveTaskQueue's retention sweep prunes terminal entries from its in-memory
                    // _tasks dict ~5 minutes after completion. If this poll lands after the unit
                    // finished AND got swept before we observed the terminal status, every future
                    // GET 404s forever -- without this check the loop would silently spin for the
                    // full TimeoutMs+60s deadline (up to ~31 minutes) on a unit that's long done.
                    // We can't recover the true outcome once swept, so surface it honestly instead
                    // of mislabeling it "pending": Passed requires "completed" everywhere, so this
                    // correctly fails the gate and prompts a rerun rather than hanging.
                    if (++consecutiveNotFound >= 3)
                    {
                        last.Status = "swept-unknown";
                        break;
                    }
                }
                await Task.Delay(2000);
            }
            Console.WriteLine($"  [{last.Status}] {unit.WorkUnitId} (claimed by {last.ClaimedBy ?? "-"})");
            stage.Units.Add(last);
        }
        report.Stages.Add(stage);
        return stage;
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }

    /// <summary>True if every "anchor" word (5+ chars, so short function words like "the"/"was"
    /// don't count) in <paramref name="expected"/> appears somewhere in <paramref name="actual"/>,
    /// matched on a 6-character prefix stem so verb-tense/suffix variation ("resulting" vs
    /// "resulted") doesn't count as missing. A coverage check, not a phrase match -- tolerates
    /// legitimate paraphrase while still catching dropped facts (a missing fact's distinctive
    /// nouns/numbers won't appear at all, stemmed or not).</summary>
    private static bool AnchorWordsCovered(string expected, string actual)
    {
        var anchors = expected.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.Trim('.', ',', ';', ':', '"', '\''))
            .Where(w => w.Length >= 5)
            .Select(w => w[..Math.Min(w.Length, 6)])
            .ToList();
        return anchors.Count > 0 && anchors.All(w => actual.Contains(w, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Mirrors HiveNativeRoleExecutorAdapter.ExecuteContextFabricVerifierAsync's citation
    /// check exactly (quote substring, char-offset, quote-digest) so it can serve as an independent
    /// ground truth for grading the verifier work unit's reported verdict.</summary>
    private static List<string> RecomputeCitationErrors(FabricClaim claim, string segmentText)
    {
        var errors = new List<string>();
        foreach (var citation in claim.Citations ?? [])
        {
            if (citation is null || string.IsNullOrWhiteSpace(citation.Quote)) continue;
            var pos = segmentText.IndexOf(citation.Quote, StringComparison.Ordinal);
            if (pos < 0)
                errors.Add($"Quote not found in source: '{citation.Quote[..Math.Min(80, citation.Quote.Length)]}'");
            else if (citation.CharStart >= 0 && citation.CharStart != pos)
                errors.Add($"CharStart mismatch: expected {pos}, got {citation.CharStart}");
            if (!string.IsNullOrWhiteSpace(citation.QuoteDigest))
            {
                var expectedDigest = FabricHashing.Sha256(citation.Quote);
                if (!string.Equals(expectedDigest, citation.QuoteDigest, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"QuoteDigest mismatch for claim {claim.ClaimId}");
            }
        }
        return errors;
    }

    // ── Worker-death recovery test (CF-6 exit gate: "worker death is recovered without
    //    duplicate accepted evidence") ─────────────────────────────────────────────────────
    //
    // Sequence, and the HiveTaskQueue mechanism each step exercises:
    //   1. submit ONE reader work unit (real native execution, MaxAttempts=3);
    //   2. wait for a worker (W1) to claim it, capture attempt 1's claim token from the
    //      Warchief's durable hive_tasks row (best-effort — the in-memory token is never
    //      exposed over HTTP, by design);
    //   3. suspend W1's process via the fault script → heartbeats stop → CheckTimeouts
    //      (HeartbeatTimeoutSec=45, 10s watchdog tick) re-queues the unit with a ROTATED token;
    //   4. wait for a different worker (W2) to claim the re-queued unit;
    //   5. while W2 holds the claim, POST /complete impersonating W1 with the captured
    //      attempt-1 token — this is exactly the packet a zombie W1 would send, and must be
    //      rejected 409 by the token-mismatch gate in HandleCompleteAsync;
    //   6. resume W1 so its REAL late completion also fires organically (same gate rejects it);
    //   7. wait for W2's completion, then soak and assert exactly ONE task_complete event and
    //      a single accepted campaign_work_units row.
    //
    // Suspend-not-kill is deliberate: from the Warchief's side a suspended process is
    // indistinguishable from a dead one (fail-stop), but a suspended worker can be resumed to
    // play the "presumed-dead node comes back and submits" half of the gate — the half that
    // actually threatens duplicate accepted evidence — and the fleet needs no manual relaunch.
    private static async Task<int> RunDeathTestAsync(
        HttpClient http, string warchief, string outDir, string modelHash, string? warchiefDb, string faultScript)
    {
        var report = new DeathTestReport
        {
            Warchief = warchief,
            ModelHash = modelHash,
            FaultScript = faultScript,
            WarchiefDb = warchiefDb,
            StartedAt = DateTimeOffset.UtcNow,
        };
        void Timeline(string what)
        {
            report.Timeline.Add($"{DateTime.UtcNow:O} {what}");
            Console.WriteLine(what);
        }
        async Task<int> FinishAsync(int exitCode, string verdict)
        {
            report.Verdict = verdict;
            report.FinishedAt = DateTimeOffset.UtcNow;
            var outPath = Path.Combine(outDir, $"cf6-death-test-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report,
                new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            Console.WriteLine();
            Console.WriteLine($"Verdict: {verdict}");
            Console.WriteLine($"Evidence written: {outPath}");
            return exitCode;
        }

        try
        {
            // Baseline the event ring so every assertion below only counts THIS test's events.
            var baseline = await http.GetFromJsonAsync<List<HiveEventWire>>("/hive/events?since=0", JsonOptions) ?? [];
            report.BaselineEventSeq = baseline.Count == 0 ? -1 : baseline.Max(e => e.Seq);

            var fixture = DeterministicFabricCorpus.Create();
            var corpus = fixture.Corpus;
            // Largest segment = longest native inference = widest window between claim and
            // completion for the suspension to land in.
            var segment = corpus.Segments.OrderByDescending(s => s.Text.Length).First();
            var single = corpus with { Segments = [segment], EstimatedSourceTokens = segment.EstimatedTokens };
            var segRef = await StageArtifactAsync(http,
                Encoding.UTF8.GetBytes(FabricJson.Serialize(single)), $"{segment.SegmentId}.corpus.json", "input");
            report.SegmentId = segment.SegmentId;

            var campaign = CampaignTemplates.ContextFabricReaders("cf6-death-test", [segRef], modelHash);
            using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                resp.EnsureSuccessStatusCode();
            var unit = campaign.WorkUnits[0];
            var taskId = $"{campaign.CampaignId}-{unit.WorkUnitId}";
            report.CampaignId = campaign.CampaignId;
            report.TaskId = taskId;
            Timeline($"Submitted 1-unit reader campaign {campaign.CampaignId} " +
                     $"(task {taskId}, segment {segment.SegmentId}, maxAttempts={unit.MaxAttempts})");

            // ── Phase 1: wait for the first claim ──
            string? w1 = null;
            var claimDeadline = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < claimDeadline)
            {
                var st = await GetTaskStatusAsync(http, taskId);
                if (st is { Status: "completed" or "failed" or "timeout" })
                {
                    Timeline($"INVALID: task reached '{st.Status}' (by {st.ClaimedBy ?? "-"}) before any fault could be injected.");
                    return await FinishAsync(4, "INVALID — unit finished before fault injection; re-run");
                }
                if (st is { Status: "claimed", ClaimedBy.Length: > 0 })
                {
                    w1 = st.ClaimedBy;
                    break;
                }
                await Task.Delay(1000);
            }
            if (w1 is null)
            {
                await TryCancelCampaignAsync(http, campaign.CampaignId);
                Timeline("INVALID: no worker claimed the unit within 10 minutes.");
                return await FinishAsync(4, "INVALID — never claimed; check worker fleet");
            }
            report.FirstClaimant = w1;
            Timeline($"Claimed by {w1} (attempt 1)");

            // Never inject a fault into the machine this runner (and the Warchief) is on.
            if (string.Equals(w1, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            {
                await TryCancelCampaignAsync(http, campaign.CampaignId);
                Timeline($"INVALID: unit was claimed by this machine ({w1}) — cannot suspend the Warchief host. " +
                         "Disable the local worker (Settings → HIVE MIND worker mode) and re-run.");
                return await FinishAsync(4, "INVALID — claimed by Warchief host");
            }

            // Attempt-1 claim token, from the durable row PersistTask wrote on claim. Must be
            // read NOW: the hive_tasks upsert overwrites claim_token when the re-claim happens.
            report.FirstClaimToken = warchiefDb is null ? null : await TryReadClaimTokenAsync(warchiefDb, taskId);
            Timeline(report.FirstClaimToken is null
                ? "⚠ attempt-1 claim token unavailable (no --warchief-db or row not yet written) — stale probe will use a placeholder token"
                : $"Captured attempt-1 claim token {report.FirstClaimToken[..Math.Min(6, report.FirstClaimToken.Length)]}… from durable history");

            // ── Phase 2: suspend W1 mid-execution ──
            var (suspendCode, suspendOut) = await RunFaultScriptAsync(faultScript, "suspend", w1);
            report.SuspendOutput = suspendOut;
            if (suspendCode != 0)
            {
                await TryCancelCampaignAsync(http, campaign.CampaignId);
                Timeline($"INVALID: fault script suspend failed (exit {suspendCode}): {suspendOut}");
                return await FinishAsync(4, "INVALID — fault injection failed");
            }
            report.SuspendedAt = DateTimeOffset.UtcNow;
            Timeline($"Suspended {w1}'s worker process: {suspendOut}");

            // ── Phase 3: watchdog re-queue (heartbeat timeout 45s + 10s watchdog tick) ──
            var requeueDeadline = DateTime.UtcNow.AddSeconds(180);
            HiveEventWire? requeueEvent = null;
            while (DateTime.UtcNow < requeueDeadline && requeueEvent is null)
            {
                var events = await GetTaskEventsAsync(http, report.BaselineEventSeq, taskId);
                requeueEvent = events.FirstOrDefault(e => e.Type == "task_requeued");
                if (events.Any(e => e.Type == "task_complete"))
                {
                    Timeline("INVALID: unit completed before the suspension took effect (fault lost the race).");
                    await RunFaultScriptAsync(faultScript, "resume", w1);
                    return await FinishAsync(4, "INVALID — completed before suspension; re-run");
                }
                if (requeueEvent is null) await Task.Delay(2000);
            }
            if (requeueEvent is null)
            {
                Timeline("FAIL: no task_requeued event within 180s of suspension — watchdog did not recover the unit.");
                await RunFaultScriptAsync(faultScript, "resume", w1);
                return await FinishAsync(2, "FAIL — worker death was not recovered");
            }
            report.RequeueSeenAt = DateTimeOffset.UtcNow;
            report.RequeueEventMsg = requeueEvent.Msg;
            Timeline($"task_requeued observed ({(report.RequeueSeenAt - report.SuspendedAt)?.TotalSeconds:F0}s after suspension): {requeueEvent.Msg}");

            // ── Phase 4: wait for the re-claim by a DIFFERENT worker ──
            // 20 minutes, not 10: the surviving worker may legitimately be mid-inference on
            // another unit when the re-queue lands (its lease loop is serial), and a large
            // segment on a slow quant can run 13+ minutes — observed 2026-07-01, where a
            // 10-minute window expired ONE second before the recovering claim arrived.
            string? w2 = null;
            var reclaimDeadline = DateTime.UtcNow.AddMinutes(20);
            var probePhase = "during-second-claim";
            while (DateTime.UtcNow < reclaimDeadline)
            {
                var st = await GetTaskStatusAsync(http, taskId);
                if (st is { Status: "claimed", ClaimedBy.Length: > 0 })
                {
                    w2 = st.ClaimedBy;
                    break;
                }
                if (st is { Status: "completed", ClaimedBy.Length: > 0 })
                {
                    // Claim window was shorter than our poll interval — task is already done.
                    // The stale probe below then exercises the status-guard 409 instead of the
                    // token-mismatch 409; both are rejection paths of the same endpoint.
                    w2 = st.ClaimedBy;
                    probePhase = "after-completion";
                    break;
                }
                if (st is { Status: "failed" or "timeout" })
                {
                    Timeline($"FAIL: re-queued unit ended '{st.Status}' instead of being re-executed.");
                    await RunFaultScriptAsync(faultScript, "resume", w1);
                    return await FinishAsync(2, "FAIL — re-queued unit was not re-executed");
                }
                await Task.Delay(1000);
            }
            if (w2 is null)
            {
                Timeline("FAIL: no second claim within 10 minutes of the re-queue.");
                await RunFaultScriptAsync(faultScript, "resume", w1);
                return await FinishAsync(2, "FAIL — re-queued unit never re-claimed");
            }
            report.SecondClaimant = w2;
            report.ReclaimSeenAt = DateTimeOffset.UtcNow;
            Timeline($"Re-claimed by {w2} (attempt 2), status phase: {probePhase}");

            // ── Phase 5: the zombie packet — W1's completion with the attempt-1 token ──
            report.StaleProbeTokenUsed = report.FirstClaimToken ?? "death-test-unknown-token";
            report.StaleProbePhase = probePhase;
            var probe = new HiveTaskResult
            {
                TaskId = taskId,
                WorkerId = w1,
                Status = "completed",
                Result = "CF-6 DEATH-TEST STALE PROBE — this result must be rejected, never accepted",
                ClaimToken = report.StaleProbeTokenUsed,
                Attempt = 1,
            };
            using (var probeResp = await http.PostAsJsonAsync($"/hive/tasks/{taskId}/complete", probe, JsonOptions))
                report.StaleProbeHttpStatus = (int)probeResp.StatusCode;
            Timeline($"Stale /complete probe (worker={w1}, attempt-1 token) → HTTP {report.StaleProbeHttpStatus}");

            // ── Phase 6: resume W1 so its REAL late completion also fires organically ──
            var (resumeCode, resumeOut) = await RunFaultScriptAsync(faultScript, "resume", w1);
            report.ResumeOutput = resumeOut;
            report.ResumedAt = DateTimeOffset.UtcNow;
            Timeline(resumeCode == 0
                ? $"Resumed {w1}'s worker process: {resumeOut}"
                : $"⚠ resume failed (exit {resumeCode}) — resume {w1} manually: {resumeOut}");

            // ── Phase 7: wait for terminal state, then soak for late/duplicate completions ──
            HiveTaskStatusWire? final = null;
            var finalDeadline = DateTime.UtcNow.AddMilliseconds(unit.TimeoutMs + 60_000);
            while (DateTime.UtcNow < finalDeadline)
            {
                var st = await GetTaskStatusAsync(http, taskId);
                if (st is { Status: "completed" or "failed" or "timeout" })
                {
                    final = st;
                    break;
                }
                await Task.Delay(2000);
            }
            report.FinalStatus = final?.Status ?? "unknown";
            report.FinalClaimedBy = final?.ClaimedBy;
            Timeline($"Terminal status: {report.FinalStatus} (claimedBy {report.FinalClaimedBy ?? "-"})");

            Timeline("Soaking 180s for W1's organic late completion / any duplicate accept…");
            await Task.Delay(TimeSpan.FromSeconds(180));

            report.TaskEvents = await GetTaskEventsAsync(http, report.BaselineEventSeq, taskId);
            report.TaskCompleteEventCount = report.TaskEvents.Count(e => e.Type == "task_complete");

            if (warchiefDb is not null)
            {
                report.FinalClaimToken = await TryReadClaimTokenAsync(warchiefDb, taskId);
                report.DurableTaskRow = await TryReadDurableRowAsync(warchiefDb,
                    "SELECT status || '|' || COALESCE(claimed_by_worker,'') || '|' || COALESCE(claim_token,'') " +
                    "FROM hive_tasks WHERE task_id=$p1 ORDER BY updated_at DESC LIMIT 1", taskId);
                report.DurableWorkUnitRow = await TryReadDurableRowAsync(warchiefDb,
                    "SELECT status || '|attempt=' || attempt || '|' || COALESCE(claimed_by_node,'') " +
                    "FROM campaign_work_units WHERE campaign_id=$p1 AND work_unit_id=$p2",
                    campaign.CampaignId, unit.WorkUnitId);
            }

            // ── Verdicts ──
            report.RecoveredOnOtherNode =
                report.FinalStatus == "completed"
                && !string.Equals(report.FinalClaimedBy, w1, StringComparison.OrdinalIgnoreCase);
            report.StaleCompleteRejected = report.StaleProbeHttpStatus is >= 400 and < 500;
            var tokenRotated = report.FirstClaimToken is null || report.FinalClaimToken is null
                ? (bool?)null
                : !string.Equals(report.FirstClaimToken, report.FinalClaimToken, StringComparison.Ordinal);
            report.ClaimTokenRotated = tokenRotated;
            report.AcceptedExactlyOnce =
                report.TaskCompleteEventCount == 1
                && report.StaleCompleteRejected
                && tokenRotated != false;
            report.Passed = report.RecoveredOnOtherNode && report.AcceptedExactlyOnce;

            Console.WriteLine();
            Console.WriteLine($"Recovered on another node:  {report.RecoveredOnOtherNode} ({w1} → {report.FinalClaimedBy})");
            Console.WriteLine($"Stale complete rejected:    {report.StaleCompleteRejected} (HTTP {report.StaleProbeHttpStatus}, phase {probePhase})");
            Console.WriteLine($"Claim token rotated:        {(tokenRotated.HasValue ? tokenRotated.ToString() : "unverified (no durable rows)")}");
            Console.WriteLine($"task_complete events:       {report.TaskCompleteEventCount} (must be exactly 1)");
            return await FinishAsync(report.Passed ? 0 : 2, report.Passed ? "PASS" : "FAIL");
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            Timeline($"Run failed: {ex.Message}");
            return await FinishAsync(1, "ERROR");
        }
    }

    private static async Task<HiveTaskStatusWire?> GetTaskStatusAsync(HttpClient http, string taskId)
    {
        using var resp = await http.GetAsync($"/hive/tasks/{taskId}");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<HiveTaskStatusWire>(JsonOptions);
    }

    private static async Task<List<HiveEventWire>> GetTaskEventsAsync(HttpClient http, long sinceSeq, string taskId)
    {
        var events = await http.GetFromJsonAsync<List<HiveEventWire>>($"/hive/events?since={sinceSeq}", JsonOptions) ?? [];
        return events.Where(e => string.Equals(e.TaskId, taskId, StringComparison.Ordinal)).ToList();
    }

    private static async Task TryCancelCampaignAsync(HttpClient http, string campaignId)
    {
        try { using var _ = await http.PostAsync($"/hive/campaigns/{campaignId}/cancel", null); } catch { }
    }

    /// <summary>Runs the operator-supplied fault script:
    /// <c>powershell -File {script} -Action suspend|resume -Worker {id}</c>. The script owns the
    /// worker-id → machine mapping and MUST refuse ids it does not recognise (that refusal is the
    /// safety net that keeps the fault away from the Warchief host and unknown machines).</summary>
    private static async Task<(int Code, string Output)> RunFaultScriptAsync(string script, string action, string worker)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
                $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Action {action} -Worker \"{worker}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var stdoutTask = p.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = p.StandardError.ReadToEndAsync(cts.Token);
            await p.WaitForExitAsync(cts.Token);
            return (p.ExitCode, $"{(await stdoutTask).Trim()} {(await stderrTask).Trim()}".Trim());
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static Task<string?> TryReadClaimTokenAsync(string dbPath, string taskId) => TryReadDurableRowAsync(
        dbPath, "SELECT claim_token FROM hive_tasks WHERE task_id=$p1 ORDER BY updated_at DESC LIMIT 1", taskId);

    /// <summary>Single-value read-only query against the Warchief's durable theorc.db. Retries
    /// briefly: the row is written best-effort by PersistTask moments after the transition, and
    /// the app may hold the WAL mid-checkpoint. Null = evidence unavailable, never fatal.</summary>
    private static async Task<string?> TryReadDurableRowAsync(string dbPath, string sql, params string[] args)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = sql;
                for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue($"$p{i + 1}", args[i]);
                if (await cmd.ExecuteScalarAsync() is string s && s.Length > 0) return s;
            }
            catch { /* retry */ }
            await Task.Delay(500);
        }
        return null;
    }

    private static FabricCorpus BuildSingleSegmentCorpus(string caseId, string side, string text)
    {
        var segmentId = $"{caseId}-{side}";
        var digest = ContentAddressedStore.ComputeSha256(Encoding.UTF8.GetBytes(text));
        var segment = new FabricSegment(segmentId, 0, $"{caseId} {side}", text, digest, text.Length / 4);
        return new FabricCorpus(
            $"stitch-fixture-{caseId}-{side}",
            $"stitch-fixture-{caseId}",
            "stitch-fixture",
            digest,
            FabricSchemaVersions.Corpus,
            [segment],
            segment.EstimatedTokens);
    }
}

/// <summary>Wire shape of GET /hive/events — HiveTaskQueue serves the HiveEventBus ring as a
/// plain JSON array of HiveEvent.</summary>
internal sealed class HiveEventWire
{
    public long Seq { get; set; }
    public DateTime Ts { get; set; }
    public string Type { get; set; } = "";
    public string Msg { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string WorkerId { get; set; } = "";
}

/// <summary>Evidence bundle for the --death-test worker-death recovery run. Everything needed to
/// audit the CF-6 exit-gate claim afterwards: the full task event timeline, the fault-injection
/// timestamps, the stale-probe HTTP result, and the durable claim-token rotation.</summary>
internal sealed class DeathTestReport
{
    public string Warchief { get; set; } = "";
    public string ModelHash { get; set; } = "";
    public string FaultScript { get; set; } = "";
    public string? WarchiefDb { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public string CampaignId { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public long BaselineEventSeq { get; set; }
    public string? FirstClaimant { get; set; }
    /// <summary>Attempt-1 claim token captured from durable history while W1 held the claim.
    /// Useless to an attacker after the rotation — recording it IS the point: the probe below
    /// proves this exact token is refused once the unit is re-queued.</summary>
    public string? FirstClaimToken { get; set; }
    public DateTimeOffset? SuspendedAt { get; set; }
    public string? SuspendOutput { get; set; }
    public DateTimeOffset? RequeueSeenAt { get; set; }
    public string? RequeueEventMsg { get; set; }
    public string? SecondClaimant { get; set; }
    public DateTimeOffset? ReclaimSeenAt { get; set; }
    public string StaleProbeTokenUsed { get; set; } = "";
    /// <summary>"during-second-claim" = probe hit the token-mismatch 409 (HandleCompleteAsync's
    /// stale gate); "after-completion" = probe hit the status-guard 409. Both reject.</summary>
    public string StaleProbePhase { get; set; } = "";
    public int StaleProbeHttpStatus { get; set; }
    public DateTimeOffset? ResumedAt { get; set; }
    public string? ResumeOutput { get; set; }
    public string FinalStatus { get; set; } = "";
    public string? FinalClaimedBy { get; set; }
    public string? FinalClaimToken { get; set; }
    public int TaskCompleteEventCount { get; set; }
    public List<HiveEventWire> TaskEvents { get; set; } = [];
    public string? DurableTaskRow { get; set; }
    public string? DurableWorkUnitRow { get; set; }
    public List<string> Timeline { get; set; } = [];
    public bool RecoveredOnOtherNode { get; set; }
    public bool StaleCompleteRejected { get; set; }
    /// <summary>Null = durable rows unavailable, rotation unverified (does not fail the gate on
    /// its own; the 409 + single task_complete still hold). False = rotation DIDN'T happen — fail.</summary>
    public bool? ClaimTokenRotated { get; set; }
    public bool AcceptedExactlyOnce { get; set; }
    public bool Passed { get; set; }
    public string Verdict { get; set; } = "";
    public string? Error { get; set; }
}

internal sealed class HiveTaskStatusWire
{
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public string? ErrorMsg { get; set; }
    public string? Result { get; set; }
    public List<ArtifactRef>? OutputArtifacts { get; set; }
}

internal sealed class HiveTaskStatusEvidence
{
    public string TaskId { get; set; } = "";
    public string WorkUnitId { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public string? ErrorMsg { get; set; }
    public string? Result { get; set; }
    public List<ArtifactRef> OutputArtifacts { get; set; } = [];
}

internal sealed class StageEvidence
{
    public string Stage { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public List<HiveTaskStatusEvidence> Units { get; set; } = [];
}

internal sealed class QuestionEvidence
{
    public string QuestionId { get; set; } = "";
    public string Kind { get; set; } = "";
    public List<string> ExpectedTerms { get; set; } = [];
    public bool ExpectAbstention { get; set; }
    public Dictionary<string, string> PerSegmentFindings { get; set; } = [];
    public bool AnswerValidated { get; set; }
}

/// <summary>Grades a citation-verifier work unit's reported verdict against an independently
/// recomputed ground truth (see Program.RecomputeCitationErrors).</summary>
internal sealed class VerifierEvidence
{
    public string WorkUnitId { get; set; } = "";
    public string SegmentId { get; set; } = "";
    public bool ReportedAllPassed { get; set; }
    public bool RecomputedAllPassed { get; set; }
    public bool Validated { get; set; }
}

/// <summary>Grades a boundary-stitch fixture case's stitcher output against
/// DeterministicFabricCorpus.CreateBoundaryStitchFixture()'s expectations.</summary>
internal sealed class StitchCaseEvidence
{
    public string CaseId { get; set; } = "";
    public string WorkUnitId { get; set; } = "";
    public bool SummaryPreserved { get; set; }
    public bool LinkedFactsCovered { get; set; }
    public bool ForbiddenTermsAbsent { get; set; }
    public bool Validated { get; set; }
}

/// <summary>Mirrors HiveNativeRoleExecutorAdapter's private StitchOutput record shape so the
/// stitcher work unit's Result JSON can be deserialized here.</summary>
internal sealed record StitchUnitOutput(
    string CorpusId,
    string DocumentId,
    string LeftSegmentId,
    string RightSegmentId,
    bool Passed,
    string Summary,
    IReadOnlyList<string> LinkedFacts);

/// <summary>Mirrors HiveNativeRoleExecutorAdapter's private ReducerOutput record shape so the
/// reducer work unit's Result JSON can be deserialized here.</summary>
internal sealed record ReducerUnitOutput(
    string CorpusId,
    string DocumentId,
    string GenerationId,
    int NodeCount,
    IReadOnlyList<FabricReductionNode> Nodes);

internal sealed class AcceptanceReport
{
    public string Warchief { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<StageEvidence> Stages { get; set; } = [];
    public List<QuestionEvidence> Questions { get; set; } = [];
    public List<VerifierEvidence> Verifiers { get; set; } = [];
    public List<StitchCaseEvidence> StitchCases { get; set; } = [];
    public bool ReducerSegmentsCovered { get; set; }
    public bool ReducerClaimsCovered { get; set; }
    public bool ReducerValidated { get; set; }
    public List<string> DistinctWorkerIds { get; set; } = [];
    public int NodeCount { get; set; }
    /// <summary>The distinct nodes that claimed a READER unit specifically — the actual CF-6
    /// per-segment fan-out proof, kept separate from DistinctWorkerIds (all claimants) so a node
    /// that only ever handled cheap verifier/query units isn't miscounted toward the reader gate.</summary>
    public List<string> ReaderWorkerIds { get; set; } = [];
    public int ReaderNodeCount { get; set; }
    public int MinNodesRequired { get; set; }
    public string GateMode { get; set; } = "smoke";
    public bool Passed { get; set; }
    public string? Error { get; set; }
    /// <summary>Corpus generation this run was executed against — a resume must match it so
    /// replayed evidence can't be attributed to a corpus it wasn't produced from.</summary>
    public string CorpusGenerationId { get; set; } = "";
    /// <summary>--model-hash this run was executed against (same matching requirement on resume).</summary>
    public string ModelHash { get; set; } = "";
    /// <summary>True when stages 1-4 (readers/verifiers/stitchers/reducer) and the reader
    /// node-fanout proof were REPLAYED from a prior report via --resume-from rather than run
    /// fresh. A resumed run validates query logic against real evidence but does NOT
    /// re-demonstrate multi-node fan-out, so it is not a clean CF-6 acceptance-gate pass.</summary>
    public bool Resumed { get; set; }
}
