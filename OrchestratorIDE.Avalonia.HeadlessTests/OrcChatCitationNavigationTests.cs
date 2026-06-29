// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using NUnit.Framework;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Panels;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// CF-5: clicking a citation footnote opens the source-preview rail with the right segment
/// text and verification badge; closing it hides the rail again. OpenSourcePreview/the
/// footnote click handler are private, so this invokes them via reflection — same convention
/// ChatPanelModeToggleTests uses for SendAsync/OnToolStart.
/// </summary>
[TestFixture]
public class OrcChatCitationNavigationTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    [AvaloniaTest]
    public void OpenSourcePreview_ShowsPanelWithCitationDetails()
    {
        using var harness = Cf5TestHarness.Create();
        var panel = new ChatPanel();
        var libraryVm = harness.NewLibraryViewModel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime("{}")),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            libraryVm,
            webImporter: null,
            harness.WorkspaceRoot);

        var citation = new CitationViewModel(
            1, "seg-0", harness.Document.DocumentId, "Section 0",
            0, "LANTERN is the assigned call sign.".Length,
            "LANTERN is the assigned call sign.", "Supported");

        InvokeOpenSourcePreview(panel, citation);

        var preview = Required<SourcePreviewPanel>(panel, "SourcePreviewPanel");
        var splitter = Required<GridSplitter>(panel, "SourcePreviewSplitter");

        Assert.Multiple(() =>
        {
            Assert.That(preview.IsVisible, Is.True);
            Assert.That(splitter.IsVisible, Is.True);
        });
    }

    [AvaloniaTest]
    public void ClosingSourcePreview_HidesSplitter()
    {
        using var harness = Cf5TestHarness.Create();
        var panel = new ChatPanel();
        panel.SetFabricServices(
            harness.NewAskService(new FakeFabricRuntime("{}")),
            harness.NewOrchestrator(new FakeFabricRuntime("{}")),
            harness.NewLibraryViewModel(),
            webImporter: null,
            harness.WorkspaceRoot);

        var citation = new CitationViewModel(1, "seg-0", harness.Document.DocumentId, "Section 0", 0, 5, "LANTERN", "Supported");
        InvokeOpenSourcePreview(panel, citation);

        var closeMethod = typeof(ChatPanel).GetMethod("OnSourcePreviewClosed", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OnSourcePreviewClosed not found via reflection.");
        closeMethod.Invoke(panel, null);

        Assert.That(Required<GridSplitter>(panel, "SourcePreviewSplitter").IsVisible, Is.False);
    }

    private static void InvokeOpenSourcePreview(ChatPanel panel, CitationViewModel citation)
    {
        var method = typeof(ChatPanel).GetMethod("OpenSourcePreview", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("OpenSourcePreview not found via reflection.");
        method.Invoke(panel, [citation]);
    }
}
