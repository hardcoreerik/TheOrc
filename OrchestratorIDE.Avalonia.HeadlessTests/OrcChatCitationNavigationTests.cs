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
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// CF-5: citation source preview is hosted in a popup window so the chat layout does not
/// collapse on long sources.
/// </summary>
[TestFixture]
public class OrcChatCitationNavigationTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    [AvaloniaTest]
    public void SourceCitationWindow_LoadsPreview()
    {
        using var harness = Cf5TestHarness.Create();
        var libraryVm = harness.NewLibraryViewModel();
        var citation = new CitationViewModel(
            1, "seg-0", harness.Document.DocumentId, "Section 0",
            0, "LANTERN is the assigned call sign.".Length,
            "LANTERN is the assigned call sign.", "Supported");

        var window = new SourceCitationWindow(citation, libraryVm, onSaveToNotebook: null);
        var preview = Required<SourcePreviewPanel>(window, "Preview");

        Assert.Multiple(() =>
        {
            Assert.That(preview.IsVisible, Is.True);
            Assert.That(window.Title, Is.EqualTo("Source citation · #1"));
        });
    }
}
