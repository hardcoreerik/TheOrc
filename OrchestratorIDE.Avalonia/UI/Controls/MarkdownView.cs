// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Renders a markdown string as native Avalonia controls, dark-theme optimised.
/// Supports: headings, code blocks, blockquotes, bullet/numbered lists, bold,
/// italic, inline code, links (rendered as coloured underlined text), and images
/// (![alt](src) — block-level when alone on a line, inline when embedded in text;
/// loads http(s) URLs, data: URIs, and local file paths, with a graceful
/// broken-image fallback).
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

            // Standalone image line — ![alt](src) alone on its own line gets a real
            // block-level Image control (proper sizing/alignment), rather than being
            // crammed inline inside a paragraph's text run.
            var blockImg = BlockImageRx.Match(line);
            if (blockImg.Success)
            {
                yield return BuildImageControl(blockImg.Groups["alt"].Value, blockImg.Groups["src"].Value, maxDim: 480);
                i++;
                continue;
            }

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
                && !BlockImageRx.IsMatch(lines[i])
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
        var tb = new SelectableTextBlock
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
            Child        = new SelectableTextBlock
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

    private static SelectableTextBlock Paragraph(string text, Action<string>? onLinkClicked)
        => InlineTextBlock(text, onLinkClicked);

    private static SelectableTextBlock InlineTextBlock(string text, Action<string>? onLinkClicked)
    {
        var tb = new SelectableTextBlock
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

    // Image-only line:  optional leading/trailing whitespace around a single ![alt](src).
    private static readonly Regex BlockImageRx =
        new(@"^\s*!\[(?<alt>[^\]\n]*)\]\((?<src>[^)\n]+)\)\s*$", RegexOptions.Compiled);

    private static readonly Regex InlineRx = new(
        @"!\[(?<it>[^\]\n]*)\]\((?<iu>[^)\n]+)\)"  // ![alt](src) image — MUST precede the link
                                                    // alternative below, else the leading '!' is
                                                    // orphaned and the rest renders as a link.
        + @"|\*\*(?<b>[^*\n]+)\*\*"       // **bold**
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

            if (m.Groups["it"].Success || m.Groups["iu"].Success)
            {
                // Inline image embedded in a text run — smaller cap than a block image so it
                // sits naturally on the line. InlineUIContainer hosts the same Image control.
                yield return new InlineUIContainer(
                    BuildImageControl(m.Groups["it"].Value, m.Groups["iu"].Value, maxDim: 220));
            }
            else if (m.Groups["b"].Success || m.Groups["b2"].Success)
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

    // ── Image rendering ─────────────────────────────────────────────────────────

    // One shared client for all markdown image fetches -- creating a new HttpClient per
    // image is the classic socket-exhaustion antipattern. 20s timeout so a slow/hung host
    // can't keep a placeholder spinning forever (it'll just fall back to the broken-image
    // text instead).
    private static readonly HttpClient ImageHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Builds a host Border containing an Image, and kicks off an async load of <paramref
    /// name="src"/> into it. The Border (not the Image) is returned so a load failure can
    /// swap in a text placeholder without the caller holding two references. Per the
    /// project-wide UX rule, the host carries a tooltip (alt text / source) and a right-click
    /// "Copy image link" action. Never throws -- a bad URL/decode just shows the placeholder.
    /// </summary>
    private static Control BuildImageControl(string alt, string src, double maxDim)
    {
        var img = new Image
        {
            MaxWidth            = maxDim,
            MaxHeight           = maxDim,
            Stretch             = Stretch.Uniform,
            // DownOnly so a small icon isn't blown up to maxDim; large images still shrink.
            StretchDirection    = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var host = new Border
        {
            Margin              = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child               = img,
            ContextMenu         = BuildImageContextMenu(src),
        };
        ToolTip.SetTip(host, string.IsNullOrWhiteSpace(alt) ? src : $"{alt}\n{src}");

        _ = LoadImageIntoAsync(host, img, alt, src);
        return host;
    }

    private static ContextMenu BuildImageContextMenu(string src)
    {
        var copy = new MenuItem { Header = "Copy image link" };
        copy.Click += async (s, _) =>
        {
            // async-void-shaped Click handler -- clipboard access can fail (no backend, another
            // process holding it); swallow so it can't surface as an unhandled UI-thread
            // exception. TopLevel is resolved from the menu item so it works wherever the
            // markdown is hosted.
            try
            {
                var top = s is Visual v ? TopLevel.GetTopLevel(v) : null;
                if (top?.Clipboard is { } clip) await clip.SetTextAsync(src);
            }
            catch { /* non-fatal convenience action */ }
        };
        return new ContextMenu { ItemsSource = new[] { copy } };
    }

    private static async Task LoadImageIntoAsync(Border host, Image img, string alt, string src)
    {
        try
        {
            var bmp = await DecodeImageAsync(src);
            await Dispatcher.UIThread.InvokeAsync(() => img.Source = bmp);
        }
        catch
        {
            // Any failure (network, missing file, bad base64, undecodable bytes) degrades to a
            // visible, muted placeholder rather than a blank gap or a thrown exception.
            await Dispatcher.UIThread.InvokeAsync(() => host.Child = BrokenImagePlaceholder(alt, src));
        }
    }

    private static async Task<Bitmap> DecodeImageAsync(string src)
    {
        // Bitmap decode (and base64/file reads) are CPU/IO-bound and run on a background
        // thread via Task.Run -- the load is kicked off from the UI thread during Build(), so
        // decoding inline here would block rendering on large images (exactly what the vision
        // ingestion path will feed in). Only Source assignment goes back to the UI thread
        // (see LoadImageIntoAsync).
        if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            // data:[<mediatype>][;base64],<data> -- only the base64 form is supported.
            var comma = src.IndexOf(',');
            if (comma < 0) throw new FormatException("malformed data: URI");
            var payload = Regex.Replace(src[(comma + 1)..], @"\s", "");
            return await Task.Run(() => DecodeBytes(Convert.FromBase64String(payload)));
        }

        if (src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
         || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            var bytes = await ImageHttp.GetByteArrayAsync(src);
            return await Task.Run(() => DecodeBytes(bytes));
        }

        // Otherwise treat it as a local path (handles both a bare path and a file:// URI).
        var path = src;
        if (src.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
            && Uri.TryCreate(src, UriKind.Absolute, out var fileUri))
            path = fileUri.LocalPath;

        return await Task.Run(() =>
        {
            using var fs = File.OpenRead(path);
            return new Bitmap(fs);
        });
    }

    private static Bitmap DecodeBytes(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return new Bitmap(ms);
    }

    private static Control BrokenImagePlaceholder(string alt, string src)
        => new SelectableTextBlock
        {
            Text         = $"🖼  {(string.IsNullOrWhiteSpace(alt) ? src : alt)}  (image unavailable)",
            Foreground   = Palette.Muted,
            FontSize     = 12,
            FontStyle    = FontStyle.Italic,
            TextWrapping = TextWrapping.Wrap,
        };
}
