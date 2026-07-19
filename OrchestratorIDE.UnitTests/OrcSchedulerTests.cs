// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// OrcScheduler.TryAdmit is pure data logic (RuntimeRoleBinding sizes vs. a VramBudget) with no
/// native dependency, unlike AdapterManager/RuntimeOrchestrator — fully unit-testable with
/// synthetic fixtures, no GGUF file or GPU required.
/// </summary>
[TestFixture]
public sealed class OrcSchedulerTests
{
    [Test]
    public void TryAdmit_Throws_When_Binding_Is_Null()
    {
        var scheduler = new OrcScheduler();

        Assert.Throws<ArgumentNullException>(
            () => scheduler.TryAdmit(null!, new VramBudget(TotalBytes: 1, ReservedBytes: 0)));
    }

    [Test]
    public void TryAdmit_Throws_When_Budget_Is_Null()
    {
        var scheduler = new OrcScheduler();
        var binding = Binding(RuntimeRole.Boss, baseSizeBytes: 1, adapterSizeBytes: null);

        Assert.Throws<ArgumentNullException>(() => scheduler.TryAdmit(binding, null!));
    }

    [Test]
    public void TryAdmit_Admits_When_Required_Fits_Available_Budget()
    {
        var scheduler = new OrcScheduler();
        var binding = Binding(RuntimeRole.Boss, baseSizeBytes: GB(4), adapterSizeBytes: GB(0.1));
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: 0);

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Admitted, Is.True);
            Assert.That(decision.Reason, Is.Null);
        });
    }

    [Test]
    public void TryAdmit_Denies_When_Required_Exceeds_Available_Budget()
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Worker, BaseModelAsset(RuntimeRole.Worker, GB(12)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: GB(10)); // 6 GB available

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Admitted, Is.False);
            Assert.That(decision.Reason, Does.Contain("12.0 GB"));
            Assert.That(decision.Reason, Does.Contain("6.0 GB"));
        });
    }

    [Test]
    public void TryAdmit_Treats_Exact_Fit_As_Admitted()
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Boss, BaseModelAsset(RuntimeRole.Boss, GB(4)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(4), ReservedBytes: 0); // exactly 4 GB available

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.True);
    }

    [TestCase(RuntimeRole.Boss, SchedulingLane.Interactive)]
    [TestCase(RuntimeRole.Reviewer, SchedulingLane.Interactive)]
    [TestCase(RuntimeRole.Worker, SchedulingLane.Background)]
    [TestCase(RuntimeRole.Researcher, SchedulingLane.Background)]
    public void TryAdmit_Assigns_Lane_By_Role(RuntimeRole role, SchedulingLane expectedLane)
    {
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(role, BaseModelAsset(role, GB(1)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(16), ReservedBytes: 0);

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Lane, Is.EqualTo(expectedLane));
    }

    [Test]
    public void TryAdmit_Uses_Fallback_Estimate_For_Adapter_With_Unknown_Size()
    {
        // PeftAdapterDirectory assets always have SizeBytes: null (ModelDepot never sizes
        // directories). A budget that fits the base model alone but not base + the fallback
        // estimate should be denied -- confirming the unknown adapter size isn't silently
        // treated as zero cost.
        var scheduler = new OrcScheduler();
        var baseOnly = GB(4);
        var baseModel = BaseModelAsset(RuntimeRole.Boss, baseOnly);
        var unsizedAdapter = AdapterAsset(RuntimeRole.Boss, sizeBytes: null);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, unsizedAdapter);
        var budget = new VramBudget(TotalBytes: baseOnly, ReservedBytes: 0); // fits base exactly, no room for adapter

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.False);
    }

    [Test]
    public void TryAdmit_Ignores_Adapter_Size_Entirely_When_No_Adapter_Bound()
    {
        var scheduler = new OrcScheduler();
        var baseOnly = GB(4);
        var baseModel = BaseModelAsset(RuntimeRole.Boss, baseOnly);
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, Adapter: null);
        var budget = new VramBudget(TotalBytes: baseOnly, ReservedBytes: 0); // fits base exactly

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.True);
    }

    [Test]
    public void TryAdmit_Treats_Null_BaseModel_SizeBytes_As_Zero_Cost()
    {
        // ModelDepot never produces a BaseModelGguf asset with SizeBytes: null (it's always a
        // single file, never a directory) -- but TryAdmit doesn't assume that invariant holds,
        // it falls back to 0 defensively. Documents that this is a fail-OPEN (under-estimate,
        // always admits) rather than fail-closed default, since a caller passing malformed data
        // gets "admitted" rather than a thrown exception or a denial with no clear reason.
        var scheduler = new OrcScheduler();
        var baseModel = BaseModelAsset(RuntimeRole.Boss, sizeBytes: 0) with { SizeBytes = null };
        var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, Adapter: null);
        var budget = new VramBudget(TotalBytes: 1, ReservedBytes: 0); // tiny budget — only admits if cost is treated as 0

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.That(decision.Admitted, Is.True);
    }

    [Test]
    public void TryAdmit_Denial_Still_Assigns_Correct_Lane()
    {
        // The lane-by-role tests above only exercise the admitted path. Confirms lane assignment
        // doesn't depend on the admission outcome -- a denied Worker request is still Background,
        // not accidentally defaulted to Interactive or omitted from the decision.
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Worker, BaseModelAsset(RuntimeRole.Worker, GB(100)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(1), ReservedBytes: 0);

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(decision.Admitted, Is.False);
            Assert.That(decision.Lane, Is.EqualTo(SchedulingLane.Background));
        });
    }

    [Test]
    public void TryAdmit_Treats_Fully_Reserved_Budget_As_Zero_Available()
    {
        // VramBudget.AvailableBytes clamps to zero rather than going negative when
        // ReservedBytes exceeds TotalBytes (e.g. stale accounting after a role was admitted
        // against a budget that has since shrunk). Any non-zero requirement must be denied.
        var scheduler = new OrcScheduler();
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Boss, BaseModelAsset(RuntimeRole.Boss, GB(1)), Adapter: null);
        var budget = new VramBudget(TotalBytes: GB(4), ReservedBytes: GB(10)); // over-reserved

        var decision = scheduler.TryAdmit(binding, budget);

        Assert.Multiple(() =>
        {
            Assert.That(budget.AvailableBytes, Is.EqualTo(0));
            Assert.That(decision.Admitted, Is.False);
        });
    }

    // ── Phase B addendum: context-aware estimate (docs/NATIVE_RUNTIME_V2_SPEC.md) ──────────

    [Test]
    public void EstimateRequiredBytes_Without_Options_Preserves_Legacy_FileSize_Behavior()
    {
        var binding = Binding(RuntimeRole.Boss, baseSizeBytes: GB(4), adapterSizeBytes: GB(0.1));

        Assert.That(OrcScheduler.EstimateRequiredBytes(binding, options: null),
            Is.EqualTo(GB(4) + GB(0.1)));
    }

    [Test]
    public void EstimateRequiredBytes_With_Options_Falls_Back_To_Legacy_When_Header_Unreadable()
    {
        // "base.gguf" doesn't exist on disk — header read fails => estimation must degrade to
        // the pre-addendum behavior, never make admission WORSE than before. Adapter: null (no
        // adapter at all — the Binding() helper's adapterSizeBytes:null instead means an
        // UNSIZED adapter, which legitimately adds the 512MB fallback).
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Boss, BaseModelAsset(RuntimeRole.Boss, GB(4)), Adapter: null);

        Assert.That(
            OrcScheduler.EstimateRequiredBytes(binding, new RuntimeOptions(ContextLength: 8192)),
            Is.EqualTo(GB(4)));
    }

    [Test]
    public void EstimateRequiredBytes_With_Options_And_Real_Header_Adds_Kv_And_Allowances()
    {
        // Fixture mirrors Llama-3.2-3B's real dims: the spike validated this exact formula
        // byte-exactly against llama.cpp's allocator (896 MiB at n_ctx=8192).
        var path = WriteLlamaHeaderFixture(blockCount: 28, headCountKv: 8, keyLength: 128);
        try
        {
            var baseModel = BaseModelAsset(RuntimeRole.Boss, GB(2)) with { Path = path };
            var binding = new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, Adapter: null);

            var estimate = OrcScheduler.EstimateRequiredBytes(
                binding, new RuntimeOptions(ContextLength: 8192));

            long expectedKv = 28L * 8192 * 8 * (128 + 128) * 2; // 896 MiB, spike-validated
            Assert.That(estimate, Is.EqualTo(
                GB(2)
                + OrcScheduler.CudaRuntimeOverheadBytes
                + expectedKv
                + OrcScheduler.ComputeBufferAllowanceBytes));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Test]
    public void EstimateRequiredBytes_Adds_Recurrent_Term_Only_For_Known_Hybrid_Architectures()
    {
        var mambaPath = WriteHeaderFixture("mamba", blockCount: 28, headCountKv: 8, keyLength: 128);
        var llamaPath = WriteLlamaHeaderFixture(blockCount: 28, headCountKv: 8, keyLength: 128);
        try
        {
            var options = new RuntimeOptions(ContextLength: 2048);
            var mamba = new RuntimeRoleBinding(RuntimeRole.Boss,
                BaseModelAsset(RuntimeRole.Boss, GB(2)) with { Path = mambaPath }, Adapter: null);
            var llama = new RuntimeRoleBinding(RuntimeRole.Boss,
                BaseModelAsset(RuntimeRole.Boss, GB(2)) with { Path = llamaPath }, Adapter: null);

            var difference = OrcScheduler.EstimateRequiredBytes(mamba, options)
                           - OrcScheduler.EstimateRequiredBytes(llama, options);

            // The ONLY difference between the two fixtures is the architecture name — so the
            // delta must be exactly the recurrent-state term, and the plain transformer must
            // not pay it (the over-reserve hazard that originally deferred this work).
            Assert.That(difference, Is.EqualTo(
                OrcScheduler.RecurrentStatePerSlotBytes * AdapterManager.SequenceHardLimit));
        }
        finally
        {
            File.Delete(mambaPath);
            File.Delete(llamaPath);
        }
    }

    [Test]
    public void TryAdmit_With_Options_Denies_What_Legacy_FileSize_Estimate_Would_Admit()
    {
        // The 2.2x-underestimate scenario from the spike, in miniature: budget fits the file
        // size exactly, but not file size + context costs. Legacy admits; context-aware denies.
        var path = WriteLlamaHeaderFixture(blockCount: 28, headCountKv: 8, keyLength: 128);
        try
        {
            var scheduler = new OrcScheduler();
            var binding = new RuntimeRoleBinding(RuntimeRole.Boss,
                BaseModelAsset(RuntimeRole.Boss, GB(2)) with { Path = path }, Adapter: null);
            var budget = new VramBudget(TotalBytes: GB(2), ReservedBytes: 0);

            Assert.Multiple(() =>
            {
                Assert.That(scheduler.TryAdmit(binding, budget).Admitted, Is.True,
                    "legacy estimate admits the exact-file-size fit");
                Assert.That(scheduler.TryAdmit(binding, budget, new RuntimeOptions(ContextLength: 8192)).Admitted,
                    Is.False,
                    "context-aware estimate must count KV + overheads and deny");
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteLlamaHeaderFixture(int blockCount, int headCountKv, int keyLength) =>
        WriteHeaderFixture("llama", blockCount, headCountKv, keyLength);

    /// <summary>Minimal valid GGUF v3 header — same writer logic GgufMetadataReaderTests
    /// exercises in detail; duplicated minimally here to keep this file self-contained.</summary>
    private static string WriteHeaderFixture(string arch, int blockCount, int headCountKv, int keyLength)
    {
        var path = Path.Combine(Path.GetTempPath(), "orc-sched-gguf-" + Guid.NewGuid().ToString("N") + ".gguf");
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(stream, System.Text.Encoding.UTF8);

        void Str(string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length);
            w.Write(b);
        }
        void KvU32(string key, uint value) { Str(key); w.Write(4u); w.Write(value); }

        w.Write(0x46554747u); // "GGUF"
        w.Write(3u);
        w.Write(0ul);          // tensor_count
        w.Write(5ul);          // kv_count
        Str("general.architecture"); w.Write(8u); Str(arch);
        KvU32($"{arch}.block_count", (uint)blockCount);
        KvU32($"{arch}.attention.head_count_kv", (uint)headCountKv);
        KvU32($"{arch}.attention.key_length", (uint)keyLength);
        KvU32($"{arch}.attention.value_length", (uint)keyLength);
        return path;
    }

    private static long GB(double gb) => (long)(gb * 1024 * 1024 * 1024);

    private static RuntimeRoleBinding Binding(RuntimeRole role, long baseSizeBytes, long? adapterSizeBytes)
    {
        var baseModel = BaseModelAsset(role, baseSizeBytes);
        var adapter = AdapterAsset(role, adapterSizeBytes);
        return new RuntimeRoleBinding(role, baseModel, adapter);
    }

    private static RuntimeModelAsset BaseModelAsset(RuntimeRole role, long sizeBytes) => new(
        Id: "base",
        Kind: RuntimeAssetKind.BaseModelGguf,
        Path: "base.gguf",
        DisplayName: "base.gguf",
        SizeBytes: sizeBytes,
        LastModifiedUtc: DateTimeOffset.UnixEpoch,
        SuggestedRoles: [role]);

    private static RuntimeModelAsset AdapterAsset(RuntimeRole role, long? sizeBytes) => new(
        Id: "adapter",
        Kind: RuntimeAssetKind.LoraGguf,
        Path: "adapter.gguf",
        DisplayName: "adapter.gguf",
        SizeBytes: sizeBytes,
        LastModifiedUtc: DateTimeOffset.UnixEpoch,
        SuggestedRoles: [role]);
}
