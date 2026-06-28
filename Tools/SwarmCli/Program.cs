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
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Services.Models;

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
bool    noRun        = false;
string? nativeTestGgufPath = null;
string? nativeCompareGgufPath = null;
string? nativeCompareOllamaModel = null;
string? nativeGgufPath     = null;
string? nativeDownloadQuery = null;
string? nativeDownloadQuant = null;
string? nativeDownloadModelRoot = null;
bool    nativeDownloadNoOllamaRegister = false;
int     nativeRepeatCount  = 1;
string? expectFile         = null;
int     warchiefPort = HiveTaskQueue.QueuePort;
string? warchiefUrl  = null;
string? warchiefNodeId = null;
string  workerId     = Environment.MachineName;
string  lanesArg     = "";
string? pairTarget   = null;
string? expectFp     = null;
var     allowFps     = new List<string>();
bool    listPeers      = false;
bool    declareWarchief = false;
string? setAcceptControlPeer   = null;
string? setAcceptControlPolicy = null;

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
        case "--no-run":        noRun        = true;            break;
        case "--native-test":   nativeTestGgufPath = Next();    break;
        case "--native-compare": nativeCompareGgufPath = Next(); break;
        case "--ollama-model":  nativeCompareOllamaModel = Next(); break;
        case "--native":        nativeGgufPath     = Next();    break;
        case "--native-download": nativeDownloadQuery = Next(); break;
        case "--quant":         nativeDownloadQuant = Next();   break;
        case "--model-root":    nativeDownloadModelRoot = Next(); break;
        case "--no-ollama-register": nativeDownloadNoOllamaRegister = true; break;
        case "--native-repeat": nativeRepeatCount  = int.TryParse(Next(), out var nr) ? nr : nativeRepeatCount; break;
        case "--expect-file":   expectFile         = Next();    break;
        case "--target":        pairTarget   = Next();          break;
        case "--expect-fingerprint": expectFp = Next();         break;
        case "--allow-fingerprint":
            { var fp = Next(); if (fp is not null) allowFps.Add(fp.Trim()); break; }
        case "--list-peers":        listPeers       = true; break;
        case "--declare-warchief":  declareWarchief = true; break;
        case "--set-accept-control":
            setAcceptControlPeer   = Next();
            setAcceptControlPolicy = Next();
            break;
        case "-h" or "--help":
            Console.WriteLine("""
                swarmcli — headless TheOrc swarm runner

                Local mode (default):
                  swarmcli --goal "<text>" [--workspace <dir>] [--plan-only]
                           [--boss <m>] [--coder <m>] [--researcher <m>] [--host <url>] [--timeout <s>]

                Local mode, native runtime (LLamaSharp) instead of Ollama for ALL roles
                (boss/coder/researcher) -- LLamaSharpRuntime.StreamCompletionAsync ignores its
                `model` argument and always runs whatever's currently loaded, so this can't be
                split per-role with today's SwarmSession API; one native model serves every
                role. Tests real SwarmSession tool-calling reliability under native runtime,
                not just a raw completion (--native-test does that):
                  swarmcli --goal "<text>" --native <path-to-gguf-or-ollama-blob> [--workspace <dir>]

                Show this node's HIVE identity (NodeId + fingerprint, for out-of-band verification):
                  swarmcli --show-identity

                Headless native-runtime smoke test (same prompt/checks as the GUI Settings
                "Run Native Test" button, no Ollama fallback -- just the native attempt):
                  swarmcli --native-test <path-to-gguf-or-ollama-blob>

                Download a native GGUF into TheOrc's model folder using the same HuggingFace
                search/download stack as the GUI model downloader. QUERY may be either an exact
                HF repo id (e.g. Qwen/Qwen2.5-Coder-14B-Instruct-GGUF) or a search phrase
                (e.g. qwen coder 14b). For gated repos, auth is picked up automatically from
                AppSettings.HuggingFaceAccessToken, HUGGING_FACE_HUB_TOKEN / HF_TOKEN, or an
                existing `hf auth login` token on this machine. By default the recommended
                quant is chosen and the GGUF is also registered into Ollama after download so
                it feels closer to "ollama pull":
                  swarmcli --native-download <query-or-hf-repo>
                           [--quant <Q4_K_M>] [--model-root <dir>] [--no-ollama-register]

                Headless native-vs-Ollama parity corpus. Loads one GGUF into the native
                runtime, runs a deterministic comparison set through BOTH runtimes, prints
                per-case pass/fail, and writes a JSON report under .orc/native-runtime-parity:
                  swarmcli --native-compare <path-to-gguf-or-ollama-blob> --ollama-model <model>
                           [--host <url>] [--workspace <dir>]

                Repeat the same goal N times against ONE loaded native model (loaded once, not
                reloaded per iteration) and report a per-iteration file outcome plus a summary --
                built for gathering real data on tool-calling reliability under retry (e.g. a
                goal asking for one specific file sometimes produces a wrong-extension file on
                the retry path instead of the requested one; one run isn't enough to call that a
                pattern). --expect-file checks each iteration's staged output against an exact
                name for automated pass/fail; omit it to just see what each iteration staged:
                  swarmcli --goal "<text>" --native <path> --native-repeat <N> [--expect-file <name>]
                           [--workspace <dir>] [--timeout <s>]

                Pair with a Warchief (initiator side; refuses unless the response fingerprint
                matches --expect-fingerprint, which you obtain from the target out-of-band):
                  swarmcli --pair --target <host> --expect-fingerprint "<8-word phrase>"

                List paired peers and their hive role / AcceptControlFrom policy
                (HIVE_MEMBERSHIP_SPEC.md §2.4, §6):
                  swarmcli --list-peers

                Declare this machine the hive's Warchief -- broadcasts a role-assignment
                request to every currently paired peer asking it to become a Worker
                (HIVE_MEMBERSHIP_SPEC.md §6.3; same action as the GUI's "Declare this machine
                Warchief" context-menu item):
                  swarmcli --declare-warchief

                Change a paired peer's AcceptControlFrom policy (Never|Ask|Allowlist|AnyPaired)
                -- governs whether that peer's role-assignment requests need a per-event
                approval. Identify the peer by NodeId (full or any unique prefix) or by name:
                  swarmcli --set-accept-control <nodeId-or-name> <policy>

                Warchief mode (dispatches to remote workers; opens pairing + task queue):
                  swarmcli --warchief --goal "<text>" [--workspace <dir>] [--port 7079]
                           [--allow-fingerprint "<phrase>" ...]   (auto-approve pairing from these)
                           [--boss <m>] [--coder <m>] [--researcher <m>] [--host <url>] [--timeout <s>]

                Warchief, pairing/queue-server only -- no swarm run at all (no --goal needed,
                immune to boss/coder model-not-found or ask_user hangs on a remote machine
                with different installed models). Use this when the only thing this machine
                needs to do is be a pairing responder / accept dispatched tasks:
                  swarmcli --warchief --no-run [--port 7079] [--timeout <s>]
                           [--allow-fingerprint "<phrase>" ...]

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

if (nativeRepeatCount > 1)
{
    if (nativeGgufPath is null)
    {
        Console.Error.WriteLine("--native-repeat requires --native <path>.");
        return 1;
    }
    if (string.IsNullOrWhiteSpace(goal))
    {
        Console.Error.WriteLine("--native-repeat requires --goal \"<text>\".");
        return 1;
    }
    if (warchiefMode || workerMode)
    {
        Console.Error.WriteLine("--native-repeat is local-only (not compatible with --warchief/--worker).");
        return 1;
    }
}

if (warchiefMode && workerMode)
{
    Console.Error.WriteLine("--warchief and --worker are mutually exclusive.");
    return 1;
}
if (nativeDownloadNoOllamaRegister && nativeDownloadQuery is null)
{
    Console.Error.WriteLine("--no-ollama-register only applies to --native-download.");
    return 1;
}
if (noRun && !warchiefMode)
{
    Console.Error.WriteLine("--no-run only applies to --warchief (pairing/queue-server only, no swarm run).");
    return 1;
}

if (nativeDownloadQuery is not null)
{
    return await NativeDownloadCli.RunAsync(
        nativeDownloadQuery,
        nativeDownloadQuant,
        nativeDownloadModelRoot,
        nativeDownloadNoOllamaRegister);
}

// ── --show-identity — print this node's NodeId + fingerprint and exit ─────────

if (showIdentity)
{
    var id = HiveIdentity.Load();
    Console.WriteLine($"NodeId:      {id.NodeId}");
    Console.WriteLine($"Fingerprint: {id.Fingerprint}");
    Console.WriteLine($"Machine:     {Environment.MachineName}");
    Console.WriteLine($"HiveId:      {(string.IsNullOrEmpty(id.HiveId) ? "(none -- not yet founded/joined a hive)" : id.HiveId)}");
    Console.WriteLine($"HiveRole:    {id.HiveRole}");
    Console.WriteLine($"SelfRole:    {id.SelfRole}  (role this node's pairing approver granted it)");
    Console.WriteLine($"CanIssueMembershipCerts: {id.CanIssueMembershipCerts}");
    Console.WriteLine($"OwnMembershipCert:       {(string.IsNullOrEmpty(id.OwnMembershipCertJson) ? "(none)" : "present")}");
    return 0;
}

// ── --list-peers — print paired peers and their hive-relevant fields ─────────

if (listPeers)
{
    var peers = HivePeerStore.Default.All();
    if (peers.Count == 0)
    {
        Console.WriteLine("No paired peers.");
        return 0;
    }
    foreach (var p in peers)
    {
        Console.WriteLine($"{p.Name}  ({p.NodeId[..Math.Min(16, p.NodeId.Length)]}…)");
        Console.WriteLine($"  Role: {p.Role}   MaxRole: {p.MaxRole}   AcceptControlFrom: {p.AcceptControlFrom}");
        Console.WriteLine($"  LastKnownAddress: {(string.IsNullOrEmpty(p.LastKnownAddress) ? "(unknown)" : p.LastKnownAddress)}" +
                           $"   Mobile: {p.IsMobile}   Revoked: {p.Revoked}");
        Console.WriteLine();
    }
    return 0;
}

// ── --declare-warchief — broadcast a role-assignment request to every peer ───
// HIVE_MEMBERSHIP_SPEC.md §6.3. Headless equivalent of HivePanel.DeclareWarchiefAsync.

if (declareWarchief)
{
    var peers = HivePeerStore.Default.All().Where(p => !p.Revoked).ToList();
    if (peers.Count == 0)
    {
        Console.Error.WriteLine("No paired peers to promote -- pair with at least one node first.");
        return 1;
    }

    Console.WriteLine($"swarmcli --declare-warchief — broadcasting role-assign (Worker) to {peers.Count} peer(s)…");
    var identity = HiveIdentity.Load();
    var anyFailed = false;
    foreach (var peer in peers)
    {
        var outcome = await HiveNodeServer.SendRoleAssignAsync(peer, HiveNodeRole.Worker, identity, HivePeerStore.Default);
        Console.WriteLine($"  {peer.Name}: {outcome}");
        if (outcome == "unreachable" || outcome.StartsWith("error:")) anyFailed = true;
    }
    return anyFailed ? 1 : 0;
}

// ── --set-accept-control — change a paired peer's AcceptControlFrom policy ────

if (setAcceptControlPeer is not null)
{
    if (setAcceptControlPolicy is null ||
        !Enum.TryParse<HiveAcceptControlPolicy>(setAcceptControlPolicy, ignoreCase: true, out var policy))
    {
        Console.Error.WriteLine(
            "--set-accept-control <nodeId-or-name> <policy> requires a valid policy: " +
            "Never | Ask | Allowlist | AnyPaired");
        return 1;
    }

    var match = HivePeerStore.Default.All().FirstOrDefault(p =>
        string.Equals(p.Name, setAcceptControlPeer, StringComparison.OrdinalIgnoreCase) ||
        p.NodeId.StartsWith(setAcceptControlPeer, StringComparison.OrdinalIgnoreCase));
    if (match is null)
    {
        Console.Error.WriteLine($"No paired peer matches '{setAcceptControlPeer}' (by name or NodeId prefix).");
        return 1;
    }

    match.AcceptControlFrom = policy;
    HivePeerStore.Default.AddOrUpdate(match);
    Console.WriteLine($"{match.Name}: AcceptControlFrom set to {policy}.");
    return 0;
}

// ── --native-test — headless equivalent of the GUI's Settings "Run Native Test"
// button. Calls the exact same NativeRuntimeTestRunner.RunLocalAsync API the GUI uses
// (OrchestratorIDE.Avalonia/UI/Panels/SettingsPanel.axaml.cs's BtnRunNativeRuntimeTest_Click)
// so this validates the real native-inference path without needing a GUI session or
// computer-use (which needs a live human to approve access -- unavailable unattended).

if (nativeTestGgufPath is not null)
{
    if (!File.Exists(nativeTestGgufPath))
    {
        Console.Error.WriteLine($"--native-test: file not found: {nativeTestGgufPath}");
        return 1;
    }

    Console.WriteLine($"swarmcli --native-test — model: {nativeTestGgufPath}");
    Console.WriteLine($"  prompt: {NativeRuntimeTestPrompt.PromptText.Replace("\n", " / ")}");
    Console.WriteLine();

    var attempt = await NativeRuntimeTestRunner.RunLocalAsync(
        nativeTestGgufPath,
        onToken: t => Console.Write(t));

    Console.WriteLine();
    Console.WriteLine();
    Console.WriteLine($"Runtime:  {attempt.RuntimeName}");
    Console.WriteLine($"Success:  {attempt.Success}");
    if (!attempt.Success)
        Console.WriteLine($"Error:    {attempt.ErrorType} — {attempt.ErrorMessage}");
    Console.WriteLine($"Health:   available={attempt.Health.IsAvailable}  {attempt.Health.Message}");
    Console.WriteLine($"Stats:    {attempt.Stats.TokensPerSecond:F1} tok/s, " +
        $"ttft={attempt.Stats.LastTimeToFirstToken?.TotalMilliseconds:F0}ms, " +
        $"vram~{(attempt.Stats.EstimatedVramBytes is { } vramBytes ? $"{vramBytes / 1024 / 1024}MB" : "n/a")}");

    return attempt.Success ? 0 : 1;
}

// ── --native-compare — deterministic native-vs-Ollama parity corpus ─────────

if (nativeCompareGgufPath is not null)
{
    if (!File.Exists(nativeCompareGgufPath))
    {
        Console.Error.WriteLine($"--native-compare: file not found: {nativeCompareGgufPath}");
        return 1;
    }

    if (string.IsNullOrWhiteSpace(nativeCompareOllamaModel))
    {
        Console.Error.WriteLine("--native-compare requires --ollama-model <model>.");
        return 1;
    }

    Console.WriteLine($"swarmcli --native-compare — native model: {nativeCompareGgufPath}");
    Console.WriteLine($"  ollama model: {nativeCompareOllamaModel}");
    Console.WriteLine($"  corpus: {NativeRuntimeComparisonCorpus.DefaultCorpusName} ({NativeRuntimeComparisonCorpus.DefaultCases.Count} cases)");
    Console.WriteLine();

    await using var compareNativeRuntime = new LLamaSharpRuntime();
    var load = await compareNativeRuntime.LoadModelAsync(nativeCompareGgufPath);
    if (!load.Success)
    {
        Console.Error.WriteLine($"--native-compare: failed to load native model: {load.Message}");
        return 1;
    }

    var ollamaRuntime = new OllamaRuntime(new OllamaClient(host));
    var report = await NativeRuntimeComparisonRunner.RunAsync(
        compareNativeRuntime,
        load.ModelRef,
        ollamaRuntime,
        nativeCompareOllamaModel);
    var reportPath = await NativeRuntimeComparisonReportStore.WriteAsync(report, workspace);

    foreach (var result in report.Results)
    {
        var verdict = result.BothMatchedExpectation && result.CanonicalOutputsMatch ? "MATCH" :
            result.BothMatchedExpectation ? "DRIFT" : "FAIL";
        Console.WriteLine($"[{verdict}] {result.TestCase.CaseId}");
        Console.WriteLine($"  native : {(result.NativeEvaluation.ExpectationMatched ? "PASS" : "FAIL")} -> {result.NativeEvaluation.CanonicalOutput}");
        Console.WriteLine($"  ollama : {(result.OllamaEvaluation.ExpectationMatched ? "PASS" : "FAIL")} -> {result.OllamaEvaluation.CanonicalOutput}");
        if (result.NativeEvaluation.ValidationErrors.Count > 0)
            Console.WriteLine($"  native errors: {string.Join(" | ", result.NativeEvaluation.ValidationErrors)}");
        if (result.OllamaEvaluation.ValidationErrors.Count > 0)
            Console.WriteLine($"  ollama errors: {string.Join(" | ", result.OllamaEvaluation.ValidationErrors)}");
    }

    Console.WriteLine();
    Console.WriteLine("Summary");
    Console.WriteLine($"  native passed : {report.Summary.NativePassedCases}/{report.Summary.TotalCases}");
    Console.WriteLine($"  ollama passed : {report.Summary.OllamaPassedCases}/{report.Summary.TotalCases}");
    Console.WriteLine($"  both passed   : {report.Summary.BothPassedCases}/{report.Summary.TotalCases}");
    Console.WriteLine($"  exact matches : {report.Summary.CanonicalMatches}/{report.Summary.TotalCases}");
    Console.WriteLine($"  report        : {reportPath}");

    return report.Summary.NativePassedCases == report.Summary.TotalCases &&
           report.Summary.OllamaPassedCases == report.Summary.TotalCases &&
           report.Summary.CanonicalMatches == report.Summary.TotalCases
        ? 0
        : 1;
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

// --no-run is the pairing/queue-server-only path -- no goal is ever run, so neither
// requirement applies (a placeholder/default goal or workspace would be misleading here,
// and on a remote machine with different installed models, a real goal can hang forever
// on an unanswerable ask_user prompt with nothing to ever answer it -- found 2026-06-21
// running HARDCOREPC as a headless responder with a goal/model that don't resolve there).
if (string.IsNullOrWhiteSpace(goal) && !noRun)
{
    Console.Error.WriteLine("Missing required --goal");
    return 1;
}

workspace = Path.GetFullPath(workspace);
if (!Directory.Exists(workspace) && !noRun)
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

var ollama = new OllamaClient(host);

IModelRuntime swarmRuntime;
// Plain nullable (not an "await using" declaration) -- C# forbids reassigning a using-declared
// variable (CS1656), and this needs assigning inside the if-block below. Every return site
// from here on that exits before the final fallthrough disposal must explicitly dispose this
// (Grok CLI BLOCKER x5, 2026-06-21: the first version of this loaded a model on several paths
// and never disposed it on any of them -- match the existing queue/nodeServer convention
// already used elsewhere in this file: explicit dispose at each early-return site).
LLamaSharpRuntime? nativeRuntime = null;
// --native is irrelevant to --no-run (a pairing/queue-server-only mode that never touches
// SwarmSession) -- skip loading a model nothing will use, and don't fail --no-run just
// because an unrelated --native path happens to be bad (Grok CLI MINOR, 2026-06-21).
if (nativeGgufPath is not null && !noRun)
{
    if (!File.Exists(nativeGgufPath))
    {
        Console.Error.WriteLine($"--native: file not found: {nativeGgufPath}");
        return 1;
    }
    nativeRuntime = new LLamaSharpRuntime();
    var load = await nativeRuntime.LoadModelAsync(nativeGgufPath);
    if (!load.Success)
    {
        Console.Error.WriteLine($"--native: failed to load model: {load.Message}");
        await nativeRuntime.DisposeAsync();
        return 1;
    }
    Console.WriteLine($"--native: loaded {nativeGgufPath} via LLamaSharp -- " +
        "serving boss/coder/researcher (model strings below are ignored by this runtime).");
    swarmRuntime = nativeRuntime;

    // ── --native-repeat: same goal, N times, one loaded model ───────────────
    // A separate, early-exit path rather than weaving into the single-run flow below --
    // --warchief/--worker/--plan-only/the dataset-staging snapshot are all irrelevant here,
    // and looping the single-run flow in place would mean re-validating all of that machinery
    // N times for no reason.
    if (nativeRepeatCount > 1)
    {
        var runsRoot = Path.Combine(workspace, ".orc", "swarm", "runs");
        var results  = new List<(int Iteration, string[] StagedFiles, bool? Matched)>();

        for (int iter = 1; iter <= nativeRepeatCount; iter++)
        {
            var runsBefore = Directory.Exists(runsRoot)
                ? Directory.GetDirectories(runsRoot).ToHashSet()
                : [];

            Console.WriteLine($"\n── Iteration {iter}/{nativeRepeatCount} ──");
            var iterSession = new SwarmSession(nativeRuntime, boss, workspace, coder, researcher);
            var iterTask = iterSession.RunAsync(goal!);
            try
            {
                var iterDone = await Task.WhenAny(iterTask, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
                if (iterDone != iterTask)
                {
                    Console.Error.WriteLine($"  [iteration {iter}] timeout after {timeoutSec}s — stopping.");
                    iterSession.Stop();
                }
                else
                {
                    await iterTask;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  [iteration {iter}] [ERROR] {ex.Message}");
            }
            finally
            {
                // The shared nativeRuntime/weights are not safe for concurrent inference --
                // the NEXT iteration's RunAsync (or the final DisposeAsync after the loop)
                // must never start while THIS iteration's task might still be mid-InferAsync.
                // Stop() above only requests cancellation; it doesn't wait. A bounded grace
                // delay before this still wasn't enough -- if the task is genuinely still
                // running after Stop(), wait for the real completion, exactly like the
                // single-run --native path already does (Grok CLI BLOCKER, 2026-06-22: the
                // first version of this loop could start iteration N+1 -- or dispose the
                // model entirely -- while iteration N's task was still executing).
                if (!iterTask.IsCompleted)
                {
                    try { await iterTask; } catch { /* already reported above */ }
                }
            }

            // Diff the runs/ dir to find THIS iteration's run folder rather than asking
            // SwarmSession for its own _runId (private, no public accessor -- this stays
            // purely additive from the CLI side instead of changing a core shared class for
            // a diagnostic tool). _runId has second-granularity, so without this delay a
            // fast iteration (cheap goal, small model) could collide with the next one on
            // the same runs/<id>/ folder and corrupt both iterations' file-outcome detection.
            await Task.Delay(1100);
            var newRunDir = Directory.Exists(runsRoot)
                ? Directory.GetDirectories(runsRoot).FirstOrDefault(d => !runsBefore.Contains(d))
                : null;
            var iterStagingDir = newRunDir is not null ? Path.Combine(newRunDir, "staging") : null;
            // Recursive (AllDirectories) -- worker output can land in subdirectories
            // (ExtractAndWriteFiles creates the dirname from each task's relative path), and
            // the main single-run path's own staging scan already does the same to match.
            var stagedFiles = iterStagingDir is not null && Directory.Exists(iterStagingDir)
                ? Directory.GetFiles(iterStagingDir, "*", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(iterStagingDir, f))
                    .OrderBy(f => f).ToArray()
                : [];

            bool? matched = expectFile is null
                ? null
                : stagedFiles.Length == 1 && string.Equals(stagedFiles[0], expectFile, StringComparison.OrdinalIgnoreCase);

            results.Add((iter, stagedFiles, matched));

            var fileSummary = stagedFiles.Length == 0 ? "(no files staged)" : string.Join(", ", stagedFiles);
            var matchSuffix = matched switch { true => "  ✓ matches --expect-file", false => "  ✗ does NOT match --expect-file", null => "" };
            Console.WriteLine($"  [iteration {iter}] staged: {fileSummary}{matchSuffix}");
        }

        Console.WriteLine($"\n── Summary: {nativeRepeatCount} iteration(s) ──");
        foreach (var (iteration, stagedFiles, matched) in results)
        {
            var fileSummary = stagedFiles.Length == 0 ? "(none)" : string.Join(", ", stagedFiles);
            var tag = matched switch { true => "PASS", false => "FAIL", null => "—" };
            Console.WriteLine($"  {iteration,3}: [{tag}] {fileSummary}");
        }
        if (expectFile is not null)
        {
            var passCount = results.Count(r => r.Matched == true);
            Console.WriteLine($"\n  {passCount}/{nativeRepeatCount} matched --expect-file \"{expectFile}\".");
        }

        await nativeRuntime.DisposeAsync();
        return expectFile is not null && results.Any(r => r.Matched != true) ? 1 : 0;
    }
}
else
{
    swarmRuntime = new OllamaRuntime(ollama);
}

var session = new SwarmSession(swarmRuntime, boss, workspace, coder, researcher);

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
        if (nativeRuntime is not null) await nativeRuntime.DisposeAsync();
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
        ProjectGoal    = goal ?? "", // null only under --no-run, where no goal ever runs
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
        if (nativeRuntime is not null) await nativeRuntime.DisposeAsync();
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

if (noRun)
{
    // Pairing/queue-server-only path: nothing left to do but stay alive (the pairing and
    // task-queue handlers above are already running via HttpListener callbacks) until
    // --timeout elapses or the operator hits Ctrl+C. No SwarmSession.RunAsync, no boss/coder
    // activity, no model dependency at all -- this is the headless-pairing-responder mode.
    Console.WriteLine($"--no-run: pairing/queue server only. Waiting up to {timeoutSec}s (Ctrl+C to stop early)...");
    var stopSignal = new TaskCompletionSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopSignal.TrySetResult(); };
    await Task.WhenAny(stopSignal.Task, Task.Delay(TimeSpan.FromSeconds(timeoutSec)));
    Console.WriteLine("Shutting down...");
    queue?.Dispose();
    nodeServer?.Dispose();
    return 0;
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

    // Under --native, the "await using" declaration above disposes nativeRuntime (and its
    // live LLamaWeights/native context) at scope-exit regardless of whether runTask actually
    // finished. Racing a 10s delay is fine for Ollama (an HttpClient), but disposing live
    // unmanaged native weights while a stream/InferAsync call might still be running is a
    // real crash risk, not just a benign ObjectDisposedException -- wait for the real
    // completion in that case instead of moving on after the bound (Grok CLI BLOCKER,
    // 2026-06-21).
    if (nativeRuntime is not null && !runTask.IsCompleted)
    {
        Console.Error.WriteLine("  Waiting for native inference to actually stop before disposing the model...");
        try { await runTask; } catch { /* already reported via stopRequested/errored above */ }
    }
}
else
{
    try { await runTask; }
    catch (OperationCanceledException) { /* expected for plan-only stop */ }
    catch (Exception ex) { errored = true; Console.Error.WriteLine($"  [ERROR] {ex.Message}"); }
}

queue?.Dispose();
nodeServer?.Dispose();
if (nativeRuntime is not null) await nativeRuntime.DisposeAsync();

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

internal static class NativeDownloadCli
{
    public static async Task<int> RunAsync(
        string queryOrRepo,
        string? quant,
        string? modelRootOverride,
        bool noOllamaRegister)
    {
        var settings = AppSettings.Load();
        var modelRoot = !string.IsNullOrWhiteSpace(modelRootOverride)
            ? Path.GetFullPath(modelRootOverride)
            : Path.GetFullPath(settings.ResolvedNativeRuntimeModelRoot);
        Directory.CreateDirectory(modelRoot);

        var userVramGb = settings.DetectedVramGb > 0
            ? (int)Math.Round(settings.DetectedVramGb)
            : 0;

        Console.WriteLine($"swarmcli --native-download - query: {queryOrRepo}");
        Console.WriteLine($"  model root: {modelRoot}");

        using var search = new ModelSearchService(settings: settings);
        using var downloader = new ModelDownloadService(settings: settings);

        var selected = await ResolveModelAsync(search, queryOrRepo.Trim(), userVramGb);
        if (selected is null)
        {
            Console.Error.WriteLine("  No GGUF model search results found.");
            return 1;
        }

        Console.WriteLine($"  selected: {selected.Name}");
        if (!string.IsNullOrWhiteSpace(selected.HuggingFaceId))
            Console.WriteLine($"  huggingface: {selected.HuggingFaceId}");
        if (!string.IsNullOrWhiteSpace(selected.OllamaName))
            Console.WriteLine($"  ollama tag: {selected.OllamaName}");

        var variants = await search.GetVariantsAsync(selected, userVramGb);
        if (variants.Count == 0)
        {
            Console.Error.WriteLine("  Selected model exposes no downloadable GGUF variants.");
            return 1;
        }

        var chosen = ChooseVariant(variants, quant);
        if (chosen is null)
        {
            Console.Error.WriteLine("  Unable to choose a GGUF variant.");
            return 1;
        }

        Console.WriteLine($"  quant: {chosen.QuantLabel}  size: {chosen.SizeDisplay}  est VRAM: {chosen.VramEstimateGb} GB");
        var fileName = Path.GetFileName(chosen.DownloadUrl.Split('?')[0]);
        var destPath = Path.Combine(modelRoot, fileName);
        Console.WriteLine($"  destination: {destPath}");

        if (File.Exists(destPath) && !string.IsNullOrWhiteSpace(chosen.Sha256))
        {
            Console.WriteLine("  Existing file found - verifying SHA-256...");
            if (await downloader.VerifySha256Async(destPath, chosen.Sha256))
            {
                Console.WriteLine("  Existing GGUF already matches expected SHA-256.");
                return await RegisterIfWantedAsync(downloader, selected, destPath, fileName, noOllamaRegister);
            }

            Console.WriteLine("  Existing file hash mismatch - redownloading.");
            try { File.Delete(destPath); } catch { }
        }

        var progress = new Progress<(long done, long total, double speed, int eta)>(p =>
        {
            var pct = p.total > 0 ? (double)p.done / p.total * 100 : 0;
            Console.Write($"\r  downloading {fileName}  {pct,6:F1}%  {FormatBytes(p.done)} / {FormatBytes(p.total)}  {FormatSpeed(p.speed)}  ETA {p.eta}s   ");
        });
        var retry = new Progress<string>(msg =>
        {
            Console.WriteLine();
            Console.WriteLine($"  {msg}");
        });

        await downloader.DownloadAsync(chosen.DownloadUrl, destPath, progress, onRetry: retry);
        Console.WriteLine();

        if (!string.IsNullOrWhiteSpace(chosen.Sha256))
        {
            Console.WriteLine("  Verifying SHA-256...");
            if (!await downloader.VerifySha256Async(destPath, chosen.Sha256))
            {
                try { File.Delete(destPath); } catch { }
                Console.Error.WriteLine("  SHA-256 mismatch - downloaded file was deleted.");
                return 1;
            }
            Console.WriteLine("  SHA-256 verified.");
        }

        return await RegisterIfWantedAsync(downloader, selected, destPath, fileName, noOllamaRegister);
    }

    private static async Task<ModelSearchResult?> ResolveModelAsync(
        ModelSearchService search,
        string trimmed,
        int userVramGb)
    {
        if (trimmed.Contains('/', StringComparison.Ordinal))
        {
            var exact = CuratedModelCatalog.FindByHfId(trimmed);
            if (exact is not null)
            {
                return new ModelSearchResult
                {
                    Id = exact.Id,
                    Name = exact.Name,
                    HuggingFaceId = exact.HuggingFaceId,
                    OllamaName = exact.OllamaName,
                    Publisher = exact.Publisher,
                    Architecture = exact.Architecture,
                    IsCurated = true,
                    IsFromHuggingFace = true,
                    IsFromOllama = !string.IsNullOrWhiteSpace(exact.OllamaName),
                    Description = exact.Description,
                    IntendedUse = exact.IntendedUse,
                    ToolUseNotes = exact.ToolUse,
                    SwarmRoles = exact.SwarmRoles,
                    SwarmCapable = exact.SwarmCapable,
                    QualityStars = exact.QualityStars,
                    RecommendedQuant = exact.RecommendedQuant,
                    VramMinGb = exact.VramMinGb,
                    VramRecommendedGb = exact.VramRecommendedGb,
                    CpuOk = exact.CpuOk,
                    ContextK = exact.ContextK,
                };
            }

            return new ModelSearchResult
            {
                Id = trimmed,
                Name = trimmed.Split('/').Last(),
                HuggingFaceId = trimmed,
                IsCurated = false,
                IsFromHuggingFace = true,
                IsFromOllama = false,
            };
        }

        var status = new List<string>();
        var results = await search.SearchAsync(
            trimmed,
            userVramGb,
            onStatus: status.Add);
        foreach (var msg in status.Distinct())
            Console.WriteLine($"  {msg}");
        return results.FirstOrDefault();
    }

    private static GgufVariant? ChooseVariant(IReadOnlyList<GgufVariant> variants, string? quant)
    {
        if (!string.IsNullOrWhiteSpace(quant))
        {
            var requested = variants
                .Where(v => string.Equals(v.QuantLabel, quant, StringComparison.OrdinalIgnoreCase))
                .OrderBy(v => IsShardedPart(v.Filename) ? 1 : 0)
                .ThenBy(v => v.Filename.Length)
                .FirstOrDefault();
            if (requested is not null)
                return requested;
        }

        return variants
            .Where(v => v.IsRecommended)
            .OrderBy(v => IsShardedPart(v.Filename) ? 1 : 0)
            .ThenBy(v => v.Filename.Length)
            .FirstOrDefault()
            ?? variants
                .OrderBy(v => IsShardedPart(v.Filename) ? 1 : 0)
                .ThenBy(v => v.SizeBytes)
                .FirstOrDefault();
    }

    private static bool IsShardedPart(string filename) =>
        filename.Contains("-000", StringComparison.OrdinalIgnoreCase)
        || filename.Contains(".part", StringComparison.OrdinalIgnoreCase);

    private static async Task<int> RegisterIfWantedAsync(
        ModelDownloadService downloader,
        ModelSearchResult selected,
        string destPath,
        string fileName,
        bool noOllamaRegister)
    {
        if (noOllamaRegister)
        {
            Console.WriteLine("  Download complete. Skipped Ollama registration (--no-ollama-register).");
            return 0;
        }

        var ollamaName = !string.IsNullOrWhiteSpace(selected.OllamaName)
            ? selected.OllamaName
            : Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        Console.WriteLine($"  Registering with Ollama as '{ollamaName}'...");
        var log = new Progress<string>(msg => Console.WriteLine($"    {msg}"));
        var registered = await downloader.RegisterWithOllamaAsync(destPath, ollamaName, log);
        if (registered)
        {
            Console.WriteLine("  Native GGUF ready and registered with Ollama.");
            return 0;
        }

        Console.WriteLine("  Download complete. Ollama registration failed or was skipped by environment.");
        return 0;
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var size = (double)bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {units[unit]}";
    }

    private static string FormatSpeed(double bytesPerSec) =>
        bytesPerSec <= 0 ? "0 B/s" : $"{FormatBytes((long)bytesPerSec)}/s";
}
