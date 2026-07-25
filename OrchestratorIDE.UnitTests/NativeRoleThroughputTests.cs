// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Native Runtime v2.0 §6 throughput gate: "is native fast enough to be the default?"
///
/// This lane exists because the §6 flip has a standing blocker sourced from campaign evidence
/// reporting ~7 tok/s for native role execution, against an expected 30-50 tok/s for a 7B on the
/// fleet's consumer GPUs. `Tools/NativeProbe --bench` already measured the same box/model/context
/// at 33.3 tok/s decode through LLamaSharp's StatelessExecutor, so whatever costs the missing 5x
/// is NOT llama.cpp, the CUDA backend, GPU-layer offload, or ModelParams.SeqMax (all four ruled
/// out by direct measurement). The two remaining candidates both live here:
///
///   1. The role path drives BatchedExecutor with a per-token Prompt(token)/InferUntilReadyAsync
///      loop (NativeRoleRuntime.StreamRoleCompletionCoreAsync) instead of StatelessExecutor. That
///      loop could carry real managed per-token overhead.
///   2. The reported number itself. NativeRoleRuntime computes TokensPerSecond as
///      ContextManager.EstimateTokens(output) / (total wall elapsed) -- a numerator that is a
///      chars/4 approximation and a denominator that includes prompt processing.
///
/// Distinguishing (1) from (2) requires measuring BOTH in one run, which is exactly what this
/// test does: an externally-timed decode-only rate (the honest number) alongside the runtime's
/// own self-reported rate (the number the campaign evidence carries). Asserting on either alone
/// would leave the ambiguity intact.
///
/// Gated on THEORC_TEST_GGUF like every other real-model lane in this workstream; it measures
/// and reports rather than enforcing a tok/s floor, because the pass threshold for the flip is a
/// product decision, not a unit-test constant.
/// </summary>
[TestFixture]
public sealed class NativeRoleThroughputTests
{
    // Long enough that decode dominates a one-off TTFT, short enough to keep the lane quick on
    // the fleet's slowest box. At the measured StatelessExecutor rate this is a few seconds.
    private const int MaxTokens = 128;

    [Test]
    public async Task RolePath_DecodeThroughput_IsMeasured_AgainstSelfReportedRate()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the native role throughput lane.");

        // Fail loudly on a bad env var. Without this, a typo'd path still yields a directory
        // that ModelDepot happily scans, and the run dies later as an opaque role-resolution
        // failure that reads like a runtime defect rather than a bad argument.
        if (!File.Exists(ggufPath))
            Assert.Fail($"THEORC_TEST_GGUF does not exist: {ggufPath}");

        var root = Path.GetDirectoryName(Path.GetFullPath(ggufPath!));
        if (string.IsNullOrWhiteSpace(root))
            Assert.Fail("THEORC_TEST_GGUF must point to a GGUF file.");

        var depot = ModelDepot.Scan(root!);

        // THEORC_TEST_GGUF names a FILE, but the depot scans that file's whole directory and
        // resolves the role itself (picking the smallest GGUF present). Reporting the env-var
        // path as "the model measured" would be a lie whenever the directory holds more than
        // one -- so report what the depot actually bound, and say so when they differ.
        var resolvedBinding = depot.ResolveRole(RuntimeRole.Worker);
        var measuredModel = resolvedBinding?.BaseModel.DisplayName ?? "<unresolved>";

        // Context 4096 deliberately matches the fleet workers' HIVE__NATIVECONTEXTSIZE and the
        // Tools/NativeProbe --bench run this is being compared against. A different context size
        // would change KV-cache sizing and make the comparison meaningless.
        await using var runtime = new NativeRoleRuntime(
            depot,
            new RuntimeOptions(ContextLength: 4096, GpuLayers: -1),
            allowUnbudgetedExecution: true);

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        // COLD CALL. NativeRoleRuntime loads the model lazily inside the first
        // StreamRoleCompletionAsync, and its `started` stamp is taken before that load -- so the
        // first call's self-reported tok/s divides the output by a wall time that is almost
        // entirely model load. Measured explicitly here rather than warmed away silently,
        // because this cold number is what the Settings "native test" button surfaces (it uses
        // exactly NativeRuntimeTestPrompt, whose two-line answer makes the numerator ~7 tokens)
        // and therefore what the §6 blocker's "1.7 tok/s on NewcorePC's native-test" figure is.
        var coldPrompt = NativeRuntimeTestPrompt.BuildMessages(NativeRuntimeTestPrompt.PromptText);
        var coldWatch = Stopwatch.StartNew();
        var coldChars = 0;
        await foreach (var token in runtime.StreamRoleCompletionAsync(
                           RuntimeRole.Worker, coldPrompt, maxTokens: MaxTokens, ct: cts.Token))
        {
            coldChars += token.Length;
        }
        coldWatch.Stop();
        var coldSelfReported = runtime.GetStats(RuntimeRole.Worker).TokensPerSecond ?? 0;

        // WARM CALL -- steady state, model already resident. This is the number that answers
        // "is native fast enough to be the default", and the one comparable to
        // Tools/NativeProbe --bench on the same box/model/context.
        var messages = NativeRuntimeTestPrompt.BuildMessages(
            "Write a detailed technical explanation of how a hash table resolves collisions. " +
            "Cover separate chaining and open addressing, and compare their trade-offs.");

        var sb = new StringBuilder();
        var watch = Stopwatch.StartNew();
        TimeSpan? ttft = null;
        var chunks = 0;
        // Chars produced strictly AFTER the first chunk. The first chunk arrives at the end of
        // the TTFT window, so charging it against the decode window (which excludes TTFT) would
        // divide a numerator and denominator that cover different spans and overstate the rate.
        var decodeWindowChars = 0;

        await foreach (var token in runtime.StreamRoleCompletionAsync(
                           RuntimeRole.Worker, messages, maxTokens: MaxTokens, ct: cts.Token))
        {
            if (ttft is null)
                ttft = watch.Elapsed;
            else
                decodeWindowChars += token.Length;

            sb.Append(token);
            chunks++;
        }
        watch.Stop();

        var output = sb.ToString();
        Assert.That(output, Is.Not.Empty, "role path produced no output -- nothing to measure.");

        // Decode-only: subtract TTFT so prompt processing is not charged against the token count.
        // This is the same accounting Tools/NativeProbe --bench uses, which is the whole point --
        // the two numbers have to be computed identically to be comparable.
        var decodeSeconds = (watch.Elapsed - (ttft ?? TimeSpan.Zero)).TotalSeconds;

        // Yielded chunks are the honest observable count here. They are not guaranteed to be
        // 1:1 with model tokens, so this is reported as a chunk rate and cross-checked against
        // the estimator below rather than presented as a definitive tok/s.
        var chunkRate = decodeSeconds > 0 && chunks > 1 ? (chunks - 1) / decodeSeconds : 0;

        var estimatedTokens = OrchestratorIDE.Core.ContextManager.EstimateTokens(output);

        // Estimated over the decode window only, to match decodeSeconds. Deliberately NOT
        // ContextManager.EstimateTokens: its Math.Max(1, ...) floor would report a phantom token
        // for an empty decode window, turning "nothing was decoded" into a nonzero rate.
        var decodeWindowTokens = decodeWindowChars / 4.0;
        var estimatedDecodeRate = decodeSeconds > 0 ? decodeWindowTokens / decodeSeconds : 0;

        var stats = runtime.GetStats(RuntimeRole.Worker);
        var selfReported = stats.TokensPerSecond ?? 0;

        TestContext.WriteLine("=== Native role-path throughput ===");
        TestContext.WriteLine($"  THEORC_TEST_GGUF          : {ggufPath}");
        TestContext.WriteLine($"  model actually measured   : {measuredModel}");
        TestContext.WriteLine("  -- cold call (model load inside the measured call) --");
        TestContext.WriteLine($"  cold output chars         : {coldChars}");
        TestContext.WriteLine($"  cold wall                 : {coldWatch.Elapsed.TotalSeconds:F2} s");
        TestContext.WriteLine($"  cold SELF-REPORTED tok/s  : {coldSelfReported:F2}");
        TestContext.WriteLine("  -- warm call (steady state) --");
        TestContext.WriteLine($"  yielded chunks            : {chunks}");
        TestContext.WriteLine($"  output chars              : {output.Length}");
        TestContext.WriteLine($"  estimated tokens (chars/4): {estimatedTokens}");
        TestContext.WriteLine($"  ttft                      : {ttft?.TotalMilliseconds ?? 0:F0} ms");
        TestContext.WriteLine($"  total wall                : {watch.Elapsed.TotalSeconds:F2} s");
        TestContext.WriteLine($"  decode window             : {decodeSeconds:F2} s");
        TestContext.WriteLine($"  DECODE chunk rate         : {chunkRate:F2} /s");
        TestContext.WriteLine($"  DECODE est. token rate    : {estimatedDecodeRate:F2} tok/s");
        TestContext.WriteLine($"  SELF-REPORTED tok/s       : {selfReported:F2}");
        TestContext.WriteLine($"  self-reported / decode    : {(estimatedDecodeRate > 0 ? selfReported / estimatedDecodeRate : 0):P0}");

        Assert.Multiple(() =>
        {
            Assert.That(chunks, Is.GreaterThan(1), "need more than one chunk to measure a rate.");
            Assert.That(decodeSeconds, Is.GreaterThan(0), "decode window must be positive.");
            Assert.That(stats.TokensPerSecond, Is.Not.Null,
                "role path must self-report a throughput number for campaign evidence to carry.");
        });
    }
}
