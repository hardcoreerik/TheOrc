// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// RuntimeOrchestrator constructs SessionManager and AdapterManager itself from a single
/// LLamaSharpRuntime (see its class doc for why — a prior draft accepted both independently and
/// review caught that nothing then enforced they shared the same runtime instance). Only the
/// "ModelDepot couldn't resolve a base model" failure path is testable without a real GGUF: it
/// returns from SessionManager.LoadRoleAsync before AdapterManager or any native LLamaSharp
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
    public void Constructor_Throws_When_Runtime_Is_Null() =>
        Assert.Throws<ArgumentNullException>(() => new RuntimeOrchestrator(null!));

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_Depot_Is_Null()
    {
        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await orchestrator.GetConversationForRoleAsync(null!, RuntimeRole.Boss));
    }

    [Test]
    public async Task GetConversationForRoleAsync_Throws_When_No_Base_Model_Resolved()
    {
        var root = NewTempRoot();
        WriteFile(root, "adapters", "worker-lora.gguf"); // adapter present, but no base model

        await using var runtime = new LLamaSharpRuntime();
        await using var orchestrator = new RuntimeOrchestrator(runtime);

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
}
