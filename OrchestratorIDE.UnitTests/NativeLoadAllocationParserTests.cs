// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// Test strings below are verbatim from the 2026-07-19 spike's captured output (a real
/// Llama-3.2-3B-Q4_K_M load on the reference box), not invented — see the Phase B addendum in
/// docs/NATIVE_RUNTIME_V2_SPEC.md.
/// </summary>
[TestFixture]
public sealed class NativeLoadAllocationParserTests
{
    [TestCase("load_tensors:        CUDA0 model buffer size =  1918.36 MiB", "model", 1918.36)]
    [TestCase("llama_kv_cache:      CUDA0 KV buffer size =   224.00 MiB", "KV", 224.00)]
    [TestCase("sched_reserve:      CUDA0 compute buffer size =   284.90 MiB", "compute", 284.90)]
    [TestCase("llama_kv_cache:      CUDA1 KV buffer size =   896.00 MiB", "KV", 896.00)]
    public void TryParseVramLine_Matches_Real_Cuda_Device_Lines(string line, string category, double mib)
    {
        var parsed = NativeLoadAllocationParser.TryParseVramLine(line);

        Assert.That(parsed, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(parsed!.Value.Category, Is.EqualTo(category));
            Assert.That(parsed.Value.Bytes, Is.EqualTo((long)(mib * 1024 * 1024)));
        });
    }

    [TestCase("load_tensors:   CPU_Mapped model buffer size =   308.24 MiB", "CPU_Mapped is host RAM, not VRAM")]
    [TestCase("sched_reserve:  CUDA_Host compute buffer size =    16.26 MiB", "CUDA_Host is pinned staging RAM, not VRAM")]
    [TestCase("some unrelated log line with no buffer size in it", "no match at all")]
    [TestCase("~llama_context:      CUDA0 compute buffer size is 266.6056 MiB, matches expectation", "\"is\" not \"=\", real line shape from a later confirmation log — must not match")]
    public void TryParseVramLine_Returns_Null_For_NonVram_Or_NonMatching_Lines(string line, string why)
    {
        Assert.That(NativeLoadAllocationParser.TryParseVramLine(line), Is.Null, why);
    }

    [Test]
    public void Accumulator_Sums_Multiple_Categories_Across_Devices()
    {
        var acc = new NativeLoadAllocationAccumulator();

        // A full real load sequence: weights (with a CPU_Mapped remainder), then a role's
        // context creation producing KV + compute lines (two devices for compute, as observed).
        acc.Observe("load_tensors:   CPU_Mapped model buffer size =   308.24 MiB"); // excluded
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        acc.Observe("llama_kv_cache:      CUDA0 KV buffer size =   224.00 MiB");
        acc.Observe("sched_reserve:      CUDA0 compute buffer size =   284.90 MiB");
        acc.Observe("sched_reserve:  CUDA_Host compute buffer size =    16.26 MiB"); // excluded

        var expectedMib = 1918.36 + 224.00 + 284.90;
        Assert.That(acc.TotalBytes, Is.EqualTo((long)(expectedMib * 1024 * 1024)));
    }

    [Test]
    public void Accumulator_TotalBytes_Is_Null_When_Nothing_Observed() =>
        Assert.That(new NativeLoadAllocationAccumulator().TotalBytes, Is.Null);

    [Test]
    public void Accumulator_Reset_Clears_Prior_Observations()
    {
        var acc = new NativeLoadAllocationAccumulator();
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        Assert.That(acc.TotalBytes, Is.Not.Null);

        acc.Reset();

        Assert.That(acc.TotalBytes, Is.Null, "a reload must not let a previous model's VRAM leak into the new one's total");
    }

    [Test]
    public void Accumulator_Second_Load_Does_Not_Double_Count_First()
    {
        // Mirrors LoadModelAsync's real sequence: Reset() then re-observe for a NEW model.
        var acc = new NativeLoadAllocationAccumulator();
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        acc.Reset();
        acc.Observe("load_tensors:        CUDA0 model buffer size =   500.00 MiB");

        Assert.That(acc.TotalBytes, Is.EqualTo(500L * 1024 * 1024));
    }

    [Test]
    public void Observe_During_Suppress_Scope_Does_Not_Accumulate()
    {
        // Mirrors StreamCompletionAsync's ephemeral StatelessExecutor lifetime: the persistent
        // load's totals must survive untouched by a stateless call's transient KV/compute lines.
        var acc = new NativeLoadAllocationAccumulator();
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");

        using (acc.Suppress())
        {
            acc.Observe("llama_kv_cache:      CUDA0 KV buffer size =   224.00 MiB");
            acc.Observe("sched_reserve:      CUDA0 compute buffer size =   284.90 MiB");
        }

        Assert.That(acc.TotalBytes, Is.EqualTo((long)(1918.36 * 1024 * 1024)),
            "lines observed while suppressed must not be counted, even after the scope ends");
    }

    [Test]
    public void Observe_Resumes_After_Suppress_Scope_Disposed()
    {
        var acc = new NativeLoadAllocationAccumulator();

        using (acc.Suppress())
        {
            acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        }

        acc.Observe("llama_kv_cache:      CUDA0 KV buffer size =   224.00 MiB");

        Assert.That(acc.TotalBytes, Is.EqualTo(224L * 1024 * 1024),
            "accumulation must resume once the suppress scope is disposed");
    }

    [Test]
    public void Overlapping_Suppress_Scopes_Only_Resume_After_All_Dispose()
    {
        // Reference-counted, not boolean: two concurrent stateless calls must not let the first
        // to finish re-enable accumulation while the second is still in flight.
        var acc = new NativeLoadAllocationAccumulator();

        var outer = acc.Suppress();
        var inner = acc.Suppress();

        inner.Dispose();
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        Assert.That(acc.TotalBytes, Is.Null, "still suppressed while the outer scope is active");

        outer.Dispose();
        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        Assert.That(acc.TotalBytes, Is.EqualTo((long)(1918.36 * 1024 * 1024)),
            "accumulation resumes only once every active scope has disposed");
    }

    [Test]
    public void Suppress_Dispose_Is_Idempotent()
    {
        // A lone scope's double-Dispose can't distinguish idempotent (depth 1->0, second call a
        // no-op) from broken (depth 1->0->-1) -- Observe()'s guard is `> 0`, and both 0 and -1
        // fail that check the same way, so accumulation resumes either way and the assertion
        // would pass even with the idempotency guard removed entirely (CodeRabbit finding on
        // PR #77's fix commit). An overlapping second scope that stays active makes the two
        // cases diverge: idempotent keeps depth at 1 (still suppressed); broken drops it to 0
        // (accumulation wrongly resumes).
        var acc = new NativeLoadAllocationAccumulator();
        var scope1 = acc.Suppress();
        using var scope2 = acc.Suppress();

        scope1.Dispose();
        scope1.Dispose();

        acc.Observe("load_tensors:        CUDA0 model buffer size =  1918.36 MiB");
        Assert.That(acc.TotalBytes, Is.Null,
            "a double Dispose must not double-decrement the suppress depth below the still-active second scope");
    }
}
