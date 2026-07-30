// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OrchestratorIDE.Daemon;
using OrchestratorIDE.Services.Hive;

// ── One-shot CLI modes ───────────────────────────────────────────────────────
// swarmcli (Tools/SwarmCli) already has --pair/--show-identity, but it's net10.0-windows
// with a ProjectReference to the whole OrchestratorIDE.Avalonia/WPF dependency graph just to
// reuse HivePairingClient -- not something to cross-compile onto a headless ARM box without
// real surgery on that project. This daemon already builds and runs cleanly cross-platform
// (confirmed on linux-arm64, a Raspberry Pi 4, 2026-06-21) and already has every Hive type
// these two modes need, so they live here instead -- same fingerprint-gated safety contract
// as swarmcli's --pair, not a separate, looser one.
//
// Pairing direction matters: HiveNodeServer.HandlePairInitiateAsync always fires
// OnPairingRequestReceived and waits for a UI to call ApprovePairing() -- this daemon
// (HiveService.cs) never subscribes to that event, so a pairing request arriving AT this
// node would sit pending until it expires; nothing here can ever approve one. This daemon
// must always be the INITIATOR (--pair --target <gui-machine>), never the responder, until
// a headless approval path exists.
// Same secret-protector wiring HiveService.ExecuteAsync uses -- AesGcmSecretProtector
// ALWAYS, even on Windows, by this project's own deliberate design (see this csproj's
// PropertyGroup comment: "On Windows, users who want DPAPI-native secret storage should
// run the WPF app instead"). NOT a mismatch to "fix" by branching on OS here.
//
// IMPORTANT -- a real collision exists, found 2026-06-21 mid-Pi-pairing, that this design
// comment doesn't call out: HiveIdentity.IdentityPath (%APPDATA%\TheOrc\hive-identity.json)
// is the SAME path on disk for both this daemon AND the GUI app (App.axaml.cs), since
// HiveIdentity.cs is shared source, not a separate per-host file. Running this daemon's CLI
// modes locally on a machine that ALSO runs the GUI -- exactly what happened testing
// --show-identity on NEWCOREPC -- means this AesGcmSecretProtector tries to decrypt a file
// the GUI wrote with DpapiSecretProtector, fails, silently treats it as corrupt, and
// overwrites it with a brand-new identity. The running GUI's in-memory identity is
// unaffected until its next restart, at which point IT will also fail to decrypt (now
// AesGcm-protected) content and generate yet another new one -- recoverable via re-pairing,
// not data loss, but avoidable: don't run these CLI modes against a machine that already
// has a live GUI-owned HIVE identity. Headless-only boxes (this Pi) have no such collision.
if (args.Contains("--show-identity") || args.Contains("--pair") || args.Contains("--leave-hive"))
    SecretProtection.Initialize(new AesGcmSecretProtector(MachineKey.Load()));

if (args.Contains("--show-identity"))
{
    HiveIdentity identity;
    try
    {
        identity = HiveIdentity.Load(regenerateOnCorruption: false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Could not load the existing identity at '{HiveIdentity.IdentityPath}': {ex.Message}");
        Console.Error.WriteLine(
            "Refusing to generate a replacement just to answer --show-identity. If this file is " +
            "genuinely unrecoverable, delete it manually and re-pair from scratch instead.");
        return 1;
    }
    Console.WriteLine($"NodeId: {identity.NodeId}");
    Console.WriteLine($"Fingerprint: {identity.Fingerprint}");
    return 0;
}

// Headless equivalent of the GUI's HivePanel "Leave current hive" item. HiveIdentity.LeaveHive
// existed but was reachable ONLY from the GUI, which strands a headless worker permanently the
// moment its HiveId diverges from the Warchief's: §4.3 refuses to bridge two hives, so pairing
// can never recover it and there is no other operator surface. Observed on both fleet workers
// during HV-3 (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md, 2026-07-25) -- re-pair returned
// "already belongs to a different hive" on machines with no GUI to click.
//
// This does NOT weaken §4.3's "no silent bridge" guarantee. Leaving stays an explicit, deliberate
// operator action -- it just becomes one an operator can perform on a machine that has no screen.
// Pairing still never leaves a hive on its own, and the confirmation gate below keeps this from
// being something a stray script or a mistyped flag can do by accident.
//
// Membership only: signing/exchange keys, NodeId and paired-peer shared secrets all survive
// (see HiveIdentity.LeaveHive). The own-membership cert is cleared because the hive that issued
// it is the one being left.
if (args.Contains("--leave-hive"))
{
    // Reject mixed modes before anything else. `Contains("--leave-hive")` alone does not notice
    // an operator error like `--leave-hive --yes --pair --target host` (a copy-paste leftover, or
    // a genuine attempt to chain leave-then-pair in one call, which this daemon does NOT support
    // atomically -- see the "pair immediately after --leave-hive --yes, before any daemon start"
    // rule this exact footgun motivated elsewhere in the docs). Silently ignoring the extra flags
    // would run leave-hive and drop --pair/--target/--expect-fingerprint/--show-identity on the
    // floor without telling the operator their command did not do what its other half implied.
    var incompatible = new[] { "--pair", "--show-identity" }.Where(args.Contains).ToArray();
    if (incompatible.Length > 0)
    {
        Console.Error.WriteLine(
            $"--leave-hive cannot be combined with {string.Join(", ", incompatible)} in one " +
            "invocation. Run them as separate commands.");
        return 1;
    }

    // Fail CLOSED here, not the lenient default every other caller uses. --leave-hive's entire
    // contract is "clear membership, keep NodeId/keys/peer-secrets unchanged" -- silently
    // regenerating a fresh identity on a transient decrypt failure (the two-AppData-views
    // collision this file's own history documents is a real, observed cause) would make this
    // command leave a hive using an identity that was never actually a member of it: no error, no
    // sign anything unusual happened, and the real identity's membership just abandoned in place.
    HiveIdentity identity;
    try
    {
        identity = HiveIdentity.Load(regenerateOnCorruption: false);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"Could not load the existing identity at '{HiveIdentity.IdentityPath}': {ex.Message}");
        Console.Error.WriteLine(
            "Refusing to proceed with --leave-hive against a freshly-generated identity that was " +
            "never actually a member of any hive. If this file is genuinely unrecoverable, delete " +
            "it manually and re-pair from scratch instead.");
        return 1;
    }
    if (string.IsNullOrEmpty(identity.HiveId))
    {
        Console.WriteLine("This node is not currently in a hive — nothing to leave.");
        return 0;
    }

    // Deliberately requires an explicit confirmation flag rather than acting on --leave-hive
    // alone. The spec frames leaving as a decision a human makes; on a headless box there is no
    // dialog to serve that role, so the second flag is what makes the intent unambiguous.
    if (!args.Contains("--yes"))
    {
        Console.Error.WriteLine(
            $"--leave-hive would abandon hive {identity.HiveId} (role {identity.HiveRole}) on " +
            $"{Environment.MachineName}, resetting membership so this node can pair into a " +
            "different hive. NodeId, keys and existing peer secrets are kept.");
        Console.Error.WriteLine("Re-run with --leave-hive --yes to confirm.");
        return 1;
    }

    var leftHiveId = identity.HiveId;
    identity.LeaveHive();
    Console.WriteLine($"Left hive {leftHiveId}. NodeId {identity.NodeId} unchanged.");
    Console.WriteLine("Pair again with: --pair --target <host> --expect-fingerprint \"<phrase>\"");
    return 0;
}

if (args.Contains("--pair"))
{
    var targetIdx = Array.IndexOf(args, "--target");
    var fpIdx     = Array.IndexOf(args, "--expect-fingerprint");
    if (targetIdx < 0 || targetIdx + 1 >= args.Length)
    {
        Console.Error.WriteLine("--pair requires --target <host> (the GUI machine's host/IP, no scheme/port)");
        return 1;
    }
    if (fpIdx < 0 || fpIdx + 1 >= args.Length)
    {
        Console.Error.WriteLine(
            "--pair requires --expect-fingerprint \"<phrase>\" -- obtain the target's fingerprint " +
            "out-of-band first (its own HIVE panel, click \"This PC\"). This is the only defense " +
            "against an on-path attacker; pairing without it is refused.");
        return 1;
    }

    var target   = args[targetIdx + 1];
    var expectFp = args[fpIdx + 1];

    Console.WriteLine($"Pairing -- target: {target}");
    Console.WriteLine($"  expecting fingerprint: {expectFp}");
    Console.WriteLine("  sending pairing request, waiting for the target to approve…");

    var result = await HivePairingClient.PairAsync(target, timeoutSec: 120);

    switch (result.Outcome)
    {
        case HivePairingClient.Outcome.Approved when result.Pending is { } pending:
            // Same CLI gate as swarmcli's --pair: only trust if the fingerprint the target
            // returned matches the one the operator independently obtained. A forged/MITM'd
            // response carries the attacker's fingerprint, which won't match.
            var got  = (pending.Fingerprint ?? "").Trim();
            var want = expectFp.Trim();
            if (!string.Equals(got, want, StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine("  ✗ FINGERPRINT MISMATCH -- refusing to trust.");
                Console.Error.WriteLine($"    expected: {want}");
                Console.Error.WriteLine($"    got:      {got}");
                return 1;
            }
            HivePairingClient.ConfirmAndTrust(pending);
            Console.WriteLine($"  ✓ Paired with {target} (fingerprint verified). Shared secret stored.");
            return 0;

        case HivePairingClient.Outcome.AlreadyPaired:
            Console.WriteLine($"  Already paired with {target}.");
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

// ── Reject unrecognized flags ────────────────────────────────────────────────
// Found 2026-06-21 overnight: an unrecognized flag (a typo'd CLI-mode name) fell straight
// through to the long-running host below instead of erroring -- on a box already running
// this exact binary under systemd, that means a SECOND full daemon instance silently starts
// and both processes log success binding the same NodeServer/TaskQueue ports. No actual
// argument here is ever valid for normal mode (the systemd unit always invokes this binary
// bare, see theorc-warband.service's ExecStart), so any arg array at all means a typo or a
// CLI mode that doesn't exist yet -- refuse to start rather than silently double-running.
if (args.Length > 0)
{
    Console.Error.WriteLine($"Unknown argument(s): {string.Join(' ', args)}");
    Console.Error.WriteLine(
        "Recognized modes: --show-identity, --pair --target <host> --expect-fingerprint \"<phrase>\", " +
        "--leave-hive --yes");
    Console.Error.WriteLine("Normal (long-running daemon) mode takes no arguments.");
    return 1;
}

// ── Normal mode — long-running HIVE node host ───────────────────────────────

// Drop process priority for the headless worker so Windows does not starve process
// creation of equal-priority admin shells (sshd → powershell/cmd) while inference is
// CPU-bound. Round 1 (2026-07-28) tried BelowNormal and measured a clean win — but only
// against SYNTHETIC CPU burners. Round 2 (2026-07-29) disproved it under REAL ggml
// inference: ggml's thread pool sets one compute-dispatch thread to Win32
// THREAD_PRIORITY_HIGHEST, which is a +2 offset RELATIVE to the process's own priority
// class, not absolute. At BelowNormal (base 6) that thread claws back up to base priority
// 8 — the exact same default priority a freshly-spawned admin shell gets — making SSH
// exec landing a coin flip under real load, confirmed via direct PID-before/after checks
// (not telemetry silence, which produced a false "kill landed" reading during that round).
// Round 3 (2026-07-29, same day) confirmed Idle instead: base 4 + the same +2 offset lands
// the elevated thread at priority 6, comfortably below a fresh shell's 8. Verified 3/3 kills
// landed via direct PID lookup under real sustained ggml compute load. GPU-bound tok/s is
// not expected to move meaningfully; priority only affects the CPU scheduler's choice when
// something else wants a core. See docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md (HV-6 /
// hv4-disconnect, 2026-07-29 entries) for the full three-round trail.
try
{
    System.Diagnostics.Process.GetCurrentProcess().PriorityClass =
        System.Diagnostics.ProcessPriorityClass.Idle;
}
catch (Exception ex)
{
    // Non-fatal: some hosts deny priority changes; continue at default priority.
    Console.Error.WriteLine($"[theorc-warband] could not set Idle priority: {ex.Message}");
}

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<DaemonConfig>(ctx.Configuration.GetSection("Hive"));
        services.AddHostedService<HiveService>();
    })
    .Build();

await host.RunAsync();
return 0;
