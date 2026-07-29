// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// RuntimeOrchestrator constructs SessionManager and AdapterManager itself from a single
/// LLamaSharpRuntime (see its class doc for why — a prior draft accepted both independently and
/// review caught that nothing then enforced they shared the same runtime instance). Only the
/// "ModelDepot couldn't resolve a base model" failure path is testable without a real GGUF: it
/// returns from SessionManager.LoadRoleAsync before AdapterManager or any native LLamaSharp
/// object is ever touched. The success path (real conversation on a real adapter-attached
/// executor) is covered by the §7 spike harness and manual verification, same precedent as
/// AdapterManagerTests and LLamaSharpRuntime itself.
/// </summary>
[TestFixture]
public sealed class RuntimeOrchestratorTests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles held briefly by test hosts.
            }
        }
        _tempRoots.Clear();
    }

    [Test]
    public void Constructor_Throws_When_Runtime_Is_Null() =>
        Assert.Throws<ArgumentNullException>(() => new RuntimeOrchestrator(null!));

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_Depot_Is_Null()
    {
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await orchestrator.GetConversationForRoleAsync(null!, RuntimeRole.Boss));
    }

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_No_Base_Model_Resolved()
    {
        var root = NewTempRoot();
        WriteFile(root, "adapters", "worker-lora.gguf"); // adapter present, but no base model

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await orchestrator.GetConversationForRoleAsync(
                ModelDepot.Scan(root), RuntimeRole.Worker));

        Assert.That(ex!.Message, Does.Contain("No base GGUF resolved"));
    }

    [Test]
    public async Task EnsureAdmitted_TracksReservationsAcrossRoles_DeniesSecondRoleWhenBudgetExhausted()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this native-load-dependent reservation test.");

        // Reproduces the gap a static budget snapshot leaves open: the provider below always
        // reports ReservedBytes=0 (exactly like MainWindow.TryBuildNativeHiveBudget today), and
        // TotalBytes is sized to fit exactly ONE base model. Without RuntimeOrchestrator's own
        // active-reservation accounting, a second role would be admitted against the same
        // always-zero ReservedBytes and silently over-commit VRAM.
        var sizeBytes = new FileInfo(ggufPath!).Length;
        var asset = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: ggufPath!,
            DisplayName: "base",
            SizeBytes: sizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            SuggestedRoles: [RuntimeRole.Boss, RuntimeRole.Worker]);

        var bossBinding = new RuntimeRoleBinding(RuntimeRole.Boss, asset, null);
        var workerBinding = new RuntimeRoleBinding(RuntimeRole.Worker, asset, null);
        var budget = new VramBudget(TotalBytes: sizeBytes, ReservedBytes: 0);

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: () => budget);

        using var bossConversation = await orchestrator
            .GetConversationForBindingAsync(bossBinding)
            .ConfigureAwait(false);

        var ex = Assert.ThrowsAsync<RuntimeAdmissionDeniedException>(
            async () => await orchestrator.GetConversationForBindingAsync(workerBinding));

        Assert.That(ex!.Budget.ReservedBytes, Is.EqualTo(sizeBytes));
    }

    [Test]
    public async Task EnsureAdmitted_ReadmitsSameRole_WithoutDoubleCountingResidentModel()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this native-load-dependent reservation test.");

        // Regression for the HV-1 6 GB-box denial (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md
        // HV-1, HardcorePC RTX 3050, 2026-07-21). A live nvidia-smi budget already counts a role's
        // resident model in ReservedBytes; EnsureAdmitted then charged a full fresh-load estimate
        // for the SAME model on top, double-counting it and denying every sequential job after the
        // first on a card too tight to hold two phantom copies. The stateful provider below stands
        // in for the live probe: 0 used before any load, then the resident model afterward — the
        // exact shape that double-counted. No second load happens (same role+binding reuses the
        // resident executor), so re-admission must credit back this role's own already-counted
        // footprint and succeed.
        var sizeBytes = new FileInfo(ggufPath!).Length;
        var asset = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: ggufPath!,
            DisplayName: "base",
            SizeBytes: sizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            SuggestedRoles: [RuntimeRole.Worker]);
        var workerBinding = new RuntimeRoleBinding(RuntimeRole.Worker, asset, null);

        // Total fits ONE model with headroom, not two. First admission sees an idle GPU; every
        // one after it sees the model resident — leaving less free than the model's own size, so a
        // full-fresh-load charge would (wrongly) deny.
        var admissionCalls = 0;
        var totalBytes = (long)(sizeBytes * 1.5);
        VramBudget Provider()
        {
            var reserved = admissionCalls == 0 ? 0L : sizeBytes;
            admissionCalls++;
            return new VramBudget(totalBytes, reserved);
        }

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: Provider);

        // First job: admitted against an idle GPU, loads and reserves the Worker model.
        using (await orchestrator.GetConversationForBindingAsync(workerBinding).ConfigureAwait(false))
        {
        }

        // Second job, same role+binding: the provider now reports the model resident. Before the
        // fix this threw RuntimeAdmissionDeniedException; the reuse loads nothing, so it must admit.
        using var second = await orchestrator
            .GetConversationForBindingAsync(workerBinding)
            .ConfigureAwait(false);

        Assert.That(second, Is.Not.Null);
        Assert.That(admissionCalls, Is.EqualTo(2), "both admissions should have consulted the live budget");
    }

    [Test]
    public async Task EnsureAdmitted_ReadmissionAfterOwnLoad_DoesNotShrinkThisRolesLedgerEntry()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this native-load-dependent reservation test.");

        // Found by Grok's review of this PR, not by any HV run -- the shrink needs exactly the two
        // calls below, in this order, from the SAME role, which no fleet campaign happened to
        // exercise. GetConversationForBindingAsync commits `_reservedByRole[role]` from
        // EnsureAdmitted's returned requiredBytes, which is the FULL model footprint on this
        // role's first (fresh) load, then the much smaller INCREMENTAL cost (KV cache/compute
        // buffer/adapter only) once WouldReuseLoadedBaseWeights sees its own base already
        // resident. Before the fix, the second call's smaller number overwrote the ledger entry
        // outright -- even though nothing was freed on the GPU, the role's own resident base is
        // still fully there. In the static-budget fallback (ReservedBytes: 0, no live probe) that
        // ledger is the ONLY signal a later role's admission check has, so an artificially
        // shrunk entry under-counts real VRAM usage and can over-admit into an actual OOM.
        var sizeBytes = new FileInfo(ggufPath!).Length;
        var asset = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: ggufPath!,
            DisplayName: "base",
            SizeBytes: sizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            SuggestedRoles: [RuntimeRole.Worker]);
        var workerBinding = new RuntimeRoleBinding(RuntimeRole.Worker, asset, null);

        // The static fallback shape itself: ReservedBytes always 0, exactly like
        // MainWindow.TryBuildNativeHiveBudget when no live probe is configured -- the scenario in
        // which the ledger is the only signal EnsureAdmitted has, so a shrunk entry here is not
        // merely cosmetic telemetry, it changes the actual admission decision for a later role.
        var totalBytes = sizeBytes * 4;
        var budget = new VramBudget(totalBytes, ReservedBytes: 0);

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: () => budget);

        // Options are REQUIRED for the reuse discount to apply at all -- it exists only on the
        // context-aware estimate path (see EstimateRequiredBytes: options is null => legacy
        // file-size-only, unconditionally, with no reuse discount ever applied). Without this,
        // both calls below return the identical "legacy" number regardless of residency, the
        // floor is never exercised, and this test cannot actually catch the shrink it names.
        var options = new RuntimeOptions(ContextLength: 2048, GpuLayers: -1);

        using (await orchestrator.GetConversationForBindingAsync(workerBinding, options).ConfigureAwait(false))
        {
        }
        var afterFirstLoad = orchestrator.GetReservationSnapshot()!.Reservations
            .Single(r => r.Role == RuntimeRole.Worker).Bytes;
        Assert.That(afterFirstLoad, Is.GreaterThan(0), "first (fresh) load must reserve something");

        // Same role, same binding, base weights now resident from the call above -- this is the
        // reuse admission whose returned requiredBytes is smaller than the first call's.
        using (await orchestrator.GetConversationForBindingAsync(workerBinding, options).ConfigureAwait(false))
        {
        }
        var afterReuse = orchestrator.GetReservationSnapshot()!.Reservations
            .Single(r => r.Role == RuntimeRole.Worker).Bytes;

        Assert.That(afterReuse, Is.GreaterThanOrEqualTo(afterFirstLoad),
            "the role's ledger entry must never shrink within the same generation -- the resident " +
            "base model this role itself loaded has not gone anywhere just because THIS call reused it");
    }

    [Test]
    public async Task EnsureAdmitted_AdmitsSecondRole_SharingResidentBaseWeights_AgainstLiveProbe()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this native-load-dependent reservation test.");

        // Regression for the HV-3 concurrent-role denial (HardcorePC RTX 3050 6 GB, 2026-07-25):
        // "Budget total=6.0 GB, reserved=10.3 GB, available=0.0 GB" -- reserved ABOVE the card's
        // total. Two compounding errors, both fixed:
        //   1. EnsureAdmitted summed the live probe (which already counts every resident model)
        //      with the ledger for those same other roles, charging them twice.
        //   2. Both roles resolve to the SAME GGUF and SessionManager keeps ONE shared base load,
        //      but the second role was still charged a full fresh-load estimate -- billing an
        //      entire extra model for something that only costs its own context.
        // Together they made a concurrent second role permanently unadmittable on a card sized
        // for one model, even though it genuinely fits.
        var sizeBytes = new FileInfo(ggufPath!).Length;
        var asset = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: ggufPath!,
            DisplayName: "base",
            SizeBytes: sizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            SuggestedRoles: [RuntimeRole.Worker, RuntimeRole.Researcher]);

        // SAME base asset for both roles -- the real fleet shape (one coder GGUF serving every
        // lane), and the only shape where reuse applies.
        var workerBinding = new RuntimeRoleBinding(RuntimeRole.Worker, asset, null);
        var researcherBinding = new RuntimeRoleBinding(RuntimeRole.Researcher, asset, null);

        // Sized so ONE model plus a second context fits, but two full copies never could --
        // exactly the 6 GB box. If the second role were still charged a whole model it would be
        // denied here, which is what this test would have caught.
        var loaded = false;
        var totalBytes = (long)(sizeBytes * 1.4);
        VramBudget Provider() => new(totalBytes, loaded ? sizeBytes : 0L);

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: Provider);

        // Options are REQUIRED for the reuse discount to apply at all -- it exists only on the
        // context-aware estimate path, where kvBytes prices the increment. This mirrors the fleet,
        // whose workers all run with an explicit HIVE__NATIVECONTEXTSIZE.
        var options = new RuntimeOptions(ContextLength: 2048, GpuLayers: -1);

        using var first = await orchestrator
            .GetConversationForBindingAsync(workerBinding, options)
            .ConfigureAwait(false);
        loaded = true;

        // Second role, different RuntimeRole, same resident base weights. Held concurrently --
        // the first conversation is deliberately still alive.
        using var second = await orchestrator
            .GetConversationForBindingAsync(researcherBinding, options)
            .ConfigureAwait(false);

        Assert.That(second, Is.Not.Null);

        var snapshot = orchestrator.GetReservationSnapshot();
        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.Reservations, Has.Count.EqualTo(2),
                "both roles should hold a reservation concurrently");
            Assert.That(snapshot.ReservedBytes, Is.LessThanOrEqualTo(snapshot.TotalBytes),
                "reserved must never exceed the card's total");
            // The second role's ledger entry must reflect the incremental cost, not a whole model.
            var researcher = snapshot.Reservations.Single(r => r.Role == RuntimeRole.Researcher);
            Assert.That(researcher.Bytes, Is.LessThan(sizeBytes),
                "a role reusing resident base weights must not be charged a full model");
        });
    }

    [Test]
    public async Task GetReservationSnapshot_DoesNotDoubleCountResidentModel_AgainstLiveProbe()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run this native-load-dependent reservation test.");

        // Regression for the HV-3 telemetry defect (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md
        // HV-3, HardcorePC RTX 3050 6 GB, 2026-07-25). The sibling of the HV-1 bug above, in the
        // REPORTING path: GetReservationSnapshot summed the live probe's ReservedBytes (which
        // already counts the resident model) with the ledger entry for that same model, so the
        // first real HV-3 run published reservedBytes = 11.04 GB on a card whose totalBytes is
        // 6.44 GB, with availableBytes stuck at 0. Admission was unaffected -- this was purely a
        // telemetry surface publishing a number the hardware cannot produce.
        var sizeBytes = new FileInfo(ggufPath!).Length;
        var asset = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: ggufPath!,
            DisplayName: "base",
            SizeBytes: sizeBytes,
            LastModifiedUtc: DateTimeOffset.UtcNow,
            SuggestedRoles: [RuntimeRole.Worker]);
        var workerBinding = new RuntimeRoleBinding(RuntimeRole.Worker, asset, null);

        // Same stateful stand-in for the live probe as the HV-1 test: idle before any load, then
        // reporting the resident model afterward.
        var loaded = false;
        var totalBytes = (long)(sizeBytes * 1.5);
        VramBudget Provider() => new(totalBytes, loaded ? sizeBytes : 0L);

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: Provider);

        using (await orchestrator.GetConversationForBindingAsync(workerBinding).ConfigureAwait(false))
        {
        }
        loaded = true;

        var snapshot = orchestrator.GetReservationSnapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            // The core invariant. Before the fix this was sizeBytes * 2 -- above totalBytes.
            Assert.That(snapshot!.ReservedBytes, Is.LessThanOrEqualTo(snapshot.TotalBytes),
                "reserved must never exceed the card's total -- that is physically impossible");
            Assert.That(snapshot.AvailableBytes, Is.GreaterThan(0),
                "one resident model on a card sized for 1.5 of them must leave headroom");
            // Still reports the real footprint rather than zeroing it out to satisfy the bound.
            Assert.That(snapshot.ReservedBytes, Is.GreaterThanOrEqualTo(sizeBytes));
        });
    }

    [Test]
    public async Task GetConversationForBindingAsync_Throws_RuntimeAdmissionDenied_When_No_Scheduler_Or_Budget_Configured()
    {
        // Native Runtime v2.0 Phase A (docs/NATIVE_RUNTIME_V2_SPEC.md §1.2 Gap 2): this used to
        // be a silent no-op that let native execution proceed with zero admission control.
        // GetConversationForBindingAsync throws EnsureAdmitted's denial before ever touching
        // SessionManager.LoadBindingAsync, so this is safe to exercise without a real GGUF —
        // the fake binding's path is never actually opened.
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, FakeBaseModel(), Adapter: null);

        var ex = Assert.ThrowsAsync<RuntimeAdmissionDeniedException>(
            async () => await orchestrator.GetConversationForBindingAsync(binding));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Decision.Admitted, Is.False);
            Assert.That(ex.Decision.Reason, Does.Contain("No VRAM scheduler/budget is configured"));
        });
    }

    [Test]
    public async Task GetReservationSnapshot_Reports_RejectedAdmissionCount_After_A_Real_Denial()
    {
        // Native Runtime v2.0 Phase C (docs/NATIVE_RUNTIME_V2_SPEC.md §2.3): the rejected-
        // admission counter is a lifetime tally, distinct from the "no scheduler/budget
        // configured" case above (which GetReservationSnapshot can't even report on, since it
        // returns null in that configuration) -- this exercises a REAL scheduler+budget denial.
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, FakeBaseModel(), Adapter: null);
        var tooSmallBudget = new VramBudget(TotalBytes: 100, ReservedBytes: 0); // FakeBaseModel is 1,000,000 bytes

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: () => tooSmallBudget);

        Assert.ThrowsAsync<RuntimeAdmissionDeniedException>(
            async () => await orchestrator.GetConversationForBindingAsync(binding));

        var snapshot = orchestrator.GetReservationSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot, Is.Not.Null);
            Assert.That(snapshot!.RejectedAdmissionCount, Is.EqualTo(1));
            Assert.That(snapshot.LastRejectionReason, Does.Contain("GB"));
            // Consistency across an induced failure (§2.4): a denial commits nothing -- no
            // phantom reservation for a role whose admission never succeeded.
            Assert.That(snapshot.Reservations, Is.Empty);
            Assert.That(snapshot.ReservedBytes, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task EnsureAdmitted_Does_Not_Throw_When_AllowUnbudgetedExecution_Is_True_And_No_Scheduler_Or_Budget()
    {
        // The deliberate opt-out (e.g. ContextFabricBench) must bypass the fail-closed denial
        // above without needing a real model load — exercised directly against the internal
        // seam so this stays a pure, isolated admission-decision test.
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime, allowUnbudgetedExecution: true);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, FakeBaseModel(), Adapter: null);

        Assert.DoesNotThrow(() => orchestrator.EnsureAdmitted(binding));
    }

    [Test]
    public async Task GetReservationSnapshot_Returns_Null_When_No_Scheduler_Configured()
    {
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);

        Assert.That(orchestrator.GetReservationSnapshot(), Is.Null);
    }

    [Test]
    public async Task GetReservationSnapshot_Reports_Empty_Reservations_Before_Any_Admission()
    {
        var budget = new VramBudget(TotalBytes: 10_000_000_000, ReservedBytes: 1_000_000_000);
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime, scheduler: new OrcScheduler(), budgetProvider: () => budget);

        var snapshot = orchestrator.GetReservationSnapshot();

        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.Reservations, Is.Empty);
            Assert.That(snapshot.TotalBytes, Is.EqualTo(10_000_000_000));
            Assert.That(snapshot.ReservedBytes, Is.EqualTo(1_000_000_000));
            Assert.That(snapshot.AvailableBytes, Is.EqualTo(9_000_000_000));
        });
    }

    [Test]
    public async Task GetReservationSnapshot_Returns_Null_When_Budget_Provider_Throws()
    {
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(
            runtime,
            scheduler: new OrcScheduler(),
            budgetProvider: () => throw new InvalidOperationException("VRAM probe unavailable"));

        Assert.That(orchestrator.GetReservationSnapshot(), Is.Null);
    }

    [Test]
    public async Task GetReservationSnapshot_Throws_After_Dispose()
    {
        var runtime = new LLamaSharpRuntime();
        var orchestrator = new RuntimeOrchestrator(runtime, disposeRuntime: true);
        await orchestrator.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => orchestrator.GetReservationSnapshot());
    }

    private static RuntimeModelAsset FakeBaseModel() => new(
        Id: "fake-base",
        Kind: RuntimeAssetKind.BaseModelGguf,
        Path: "does-not-exist.gguf",
        DisplayName: "does-not-exist.gguf",
        SizeBytes: 1_000_000,
        LastModifiedUtc: DateTimeOffset.UnixEpoch,
        SuggestedRoles: [RuntimeRole.Boss]);

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-runtime-orchestrator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static void WriteFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake model bytes");
    }
}
