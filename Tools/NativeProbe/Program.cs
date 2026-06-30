// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Minimal LLamaSharp native-load probe. Reproduces the EXACT native library selection +
// load that OrchestratorIDE.NativeRuntime performs, but in isolation and with full logging,
// so a "TypeInitializationException on LLama.Native.NativeApi" on a deployed machine can be
// diagnosed without dragging the whole app/HIVE stack along. Build it single-file/self-
// contained the same way the app is deployed, copy it next to the failing exe, run it from a
// console, and read the native log it prints.
//
// Usage:
//   native-probe                       # DryRun selection + native log only (no model needed)
//   native-probe <path-to-model.gguf>  # also attempt a real LLamaWeights load

using LLama;
using LLama.Common;
using LLama.Native;

Console.WriteLine("=== TheOrc LLamaSharp native probe ===");
Console.WriteLine($"OS            : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine($"Arch          : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
Console.WriteLine($"Process path  : {Environment.ProcessPath}");
Console.WriteLine($"Base dir      : {AppContext.BaseDirectory}");
Console.WriteLine($"CUDA_PATH     : {Environment.GetEnvironmentVariable("CUDA_PATH") ?? "<unset>"}");
Console.WriteLine();

// Wire the native log to stdout BEFORE any NativeApi touch -- this is the diagnostic that the
// app currently throws away (it never configures a log callback, so the only signal it gets is
// the opaque outer TypeInitializationException).
NativeLibraryConfig.All
    .WithLogCallback((level, message) =>
        Console.Write($"[llama:{level}] {message}"))
    .WithAutoFallback(true);   // CUDA-load failure should degrade to CPU, not throw

Console.WriteLine("--- DryRun: which native library does LLamaSharp select here? ---");
try
{
    var ok = NativeLibraryConfig.All.DryRun(out var llama, out var mtmd);
    Console.WriteLine($"DryRun success: {ok}");
    Console.WriteLine($"  selected llama: {Describe(llama)}");
    Console.WriteLine($"  selected mtmd : {Describe(mtmd)}");
}
catch (Exception ex)
{
    Console.WriteLine($"DryRun THREW: {ex.GetType().Name}: {ex.Message}");
    if (ex.InnerException is { } inner)
        Console.WriteLine($"  inner: {inner.GetType().Name}: {inner.Message}");
}
Console.WriteLine();

if (args.Length > 0)
{
    var modelPath = args[0];
    Console.WriteLine($"--- Attempting real model load: {modelPath} ---");
    if (!File.Exists(modelPath))
    {
        Console.WriteLine("  model file does not exist; skipping load.");
    }
    else
    {
        try
        {
            var mp = new ModelParams(modelPath) { GpuLayerCount = 0, ContextSize = 512 };
            using var weights = LLamaWeights.LoadFromFile(mp);
            Console.WriteLine($"  LOADED OK. vocab/context probe succeeded.");
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

static string Describe(LLama.Abstractions.INativeLibrary? lib) =>
    lib is null
        ? "<null -- nothing selected>"
        : $"metadata={lib.Metadata}";
