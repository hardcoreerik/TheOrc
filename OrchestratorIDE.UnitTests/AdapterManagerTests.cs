// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// AdapterManager's native-dependent behavior (executor lifecycle, reference counting against
/// real Conversation objects, weights-generation invalidation, LoRA load/unload) cannot be
/// unit-tested with mocks: BatchedExecutor, Conversation, and LoraAdapter are sealed types from
/// the LLamaSharp package with no virtual surface, and LLamaSharpRuntime itself is sealed with
/// no existing test seam — consistent with LLamaSharpRuntime having zero unit tests of its own
/// in this codebase today (its native paths are verified by the §7 spike harness at
/// .grok/spike-assets/HotSwapSpike/, not NUnit). This file covers the logic that genuinely has
/// no native dependency: BindingMatches' comparison semantics, and the null-argument guards that
/// run before any native object is touched.
/// </summary>
[TestFixture]
public sealed class AdapterManagerTests
{
    [Test]
    public void Constructor_Throws_When_Runtime_Is_Null() =>
        Assert.Throws<ArgumentNullException>(() => new AdapterManager(null!));

    [Test]
    public async Task CreateConversationAsync_Throws_When_Binding_Is_Null()
    {
        await using var manager = new AdapterManager(new LLamaSharpRuntime());

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await manager.CreateConversationAsync(null!));
    }

    [Test]
    public async Task RebindRoleAsync_Throws_When_Binding_Is_Null()
    {
        await using var manager = new AdapterManager(new LLamaSharpRuntime());

        Assert.ThrowsAsync<ArgumentNullException>(
            async () => await manager.RebindRoleAsync(null!));
    }

    [Test]
    public void BindingMatches_True_For_Identical_Bindings()
    {
        var binding = Boss("base.gguf", "boss-lora.gguf");

        Assert.That(AdapterManager.BindingMatches(binding, binding), Is.True);
    }

    [Test]
    public void BindingMatches_True_When_Paths_Differ_Only_By_Case()
    {
        var a = Boss(@"C:\models\BASE.gguf", @"C:\adapters\BOSS-LORA.gguf");
        var b = Boss(@"C:\models\base.gguf", @"C:\adapters\boss-lora.gguf");

        Assert.That(AdapterManager.BindingMatches(a, b), Is.True);
    }

    [Test]
    public void BindingMatches_True_When_Neither_Has_An_Adapter()
    {
        var a = Boss("base.gguf", adapterPath: null);
        var b = Boss("base.gguf", adapterPath: null);

        Assert.That(AdapterManager.BindingMatches(a, b), Is.True);
    }

    [Test]
    public void BindingMatches_False_When_Role_Differs()
    {
        var boss = Boss("base.gguf", "lora.gguf");
        var worker = new RuntimeRoleBinding(
            RuntimeRole.Worker, boss.BaseModel, boss.Adapter);

        Assert.That(AdapterManager.BindingMatches(boss, worker), Is.False);
    }

    [Test]
    public void BindingMatches_False_When_Base_Model_Path_Differs()
    {
        var a = Boss("base-v1.gguf", "lora.gguf");
        var b = Boss("base-v2.gguf", "lora.gguf");

        Assert.That(AdapterManager.BindingMatches(a, b), Is.False);
    }

    [Test]
    public void BindingMatches_False_When_Adapter_Path_Differs()
    {
        var a = Boss("base.gguf", "lora-v1.gguf");
        var b = Boss("base.gguf", "lora-v2.gguf");

        Assert.That(AdapterManager.BindingMatches(a, b), Is.False);
    }

    [Test]
    public void BindingMatches_False_When_One_Has_An_Adapter_And_The_Other_Does_Not()
    {
        var withAdapter = Boss("base.gguf", "lora.gguf");
        var withoutAdapter = Boss("base.gguf", adapterPath: null);

        Assert.Multiple(() =>
        {
            Assert.That(AdapterManager.BindingMatches(withAdapter, withoutAdapter), Is.False);
            Assert.That(AdapterManager.BindingMatches(withoutAdapter, withAdapter), Is.False);
        });
    }

    private static RuntimeRoleBinding Boss(string basePath, string? adapterPath)
    {
        var baseModel = new RuntimeModelAsset(
            Id: "base",
            Kind: RuntimeAssetKind.BaseModelGguf,
            Path: basePath,
            DisplayName: System.IO.Path.GetFileName(basePath),
            SizeBytes: null,
            LastModifiedUtc: DateTimeOffset.UnixEpoch,
            SuggestedRoles: [RuntimeRole.Boss]);

        var adapter = adapterPath is null
            ? null
            : new RuntimeModelAsset(
                Id: "adapter",
                Kind: RuntimeAssetKind.LoraGguf,
                Path: adapterPath,
                DisplayName: System.IO.Path.GetFileName(adapterPath),
                SizeBytes: null,
                LastModifiedUtc: DateTimeOffset.UnixEpoch,
                SuggestedRoles: [RuntimeRole.Boss]);

        return new RuntimeRoleBinding(RuntimeRole.Boss, baseModel, adapter);
    }
}
