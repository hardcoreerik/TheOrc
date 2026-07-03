// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// CF-5 source-preview right rail — code-behind-only, mirrors LibraryDrawerControl's
/// dynamic-content convention.
/// </summary>
public partial class SourcePreviewPanel : UserControl
{
    public event Action? CloseRequested;
    public event Action<CitationViewModel>? SaveToNotebookRequested;

    private CitationViewModel? _citation;
    private LibraryViewModel? _libraryVm;

    public SourcePreviewPanel()
    {
        InitializeComponent();
        BuildHeader();
        BuildFooter();
    }

    public void LoadCitation(CitationViewModel citation, LibraryViewModel libraryVm)
    {
        _citation = citation;
        _libraryVm = libraryVm;
        IsVisible = true;

        var document = libraryVm.Repository.GetDocument(citation.DocumentId);
        var segment = libraryVm.Repository.GetSegment(citation.SegmentId);

        BodyStack.Children.Clear();

        var meta = new StackPanel { Spacing = 4 };
        meta.Children.Add(new TextBlock
        {
            Text = document?.DisplayName ?? citation.DocumentId,
            FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Brush(0xD8, 0xE4, 0xD0),
        });
        meta.Children.Add(new TextBlock
        {
            Text = $"{(string.IsNullOrWhiteSpace(citation.HeadingPath) ? segment?.HeadingPath ?? "" : citation.HeadingPath)} · {Short(citation.SegmentId)} · {citation.CharStart}-{citation.CharEnd}",
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            FontSize = 10, Foreground = Brush(0x6E, 0x80, 0x68), LineHeight = 16,
        });
        meta.Children.Add(BuildVerificationBadge(citation));

        BodyStack.Children.Add(new Border
        {
            Background = Brush(0x0E, 0x14, 0x0E), BorderBrush = Brush(0x1A, 0x24, 0x1A),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10), Child = meta,
        });

        BodyStack.Children.Add(BuildSourceText(segment?.Text ?? citation.Quote, citation));

        _headerLabel.Text = $"📄 Source · {Short(citation.SegmentId)}";
    }

    private TextBlock _headerLabel = new();

    private void BuildHeader()
    {
        HeaderBorder.BorderBrush = Brush(0x1E, 0x2E, 0x1E);
        HeaderBorder.BorderThickness = new Thickness(0, 0, 0, 1);
        HeaderBorder.Padding = new Thickness(14, 10);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        _headerLabel = new TextBlock
        {
            Text = "📄 Source",
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            FontSize = 11, Foreground = Brush(0x6E, 0x80, 0x68), VerticalAlignment = VerticalAlignment.Center,
        };
        var closeBtn = new Button
        {
            Content = "✕", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Foreground = Brush(0x5A, 0x6B, 0x5A), Padding = new Thickness(4),
        };
        closeBtn.Click += (_, _) => { IsVisible = false; CloseRequested?.Invoke(); };

        Grid.SetColumn(_headerLabel, 0);
        Grid.SetColumn(closeBtn, 1);
        row.Children.Add(_headerLabel);
        row.Children.Add(closeBtn);
        HeaderBorder.Child = row;
    }

    private void BuildFooter()
    {
        FooterBorder.BorderBrush = Brush(0x1E, 0x2E, 0x1E);
        FooterBorder.BorderThickness = new Thickness(0, 1, 0, 0);
        FooterBorder.Padding = new Thickness(14, 10);

        var stack = new StackPanel { Spacing = 6 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openBtn = new Button
        {
            Content = "Open source file", FontSize = 10.5, Padding = new Thickness(8, 4),
            Background = Brush(0x0E, 0x16, 0x0E), BorderBrush = Brush(0x1E, 0x2E, 0x1E), Foreground = Brush(0x9F, 0xB8, 0x90),
        };
        var saveBtn = new Button
        {
            Content = "Save to notebook", FontSize = 10.5, Padding = new Thickness(8, 4),
            Background = Brush(0x0E, 0x16, 0x0E), BorderBrush = Brush(0x1E, 0x2E, 0x1E), Foreground = Brush(0x9F, 0xB8, 0x90),
        };
        var openErrorText = new TextBlock
        {
            FontSize = 10, Foreground = Brush(0xC0, 0x61, 0x4F), TextWrapping = TextWrapping.Wrap, IsVisible = false,
        };
        openBtn.Click += (_, _) =>
        {
            var opened = TryOpenSourceFile(out var error);
            openErrorText.Text = opened ? "" : error;
            openErrorText.IsVisible = !opened;
        };
        saveBtn.Click += (_, _) =>
        {
            if (_citation is not null) SaveToNotebookRequested?.Invoke(_citation);
        };
        row.Children.Add(openBtn);
        row.Children.Add(saveBtn);
        stack.Children.Add(row);
        stack.Children.Add(openErrorText);
        FooterBorder.Child = stack;
    }

    /// <summary>
    /// Opens the citation's original source document (PDF, DOCX, etc.) via the OS default
    /// handler -- the CF-5 "source preview/open behavior" exit-gate requirement. Stages a
    /// properly-extensioned temp copy first since the content-addressed store keeps artifacts
    /// as ".blob", which the OS shell can't associate with any application.
    /// </summary>
    private bool TryOpenSourceFile(out string error)
    {
        error = "";
        if (_citation is null || _libraryVm is null)
        {
            error = "No citation loaded.";
            return false;
        }

        var stagedPath = _libraryVm.TryStageSourceFileForOpen(_citation.DocumentId);
        if (stagedPath is null)
        {
            error = "Source file is no longer available.";
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(stagedPath) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = $"Could not open source file: {ex.Message}";
            return false;
        }
    }

    private static Control BuildVerificationBadge(CitationViewModel citation)
    {
        var (text, fg, bg) = citation.IsVerified
            ? ("✓ supported — exact source match", Brush(0x7F, 0xB0, 0x69), Brush(0x13, 0x25, 0x13))
            : citation.IsInterpretive
                ? ("✎ interpretive", Brush(0x7F, 0x94, 0xA8), Brush(0x10, 0x16, 0x1C))
                : ("⚠ unverified", Brush(0xC0, 0x61, 0x4F), Brush(0x1E, 0x11, 0x0E));
        return new Border
        {
            Background = bg, CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock { Text = text, FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = fg },
        };
    }

    private static Control BuildSourceText(string sourceText, CitationViewModel citation)
    {
        var panel = new TextBlock
        {
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            FontSize = 12.5, LineHeight = 21, TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(0x8A, 0x9A, 0x84),
        };

        if (citation.CharStart >= 0 && citation.CharEnd > citation.CharStart && citation.CharEnd <= sourceText.Length)
        {
            var before = sourceText[..citation.CharStart];
            var quote = sourceText[citation.CharStart..citation.CharEnd];
            var after = sourceText[citation.CharEnd..];
            var highlightFg = citation.IsVerified ? Color.FromRgb(0xD8, 0xE4, 0xD0) : Color.FromRgb(0xD4, 0xE0, 0xE8);
            panel.Inlines!.Add(new Run(before));
            panel.Inlines!.Add(new Run(quote) { Foreground = new SolidColorBrush(highlightFg) });
            panel.Inlines!.Add(new Run(after));
        }
        else
        {
            panel.Text = sourceText;
        }

        return panel;
    }

    private static string Short(string id) => id.Length <= 8 ? id : id[..8];

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
