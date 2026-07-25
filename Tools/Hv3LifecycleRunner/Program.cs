// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Services.Hive;

namespace Hv3LifecycleRunner;

/// <summary>
/// HV-3 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): proves model/adapter lifecycle
/// behavior across machines. Same submit -> poll -> evidence-JSON shape as
/// Tools/Hv1NativeCampaignRunner and Tools/Hv2SchedulingRunner.
///
///   --phase sequential: N jobs dispatched to a worker ONE AT A TIME, with a
///     GET /hive/native-telemetry sample taken between each. This is the phase's core
///     assertion and the reason residency had to become remotely observable at all: a role's
///     residency must return to baseline BETWEEN jobs while its VRAM reservation does NOT --
///     the documented decoupling (reservation persists with the loaded model; residency is
///     per-conversation). ConversationsCreated must also strictly increase, which is what
///     distinguishes "a fresh conversation per job" from "one conversation silently reused".
///
///   --phase concurrent: two jobs of DIFFERENT roles dispatched to the SAME worker at once,
///     with telemetry polled throughout. Folds in the deferred Phase D "second concurrent role"
///     increment: cross-role admission accounting proven inside one evidence-bearing run,
///     rather than inferred from two single-role runs.
///
/// NOT covered here, deliberately: the plan's third HV-3 item (force a role recycle remotely
/// via the MarkRoleDegraded path and prove the next job gets a fresh working executor).
/// MarkRoleDegraded is reachable only from the runtime's own NoKvSlot handling -- there is no
/// remote trigger, and adding one is not a telemetry addition but a MUTATION, which needs an
/// authenticated control endpoint and its own security review rather than being smuggled in
/// alongside a read-only campaign driver. Recorded in the evidence as an explicit gap so a
/// green run of this driver cannot be mistaken for full HV-3 coverage.
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
        var outDir = GetArg(args, "--out") ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-3-lane");
        var phase = GetArg(args, "--phase") ?? "sequential";
        if (phase is not ("sequential" or "concurrent"))
            throw new InvalidOperationException("--phase must be 'sequential' or 'concurrent'.");
        var cycles = int.TryParse(GetArg(args, "--cycles"), out var c) && c > 0 ? c : 3;
        var timeoutMs = int.TryParse(GetArg(args, "--timeout-ms"), out var t) ? t : 300_000;
        var primaryRole = GetArg(args, "--role") ?? "Coder";
        var secondaryRole = GetArg(args, "--second-role") ?? "Researcher";

        var workers = new List<(string Id, string NodeUrl)>();
        foreach (var slot in new[] { "a", "b", "c" })
        {
            var id = GetArg(args, $"--worker-{slot}");
            var nodeUrl = GetArg(args, $"--worker-{slot}-node");
            if (id is null || nodeUrl is null) continue;
            workers.Add((id, nodeUrl));
        }
        if (workers.Count == 0)
            throw new InvalidOperationException(
                "No workers configured. Pass --worker-a <id> --worker-a-node <http://ip:7078> (and -b/-c).");

        Directory.CreateDirectory(outDir);

        var report = new Hv3Report
        {
            Warchief = warchief,
            Phase = phase,
            Cycles = cycles,
            StartedAt = DateTimeOffset.UtcNow,
            UncoveredItems =
            [
                "HV-3 item 3 (forced role recycle / MarkRoleDegraded across machines) is NOT " +
                "exercised: no remote trigger exists, and adding one is a mutation needing an " +
                "authenticated control endpoint plus its own security review.",
            ],
        };

        using var http = new HttpClient { BaseAddress = new Uri(warchief), Timeout = TimeSpan.FromMinutes(10) };

        try
        {
            if (phase == "sequential")
                await RunSequentialAsync(http, workers, report, cycles, primaryRole, timeoutMs);
            else
                await RunConcurrentAsync(http, workers, report, primaryRole, secondaryRole, timeoutMs);

            report.FinishedAt = DateTimeOffset.UtcNow;
            report.Passed = report.Jobs.Count > 0
                            && report.Jobs.All(j => j.Status == "completed" && j.IsNativeRuntime)
                            && report.LifecycleChecks.All(l => l.Passed);
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.Passed = false;
        }

        var outPath = Path.Combine(outDir, $"hv3_{phase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

        Console.WriteLine();
        foreach (var l in report.LifecycleChecks)
            Console.WriteLine($"  check[{l.WorkerId}] {l.Name}: {(l.Passed ? "PASS" : "FAIL")} — {l.Detail}");
        Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Evidence written: {outPath}");
        return report.Passed ? 0 : 2;
    }

    // ── Phase: sequential load -> generate -> dispose cycles ────────────────────

    private static async Task RunSequentialAsync(
        HttpClient http,
        List<(string Id, string NodeUrl)> workers,
        Hv3Report report,
        int cycles,
        string role,
        int timeoutMs)
    {
        foreach (var w in workers)
        {
            var samples = new List<Hv3TelemetrySample>();
            samples.Add(await SampleAsync(w.NodeUrl, "before-all"));

            for (var i = 1; i <= cycles; i++)
            {
                // One work unit per campaign, awaited to a terminal state before the next is
                // submitted. Submitting all N up front would let the Warchief overlap them and
                // destroy the very thing this phase measures -- residency between jobs.
                var unitId = $"hv3-seq-{w.Id}-{i:D2}";
                var campaign = new CampaignDefinition
                {
                    Name = $"hv3-sequential-{w.Id}-{i}",
                    WorkUnits =
                    [
                        new WorkUnit
                        {
                            WorkUnitId = unitId,
                            Title = $"HV-3 sequential lifecycle cycle {i} on {w.Id}",
                            Role = role,
                            ExecutionKind = HiveExecutionKinds.NativeAgent,
                            Requirements = new ResourceRequirements
                            {
                                ExcludedWorkerIds = workers
                                    .Where(x => !string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase))
                                    .Select(x => x.Id).ToArray(),
                            },
                            Spec = "Create a file named hv3_proof.txt in the workspace root containing " +
                                   $"exactly this single line and nothing else: HV3-PROOF {w.Id} {i}",
                            TimeoutMs = timeoutMs,
                        },
                    ],
                };

                Console.WriteLine($"Submitting {unitId}...");
                using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                    resp.EnsureSuccessStatusCode();

                var last = await PollToTerminalAsync(http, $"{campaign.CampaignId}-{unitId}", timeoutMs);
                report.Jobs.Add(BuildJobEvidence(unitId, w.Id, role, last));
                samples.Add(await SampleAsync(w.NodeUrl, $"after-cycle-{i}"));
                Console.WriteLine($"  [{last?.Status ?? "unknown"}] {unitId}");
            }

            report.Samples.AddRange(samples.Select(s => s with { WorkerId = w.Id }));
            EvaluateSequential(w.Id, samples, cycles, report);
        }
    }

    /// <summary>
    /// The three assertions HV-3's first item actually makes, evaluated against the samples
    /// taken between jobs rather than asserted in prose.
    /// </summary>
    private static void EvaluateSequential(
        string workerId, List<Hv3TelemetrySample> samples, int cycles, Hv3Report report)
    {
        var afterCycles = samples.Where(s => s.Stage.StartsWith("after-cycle", StringComparison.Ordinal)).ToList();

        // 1. Residency returns to baseline between jobs. Sampled AFTER each job completes, so a
        //    nonzero ActiveCount here means a conversation outlived the job that created it.
        var leaked = afterCycles.Where(s => s.TotalActiveCount > 0).Select(s => s.Stage).ToList();
        report.LifecycleChecks.Add(new Hv3LifecycleCheck
        {
            WorkerId = workerId,
            Name = "residency-returns-to-baseline",
            Passed = afterCycles.Count > 0 && leaked.Count == 0,
            Detail = afterCycles.Count == 0
                ? "no post-cycle samples captured"
                : leaked.Count == 0
                    ? $"ActiveCount back to 0 after all {afterCycles.Count} cycle(s)"
                    : $"ActiveCount still nonzero after: {string.Join(", ", leaked)}",
        });

        // 2. Reservation persists across the gap. The model stays loaded between jobs, so the
        //    reservation must NOT drop back to the pre-run baseline the way residency does --
        //    this is the half of the decoupling that would be invisible if we only checked
        //    that things return to zero.
        var beforeAll = samples.FirstOrDefault(s => s.Stage == "before-all");
        var reservedAfter = afterCycles.Select(s => s.ReservedBytes).ToList();

        // "Persists" means the reservation never DROPS across the gaps between jobs -- it must
        // not be tied to `> baseline`. That stricter form only holds when the run starts against
        // a cold worker; against a worker still warm from a previous run the baseline sample
        // ALREADY includes the resident model, so the correct observation is equality and the
        // strict comparison reports a false failure. Seen exactly that way on the first
        // two-machine run: HardcorePC (warm, baseline 5589043712) failed while
        // HardcoreLaptopMSI (freshly started, baseline 45088768) passed on identical behavior.
        //
        // Non-decreasing across cycles plus at-least-baseline is what the decoupling actually
        // claims, and it holds from either starting state.
        var reservationHeld = beforeAll is not null
                              && reservedAfter.Count > 0
                              && reservedAfter.All(r => r >= beforeAll.ReservedBytes)
                              && reservedAfter.Zip(reservedAfter.Skip(1), (a, b) => b >= a).All(x => x);

        // A worker that never loaded anything would also satisfy "non-decreasing" trivially, so
        // require the reservation to actually reflect a loaded model rather than an idle card.
        var residentFootprintSeen = afterCycles.Any(s => s.MaxConversationsCreated > 0);

        report.LifecycleChecks.Add(new Hv3LifecycleCheck
        {
            WorkerId = workerId,
            Name = "reservation-persists-between-jobs",
            Passed = reservationHeld && residentFootprintSeen,
            Detail = beforeAll is null
                ? "no baseline sample captured"
                : $"baseline reservedBytes={beforeAll.ReservedBytes} " +
                  $"({(beforeAll.MaxConversationsCreated > 0 ? "warm worker" : "cold worker")}), " +
                  $"after-cycle values=[{string.Join(", ", reservedAfter)}]",
        });

        // 3. A fresh conversation per job. Without this, "residency returned to zero" could also
        //    be explained by a single conversation being reused and never counted again.
        var created = afterCycles.Select(s => s.MaxConversationsCreated).ToList();
        var strictlyIncreasing = created.Count == cycles
                                 && created.Zip(created.Skip(1), (a, b) => b > a).All(x => x);
        report.LifecycleChecks.Add(new Hv3LifecycleCheck
        {
            WorkerId = workerId,
            Name = "fresh-conversation-per-job",
            Passed = created.Count > 0 && (cycles == 1 ? created[0] > 0 : strictlyIncreasing),
            Detail = $"ConversationsCreated across cycles=[{string.Join(", ", created)}]",
        });
    }

    // ── Phase: concurrent second role on the same worker ───────────────────────

    private static async Task RunConcurrentAsync(
        HttpClient http,
        List<(string Id, string NodeUrl)> workers,
        Hv3Report report,
        string primaryRole,
        string secondaryRole,
        int timeoutMs)
    {
        foreach (var w in workers)
        {
            var others = workers
                .Where(x => !string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Id).ToArray();

            WorkUnit Unit(string suffix, string role) => new()
            {
                WorkUnitId = $"hv3-conc-{w.Id}-{suffix}",
                Title = $"HV-3 concurrent {role} on {w.Id}",
                Role = role,
                ExecutionKind = HiveExecutionKinds.NativeAgent,
                Requirements = new ResourceRequirements { ExcludedWorkerIds = others },
                Spec = "Create a file named hv3_conc_" + suffix + ".txt in the workspace root " +
                       $"containing exactly this single line and nothing else: HV3-CONC {w.Id} {role}",
                TimeoutMs = timeoutMs,
            };

            var campaign = new CampaignDefinition
            {
                Name = $"hv3-concurrent-{w.Id}",
                WorkUnits = [Unit("primary", primaryRole), Unit("secondary", secondaryRole)],
            };

            Console.WriteLine($"Submitting concurrent pair to {w.Id} ({primaryRole} + {secondaryRole})...");
            using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                resp.EnsureSuccessStatusCode();

            // Poll telemetry while the pair runs, tracking the PEAK rather than the endpoint.
            //
            // What this phase can and cannot prove, established by reading HiveWorkerAgent rather
            // than inferred from a red run: the worker's main loop is
            // `while { PollLease -> await ClaimAndExecute }` -- strictly one task at a time. Two
            // work units dispatched to the same worker therefore execute SERIALLY, and no amount
            // of polling will ever catch two roles with ActiveCount > 0 simultaneously. That is an
            // architectural property of the dispatch layer, not a scheduling or admission defect
            // (same class as HV-2's finding that a Daemon-hosted Warchief cannot approve pairing).
            //
            // What HV-3 actually asks for here is cross-role ADMISSION ACCOUNTING, and that is
            // observable: a reservation persists while the model stays loaded, outliving the
            // conversation that created it (the documented decoupling this whole phase is built
            // on). So two roles genuinely do hold reservations concurrently against one live
            // budget, and that -- not simultaneous execution -- is the claim under test.
            using var pollCts = new CancellationTokenSource();
            var peakReserved = 0;
            var peakReservedRoles = Array.Empty<int>();
            var peakResident = 0;
            var reservedEverExceededTotal = false;
            var pollTask = Task.Run(async () =>
            {
                while (!pollCts.IsCancellationRequested)
                {
                    var s = await SampleAsync(w.NodeUrl, "mid-flight");
                    if (s.ReservedRoles.Length > peakReserved)
                    {
                        peakReserved = s.ReservedRoles.Length;
                        peakReservedRoles = s.ReservedRoles;
                    }
                    if (s.ResidentRoles.Length > peakResident) peakResident = s.ResidentRoles.Length;
                    // The defect this phase originally exposed published reserved > total. Watch
                    // for it throughout rather than only at the end, so a transient impossible
                    // reading cannot slip past between samples.
                    if (s.Reachable && s.TotalBytes > 0 && s.ReservedBytes > s.TotalBytes)
                        reservedEverExceededTotal = true;
                    report.Samples.Add(s with { WorkerId = w.Id });
                    try { await Task.Delay(1500, pollCts.Token); } catch (OperationCanceledException) { break; }
                }
            });

            foreach (var unit in campaign.WorkUnits)
            {
                var last = await PollToTerminalAsync(http, $"{campaign.CampaignId}-{unit.WorkUnitId}", timeoutMs);
                report.Jobs.Add(BuildJobEvidence(unit.WorkUnitId, w.Id, unit.Role, last));
                Console.WriteLine($"  [{last?.Status ?? "unknown"}] {unit.WorkUnitId} role={unit.Role}");
            }

            await pollCts.CancelAsync();
            await pollTask;

            report.LifecycleChecks.Add(new Hv3LifecycleCheck
            {
                WorkerId = w.Id,
                Name = "two-roles-hold-reservations-concurrently",
                Passed = peakReserved >= 2,
                Detail = $"peak concurrent role reservations={peakReserved} " +
                         $"[roles {string.Join(", ", peakReservedRoles)}]; " +
                         $"peak simultaneously-resident roles={peakResident} " +
                         "(1 is expected and correct — HiveWorkerAgent executes one task at a time)",
            });

            report.LifecycleChecks.Add(new Hv3LifecycleCheck
            {
                WorkerId = w.Id,
                Name = "cross-role-accounting-stays-physical",
                Passed = !reservedEverExceededTotal,
                Detail = reservedEverExceededTotal
                    ? "reservedBytes exceeded totalBytes at least once — the cross-role double-count is back"
                    : "reservedBytes stayed within totalBytes across every sample",
            });
        }
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static async Task<HiveTaskStatusResponse?> PollToTerminalAsync(
        HttpClient http, string taskId, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 60_000);
        HiveTaskStatusResponse? last = null;
        var seenClaimed = false;
        var consecutiveNotFound = 0;

        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http.GetAsync($"/hive/tasks/{taskId}");
            if (resp.IsSuccessStatusCode)
            {
                consecutiveNotFound = 0;
                var body = await resp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions);
                if (body is not null)
                {
                    last = body;
                    if (body.Status is "claimed" or "running") seenClaimed = true;
                    if (body.Status is "completed" or "failed" or "timeout" or "cancelled") break;
                }
            }
            else if (resp.StatusCode == System.Net.HttpStatusCode.NotFound && seenClaimed)
            {
                // Same swept-task tolerance as the HV-2 driver: a completed task can be pruned
                // from the queue between polls, and that must not read as an infra failure.
                if (++consecutiveNotFound >= 3)
                {
                    if (last is not null) last.Status = "swept-unknown";
                    break;
                }
            }
            await Task.Delay(2000);
        }
        return last;
    }

    private static Hv3JobEvidence BuildJobEvidence(
        string unitId, string workerId, string role, HiveTaskStatusResponse? last)
    {
        var attestation = last?.Attestation;
        return new Hv3JobEvidence
        {
            WorkUnitId = unitId,
            WorkerId = workerId,
            Role = role,
            Status = last?.Status ?? "unknown",
            ClaimedBy = last?.ClaimedBy,
            ClaimedByExpected = string.Equals(last?.ClaimedBy, workerId, StringComparison.OrdinalIgnoreCase),
            RuntimeName = attestation?.RuntimeName,
            IsNativeRuntime = attestation?.RuntimeName == "NativeRoleRuntime",
            Stats = last?.Metrics ?? [],
            ErrorMsg = last?.ErrorMsg,
        };
    }

    private static async Task<Hv3TelemetrySample> SampleAsync(string nodeUrl, string stage)
    {
        var t = await TryFetchTelemetryAsync(nodeUrl);
        var residency = t?.Residency ?? [];
        return new Hv3TelemetrySample
        {
            Stage = stage,
            At = DateTimeOffset.UtcNow,
            Reachable = t is not null,
            ReservedBytes = t?.ReservedBytes ?? -1,
            AvailableBytes = t?.AvailableBytes ?? -1,
            TotalBytes = t?.TotalBytes ?? -1,
            TotalActiveCount = residency.Sum(r => r.ActiveCount),
            MaxConversationsCreated = residency.Count > 0 ? residency.Max(r => r.ConversationsCreated) : 0,
            ResidentRoles = residency.Where(r => r.ActiveCount > 0).Select(r => r.Role).ToArray(),
            ReservedRoles = (t?.Reservations ?? []).Select(r => r.Role).ToArray(),
            Residency = residency,
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
            // Unreachable telemetry is recorded as Reachable=false rather than thrown: HV-2 hit a
            // real firewall block on one box, and losing the whole run to it would have discarded
            // valid job evidence alongside the missing samples.
            return null;
        }
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

// ── DTOs ───────────────────────────────────────────────────────────────────────

internal sealed class NativeTelemetry
{
    public long RejectedAdmissionCount { get; set; }
    public string? LastRejectionReason { get; set; }
    public long TotalBytes { get; set; }
    public long ReservedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public List<ReservationEntry> Reservations { get; set; } = [];
    public List<ResidencyEntry> Residency { get; set; } = [];
}

internal sealed class ReservationEntry
{
    // Serialized as the RuntimeRole enum's numeric value by the worker's telemetry endpoint.
    public int Role { get; set; }
    public long Bytes { get; set; }
}

internal sealed class ResidencyEntry
{
    public string Role { get; set; } = "";
    public string? BaseModel { get; set; }
    public string? Adapter { get; set; }
    public int ActiveCount { get; set; }
    public int ConversationsCreated { get; set; }
    public string Status { get; set; } = "";
}

internal sealed record Hv3TelemetrySample
{
    public string WorkerId { get; init; } = "";
    public string Stage { get; init; } = "";
    public DateTimeOffset At { get; init; }
    public bool Reachable { get; init; }
    public long ReservedBytes { get; init; }
    public long AvailableBytes { get; init; }
    public long TotalBytes { get; init; }
    public int TotalActiveCount { get; init; }
    public int MaxConversationsCreated { get; init; }
    public string[] ResidentRoles { get; init; } = [];
    public int[] ReservedRoles { get; init; } = [];
    public List<ResidencyEntry> Residency { get; init; } = [];
}

internal sealed class Hv3Report
{
    public string Warchief { get; set; } = "";
    public string Phase { get; set; } = "";
    public int Cycles { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public List<Hv3JobEvidence> Jobs { get; set; } = [];
    public List<Hv3TelemetrySample> Samples { get; set; } = [];
    public List<Hv3LifecycleCheck> LifecycleChecks { get; set; } = [];
    public List<string> UncoveredItems { get; set; } = [];
    public bool Passed { get; set; }
    public string? Error { get; set; }
}

internal sealed class Hv3JobEvidence
{
    public string WorkUnitId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public bool ClaimedByExpected { get; set; }
    public string? RuntimeName { get; set; }
    public bool IsNativeRuntime { get; set; }
    public Dictionary<string, double> Stats { get; set; } = [];
    public string? ErrorMsg { get; set; }
}

internal sealed class Hv3LifecycleCheck
{
    public string WorkerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}
