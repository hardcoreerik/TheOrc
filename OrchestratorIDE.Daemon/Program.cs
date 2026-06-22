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
if (args.Contains("--show-identity") || args.Contains("--pair"))
    SecretProtection.Initialize(new AesGcmSecretProtector(MachineKey.Load()));

if (args.Contains("--show-identity"))
{
    var identity = HiveIdentity.Load();
    Console.WriteLine($"NodeId: {identity.NodeId}");
    Console.WriteLine($"Fingerprint: {identity.Fingerprint}");
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

// ── Normal mode — long-running HIVE node host ───────────────────────────────

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((ctx, services) =>
    {
        services.Configure<DaemonConfig>(ctx.Configuration.GetSection("Hive"));
        services.AddHostedService<HiveService>();
    })
    .Build();

await host.RunAsync();
return 0;
