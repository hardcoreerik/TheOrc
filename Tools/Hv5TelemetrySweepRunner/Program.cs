// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using OrchestratorIDE.Services.Hive;

namespace Hv5TelemetrySweepRunner;

/// <summary>
/// HV-5 driver (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md): telemetry consistency across
/// machines, a no-silent-fallback sweep, and the diagnosability drill. Same evidence-JSON shape as
/// the HV-1/HV-2/HV-3/HV-4 runners so HV-6 can drive them all identically.
///
///   --phase telemetry  ONE shared campaign fanned out across every configured worker, with each
///                      box's reservation/residency/measured-VRAM snapshot collected centrally.
///                      Asserts the snapshots have the SAME SHAPE from every box -- §6 asks for
///                      consistent telemetry across machines, and a field that exists on one box
///                      and not another is exactly the gap HV-2 and HV-3 each found one level down.
///   --phase sweep      Greps every box's worker log for silent-fallback markers and for the
///                      standing NoKvSlot check. Zero hits required.
///   --phase diagnose   Induces one real failure per box and then asks the question HV-5 actually
///                      poses: is what was RETAINED enough to identify the cause without
///                      interactive debugging? Measured by asserting on the retained artifacts
///                      only -- the task's own error text and the box's telemetry counters.
///   --phase all        All three, in that order.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Markers that would mean a native job quietly became an Ollama job. §6 forbids this
    /// outright, so the sweep looks for the substitution itself rather than for a tidy log line
    /// somebody remembered to add -- any mention of an Ollama runtime name or endpoint in a worker
    /// log that was only ever asked for native work is the signal.
    /// </summary>
    private static readonly string[] FallbackMarkers =
    [
        "OllamaRuntime",
        "falling back to Ollama",
        "fallback to Ollama",
        "11434",            // the Ollama HTTP port — a native-only worker has no business dialling it
    ];

    public static async Task<int> Main(string[] args)
    {
        var warchief = GetArg(args, "--warchief") ?? "http://localhost:7079";
        var outDir = GetArg(args, "--out")
                     ?? Path.Combine(Environment.CurrentDirectory, ".orc", "hv-5-lane");
        var phase = GetArg(args, "--phase") ?? "all";
        if (phase is not ("telemetry" or "sweep" or "diagnose" or "all"))
            throw new InvalidOperationException("--phase must be one of: telemetry, sweep, diagnose, all.");
        var timeoutMs = int.TryParse(GetArg(args, "--timeout-ms"), out var t) ? t : 300_000;
        var role = GetArg(args, "--role") ?? "Coder";

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
                LogPath = GetArg(args, $"--worker-{slot}-log"),
            });
        }
        if (workers.Count == 0)
            throw new InvalidOperationException(
                "No workers configured. Pass --worker-a <id> --worker-a-node <http://ip:7078> " +
                "--worker-a-ssh <host> --worker-a-log <path> (and -b/-c).");

        Directory.CreateDirectory(outDir);

        var report = new Hv5Report
        {
            Warchief = warchief,
            Phase = phase,
            StartedAt = DateTimeOffset.UtcNow,
            Workers = workers.Select(w => w.Id).ToList(),
        };

        using var http = new HttpClient
        {
            BaseAddress = new Uri(warchief),
            Timeout = TimeSpan.FromMinutes(10),
        };

        try
        {
            if (phase is "telemetry" or "all") await RunTelemetryAsync(http, workers, report, role, timeoutMs);
            if (phase is "sweep" or "all") RunSweep(workers, report);
            if (phase is "diagnose" or "all") await RunDiagnoseAsync(http, workers, report, role, timeoutMs);

            report.FinishedAt = DateTimeOffset.UtcNow;
            report.Passed = report.Checks.Count > 0 && report.Checks.All(c => c.Passed);
        }
        catch (Exception ex)
        {
            report.Error = ex.ToString();
            report.Passed = false;
        }

        var outPath = Path.Combine(outDir, $"hv5_{phase}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await File.WriteAllTextAsync(outPath,
            JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));

        Console.WriteLine();
        foreach (var c in report.Checks)
            Console.WriteLine($"  check[{c.WorkerId}] {c.Name}: {(c.Passed ? "PASS" : "FAIL")} — {c.Detail}");
        Console.WriteLine($"Verdict: {(report.Passed ? "PASS" : "FAIL")}");
        Console.WriteLine($"Evidence written: {outPath}");
        return report.Passed ? 0 : 2;
    }

    // ── Phase: one shared campaign, telemetry collected centrally ──────────────

    private static async Task RunTelemetryAsync(
        HttpClient http, List<WorkerTarget> workers, Hv5Report report, string role, int timeoutMs)
    {
        Console.WriteLine("[telemetry] dispatching ONE campaign across every worker...");

        // Deliberately one campaign with one work unit per box, rather than a campaign per box:
        // §6 asks for consistent telemetry across machines FOR A SHARED WORKLOAD, and separate
        // campaigns would let per-campaign differences masquerade as per-machine ones.
        var campaign = new CampaignDefinition
        {
            Name = "hv5-telemetry",
            WorkUnits = workers.Select(w => new WorkUnit
            {
                WorkUnitId = $"hv5-tel-{w.Id}",
                Title = $"HV-5 shared-campaign unit on {w.Id}",
                Role = role,
                ExecutionKind = HiveExecutionKinds.NativeAgent,
                Requirements = new ResourceRequirements
                {
                    ExcludedWorkerIds = workers
                        .Where(x => !string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Id).ToArray(),
                },
                Spec = "Create a file named hv5_proof.txt in the workspace root containing exactly " +
                       $"this single line and nothing else: HV5-PROOF {w.Id}",
                TimeoutMs = timeoutMs,
            }).ToList(),
        };

        using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
            resp.EnsureSuccessStatusCode();

        foreach (var w in workers)
        {
            var taskId = $"{campaign.CampaignId}-hv5-tel-{w.Id}";
            var last = await PollToTerminalAsync(http, taskId, timeoutMs);
            var snapshot = await SnapshotAsync(w);
            report.Snapshots.Add(snapshot);
            report.Jobs.Add(new Hv5JobEvidence
            {
                TaskId = taskId,
                WorkerId = w.Id,
                Role = role,
                Status = last?.Status ?? "unknown",
                ClaimedBy = last?.ClaimedBy,
                ClaimedByExpected = string.Equals(last?.ClaimedBy, w.Id, StringComparison.OrdinalIgnoreCase),
                RuntimeName = last?.Attestation?.RuntimeName,
                IsNativeRuntime = last?.Attestation?.RuntimeName == "NativeRoleRuntime",
                ErrorMsg = last?.ErrorMsg,
            });
            Console.WriteLine($"  [{last?.Status ?? "unknown"}] {taskId}");

            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "telemetry/shared-campaign-unit-ran-natively-here",
                Passed = last?.Status == "completed"
                         && last.Attestation?.RuntimeName == "NativeRoleRuntime"
                         && string.Equals(last.ClaimedBy, w.Id, StringComparison.OrdinalIgnoreCase),
                Detail = $"status={last?.Status ?? "none"}, runtime={last?.Attestation?.RuntimeName ?? "none"}, "
                         + $"claimedBy={last?.ClaimedBy ?? "none"}",
            });

            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "telemetry/snapshot-is-complete-and-physical",
                // "Reachable and non-degenerate": a box that answers with totalBytes 0 or a
                // reservedBytes above its own card is publishing numbers no one can act on, which
                // is the diagnosability half of §6 failing quietly. Both shapes have been seen for
                // real -- the reservation-snapshot double-count published 11.04 GB against a
                // 6.44 GB card and nothing rejected it.
                Passed = snapshot.Reachable
                         && snapshot.TotalBytes > 0
                         && snapshot.ReservedBytes >= 0
                         && snapshot.ReservedBytes <= snapshot.TotalBytes,
                Detail = snapshot.Reachable
                    ? $"total={snapshot.TotalBytes}, reserved={snapshot.ReservedBytes}, "
                      + $"available={snapshot.AvailableBytes}, roles reserved={snapshot.ReservedRoleCount}, "
                      + $"residency entries={snapshot.ResidencyCount}"
                    : "telemetry endpoint unreachable",
            });
        }

        // Cross-machine SHAPE agreement, evaluated once rather than per box: every snapshot must
        // carry the same field set. This is the check that would have caught the gap this lane was
        // built to close -- residency existing in-process on one box and nowhere else.
        var fieldSets = report.Snapshots.Where(s => s.Reachable).Select(s => s.PresentFields).ToList();
        var shapesAgree = fieldSets.Count > 1
                          && fieldSets.Skip(1).All(f => f.SetEquals(fieldSets[0]));
        report.Checks.Add(new Hv5Check
        {
            WorkerId = "(fleet)",
            Name = "telemetry/same-schema-from-every-box",
            Passed = shapesAgree,
            Detail = fieldSets.Count <= 1
                ? $"only {fieldSets.Count} box(es) answered — cross-machine consistency cannot be "
                  + "evaluated from a single snapshot"
                : shapesAgree
                    ? $"all {fieldSets.Count} boxes published the same field set: "
                      + string.Join(", ", fieldSets[0].OrderBy(x => x))
                    : "field sets differ across boxes: "
                      + string.Join(" || ", report.Snapshots.Where(s => s.Reachable)
                          .Select(s => $"{s.WorkerId}={string.Join(",", s.PresentFields.OrderBy(x => x))}")),
        });
    }

    // ── Phase: no-silent-fallback + NoKvSlot log sweep ─────────────────────────

    private static void RunSweep(List<WorkerTarget> workers, Hv5Report report)
    {
        foreach (var w in workers)
        {
            if (string.IsNullOrWhiteSpace(w.SshHost) || string.IsNullOrWhiteSpace(w.LogPath))
            {
                report.Checks.Add(new Hv5Check
                {
                    WorkerId = w.Id,
                    Name = "sweep/log-reachable",
                    Passed = false,
                    Detail = "no --worker-*-ssh / --worker-*-log configured, so this box's log was "
                             + "never swept — recorded as a FAILURE rather than skipped, because an "
                             + "unswept box in a 'zero fallback markers' claim is the claim being wrong",
                });
                continue;
            }

            Console.WriteLine($"[sweep] {w.Id}: reading {w.LogPath}...");

            // Read the log once and match locally rather than running a grep per marker: fewer ssh
            // round-trips, and the retained Detail can quote what was actually found.
            var log = Ssh(w.SshHost!,
                $"powershell -NoProfile -Command \"if (Test-Path '{w.LogPath}') " +
                $"{{ Get-Content '{w.LogPath}' -Raw }} else {{ '__MISSING__' }}\"");

            var missing = log.Contains("__MISSING__", StringComparison.Ordinal) || log.Length == 0;
            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "sweep/log-reachable",
                Passed = !missing,
                Detail = missing ? $"could not read {w.LogPath}" : $"read {log.Length} chars from {w.LogPath}",
            });
            if (missing) continue;

            var hits = FallbackMarkers
                .Where(m => log.Contains(m, StringComparison.OrdinalIgnoreCase))
                .ToList();
            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "sweep/no-silent-fallback-markers",
                Passed = hits.Count == 0,
                Detail = hits.Count == 0
                    ? $"none of [{string.Join(", ", FallbackMarkers)}] present"
                    : $"FOUND: {string.Join(", ", hits)}",
            });

            // The standing NoKvSlot check. Not a fallback, but the plan requires it before trusting
            // any numbers from a run: a KV-slot exhaustion silently degrades output quality, and
            // CF-7 scores were once read as model capability when they were this.
            var kvHits = log.Contains("NoKvSlot", StringComparison.OrdinalIgnoreCase);
            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "sweep/no-nokvslot",
                Passed = !kvHits,
                Detail = kvHits
                    ? "NoKvSlot present — numbers from this box must not be trusted until explained"
                    : "no NoKvSlot occurrences",
            });
        }
    }

    // ── Phase: diagnosability drill ────────────────────────────────────────────

    private static async Task RunDiagnoseAsync(
        HttpClient http, List<WorkerTarget> workers, Hv5Report report, string role, int timeoutMs)
    {
        foreach (var w in workers)
        {
            Console.WriteLine($"[diagnose] {w.Id}: inducing a native failure...");

            // Induced failure: demand a native model hash the box cannot possibly hold. This is
            // reversible, needs no reconfiguration of the box, and exercises the exact path §6
            // cares about -- the runtime must refuse rather than quietly serve the job some other
            // way. A VRAM-based inducement was rejected for this: an impossible MinVramMb is
            // filtered at CLAIM time, so the job would sit pending and produce no diagnostics at
            // all, which tests the scheduler rather than the runtime's failure reporting.
            var unitId = $"hv5-diag-{w.Id}";
            var campaign = new CampaignDefinition
            {
                Name = $"hv5-diagnose-{w.Id}",
                WorkUnits =
                [
                    new WorkUnit
                    {
                        WorkUnitId = unitId,
                        Title = $"HV-5 induced native failure on {w.Id}",
                        Role = role,
                        ExecutionKind = HiveExecutionKinds.NativeAgent,
                        Requirements = new ResourceRequirements
                        {
                            NativeModelHash = new string('0', 64),
                            ExcludedWorkerIds = workers
                                .Where(x => !string.Equals(x.Id, w.Id, StringComparison.OrdinalIgnoreCase))
                                .Select(x => x.Id).ToArray(),
                        },
                        Spec = "This unit is expected to fail: it demands a model hash no box holds.",
                        TimeoutMs = timeoutMs,
                    },
                ],
            };

            using (var resp = await http.PostAsJsonAsync("/hive/campaigns", campaign, JsonOptions))
                resp.EnsureSuccessStatusCode();

            var taskId = $"{campaign.CampaignId}-{unitId}";
            var last = await PollToTerminalAsync(http, taskId, timeoutMs);
            var after = await SnapshotAsync(w);

            report.Jobs.Add(new Hv5JobEvidence
            {
                TaskId = taskId,
                WorkerId = w.Id,
                Role = role,
                Status = last?.Status ?? "unknown",
                ClaimedBy = last?.ClaimedBy,
                RuntimeName = last?.Attestation?.RuntimeName,
                IsNativeRuntime = last?.Attestation?.RuntimeName == "NativeRoleRuntime",
                ErrorMsg = last?.ErrorMsg,
            });

            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "diagnose/failure-is-terminal-not-hung",
                Passed = last?.Status is "failed" or "timeout",
                Detail = $"status={last?.Status ?? "none"}",
            });

            // This is the actual HV-5 question, and it is deliberately asked of the RETAINED text
            // only: someone reading the evidence file cold, with no access to the box and no live
            // debugger, must be able to name the cause. An empty or generic error fails even if the
            // job correctly refused to run.
            var err = last?.ErrorMsg ?? "";
            var namesTheCause = err.Length > 0
                                && (err.Contains("hash", StringComparison.OrdinalIgnoreCase)
                                    || err.Contains("model", StringComparison.OrdinalIgnoreCase)
                                    || err.Contains("native", StringComparison.OrdinalIgnoreCase));
            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "diagnose/retained-error-identifies-the-cause",
                Passed = namesTheCause,
                Detail = err.Length == 0
                    ? "no error text was retained — the cause is not recoverable from the evidence alone"
                    : $"retained error: \"{Truncate(err, 300)}\"",
            });

            // Fail CLOSED: refusing is only correct if it also did not quietly succeed some other
            // way. A completed status here would be a silent fallback by definition.
            report.Checks.Add(new Hv5Check
            {
                WorkerId = w.Id,
                Name = "diagnose/no-fallback-on-induced-failure",
                Passed = last?.Status != "completed",
                Detail = $"status={last?.Status ?? "none"}, runtime={last?.Attestation?.RuntimeName ?? "none"} "
                         + $"(post-failure rejectedAdmissionCount={after.RejectedAdmissionCount}, "
                         + $"lastRejectionReason=\"{Truncate(after.LastRejectionReason ?? "", 200)}\")",
            });
        }
    }

    // ── Shared helpers ─────────────────────────────────────────────────────────

    private static async Task<Hv5Snapshot> SnapshotAsync(WorkerTarget w)
    {
        var raw = await TryFetchRawAsync(w.NodeUrl);
        if (raw is null)
            return new Hv5Snapshot { WorkerId = w.Id, At = DateTimeOffset.UtcNow, Reachable = false };

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        // Field names are read off the LIVE payload rather than off a deserialized DTO. A DTO would
        // silently normalize away exactly the difference this check exists to find: a box omitting
        // a property still deserializes into a default, and the shapes would always "agree".
        var present = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        long Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64() : -1;
        int Count(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.GetArrayLength() : -1;

        return new Hv5Snapshot
        {
            WorkerId = w.Id,
            At = DateTimeOffset.UtcNow,
            Reachable = true,
            TotalBytes = Num("totalBytes"),
            ReservedBytes = Num("reservedBytes"),
            AvailableBytes = Num("availableBytes"),
            RejectedAdmissionCount = Num("rejectedAdmissionCount"),
            LastRejectionReason =
                root.TryGetProperty("lastRejectionReason", out var lr) && lr.ValueKind == JsonValueKind.String
                    ? lr.GetString() : null,
            ReservedRoleCount = Count("reservations"),
            ResidencyCount = Count("residency"),
            PresentFields = present,
            Raw = raw,
        };
    }

    private static async Task<string?> TryFetchRawAsync(string nodeUrl)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var resp = await http.GetAsync($"{nodeUrl.TrimEnd('/')}/hive/native-telemetry");
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync();
        }
        catch { return null; }
    }

    private static async Task<HiveTaskStatusResponse?> PollToTerminalAsync(
        HttpClient http, string taskId, int timeoutMs)
    {
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

    /// <summary>
    /// stdout only, for the same reason as the HV-4 driver: current OpenSSH writes a multi-line
    /// post-quantum advisory to stderr on every connection, and folding it into the returned text
    /// makes every parsed remote result wrong.
    /// </summary>
    private static string Ssh(string host, string command)
    {
        var psi = new ProcessStartInfo("ssh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("ConnectTimeout=10");
        psi.ArgumentList.Add(host);
        psi.ArgumentList.Add(command);

        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        _ = p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return stdout;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

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
    public string? LogPath { get; init; }
}

internal sealed class Hv5Snapshot
{
    public string WorkerId { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public bool Reachable { get; set; }
    public long TotalBytes { get; set; }
    public long ReservedBytes { get; set; }
    public long AvailableBytes { get; set; }
    public long RejectedAdmissionCount { get; set; }
    public string? LastRejectionReason { get; set; }
    public int ReservedRoleCount { get; set; }
    public int ResidencyCount { get; set; }
    public HashSet<string> PresentFields { get; set; } = new(StringComparer.Ordinal);
    /// <summary>The untouched payload, kept so a later reader can re-derive anything this driver
    /// did not think to extract. Cheap, and the alternative is re-running the fleet.</summary>
    public string? Raw { get; set; }
}

internal sealed class Hv5Check
{
    public string WorkerId { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Passed { get; set; }
    public string Detail { get; set; } = "";
}

internal sealed class Hv5JobEvidence
{
    public string TaskId { get; set; } = "";
    public string WorkerId { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string? ClaimedBy { get; set; }
    public bool ClaimedByExpected { get; set; }
    public string? RuntimeName { get; set; }
    public bool IsNativeRuntime { get; set; }
    public string? ErrorMsg { get; set; }
}

internal sealed class Hv5Report
{
    public string Warchief { get; set; } = "";
    public string Phase { get; set; } = "";
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public List<string> Workers { get; set; } = [];
    public List<Hv5Snapshot> Snapshots { get; set; } = [];
    public List<Hv5JobEvidence> Jobs { get; set; } = [];
    public List<Hv5Check> Checks { get; set; } = [];
    public bool Passed { get; set; }
    public string? Error { get; set; }
}
