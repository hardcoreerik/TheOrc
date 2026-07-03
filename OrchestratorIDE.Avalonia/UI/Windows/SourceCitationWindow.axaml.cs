// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Popup dialog shown on citation click -- replaces the earlier docked side-rail
/// (SourcePreviewPanel is now hosted here rather than in ChatPanel's layout grid).
/// </summary>
public partial class SourceCitationWindow : Window
{
    public SourceCitationWindow()
    {
        InitializeComponent();
    }

    public SourceCitationWindow(
        CitationViewModel citation,
        LibraryViewModel libraryVm,
        Action<CitationViewModel>? onSaveToNotebook) : this()
    {
        Preview.CloseRequested += Close;
        if (onSaveToNotebook is not null)
            Preview.SaveToNotebookRequested += onSaveToNotebook;
        Preview.LoadCitation(citation, libraryVm);
        Title = $"Source citation · #{citation.Index}";
    }
}
