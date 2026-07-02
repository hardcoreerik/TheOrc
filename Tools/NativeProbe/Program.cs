// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Minimal LLamaSharp native-load probe. Exercises the EXACT native library selection the
// app/daemon perform — both now route through OrchestratorIDE.Core.Runtime.NativeBackendBootstrap
// (source-linked here) — but in isolation and with full logging, so backend mis-selection or a
// "TypeInitializationException on LLama.Native.NativeApi" on a deployed machine can be diagnosed
// without dragging the whole app/HIVE stack along. Build it single-file/self-contained the same
// way the app is deployed, copy it next to the failing exe, run it from a console.
//
// Usage:
//   native-probe                                  # backend selection + native log only
//   native-probe <path-to-model.gguf>             # also load the model, GpuLayerCount=-1 (all on GPU)
//   native-probe <path-to-model.gguf> <gpuLayers> # explicit GPU layer count (0 = CPU)

using LLama;
using LLama.Common;
using OrchestratorIDE.Core.Runtime;

Console.WriteLine("=== TheOrc LLamaSharp native probe ===");
Console.WriteLine($"OS            : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Arch          : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Process path  : {Environment.ProcessPath}");
Console.WriteLine($"Base dir      : {AppContext.BaseDirectory}");
Console.WriteLine($"CUDA_PATH     : {Environment.GetEnvironmentVariable("CUDA_PATH") ?? "<unset>"}");
Console.WriteLine();

Console.WriteLine("--- NativeBackendBootstrap (same call the app/daemon make) ---");
var report = NativeBackendBootstrap.EnsureConfigured(line => Console.WriteLine(line));
Console.WriteLine();
Console.WriteLine("--- report.Log (pre-flight + selection, what the app surfaces on fallback) ---");
foreach (var line in report.Log)
    Console.WriteLine($"  {line}");
Console.WriteLine();
Console.WriteLine($"CUDA-capable GPU : {report.CudaCapableGpu}");
Console.WriteLine($"DryRun success   : {report.DryRunSucceeded}");
Console.WriteLine($"Selected llama   : {report.SelectedLlama}");
Console.WriteLine($"Selected mtmd    : {report.SelectedMtmd}");
Console.WriteLine($"VERDICT          : {report.Verdict}");
Console.WriteLine();

if (args.Length > 0)
{
    var modelPath = args[0];
    var gpuLayers = args.Length > 1 && int.TryParse(args[1], out var g) ? g : -1;
    Console.WriteLine($"--- Attempting real model load: {modelPath} (GpuLayerCount={gpuLayers}) ---");
    if (!File.Exists(modelPath))
    {
        Console.WriteLine("  model file does not exist; skipping load.");
    }
    else
    {
        try
        {
            var mp = new ModelParams(modelPath) { GpuLayerCount = gpuLayers, ContextSize = 512 };
            using var weights = LLamaWeights.LoadFromFile(mp);
            Console.WriteLine("  LOADED OK. Check nvidia-smi NOW (process is holding the model) ...");
            Console.WriteLine("  Holding model for 15s so VRAM residency can be observed.");
            Thread.Sleep(TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  LOAD THREW: {ex.GetType().Name}: {ex.Message}");
            for (var inner = ex.InnerException; inner is not null; inner = inner.InnerException)
                Console.WriteLine($"    inner: {inner.GetType().Name}: {inner.Message}");
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== probe complete ===");
return 0;
