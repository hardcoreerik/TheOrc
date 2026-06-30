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
/// benchmark's expected terms/segments/abstention. It does NOT validate the semantic correctness
/// of verifier/stitcher/reducer output (only that those units complete) -- that is a separate,
/// not-yet-built piece of the CF-6 exit gate. Worker-death recovery and the Ollama-absence proof
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

            var strippedMeta = corpus with
            {
                Segments = corpus.Segments.Select(s => s with { Text = "", TextDigest = "", EstimatedTokens = 0 }).ToArray(),
                EstimatedSourceTokens = 0,
            };
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
            foreach (var unit in readersCampaign.WorkUnits)
            {
                var status = readerStage.Units.First(u => u.WorkUnitId == unit.WorkUnitId);
                if (status.Status != "completed" || status.OutputArtifacts.Count == 0)
                    throw new InvalidOperationException(
                        $"Reader unit '{unit.WorkUnitId}' did not complete with an output artifact (status={status.Status}).");
                readerCardRefs.Add(status.OutputArtifacts[0]);
                readerWorkUnitIds.Add(unit.WorkUnitId);
            }

            // ── Stage 2: verifiers (CPU-only citation check, one per reader output) ──
            var verifierItems = readerCardRefs
                .Zip(segmentRefs, readerWorkUnitIds)
                .Select(t => (t.First, t.Second, t.Third))
                .ToList();
            var verifiersCampaign = StripDependsOn(CampaignTemplates.ContextFabricVerifiers(
                "cf6-acceptance-verifiers", verifierItems, modelHash));
            await RunCampaignAsync(http, verifiersCampaign, report, "verifiers");

            // ── Stage 3: stitchers (adjacent-segment boundary resolution) ──
            var pairs = new List<(ArtifactRef LeftCorpus, ArtifactRef RightCorpus, string LeftReaderId, string RightReaderId)>();
            for (var i = 0; i < segmentRefs.Count - 1; i++)
                pairs.Add((segmentRefs[i], segmentRefs[i + 1], readerWorkUnitIds[i], readerWorkUnitIds[i + 1]));
            var stitchersCampaign = StripDependsOn(CampaignTemplates.ContextFabricStitchers(
                "cf6-acceptance-stitchers", pairs, modelHash));
            await RunCampaignAsync(http, stitchersCampaign, report, "stitchers");

            // ── Stage 4: reducer (fan-in over corpus-meta + every reader card) ──
            var reducerCampaign = StripDependsOn(CampaignTemplates.ContextFabricReducer(
                "cf6-acceptance-reducer", corpusMetaRef, readerCardRefs, readerWorkUnitIds, modelHash));
            await RunCampaignAsync(http, reducerCampaign, report, "reducer");

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
                && report.Questions.All(q => q.AnswerValidated);

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
            while (DateTime.UtcNow < deadline)
            {
                using var statusResp = await http.GetAsync($"/hive/tasks/{taskId}");
                if (statusResp.IsSuccessStatusCode)
                {
                    var body = await statusResp.Content.ReadFromJsonAsync<HiveTaskStatusWire>(JsonOptions);
                    if (body is not null)
                    {
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

internal sealed class AcceptanceReport
{
    public string Warchief { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<StageEvidence> Stages { get; set; } = [];
    public List<QuestionEvidence> Questions { get; set; } = [];
    public List<string> DistinctWorkerIds { get; set; } = [];
    public int NodeCount { get; set; }
    public int ReaderNodeCount { get; set; }
    public int MinNodesRequired { get; set; }
    public string GateMode { get; set; } = "smoke";
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
