// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// OrcScheduler.TryAdmit is pure data logic (RuntimeRoleBinding sizes vs. a VramBudget) with no
/// native dependency, unlike AdapterManager/RuntimeOrchestrator — fully unit-testable with
/// synthetic fixtures, no GGUF file or GPU required.
/// </summary>
[TestFixture]
public sealed class OrcSchedulerTests
{
    [Test]
    public void TryAdmit_Throws_When_Binding_Is_Null()
    {
        var scheduler = new OrcScheduler();

        Assert.Throws<ArgumentNullException>(
            () => scheduler.TryAdmit(null!, new VramBudget(TotalBytes: 1, ReservedBytes: 0)));
    }

    [Test]
    public void TryAdmit_Throws_When_Budget_Is_Null()
    {
        var scheduler = new OrcScheduler();
        var binding = Binding(RuntimeRole.Boss, baseSizeBytes: 1, adapterSizeBytes: null);

        Assert.Throws<ArgumentNullException>(() => scheduler.TryAdmit(binding, null!));
    }

    [Test]
    public void TryAdmit_Admits_When_Required_Fits_Available_Budget()
    {
        var scheduler = new OrcScheduler();
        var binding = Binding(RuntimeRole.Boss, baseSizeBytes: GB(4), adapterSizeBytes: GB(0.1));
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: 0);

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Admitted, Is.True);
            Assert.That(decision.Reason, Is.Null);
        });
    }

    [Test]
    public void TryAdmit_Denies_When_Required_Exceeds_Available_Budget()
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Worker, BaseModelAsset(RuntimeRole.Worker, GB(12)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: GB(10)); // 6 GB available

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Admitted, Is.False);
            Assert.That(decision.Reason, Does.Contain("12.0 GB"));
            Assert.That(decision.Reason, Does.Contain("6.0 GB"));
        });
    }

    [Test]
    public void TryAdmit_Treats_Exact_Fit_As_Admitted()
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Boss, BaseModelAsset(RuntimeRole.Boss, GB(4)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(4), ReservedBytes: 0); // exactly 4 GB available

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.True);
    }

    [TestCase(RuntimeRole.Boss, SchedulingLane.Interactive)]
    [TestCase(RuntimeRole.Reviewer, SchedulingLane.Interactive)]
    [TestCase(RuntimeRole.Worker, SchedulingLane.Background)]
    [TestCase(RuntimeRole.Researcher, SchedulingLane.Background)]
    public void TryAdmit_Assigns_Lane_By_Role(RuntimeRole role, SchedulingLane expectedLane)
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(role, BaseModelAsset(role, GB(1)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: 0);

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Lane, Is.EqualTo(expectedLane));
    }

    [Test]
    public void TryAdmit_Uses_Fallback_Estimate_For_Adapter_With_Unknown_Size()
    {
        // PeftAdapterDirectory assets always have SizeBytes: null (ModelDepot never sizes
        // directories). A budget that fits the base model alone but not base + the fallback
        // estimate should be denied -- confirming the unknown adapter size isn't silently
        // treated as zero cost.
        var scheduler = new OrcScheduler();
        var baseOnly = GB(4);
        var baseModel = BaseModelAsset(RuntimeRole.Boss, baseOnly);
        var unsizedAdapter = AdapterAsset(RuntimeRole.Boss, sizeBytes: null);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, unsizedAdapter);
        var budget = new VramBudget(TotalBytes: baseOnly, ReservedBytes: 0); // fits base exactly, no room for adapter

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.False);
    }

    [Test]
    public void TryAdmit_Ignores_Adapter_Size_Entirely_When_No_Adapter_Bound()
    {
        var scheduler = new OrcScheduler();
        var baseOnly = GB(4);
        var baseModel = BaseModelAsset(RuntimeRole.Boss, baseOnly);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, Adapter: null);
        var budget = new VramBudget(TotalBytes: baseOnly, ReservedBytes: 0); // fits base exactly

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.True);
    }

    private static long GB(double gb) => (long)(gb * 1024 * 1024 * 1024);

    private static RuntimeRoleBinding Binding(RuntimeRole role, long baseSizeBytes, long? adapterSizeBytes)
    {
        var baseModel = BaseModelAsset(role, baseSizeBytes);
        var adapter = AdapterAsset(role, adapterSizeBytes);
        return new RuntimeRoleBinding(role, baseModel, adapter);
    }

    private static RuntimeModelAsset BaseModelAsset(RuntimeRole role, long sizeBytes) => new(
        Id: "base",
        Kind: RuntimeAssetKind.BaseModelGguf,
        Path: "base.gguf",
        DisplayName: "base.gguf",
        SizeBytes: sizeBytes,
        LastModifiedUtc: DateTimeOffset.UnixEpoch,
        SuggestedRoles: [role]);

    private static RuntimeModelAsset AdapterAsset(RuntimeRole role, long? sizeBytes) => new(
        Id: "adapter",
        Kind: RuntimeAssetKind.LoraGguf,
        Path: "adapter.gguf",
        DisplayName: "adapter.gguf",
        SizeBytes: sizeBytes,
        LastModifiedUtc: DateTimeOffset.UnixEpoch,
        SuggestedRoles: [role]);
}
