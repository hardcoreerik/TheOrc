// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;

namespace Hv6RepeatabilityRunner;

/// <summary>
/// HV-6 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): runs the whole HV-1 → HV-5 campaign
/// N times back-to-back with no intervention, and aggregates every lane's own evidence into one
/// fleet report.
///
/// It deliberately talks to NOTHING. No Warchief, no worker, no HIVE contract is linked into this
/// project. It invokes the five existing lane runners and reads what they already wrote, so:
///   * it cannot drift out of sync with a contract it does not use;
///   * a lane's verdict here is that lane's OWN verdict, not this driver's re-interpretation of raw
///     telemetry — which is the only way "the campaign is repeatable" means anything;
///   * adding a lane is adding a row to <see cref="BuildLanes"/>, not new assertion logic.
///
/// **This report does not, and must not, claim the §6 default-runtime flip.** It presents evidence
/// for the maintainer's decision. Three green rounds prove repeatability, which is ONE of §6's eight
/// criteria — and several of the others are only partially evidenced, for reasons each lane records
/// in its own `uncoveredItems`. The report carries that disclaimer in-band so it cannot be quoted
/// out of context.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private const string FlipDisclaimer =
        "This report does NOT claim the §6 default-runtime flip. Repeatability is one of §6's eight " +
        "criteria; the others are evidenced (or not) by the individual lanes, each of which records " +
        "its own uncovered items. Read those before quoting this verdict.";

    public static async Task<int> Main(string[] args)
    {
        var rounds = int.TryParse(GetArg(args, "--rounds"), out var r) && r > 0 ? r : 3;
        var warchief = GetArg(args, "--warchief") ?? "http://localhost:7079";
        var outDir = GetArg(args, "--out")
                     ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-6-lane");
        var toolsRoot = GetArg(args, "--tools-root")
                        ?? Path.Combine(Environment.CurrentDirectory, "Tools");
        var modelHash = GetArg(args, "--model-hash")
                        ?? throw new InvalidOperationException(
                            "--model-hash is required (the pinned fleet GGUF's SHA-256) — HV-1 records it per job.");

        // The two fleet configurations the campaign needs. HV-6's requirement is the full campaign
        // "run 3x back-to-back, all green, NO MANUAL INTERVENTION between runs", and HV-2's large
        // phase only means anything against a context size whose footprint exceeds the low-VRAM
        // box's budget — so switching between them has to be part of the run, not something an
        // operator does by hand halfway through.
        var stdContextSize = int.TryParse(GetArg(args, "--context-size"), out var cs) ? cs : 4096;
        var largeContextSize = int.TryParse(GetArg(args, "--large-context-size"), out var lcs) ? lcs : 32768;

        var fleet = new Fleet
        {
            WorkerAId   = GetArg(args, "--worker-a") ?? "HardcorePC",
            WorkerANode = GetArg(args, "--worker-a-node") ?? "http://100.102.190.112:7078",
            WorkerASsh  = GetArg(args, "--worker-a-ssh") ?? "100.102.190.112",
            WorkerATask = GetArg(args, "--worker-a-task") ?? "TheOrcWorker",
            WorkerALog  = GetArg(args, "--worker-a-log") ?? @"F:\Ai\OrchestratorIDE-dev\worker_hpc.log",
            WorkerBId   = GetArg(args, "--worker-b") ?? "HardcoreLaptopMSI",
            WorkerBNode = GetArg(args, "--worker-b-node") ?? "http://100.114.151.4:7078",
            WorkerBSsh  = GetArg(args, "--worker-b-ssh") ?? "100.114.151.4",
            WorkerBTask = GetArg(args, "--worker-b-task") ?? "TheOrcLaptopWorker",
            WorkerBLog  = GetArg(args, "--worker-b-log") ?? @"C:\Ai\OrchestratorIDE-dev\worker_laptop.log",
            // Defaults to worker A because that is HardcorePC's 6 GB card against the laptop's 8 GB.
            // Override when the fleet shape changes; HV-2's `large` phase is meaningless if this
            // names the box with headroom.
            WorkerADir  = GetArg(args, "--worker-a-dir") ?? @"F:\Ai\OrchestratorIDE-dev",
            WorkerBDir  = GetArg(args, "--worker-b-dir") ?? @"C:\Ai\OrchestratorIDE-dev",
            LowVramWorkerId = GetArg(args, "--low-vram-worker") ?? GetArg(args, "--worker-a") ?? "HardcorePC",
        };

        // Opt-out for lanes a given environment cannot currently drive. Named rather than silently
        // skipped: a lane left out of an "all green, 3 rounds" claim has to be visible IN the report,
        // or the report is a lie by omission. HardcoreLaptopMSI's sshd instability is the live
        // example — it blocks that box's disconnect phase without touching anything in the product.
        var skip = (GetArg(args, "--skip") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lanes = BuildLanes(toolsRoot, warchief, fleet, modelHash, largeContextSize)
            .Where(l => !skip.Contains(l.Name))
            .ToList();
        if (lanes.Count == 0)
            throw new InvalidOperationException("--skip excluded every lane; nothing to run.");

        Directory.CreateDirectory(outDir);

        var report = new Hv6Report
        {
            Warchief = warchief,
            RoundsRequested = rounds,
            Lanes = lanes.Select(l => l.Name).ToList(),
            SkippedLanes = skip.ToList(),
            StartedAt = DateTimeOffset.UtcNow,
            FlipClaim = FlipDisclaimer,
        };

        foreach (var lane in lanes)
            if (!File.Exists(lane.ExePath))
                throw new FileNotFoundException(
                    $"Lane '{lane.Name}' runner not found at {lane.ExePath}. Build the Tools/ " +
                    "projects in Release before running HV-6.", lane.ExePath);

        // Lanes are ordered so every standard-config lane runs before any that needs a different
        // one, which keeps reconfigurations to at most one per round instead of thrashing the fleet
        // between lanes. Stable sort, so the authored order inside each group is preserved.
        lanes = lanes.OrderBy(l => l.ContextSize is null ? 0 : 1).ToList();

        // Whatever happens — a failed lane, a thrown reconfiguration, a crash — the fleet goes back
        // to the standard context size before this process exits. Leaving it on the large config
        // would silently break every later campaign, and the symptom (jobs denied on the small box)
        // looks like a runtime regression rather than leftover state.
        var currentContextSize = stdContextSize;
        try
        {
        for (var round = 1; round <= rounds; round++)
        {
            Console.WriteLine();
            Console.WriteLine($"═══ ROUND {round} of {rounds} ═══");
            var roundResult = new Hv6Round { Round = round, StartedAt = DateTimeOffset.UtcNow };

            foreach (var lane in lanes)
            {
                var required = lane.ContextSize ?? stdContextSize;
                if (required != currentContextSize)
                {
                    if (!await SetFleetContextSizeAsync(fleet, required))
                    {
                        // Record the lane as unrun rather than running it against the wrong config.
                        // A lane that silently ran under the wrong fleet configuration is how HV-2's
                        // large phase came to report a denial that never happened.
                        roundResult.Lanes.Add(new Hv6LaneResult
                        {
                            Lane = lane.Name,
                            ExitCode = -1,
                            Verdict = "NOT-RUN(reconfigure-failed)",
                            FailedChecks =
                            [
                                $"fleet could not be set to NATIVECONTEXTSIZE={required}; lane skipped "
                                + "rather than run against the wrong configuration",
                            ],
                            StartedAt = DateTimeOffset.UtcNow,
                            FinishedAt = DateTimeOffset.UtcNow,
                        });
                        Console.WriteLine($"     NOT-RUN — could not reconfigure to {required}");
                        continue;
                    }
                    currentContextSize = required;
                    report.Reconfigurations++;
                }

                Console.WriteLine($"  ── {lane.Name} …"
                                  + (lane.ContextSize is null ? "" : $" (ctx={lane.ContextSize})"));
                var started = DateTimeOffset.UtcNow;
                var (exitCode, stdout) = await RunLaneAsync(lane);
                var finished = DateTimeOffset.UtcNow;

                var laneResult = new Hv6LaneResult
                {
                    Lane = lane.Name,
                    ExitCode = exitCode,
                    // The lane's OWN verdict line is authoritative. Exit code is kept alongside it
                    // rather than trusted alone: a runner that crashes before writing evidence also
                    // exits non-zero, and those two situations need telling apart.
                    Verdict = ParseVerdict(stdout) ?? (exitCode == 0 ? "PASS(exit-only)" : "NO-VERDICT"),
                    EvidencePath = ParseEvidencePath(stdout),
                    FailedChecks = ParseFailedChecks(stdout),
                    StartedAt = started,
                    FinishedAt = finished,
                    DurationSeconds = (int)(finished - started).TotalSeconds,
                };
                roundResult.Lanes.Add(laneResult);

                Console.WriteLine($"     {laneResult.Verdict}  ({laneResult.DurationSeconds}s)"
                                  + (laneResult.EvidencePath is null ? "" : $"  → {laneResult.EvidencePath}"));
                foreach (var f in laneResult.FailedChecks)
                    Console.WriteLine($"       ✗ {f}");
            }

            roundResult.FinishedAt = DateTimeOffset.UtcNow;
            roundResult.Passed = roundResult.Lanes.All(l => l.Verdict.StartsWith("PASS", StringComparison.Ordinal));
            report.Rounds.Add(roundResult);

            Console.WriteLine($"  ROUND {round}: {(roundResult.Passed ? "PASS" : "FAIL")}");

            // Keep going after a failed round rather than aborting. HV-6 is asking whether the
            // campaign is REPEATABLE, and "round 2 failed, rounds 1 and 3 passed" is a far more
            // useful answer than "stopped at round 2" — intermittency is exactly what this lane is
            // built to surface, and stopping early hides it.

            // Back to the standard config before the next round, so every round starts from the
            // same fleet state. Without this, round 2 would begin on whatever the last lane of
            // round 1 happened to leave behind, and "repeatable" would be measuring three
            // different setups.
            if (currentContextSize != stdContextSize && round < rounds)
            {
                if (await SetFleetContextSizeAsync(fleet, stdContextSize))
                {
                    currentContextSize = stdContextSize;
                    report.Reconfigurations++;
                }
            }
        }
        }
        finally
        {
            if (currentContextSize != stdContextSize)
            {
                Console.WriteLine();
                Console.WriteLine("Restoring fleet to the standard context size …");
                report.FleetRestored = await SetFleetContextSizeAsync(fleet, stdContextSize);
                report.Reconfigurations++;
            }
            else report.FleetRestored = true;
        }

        report.FinishedAt = DateTimeOffset.UtcNow;
        report.Passed = report.Rounds.Count == rounds && report.Rounds.All(x => x.Passed);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        var jsonPath = Path.Combine(outDir, $"hv6_report_{stamp}.json");
        await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(report, JsonOptions));

        var summaryPath = Path.Combine(outDir, $"hv6_summary_{stamp}.md");
        await File.WriteAllTextAsync(summaryPath, BuildSummary(report));

        Console.WriteLine();
        Console.WriteLine(BuildSummary(report));
        Console.WriteLine($"Evidence written: {jsonPath}");
        Console.WriteLine($"Summary written:  {summaryPath}");
        return report.Passed ? 0 : 2;
    }

    // ── Lane table ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The campaign, in order. HV-3 and HV-4 appear once per phase because each phase writes its own
    /// evidence file and carries its own verdict — collapsing them would lose which phase regressed.
    /// </summary>
    private static List<Lane> BuildLanes(string toolsRoot, string warchief, Fleet f, string modelHash, int largeContextSize)
    {
        string Exe(string project, string exeName) =>
            Path.Combine(toolsRoot, project, "bin", "Release", "net10.0", exeName + ".exe");

        // Shared fleet descriptors, spelled per-runner because the lanes genuinely differ: HV-1/HV-2
        // identify workers by id alone, HV-3 needs node URLs for telemetry, HV-4 needs ssh + the
        // scheduled-task name for box-level actions, HV-5 needs ssh + a log path to sweep.
        var nodeArgs =
            $"--worker-a {f.WorkerAId} --worker-a-node {f.WorkerANode} " +
            $"--worker-b {f.WorkerBId} --worker-b-node {f.WorkerBNode}";
        var sshTaskArgs =
            $"--worker-a {f.WorkerAId} --worker-a-node {f.WorkerANode} --worker-a-ssh {f.WorkerASsh} --worker-a-task {f.WorkerATask} " +
            $"--worker-b {f.WorkerBId} --worker-b-node {f.WorkerBNode} --worker-b-ssh {f.WorkerBSsh} --worker-b-task {f.WorkerBTask}";
        var sshLogArgs =
            $"--worker-a {f.WorkerAId} --worker-a-node {f.WorkerANode} --worker-a-ssh {f.WorkerASsh} --worker-a-log \"{f.WorkerALog}\" " +
            $"--worker-b {f.WorkerBId} --worker-b-node {f.WorkerBNode} --worker-b-ssh {f.WorkerBSsh} --worker-b-log \"{f.WorkerBLog}\"";

        return
        [
            new Lane("hv1", Exe("Hv1NativeCampaignRunner", "hv1-native-campaign-runner"),
                $"--warchief {warchief} --model-hash {modelHash} --worker-a {f.WorkerAId} --worker-b {f.WorkerBId}"),

            // HV-2 needs node URLs AND --low-vram-worker: its `large` phase asserts that the
            // smaller-VRAM box DENIES admission, so it has to be told which box that is. Caught by
            // running it — without these it throws "No workers configured" before doing anything,
            // which as an HV-6 lane would have read as a lane failure rather than a driver misuse.
            new Lane("hv2-large", Exe("Hv2SchedulingRunner", "hv2-scheduling-runner"),
                $"--warchief {warchief} --phase large {nodeArgs} --low-vram-worker {f.LowVramWorkerId}",
                ContextSize: largeContextSize),
            new Lane("hv2-small", Exe("Hv2SchedulingRunner", "hv2-scheduling-runner"),
                $"--warchief {warchief} --phase small {nodeArgs} --low-vram-worker {f.LowVramWorkerId}"),

            new Lane("hv3-sequential", Exe("Hv3LifecycleRunner", "hv3-lifecycle-runner"),
                $"--warchief {warchief} --phase sequential --cycles 3 {nodeArgs}"),
            new Lane("hv3-concurrent", Exe("Hv3LifecycleRunner", "hv3-lifecycle-runner"),
                $"--warchief {warchief} --phase concurrent {nodeArgs}"),

            new Lane("hv4-cancel", Exe("Hv4RecoveryRunner", "hv4-recovery-runner"),
                $"--warchief {warchief} --phase cancel {sshTaskArgs}"),
            new Lane("hv4-ollama", Exe("Hv4RecoveryRunner", "hv4-recovery-runner"),
                $"--warchief {warchief} --phase ollama {sshTaskArgs}"),
            new Lane("hv4-kill", Exe("Hv4RecoveryRunner", "hv4-recovery-runner"),
                $"--warchief {warchief} --phase kill {sshTaskArgs}"),
            new Lane("hv4-disconnect", Exe("Hv4RecoveryRunner", "hv4-recovery-runner"),
                $"--warchief {warchief} --phase disconnect {sshTaskArgs}"),

            new Lane("hv5", Exe("Hv5TelemetrySweepRunner", "hv5-telemetry-sweep-runner"),
                $"--warchief {warchief} --phase all {sshLogArgs}"),
        ];
    }

    // ── Fleet reconfiguration ──────────────────────────────────────────────────

    /// <summary>
    /// Rewrites HIVE__NATIVECONTEXTSIZE in each worker's start script and restarts it, then proves
    /// the value took by reading it back.
    ///
    /// This is the ONE thing this driver does that is not "invoke a runner and read its evidence",
    /// and it is deliberate: HV-6 requires the campaign to run 3x with no manual intervention
    /// between runs, and HV-2's large phase is a fleet configuration rather than a job shape. An
    /// operator reconfiguring the fleet halfway through IS the intervention the requirement forbids.
    ///
    /// It is box administration, not HIVE traffic — the same ssh-driven shape HV-4 already uses for
    /// killing and firewalling workers, and the reason that pattern is trusted here is that HV-4's
    /// try/finally restore worked on every run and was verified clean afterwards each time.
    ///
    /// Verified, not assumed. A silent no-op here would leave HV-2's large phase running against a
    /// config where nothing can be denied — which is exactly how it reported the low-VRAM box
    /// completing a job it was supposed to refuse.
    /// </summary>
    private static async Task<bool> SetFleetContextSizeAsync(Fleet f, int contextSize)
    {
        Console.WriteLine($"  ⚙ reconfiguring fleet to NATIVECONTEXTSIZE={contextSize} …");
        var ok = true;

        foreach (var (ssh, dir, task) in new[]
        {
            (f.WorkerASsh, f.WorkerADir, f.WorkerATask),
            (f.WorkerBSsh, f.WorkerBDir, f.WorkerBTask),
        })
        {
            var script = $@"{dir}\start-worker.bat";
            // One call: rewrite, restart, read back. Fewer ssh round trips is not cosmetic here —
            // HardcoreLaptopMSI's sshd drops out for minutes at a time while the box stays healthy,
            // and every extra hop is another chance to land half a reconfiguration.
            var readBack = Ssh(ssh,
                "powershell -NoProfile -Command \"" +
                $"(Get-Content '{script}') -replace 'NATIVECONTEXTSIZE=\\d+','NATIVECONTEXTSIZE={contextSize}' " +
                $"| Set-Content '{script}'; " +
                $"Stop-ScheduledTask -TaskName {task} -ErrorAction SilentlyContinue; Start-Sleep -Seconds 3; " +
                "Get-Process theorc-warband -ErrorAction SilentlyContinue | Stop-Process -Force; " +
                $"Start-Sleep -Seconds 2; Start-ScheduledTask -TaskName {task}; Start-Sleep -Seconds 12; " +
                $"(Select-String -Path '{script}' -Pattern 'NATIVECONTEXTSIZE=(\\d+)').Matches.Groups[1].Value\"");

            var applied = readBack.Trim();
            if (applied != contextSize.ToString())
            {
                Console.WriteLine($"     ✗ {ssh}: start script reports '{applied}', expected {contextSize}");
                ok = false;
            }
        }

        // The script says the right thing; now confirm the worker actually came back, because a
        // reconfigured-but-dead worker fails every following lane for a reason that looks nothing
        // like a config problem.
        foreach (var node in new[] { f.WorkerANode, f.WorkerBNode })
        {
            if (await WaitForTelemetryAsync(node, TimeSpan.FromMinutes(3))) continue;
            Console.WriteLine($"     ✗ {node}: worker did not come back after restart");
            ok = false;
        }

        Console.WriteLine(ok ? "     ✓ fleet reconfigured" : "     ✗ fleet reconfiguration INCOMPLETE");
        return ok;
    }

    private static async Task<bool> WaitForTelemetryAsync(string nodeUrl, TimeSpan within)
    {
        var deadline = DateTime.UtcNow + within;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var resp = await http.GetAsync($"{nodeUrl.TrimEnd('/')}/hive/native-telemetry");
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { /* still down */ }
            await Task.Delay(3000);
        }
        return false;
    }

    private static string Ssh(string host, string command)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=15");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        // Drained but discarded: current OpenSSH writes a post-quantum advisory to stderr on every
        // connection, and folding it into the result makes every parsed value wrong.
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit(180_000);
        return stdout;
    }

    // ── Child process plumbing ─────────────────────────────────────────────────

    private static async Task<(int ExitCode, string Stdout)> RunLaneAsync(Lane lane)
    {
        var psi = new ProcessStartInfo(lane.ExePath, lane.Args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        using var p = Process.Start(psi)!;
        // Read both streams concurrently. Reading one to completion first can deadlock the child
        // once the other pipe's buffer fills, and these lanes are chatty enough to reach it.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr)) stdout += "\n[stderr]\n" + stderr;
        return (p.ExitCode, stdout);
    }

    private static string? ParseVerdict(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Verdict:", StringComparison.Ordinal))
                return t["Verdict:".Length..].Trim();
        }
        return null;
    }

    private static string? ParseEvidencePath(string stdout)
    {
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("Evidence written:", StringComparison.Ordinal))
                return t["Evidence written:".Length..].Trim();
        }
        return null;
    }

    /// <summary>
    /// The failing check names, so a failed round is actionable straight from this report instead of
    /// requiring the reader to go open the lane's evidence file.
    /// </summary>
    private static List<string> ParseFailedChecks(string stdout)
    {
        var failed = new List<string>();
        foreach (var line in stdout.Split('\n'))
        {
            var t = line.Trim();
            if (!t.StartsWith("check[", StringComparison.Ordinal)) continue;
            // Lane runners print "check[worker] name: PASS — detail" (em dash) — match on the
            // verdict token rather than splitting on punctuation that varies between runners.
            if (t.Contains(": FAIL", StringComparison.Ordinal))
                failed.Add(t);
        }
        return failed;
    }

    // ── Human summary ──────────────────────────────────────────────────────────

    private static string BuildSummary(Hv6Report report)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# HV-6 — repeatability + fleet report");
        sb.AppendLine();
        sb.AppendLine($"- Warchief: `{report.Warchief}`");
        sb.AppendLine($"- Rounds: {report.Rounds.Count} of {report.RoundsRequested} requested");
        sb.AppendLine($"- Started: {report.StartedAt:u}");
        sb.AppendLine($"- Finished: {report.FinishedAt:u}");
        if (report.SkippedLanes.Count > 0)
            sb.AppendLine($"- **Lanes SKIPPED (not covered by this report): {string.Join(", ", report.SkippedLanes)}**");
        sb.AppendLine();
        sb.AppendLine($"> {report.FlipClaim}");
        sb.AppendLine();

        // Lane × round matrix — the shape that makes intermittency obvious at a glance, which is
        // the whole reason HV-6 runs the campaign more than once.
        sb.AppendLine("| Lane | " + string.Join(" | ", report.Rounds.Select(r => $"R{r.Round}")) + " |");
        sb.AppendLine("|---|" + string.Join("|", report.Rounds.Select(_ => "---")) + "|");
        foreach (var lane in report.Lanes)
        {
            var cells = report.Rounds.Select(r =>
            {
                var res = r.Lanes.FirstOrDefault(l => l.Lane == lane);
                return res is null ? "—" : res.Verdict.StartsWith("PASS", StringComparison.Ordinal) ? "PASS" : "**FAIL**";
            });
            sb.AppendLine($"| `{lane}` | " + string.Join(" | ", cells) + " |");
        }
        sb.AppendLine();
        sb.AppendLine($"**Verdict: {(report.Passed ? "PASS" : "FAIL")}** — "
                      + (report.Passed
                          ? $"every lane green in all {report.Rounds.Count} rounds, no intervention between rounds."
                          : "see the failing checks below."));

        var failures = report.Rounds
            .SelectMany(r => r.Lanes.Where(l => !l.Verdict.StartsWith("PASS", StringComparison.Ordinal))
                .Select(l => (r.Round, l)))
            .ToList();
        if (failures.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Failures");
            foreach (var (round, lane) in failures)
            {
                sb.AppendLine();
                sb.AppendLine($"### R{round} `{lane.Lane}` — {lane.Verdict} (exit {lane.ExitCode})");
                if (lane.EvidencePath is not null) sb.AppendLine($"Evidence: `{lane.EvidencePath}`");
                foreach (var f in lane.FailedChecks) sb.AppendLine($"- {f}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Evidence files");
        foreach (var round in report.Rounds)
            foreach (var lane in round.Lanes.Where(l => l.EvidencePath is not null))
                sb.AppendLine($"- R{round.Round} `{lane.Lane}`: `{lane.EvidencePath}`");

        return sb.ToString();
    }

    private static string? GetArg(string[] args, string name)
    {
        var idx = Array.IndexOf(args, name);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}

// ── DTOs ───────────────────────────────────────────────────────────────────────

/// <param name="ContextSize">
/// The fleet NativeContextSize this lane requires, or null for the standard configuration.
/// HV-2's `large` phase is not a job shape — it is a FLEET CONFIGURATION, and its own docs say to
/// run it "against a fleet already reconfigured (NativeContextSize env var) and restarted for that
/// phase". Against the standard config it cannot deny anything and reports the low-VRAM box
/// completing a job it was supposed to refuse.
/// </param>
internal sealed record Lane(string Name, string ExePath, string Args, int? ContextSize = null);

internal sealed class Fleet
{
    public string WorkerAId { get; init; } = "";
    public string WorkerANode { get; init; } = "";
    public string WorkerASsh { get; init; } = "";
    public string WorkerATask { get; init; } = "";
    public string WorkerALog { get; init; } = "";
    public string WorkerBId { get; init; } = "";
    public string WorkerBNode { get; init; } = "";
    public string WorkerBSsh { get; init; } = "";
    public string WorkerBTask { get; init; } = "";
    public string WorkerBLog { get; init; } = "";
    /// <summary>Checkout directory holding each worker's start-worker.bat, for context-size edits.</summary>
    public string WorkerADir { get; init; } = "";
    public string WorkerBDir { get; init; } = "";
    /// <summary>Which box HV-2's `large` phase expects to DENY admission — the smaller card.</summary>
    public string LowVramWorkerId { get; init; } = "";
}

internal sealed class Hv6LaneResult
{
    public string Lane { get; set; } = "";
    public int ExitCode { get; set; }
    public string Verdict { get; set; } = "";
    public string? EvidencePath { get; set; }
    public List<string> FailedChecks { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int DurationSeconds { get; set; }
}

internal sealed class Hv6Round
{
    public int Round { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<Hv6LaneResult> Lanes { get; set; } = [];
    public bool Passed { get; set; }
}

internal sealed class Hv6Report
{
    public string Warchief { get; set; } = "";
    public int RoundsRequested { get; set; }
    public List<string> Lanes { get; set; } = [];
    public List<string> SkippedLanes { get; set; } = [];
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<Hv6Round> Rounds { get; set; } = [];
    public bool Passed { get; set; }
    /// <summary>In-band disclaimer, so this verdict cannot be quoted as a §6 flip authorisation.</summary>
    public string FlipClaim { get; set; } = "";
    /// <summary>How many times the fleet's context size was rewritten during this campaign.</summary>
    public int Reconfigurations { get; set; }
    /// <summary>Whether the fleet was left on the standard context size. A false here means a box
    /// needs manual attention before the next campaign.</summary>
    public bool FleetRestored { get; set; }
}
