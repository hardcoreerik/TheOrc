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
        // ExcludedWorkerIds is the ONLY pinning mechanism these phases have, and it is built by
        // excluding the OTHER configured workers -- so a run configured with a single --worker-a
        // excludes nobody and any live worker in the fleet can claim its jobs. That silently
        // invalidated a targeted run: a phase aimed at HardcoreLaptopMSI had its second work unit
        // claimed by HardcorePC, whose 6 GB card then produced the very timeout the run was
        // trying to attribute to the laptop. The evidence file recorded the intended worker, not
        // the one that actually ran it, so the result looked like a finding rather than a
        // mis-targeted job. Warn loudly rather than fail: single-worker runs are legitimate when
        // the operator has genuinely stopped the other workers, which is exactly what the message
        // tells them to confirm.
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

        if (workers.Count == 1)
        {
            Console.WriteLine(
                $"WARNING: only one worker configured ({workers[0].Id}), so ExcludedWorkerIds is " +
                "empty and NOTHING pins these jobs to it — any other live worker in the fleet can " +
                "claim them, and the evidence file would still record the intended target. " +
                "Confirm every other worker is stopped, or pass the others via --worker-b/-c so " +
                "they are excluded explicitly.");
        }

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
            // ClaimedByExpected is part of the verdict, not just a recorded field. It was already
            // being captured per job and then ignored, so a work unit claimed by a DIFFERENT
            // worker than the phase targeted could still produce a green run whose evidence named
            // the intended machine. That is exactly how a laptop-targeted phase got its second
            // job executed on the 6 GB box and the resulting timeout misread as a laptop result.
            // A phase that cannot prove WHICH machine ran the work proves nothing about "across
            // machines", which is the entire §6 criterion this campaign exists to evidence.
            report.Passed = report.Jobs.Count > 0
                            && report.Jobs.All(j => j.Status == "completed"
                                                    && j.IsNativeRuntime
                                                    && j.ClaimedByExpected)
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
        // The residency role this phase actually exercises, so the conversation-count check below
        // tracks THAT role's own counter rather than whichever role happens to have the largest
        // number resident. Found necessary by running the campaign for real: hv3-concurrent (which
        // loads Researcher) run immediately before this phase in the same HV-6 round left Researcher
        // resident with ConversationsCreated=3, and MAX-across-roles then reported a false flatline
        // ([3,3,3]) for two of three cycles while the Worker role's OWN counter was correctly
        // climbing 1->2->3 underneath it the whole time. Not a product bug -- a driver aggregation
        // bug that happened to be invisible until a second role was already resident.
        var residencyRole = MapToResidencyRoleName(role);

        foreach (var w in workers)
        {
            var samples = new List<Hv3TelemetrySample>();
            samples.Add(await SampleAsync(w.NodeUrl, "before-all", residencyRole));

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
                samples.Add(await SampleAsync(w.NodeUrl, $"after-cycle-{i}", residencyRole));
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
        //
        //    Requires evidence that work actually HAPPENED before it can pass. "ActiveCount is 0"
        //    is trivially true on a worker that never ran anything, and this check reported PASS
        //    on both machines during a run where every job sat unclaimed and the telemetry
        //    endpoint was unreachable -- a green tick on an empty run is worse than no check,
        //    because it reads as evidence. ConversationsCreated > 0 is the liveness proof.
        var workDone = afterCycles.Any(s => s.Reachable && s.MaxConversationsCreated > 0);
        var leaked = afterCycles.Where(s => s.TotalActiveCount > 0).Select(s => s.Stage).ToList();
        report.LifecycleChecks.Add(new Hv3LifecycleCheck
        {
            WorkerId = workerId,
            Name = "residency-returns-to-baseline",
            Passed = afterCycles.Count > 0 && workDone && leaked.Count == 0,
            Detail = afterCycles.Count == 0
                ? "no post-cycle samples captured"
                : !workDone
                    ? "VACUOUS — no conversation was ever created on this worker (unreachable " +
                      "telemetry or unclaimed jobs); residency being zero proves nothing here"
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

        // "Persists" is a claim about the ROLE'S RESERVATION ENTRY, and it has to be measured on
        // that entry -- not on `reservedBytes`.
        //
        // `reservedBytes` is not the ledger. GetReservationSnapshot publishes the MAX of the ledger
        // and a live whole-GPU nvidia-smi probe, and on a card this full the probe wins. Asserting
        // any monotonic property of a live physical measurement is asserting that nothing else on
        // the machine may allocate a byte of VRAM for the duration, which is not what HV-3 claims
        // and not something the runtime controls. It duly failed on a ~27 MB drift
        // (6226577920 -> 6198132736) while every role held its reservation correctly throughout.
        //
        // This is the SECOND false failure from this one check. The first was the `> baseline` form,
        // which only held from a cold worker -- warm HardcorePC failed while a freshly-started
        // laptop passed on identical behavior. Restating it as non-decreasing fixed that case and
        // left this one. The lesson both times is the same: the check drifted onto an aggregate
        // that is convenient to read rather than the quantity under test.
        //
        // What the decoupling actually claims: residency returns to zero between jobs while the
        // role's RESERVATION does not disappear. So: every post-job sample must still show the role
        // holding a reservation, and no role may lose one it held after the previous job. That is
        // exact, immune to warm-vs-cold starting state, and immune to whole-GPU drift.
        //
        // Byte values are still recorded in Detail -- they are evidence worth keeping -- but they
        // are not asserted. Note in particular that a role's reservation legitimately SHRINKS from
        // a full-model charge to an incremental context charge once another role has the base model
        // resident (the shared-base behavior the cross-role admission fix introduced): on this run
        // role 1 went 5589043712 -> 637534208 while the physical footprint stayed at 6.2 GB. The
        // model never left the card; only the ledger's attribution changed.
        var reservedRoleSets = afterCycles.Select(s => s.ReservedRoles.ToHashSet()).ToList();
        var everyGapHoldsAReservation = reservedRoleSets.Count > 0
                                        && reservedRoleSets.All(set => set.Count > 0);
        var noRoleLostItsReservation = reservedRoleSets
            .Zip(reservedRoleSets.Skip(1), (prev, next) => prev.IsSubsetOf(next))
            .All(x => x);

        // A worker that never loaded anything would satisfy the above trivially, so require proof
        // that work actually happened rather than an idle card.
        var residentFootprintSeen = afterCycles.Any(s => s.MaxConversationsCreated > 0);

        report.LifecycleChecks.Add(new Hv3LifecycleCheck
        {
            WorkerId = workerId,
            Name = "reservation-persists-between-jobs",
            Passed = everyGapHoldsAReservation && noRoleLostItsReservation && residentFootprintSeen,
            Detail = beforeAll is null
                ? "no baseline sample captured"
                : $"reserved roles after each cycle=[" +
                  string.Join(" | ", reservedRoleSets.Select(s => string.Join(",", s.OrderBy(r => r)))) +
                  $"]; reservedBytes (recorded, not asserted — live whole-GPU probe) " +
                  $"baseline={beforeAll.ReservedBytes} " +
                  $"({(beforeAll.MaxConversationsCreated > 0 ? "warm worker" : "cold worker")}), " +
                  $"after-cycle=[{string.Join(", ", reservedAfter)}]",
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

    /// <summary>
    /// Mirrors HiveNativeRoleExecutorAdapter.MapHiveRoleToRuntimeRole's tested logic (only
    /// "researcher" maps to Researcher, everything else -- coder, uideveloper, tester, unknown
    /// lanes, null -- maps to Worker) without pulling in the whole Core.Runtime project for one
    /// mapping. If that mapping ever changes, this local copy has to change with it -- same risk
    /// class as CampaignCapabilityMatcher.ExplainIneligibility mirroring IsEligible, accepted for
    /// the same reason: a Tools/ driver project, not a reason to reference production internals.
    /// </summary>
    private static string MapToResidencyRoleName(string? hiveRole)
        => string.Equals(hiveRole, "researcher", StringComparison.OrdinalIgnoreCase) ? "Researcher" : "Worker";

    /// <param name="roleFilter">
    /// When set, MaxConversationsCreated (despite the name, kept for evidence-file compatibility)
    /// is THAT role's own ConversationsCreated rather than the max across every resident role.
    /// Required for the sequential phase: a second role left resident by an earlier phase in the
    /// same campaign round has its own, unrelated counter, and taking the max across roles reports
    /// whichever role's count is currently larger -- which can flatline the metric for the role
    /// actually under test while it is still genuinely climbing underneath. Concurrent's mid-flight
    /// sampling deliberately omits this and wants the true cross-role max, since it is asking
    /// whether TWO roles hold reservations at once.
    /// </param>
    private static async Task<Hv3TelemetrySample> SampleAsync(string nodeUrl, string stage, string? roleFilter = null)
    {
        var t = await TryFetchTelemetryAsync(nodeUrl);
        var residency = t?.Residency ?? [];
        var forMetric = roleFilter is null
            ? residency
            : residency.Where(r => string.Equals(r.Role, roleFilter, StringComparison.OrdinalIgnoreCase)).ToList();
        return new Hv3TelemetrySample
        {
            Stage = stage,
            At = DateTimeOffset.UtcNow,
            Reachable = t is not null,
            ReservedBytes = t?.ReservedBytes ?? -1,
            AvailableBytes = t?.AvailableBytes ?? -1,
            TotalBytes = t?.TotalBytes ?? -1,
            TotalActiveCount = residency.Sum(r => r.ActiveCount),
            MaxConversationsCreated = forMetric.Count > 0 ? forMetric.Max(r => r.ConversationsCreated) : 0,
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
