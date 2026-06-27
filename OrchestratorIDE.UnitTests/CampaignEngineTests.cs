// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class CampaignEngineTests
{
    private readonly List<string> _tempDirs = [];

    [TearDown]
    public void Cleanup()
    {
        foreach (var dir in _tempDirs)
            try { Directory.Delete(dir, recursive: true); } catch { }
        _tempDirs.Clear();
    }

    [Test]
    public void CapabilityMatcher_RejectsMissingNativeAsset_AndRewardsLocality()
    {
        var digest = new string('a', 64);
        var input = new string('b', 64);
        var bundle = new HiveTaskBundle
        {
            ExecutionKind = HiveExecutionKinds.NativeAgent,
            Requirements = new ResourceRequirements { MinCpuCores = 4, MinMemoryMb = 4096, NativeModelHash = digest },
            InputArtifacts = [new ArtifactRef { DigestSha256 = input, SizeBytes = 10 }],
        };
        var missing = new WorkerCapabilities
        {
            CpuCores = 16, AvailableMemoryMb = 32_000,
            ExecutionKinds = [HiveExecutionKinds.NativeAgent],
        };
        var ready = missing with
        {
            NativeModelHashes = [digest],
            CachedArtifacts = [input],
            FreeVramMb = 12_000,
        };

        Assert.Multiple(() =>
        {
            Assert.That(CampaignCapabilityMatcher.IsEligible(bundle, missing), Is.False);
            Assert.That(CampaignCapabilityMatcher.IsEligible(bundle, ready), Is.True);
            Assert.That(CampaignCapabilityMatcher.Score(bundle, ready), Is.GreaterThan(1000));
        });
    }

    [Test]
    public async Task ContentStore_Resumes_AtomicallyVerifies_AndRejectsBadDigest()
    {
        var store = NewStore();
        var bytes = "warbands pool verifiable work"u8.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        var first = await store.WriteChunkAsync(digest, 0, bytes.Length, bytes.AsMemory(0, 8));
        Assert.That(first.Complete, Is.False);
        Assert.That(store.GetResumeOffset(digest), Is.EqualTo(8));
        var complete = await store.WriteChunkAsync(digest, 8, bytes.Length, bytes.AsMemory(8));

        Assert.Multiple(() =>
        {
            Assert.That(complete.Complete, Is.True);
            Assert.That(store.Has(digest), Is.True);
            Assert.That(File.ReadAllBytes(store.GetPath(digest)), Is.EqualTo(bytes));
            Assert.That(() => store.GetPath("../escape"), Throws.ArgumentException);
        });

        var bad = new string('0', 64);
        Assert.That(async () => await store.WriteChunkAsync(bad, 0, bytes.Length, bytes),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(store.GetResumeOffset(bad), Is.Zero, "A hash mismatch must discard the partial object.");
    }

    [Test]
    public void CampaignLifecycle_PauseResumeCancel_PreservesLogicalTotals()
    {
        using var queue = new HiveTaskQueue();
        var campaign = CampaignTemplates.NativeAiEval("smoke", ["one", "two"], new string('c', 64));
        queue.SubmitCampaign(campaign);

        Assert.That(queue.GetCampaignStatus(campaign.CampaignId)!.Pending, Is.EqualTo(2));
        Assert.That(queue.SetCampaignState(campaign.CampaignId, CampaignStates.Paused), Is.True);
        Assert.That(queue.GetCampaignStatus(campaign.CampaignId)!.Status, Is.EqualTo(CampaignStates.Paused));
        Assert.That(queue.SetCampaignState(campaign.CampaignId, CampaignStates.Running), Is.True);
        Assert.That(queue.SetCampaignState(campaign.CampaignId, CampaignStates.Cancelled), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(queue.GetCampaignStatus(campaign.CampaignId)!.Cancelled, Is.EqualTo(2));
            Assert.That(queue.GetCampaignStatus(campaign.CampaignId)!.Total, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task HeadlessLoop_ExecutesAllowedTool_ThenReturnsFinalAnswer()
    {
        var runtime = new ToolThenAnswerRuntime();
        var toolRan = false;
        var tool = new HeadlessTool("read_file", new { type = "function" }, (args, _) =>
        {
            toolRan = true;
            return Task.FromResult("trusted contents");
        });
        var loop = new HeadlessAgentLoop(runtime);

        var result = await loop.ExecuteAsync(RuntimeRole.Worker,
            [new AgentMessage { Role = MessageRole.User, Content = "Inspect it." }],
            [tool], new HeadlessAgentLimits(MaxSteps: 3));

        Assert.Multiple(() =>
        {
            Assert.That(toolRan, Is.True);
            Assert.That(runtime.SawToolResult, Is.True);
            Assert.That(result.Output, Is.EqualTo("verified answer"));
            Assert.That(result.Steps, Is.EqualTo(2));
            Assert.That(result.TraceDigest, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void VerificationPolicy_RequiresIndependentNodes_AndMatchingProvenance()
    {
        var attestation = new ExecutionAttestation
        {
            RuntimeName = "native",
            ModelHash = new string('d', 64),
            InputDigests = new() { ["case"] = new string('e', 64) },
        };
        var evidence = new[]
        {
            new HiveTaskResult { WorkerId = "node-a", Result = "candidate", Attestation = attestation },
            new HiveTaskResult { WorkerId = "node-b", Result = "candidate", Attestation = attestation },
        };
        var policy = new VerificationPolicy
        {
            Mode = "independent_rerun", RequiredIndependentRuns = 2, RequireDifferentNode = true,
        };

        Assert.That(HiveTaskQueue.VerifyEvidence(policy, evidence, 2), Is.Null);
        Assert.That(HiveTaskQueue.VerifyEvidence(policy, [evidence[0], evidence[0]], 2),
            Does.Contain("different worker"));
        var different = new HiveTaskResult { WorkerId = "node-b", Result = "different", Attestation = attestation };
        Assert.That(HiveTaskQueue.VerifyEvidence(policy with { Mode = "deterministic_rerun" },
            [evidence[0], different], 2), Does.Contain("did not match"));
    }

    private ContentAddressedStore NewStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "theorc-campaign-test-" + Guid.NewGuid().ToString("N"));
        _tempDirs.Add(dir);
        return new ContentAddressedStore(dir, maxObjectBytes: 1024 * 1024, maxStoreBytes: 2 * 1024 * 1024);
    }

    private sealed class ToolThenAnswerRuntime : IRoleRuntime
    {
        private int _calls;
        public bool SawToolResult { get; private set; }
        public string RuntimeName => "test-native";

        public async IAsyncEnumerable<string> StreamRoleCompletionAsync(RuntimeRole role,
            IEnumerable<AgentMessage> history, IReadOnlyList<object>? tools = null,
            double temperature = 0.1, int maxTokens = 4096, Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null, [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            _calls++;
            if (_calls == 1)
            {
                onToolCall?.Invoke(new ToolCall { Name = "read_file", Arguments = new() { ["path"] = "a.txt" } });
                yield break;
            }
            SawToolResult = history.Any(m => m.Content.Contains("trusted contents", StringComparison.Ordinal));
            onUsage?.Invoke(4, 2);
            yield return "verified answer";
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName, "fake.gguf");
        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName, "fake.gguf");
    }
}
