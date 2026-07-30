// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// docs/NATIVE_RUNTIME_HIVE_VALIDATION_PLAN.md HV-1's "minor evidence-quality gap noted, not
/// fixed": HiveService.cs called DetectAsync without threading NativeBackendBootstrap.
/// EnsureConfigured's verdict into verifiedNativeBackend, so every job's Attestation.Backend/
/// WorkerCapabilities.NativeBackend read "cpu" (the parameter's default) even on a confirmed CUDA
/// box, and FreeVramMb silently read 0 for the same reason. Fixed at the HiveService.cs call
/// site (not testable directly -- hosted-service startup, same "no mockable seam" precedent as
/// MainWindow/RuntimeOrchestrator); this locks down the other half DetectAsync itself owns: it
/// must actually honor whatever verifiedNativeBackend it's given.
/// </summary>
[TestFixture]
public sealed class WorkerCapabilityDetectorTests
{
    [TestCase("cuda12", ExpectedResult = "cuda12")]
    [TestCase("metal", ExpectedResult = "metal")]
    [TestCase("cpu", ExpectedResult = "cpu")]
    [TestCase("", ExpectedResult = "cpu")]
    [TestCase("something-unrecognized", ExpectedResult = "cpu")]
    public async Task<string> DetectAsync_NativeBackend_ReflectsVerifiedNativeBackendArgument(
        string verifiedNativeBackend)
    {
        var caps = await WorkerCapabilityDetector.DetectAsync(
            "test-worker", ModelDepot.Scan(""), freeVramMb: 4096,
            verifiedNativeBackend: verifiedNativeBackend);

        return caps.NativeBackend;
    }

    [TestCase("cuda12", 4096)]
    [TestCase("metal", 4096)]
    [TestCase("cpu", 0)]
    [TestCase("", 0)]
    public async Task DetectAsync_FreeVramMb_IsZeroUnlessBackendIsVerifiedGpu(
        string verifiedNativeBackend, long expectedFreeVramMb)
    {
        var caps = await WorkerCapabilityDetector.DetectAsync(
            "test-worker", ModelDepot.Scan(""), freeVramMb: 4096,
            verifiedNativeBackend: verifiedNativeBackend);

        Assert.That(caps.FreeVramMb, Is.EqualTo(expectedFreeVramMb),
            "configured VRAM is not proof a GPU backend actually loaded -- FreeVramMb must stay " +
            "0 for any unverified/CPU backend, matching this method's own comment on that field");
    }

    [Test]
    public async Task DetectAsync_DefaultsToCpu_WhenVerifiedNativeBackendIsOmitted()
    {
        // The exact default the HiveService.cs bug silently relied on for every job on real
        // CUDA hardware, before the call site was fixed to pass the real verdict.
        var caps = await WorkerCapabilityDetector.DetectAsync(
            "test-worker", ModelDepot.Scan(""), freeVramMb: 4096);

        Assert.Multiple(() =>
        {
            Assert.That(caps.NativeBackend, Is.EqualTo("cpu"));
            Assert.That(caps.FreeVramMb, Is.EqualTo(0));
        });
    }
}
