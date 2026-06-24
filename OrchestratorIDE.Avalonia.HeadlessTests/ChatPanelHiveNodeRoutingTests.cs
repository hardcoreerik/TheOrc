// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.Tests;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless coverage for ChatPanel's Phase B3 HIVE node-routing picker -- "Local" or a
/// specific paired node as the chat's inference target, the same routing concept
/// SwarmSession already uses per-task, applied to chat. Uses HiveHosts.Load's storePath
/// parameter (the same temp-file seam T14_HiveHostsTests uses) instead of touching this
/// machine's real persisted host list.
/// </summary>
[TestFixture]
public class ChatPanelHiveNodeRoutingTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private string _store = "";

    [SetUp]
    public void SetUp() => _store = Path.Combine(Path.GetTempPath(), $"chatpanel_hive_test_{Guid.NewGuid():N}.json");

    [TearDown]
    public void TearDown() { if (File.Exists(_store)) File.Delete(_store); }

    [AvaloniaTest]
    public void DefaultState_isLocal_withNoOtherNodesListed()
    {
        var panel = new ChatPanel();

        var node = Required<ComboBox>(panel, "CbNode");
        Assert.Multiple(() =>
        {
            Assert.That(node.SelectedItem, Is.EqualTo("Local"));
            Assert.That(node.Items, Has.Count.EqualTo(1));
        });
    }

    [AvaloniaTest]
    public void RefreshHiveHosts_listsPairedNodes_withoutDuplicatingLocal()
    {
        HiveHosts.Save(
        [
            new HiveHost { Name = "HARDCOREPC", Url = "http://192.168.1.20:11434" },
            new HiveHost { Name = "HARDCORELAPTOPM", Url = "http://192.168.1.117:11434" },
        ], _store);

        var panel = new ChatPanel();
        panel.RefreshHiveHosts(_store);

        var node = Required<ComboBox>(panel, "CbNode");
        var items = node.Items.Cast<string>().ToList();

        // HiveHosts.Load always injects its own "This PC" entry for the local machine (see
        // HiveHosts.Load's doc comment) -- must not appear alongside this panel's own
        // "Local" entry, which already represents the same machine via a different
        // (injected-OllamaClient) code path.
        Assert.Multiple(() =>
        {
            Assert.That(items, Is.EqualTo(new[] { "Local", "HARDCOREPC", "HARDCORELAPTOPM" }));
            Assert.That(items, Does.Not.Contain("This PC"));
        });
    }

    [AvaloniaTest]
    public void ResolveTargetNode_returnsNull_forLocal_andTheMatchingHost_forARemoteNode()
    {
        HiveHosts.Save([new HiveHost { Name = "HARDCOREPC", Url = "http://192.168.1.20:11434" }], _store);

        var panel = new ChatPanel();
        panel.RefreshHiveHosts(_store);

        Assert.That(panel.ResolveTargetNode(), Is.Null, "Local should resolve to null (caller falls back to the injected OllamaClient).");

        Required<ComboBox>(panel, "CbNode").SelectedItem = "HARDCOREPC";
        var resolved = panel.ResolveTargetNode();

        Assert.Multiple(() =>
        {
            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Url, Is.EqualTo("http://192.168.1.20:11434"));
        });
    }

    [AvaloniaTest]
    public async Task SwitchingToARemoteNode_startsAFreshConversation()
    {
        HiveHosts.Save([new HiveHost { Name = "HARDCOREPC", Url = "http://192.168.1.20:11434" }], _store);

        var fake  = new FakeOllamaClient();
        var panel = new ChatPanel { OllamaClient = fake };
        panel.SetModels(["fake-model:7b"], "fake-model:7b");
        panel.RefreshHiveHosts(_store);

        fake.Enqueue("hi");
        Required<TextBox>(panel, "TbInput").Text = "hello";
        Click(Required<Button>(panel, "BtnSend"));
        await WaitForCapture(fake);

        // A real exchange happened -- the welcome card must be gone.
        Assert.That(Required<Border>(panel, "BdrWelcome").IsVisible, Is.False);

        Required<ComboBox>(panel, "CbNode").SelectedItem = "HARDCOREPC";

        // Switching to a different (remote) node is a backend change, same as a mode
        // switch -- must reset the conversation UI rather than silently continue history
        // against a different machine's model.
        Assert.That(Required<Border>(panel, "BdrWelcome").IsVisible, Is.True);
    }

    [AvaloniaTest]
    public void RefreshHiveHosts_calledAgainWithSameSelection_doesNotResetAnInProgressConversation()
    {
        HiveHosts.Save([new HiveHost { Name = "HARDCOREPC", Url = "http://192.168.1.20:11434" }], _store);

        var panel = new ChatPanel();
        panel.RefreshHiveHosts(_store);

        // Simulate an in-progress conversation by hiding the welcome card directly --
        // RefreshHiveHosts clears and re-populates CbNode.Items, which fires
        // SelectionChanged even when the user hasn't touched anything; that must NOT be
        // mistaken for a real node switch and reset the conversation.
        Required<Border>(panel, "BdrWelcome").IsVisible = false;

        panel.RefreshHiveHosts(_store);   // same store, same selection ("Local") preserved

        Assert.That(Required<Border>(panel, "BdrWelcome").IsVisible, Is.False,
            "A no-op hosts refresh must not reset the conversation.");
    }

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task WaitForCapture(FakeOllamaClient fake)
    {
        for (var i = 0; i < 100 && fake.LastHistory is null; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }
}
