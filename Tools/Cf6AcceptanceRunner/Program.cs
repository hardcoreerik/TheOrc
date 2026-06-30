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
/// the reducer's coverage of every segment's and reader card's claims. Worker-death recovery and
/// the Ollama-absence proof
/// are likewise separate manual procedures this tool supports as a harness for, not something it
/// runs itself. Run this directly against the Warchief's own loopback (no HMAC needed locally);
/// the remote workers reached over the LAN/Tailscale are the ones that supply the second/third node.
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
        Directory.CreateDirectory(outDir);
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

            // Query work units are built per-segment in corpus order (see queryUnits below) --
            // this mirrors that same ordering so a question's ExpectedSegmentIds can be mapped to
            // the exact work unit that should (and the ones that should NOT) report Relevant=true.
            var segmentIdToQueryUnitId = corpus.Segments.OrderBy(s => s.Ordinal)
                .Select((s, index) => (s.SegmentId, WorkUnitId: $"query-{index + 1:00000}"))
                .ToDictionary(t => t.SegmentId, t => t.WorkUnitId);
            var queryUnitIdToSegmentId = segmentIdToQueryUnitId.ToDictionary(kv => kv.Value, kv => kv.Key);

            // ── Stage 1: readers (one work unit per segment -- the fan-out that proves >1 node) ──
            var readersCampaign = CampaignTemplates.ContextFabricReaders(
                "cf6-acceptance-readers", segmentRefs, modelHash);
            var readerStage = await RunCampaignAsync(http, readersCampaign, report, "readers");

            var readerCardRefs = new List<ArtifactRef>(segmentRefs.Count);
            var readerWorkUnitIds = new List<string>(segmentRefs.Count);
            var readerCards = new List<FabricEvidenceCard>(segmentRefs.Count);
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
                    Inputs = [questionRef, segCorpus],
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
            // proven multi-node distribution.
            var distinctWorkers = report.Stages.SelectMany(s => s.Units).Select(u => u.ClaimedBy)
                .Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var readerWorkers = readerStage.Units.Select(u => u.ClaimedBy)
                .Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            report.DistinctWorkerIds = distinctWorkers;
            report.NodeCount = distinctWorkers.Count;
            report.ReaderNodeCount = readerWorkers.Count;
            report.MinNodesRequired = minNodes;
            report.FinishedAt = DateTimeOffset.UtcNow;
            report.Passed = report.Stages.All(s => s.Units.All(u => u.Status == "completed"))
                && report.ReaderNodeCount >= minNodes
                && report.Questions.All(q => q.AnswerValidated)
                && report.Verifiers.All(v => v.Validated)
                && report.StitchCases.All(c => c.Validated)
                && report.ReducerValidated;

            var outPath = Path.Combine(outDir, $"cf6-acceptance-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"Distinct worker nodes that claimed at least one unit: {report.NodeCount} ({string.Join(", ", distinctWorkers)})");
            Console.WriteLine($"Distinct worker nodes that claimed a READER unit (the fan-out proof): {report.ReaderNodeCount} ({string.Join(", ", readerWorkers)})");
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
    public int ReaderNodeCount { get; set; }
    public int MinNodesRequired { get; set; }
    public string GateMode { get; set; } = "smoke";
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
