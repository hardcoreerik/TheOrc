// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// When a campaign work unit cannot run anywhere, does anything say WHY?
///
/// Before this, nothing did. Campaign units are exempt from <c>PendingTimeoutSec</c>, so a unit no
/// worker can satisfy sits <c>pending</c> indefinitely with no error, no timeout and nothing
/// recorded — HV-5's diagnosability drill could only demonstrate the problem by inducing it and
/// then waiting out a poll deadline to report the silence
/// (docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-5, 2026-07-27). §6 requires diagnosability
/// across machines, and silence is the opposite of it.
///
/// These tests pin the explanation itself rather than the plumbing that surfaces it: that
/// <see cref="CampaignCapabilityMatcher.ExplainIneligibility"/> never disagrees with
/// <see cref="CampaignCapabilityMatcher.IsEligible"/> about WHETHER there is a reason, and that the
/// reason it gives names the specific requirement that failed. The two functions are separate on
/// purpose — IsEligible is on the dispatch hot path — so "they agree" is exactly the property that
/// could silently rot.
/// </summary>
[TestFixture]
public sealed class CampaignIneligibilityExplanationTests
{
    private static WorkerCapabilities CapableWorker() => new()
    {
        WorkerId          = "HardcorePC",
        CpuCores          = 16,
        AvailableMemoryMb = 32_000,
        FreeVramMb        = 6_000,
        Os                = "Windows 11 Pro",
        Architecture      = "x64",
        ExecutionKinds    = [HiveExecutionKinds.NativeAgent],
        NativeModelHashes = ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"],
    };

    private static HiveTaskBundle Unit(ResourceRequirements requirements) => new()
    {
        TaskId        = "campaign-1-unit-1",
        CampaignId    = "campaign-1",
        WorkUnitId    = "unit-1",
        Title         = "HV-5 induced native failure",
        Role          = "Coder",
        ExecutionKind = HiveExecutionKinds.NativeAgent,
        Requirements  = requirements,
    };

    [Test]
    public void ASatisfiableUnit_HasNoExplanation()
    {
        // The negative control. If this ever returns text, every pending unit in the fleet would
        // start reporting an unsatisfiable reason and the diagnostic would become noise.
        var unit = Unit(new ResourceRequirements());
        var worker = CapableWorker();

        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.True);
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, worker), Is.Null);
    }

    [Test]
    public void AnImpossibleModelHash_IsExplainedByNamingTheHash()
    {
        // The exact inducement HV-5's drill uses, and the case that was previously silent.
        var missing = new string('0', 64);
        var unit = Unit(new ResourceRequirements { NativeModelHash = missing });
        var worker = CapableWorker();

        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.False);

        var reason = CampaignCapabilityMatcher.ExplainIneligibility(unit, worker);
        Assert.That(reason, Is.Not.Null);
        Assert.That(reason, Does.Contain("native model hash"));
        // Truncated in the message, so assert on the prefix a reader would actually match against
        // the requirement rather than on the full 64 chars.
        Assert.That(reason, Does.Contain(missing[..12]));
    }

    [TestCaseSource(nameof(EveryRejectionPath))]
    public void EveryRejection_HasAnExplanation(string label, ResourceRequirements requirements)
    {
        // The agreement property. ExplainIneligibility mirrors IsEligible by hand, so a new
        // requirement added to one and not the other would leave a unit rejected for a reason
        // nothing can state -- back to the silence this whole change exists to remove.
        var unit = Unit(requirements);
        var worker = CapableWorker();

        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.False,
            $"[{label}] expected this requirement to make the worker ineligible");
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, worker), Is.Not.Null.And.Not.Empty,
            $"[{label}] rejected with no explanation — IsEligible and ExplainIneligibility disagree");
    }

    private static IEnumerable<object[]> EveryRejectionPath()
    {
        yield return ["cpu", new ResourceRequirements { MinCpuCores = 999 }];
        yield return ["memory", new ResourceRequirements { MinMemoryMb = 999_999 }];
        yield return ["vram", new ResourceRequirements { MinVramMb = 999_999 }];
        yield return ["os", new ResourceRequirements { Os = "Plan9" }];
        yield return ["arch", new ResourceRequirements { Architecture = "sparc" }];
        yield return ["modelHash", new ResourceRequirements { NativeModelHash = new string('0', 64) }];
        yield return ["adapterHash", new ResourceRequirements { NativeAdapterHash = new string('1', 64) }];
        yield return ["containerEngine", new ResourceRequirements { ContainerEngine = "podman" }];
        yield return ["excluded", new ResourceRequirements { ExcludedWorkerIds = ["HardcorePC"] }];
        yield return ["packs", new ResourceRequirements { RequiredPacks = ["theorc.nonexistent"] }];
    }

    [Test]
    public void AnUnsupportedExecutionKind_IsExplained()
    {
        // Not a ResourceRequirements field, and checked before them, so it needs its own case.
        var unit = new HiveTaskBundle
        {
            TaskId        = "campaign-1-unit-2",
            CampaignId    = "campaign-1",
            WorkUnitId    = "unit-2",
            Title         = "container unit on a native-only worker",
            Role          = "Coder",
            ExecutionKind = "container-pack",
            Requirements  = new ResourceRequirements(),
        };
        var worker = CapableWorker();

        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.False);
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, worker),
            Does.Contain("executionKind"));
    }
}
