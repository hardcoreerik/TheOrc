// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// CF-6 first slice: WorkUnit.DependsOn / HiveTaskQueue's lease-side barrier check. Reaches
/// into HiveTaskQueue's private _tasks dictionary and AreDependenciesSatisfied via reflection
/// since the lease/claim HTTP handlers aren't otherwise unit-testable without a real listener
/// (same constraint CampaignEngineTests works around by testing SubmitCampaign/GetCampaignStatus
/// at the non-HTTP level).
/// </summary>
[TestFixture]
public sealed class CampaignDependencyBarrierTests
{
    [Test]
    public void SubmitCampaign_PropagatesDependsOn_OntoBundle()
    {
        using var queue = new HiveTaskQueue();
        var campaign = new CampaignDefinition
        {
            Name = "cf6-barrier-test",
            WorkUnits =
            [
                new WorkUnit { WorkUnitId = "a", Title = "Read seg 0" },
                new WorkUnit { WorkUnitId = "b", Title = "Reduce", DependsOn = ["a"] },
            ],
        };

        queue.SubmitCampaign(campaign);

        var bundle = GetBundle(queue, $"{campaign.CampaignId}-b");
        Assert.That(bundle.DependsOnWorkUnitIds, Is.EqualTo(new[] { "a" }));
    }

    [Test]
    public void AreDependenciesSatisfied_BlocksUntilDependencyCompletes()
    {
        using var queue = new HiveTaskQueue();
        var campaign = new CampaignDefinition
        {
            Name = "cf6-barrier-test",
            WorkUnits =
            [
                new WorkUnit { WorkUnitId = "a", Title = "Read seg 0" },
                new WorkUnit { WorkUnitId = "b", Title = "Reduce", DependsOn = ["a"] },
            ],
        };
        queue.SubmitCampaign(campaign);

        var entryA = GetEntry(queue, $"{campaign.CampaignId}-a");
        var entryB = GetEntry(queue, $"{campaign.CampaignId}-b");
        var method = typeof(HiveTaskQueue).GetMethod("AreDependenciesSatisfied", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("AreDependenciesSatisfied not found via reflection.");

        Assert.That((bool)method.Invoke(queue, [entryB])!, Is.False, "must wait while 'a' is still pending");

        entryA.GetType().GetProperty("Status")!.SetValue(entryA, "completed");

        Assert.That((bool)method.Invoke(queue, [entryB])!, Is.True, "must unblock once 'a' is completed");
    }

    [Test]
    public void AreDependenciesSatisfied_TrueByDefault_WhenNoDependencies()
    {
        using var queue = new HiveTaskQueue();
        var campaign = new CampaignDefinition
        {
            Name = "cf6-barrier-test",
            WorkUnits = [new WorkUnit { WorkUnitId = "a", Title = "Read seg 0" }],
        };
        queue.SubmitCampaign(campaign);

        var entryA = GetEntry(queue, $"{campaign.CampaignId}-a");
        var method = typeof(HiveTaskQueue).GetMethod("AreDependenciesSatisfied", BindingFlags.NonPublic | BindingFlags.Instance)!;

        Assert.That((bool)method.Invoke(queue, [entryA])!, Is.True);
    }

    [Test]
    public void SubmitCampaign_ThrowsOnUnknownDependencyId()
    {
        using var queue = new HiveTaskQueue();
        var campaign = new CampaignDefinition
        {
            Name = "cf6-bad-dep",
            WorkUnits =
            [
                new WorkUnit { WorkUnitId = "a", Title = "Read" },
                new WorkUnit { WorkUnitId = "b", Title = "Reduce", DependsOn = ["does-not-exist"] },
            ],
        };

        Assert.Throws<ArgumentException>(() => queue.SubmitCampaign(campaign),
            "SubmitCampaign must reject a DependsOn ID that isn't in the same campaign.");
    }

    [Test]
    public void AreDependenciesSatisfied_CascadesToFailed_WhenDependencyFails()
    {
        using var queue = new HiveTaskQueue();
        var campaign = new CampaignDefinition
        {
            Name = "cf6-cascade-test",
            WorkUnits =
            [
                new WorkUnit { WorkUnitId = "a", Title = "Read seg 0" },
                new WorkUnit { WorkUnitId = "b", Title = "Reduce", DependsOn = ["a"] },
            ],
        };
        queue.SubmitCampaign(campaign);

        var entryA = GetEntry(queue, $"{campaign.CampaignId}-a");
        var entryB = GetEntry(queue, $"{campaign.CampaignId}-b");
        var method = typeof(HiveTaskQueue).GetMethod("AreDependenciesSatisfied",
            BindingFlags.NonPublic | BindingFlags.Instance)!;
        var statusProp = entryB.GetType().GetProperty("Status")!;

        // Mark dependency as failed.
        entryA.GetType().GetProperty("Status")!.SetValue(entryA, "failed");

        // First call cascades 'b' to failed and returns false.
        Assert.That((bool)method.Invoke(queue, [entryB])!, Is.False);
        Assert.That(statusProp.GetValue(entryB), Is.EqualTo("failed"),
            "AreDependenciesSatisfied must cascade dependent to 'failed' when dependency is terminal.");
    }

    private static object GetEntry(HiveTaskQueue queue, string taskId)
    {
        var tasksField = typeof(HiveTaskQueue).GetField("_tasks", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_tasks field not found via reflection.");
        var tasks = (IDictionary)tasksField.GetValue(queue)!;
        return tasks[taskId] ?? throw new InvalidOperationException($"No queued task for '{taskId}'.");
    }

    private static HiveTaskBundle GetBundle(HiveTaskQueue queue, string taskId)
    {
        var entry = GetEntry(queue, taskId);
        return (HiveTaskBundle)entry.GetType().GetProperty("Bundle")!.GetValue(entry)!;
    }
}
