// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Services.Hive;

namespace Hv1NativeCampaignRunner;

/// <summary>
/// HV-1 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): submits real native-role campaign
/// work units to a live Warchief, pinned via ExcludedWorkerIds so a target count of jobs lands on
/// each of two named workers, polls each unit to a terminal state, and records the per-job
/// evidence HV-1 requires -- runtime name, machine identity (ClaimedBy), model/adapter binding,
/// output, and worker-reported stats -- straight from HiveTaskResult.Attestation/Metrics
/// (surfaced over GET /hive/tasks/{id} for exactly this purpose). ExecutionKind=NativeAgent is
/// fail-closed by HiveWorkerAgent's own design (no Ollama fallback path is even reachable for a
/// non-LegacyAgent execution kind), so "zero fallback" is a structural property of every job
/// submitted here -- the Attestation.RuntimeName=="NativeRoleRuntime" check below is the
/// after-the-fact confirmation that guarantee actually held, not the mechanism enforcing it.
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
        var outDir = GetArg(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-1-lane");
        var modelHash = GetArg(args, "--model-hash")
            ?? throw new InvalidOperationException("--model-hash is required (the pinned fleet GGUF's SHA-256).");
        var workerA = GetArg(args, "--worker-a") ?? "HARDCOREPC";
        var workerB = GetArg(args, "--worker-b") ?? "HARDCORELAPTOPMSI";
        var jobsPerWorker = int.TryParse(GetArg(args, "--jobs-per-worker"), out var n) ? n : 5;
        var role = GetArg(args, "--role") ?? "Coder";
        var timeoutMs = int.TryParse(GetArg(args, "--timeout-ms"), out var t) ? t : 300_000;
        // Opt-in: only workers whose Capabilities were populated via WorkerCapabilityDetector
        // (OrchestratorIDE.Daemon's HiveService) actually report NativeModelHashes -- swarmcli's
        // --worker mode never calls that detector, so gating on this against a swarmcli-based
        // fleet would reject every worker unconditionally. Off by default for that reason.
        var gateModelHash = Array.IndexOf(args, "--gate-model-hash") >= 0;
        Directory.CreateDirectory(outDir);

        using var http = new HttpClient { BaseAddress = new Uri(warchief), Timeout = TimeSpan.FromMinutes(10) };

        var report = new Hv1Report
        {
            Warchief = warchief,
            ModelHash = modelHash,
            WorkerA = workerA,
            WorkerB = workerB,
            JobsPerWorkerRequired = jobsPerWorker,
            StartedAt = DateTimeOffset.UtcNow,
            ModelHashNote = gateModelHash
                ? "ModelHash gating was ON (--gate-model-hash): each work unit required this hash in " +
                  "the worker's advertised NativeModelHashes, so a completed job's Attestation.ModelHash " +
                  "below is a live per-job capability match, not just an echoed value."
                : "ModelHash gating was OFF (default): --model-hash is recorded for reference against " +
                  "HV-0.2's separately-verified pinned fixture, not asserted as a live per-job capability " +
                  "match. Workers whose Capabilities were never populated via WorkerCapabilityDetector " +
                  "(e.g. swarmcli --worker) would otherwise be rejected unconditionally by this gate.",
        };

        try
        {
            Console.WriteLine($"HV-1 native campaign run against {warchief}");
            Console.WriteLine($"Target: {jobsPerWorker} native '{role}' job(s) each on {workerA} and {workerB}");

            var workUnits = new List<WorkUnit>();
            foreach (var (target, other) in new[] { (workerA, workerB), (workerB, workerA) })
            {
                for (var i = 1; i <= jobsPerWorker; i++)
                {
                    var workUnitId = $"hv1-{target}-{i:00}";
                    var marker = $"HV1-PROOF {workUnitId}";
                    workUnits.Add(new WorkUnit
                    {
                        WorkUnitId = workUnitId,
                        Title = $"HV-1 native proof job {i} targeting {target}",
                        Role = role,
                        ExecutionKind = HiveExecutionKinds.NativeAgent,
                        Requirements = new ResourceRequirements
                        {
                            ExcludedWorkerIds = [other],
                            NativeModelHash = gateModelHash ? modelHash : "",
                        },
                        Spec = $"Create a file named hv1_proof.txt in the workspace root containing exactly " +
                               $"this single line and nothing else: {marker}",
                        TimeoutMs = timeoutMs,
                    });
                }
            }

            var campaign = new CampaignDefinition
            {
                Name = "hv1-native-campaign",
                WorkUnits = workUnits,
            };

            Console.WriteLine($"Submitting campaign {campaign.CampaignId}: {workUnits.Count} work unit(s)...");
            using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                resp.EnsureSuccessStatusCode();
            report.CampaignId = campaign.CampaignId;

            var targetByUnitId = workUnits.ToDictionary(
                u => u.WorkUnitId,
                u => u.WorkUnitId.StartsWith($"hv1-{workerA}-", StringComparison.Ordinal) ? workerA : workerB);
            var markerByUnitId = workUnits.ToDictionary(
                u => u.WorkUnitId, u => $"HV1-PROOF {u.WorkUnitId}");

            foreach (var unit in workUnits)
            {
                var taskId = $"{campaign.CampaignId}-{unit.WorkUnitId}";
                var deadline = DateTime.UtcNow.AddMilliseconds(unit.TimeoutMs + 60_000);
                HiveTaskStatusResponse? last = null;
                var consecutiveNotFound = 0;
                var seenClaimed = false;
                while (DateTime.UtcNow < deadline)
                {
                    using var statusResp = await http.GetAsync($"/hive/tasks/{taskId}");
                    if (statusResp.IsSuccessStatusCode)
                    {
                        consecutiveNotFound = 0;
                        var body = await statusResp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions);
                        if (body is not null)
                        {
                            last = body;
                            if (body.Status is "claimed" or "running") seenClaimed = true;
                            if (body.Status is "completed" or "failed" or "timeout" or "cancelled") break;
                        }
                    }
                    else if (statusResp.StatusCode == System.Net.HttpStatusCode.NotFound && seenClaimed)
                    {
                        if (++consecutiveNotFound >= 3) { if (last is not null) last.Status = "swept-unknown"; break; }
                    }
                    await Task.Delay(2000);
                }

                var target = targetByUnitId[unit.WorkUnitId];
                var marker = markerByUnitId[unit.WorkUnitId];
                var evidence = BuildJobEvidence(unit.WorkUnitId, target, marker, last);
                report.Jobs.Add(evidence);
                Console.WriteLine($"  [{evidence.Status}] {unit.WorkUnitId} -> target={target} claimedBy={evidence.ClaimedBy ?? "-"} " +
                                   $"runtime={evidence.RuntimeName ?? "-"} validated={evidence.Validated}");
            }

            report.FinishedAt = DateTimeOffset.UtcNow;

            var byTarget = report.Jobs.GroupBy(j => j.TargetWorker);
            report.ValidatedCountByWorker = byTarget.ToDictionary(g => g.Key, g => g.Count(j => j.Validated));
            var anyNonNativeCompletion = report.Jobs.Any(j => j.Status == "completed" &&
                !string.Equals(j.RuntimeName, "NativeRoleRuntime", StringComparison.Ordinal));
            report.FallbackDetected = anyNonNativeCompletion;

            var workerAOk = report.ValidatedCountByWorker.GetValueOrDefault(workerA) >= jobsPerWorker;
            var workerBOk = report.ValidatedCountByWorker.GetValueOrDefault(workerB) >= jobsPerWorker;
            report.Passed = workerAOk && workerBOk && !anyNonNativeCompletion;

            var outPath = Path.Combine(outDir, $"hv1_native_campaign_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"{workerA}: {report.ValidatedCountByWorker.GetValueOrDefault(workerA)}/{jobsPerWorker} validated native jobs");
            Console.WriteLine($"{workerB}: {report.ValidatedCountByWorker.GetValueOrDefault(workerB)}/{jobsPerWorker} validated native jobs");
            Console.WriteLine($"Fallback detected anywhere: {report.FallbackDetected}");
            Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"Evidence written: {outPath}");
            return report.Passed ? 0 : 2;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.FinishedAt = DateTimeOffset.UtcNow;
            var outPath = Path.Combine(outDir, $"hv1_native_campaign_FAILED_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            Console.Error.WriteLine($"Run failed: {ex.Message}");
            Console.Error.WriteLine($"Partial evidence written: {outPath}");
            return 1;
        }
    }

    private static Hv1JobEvidence BuildJobEvidence(
        string workUnitId, string target, string marker, HiveTaskStatusResponse? last)
    {
        var status = last?.Status ?? "unknown";
        var claimedBy = last?.ClaimedBy;
        var attestation = last?.Attestation;
        var result = last?.Result;

        var claimedByExpected = claimedBy is not null &&
            string.Equals(claimedBy, target, StringComparison.OrdinalIgnoreCase);
        var isNativeRuntime = attestation is not null &&
            string.Equals(attestation.RuntimeName, "NativeRoleRuntime", StringComparison.Ordinal) &&
            attestation.Backend.Length > 0;
        var outputHasMarker = !string.IsNullOrEmpty(result) && result.Contains(marker, StringComparison.Ordinal);
        var hasStats = last?.Metrics is { Count: > 0 };

        var validated = status == "completed" && claimedByExpected && isNativeRuntime && outputHasMarker && hasStats;

        return new Hv1JobEvidence
        {
            WorkUnitId = workUnitId,
            TargetWorker = target,
            Status = status,
            ClaimedBy = claimedBy,
            ClaimedByExpected = claimedByExpected,
            RuntimeName = attestation?.RuntimeName,
            Backend = attestation?.Backend,
            ModelHash = attestation?.ModelHash,
            AdapterHash = attestation?.AdapterHash,
            Output = result,
            Stats = last?.Metrics ?? [],
            OutputHasMarker = outputHasMarker,
            ErrorMsg = last?.ErrorMsg,
            Validated = validated,
        };
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

internal sealed class Hv1Report
{
    public string Warchief { get; set; } = "";
    public string ModelHash { get; set; } = "";
    public string ModelHashNote { get; set; } = "";
    public string WorkerA { get; set; } = "";
    public string WorkerB { get; set; } = "";
    public int JobsPerWorkerRequired { get; set; }
    public string CampaignId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<Hv1JobEvidence> Jobs { get; set; } = [];
    public Dictionary<string, int> ValidatedCountByWorker { get; set; } = [];
    public bool FallbackDetected { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
}

/// <summary>Per-job evidence: runtime name, machine identity (ClaimedBy), model/adapter binding,
/// output, and stats -- the exact evidence shape HV-1 requires per docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md.</summary>
internal sealed class Hv1JobEvidence
{
    public string WorkUnitId { get; set; } = "";
    public string TargetWorker { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public bool ClaimedByExpected { get; set; }
    public string? RuntimeName { get; set; }
    public string? Backend { get; set; }
    public string? ModelHash { get; set; }
    public string? AdapterHash { get; set; }
    public string? Output { get; set; }
    public bool OutputHasMarker { get; set; }
    public Dictionary<string, double> Stats { get; set; } = [];
    public string? ErrorMsg { get; set; }
    public bool Validated { get; set; }
}
