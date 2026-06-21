// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// swarmcli — headless TheOrc swarm runner.
//
// Three modes:
//   1. Local (default)         — runs SwarmSession against local Ollama, no HIVE.
//   2. --warchief               — opens a HiveTaskQueue and dispatches the swarm's
//                                 tasks to remote --worker instances instead of
//                                 running them locally.
//   3. --worker --warchief-url  — polls a remote Warchief and executes tasks as
//                                 they arrive; runs until Ctrl+C.
//
// Local mode runs SwarmSession without the WPF/Avalonia shell so capture runs can
// be driven from a terminal or script. DatasetCapture stages qualifying boss plans
// exactly as it does in the app (immediately after decomposition, before workers
// run), so --plan-only is the fastest capture loop: decompose -> stage -> stop.
//
// Usage:
//   swarmcli --goal "<text>" [--workspace <dir>] [--plan-only]
//            [--boss <model>] [--coder <model>] [--researcher <model>]
//            [--host <url>] [--timeout <seconds>]
//
//   swarmcli --warchief --goal "<text>" [--workspace <dir>] [--port 7079]
//            [--boss <model>] [--coder <model>] [--researcher <model>]
//            [--host <url>] [--timeout <seconds>]
//
//   swarmcli --worker --warchief-url <url> [--lanes coder,researcher]
//            [--host <url>] [--worker-id <name>]
//
// Exit codes (local + --warchief modes):
//   0  capture staged (good or bad — reviewable either way)
//   1  run error / boss returned no tasks
//   2  run finished but nothing staged (marginal 40–69 plan)
//   3  timeout
//
// Exit codes (--worker mode):
//   0  clean shutdown (Ctrl+C)
//   1  failed to start (bad --warchief-url, etc.)

using System.IO;
using System.Linq;
using System.Text;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Hive;

Console.OutputEncoding = Encoding.UTF8;

// Boot secret protection before any HIVE identity/secret access (pairing, signing,
// --show-identity). Mirrors App.axaml.cs. swarmcli is Windows-only (net10.0-windows),
// so DPAPI is the right protector, same as the GUI's Windows branch.
SecretProtection.Initialize(new DpapiSecretProtector());

// ── Args ─────────────────────────────────────────────────────────────────────

string? goal         = null;
string  workspace    = Directory.GetCurrentDirectory();
string  boss         = "theorc-boss:gemma4";
string  coder        = "qwen2.5-coder:7b";
string? researcher   = null;
string  host         = "http://localhost:11434";
int     timeoutSec   = 600;
bool    planOnly     = false;
bool    warchiefMode = false;
bool    workerMode   = false;
bool    showIdentity = false;
bool    pairMode     = false;
int     warchiefPort = HiveTaskQueue.QueuePort;
string? warchiefUrl  = null;
string? warchiefNodeId = null;
string  workerId     = Environment.MachineName;
string  lanesArg     = "";
string? pairTarget   = null;
string? expectFp     = null;
var     allowFps     = new List<string>();

for (int i = 0; i < args.Length; i++)
{
    string? Next() => i + 1 < args.Length ? args[++i] : null;
    switch (args[i])
    {
        case "--goal":          goal         = Next();          break;
        case "--workspace":     workspace    = Next() ?? workspace; break;
        case "--boss":          boss         = Next() ?? boss;  break;
        case "--coder":         coder        = Next() ?? coder; break;
        case "--researcher":    researcher   = Next();          break;
        case "--host":          host         = Next() ?? host;  break;
        case "--timeout":       timeoutSec   = int.TryParse(Next(), out var t) ? t : timeoutSec; break;
        case "--plan-only":     planOnly     = true;            break;
        case "--warchief":      warchiefMode = true;            break;
        case "--worker":        workerMode   = true;            break;
        case "--port":          warchiefPort = int.TryParse(Next(), out var p) ? p : warchiefPort; break;
        case "--warchief-url":  warchiefUrl  = Next();          break;
        case "--warchief-nodeid": warchiefNodeId = Next();      break;
        case "--worker-id":     workerId     = Next() ?? workerId; break;
        case "--lanes":         lanesArg     = Next() ?? "";    break;
        case "--show-identity": showIdentity = true;            break;
        case "--pair":          pairMode     = true;            break;
        case "--target":        pairTarget   = Next();          break;
        case "--expect-fingerprint": expectFp = Next();         break;
        case "--allow-fingerprint":
            { var fp = Next(); if (fp is not null) allowFps.Add(fp.Trim()); break; }
        case "-h" or "--help":
            Console.WriteLine("""
                swarmcli — headless TheOrc swarm runner

                Local mode (default):
                  swarmcli --goal "<text>" [--workspace <dir>] [--plan-only]
                           [--boss <m>] [--coder <m>] [--researcher <m>] [--host <url>] [--timeout <s>]

                Show this node's HIVE identity (NodeId + fingerprint, for out-of-band verification):
                  swarmcli --show-identity

                Pair with a Warchief (initiator side; refuses unless the response fingerprint
                matches --expect-fingerprint, which you obtain from the target out-of-band):
                  swarmcli --pair --target <host> --expect-fingerprint "<8-word phrase>"

                Warchief mode (dispatches to remote workers; opens pairing + task queue):
                  swarmcli --warchief --goal "<text>" [--workspace <dir>] [--port 7079]
                           [--allow-fingerprint "<phrase>" ...]   (auto-approve pairing from these)
                           [--boss <m>] [--coder <m>] [--researcher <m>] [--host <url>] [--timeout <s>]

                Worker mode (polls a remote Warchief, runs until Ctrl+C; must be paired first):
                  swarmcli --worker --warchief-url <url> [--warchief-nodeid <id>]
                           [--lanes coder,researcher] [--host <url>] [--worker-id <name>]
                  (pass --warchief-nodeid from `swarmcli --show-identity` on the Warchief for
                   robust request signing — avoids IP-vs-hostname shared-secret lookup misses)
                """);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument: {args[i]} (try --help)");
            return 1;
    }
}

if (warchiefMode && workerMode)
{
    Console.Error.WriteLine("--warchief and --worker are mutually exclusive.");
    return 1;
}

// ── --show-identity — print this node's NodeId + fingerprint and exit ─────────

if (showIdentity)
{
    var id = HiveIdentity.Load();
    Console.WriteLine($"NodeId:      {id.NodeId}");
    Console.WriteLine($"Fingerprint: {id.Fingerprint}");
    Console.WriteLine($"Machine:     {Environment.MachineName}");
    return 0;
}

// ── --pair — initiator side of the pairing ceremony, fingerprint-gated ────────

if (pairMode)
{
    if (string.IsNullOrWhiteSpace(pairTarget))
    {
        Console.Error.WriteLine("--pair requires --target <host> (the Warchief's host/IP, no scheme/port)");
        return 1;
    }
    if (string.IsNullOrWhiteSpace(expectFp))
    {
        Console.Error.WriteLine(
            "--pair requires --expect-fingerprint \"<phrase>\" — obtain the target's fingerprint " +
            "out-of-band first (run `swarmcli --show-identity` on the target). This is the only " +
            "defense against an on-path attacker; pairing without it is refused.");
        return 1;
    }

    Console.WriteLine($"swarmcli --pair — target: {pairTarget}");
    Console.WriteLine($"  expecting fingerprint: {expectFp}");
    Console.WriteLine("  sending pairing request, waiting for the target to approve…");

    var result = await HivePairingClient.PairAsync(pairTarget, timeoutSec: Math.Max(timeoutSec, 30));

    switch (result.Outcome)
    {
        case HivePairingClient.Outcome.Approved when result.Pending is { } pending:
            // The CLI gate, equivalent to the GUI's fingerprint-comparison confirmation:
            // only trust if the fingerprint the target returned matches the one the operator
            // independently obtained and passed in --expect-fingerprint. A forged/MITM'd
            // response carries the attacker's fingerprint, which won't match.
            var got = (pending.Fingerprint ?? "").Trim();
            var want = expectFp.Trim();
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"  ✗ FINGERPRINT MISMATCH — refusing to trust.");
                Console.Error.WriteLine($"    expected: {want}");
                Console.Error.WriteLine($"    got:      {got}");
                Console.Error.WriteLine("    This could mean an on-path attacker, or a wrong --expect-fingerprint value.");
                return 1;
            }
            HivePairingClient.ConfirmAndTrust(pending);
            Console.WriteLine($"  ✓ Paired with {pairTarget} (fingerprint verified). Shared secret stored.");
            return 0;

        case HivePairingClient.Outcome.AlreadyPaired:
            Console.WriteLine($"  Already paired with {pairTarget}.");
            return 0;
        case HivePairingClient.Outcome.Rejected:
            Console.Error.WriteLine("  ✗ Target rejected the pairing request.");
            return 1;
        case HivePairingClient.Outcome.Expired:
            Console.Error.WriteLine("  ✗ Pairing request expired before it was approved.");
            return 1;
        case HivePairingClient.Outcome.TimedOut:
            Console.Error.WriteLine("  ✗ Timed out waiting for approval.");
            return 1;
        default:
            Console.Error.WriteLine($"  ✗ Pairing failed: {result.Message}");
            return 1;
    }
}

// ── Worker mode — polls a remote Warchief, no local swarm/goal involved ──────

if (workerMode)
{
    if (string.IsNullOrWhiteSpace(warchiefUrl))
    {
        Console.Error.WriteLine("--worker requires --warchief-url <url>");
        return 1;
    }
    // Fail fast on a malformed URL rather than letting HiveWorkerAgent retry a bad
    // address forever (it swallows poll errors) -- headless automation needs the
    // misconfiguration surfaced, not a silent hang (Codex CLI MINOR, 2026-06-20).
    if (!Uri.TryCreate(warchiefUrl, UriKind.Absolute, out _))
    {
        Console.Error.WriteLine($"--warchief-url is not a valid absolute URL: {warchiefUrl}");
        return 1;
    }

    // Resolve the Warchief's NodeId so HiveWorkerAgent.SignIfPaired() does a NodeId-based
    // shared-secret lookup (immune to IP-vs-hostname/Tailscale-name mismatches). Without
    // this it falls back to host-string matching against LastKnownAddress; if pairing used
    // an IP but the worker connects by hostname (or vice versa), the lookup misses, requests
    // go UNSIGNED, and the fail-closed queue 401s every claim -- a correctly paired worker
    // that silently can't take work (Codex CLI BLOCKER, 2026-06-20). Prefer the explicit
    // --warchief-nodeid (the skill passes the value from `swarmcli --show-identity` on the
    // Warchief); otherwise resolve via HivePeerStore.ResolveNodeIdForUrl -- the same shared
    // helper MainWindow.axaml.cs uses, which also falls back through DNS resolution if no
    // exact host-string match is found (Codex CLI BLOCKER, 2026-06-21: an earlier inline
    // version here lacked that DNS fallback, a second, drifting copy of the GUI's logic).
    if (string.IsNullOrWhiteSpace(warchiefNodeId))
        warchiefNodeId = HivePeerStore.Default.ResolveNodeIdForUrl(warchiefUrl);

    // A worker with no resolvable, actually-paired Warchief secret sends unsigned requests
    // forever -- the fail-closed queue 401s every claim and the process just idles with no
    // indication anything is wrong. Fail startup instead (Codex CLI BLOCKER, 2026-06-20).
    var pairedPeer = string.IsNullOrEmpty(warchiefNodeId) ? null : HivePeerStore.Default.Find(warchiefNodeId);
    if (pairedPeer is null || pairedPeer.Revoked || string.IsNullOrEmpty(pairedPeer.SharedSecretEnc))
    {
        Console.Error.WriteLine($"  ✗ No trusted, paired Warchief found for '{warchiefUrl}'.");
        Console.Error.WriteLine("    Pair with it first: `swarmcli --pair --target <warchief-host> --expect-fingerprint \"<phrase>\"`");
        Console.Error.WriteLine("    or pass --warchief-nodeid explicitly if it's already paired under a different address.");
        return 1;
    }

    var lanes = string.IsNullOrWhiteSpace(lanesArg)
        ? []
        : lanesArg.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToArray();

    var worker = new HiveWorkerAgent
    {
        WorkerId       = workerId,
        WorkerUrl      = host,
        WarchiefUrl    = warchiefUrl,
        WarchiefNodeId = warchiefNodeId ?? "",
        Lanes          = lanes,
        Runtime        = new OllamaRuntime(new OllamaClient(host)),
    };
    worker.OnLog          += msg => Console.WriteLine($"  {msg}");
    worker.OnTaskActivity += (taskId, msg) => Console.WriteLine($"  [{taskId}] {msg}");

    Console.WriteLine($"swarmcli --worker — id: {workerId}  warchief: {warchiefUrl}");
    Console.WriteLine($"  lanes: {(lanes.Length == 0 ? "(all)" : string.Join(", ", lanes))}  ollama: {host}");
    Console.WriteLine("Polling for tasks. Ctrl+C to stop.");
    Console.WriteLine();

    worker.Start();

    var stopSignal = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true; // we shut down gracefully ourselves instead of an abrupt process kill
        stopSignal.TrySetResult();
    };
    await stopSignal.Task;

    Console.WriteLine();
    Console.WriteLine("Shutting down…");
    await worker.ShutdownAsync();
    return 0;
}

// ── Local + Warchief modes — both run a goal through SwarmSession ───────────

if (string.IsNullOrWhiteSpace(goal))
{
    Console.Error.WriteLine("Missing required --goal");
    return 1;
}

workspace = Path.GetFullPath(workspace);
if (!Directory.Exists(workspace))
{
    Console.Error.WriteLine($"Workspace does not exist: {workspace}");
    return 1;
}

researcher ??= coder;

Console.WriteLine($"swarmcli{(warchiefMode ? " --warchief" : "")} — workspace: {workspace}");
Console.WriteLine($"  boss: {boss}  coder: {coder}  researcher: {researcher}");
Console.WriteLine($"  mode: {(planOnly ? "plan-only (stop after boss plan capture)" : "full run")}  timeout: {timeoutSec}s");
Console.WriteLine($"  goal: {goal}");
Console.WriteLine();

// ── Staging snapshot (to report what this run added) ─────────────────────────

var stagingDir = Path.Combine(workspace, ".orc", "swarm", "dataset-staging");
var before = Directory.Exists(stagingDir)
    ? Directory.GetFiles(stagingDir).Select(Path.GetFileName).ToHashSet()
    : [];

// ── Run ──────────────────────────────────────────────────────────────────────

var ollama  = new OllamaClient(host);
var session = new SwarmSession(new OllamaRuntime(ollama), boss, workspace, coder, researcher);

HiveTaskQueue? queue = null;
HiveNodeServer? nodeServer = null;
if (warchiefMode)
{
    // Pairing/identity server (port 7078) — workers must pair here before the task
    // queue (7079) will accept their authenticated claims. Assumes the GUI app is NOT
    // also running on this machine (it would already hold 7078).
    nodeServer = new HiveNodeServer();
    nodeServer.OnPairingRequestReceived += (sessionId, req) =>
    {
        var fp = (req.InitiatorFingerprint ?? "").Trim();
        // req.InitiatorFingerprint is recomputed server-side from the signing key, so it's
        // trustworthy, not attacker-supplied. Auto-approve ONLY fingerprints the operator
        // pre-authorized via --allow-fingerprint (obtained out-of-band over trusted SSH).
        var allowed = allowFps.Any(a => string.Equals(a, fp, StringComparison.OrdinalIgnoreCase));
        if (allowed)
        {
            Console.WriteLine($"  [warchief] ✓ auto-approving pairing from {req.InitiatorName} ({fp})");
            nodeServer!.ApprovePairing(sessionId, HiveNodeRole.Worker, [], isMobile: false);
        }
        else
        {
            Console.WriteLine($"  [warchief] ✗ rejecting pairing from {req.InitiatorName} — fingerprint not in --allow-fingerprint list ({fp})");
            nodeServer!.RejectPairing(sessionId);
        }
    };
    // Rewrite a localhost/127.0.0.1 Ollama URL to a LAN-reachable address before advertising
    // it to peers -- mirrors MainWindow.axaml.cs's identical rewrite. Otherwise this node
    // publishes "http://localhost:11434", which resolves to THAT remote peer's own loopback,
    // not this machine (Codex CLI MINOR, 2026-06-21).
    var ollamaUrlForPeers = host;
    if ((ollamaUrlForPeers.Contains("localhost") || ollamaUrlForPeers.Contains("127.0.0.1"))
        && Uri.TryCreate(ollamaUrlForPeers, UriKind.Absolute, out var ollamaUri))
    {
        // Grok CLI BLOCKER, 2026-06-21: `new Uri(...)` directly would throw UriFormatException
        // if --host lacks a scheme (e.g. "localhost:11434") while still containing the
        // substring match above -- TryCreate fails closed (skip the rewrite) instead of crashing.
        var lanIp = HiveRpcWorker.LocalAddresses().FirstOrDefault();
        if (lanIp is not null)
            ollamaUrlForPeers = $"http://{lanIp}:{ollamaUri.Port}";
    }
    var info = new HiveNodeInfo(
        Environment.MachineName, ollamaUrlForPeers, [], 0, 0,
        ["inference", "coder", "researcher"], 0);
    nodeServer.Start(info);
    // Start() can fail to bind (port in use, ACL/permission denied) and only logs internally,
    // leaving IsListening false with no exception thrown -- check explicitly rather than
    // telling the operator pairing is live when --pair would just time out against a dead
    // endpoint (Codex CLI BLOCKER, 2026-06-20).
    if (!nodeServer.IsListening)
    {
        Console.Error.WriteLine($"  [warchief] ✗ FAILED to bind pairing server on :{HiveNodeServer.ApiPort} " +
            "(port in use, or insufficient permission for the URL ACL). Pairing will not work.");
        nodeServer.Dispose(); // Grok CLI MINOR, 2026-06-21: was left allocated on this path.
        return 1;
    }
    // IsListening alone doesn't mean remote pairing works -- Start() can fall back to a
    // localhost-only bind (no admin/URL ACL) and still report IsListening == true. Only
    // IsRemoteReachable means another machine can actually reach this (Codex CLI BLOCKER,
    // 2026-06-20 -- the earlier message claimed "listening" without this distinction).
    if (!nodeServer.IsRemoteReachable)
        Console.WriteLine($"  [warchief] ⚠ pairing server bound to LOCALHOST ONLY on :{HiveNodeServer.ApiPort} " +
            "(no admin rights / no URL ACL reservation). Remote machines cannot pair with this node. " +
            "Run OrchestratorSetup to open the firewall/ACL, or run elevated.");
    else
        Console.WriteLine($"  [warchief] pairing server listening on :{HiveNodeServer.ApiPort}" +
            (allowFps.Count > 0 ? $" — auto-approving {allowFps.Count} fingerprint(s)" : " — no --allow-fingerprint set, all pairing rejected"));

    queue = new HiveTaskQueue();
    queue.OnLog += msg => Console.WriteLine($"  [warchief] {msg}");
    queue.Start(new HiveSessionContext
    {
        SessionId      = Environment.MachineName,
        ProjectGoal    = goal,
        CoderModel     = coder,
        ResearcherModel = researcher,
    }, warchiefPort);

    // Same silent-bind-failure shape as HiveNodeServer above (Codex CLI MINOR, 2026-06-20) --
    // without this check, distributed mode proceeds, prints worker instructions for a dead
    // queue, and every task sits for the full 60s pending timeout before falling back local.
    if (!queue.IsListening)
    {
        Console.Error.WriteLine($"  [warchief] ✗ FAILED to bind task queue on :{warchiefPort} " +
            "(port in use, or insufficient permission for the URL ACL). Distributed dispatch will not work.");
        nodeServer.Dispose();
        queue.Dispose(); // Grok CLI MINOR, 2026-06-21: watchdog timer + listener were left allocated.
        return 1;
    }

    Console.WriteLine();
    if (queue.IsRemoteReachable)
    {
        var addrs = HiveRpcWorker.LocalAddresses();
        Console.WriteLine("Warchief listening. Point workers at one of these, e.g.:");
        foreach (var a in addrs)
            Console.WriteLine($"  swarmcli --worker --warchief-url http://{a}:{warchiefPort}");
        Console.WriteLine($"  (tailscale)  swarmcli --worker --warchief-url {queue.BaseUrl.Replace("localhost", "<this-machine-tailscale-name>")}");
    }
    else
    {
        // Codex CLI BLOCKER, 2026-06-20: don't advertise LAN/Tailscale worker URLs when the
        // queue only bound to localhost -- remote workers would just time out against them.
        Console.WriteLine($"  [warchief] ⚠ task queue bound to LOCALHOST ONLY on :{warchiefPort} -- " +
            "only a worker on THIS machine can connect. Remote workers cannot dispatch here.");
    }
    Console.WriteLine();

    session.SetDistributedQueue(queue);
}

bool planSeen = false, errored = false, stopRequested = false;

session.OnActivity += (agent, msg) => Console.WriteLine($"  [{agent}] {msg}");
session.OnError    += msg =>
{
    // Our own plan-only Stop() surfaces as "Swarm stopped." — not an error
    if (stopRequested && msg.Contains("stopped", StringComparison.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  [{msg.TrimEnd('.')}]");
        return;
    }
    errored = true;
    Console.Error.WriteLine($"  [ERROR] {msg}");
};

session.OnTasksPlanned += tasks =>
{
    planSeen = true;
    Console.WriteLine();
    Console.WriteLine($"── Boss plan: {tasks.Count} task(s) ──");
    foreach (var t in tasks)
        Console.WriteLine($"  • {t.Role,-12} {t.Title}");
    Console.WriteLine();

    if (planOnly)
    {
        Console.WriteLine("Plan captured (staging happens before this event) — stopping run.");
        stopRequested = true;
        session.Stop();
    }
};

session.OnSwarmComplete += _ => Console.WriteLine("Swarm complete.");

var runTask  = session.RunAsync(goal!);
var finished = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));

bool timedOut = finished != runTask;
if (timedOut)
{
    Console.Error.WriteLine($"Timeout after {timeoutSec}s — stopping run.");
    stopRequested = true;
    session.Stop();
    await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(10)));
}
else
{
    try { await runTask; }
    catch (OperationCanceledException) { /* expected for plan-only stop */ }
    catch (Exception ex) { errored = true; Console.Error.WriteLine($"  [ERROR] {ex.Message}"); }
}

queue?.Dispose();
nodeServer?.Dispose();

// ── Report staged captures ───────────────────────────────────────────────────

var added = Directory.Exists(stagingDir)
    ? Directory.GetFiles(stagingDir).Select(Path.GetFileName)
        .Where(f => f is not null && !before.Contains(f)).Cast<string>().OrderBy(f => f).ToList()
    : [];

Console.WriteLine();
if (added.Count > 0)
{
    Console.WriteLine($"Staged {added.Count} capture(s):");
    foreach (var f in added)
        Console.WriteLine($"  {Path.Combine(".orc", "swarm", "dataset-staging", f)}");
    return 0;
}

if (timedOut)            { Console.WriteLine("No capture staged (timeout).");                return 3; }
if (errored || !planSeen){ Console.WriteLine("No capture staged (run error / no plan).");    return 1; }
Console.WriteLine("No capture staged — plan likely scored marginal (40–69, silently skipped).");
return 2;
