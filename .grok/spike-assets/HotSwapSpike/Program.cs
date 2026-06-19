// Native Runtime Phase 3 spike — RUNTIME_PHASE0_SPEC.md §7.
// Throwaway harness, not shipped code. Measures:
//   1. Does SetLoraAdapters work on a context that already has KV cache populated
//      WITHOUT clearing memory first — does it throw, or silently apply wrong
//      results because old cache entries were computed without the adapter?
//   2. What is the actual cost of LoadLoraFromFile + SetLoraAdapters themselves
//      (expected: near-zero, just pointer/struct assignment)?
//   3. What is the cost of the "correct" path — MemoryClear + full re-prefill —
//      i.e. the real "context rebuild" cost the spec asks about?
//
// Base model lineage caveat: using a locally-available Llama-3.2-3B-Instruct
// *fork* (uncensored/Dolphin), not the exact meta-llama/Llama-3.2-3B-Instruct
// the downloaded LoRA was trained against. Architecture/tensor shapes match
// (same base family), so loading and the mechanism test are valid; the
// *quality* of the adapter's effect on output may be muted by base weight
// drift between forks. This spike is about mechanism and cost, not quality.
//
// Paths are overridable via args[0] (base GGUF) / args[1] (LoRA GGUF) or the
// HOTSWAP_BASE_GGUF / HOTSWAP_LORA_GGUF env vars, so this harness is portable
// across machines, not just the one it was first run on — falls back to the
// original local paths used for the §7 finding if neither is supplied.
// Results are appended to results.log next to this file for an auditable,
// diffable record instead of console-only narrative.

using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Batched;
using LLama.Common;
using LLama.Native;

var basePath = args.Length > 0 ? args[0]
    : Environment.GetEnvironmentVariable("HOTSWAP_BASE_GGUF")
      ?? @"F:\Ai\GarfChat\checkpoints\android-test-models\Llama-3.2-3B-Instruct-uncensored.Q4_K_M.gguf";
var loraPath = args.Length > 1 ? args[1]
    : Environment.GetEnvironmentVariable("HOTSWAP_LORA_GGUF")
      ?? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Llama-3.2-3B-appreciation-f16.gguf");

if (!File.Exists(basePath)) { Console.WriteLine($"MISSING base model: {basePath} (pass as arg[0] or set HOTSWAP_BASE_GGUF)"); return 1; }
if (!File.Exists(loraPath)) { Console.WriteLine($"MISSING LoRA: {loraPath} (pass as arg[1] or set HOTSWAP_LORA_GGUF)"); return 1; }

var resultsPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "results.log");
var results = new StringBuilder();
void Log(string line) { Console.WriteLine(line); results.AppendLine(line); }

var mp = new ModelParams(basePath) { ContextSize = 2048, GpuLayerCount = 33 };

Log("=== Loading base weights ===");
var sw = Stopwatch.StartNew();
using var weights = await LLamaWeights.LoadFromFileAsync(mp);
Log($"Base weights loaded in {sw.ElapsedMilliseconds} ms");

using var executor = new BatchedExecutor(weights, mp);
using var conv = executor.Create();

const string prompt = "<|start_header_id|>user<|end_header_id|>\n\nWrite one sentence praising a student's essay.<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n";

// ── Step 1: baseline generation, no adapter ──────────────────────────────
Log("\n=== Step 1: baseline (no adapter) ===");
sw.Restart();
conv.Prompt(prompt, addBos: true, special: true);
await executor.Infer();
var baselineOutput = await SampleN(conv, executor, weights, n: 24);
var baselineMs = sw.ElapsedMilliseconds;
Log($"Baseline prefill+gen: {baselineMs} ms");
Log($"Output: {baselineOutput}");

// ── Step 2: load LoRA, attach to the SAME live context, NO memory clear ─
Log("\n=== Step 2: load + attach LoRA mid-sequence, no MemoryClear ===");
sw.Restart();
var lora = weights.NativeHandle.LoadLoraFromFile(loraPath);
var loadMs = sw.ElapsedMilliseconds;

// Real signature is Span<(LoraAdapter Adapter, float Scale)> — confirms scale is
// runtime-adjustable per attach call, not baked in at LoadLoraFromFile time.
sw.Restart();
executor.Context.NativeHandle.SetLoraAdapters(new (LoraAdapter Adapter, float Scale)[] { (lora, 1.0f) });
var attachMs = sw.ElapsedMilliseconds;
Log($"LoadLoraFromFile: {loadMs} ms | SetLoraAdapters: {attachMs} ms (expect both near-zero — no recompute happens here)");

bool crashedOnDirtySwap = false;
string dirtyOutput = "(not run)";
try
{
    // Continue the SAME conversation/sequence — KV cache up to this point was
    // computed WITHOUT the adapter. This is the exact unsafe case the spec flags.
    conv.Prompt(" Another one:", addBos: false, special: false);
    await executor.Infer();
    dirtyOutput = await SampleN(conv, executor, weights, n: 24);
}
catch (Exception ex)
{
    crashedOnDirtySwap = true;
    dirtyOutput = $"THREW: {ex.GetType().Name}: {ex.Message}";
}
Log("Continued generation on dirty (un-cleared) cache after adapter swap:");
Log($"  Crashed: {crashedOnDirtySwap}");
Log($"  Output: {dirtyOutput}");

// ── Step 3: the "correct" path — clear memory, full re-prefill under adapter ─
Log("\n=== Step 3: MemoryClear + full re-prefill under adapter (the real rebuild cost) ===");
sw.Restart();
executor.Context.NativeHandle.MemoryClear(data: true);
var clearMs = sw.ElapsedMilliseconds;

using var conv2 = executor.Create();
sw.Restart();
conv2.Prompt(prompt, addBos: true, special: true);
await executor.Infer();
var adapterOutput = await SampleN(conv2, executor, weights, n: 24);
var rebuildMs = sw.ElapsedMilliseconds;
Log($"MemoryClear: {clearMs} ms | Full re-prefill+gen under adapter: {rebuildMs} ms | baseline was: {baselineMs} ms");
Log($"Output under adapter (clean): {adapterOutput}");

// ── Step 4: detach adapter, confirm base behavior returns ───────────────
Log("\n=== Step 4: detach adapter, re-prefill clean ===");
executor.Context.NativeHandle.SetLoraAdapters(Array.Empty<(LoraAdapter Adapter, float Scale)>());
executor.Context.NativeHandle.MemoryClear(data: true);
using var conv3 = executor.Create();
sw.Restart();
conv3.Prompt(prompt, addBos: true, special: true);
await executor.Infer();
var detachedOutput = await SampleN(conv3, executor, weights, n: 24);
var detachMs = sw.ElapsedMilliseconds;
Log($"Detach + re-prefill: {detachMs} ms");
Log($"Output after detach (should resemble baseline): {detachedOutput}");

var detachMatchesBaseline = detachedOutput == baselineOutput;
Log($"\nDetach output byte-for-byte matches baseline: {detachMatchesBaseline}");

Log("\n=== DONE ===");
File.WriteAllText(resultsPath, results.ToString());
Console.WriteLine($"\nFull results also written to {Path.GetFullPath(resultsPath)}");
return 0;

static async Task<string> SampleN(Conversation conv, BatchedExecutor executor, LLamaWeights weights, int n)
{
    var sb = new StringBuilder();
    for (var i = 0; i < n; i++)
    {
        var logits = conv.Sample(0);
        // Greedy argmax — deterministic, no sampling-pipeline complexity needed for this spike.
        LLamaToken bestToken = 0;
        var bestVal = float.MinValue;
        for (var t = 0; t < logits.Length; t++)
        {
            if (logits[t] > bestVal) { bestVal = logits[t]; bestToken = t; }
        }
        sb.Append(weights.Vocab.LLamaTokenToString(bestToken, isSpecialToken: false));
        conv.Prompt(bestToken);
        await executor.Infer();
    }
    return sb.ToString();
}
