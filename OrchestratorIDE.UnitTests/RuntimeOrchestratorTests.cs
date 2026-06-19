// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// RuntimeOrchestrator wires SessionManager + AdapterManager together. Only the
/// "ModelDepot couldn't resolve a base model" failure path is testable without a real GGUF:
/// it returns from SessionManager.LoadRoleAsync before AdapterManager or any native LLamaSharp
/// object is ever touched. The success path (real conversation on a real adapter-attached
/// executor) is covered by the §7 spike harness and manual verification, same precedent as
/// AdapterManagerTests and LLamaSharpRuntime itself.
/// </summary>
[TestFixture]
public sealed class RuntimeOrchestratorTests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort cleanup for Windows file handles held briefly by test hosts.
            }
        }
        _tempRoots.Clear();
    }

    [Test]
    public async Task Constructor_Throws_When_SessionManager_Is_Null()
    {
        await using var runtime = new LLamaSharpRuntime();
        var adapterManager = new AdapterManager(runtime);

        Assert.Throws<ArgumentNullException>(
            () => new RuntimeOrchestrator(null!, adapterManager));
    }

    [Test]
    public void Constructor_Throws_When_AdapterManager_Is_Null()
    {
        var sessionManager = new SessionManager(new EmptyLocalModelRuntime());

        Assert.Throws<ArgumentNullException>(
            () => new RuntimeOrchestrator(sessionManager, null!));
    }

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_Depot_Is_Null()
    {
        await using var runtime = new LLamaSharpRuntime();
        var sessionManager = new SessionManager(new EmptyLocalModelRuntime());
        var adapterManager = new AdapterManager(runtime);
        await using var orchestrator = new RuntimeOrchestrator(sessionManager, adapterManager);

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await orchestrator.GetConversationForRoleAsync(null!, RuntimeRole.Boss));
    }

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_No_Base_Model_Resolved()
    {
        var root = NewTempRoot();
        WriteFile(root, "adapters", "worker-lora.gguf"); // adapter present, but no base model

        await using var runtime = new LLamaSharpRuntime();
        var sessionManager = new SessionManager(new EmptyLocalModelRuntime());
        var adapterManager = new AdapterManager(runtime);
        await using var orchestrator = new RuntimeOrchestrator(sessionManager, adapterManager);

        var ex = Assert.ThrowsAsync<InvalidOperationException>(
            async () => await orchestrator.GetConversationForRoleAsync(
                ModelDepot.Scan(root), RuntimeRole.Worker));

        Assert.That(ex!.Message, Does.Contain("No base GGUF resolved"));
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-runtime-orchestrator-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static void WriteFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake model bytes");
    }

    /// <summary>Minimal ILocalModelRuntime that never succeeds a load — sufficient for the
    /// failure-path tests above, which never reach LoadModelAsync at all.</summary>
    private sealed class EmptyLocalModelRuntime : ILocalModelRuntime
    {
        public string RuntimeName => "Empty";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public RuntimeHealth GetHealth() => new(IsAvailable: false, RuntimeName: RuntimeName);

        public RuntimeStats GetStats() => new(RuntimeName: RuntimeName);

        public Task<ModelLoadResult> LoadModelAsync(
            string baseGgufPath,
            string? adapterPath = null,
            RuntimeOptions? options = null,
            CancellationToken ct = default) =>
            throw new InvalidOperationException("Should never be called by the failure-path tests.");

        public Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
