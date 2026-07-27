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
        };

        // Opt-out for lanes a given environment cannot currently drive. Named rather than silently
        // skipped: a lane left out of an "all green, 3 rounds" claim has to be visible IN the report,
        // or the report is a lie by omission. HardcoreLaptopMSI's sshd instability is the live
        // example — it blocks that box's disconnect phase without touching anything in the product.
        var skip = (GetArg(args, "--skip") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var lanes = BuildLanes(toolsRoot, warchief, fleet, modelHash)
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

        for (var round = 1; round <= rounds; round++)
        {
            Console.WriteLine();
            Console.WriteLine($"═══ ROUND {round} of {rounds} ═══");
            var roundResult = new Hv6Round { Round = round, StartedAt = DateTimeOffset.UtcNow };

            foreach (var lane in lanes)
            {
                Console.WriteLine($"  ── {lane.Name} …");
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
    private static List<Lane> BuildLanes(string toolsRoot, string warchief, Fleet f, string modelHash)
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

            new Lane("hv2-large", Exe("Hv2SchedulingRunner", "hv2-scheduling-runner"),
                $"--warchief {warchief} --phase large --worker-a {f.WorkerAId} --worker-b {f.WorkerBId}"),
            new Lane("hv2-small", Exe("Hv2SchedulingRunner", "hv2-scheduling-runner"),
                $"--warchief {warchief} --phase small --worker-a {f.WorkerAId} --worker-b {f.WorkerBId}"),

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

internal sealed record Lane(string Name, string ExePath, string Args);

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
}
