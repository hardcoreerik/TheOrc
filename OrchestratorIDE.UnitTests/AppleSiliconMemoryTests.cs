// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.InteropServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Apple Silicon (Metal, unified memory) in HIVE and admission: workers advertise "metal" with
/// Metal's live free memory instead of "cpu"/0, the matcher stops counting RAM and VRAM twice on
/// unified-memory workers, and the Metal budget is capped by what the whole Mac has free.
/// </summary>
[TestFixture]
public sealed class AppleSiliconMemoryTests
{
    private static bool IsAppleSilicon =>
        OperatingSystem.IsMacOS() && RuntimeInformation.OSArchitecture == Architecture.Arm64;

    private static NativeBackendReport Report(bool dryRunOk, bool cuda) =>
        new(CudaCapableGpu: cuda, DryRunSucceeded: dryRunOk, SelectedCuda: cuda,
            SelectedLlama: "llama", SelectedMtmd: "mtmd", Log: []);

    // ── Backend label ─────────────────────────────────────────────────────────────────────

    [Test]
    public void HiveBackend_is_cuda12_whenever_cuda_was_selected() =>
        Assert.That(Report(dryRunOk: true, cuda: true).HiveBackend, Is.EqualTo("cuda12"));

    [Test]
    public void HiveBackend_is_metal_only_on_apple_silicon_after_a_successful_dry_run()
    {
        // The same report means Metal on an M-series Mac and CPU everywhere else, so the
        // Windows/Linux label is unchanged.
        Assert.That(Report(dryRunOk: true, cuda: false).HiveBackend,
            Is.EqualTo(IsAppleSilicon ? "metal" : "cpu"));
        Assert.That(Report(dryRunOk: false, cuda: false).HiveBackend, Is.EqualTo("cpu"));
    }

    [Test]
    public async Task DetectAsync_marks_metal_workers_as_unified_memory_only()
    {
        foreach (var (backend, unified) in new[] { ("metal", true), ("cuda12", false), ("cpu", false) })
        {
            var caps = await WorkerCapabilityDetector.DetectAsync(
                "test-worker", ModelDepot.Scan(""), freeVramMb: 4096,
                verifiedNativeBackend: backend);
            Assert.That(caps.IsUnifiedMemory, Is.EqualTo(unified), backend);
        }
    }

    // ── Matcher ───────────────────────────────────────────────────────────────────────────

    private static WorkerCapabilities Mac(bool unified) => new()
    {
        WorkerId          = "M1-Max",
        CpuCores          = 10,
        AvailableMemoryMb = 32_768,
        FreeVramMb        = 24_000,
        NativeBackend     = unified ? "metal" : "cuda12",
        IsUnifiedMemory   = unified,
        ExecutionKinds    = [HiveExecutionKinds.NativeAgent],
    };

    private static HiveTaskBundle Unit(long ramMb, long vramMb) => new()
    {
        TaskId        = "c-1-u-1",
        CampaignId    = "c-1",
        WorkUnitId    = "u-1",
        ExecutionKind = HiveExecutionKinds.NativeAgent,
        Requirements  = new ResourceRequirements { MinMemoryMb = ramMb, MinVramMb = vramMb },
    };

    [Test]
    public void Unified_worker_rejects_a_unit_whose_ram_and_vram_only_fit_separately()
    {
        var unit = Unit(ramMb: 16_000, vramMb: 20_000);   // 36 GB total on a 32 GB Mac

        var worker = Mac(unified: true);
        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.False);
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, worker),
            Does.Contain("unified memory"));

        // Control: the same figures on a separate-pool worker still pass, as before.
        var discrete = Mac(unified: false);
        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, discrete), Is.True);
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, discrete), Is.Null);
    }

    [Test]
    public void Unified_worker_accepts_a_unit_that_fits_in_the_shared_pool()
    {
        var unit = Unit(ramMb: 8_000, vramMb: 20_000);
        var worker = Mac(unified: true);

        Assert.That(CampaignCapabilityMatcher.IsEligible(unit, worker), Is.True);
        Assert.That(CampaignCapabilityMatcher.ExplainIneligibility(unit, worker), Is.Null);
    }

    // ── Metal budget capped by host memory ───────────────────────────────────────────────

    private const long Gb = 1024L * 1024 * 1024;

    [Test]
    public void CombineMetalWithHost_uses_metal_free_space_when_the_host_has_more()
    {
        var b = NativeVramProbe.CombineMetalWithHost(25 * Gb, 1 * Gb, hostAvailableBytes: 30 * Gb);
        Assert.That(b.TotalBytes, Is.EqualTo(25 * Gb));
        Assert.That(b.ReservedBytes, Is.EqualTo(1 * Gb));
    }

    [Test]
    public void CombineMetalWithHost_caps_free_space_at_host_available_memory()
    {
        // Ollama or a browser holding memory this process's Metal counter can't see.
        var b = NativeVramProbe.CombineMetalWithHost(25 * Gb, 1 * Gb, hostAvailableBytes: 10 * Gb);
        Assert.That(b.TotalBytes - b.ReservedBytes, Is.EqualTo(10 * Gb));
    }

    [Test]
    public void CombineMetalWithHost_without_a_host_reading_is_metal_only_and_never_negative()
    {
        Assert.That(NativeVramProbe.CombineMetalWithHost(25 * Gb, 1 * Gb, null).ReservedBytes,
            Is.EqualTo(1 * Gb));
        var full = NativeVramProbe.CombineMetalWithHost(25 * Gb, 1 * Gb, hostAvailableBytes: -5);
        Assert.That(full.ReservedBytes, Is.EqualTo(25 * Gb));
    }

    [Test]
    public void Live_probes_report_real_numbers_on_apple_silicon_and_nothing_elsewhere()
    {
        if (!IsAppleSilicon)
        {
            Assert.That(NativeVramProbe.TryQueryLiveMetalBudget(), Is.Null);
            Assert.That(NativeVramProbe.IsUnifiedMemoryHost, Is.False);
            return;
        }

        var host = NativeVramProbe.TryQueryHostAvailableBytes();
        Assert.That(host, Is.Not.Null.And.GreaterThan(0));

        var budget = NativeVramProbe.TryQueryLiveMetalBudget();
        Assert.That(budget, Is.Not.Null);
        Assert.That(budget!.TotalBytes, Is.GreaterThan(0));
        Assert.That(budget.ReservedBytes, Is.InRange(0, budget.TotalBytes));
        // (No free <= host comparison here: the two live reads race against the OS. The cap
        // itself is covered by the CombineMetalWithHost tests.)
        Assert.That(NativeVramProbe.TryQueryLiveGpuBudget(), Is.Not.Null);
    }
}
