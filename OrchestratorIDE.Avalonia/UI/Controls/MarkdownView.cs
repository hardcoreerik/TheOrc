// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Renders a markdown string as native Avalonia controls, dark-theme optimised.
/// Supports: headings, code blocks, blockquotes, bullet/numbered lists, bold,
/// italic, inline code, and links (rendered as coloured underlined text).
/// </summary>
public sealed class MarkdownView : ContentControl
{
    public static readonly StyledProperty<string> TextProperty =
        AvaloniaProperty.Register<MarkdownView, string>(nameof(Text), "");

    public string Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Action<string>? LinkClicked { get; set; }

    static MarkdownView()
    {
        TextProperty.Changed.AddClassHandler<MarkdownView>((v, _) => v.Rebuild());
        // Re-render when the control becomes visible (deferred until UseMarkdown=true).
        Visual.IsVisibleProperty.Changed.AddClassHandler<MarkdownView>((v, e) =>
        {
            if (e.NewValue is true) v.Rebuild();
        });
    }

    public MarkdownView() { }

    private void Rebuild()
    {
        // Guard: do nothing while hidden.  Content is set once when IsVisible flips to true.
        if (!IsVisible) return;
        Content = MarkdownBuilder.Build(GetValue(TextProperty) ?? "", LinkClicked);
    }
}

// ── Dark-theme palette ────────────────────────────────────────────────────────

file static class Palette
{
    internal static readonly IBrush Fg      = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4));
    internal static readonly IBrush Muted   = new SolidColorBrush(Color.FromRgb(0x7A, 0x8A, 0x6A));
    internal static readonly IBrush Accent  = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
    internal static readonly IBrush Code    = new SolidColorBrush(Color.FromRgb(0xCE, 0x91, 0x78));
    internal static readonly IBrush BgCode  = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    internal static readonly IBrush BgQuote = new SolidColorBrush(Color.FromRgb(0x1A, 0x24, 0x1A));
    internal static readonly IBrush Link    = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    internal static readonly IBrush H1      = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
    internal static readonly IBrush H2      = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80));
    internal static readonly IBrush H3      = new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x60));
}

// ── Block parser + renderer ───────────────────────────────────────────────────

file static class MarkdownBuilder
{
    private static readonly FontFamily Mono = new("Consolas,Menlo,monospace");

    public static Control Build(string md, Action<string>? onLinkClicked = null)
    {
        var panel = new StackPanel { Spacing = 5 };
        if (string.IsNullOrEmpty(md)) return panel;
        foreach (var block in ParseBlocks(md.Replace("\r\n", "\n"), onLinkClicked))
            panel.Children.Add(block);
        return panel;
    }

    // ── Block parser ──────────────────────────────────────────────────────────

    private static IEnumerable<Control> ParseBlocks(string md, Action<string>? onLinkClicked)
    {
        var lines = md.Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block
            if (line.TrimStart().StartsWith("```"))
            {
                i++;
                var sb = new StringBuilder();
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                    sb.AppendLine(lines[i++]);
                i++; // skip closing fence
                yield return CodeBlock(sb.ToString().TrimEnd('\n', '\r'));
                continue;
            }

            // Headings
            if      (line.StartsWith("### ")) { yield return Heading(line[4..], 3, onLinkClicked); i++; continue; }
            else if (line.StartsWith("## "))  { yield return Heading(line[3..], 2, onLinkClicked); i++; continue; }
            else if (line.StartsWith("# "))   { yield return Heading(line[2..], 1, onLinkClicked); i++; continue; }

            // Horizontal rule
            var trimmed = line.Trim();
            if (trimmed is "---" or "***" or "___" or "===") { yield return HRule(); i++; continue; }

            // Blockquote — consecutive "> " lines
            if (line.StartsWith("> "))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].StartsWith("> "))
                    sb.AppendLine(lines[i++][2..]);
                yield return Blockquote(sb.ToString().TrimEnd(), onLinkClicked);
                continue;
            }

            // Bullet list — consecutive "- " or "* " lines
            if (line.Length > 2 && (line.StartsWith("- ") || line.StartsWith("* ")))
            {
                var items = new List<string>();
                while (i < lines.Length && lines[i].Length > 2
                    && (lines[i].StartsWith("- ") || lines[i].StartsWith("* ")))
                    items.Add(lines[i++][2..]);
                yield return BulletList(items, onLinkClicked);
                continue;
            }

            // Numbered list — consecutive "N. " lines
            if (NumberedRx.IsMatch(line))
            {
                var items = new List<string>();
                while (i < lines.Length && NumberedRx.IsMatch(lines[i]))
                    items.Add(NumberedRx.Replace(lines[i++], ""));
                yield return NumberedList(items, onLinkClicked);
                continue;
            }

            // Empty line
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Paragraph — gather consecutive non-special lines
            var para = new StringBuilder();
            while (i < lines.Length
                && !string.IsNullOrWhiteSpace(lines[i])
                && !lines[i].TrimStart().StartsWith("```")
                && !lines[i].StartsWith("# ")  && !lines[i].StartsWith("## ") && !lines[i].StartsWith("### ")
                && !lines[i].StartsWith("> ")
                && !(lines[i].Length > 2 && (lines[i].StartsWith("- ") || lines[i].StartsWith("* ")))
                && !NumberedRx.IsMatch(lines[i])
                && lines[i].Trim() is not ("---" or "***" or "___" or "==="))
            {
                if (para.Length > 0) para.Append(' ');
                para.Append(lines[i].Trim());
                i++;
            }
            yield return Paragraph(para.ToString(), onLinkClicked);
        }
    }

    // ── Block renderers ───────────────────────────────────────────────────────

    private static Control Heading(string text, int level, Action<string>? onLinkClicked)
    {
        var (fg, size, weight) = level switch
        {
            1 => (Palette.H1, 17.0, FontWeight.Bold),
            2 => (Palette.H2, 14.0, FontWeight.SemiBold),
            _ => (Palette.H3, 13.0, FontWeight.SemiBold),
        };
        var tb = new TextBlock
        {
            Foreground   = fg,
            FontSize     = size,
            FontWeight   = weight,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, level == 1 ? 6 : 3, 0, 2),
        };
        foreach (var inline in ParseInlines(text, onLinkClicked)) tb.Inlines!.Add(inline);

        if (level > 1) return tb;

        return new StackPanel
        {
            Children =
            {
                tb,
                new Border
                {
                    Height     = 1,
                    Background = Palette.Accent,
                    Opacity    = 0.35,
                    Margin     = new Thickness(0, 2, 0, 0),
                },
            }
        };
    }

    private static Control CodeBlock(string code)
        => new Border
        {
            Background   = Palette.BgCode,
            CornerRadius = new CornerRadius(4),
            Padding      = new Thickness(12, 8),
            Margin       = new Thickness(0, 2, 0, 2),
            Child        = new TextBlock
            {
                Text         = code,
                FontFamily   = Mono,
                FontSize     = 12,
                Foreground   = Palette.Code,
                TextWrapping = TextWrapping.Wrap,
            },
        };

    private static Control Blockquote(string text, Action<string>? onLinkClicked)
        => new Border
        {
            Background      = Palette.BgQuote,
            BorderBrush     = Palette.Accent,
            BorderThickness = new Thickness(3, 0, 0, 0),
            Padding         = new Thickness(10, 6),
            Margin          = new Thickness(0, 2, 0, 2),
            Child           = Paragraph(text, onLinkClicked),
        };

    private static Control HRule()
        => new Border
        {
            Height     = 1,
            Background = Palette.Muted,
            Opacity    = 0.4,
            Margin     = new Thickness(0, 6, 0, 6),
        };

    private static Control BulletList(IReadOnlyList<string> items, Action<string>? onLinkClicked)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(8, 2, 0, 2) };
        foreach (var item in items)
            panel.Children.Add(ListRow("•", item, bullet: true, onLinkClicked));
        return panel;
    }

    private static Control NumberedList(IReadOnlyList<string> items, Action<string>? onLinkClicked)
    {
        var panel = new StackPanel { Spacing = 3, Margin = new Thickness(8, 2, 0, 2) };
        for (int n = 0; n < items.Count; n++)
            panel.Children.Add(ListRow($"{n + 1}.", items[n], bullet: false, onLinkClicked));
        return panel;
    }

    private static Control ListRow(string marker, string text, bool bullet, Action<string>? onLinkClicked)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition(bullet ? 18 : 26, GridUnitType.Pixel));
        grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

        var markerTb = new TextBlock
        {
            Text                = marker,
            Foreground          = Palette.Accent,
            FontSize            = 12,
            VerticalAlignment   = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 0, 4, 0),
        };
        var content = InlineTextBlock(text, onLinkClicked);
        Grid.SetColumn(markerTb, 0);
        Grid.SetColumn(content,  1);
        grid.Children.Add(markerTb);
        grid.Children.Add(content);
        return grid;
    }

    private static TextBlock Paragraph(string text, Action<string>? onLinkClicked)
        => InlineTextBlock(text, onLinkClicked);

    private static TextBlock InlineTextBlock(string text, Action<string>? onLinkClicked)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize     = 13,
            Foreground   = Palette.Fg,
        };
        foreach (var inline in ParseInlines(text, onLinkClicked))
            tb.Inlines!.Add(inline);
        return tb;
    }

    // ── Inline parser ─────────────────────────────────────────────────────────

    private static readonly Regex NumberedRx =
        new(@"^\d+\.\s", RegexOptions.Compiled);

    private static readonly Regex InlineRx = new(
        @"\*\*(?<b>[^*\n]+)\*\*"         // **bold**
        + @"|__(?<b2>[^_\n]+)__"          // __bold__
        + @"|\*(?<i>[^*\n]+)\*"           // *italic*
        + @"|_(?<i2>[^_\n]+)_"            // _italic_
        + @"|`(?<c>[^`\n]+)`"             // `code`
        + @"|\[(?<lt>[^\]\n]+)\]\((?<lu>[^)\n]+)\)"  // [text](url)
        + @"|(?<url>https?://\S+)",        // bare URL
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static IEnumerable<Inline> ParseInlines(string text, Action<string>? onLinkClicked)
    {
        int pos = 0;
        foreach (Match m in InlineRx.Matches(text))
        {
            if (m.Index > pos)
                yield return new Run { Text = text[pos..m.Index] };

            if (m.Groups["b"].Success || m.Groups["b2"].Success)
            {
                var inner = m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["b2"].Value;
                yield return new Run { Text = inner, FontWeight = FontWeight.Bold };
            }
            else if (m.Groups["i"].Success || m.Groups["i2"].Success)
            {
                var inner = m.Groups["i"].Success ? m.Groups["i"].Value : m.Groups["i2"].Value;
                yield return new Run { Text = inner, FontStyle = FontStyle.Italic };
            }
            else if (m.Groups["c"].Success)
            {
                yield return new Run
                {
                    Text       = m.Groups["c"].Value,
                    FontFamily = Mono,
                    FontSize   = 12,
                    Foreground = Palette.Code,
                    Background = Palette.BgCode,
                };
            }
            else if (m.Groups["lt"].Success)
            {
                yield return MakeLinkInline(m.Groups["lt"].Value, m.Groups["lu"].Value, onLinkClicked);
            }
            else if (m.Groups["url"].Success)
            {
                yield return MakeLinkInline(m.Value, m.Value, onLinkClicked);
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
            yield return new Run { Text = text[pos..] };
    }

    private static Inline MakeLinkInline(string text, string url, Action<string>? onLinkClicked)
    {
        var link = new HyperlinkButton
        {
            Content = text,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            Foreground = Palette.Link,
            Background = Brushes.Transparent,
        };

        if (onLinkClicked is null)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                link.NavigateUri = uri;
        }
        else
        {
            link.Click += (_, _) => onLinkClicked(url);
        }

        return new InlineUIContainer(link);
    }
}
