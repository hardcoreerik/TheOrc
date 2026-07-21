// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Services.Hive;

namespace Hv2SchedulingRunner;

/// <summary>
/// HV-2 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): proves capability/resource-aware
/// scheduling across the fleet. NativeContextSize is a per-worker-process startup config, not a
/// per-job parameter the HIVE contract exposes, so the two HV-2 checks map to two separate fleet
/// configurations of the same three machines rather than two job shapes against one config:
///
///   --phase large: every worker started with a context size whose estimated footprint exceeds
///     the low-VRAM box's budget. That box must deny with a real RuntimeAdmissionDeniedException
///     (correct numbers, observable via GET /hive/native-telemetry's RejectedAdmissionCount/
///     LastRejectionReason) while the higher-VRAM boxes complete normally -- and must never fall
///     back to Ollama.
///   --phase small: every worker started with a context size that fits everywhere. All three
///     must complete -- proving the large-phase denial was footprint-driven, not "that box
///     always fails."
///
/// Run this once per phase against a fleet already reconfigured (NativeContextSize env var) and
/// restarted for that phase.
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
        var outDir = GetArg(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-2-lane");
        var phase = GetArg(args, "--phase")
            ?? throw new InvalidOperationException("--phase large|small is required.");
        if (phase is not ("large" or "small"))
            throw new InvalidOperationException("--phase must be 'large' or 'small'.");
        var role = GetArg(args, "--role") ?? "Coder";
        var timeoutMs = int.TryParse(GetArg(args, "--timeout-ms"), out var t) ? t : 300_000;

        var workers = new List<(string Id, string NodeUrl, bool ExpectDenied)>();
        foreach (var slot in new[] { "a", "b", "c" })
        {
            var id = GetArg(args, $"--worker-{slot}");
            var nodeUrl = GetArg(args, $"--worker-{slot}-node");
            if (id is null || nodeUrl is null) continue;
            var isLowVram = string.Equals(GetArg(args, "--low-vram-worker"), id, StringComparison.OrdinalIgnoreCase);
            workers.Add((id, nodeUrl, isLowVram && phase == "large"));
        }
        if (workers.Count == 0)
            throw new InvalidOperationException(
                "No workers configured. Pass --worker-a <id> --worker-a-node <http://ip:7078> (and -b/-c), " +
                "plus --low-vram-worker <id> to mark which one is expected to deny in --phase large.");

        Directory.CreateDirectory(outDir);
        using var http = new HttpClient { BaseAddress = new Uri(warchief), Timeout = TimeSpan.FromMinutes(10) };

        var report = new Hv2Report { Warchief = warchief, Phase = phase, StartedAt = DateTimeOffset.UtcNow };

        try
        {
            Console.WriteLine($"HV-2 scheduling run, phase={phase}, against {warchief}");

            // Baseline each worker's native telemetry BEFORE dispatch, so a denial's effect on
            // RejectedAdmissionCount can be verified as an increase, not just a nonzero value
            // that might already be nonzero from an earlier run.
            var baselineTelemetry = new Dictionary<string, NativeTelemetry?>();
            foreach (var w in workers)
                baselineTelemetry[w.Id] = await TryFetchTelemetryAsync(w.NodeUrl);

            var otherIds = workers.Select(w => w.Id).ToArray();
            var workUnits = workers.Select(w => new WorkUnit
            {
                WorkUnitId = $"hv2-{phase}-{w.Id}",
                Title = $"HV-2 {phase}-context scheduling proof targeting {w.Id}",
                Role = role,
                ExecutionKind = HiveExecutionKinds.NativeAgent,
                Requirements = new ResourceRequirements
                {
                    ExcludedWorkerIds = otherIds.Where(id => !string.Equals(id, w.Id, StringComparison.OrdinalIgnoreCase)).ToArray(),
                },
                Spec = $"Create a file named hv2_proof.txt in the workspace root containing exactly " +
                       $"this single line and nothing else: HV2-PROOF {phase} {w.Id}",
                TimeoutMs = timeoutMs,
            }).ToList();

            var campaign = new CampaignDefinition { Name = $"hv2-{phase}", WorkUnits = workUnits };
            Console.WriteLine($"Submitting campaign {campaign.CampaignId}: {workUnits.Count} work unit(s)...");
            using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                resp.EnsureSuccessStatusCode();
            report.CampaignId = campaign.CampaignId;

            foreach (var unit in workUnits)
            {
                var target = workers.First(w => unit.WorkUnitId.EndsWith(w.Id, StringComparison.Ordinal));
                var taskId = $"{campaign.CampaignId}-{unit.WorkUnitId}";
                var deadline = DateTime.UtcNow.AddMilliseconds(unit.TimeoutMs + 60_000);
                HiveTaskStatusResponse? last = null;
                var seenClaimed = false;
                var consecutiveNotFound = 0;
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

                var evidence = BuildJobEvidence(unit.WorkUnitId, target.Id, target.ExpectDenied, last);
                report.Jobs.Add(evidence);
                Console.WriteLine($"  [{evidence.Status}] {unit.WorkUnitId} -> worker={target.Id} " +
                                   $"expectDenied={target.ExpectDenied} runtime={evidence.RuntimeName ?? "-"} " +
                                   $"errorMsg={evidence.ErrorMsg ?? "-"} matchesExpectation={evidence.MatchesExpectation}");
            }

            // Post-dispatch telemetry: only meaningful for the worker(s) expected to deny.
            foreach (var w in workers.Where(w => w.ExpectDenied))
            {
                var after = await TryFetchTelemetryAsync(w.NodeUrl);
                var before = baselineTelemetry[w.Id];
                var rejectedDelta = (after?.RejectedAdmissionCount ?? 0) - (before?.RejectedAdmissionCount ?? 0);
                report.TelemetryChecks.Add(new Hv2TelemetryCheck
                {
                    WorkerId = w.Id,
                    NodeUrl = w.NodeUrl,
                    RejectedAdmissionCountBefore = before?.RejectedAdmissionCount,
                    RejectedAdmissionCountAfter = after?.RejectedAdmissionCount,
                    RejectedAdmissionCountIncreased = rejectedDelta > 0,
                    LastRejectionReason = after?.LastRejectionReason,
                });
                Console.WriteLine($"  telemetry[{w.Id}]: rejected {(before is null ? "?" : before.RejectedAdmissionCount.ToString())} -> " +
                                   $"{(after is null ? "?" : after.RejectedAdmissionCount.ToString())}, reason: {after?.LastRejectionReason ?? "(none)"}");
            }

            report.FinishedAt = DateTimeOffset.UtcNow;
            var allJobsMatchedExpectation = report.Jobs.All(j => j.MatchesExpectation);
            var allTelemetryConfirmed = report.TelemetryChecks.All(c => c.RejectedAdmissionCountIncreased);
            report.Passed = allJobsMatchedExpectation && allTelemetryConfirmed;

            var outPath = Path.Combine(outDir, $"hv2_{phase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

            Console.WriteLine();
            Console.WriteLine($"All jobs matched expectation: {allJobsMatchedExpectation}");
            Console.WriteLine($"All expected-denial telemetry confirmed: {allTelemetryConfirmed}");
            Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
            Console.WriteLine($"Evidence written: {outPath}");
            return report.Passed ? 0 : 2;
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.FinishedAt = DateTimeOffset.UtcNow;
            var outPath = Path.Combine(outDir, $"hv2_{phase}_FAILED_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            await File.WriteAllTextAsync(outPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
            Console.Error.WriteLine($"Run failed: {ex.Message}");
            Console.Error.WriteLine($"Partial evidence written: {outPath}");
            return 1;
        }
    }

    private static Hv2JobEvidence BuildJobEvidence(
        string workUnitId, string workerId, bool expectDenied, HiveTaskStatusResponse? last)
    {
        var status = last?.Status ?? "unknown";
        var attestation = last?.Attestation;
        var isNativeRuntime = attestation is not null &&
            string.Equals(attestation.RuntimeName, "NativeRoleRuntime", StringComparison.Ordinal);
        var errorMsg = last?.ErrorMsg;
        // A real, fail-closed denial surfaces as a "failed" task -- but the task-level ErrorMsg
        // the Warchief actually sees is HiveWorkerAgent's generic wrapper text ("native role
        // runtime failed. Phase 3B does not fall back."), NOT the RuntimeAdmissionDeniedException's
        // own detailed message (which only reaches the worker's own local log). Confirmed
        // empirically during HV-2 calibration: a genuine admission denial with correct numbers
        // ("Requires ~6.8 GB, only 5.6 GB available...") still produced this exact generic
        // wrapper as ErrorMsg. So "failed" here (this shape can structurally never fall back
        // instead) is what proves fail-closed; the SEPARATE /hive/native-telemetry check below
        // is what proves it was specifically an admission denial with correct numbers.
        var wasDenied = status == "failed";
        var wasAdmitted = status == "completed" && isNativeRuntime;
        var matchesExpectation = expectDenied ? wasDenied : wasAdmitted;

        return new Hv2JobEvidence
        {
            WorkUnitId = workUnitId,
            WorkerId = workerId,
            ExpectDenied = expectDenied,
            Status = status,
            RuntimeName = attestation?.RuntimeName,
            Backend = attestation?.Backend,
            Stats = last?.Metrics ?? [],
            ErrorMsg = errorMsg,
            WasDenied = wasDenied,
            WasAdmitted = wasAdmitted,
            MatchesExpectation = matchesExpectation,
        };
    }

    private static async Task<NativeTelemetry?> TryFetchTelemetryAsync(string nodeUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await http.GetAsync($"{nodeUrl.TrimEnd('/')}/hive/native-telemetry");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<NativeTelemetry>(JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

internal sealed class NativeTelemetry
{
    public long RejectedAdmissionCount { get; set; }
    public string? LastRejectionReason { get; set; }
    public long TotalBytes { get; set; }
    public long ReservedBytes { get; set; }
    public long AvailableBytes { get; set; }
}

internal sealed class Hv2Report
{
    public string Warchief { get; set; } = "";
    public string Phase { get; set; } = "";
    public string CampaignId { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<Hv2JobEvidence> Jobs { get; set; } = [];
    public List<Hv2TelemetryCheck> TelemetryChecks { get; set; } = [];
    public bool Passed { get; set; }
    public string? Error { get; set; }
}

internal sealed class Hv2JobEvidence
{
    public string WorkUnitId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public bool ExpectDenied { get; set; }
    public string Status { get; set; } = "";
    public string? RuntimeName { get; set; }
    public string? Backend { get; set; }
    public Dictionary<string, double> Stats { get; set; } = [];
    public string? ErrorMsg { get; set; }
    public bool WasDenied { get; set; }
    public bool WasAdmitted { get; set; }
    public bool MatchesExpectation { get; set; }
}

internal sealed class Hv2TelemetryCheck
{
    public string WorkerId { get; set; } = "";
    public string NodeUrl { get; set; } = "";
    public long? RejectedAdmissionCountBefore { get; set; }
    public long? RejectedAdmissionCountAfter { get; set; }
    public bool RejectedAdmissionCountIncreased { get; set; }
    public string? LastRejectionReason { get; set; }
}
