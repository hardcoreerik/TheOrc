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
