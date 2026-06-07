using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace OrchestratorIDE.Research;

/// <summary>
/// Converts a markdown string to a WPF <see cref="FlowDocument"/>.
///
/// Supported elements:
///   Block: # H1–H6, - and * bullets, 1. numbered lists, ``` code blocks,
///          > blockquotes, --- horizontal rules, paragraphs
///   Inline: **bold**, *italic*, ***bold-italic***, `code`, [text](url),
///           bare https:// URLs
///
/// URLs become clickable Hyperlinks that open the default browser.
/// </summary>
public static class MarkdownFlowDocument
{
    // ── Colours (dark-theme palette) ──────────────────────────────────────────
    private static readonly Color BgApp       = Color.FromRgb(0x16, 0x16, 0x16);
    private static readonly Color FgText      = Color.FromRgb(0xD4, 0xD4, 0xD4);
    private static readonly Color FgMuted     = Color.FromRgb(0x7A, 0x8A, 0x6A);
    private static readonly Color FgAccent    = Color.FromRgb(0x76, 0xB9, 0x00);
    private static readonly Color FgLink      = Color.FromRgb(0x4E, 0xC9, 0xB0);
    private static readonly Color FgCode      = Color.FromRgb(0xCE, 0x91, 0x78);
    private static readonly Color BgCode      = Color.FromRgb(0x1E, 0x1E, 0x1E);
    private static readonly Color BgQuote     = Color.FromRgb(0x1A, 0x24, 0x1A);
    private static readonly Color FgH1        = Color.FromRgb(0x76, 0xB9, 0x00);
    private static readonly Color FgH2        = Color.FromRgb(0xA8, 0xCC, 0x80);
    private static readonly Color FgH3        = Color.FromRgb(0x88, 0xAA, 0x60);

    // ── Public entry point ────────────────────────────────────────────────────

    public static FlowDocument Parse(string markdown)
    {
        var doc = new FlowDocument
        {
            Background  = new SolidColorBrush(Colors.Transparent),
            Foreground  = new SolidColorBrush(FgText),
            FontFamily  = new FontFamily("Segoe UI, Arial"),
            FontSize    = 13,
            PagePadding = new Thickness(0),
        };

        if (string.IsNullOrWhiteSpace(markdown))
            return doc;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        ParseBlocks(doc, lines);
        return doc;
    }

    // ── Block-level parser ────────────────────────────────────────────────────

    private static void ParseBlocks(FlowDocument doc, string[] lines)
    {
        bool inCode    = false;
        string codeLang = "";
        var   codeLines = new List<string>();

        // Current bullet list accumulator
        List? currentList    = null;
        bool  currentIsNum   = false;

        void FlushList()
        {
            if (currentList != null)
            {
                doc.Blocks.Add(currentList);
                currentList  = null;
                currentIsNum = false;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // ── Code block ────────────────────────────────────────────────
            if (line.StartsWith("```"))
            {
                if (!inCode)
                {
                    FlushList();
                    inCode   = true;
                    codeLang = line[3..].Trim();
                    codeLines.Clear();
                }
                else
                {
                    inCode = false;
                    doc.Blocks.Add(MakeCodeBlock(codeLines, codeLang));
                    codeLines.Clear();
                }
                continue;
            }
            if (inCode) { codeLines.Add(line); continue; }

            // ── Horizontal rule ───────────────────────────────────────────
            if (Regex.IsMatch(line, @"^(---+|\*\*\*+|___+)\s*$"))
            {
                FlushList();
                doc.Blocks.Add(MakeRule());
                continue;
            }

            // ── ATX Header ────────────────────────────────────────────────
            var headerMatch = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (headerMatch.Success)
            {
                FlushList();
                doc.Blocks.Add(MakeHeader(
                    headerMatch.Groups[1].Length,
                    headerMatch.Groups[2].Value));
                continue;
            }

            // ── Blockquote ────────────────────────────────────────────────
            if (line.StartsWith("> ") || line == ">")
            {
                FlushList();
                var gtIdx = line.IndexOf('>');
                doc.Blocks.Add(MakeBlockquote(line[(gtIdx + 1)..].TrimStart()));
                continue;
            }

            // ── Bullet list item ──────────────────────────────────────────
            var bulletMatch = Regex.Match(line, @"^(\s*)([-*+])\s+(.+)$");
            if (bulletMatch.Success)
            {
                if (currentList == null || currentIsNum)
                {
                    FlushList();
                    currentList  = new List { MarkerStyle = TextMarkerStyle.Disc };
                    currentList.Margin   = new Thickness(0, 2, 0, 2);
                    currentIsNum = false;
                }
                currentList.ListItems.Add(MakeListItem(bulletMatch.Groups[3].Value));
                continue;
            }

            // ── Numbered list item ────────────────────────────────────────
            var numMatch = Regex.Match(line, @"^\s*(\d+)\.\s+(.+)$");
            if (numMatch.Success)
            {
                if (currentList == null || !currentIsNum)
                {
                    FlushList();
                    currentList  = new List { MarkerStyle = TextMarkerStyle.Decimal };
                    currentList.Margin   = new Thickness(0, 2, 0, 2);
                    currentIsNum = true;
                }
                currentList.ListItems.Add(MakeListItem(numMatch.Groups[2].Value));
                continue;
            }

            // ── Blank line — flush current list ──────────────────────────
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushList();
                continue;
            }

            // ── Normal paragraph ──────────────────────────────────────────
            FlushList();
            var para = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
            foreach (var inline in ParseInline(line))
                para.Inlines.Add(inline);
            doc.Blocks.Add(para);
        }

        // Flush any open list or code block
        FlushList();
        if (inCode && codeLines.Count > 0)
            doc.Blocks.Add(MakeCodeBlock(codeLines, codeLang));
    }

    // ── Block factories ───────────────────────────────────────────────────────

    private static Paragraph MakeHeader(int level, string text)
    {
        var (size, fg) = level switch
        {
            1 => (20.0, FgH1),
            2 => (17.0, FgH2),
            _ => (14.0, FgH3),
        };
        var para = new Paragraph
        {
            Margin     = new Thickness(0, level <= 2 ? 12 : 6, 0, 4),
            FontSize   = size,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(fg),
        };
        foreach (var inline in ParseInline(text))
            para.Inlines.Add(inline);
        return para;
    }

    private static Section MakeCodeBlock(List<string> lines, string lang)
    {
        var text = string.Join("\n", lines);
        var sec  = new Section
        {
            Background = new SolidColorBrush(BgCode),
            Margin     = new Thickness(0, 4, 0, 8),
        };
        var para = new Paragraph
        {
            FontFamily  = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize    = 12,
            Foreground  = new SolidColorBrush(FgCode),
            Padding     = new Thickness(10, 8, 10, 8),
            Margin      = new Thickness(0),
        };
        para.Inlines.Add(new Run(text));
        sec.Blocks.Add(para);
        return sec;
    }

    private static Section MakeBlockquote(string text)
    {
        var sec = new Section
        {
            Background  = new SolidColorBrush(BgQuote),
            BorderBrush = new SolidColorBrush(FgMuted),
            BorderThickness = new Thickness(3, 0, 0, 0),
            Margin      = new Thickness(0, 4, 0, 4),
            Padding     = new Thickness(10, 4, 10, 4),
        };
        var para = new Paragraph
        {
            Foreground = new SolidColorBrush(FgMuted),
            FontStyle  = FontStyles.Italic,
            Margin     = new Thickness(0),
        };
        foreach (var inline in ParseInline(text))
            para.Inlines.Add(inline);
        sec.Blocks.Add(para);
        return sec;
    }

    private static BlockUIContainer MakeRule()
    {
        var border = new System.Windows.Controls.Border
        {
            Height          = 1,
            Background      = new SolidColorBrush(Color.FromRgb(0x33, 0x44, 0x33)),
            Margin          = new Thickness(0, 8, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        return new BlockUIContainer(border);
    }

    private static ListItem MakeListItem(string text)
    {
        var para = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
        foreach (var inline in ParseInline(text))
            para.Inlines.Add(inline);
        return new ListItem(para);
    }

    // ── Inline parser ─────────────────────────────────────────────────────────

    private static readonly Regex _inlineRx = new(
        @"(\*\*\*(.+?)\*\*\*)" +          // Group 1/2: ***bold-italic***
        @"|(\*\*(.+?)\*\*)" +              // Group 3/4: **bold**
        @"|(__(.+?)__)" +                  // Group 5/6: __bold__
        @"|(\*(.+?)\*)" +                  // Group 7/8: *italic*
        @"|(_(.+?)_)" +                    // Group 9/10: _italic_
        @"|(``(.+?)``)" +                  // Group 11/12: ``code``
        @"|(`(.+?)`)" +                    // Group 13/14: `code`
        @"|(\[([^\]]+)\]\(([^\)]+)\))" +   // Group 15/16/17: [text](url)
        @"|(https?://[^\s\)\]""<>]+)",     // Group 18: bare URL
        RegexOptions.Singleline);

    public static IEnumerable<Inline> ParseInline(string text)
    {
        int pos = 0;
        foreach (Match m in _inlineRx.Matches(text))
        {
            // Emit any plain text before this match
            if (m.Index > pos)
                yield return new Run(text[pos..m.Index]);
            pos = m.Index + m.Length;

            // ***bold-italic***
            if (m.Groups[1].Success)
            {
                var b = new Bold(); var it = new Italic();
                it.Inlines.Add(new Run(m.Groups[2].Value));
                b.Inlines.Add(it);
                yield return b;
            }
            // **bold** or __bold__
            else if (m.Groups[3].Success || m.Groups[5].Success)
            {
                var inner = m.Groups[3].Success ? m.Groups[4].Value : m.Groups[6].Value;
                var b = new Bold();
                b.Inlines.Add(new Run(inner));
                yield return b;
            }
            // *italic* or _italic_
            else if (m.Groups[7].Success || m.Groups[9].Success)
            {
                var inner = m.Groups[7].Success ? m.Groups[8].Value : m.Groups[10].Value;
                var it = new Italic();
                it.Inlines.Add(new Run(inner));
                yield return it;
            }
            // ``code`` or `code`
            else if (m.Groups[11].Success || m.Groups[13].Success)
            {
                var inner = m.Groups[11].Success ? m.Groups[12].Value : m.Groups[14].Value;
                yield return MakeCodeSpan(inner);
            }
            // [text](url)
            else if (m.Groups[15].Success)
            {
                yield return MakeHyperlink(m.Groups[16].Value, m.Groups[17].Value);
            }
            // bare URL
            else if (m.Groups[18].Success)
            {
                var url = m.Groups[18].Value.TrimEnd('.', ',', ')');
                yield return MakeHyperlink(url, url);
                // If we trimmed trailing chars, put them back
                if (url.Length < m.Groups[18].Value.Length)
                    yield return new Run(m.Groups[18].Value[url.Length..]);
            }
        }

        // Remaining plain text after last match
        if (pos < text.Length)
            yield return new Run(text[pos..]);
    }

    private static Inline MakeCodeSpan(string code)
    {
        var span = new Span
        {
            Background = new SolidColorBrush(BgCode),
            Foreground = new SolidColorBrush(FgCode),
        };
        span.FontFamily = new FontFamily("Cascadia Code, Consolas, monospace");
        span.FontSize   = 12;
        span.Inlines.Add(new Run($" {code} ")); // thin non-breaking-space padding
        return span;
    }

    private static Hyperlink MakeHyperlink(string text, string url)
    {
        var hl = new Hyperlink(new Run(text))
        {
            Foreground          = new SolidColorBrush(FgLink),
            TextDecorations     = TextDecorations.Underline,
            NavigateUri         = TryMakeUri(url),
        };
        // Open in default browser when clicked
        hl.RequestNavigate += OnHyperlinkNavigate;
        return hl;
    }

    private static void OnHyperlinkNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
        }
        catch { /* ignore — browser may not be available */ }
        e.Handled = true;
    }

    private static Uri? TryMakeUri(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri;
        if (Uri.TryCreate("https://" + url, UriKind.Absolute, out var uri2)) return uri2;
        return null;
    }
}
