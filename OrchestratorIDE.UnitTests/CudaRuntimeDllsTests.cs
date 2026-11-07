// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorSetup.Services;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// The installer copies the CUDA runtime DLLs next to llama-server.exe: llama.cpp's CUDA zip
/// doesn't contain them, and without them llama-server runs on the CPU.
/// </summary>
[TestFixture]
public sealed class CudaRuntimeDllsTests
{
    private string _dir = "";
    private string From => Path.Combine(_dir, "CudaRedist");
    private string To => Path.Combine(_dir, "llama");

    [SetUp]
    public void SetUp()
    {
        _dir = Path.Combine(Path.GetTempPath(), "orc-cudart-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(From);
        Directory.CreateDirectory(To);
    }

    [TearDown]
    public void TearDown()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Test]
    public void Copies_the_three_CUDA_12_runtime_DLLs()
    {
        foreach (var n in CudaRuntimeDlls.Names) File.WriteAllText(Path.Combine(From, n), n);
        Assert.That(CudaRuntimeDlls.CopyInto(From, To), Is.EqualTo(3));
        Assert.That(CudaRuntimeDlls.Names, Is.EquivalentTo(new[] { "cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll" }));
        foreach (var n in CudaRuntimeDlls.Names)
            Assert.That(File.ReadAllText(Path.Combine(To, n)), Is.EqualTo(n));
    }

    [Test]
    public void Keeps_DLLs_already_there_and_skips_ones_the_source_lacks()
    {
        File.WriteAllText(Path.Combine(From, "cudart64_12.dll"), "new");
        File.WriteAllText(Path.Combine(From, "cublas64_12.dll"), "new");
        File.WriteAllText(Path.Combine(To, "cudart64_12.dll"), "existing");

        Assert.That(CudaRuntimeDlls.CopyInto(From, To), Is.EqualTo(1));
        Assert.That(File.ReadAllText(Path.Combine(To, "cudart64_12.dll")), Is.EqualTo("existing"));
        Assert.That(File.Exists(Path.Combine(To, "cublasLt64_12.dll")), Is.False);
    }
}
