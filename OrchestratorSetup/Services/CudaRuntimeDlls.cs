// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorSetup.Services;

/// <summary>
/// The CUDA 12 runtime DLLs that both CUDA consumers need: TheOrc's in-process LLamaSharp
/// backend (installed to <c>%LOCALAPPDATA%\TheOrc\CudaRedist</c> by
/// <see cref="CudaRedistributableInstaller"/>) and llama.cpp's <c>llama-server.exe</c>, whose
/// <c>llama-bNNNNN-bin-win-cuda-*.zip</c> doesn't include them (llama.cpp ships them separately
/// as <c>cudart-llama-bin-win-cuda-*.zip</c>). Without them next to llama-server, llama.cpp
/// can't load its CUDA backend and runs on the CPU.
/// </summary>
public static class CudaRuntimeDlls
{
    public static readonly IReadOnlyList<string> Names = ["cudart64_12.dll", "cublas64_12.dll", "cublasLt64_12.dll"];

    /// <summary>
    /// Copies each DLL from <paramref name="fromDir"/> into <paramref name="toDir"/> unless it
    /// is already there. Returns how many were copied; DLLs missing from the source are skipped.
    /// </summary>
    public static int CopyInto(string fromDir, string toDir)
    {
        var copied = 0;
        foreach (var name in Names)
        {
            var src = Path.Combine(fromDir, name);
            var dst = Path.Combine(toDir, name);
            if (!File.Exists(src) || File.Exists(dst))
                continue;
            File.Copy(src, dst);
            copied++;
        }
        return copied;
    }
}
