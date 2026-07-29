// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Services.Hive;

namespace Hv4RecoveryRunner;

/// <summary>
/// HV-4 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): failure, cancellation, disconnect
/// and recovery, exercised on REAL jobs mid-flight against real machines. Same
/// submit -> poll -> evidence-JSON shape as the HV-1/HV-2/HV-3 runners, so HV-6 can drive all of
/// them the same way.
///
/// Every phase asserts the same two things beyond its own subject, because they are what §6
/// actually rides on:
///   * the failure is VISIBLE -- the job reaches a terminal state the Warchief reports, rather
///     than hanging or silently vanishing;
///   * there is NO Ollama substitution -- any job that completes did so on NativeRoleRuntime.
///
///   --phase kill        worker process killed mid-job -> job fails visibly, worker restarts,
///                       rejoins, and serves a NEW job successfully.
///   --phase disconnect  worker's link to the Warchief blocked mid-job -> same visibility, then
///                       unblocked and proven to recover.
///   --phase ollama      Ollama stopped on the worker -> a native-routed job still runs natively.
///   --phase cancel      campaign cancelled mid-job -> the task is visibly cancelled and the role
///                       is still usable afterwards.
///   --phase all         all four, in that order.
///
/// SCOPE LIMIT, recorded in every evidence file rather than left implicit: the plan's item 1 asks
/// for cancellation to surface mid-GENERATION as an OperationCanceledException on the worker.
/// There is no remote trigger for that. The worker's only inbound listener is HiveNodeServer
/// (pair / info / native-telemetry / mesh / update) and it has no task-cancel endpoint, so
/// cancelling a campaign marks the task cancelled on the WARCHIEF while the worker keeps
/// generating to completion. The `cancel` phase therefore proves the Warchief-side half and role
/// reusability, and nothing more. Adding the missing endpoint is a MUTATION needing authentication
/// and its own security review -- the same call already made for HV-3's MarkRoleDegraded item, and
/// it should not be smuggled in alongside a campaign driver.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Stale as of the workercancel phase (grok-review, PR #95): the worker DOES now expose a
    // task-cancel endpoint and item 1 IS exercised when that phase runs. Kept, but only stamped
    // onto reports for phases that genuinely don't exercise it -- see its one call site.
    private const string RemoteCancelGap =
        "Item 1 (cancellation surfacing mid-generation as OperationCanceledException on the worker) " +
        "is not exercised by this phase: only --phase workercancel (or all) drives the real " +
        "/hive/tasks/cancel endpoint against an in-flight generation; this phase's own cancel " +
        "check (if any) covers only the Warchief-side campaign-cancel path.";

    public static async Task<int> Main(string[] args)
    {
        var warchief = GetArg(args, "--warchief") ?? "http://localhost:7079";
        var outDir = GetArg(args, "--out")
                     ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-4-lane");
        var phase = GetArg(args, "--phase") ?? "all";
        if (phase is not ("kill" or "disconnect" or "ollama" or "cancel" or "workercancel" or "all"))
            throw new InvalidOperationException(
                "--phase must be one of: kill, disconnect, ollama, cancel, workercancel, all.");

        // Only workercancel needs to sign a request AS the Warchief directly to a worker (every
        // other phase only talks to the Warchief's own task queue, which needs no signing).
        // Idempotent, so unconditional init here costs nothing for phases that never use it.
        SecretProtection.Initialize(new DpapiSecretProtector());
        var timeoutMs = int.TryParse(GetArg(args, "--timeout-ms"), out var t) ? t : 300_000;
        var role = GetArg(args, "--role") ?? "Researcher";
        var warchiefPort = int.TryParse(GetArg(args, "--warchief-port"), out var wp) ? wp : 7079;

        var workers = new List<WorkerTarget>();
        foreach (var slot in new[] { "a", "b", "c" })
        {
            var id = GetArg(args, $"--worker-{slot}");
            var nodeUrl = GetArg(args, $"--worker-{slot}-node");
            if (id is null || nodeUrl is null) continue;
            workers.Add(new WorkerTarget
            {
                Id = id,
                NodeUrl = nodeUrl,
                SshHost = GetArg(args, $"--worker-{slot}-ssh"),
                TaskName = GetArg(args, $"--worker-{slot}-task"),
            });
        }
        if (workers.Count == 0)
            throw new InvalidOperationException(
                "No workers configured. Pass --worker-a <id> --worker-a-node <http://ip:7078> " +
                "--worker-a-ssh <host> --worker-a-task <scheduled-task> (and -b/-c).");

        // Same warning as the HV-3 driver, for the same reason: ExcludedWorkerIds is built by
        // excluding the OTHER configured workers, so a single-worker run pins nothing and any live
        // worker in the fleet can claim the jobs. Here it would be worse than a mis-attributed
        // result -- the kill and disconnect phases would take down a machine that is not the one
        // the evidence names.
        if (workers.Count == 1)
            Console.WriteLine(
                $"WARNING: only one worker configured ({workers[0].Id}), so ExcludedWorkerIds is " +
                "empty and NOTHING pins these jobs to it. Confirm every other worker is stopped, " +
                "or pass the others via --worker-b/-c so they are excluded explicitly.");

        Directory.CreateDirectory(outDir);

        var report = new Hv4Report
        {
            Warchief = warchief,
            Phase = phase,
            StartedAt = DateTimeOffset.UtcNow,
            // "all" and "workercancel" both genuinely drive the real cancel endpoint against an
            // in-flight generation (RunWorkerCancelAsync); every other phase selection does not.
            UncoveredItems = phase is "workercancel" or "all" ? [] : [RemoteCancelGap],
        };

        using var http = new HttpClient
        {
            BaseAddress = new Uri(warchief),
            Timeout = TimeSpan.FromMinutes(10),
        };

        try
        {
            // --target narrows which workers are EXERCISED without narrowing which are EXCLUDED.
            // Those are different things: every configured worker must stay in the exclusion list
            // or the jobs stop being pinned, but a first validation run (or a box that is
            // temporarily off-limits) should be able to disrupt just one machine. Dropping a worker
            // from --worker-* instead would silently unpin the jobs and let the phase kill a
            // machine the evidence does not name.
            var targetIds = GetArg(args, "--target")?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var targets = targetIds is null
                ? workers
                : workers.Where(x => targetIds.Contains(x.Id)).ToList();
            if (targets.Count == 0)
                throw new InvalidOperationException(
                    $"--target matched none of the configured workers ({string.Join(", ", workers.Select(x => x.Id))}).");
            report.TargetedWorkers = targets.Select(x => x.Id).ToList();

            foreach (var w in targets)
            {
                if (phase is "kill" or "all") await RunKillAsync(http, workers, w, report, role, timeoutMs);
                if (phase is "disconnect" or "all")
                    await RunDisconnectAsync(http, workers, w, report, role, timeoutMs, warchiefPort);
                if (phase is "ollama" or "all") await RunOllamaAbsentAsync(http, workers, w, report, role, timeoutMs);
                if (phase is "cancel" or "all") await RunCancelAsync(http, workers, w, report, role, timeoutMs);
                if (phase is "workercancel" or "all")
                    await RunWorkerCancelAsync(http, workers, w, report, role, timeoutMs);
            }

            report.FinishedAt = DateTimeOffset.UtcNow;
            report.Passed = report.Checks.Count > 0 && report.Checks.All(c => c.Passed);
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.Passed = false;
        }

        var outPath = Path.Combine(outDir, $"hv4_{phase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

        Console.WriteLine();
        foreach (var c in report.Checks)
            Console.WriteLine($"  check[{c.WorkerId}] {c.Name}: {(c.Passed ? "PASS" : "FAIL")} — {c.Detail}");
        Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Evidence written: {outPath}");
        return report.Passed ? 0 : 2;
    }

    // ── Phase: worker process killed mid-job ───────────────────────────────────

    private static async Task RunKillAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, int timeoutMs)
    {
        Console.WriteLine($"[kill] {w.Id}: submitting a long job, then killing the worker mid-flight...");
        RequireSsh(w, "kill");

        // Pre-warm the ssh connection NOW, before the job exists, while the box is still idle --
        // see the SSH connection multiplexing section above SshOnce. Every ssh call in this phase
        // reuses it automatically via ControlMaster=auto; a failure here just means those calls
        // fall back to their own ordinary connections, same as always.
        var sshWarm = await WarmSshConnectionAsync(w.SshHost!);
        Console.WriteLine($"[kill] {w.Id}: ssh connection pre-warm {(sshWarm ? "succeeded" : "failed — calls will connect individually")}");

        var (campaignId, taskId) = await SubmitAsync(http, all, w, role, "kill", LongSpec, timeoutMs);

        // Wait for the job to be genuinely IN FLIGHT before killing. Killing a worker that has not
        // claimed yet proves nothing about mid-job death -- it is just a restart.
        var claimed = await WaitForClaimAsync(http, taskId, w.Id, TimeSpan.FromMinutes(3));
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "kill/job-was-in-flight-before-kill",
            Passed = claimed,
            Detail = claimed
                ? $"task {taskId} observed claimed by {w.Id} before the kill"
                : "task never reached claimed on the target worker — the kill would prove nothing, "
                  + "so the rest of this phase's result is not meaningful",
        });
        if (!claimed) return;

        await Ssh(w.SshHost!,
            "powershell -NoProfile -Command \"Get-Process theorc-warband -ErrorAction SilentlyContinue " +
            "| Stop-Process -Force\"");
        // Prove the disruption LANDED before judging anything by it. On one run the ssh kill
        // returned nothing, the laptop's worker kept running, its job completed normally, and the
        // visibility check below scored that as a pass -- "reached a terminal state" is trivially
        // true of a job that was never disrupted. A phase that can go green without doing the thing
        // it is named after is worse than no phase.
        //
        // Confirmed by the worker's OWN endpoint going dark rather than by an ssh process count.
        // Two reasons: a remote `Get-Process | .Count` came back as an empty string rather than "0"
        // from inside this driver (it works by hand, so something in the ssh/argument path eats it,
        // and a check that cannot distinguish "0" from "no answer" is not a check); and more
        // importantly, telemetry silence is the stronger claim anyway -- it says the worker is not
        // SERVING, which is what the phase is about, rather than that a process table entry is gone.
        var killLanded = await WaitForTelemetryGoneAsync(w.NodeUrl, TimeSpan.FromSeconds(60));
        Console.WriteLine($"[kill] {w.Id}: worker telemetry {(killLanded ? "went dark" : "still answering")}");
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "kill/kill-actually-landed",
            Passed = killLanded,
            Detail = killLanded
                ? "worker stopped answering /hive/native-telemetry within 60s of the kill"
                : "worker still answering /hive/native-telemetry 60s after the kill — nothing was "
                  + "disrupted, so every later check in this phase would be vacuous",
        });
        if (!killLanded) return;

        // From here on, the worker process is DEFINITELY dead (killLanded confirmed it) and MUST be
        // restarted before this method returns, no matter which branch below is taken. The race
        // check that follows can legitimately end this phase's EVIDENCE early — but ending the
        // METHOD early must never mean leaving a worker this driver just killed sitting dead for
        // every later lane and round to trip over.
        //
        // That is exactly what happened in the first HV-6 retry: R2's kill raced (the job had
        // already completed when the kill landed), the race check correctly recorded that and
        // returned — and the restart, which used to sit after this whole block, never ran. R2's
        // disconnect and R3's kill then failed against a worker this driver itself had killed and
        // abandoned, which read as cascading fleet instability but was self-inflicted.
        //
        // Restart is idempotent via `restartAttempted` and called from TWO places: inline once the
        // happy path knows it needs a live worker before polling for recovery, and from `finally` to
        // cover the early-return race path. It must never run twice — the second attempt found here
        // during review, before this comment was written, called Ssh() again pointlessly and would
        // have logged a duplicate "kill/worker-rejoins" check.
        var restartAttempted = false;
        var rejoined = false;

        async Task EnsureRestartedAsync()
        {
            if (restartAttempted) return;
            restartAttempted = true;

            Console.WriteLine($"[kill] {w.Id}: restarting via scheduled task {w.TaskName}...");
            // Stop THEN start. Killing the daemon leaves the scheduled task's own state ambiguous —
            // it can still report Running for the cmd wrapper — and Start-ScheduledTask on a task
            // the scheduler already considers running is a no-op. The worker would then never come
            // back and the phase would blame recovery for what was really a scheduler-state quirk.
            await Ssh(w.SshHost!,
                $"powershell -NoProfile -Command \"Stop-ScheduledTask -TaskName {w.TaskName} " +
                $"-ErrorAction SilentlyContinue; Start-Sleep -Seconds 2; " +
                $"Start-ScheduledTask -TaskName {w.TaskName}\"");
            rejoined = await WaitForTelemetryAsync(w.NodeUrl, TimeSpan.FromMinutes(3));
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "kill/worker-rejoins",
                Passed = rejoined,
                Detail = rejoined
                    ? "worker answered /hive/native-telemetry again after restart"
                    : "worker never came back within 3 minutes",
            });
        }

        try
        {
            // The kill has to land while the job is STILL RUNNING. HardcoreLaptopMSI finishes these
            // jobs fast enough to beat the ssh round trip: in HV-6 round 1 the kill landed (telemetry
            // went dark) against a job that had ALREADY completed, and the phase then reported "the
            // Warchief never noticed the death" about a death that happened after there was anything
            // left to notice.
            using var stillResp = await http.GetAsync($"/hive/tasks/{taskId}");
            var stillBody = stillResp.IsSuccessStatusCode
                ? await stillResp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions)
                : null;
            var stillRunning = stillBody?.Status is "claimed" or "running";
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "kill/job-still-running-when-kill-landed",
                Passed = stillRunning,
                Detail = stillRunning
                    ? $"task was still {stillBody!.Status} when the worker died"
                    : $"task had already reached {stillBody?.Status ?? "unknown"} before the kill "
                      + "landed — the job outran the disruption, so this run proves nothing about "
                      + "death detection (use a longer job or a slower box)",
            });
            if (!stillRunning) return;

            // The whole point: the Warchief must NOTICE and report, rather than leaving the job
            // claimed forever. This is the exact behaviour the HV-3 investigation kept
            // mis-attributing — a healthy worker being declared dead. Here the worker really IS
            // dead, so the same watchdog firing is the CORRECT outcome.
            //
            // "Visible" is deliberately NOT "terminal". A killed worker's job is re-queued to
            // pending with its attempt advanced, and it stays there because attempts only advance
            // when a worker claims and then goes silent — with the box down, nobody claims.
            // Demanding a terminal status here would fail the CORRECT behaviour (the work is
            // retryable and was not lost) and would only ever pass by waiting out a timeout. What
            // death visibility actually means is that the job stops being attributed to the dead
            // worker, promptly, and the Warchief says so: "heartbeat timeout from <worker> —
            // re-queued (attempt N)".
            var (leftClaimed, afterDeath) = await WaitForLeavesClaimedAsync(
                http, taskId, w.Id, TimeSpan.FromMinutes(3));
            // "completed" is explicitly NOT a pass here even though it is terminal: a job that
            // finished was not disrupted, so it evidences nothing about death detection. The
            // kill-actually-landed gate above already catches the common case, but a kill that
            // lands in the last second of a job would otherwise slip through as a green tick.
            var deathVisible = leftClaimed && afterDeath?.Status != "completed";
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "kill/death-is-visible-not-silent",
                Passed = deathVisible,
                Detail = deathVisible
                    ? $"job stopped being attributed to the dead worker within 3 min: "
                      + $"status={afterDeath?.Status ?? "none"}, "
                      + $"claimedBy={afterDeath?.ClaimedBy ?? "none"}"
                    : $"job still claimed by the dead worker after 3 min (status={afterDeath?.Status ?? "none"}) "
                      + "— the Warchief never noticed the death",
            });
            report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "kill", afterDeath));

            // Restart HERE, before polling for recovery. The re-queued unit is pinned to this worker
            // via ExcludedWorkerIds, so nobody can claim it until this worker is alive again — the
            // previous shape polled for recovery from inside this try block and restarted afterwards
            // in a separate `finally`, which meant the recovery poll was always racing a worker that
            // could not possibly answer yet. Caught by HardcorePC's own kill lane going red for the
            // first time all day, on exactly this check, immediately after that restructuring landed.
            await EnsureRestartedAsync();

            if (rejoined)
            {
                // The re-queued work must actually be RECOVERED, not merely re-queued. This is the
                // half that proves the lease/queue behaviour was correct across the death: the same
                // work unit, on its next attempt, reaches a terminal state on the restarted worker.
                var recovered = await PollToTerminalAsync(http, taskId, timeoutMs);
                report.Checks.Add(new Hv4Check
                {
                    WorkerId = w.Id,
                    Name = "kill/requeued-work-is-recovered",
                    Passed = recovered?.Status is "completed" or "failed",
                    Detail = $"the SAME work unit reached status={recovered?.Status ?? "none"} after "
                             + $"the restart (claimedBy={recovered?.ClaimedBy ?? "none"}, "
                             + $"runtime={recovered?.Attestation?.RuntimeName ?? "none"})",
                });
                report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "kill-recovered", recovered));
            }
            else
            {
                // Don't let this check silently disappear from the evidence just because the
                // worker never came back — that would read as "not applicable" when it is actually
                // the clearest possible failure of it.
                report.Checks.Add(new Hv4Check
                {
                    WorkerId = w.Id,
                    Name = "kill/requeued-work-is-recovered",
                    Passed = false,
                    Detail = "worker never rejoined after the restart, so the re-queued unit could "
                             + "not be recovered",
                });
            }
        }
        finally
        {
            // Covers only the early-return race path above; EnsureRestartedAsync is a no-op if the
            // happy path already ran it.
            await EnsureRestartedAsync();
        }

        // Prove the role is usable again REGARDLESS of how the race check above came out: even when
        // the original job's death was never observably disruptive, this driver still owes the fleet
        // a live, working worker before moving on to the next lane.
        if (rejoined)
            await ProveServesANewJobAsync(http, all, w, report, role, "kill", timeoutMs);

        // Explicit close on the normal-completion path only. Earlier returns (claim failed, kill
        // never landed) leave the warm connection to expire on its own via ControlPersist=600 --
        // acceptable per WarmSshConnectionAsync's own docs, and simpler than threading an outer
        // try/finally through the delicate restart-guarantee logic above it.
        await CloseSshConnectionAsync(w.SshHost!);
    }

    // ── Phase: worker's link to the Warchief blocked mid-job ───────────────────

    private static async Task RunDisconnectAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, int timeoutMs, int warchiefPort)
    {
        Console.WriteLine($"[disconnect] {w.Id}: submitting a long job, then cutting its link mid-flight...");
        RequireSsh(w, "disconnect");

        // Pre-warm the ssh connection NOW, before the job exists, while the box is still idle. This
        // is the actual fix for the gap the controlled sleep experiment ruled everything else out
        // for: the firewall-rule ssh call needs a fresh, CPU-bound handshake, and that handshake was
        // competing directly with the induced job for the same cycles at exactly the moment this
        // phase needed it least. Paying that cost up front, while idle, and reusing the connection
        // via ControlMaster=auto in every subsequent Ssh() call removes the competition entirely.
        var sshWarm = await WarmSshConnectionAsync(w.SshHost!);
        Console.WriteLine($"[disconnect] {w.Id}: ssh connection pre-warm {(sshWarm ? "succeeded" : "failed — calls will connect individually")}");

        const string ruleName = "TheOrc-HV4-Disconnect";
        var (campaignId, taskId) = await SubmitAsync(http, all, w, role, "disconnect", LongSpec, timeoutMs);

        var claimed = await WaitForClaimAsync(http, taskId, w.Id, TimeSpan.FromMinutes(3));
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "disconnect/job-was-in-flight-before-cut",
            Passed = claimed,
            Detail = claimed
                ? $"task {taskId} observed claimed by {w.Id} before the cut"
                : "task never reached claimed on the target worker — the cut would prove nothing",
        });
        if (!claimed) return;

        // Block only the OUTBOUND path to the Warchief's queue port, not the whole link: the
        // worker's own /hive/native-telemetry must stay reachable so this driver can still observe
        // it, and a full network drop would also cut the SSH session used to undo the rule.
        try
        {
            // Create AND verify in ONE pipe-free call. Two separate ssh round trips gave this box
            // time to drop the connection between them (its sshd goes unreachable for minutes
            // while the machine itself stays healthy), and the `| Out-Null` in the original create
            // did not survive the ssh → cmd → powershell quoting chain intact: the create reported
            // success and left no rule behind, three runs running, while the identical command
            // typed by hand worked. Redirect to $null instead of piping, sleep so the new rule is
            // visible to Get-NetFirewallRule, and make the count the command's ONLY output so
            // there is nothing left to misparse.
            // Ssh() already trims trailing whitespace off what it returns (up to the completion
            // marker), so no further .Trim() is needed here.
            // A `for` loop with braces/semicolons was tried here to shrink this from a flat 3s sleep
            // to a short poll, and it broke 3/3 -- empty string every time, not intermittently,
            // which means the loop's syntax did not survive the ssh -> cmd -> powershell quoting
            // chain intact. That is the exact class of failure the comment below already warns
            // about for the New-NetFirewallRule call itself; a multi-statement for-loop is more of
            // the same risk, not less. The 3s->1s sleep cut still wasn't enough: a full HV-6 run on
            // a confirmed-idle fleet showed this ssh call itself failing outright (empty read-back)
            // 3/3 times specifically while the induced job ran under load, while every OTHER ssh
            // call in the same 75-minute run (cancel, ollama, kill) succeeded -- isolated by hand,
            // outside the campaign, to NOT be a scripting bug (the exact command with the 1s sleep
            // reproduced clean, twice) -- so the sleep itself is dead weight on the one thing that
            // actually matters here: how long this ssh call takes to complete while sshd is
            // CPU-starved. New-NetFirewallRule via the NetSecurity module commits synchronously
            // within its OWN PowerShell session -- Get-NetFirewallRule in the SAME session (no
            // second ssh connection, so none of the cross-connection risk the comment above warns
            // about) should see it with zero added delay. Removed rather than shortened further.
            // Fourth lever, after sleep duration (x2) and SSH transport multiplexing were each
            // ruled out by controlled experiment (see the doc history above): OS SCHEDULING
            // PRIORITY. Neither of the first two touched what actually competes for CPU once this
            // process is alive and running -- the induced job's native inference holds normal
            // priority same as this remote PowerShell process, so the scheduler has no reason to
            // prefer one over the other. Raising THIS process's own priority as its first statement
            // is a real, standard, bounded technique for exactly this shape of problem (a
            // short-lived administrative task that must stay responsive opposite a CPU-heavy
            // workload) -- verified by hand against the idle box first. It does not address time
            // spent BEFORE this process starts running (process creation itself), only time spent
            // after -- if the bottleneck is spawn latency rather than in-process contention, this
            // will not help, and that would itself be useful, narrower information.
            var ruleCount = await Ssh(w.SshHost!,
                "powershell -NoProfile -Command \"(Get-Process -Id $PID).PriorityClass = 'High'; " +
                $"New-NetFirewallRule -DisplayName '{ruleName}' " +
                $"-Direction Outbound -Action Block -Protocol TCP -RemotePort {warchiefPort} " +
                "-ErrorAction SilentlyContinue > $null; " +
                $"@(Get-NetFirewallRule -DisplayName '{ruleName}' -ErrorAction SilentlyContinue).Count\"");
            Console.WriteLine($"[disconnect] {w.Id}: block rule count = '{ruleCount}'");
            // Same landed-gate discipline as the kill phase, and for the same reason: the ssh that
            // creates the rule can silently not land (HardcoreLaptopMSI's sshd drops out for
            // minutes at a time), the job then rides straight through an uncut link and finishes,
            // and the phase reports on a disruption that never happened. Verify the rule EXISTS
            // before believing anything measured after it.
            var cutLanded = ruleCount is "1";
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "disconnect/cut-actually-landed",
                Passed = cutLanded,
                Detail = cutLanded
                    ? $"outbound TCP/{warchiefPort} block rule '{ruleName}' is present on the worker"
                    : $"block rule not present (Get-NetFirewallRule returned '{ruleCount}') — the "
                      + "link was never cut, so every later check in this phase would be vacuous",
            });
            if (!cutLanded) return;

            // The cut has to land while the job is STILL RUNNING. Applying it takes ~10s of wall
            // clock, and a fast box can finish inside that window -- which is exactly what happened
            // on HardcoreLaptopMSI (job done in 14.5s, link cut afterwards, nothing disrupted).
            // Without this the phase would go on to "measure" an undisturbed run.
            using (var stillResp = await http.GetAsync($"/hive/tasks/{taskId}"))
            {
                var stillBody = stillResp.IsSuccessStatusCode
                    ? await stillResp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions)
                    : null;
                var stillRunning = stillBody?.Status is "claimed" or "running";
                report.Checks.Add(new Hv4Check
                {
                    WorkerId = w.Id,
                    Name = "disconnect/job-still-running-when-cut-landed",
                    Passed = stillRunning,
                    Detail = stillRunning
                        ? $"task was still {stillBody!.Status} when the link went down"
                        : $"task had already reached {stillBody?.Status ?? "unknown"} before the cut "
                          + "landed — the job outran the disruption, so this run proves nothing "
                          + "about link loss (use a longer job or a slower box)",
                });
                if (!stillRunning) return;
            }

            // Same definition of "visible" as the kill phase, for the same reason: a worker that
            // cannot reach the Warchief has its job re-queued, not failed outright.
            var (leftClaimed, afterCut) = await WaitForLeavesClaimedAsync(
                http, taskId, w.Id, TimeSpan.FromMinutes(3));
            // Same exclusion as the kill phase: a job that completed rode straight through the cut
            // and proves nothing about loss detection.
            var lossVisible = leftClaimed && afterCut?.Status != "completed";
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "disconnect/loss-is-visible-not-silent",
                Passed = lossVisible,
                Detail = lossVisible
                    ? $"job stopped being attributed to the unreachable worker within 3 min: "
                      + $"status={afterCut?.Status ?? "none"}"
                    : $"job still claimed after 3 min (status={afterCut?.Status ?? "none"}) — the "
                      + "Warchief never noticed the link loss",
            });
            report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "disconnect", afterCut));
        }
        finally
        {
            // Always restore, including on an exception or a driver timeout: leaving a Block rule
            // behind would quietly break every later phase and every future campaign on that box,
            // and the symptom would look like a HIVE auth or claiming fault.
            await Ssh(w.SshHost!,
                $"powershell -NoProfile -Command \"Remove-NetFirewallRule -DisplayName '{ruleName}' " +
                "-ErrorAction SilentlyContinue; 'unblocked'\"");
            Console.WriteLine($"[disconnect] {w.Id}: firewall rule removed.");
        }

        await ProveServesANewJobAsync(http, all, w, report, role, "disconnect", timeoutMs);

        // Same tradeoff as the kill phase: explicit close on the normal path only, earlier returns
        // (never claimed) leave it to expire via ControlPersist=600.
        await CloseSshConnectionAsync(w.SshHost!);
    }

    // ── Phase: Ollama stopped on the worker ────────────────────────────────────

    private static async Task RunOllamaAbsentAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, int timeoutMs)
    {
        Console.WriteLine($"[ollama] {w.Id}: stopping Ollama, then running a native-routed job...");
        RequireSsh(w, "ollama");

        // Match 'ollama*', not the exact process name 'ollama'. The desktop install also runs a
        // tray supervisor called "ollama app" (with a space), which `Get-Process -Name ollama` does
        // not match and which RESTARTS the server within seconds of it being killed. Stopping only
        // the server therefore left Ollama running on the laptop -- before=1, after=1 -- and the
        // phase reported that it could not prove absence, correctly but uselessly. Killing the
        // supervisor first is what makes the absence stick.
        const string CountOllama =
            "powershell -NoProfile -Command \"@(Get-Process | Where-Object { $_.ProcessName -like 'ollama*' }).Count\"";

        var before = await Ssh(w.SshHost!, CountOllama);
        await Ssh(w.SshHost!,
            "powershell -NoProfile -Command \"Get-Process | Where-Object { $_.ProcessName -like 'ollama*' } " +
            "| Sort-Object { $_.ProcessName -eq 'ollama' } | Stop-Process -Force; Start-Sleep -Seconds 4\"");
        var after = await Ssh(w.SshHost!, CountOllama);

        // Record what was actually true rather than assuming the stop worked. If Ollama was never
        // running, the phase still proves native execution but proves nothing about ABSENCE, and
        // saying so is the difference between evidence and a green tick.
        var ollamaGone = after.Trim() is "" or "0";
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "ollama/ollama-is-actually-absent",
            Passed = ollamaGone,
            Detail = $"ollama processes before={before.Trim()}, after={after.Trim()}"
                     + (before.Trim() is "" or "0"
                         ? " (it was not running to begin with — this phase then proves native "
                           + "execution, but not that absence was survived)"
                         : ""),
        });

        var (_, taskId) = await SubmitAsync(http, all, w, role, "ollama", ShortSpec, timeoutMs);
        var last = await PollToTerminalAsync(http, taskId, timeoutMs);
        var nativeOk = last?.Status == "completed" && last.Attestation?.RuntimeName == "NativeRoleRuntime";
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "ollama/native-job-runs-without-ollama",
            Passed = nativeOk,
            Detail = $"status={last?.Status ?? "none"}, runtime={last?.Attestation?.RuntimeName ?? "none"}",
        });
        report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "ollama", last));
    }

    // ── Phase: campaign cancelled mid-job ──────────────────────────────────────

    private static async Task RunCancelAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, int timeoutMs)
    {
        Console.WriteLine($"[cancel] {w.Id}: submitting a long job, then cancelling its campaign...");

        var (campaignId, taskId) = await SubmitAsync(http, all, w, role, "cancel", LongSpec, timeoutMs);
        var claimed = await WaitForClaimAsync(http, taskId, w.Id, TimeSpan.FromMinutes(3));
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "cancel/job-was-in-flight-before-cancel",
            Passed = claimed,
            Detail = claimed
                ? $"task {taskId} observed claimed by {w.Id} before the cancel"
                : "task never reached claimed on the target worker",
        });
        if (!claimed) return;

        using (var resp = await http.PostAsync($"/hive/campaigns/{campaignId}/cancel", null))
            Console.WriteLine($"[cancel] {w.Id}: cancel returned HTTP {(int)resp.StatusCode}");

        var last = await PollToTerminalAsync(http, taskId, timeoutMs);

        // Deliberately narrow: this asserts the WARCHIEF-side outcome only. See RemoteCancelGap --
        // without a worker-side cancel endpoint the worker keeps generating, so "cancelled" here
        // means the queue stopped waiting for it, not that generation was interrupted.
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "cancel/warchief-side-cancellation-is-visible",
            Passed = last?.Status == "cancelled",
            Detail = $"terminal status={last?.Status ?? "none"} "
                     + "(Warchief-side only — the worker has no cancel endpoint and runs to completion)",
        });
        report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "cancel", last));

        await ProveServesANewJobAsync(http, all, w, report, role, "cancel", timeoutMs);
    }

    // ── Phase: single in-flight task cancelled via the direct worker-side endpoint ─────────────
    //
    // Closes the gap RunCancelAsync's own check names ("Warchief-side only") and RemoteCancelGap
    // both flagged: that phase never actually reached the worker, so it proved the queue stopped
    // waiting, not that generation was interrupted. HiveNodeServer now has a real
    // POST /hive/tasks/cancel (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-4 item 1), and this
    // phase is what closes it for real: submit a long job, wait for claim, sign and POST directly
    // to the worker's own HiveNodeServer (not the Warchief's task queue), then confirm the task's
    // terminal status is "cancelled" -- never "failed" (which would mean HiveTaskQueue's
    // requeue-on-fail logic silently resurrected the same work, the exact BLOCKER a grok-review
    // pass caught and fixed on PR #94 before this ever shipped) and never "completed" (which would
    // mean the cancel raced a job that finished on its own, proving nothing about interruption).
    private static async Task RunWorkerCancelAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, int timeoutMs)
    {
        Console.WriteLine($"[workercancel] {w.Id}: submitting a long job, then cancelling it via the direct worker endpoint...");

        // regenerateOnCorruption: false -- this tool only ever needs to sign as the Warchief
        // identity that already exists on this machine; Load()'s default (true) would silently
        // mint and persist a BRAND NEW identity over the real one on any decrypt/deserialize
        // failure, the exact footgun Program.cs's own --leave-hive handling was hardened against
        // (grok-review, PR #95). A transient/corrupt read here should fail loudly, not overwrite
        // the fleet's real Warchief keys.
        HiveIdentity identity;
        try { identity = HiveIdentity.Load(regenerateOnCorruption: false); }
        catch (Exception ex)
        {
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "workercancel/identity-loaded",
                Passed = false,
                Detail = $"could not load this machine's existing HIVE identity: {ex.Message} — " +
                    "refusing to regenerate one, since that would overwrite the real Warchief keys",
            });
            return;
        }
        // Matches on HivePeer.Name against the --worker-a-style id (e.g. "HardcorePC") -- this
        // fleet's actual pairing sets Name to the machine's display name, matching the same id
        // used throughout this driver and --worker-a/-b/-c, verified working live repeatedly.
        // CodeRabbit correctly flagged this as fragile in general (Name's source varies:
        // WarchiefName, WarchiefNodeId, or a membership cert's subject name depending on the
        // pairing path taken) -- not hardened further here since no more canonical
        // human-readable identifier exists on HivePeer to match against, and a mismatch already
        // fails safely below (a clear "no peer named X found" check, not a wrong action).
        var peer = HivePeerStore.Default.All()
            .FirstOrDefault(p => string.Equals(p.Name, w.Id, StringComparison.OrdinalIgnoreCase));
        var secret = peer is null ? null : HivePeerStore.Default.GetSharedSecret(peer.NodeId);
        if (peer is null || secret is null)
        {
            report.Checks.Add(new Hv4Check
            {
                WorkerId = w.Id,
                Name = "workercancel/peer-resolved",
                Passed = false,
                Detail = peer is null
                    ? $"no peer named '{w.Id}' found in this Warchief's own peer store — cannot sign a request to it"
                    : $"no shared secret on file for peer '{w.Id}' ({peer.NodeId})",
            });
            return;
        }

        var (campaignId, taskId) = await SubmitAsync(http, all, w, role, "workercancel", LongSpec, timeoutMs);

        // Tighter poll than the shared WaitForClaimAsync (1.5s): this phase's disruption is a
        // single direct HTTP call, fast enough that a fast box can finish a 20-step job within
        // the shared helper's own detection latency -- confirmed live against HardcorePC, which
        // raced and completed twice in a row at the 1.5s poll interval. kill/disconnect don't
        // need this because their SSH-based disruption has its own latency floor regardless of
        // how fast detection is; this one genuinely benefits from reacting sooner.
        var claimed = await WaitForClaimFastAsync(http, taskId, w.Id, TimeSpan.FromMinutes(3));
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "workercancel/job-was-in-flight-before-cancel",
            Passed = claimed,
            Detail = claimed
                ? $"task {taskId} observed claimed by {w.Id} before the cancel"
                : "task never reached claimed on the target worker — the cancel would prove nothing",
        });
        if (!claimed) return;

        var cancelBody = JsonSerializer.Serialize(new { taskId }, JsonOptions);
        var (status, respBody) = await PostSignedAsync(w.NodeUrl, "/hive/tasks/cancel", cancelBody, identity, secret);
        Console.WriteLine($"[workercancel] {w.Id}: direct cancel call returned HTTP {status}: {respBody}");
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "workercancel/cancel-request-accepted",
            Passed = status == 200,
            Detail = $"HTTP {status}: {respBody}",
        });
        if (status != 200) return;

        var last = await PollToTerminalAsync(http, taskId, timeoutMs);
        var genuinelyCancelled = last?.Status == "cancelled";
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = "workercancel/task-reports-cancelled-not-requeued",
            Passed = genuinelyCancelled,
            Detail = last?.Status switch
            {
                "cancelled" => "terminal status=cancelled — genuine remote cancellation, not a requeue",
                "completed" => "terminal status=completed — the job finished before the cancel landed, " +
                               "raced, proves nothing about interruption",
                _ => $"terminal status={last?.Status ?? "none"} — if this is anything other than " +
                     "cancelled/completed, the cancel likely fell through to the requeue path " +
                     "(the exact bug a grok-review pass caught and fixed on PR #94)",
            },
        });
        report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, "workercancel", last));

        await ProveServesANewJobAsync(http, all, w, report, role, "workercancel", timeoutMs);
    }

    private static async Task<(int Status, string Body)> PostSignedAsync(
        string nodeUrl, string path, string jsonBody, HiveIdentity identity, byte[] secret)
    {
        using var signHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var bodyBytes = System.Text.Encoding.UTF8.GetBytes(jsonBody);
        using var req = new HttpRequestMessage(HttpMethod.Post, nodeUrl.TrimEnd('/') + path)
            { Content = new ByteArrayContent(bodyBytes) };
        req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        HiveAuthMiddleware.SignRequest(req, bodyBytes, identity.NodeId, secret);
        using var resp = await signHttp.SendAsync(req).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ((int)resp.StatusCode, body);
    }

    /// <summary>250ms-poll variant of <see cref="WaitForClaimAsync"/> for workercancel only —
    /// see the comment at its call site for why this phase specifically needs tighter detection
    /// latency than the shared 1.5s helper.</summary>
    private static async Task<bool> WaitForClaimFastAsync(
        HttpClient http, string taskId, string workerId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http.GetAsync($"/hive/tasks/{taskId}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions);
                if (body?.Status is "claimed" or "running"
                    && string.Equals(body.ClaimedBy, workerId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (body?.Status is "completed" or "failed" or "timeout" or "cancelled") return false;
            }
            await Task.Delay(250);
        }
        return false;
    }

    // ── Shared: recovery proof ─────────────────────────────────────────────────

    /// <summary>
    /// The recovery half of every phase. A worker is only proven usable again by actually USING
    /// it -- a live process or a reachable telemetry endpoint says nothing about whether the role
    /// still holds a working executor, which is exactly what a mid-job death or cancel puts in
    /// doubt.
    /// </summary>
    private static async Task ProveServesANewJobAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, Hv4Report report,
        string role, string phase, int timeoutMs)
    {
        Console.WriteLine($"[{phase}] {w.Id}: proving the role still serves a new job...");
        var (_, taskId) = await SubmitAsync(http, all, w, role, $"{phase}-recovery", ShortSpec, timeoutMs);
        var last = await PollToTerminalAsync(http, taskId, timeoutMs);
        var ok = last?.Status == "completed"
                 && last.Attestation?.RuntimeName == "NativeRoleRuntime"
                 && string.Equals(last.ClaimedBy, w.Id, StringComparison.OrdinalIgnoreCase);
        report.Checks.Add(new Hv4Check
        {
            WorkerId = w.Id,
            Name = $"{phase}/role-reusable-after-recovery",
            Passed = ok,
            Detail = $"status={last?.Status ?? "none"}, runtime={last?.Attestation?.RuntimeName ?? "none"}, "
                     + $"claimedBy={last?.ClaimedBy ?? "none"}",
        });
        report.Jobs.Add(BuildJobEvidence(taskId, w.Id, role, $"{phase}-recovery", last));
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    // Long enough that the disruption genuinely lands mid-generation rather than racing the job's
    // own completion. This matters more than it looks: landing a cut takes ~10s of wall clock
    // (claim poll, an ssh round trip to apply it, another to verify), and HardcoreLaptopMSI
    // finished the previous, shorter version of this spec in 14.5s -- so the job was essentially
    // over before the link was cut, and the phase measured nothing.
    //
    // A single-turn "write a very long document" prompt does NOT reliably occupy MaxSteps: an LLM
    // can (and, observed repeatedly in the fleet logs, often does) emit the whole thing in one
    // completion -- `steps: 1` -- so wall-clock time is bounded purely by that one call's raw
    // token-generation speed. A genuinely fast card can finish that in well under the time it
    // takes this driver to ssh in and apply a kill/firewall rule, which is exactly the race seen
    // repeatedly against HardcoreLaptopMSI even with the size bumped up.
    //
    // Forcing multiple DISCRETE TOOL-CALL STEPS is the reliable fix, because each step costs a full
    // extra model round trip (generate -> execute tool -> re-invoke with the extended context) on
    // top of raw generation speed -- overhead that does not shrink just because the GPU is fast.
    // Ten steps costs roughly ten times one step's wall clock almost regardless of card speed,
    // which a single long completion never guarantees. Kept under MaxSteps' ceiling of 12 with
    // headroom rather than sized to exactly hit it.
    // Confirmed against the fleet: 10 steps reliably outlasted `kill`'s landing (near-instant --
    // a single ssh Stop-Process call) 3/3, but HardcoreLaptopMSI still finished all 10 before
    // `disconnect`'s slower landing (ssh round trip + create-rule + verify, several times kill's
    // latency) 2/3 times. Doubled to 20 rather than tuned per-phase: one spec shared by both
    // phases is simpler than two, and 20 steps costs kill nothing it wasn't already comfortably
    // beating.
    private const string LongSpec =
        "Create twenty separate files, one at a time, named hv4_step_01.txt through hv4_step_20.txt. " +
        "Do not create more than one file per response. After creating each file, wait for the " +
        "result before creating the next one. Each file must contain a two-paragraph essay on a " +
        "DIFFERENT subtopic of distributed systems design (e.g. consensus, replication, backpressure, " +
        "idempotency, partitioning, consistent hashing, leader election, vector clocks, CRDTs, " +
        "gossip protocols, quorum reads/writes) -- pick a new subtopic for each file, do not repeat " +
        "one. Create all twenty files, each in its own separate step.";

    private const string ShortSpec =
        "Create a file named hv4_proof.txt in the workspace root containing exactly this single " +
        "line and nothing else: HV4-PROOF";

    private static async Task<(string CampaignId, string TaskId)> SubmitAsync(
        HttpClient http, List<WorkerTarget> all, WorkerTarget w, string role,
        string tag, string spec, int timeoutMs)
    {
        var unitId = $"hv4-{tag}-{w.Id}";
        var campaign = new CampaignDefinition
        {
            Name = $"hv4-{tag}-{w.Id}",
            WorkUnits =
            [
                new WorkUnit
                {
                    WorkUnitId = unitId,
                    Title = $"HV-4 {tag} on {w.Id}",
                    Role = role,
                    ExecutionKind = HiveExecutionKinds.NativeAgent,
                    Requirements = new ResourceRequirements
                    {
                        ExcludedWorkerIds = all
                            .Where(x => !string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase))
                            .Select(x => x.Id).ToArray(),
                    },
                    Spec = spec,
                    TimeoutMs = timeoutMs,
                },
            ],
        };
        using var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions);
        resp.EnsureSuccessStatusCode();
        return (campaign.CampaignId, $"{campaign.CampaignId}-{unitId}");
    }

    private static async Task<bool> WaitForClaimAsync(
        HttpClient http, string taskId, string workerId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http.GetAsync($"/hive/tasks/{taskId}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions);
                if (body?.Status is "claimed" or "running"
                    && string.Equals(body.ClaimedBy, workerId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (body?.Status is "completed" or "failed" or "timeout" or "cancelled") return false;
            }
            await Task.Delay(1500);
        }
        return false;
    }

    /// <summary>
    /// Waits for a task to stop being attributed to <paramref name="workerId"/> -- either it went
    /// terminal, or the watchdog re-queued it (status back to pending, ClaimedBy cleared, attempt
    /// advanced). Returns the last status seen either way, so a failing check can report what the
    /// job was actually doing rather than just "not what I wanted".
    /// </summary>
    private static async Task<(bool Left, HiveTaskStatusResponse? Last)> WaitForLeavesClaimedAsync(
        HttpClient http, string taskId, string workerId, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        HiveTaskStatusResponse? last = null;
        while (DateTime.UtcNow < deadline)
        {
            using var resp = await http.GetAsync($"/hive/tasks/{taskId}");
            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<HiveTaskStatusResponse>(JsonOptions);
                if (body is not null)
                {
                    last = body;
                    if (body.Status is "completed" or "failed" or "timeout" or "cancelled")
                        return (true, body);
                    if (!string.Equals(body.ClaimedBy, workerId, StringComparison.OrdinalIgnoreCase))
                        return (true, body);
                }
            }
            await Task.Delay(2000);
        }
        return (false, last);
    }

    /// <summary>
    /// Inverse of <see cref="WaitForTelemetryAsync"/>: waits for a worker to STOP answering. Two
    /// consecutive misses are required so a single dropped request cannot be read as a dead worker
    /// — the whole point of this helper is to be a precondition strong enough to gate a phase on.
    /// </summary>
    private static async Task<bool> WaitForTelemetryGoneAsync(string nodeUrl, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        var misses = 0;
        while (DateTime.UtcNow < deadline)
        {
            if (await TryFetchTelemetryAsync(nodeUrl) is null)
            {
                if (++misses >= 2) return true;
            }
            else misses = 0;
            await Task.Delay(2000);
        }
        return false;
    }

    private static async Task<bool> WaitForTelemetryAsync(string nodeUrl, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        while (DateTime.UtcNow < deadline)
        {
            if (await TryFetchTelemetryAsync(nodeUrl) is not null) return true;
            await Task.Delay(3000);
        }
        return false;
    }

    private static async Task<HiveTaskStatusResponse?> PollToTerminalAsync(
        HttpClient http, string taskId, int timeoutMs)
    {
        // Generous headroom over the unit's own timeout: this phase is specifically waiting for a
        // WATCHDOG to fire (45s heartbeat window, up to MaxAttempts retries), which by design takes
        // longer than the job would have.
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs + 300_000);
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

    private static Hv4JobEvidence BuildJobEvidence(
        string taskId, string workerId, string role, string phase, HiveTaskStatusResponse? last)
        => new()
        {
            TaskId = taskId,
            WorkerId = workerId,
            Role = role,
            Phase = phase,
            Status = last?.Status ?? "unknown",
            ClaimedBy = last?.ClaimedBy,
            ClaimedByExpected = string.Equals(last?.ClaimedBy, workerId, StringComparison.OrdinalIgnoreCase),
            RuntimeName = last?.Attestation?.RuntimeName,
            // A job that completed on anything other than NativeRoleRuntime is a silent fallback,
            // which §6 forbids outright. A job that did NOT complete has no runtime to judge.
            IsNativeRuntime = last?.Attestation?.RuntimeName == "NativeRoleRuntime",
            Stats = last?.Metrics ?? [],
            ErrorMsg = last?.ErrorMsg,
        };

    private static async Task<NativeTelemetry?> TryFetchTelemetryAsync(string nodeUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync($"{nodeUrl.TrimEnd('/')}/hive/native-telemetry");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<NativeTelemetry>(JsonOptions);
        }
        catch { return null; }
    }

    private static void RequireSsh(WorkerTarget w, string phase)
    {
        if (string.IsNullOrWhiteSpace(w.SshHost) || string.IsNullOrWhiteSpace(w.TaskName))
            throw new InvalidOperationException(
                $"Phase '{phase}' performs box-level actions on {w.Id} and needs both " +
                $"--worker-*-ssh and --worker-*-task for it.");
    }

    /// <summary>
    /// Box-level actions go over ssh, matching how the fleet is already administered. Output is
    /// returned rather than streamed so it can be recorded in the evidence; failures are surfaced
    /// as text rather than thrown, because a phase that cannot take a box down still needs to say
    /// so in its verdict instead of aborting the whole run.
    /// </summary>
    /// <summary>
    /// Retries with a lengthening connect timeout, because one retry was not enough.
    ///
    /// HardcoreLaptopMSI's sshd becomes unreachable for minutes at a time while the box itself stays
    /// healthy — ping 5 ms, /hive/native-telemetry answering 200, jobs completing normally. It
    /// correlates with the box being saturated doing inference, which is exactly when these phases
    /// need to reach it: a key exchange needs CPU, and an already-established HTTP listener does
    /// not. Across an HV-6 campaign it cost hv4-kill and hv4-disconnect two rounds out of three,
    /// every failure on that one machine and none on HardcorePC.
    ///
    /// Three attempts at 10s / 20s / 30s with backoff between. The landed-gates still decide
    /// whether a phase ran — this only stops a recoverable blip from being reported as one.
    ///
    /// Two correctness bugs found by CodeRabbit's review of this file, both fixed here together
    /// since they live in the same two functions:
    ///
    /// (1) Retrying on empty stdout re-ran side-effecting commands. `Stop-Process` and
    /// `Stop-ScheduledTask; Start-ScheduledTask` legitimately print nothing, so every kill/restart
    /// was silently retried up to 3 times regardless of whether it worked the first time — the
    /// restart in particular re-stopped a worker that had just come back up, racing
    /// WaitForTelemetryAsync immediately afterward. This is a strong candidate for a good deal of
    /// the "HardcoreLaptopMSI flakiness" attributed to sshd/power-plan limits earlier in this
    /// campaign: a self-inflicted stop/start loop produces exactly the symptom of a worker that
    /// flickers up and down under load. Fixed with an explicit completion marker appended to every
    /// command — success is now "the marker arrived", not "stdout was non-empty" — so a legitimately
    /// silent action is trusted on its first attempt.
    ///
    /// (2) `ReadToEnd()` blocks with no timeout of its own; the `WaitForExit(ms)` that followed it
    /// could only ever run AFTER both reads returned, so a genuinely hung ssh session (network
    /// partition mid-command, pipes never closed) blocked this call — and the whole driver —
    /// forever, silently upgrading a recoverable blip into an unbounded hang. Fixed by racing the
    /// reads against a real, cancellable timeout and killing the process tree if it fires.
    /// </summary>
    private static async Task<string> Ssh(string host, string command)
    {
        int[] timeouts = [10, 20, 30];
        for (var attempt = 0; attempt < timeouts.Length; attempt++)
        {
            var (ok, output) = await SshOnce(host, command, timeouts[attempt]).ConfigureAwait(false);
            if (ok) return output;
            if (attempt < timeouts.Length - 1) await Task.Delay(3000 * (attempt + 1)).ConfigureAwait(false);
        }
        return "";
    }

    // ── SSH connection multiplexing ─────────────────────────────────────────
    //
    // The controlled sleep experiment (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-6,
    // 2026-07-28) proved command latency was never the bottleneck for HardcoreLaptopMSI's
    // disconnect failures: 1s and 0s produced statistically identical outcomes. What actually
    // fails is the ssh session completing AT ALL while the box is CPU-loaded serving the induced
    // job -- because a fresh SSH connection pays for a full crypto key exchange, which is
    // CPU-bound, competing directly with the inference job for the same cycles.
    //
    // OpenSSH connection multiplexing (ControlMaster/ControlPath) sidesteps this: establish ONE
    // authenticated connection while the box is still idle (before the job is even submitted),
    // hold it open, and have every subsequent command open a cheap new CHANNEL on that existing
    // connection instead of a new one -- no repeat key exchange, so no repeat CPU contention.
    // Verified by hand against this exact host before writing this: a command that normally needs
    // seconds-to-tens-of-seconds of connect-timeout retries completed in 0.35s over a pre-warmed
    // connection.
    //
    // A fresh, unshared control socket per (worker, phase, process) run avoids any collision
    // between concurrent invocations, and ControlPersist bounds its lifetime so a crashed run
    // cannot leave a socket held open forever.
    private static string ControlPath(string sshHost)
        => Path.Combine(Path.GetTempPath(), $"theorc-hv4-cm-{sshHost}-{Environment.ProcessId}");

    /// <summary>
    /// Establishes the multiplexed master connection. Call ONCE per phase, as early as possible
    /// -- ideally before the induced job is even submitted, while the box is still idle -- so the
    /// one unavoidable CPU-bound handshake happens on a machine that can actually spare the
    /// cycles for it. A failure here is non-fatal: <see cref="Ssh"/> falls back to establishing
    /// its own ordinary (non-multiplexed) connection per call, exactly as it did before this
    /// existed, so a box that cannot even accept the warm-up connection is no worse off than
    /// before.
    /// </summary>
    private static async Task<bool> WarmSshConnectionAsync(string sshHost)
    {
        var controlPath = ControlPath(sshHost);
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=15");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlMaster=yes");
        // Long enough to outlive the phase's own worst-case retries and timeouts; the connection
        // is explicitly closed at the end of the phase regardless, so this is a ceiling, not the
        // expected lifetime.
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ControlPersist=600");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add($"ControlPath={controlPath}");
        // -N: no remote command, just hold the tunnel. -f: fork to background once authenticated,
        // so this call returns promptly instead of blocking for the connection's whole lifetime.
        psi.ArgumentList.Add("-N");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(sshHost);

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await p.WaitForExitAsync(cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return false;
        }
        _ = await stdoutTask.ConfigureAwait(false);
        _ = await stderrTask.ConfigureAwait(false);

        // -f backgrounds on success but the parent's own exit code still reports whether the
        // handshake completed -- don't just assume it worked because the process returned.
        return p.ExitCode == 0 && await CheckMultiplexedConnectionAsync(sshHost).ConfigureAwait(false);
    }

    private static async Task<bool> CheckMultiplexedConnectionAsync(string sshHost)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add($"ControlPath={ControlPath(sshHost)}");
        psi.ArgumentList.Add("-O"); psi.ArgumentList.Add("check");
        psi.ArgumentList.Add(sshHost);
        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync().ConfigureAwait(false);
        return p.ExitCode == 0;
    }

    /// <summary>
    /// Best-effort teardown. Not calling this is not a leak in the way it would be for most
    /// resources -- ControlPersist=600 above already bounds the socket's lifetime -- but a run
    /// that finished normally should not leave 600s of dangling connection behind it either.
    /// </summary>
    private static async Task CloseSshConnectionAsync(string sshHost)
    {
        try
        {
            var psi = new ProcessStartInfo("ssh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add($"ControlPath={ControlPath(sshHost)}");
            psi.ArgumentList.Add("-O"); psi.ArgumentList.Add("exit");
            psi.ArgumentList.Add(sshHost);
            using var p = Process.Start(psi)!;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch { /* best-effort; ControlPersist bounds any leak regardless */ }
    }

    // Marks where the caller's command ends and Ssh()'s own bookkeeping begins. Its absence from
    // the captured stdout is what "the command did not complete" means now — not "stdout was
    // empty", which a correctly-succeeding side-effecting command produces by design.
    private const string SshDoneMarker = "__HV4_SSH_DONE__";

    private static async Task<(bool Ok, string Output)> SshOnce(
        string host, string command, int connectTimeoutSec)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ConnectTimeout={connectTimeoutSec}");
        // Keep a slow session alive rather than letting it die mid-command: the box is often
        // CPU-starved when we reach it, not unreachable.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveInterval=5");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ServerAliveCountMax=12");
        // ControlMaster=auto: if WarmSshConnectionAsync already established a multiplexed
        // connection at this ControlPath, reuse it (opening a cheap new channel, no repeat
        // handshake); if not -- the warm-up was never called, failed, or already timed out -- ssh
        // transparently falls back to an ordinary connection of its own, exactly as before this
        // existed. This call is never worse off for trying.
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ControlMaster=auto");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add($"ControlPath={ControlPath(host)}");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add($"{command}; Write-Output '{SshDoneMarker}'");

        using var p = Process.Start(psi)!;
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(180_000));
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            return (false, "");
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        // Drained but discarded: current OpenSSH writes a post-quantum advisory to stderr on every
        // connection, and folding it into the result makes every parsed value wrong.
        _ = await stderrTask.ConfigureAwait(false);

        var markerAt = stdout.LastIndexOf(SshDoneMarker, StringComparison.Ordinal);
        if (markerAt < 0) return (false, "");
        return (true, stdout[..markerAt].TrimEnd());
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

// ── DTOs ───────────────────────────────────────────────────────────────────────

internal sealed class WorkerTarget
{
    public string Id { get; init; } = "";
    public string NodeUrl { get; init; } = "";
    public string? SshHost { get; init; }
    public string? TaskName { get; init; }
}

internal sealed class NativeTelemetry
{
    public long RejectedAdmissionCount { get; set; }
    public string? LastRejectionReason { get; set; }
    public long TotalBytes { get; set; }
    public long ReservedBytes { get; set; }
    public long AvailableBytes { get; set; }
}

internal sealed class Hv4Check
{
    public string WorkerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed class Hv4JobEvidence
{
    public string TaskId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Phase { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public bool ClaimedByExpected { get; set; }
    public string? RuntimeName { get; set; }
    public bool IsNativeRuntime { get; set; }
    public Dictionary<string, double> Stats { get; set; } = [];
    public string? ErrorMsg { get; set; }
}

internal sealed class Hv4Report
{
    public string Warchief { get; set; } = "";
    public string Phase { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<Hv4JobEvidence> Jobs { get; set; } = [];
    public List<Hv4Check> Checks { get; set; } = [];
    public List<string> TargetedWorkers { get; set; } = [];
    public List<string> UncoveredItems { get; set; } = [];
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
