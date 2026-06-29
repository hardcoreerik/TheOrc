// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// CF-5: covers the library drawer toggle and corpus attach/detach state in ChatPanel's
/// corpus bar. Mirrors ChatPanelModeToggleTests' Required&lt;T&gt;/reflection conventions.
/// </summary>
[TestFixture]
public class OrcChatLibraryTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button) =>
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void LibraryDrawer_And_SourcePreview_AreHidden_ByDefault()
    {
        var panel = new ChatPanel();

        Assert.Multiple(() =>
        {
            Assert.That(Required<LibraryDrawerControl>(panel, "LibraryDrawer").IsVisible, Is.False);
            Assert.That(Required<SourcePreviewPanel>(panel, "SourcePreviewPanel").IsVisible, Is.False);
            Assert.That(Required<Border>(panel, "BdrCorpusBadge").IsVisible, Is.False);
        });
    }

    [AvaloniaTest]
    public void ToggleLibraryButton_ShowsAndHidesDrawer()
    {
        var panel = new ChatPanel();
        var toggle = Required<ToggleButton>(panel, "BtnToggleLibrary");

        Click(toggle);
        Dispatcher.UIThread.RunJobs();
        Assert.That(Required<LibraryDrawerControl>(panel, "LibraryDrawer").IsVisible, Is.True);

        Click(toggle);
        Dispatcher.UIThread.RunJobs();
        Assert.That(Required<LibraryDrawerControl>(panel, "LibraryDrawer").IsVisible, Is.False);
    }

    [AvaloniaTest]
    public void AttachingCorpus_ShowsBadgeAndModeToggle()
    {
        using var harness = Cf5TestHarness.Create();
        var panel = new ChatPanel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime("{}")),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            harness.NewLibraryViewModel(),
            webImporter: null,
            harness.WorkspaceRoot);

        InvokeAttach(panel, harness.Corpus.CorpusId);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Required<Border>(panel, "BdrCorpusBadge").IsVisible, Is.True);
            Assert.That(Required<TextBlock>(panel, "TxtCorpusName").Text, Is.EqualTo(harness.Corpus.Name));
            Assert.That(Required<StackPanel>(panel, "StackModeToggle").IsVisible, Is.True);
        });
    }

    [AvaloniaTest]
    public void DetachingCorpus_HidesBadgeAndSourcePreview()
    {
        using var harness = Cf5TestHarness.Create();
        var panel = new ChatPanel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime("{}")),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            harness.NewLibraryViewModel(),
            webImporter: null,
            harness.WorkspaceRoot);

        InvokeAttach(panel, harness.Corpus.CorpusId);
        Dispatcher.UIThread.RunJobs();
        Required<SourcePreviewPanel>(panel, "SourcePreviewPanel").IsVisible = true;

        InvokeDetach(panel);
        Dispatcher.UIThread.RunJobs();

        Assert.Multiple(() =>
        {
            Assert.That(Required<Border>(panel, "BdrCorpusBadge").IsVisible, Is.False);
            Assert.That(Required<StackPanel>(panel, "StackModeToggle").IsVisible, Is.False);
            Assert.That(Required<SourcePreviewPanel>(panel, "SourcePreviewPanel").IsVisible, Is.False);
        });
    }

    [AvaloniaTest]
    public void ModeToggle_SwitchesHintText()
    {
        using var harness = Cf5TestHarness.Create();
        var panel = new ChatPanel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime("{}")),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            harness.NewLibraryViewModel(),
            webImporter: null,
            harness.WorkspaceRoot);

        InvokeAttach(panel, harness.Corpus.CorpusId);
        Dispatcher.UIThread.RunJobs();

        Click(Required<Button>(panel, "BtnModeStudy"));
        Dispatcher.UIThread.RunJobs();
        Assert.That(Required<TextBlock>(panel, "TxtModeHint").Text, Does.Contain("iterative retrieval"));

        Click(Required<Button>(panel, "BtnModeQuick"));
        Dispatcher.UIThread.RunJobs();
        Assert.That(Required<TextBlock>(panel, "TxtModeHint").Text, Does.Contain("hybrid retrieval"));
    }

    internal static void InvokeAttach(ChatPanel panel, string corpusId)
    {
        var method = typeof(ChatPanel).GetMethod("OnCorpusAttachRequested", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnCorpusAttachRequested not found via reflection.");
        method.Invoke(panel, [corpusId]);
    }

    internal static void InvokeDetach(ChatPanel panel)
    {
        var method = typeof(ChatPanel).GetMethod("OnCorpusDetachRequested", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnCorpusDetachRequested not found via reflection.");
        method.Invoke(panel, null);
    }
}
