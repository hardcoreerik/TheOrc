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
//   native-probe <model> <gpuLayers> --bench [seqMax] [ctx] [maxTokens]
//                                                 # measure real decode throughput (tok/s) and TTFT
//
// The --bench mode exists for the Native Runtime v2 §6 throughput gate: "is native fast enough to
// be the default?" is a measurement, not an opinion, and it has to be answerable on a fleet box
// without dragging the HIVE stack along. It deliberately takes seqMax as a parameter because
// ModelParams.SeqMax is set to AdapterManager.SequenceHardLimit on the persistent role executor
// (LLamaSharpRuntime.cs) but left at the native default of 1 on the stateless executor -- so the
// two production paths run llama.cpp with materially different context params, and comparing them
// back-to-back on one box is the only honest way to attribute a throughput difference to it.

using System.Diagnostics;
using LLama;
using LLama.Common;
using LLama.Sampling;
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

var benchIndex = Array.IndexOf(args, "--bench");

if (benchIndex >= 0)
{
    await RunBenchAsync(args, benchIndex);
}
else if (args.Length > 0)
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

// Measures decode throughput the way a user actually experiences it: time to first token, then
// tokens/second over the remaining decode. Reports both, because they have different causes -- a
// bad TTFT is prompt-processing/offload, a bad tok/s is decode bandwidth or an oversized cache.
async Task RunBenchAsync(string[] argv, int flagIndex)
{
    var modelPath = argv[0];
    var gpuLayers = argv.Length > 1 && int.TryParse(argv[1], out var g) ? g : -1;

    int ArgAt(int offset, int fallback) =>
        argv.Length > flagIndex + offset && int.TryParse(argv[flagIndex + offset], out var v) ? v : fallback;

    var seqMax    = ArgAt(1, 1);
    var ctxSize   = ArgAt(2, 4096);
    var maxTokens = ArgAt(3, 128);

    Console.WriteLine("--- Throughput bench ---");
    Console.WriteLine($"  model     : {modelPath}");
    Console.WriteLine($"  gpuLayers : {gpuLayers}");
    Console.WriteLine($"  seqMax    : {seqMax}");
    Console.WriteLine($"  ctxSize   : {ctxSize}");
    Console.WriteLine($"  maxTokens : {maxTokens}");
    Console.WriteLine();

    if (!File.Exists(modelPath))
    {
        Console.WriteLine("  model file does not exist; skipping bench.");
        return;
    }

    var mp = new ModelParams(modelPath)
    {
        ContextSize   = (uint)ctxSize,
        GpuLayerCount = gpuLayers,
        SeqMax        = (uint)seqMax,
    };

    var loadWatch = Stopwatch.StartNew();
    using var weights = LLamaWeights.LoadFromFile(mp);
    loadWatch.Stop();
    Console.WriteLine($"  model load: {loadWatch.Elapsed.TotalSeconds:F2}s");

    // StatelessExecutor builds its own context from these exact params, so SeqMax/ContextSize
    // reach llama.cpp unchanged -- which is the whole point of parameterizing them here.
    var executor = new StatelessExecutor(weights, mp);
    var inference = new InferenceParams
    {
        MaxTokens         = maxTokens,
        AntiPrompts       = [],
        SamplingPipeline  = new DefaultSamplingPipeline { Temperature = 0f },
    };

    const string prompt =
        "Write a short technical explanation of how a hash table resolves collisions.";

    var watch  = Stopwatch.StartNew();
    TimeSpan? ttft = null;
    var tokens = 0;

    await foreach (var _ in executor.InferAsync(prompt, inference))
    {
        ttft ??= watch.Elapsed;
        tokens++;
    }
    watch.Stop();

    // Decode rate excludes prompt processing: charging TTFT against the token count would blend
    // two different bottlenecks into one number and make the result useless for diagnosis.
    var decodeSeconds = (watch.Elapsed - (ttft ?? TimeSpan.Zero)).TotalSeconds;
    var decodeRate    = decodeSeconds > 0 && tokens > 1 ? (tokens - 1) / decodeSeconds : 0;

    Console.WriteLine();
    Console.WriteLine($"  RESULT tokens        : {tokens}");
    Console.WriteLine($"  RESULT ttft          : {ttft?.TotalMilliseconds ?? 0:F0} ms");
    Console.WriteLine($"  RESULT wall          : {watch.Elapsed.TotalSeconds:F2} s");
    Console.WriteLine($"  RESULT decode tok/s  : {decodeRate:F2}");
}
