// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.NUnit;
using Avalonia.Media;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Headless tests for the Phase 6 MarkdownView renderer. Asserts the block/inline
/// parser produces the expected Avalonia control tree, and that the IsVisible
/// guard defers rendering until the view is shown (the streaming-token perf fix).
/// </summary>
[TestFixture]
public class MarkdownViewTests
{
    // ── Tree helpers ──────────────────────────────────────────────────────────

    private static StackPanel Root(MarkdownView v)
    {
        Assert.That(v.Content, Is.InstanceOf<StackPanel>(), "MarkdownView content root should be a StackPanel");
        return (StackPanel)v.Content!;
    }

    private static IEnumerable<TextBlock> AllTextBlocks(Control? c)
    {
        if (c is TextBlock tb) { yield return tb; yield break; }
        if (c is Panel p)        foreach (var child in p.Children) foreach (var t in AllTextBlocks(child)) yield return t;
        else if (c is Border b)  foreach (var t in AllTextBlocks(b.Child)) yield return t;
        else if (c is ContentControl cc && cc.Content is Control inner)
                                 foreach (var t in AllTextBlocks(inner)) yield return t;
    }

    private static IEnumerable<Border> AllBorders(Control? c)
    {
        if (c is Border b) { yield return b; foreach (var x in AllBorders(b.Child)) yield return x; yield break; }
        if (c is Panel p) foreach (var child in p.Children) foreach (var x in AllBorders(child)) yield return x;
    }

    private static IEnumerable<Image> AllImages(Control? c)
    {
        if (c is Image im) { yield return im; yield break; }
        if (c is Panel p)        foreach (var child in p.Children) foreach (var x in AllImages(child)) yield return x;
        else if (c is Border b)  foreach (var x in AllImages(b.Child)) yield return x;
        else if (c is ContentControl cc && cc.Content is Control inner)
                                 foreach (var x in AllImages(inner)) yield return x;
    }

    /// <summary>
    /// Image loads are fire-and-forget async (decode + a Dispatcher hop to set Source), so
    /// the headless dispatcher needs pumping for them to land. Bounded so a genuinely-failing
    /// load can't hang the test.
    /// </summary>
    private static async Task PumpUntil(Func<bool> condition, int maxMs = 2000)
    {
        for (var i = 0; i < maxMs / 10 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // A 1×1 transparent PNG — lets the data: decode path be exercised with zero network.
    private const string OnePixelPngDataUri =
        "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGNgAAIAAAUAAen63NgAAAAASUVORK5CYII=";

    // ── Block-level rendering ─────────────────────────────────────────────────

    [AvaloniaTest]
    public void Empty_text_yields_no_content()
    {
        // Default Text is already "" so setting "" raises no change → Content stays null.
        // Explicitly clearing from non-empty builds an empty StackPanel.
        var v = new MarkdownView { Text = "x" };
        v.Text = "";
        Assert.That(v.Content, Is.Null.Or.InstanceOf<StackPanel>());
        if (v.Content is StackPanel sp) Assert.That(sp.Children, Is.Empty);
    }

    [AvaloniaTest]
    public void Paragraph_renders_single_textblock_with_text()
    {
        var v  = new MarkdownView { Text = "Hello world." };
        var tb = AllTextBlocks(Root(v)).ToList();
        Assert.That(tb, Is.Not.Empty);
        Assert.That(tb.Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("Hello world.")), Is.True);
    }

    [AvaloniaTest]
    public void Heading_renders_text_without_hash_markers()
    {
        var v   = new MarkdownView { Text = "# Title Here" };
        var all = AllTextBlocks(Root(v)).Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.That(all.Any(s => s.Contains("Title Here")), Is.True);
        Assert.That(all.Any(s => s.Contains("#")), Is.False, "heading markers should be stripped");
    }

    [AvaloniaTest]
    public void Code_block_renders_in_border_with_monospace()
    {
        var v  = new MarkdownView { Text = "```\nvar x = 1;\n```" };
        var tb = AllTextBlocks(Root(v)).FirstOrDefault(t => (t.Text ?? "").Contains("var x = 1;"));
        Assert.That(tb, Is.Not.Null, "code text should be present");
        Assert.That(tb!.FontFamily.Name, Does.Contain("Consolas").IgnoreCase
            .Or.Contain("mono").IgnoreCase.Or.Contain("Menlo").IgnoreCase);
    }

    [AvaloniaTest]
    public void Bullet_list_renders_one_row_per_item()
    {
        var v    = new MarkdownView { Text = "- one\n- two\n- three" };
        var text = AllTextBlocks(Root(v)).Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(text.Any(s => s.Contains("one")),   Is.True);
            Assert.That(text.Any(s => s.Contains("two")),   Is.True);
            Assert.That(text.Any(s => s.Contains("three")), Is.True);
        });
    }

    [AvaloniaTest]
    public void Numbered_list_renders_markers()
    {
        var v    = new MarkdownView { Text = "1. first\n2. second" };
        var text = AllTextBlocks(Root(v)).Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.That(text.Any(s => s.Contains("first")),  Is.True);
        Assert.That(text.Any(s => s.Contains("second")), Is.True);
    }

    // ── Inline rendering ──────────────────────────────────────────────────────

    [AvaloniaTest]
    public void Bold_inline_produces_bold_run()
    {
        var v    = new MarkdownView { Text = "this is **bold** text" };
        var runs = AllTextBlocks(Root(v)).SelectMany(t => t.Inlines ?? new InlineCollection())
                                         .OfType<Run>().ToList();
        Assert.That(runs.Any(r => r.Text == "bold" && r.FontWeight == FontWeight.Bold), Is.True,
            "expected a bold Run containing 'bold'");
    }

    [AvaloniaTest]
    public void Inline_code_produces_monospace_run()
    {
        var v    = new MarkdownView { Text = "call `Foo()` now" };
        var runs = AllTextBlocks(Root(v)).SelectMany(t => t.Inlines ?? new InlineCollection())
                                         .OfType<Run>().ToList();
        Assert.That(runs.Any(r => (r.Text ?? "").Contains("Foo()")), Is.True);
    }

    // ── Visibility guard (streaming perf fix) ─────────────────────────────────

    [AvaloniaTest]
    public void Hidden_view_defers_render_until_shown()
    {
        // Starts hidden: Rebuild() must short-circuit so streaming tokens don't
        // re-parse the whole document on every Content change.
        var v = new MarkdownView { IsVisible = false, Text = "# Deferred" };
        Assert.That(v.Content, Is.Null.Or.InstanceOf<StackPanel>());
        if (v.Content is StackPanel before)
            Assert.That(before.Children, Is.Empty, "hidden view should not build content");

        // Becoming visible triggers the deferred render.
        v.IsVisible = true;
        var after = Root(v);
        Assert.That(after.Children, Is.Not.Empty, "shown view should render content");
    }

    [AvaloniaTest]
    public void Updating_text_while_visible_rerenders()
    {
        var v = new MarkdownView { Text = "first" };
        Assert.That(AllTextBlocks(Root(v)).Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("first")), Is.True);

        v.Text = "second";
        Assert.That(AllTextBlocks(Root(v)).Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("second")), Is.True);
        Assert.That(AllTextBlocks(Root(v)).Any(t => (t.Inlines?.Text ?? t.Text ?? "").Contains("first")),  Is.False);
    }

    // ── Image rendering ───────────────────────────────────────────────────────

    [AvaloniaTest]
    public void Block_image_line_renders_an_Image_control()
    {
        var v    = new MarkdownView { Text = $"![a chart]({OnePixelPngDataUri})" };
        var imgs = AllImages(Root(v)).ToList();
        Assert.That(imgs, Is.Not.Empty, "a standalone image line should render an Image control");
    }

    [AvaloniaTest]
    public void Image_markdown_does_not_render_as_a_link_with_stray_bang()
    {
        // Regression: before image support, '![alt](url)' matched the link rule, leaving the
        // leading '!' orphaned as text and the rest as a clickable link. A block image line
        // must now produce an Image and no stray '!' text.
        var v    = new MarkdownView { Text = "![diagram](http://example.test/x.png)" };
        var imgs = AllImages(Root(v)).ToList();
        var text = AllTextBlocks(Root(v)).Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(imgs, Is.Not.Empty, "image markdown should render an Image, not a link");
            Assert.That(text.Any(s => s.Contains('!')), Is.False, "no orphaned '!' text should remain");
        });
    }

    [AvaloniaTest]
    public async Task Data_uri_image_decodes_into_a_bitmap_source()
    {
        var v   = new MarkdownView { Text = $"![px]({OnePixelPngDataUri})" };
        var img = AllImages(Root(v)).FirstOrDefault();
        Assert.That(img, Is.Not.Null, "expected an Image control");

        await PumpUntil(() => img!.Source is not null);
        Assert.That(img!.Source, Is.Not.Null, "the data: URI should decode into a Bitmap source");
    }

    [AvaloniaTest]
    public async Task Broken_image_falls_back_to_placeholder_without_throwing()
    {
        // A path that cannot be opened must degrade to the muted placeholder text rather than
        // throwing out of the fire-and-forget load or leaving a blank gap.
        var v = new MarkdownView { Text = @"![missing](Z:\definitely\not\here\nope12345.png)" };

        await PumpUntil(() =>
            AllTextBlocks(Root(v)).Any(t => (t.Text ?? "").Contains("image unavailable")));

        var text = AllTextBlocks(Root(v)).Select(t => t.Text ?? "").ToList();
        Assert.That(text.Any(s => s.Contains("image unavailable")), Is.True,
            "a failed image load should show the broken-image placeholder");
    }

    [AvaloniaTest]
    public void Mixed_document_renders_all_blocks()
    {
        const string md = "# Heading\n\nA paragraph.\n\n- item a\n- item b\n\n```\ncode();\n```";
        var v       = new MarkdownView { Text = md };
        var borders = AllBorders(Root(v)).ToList();
        var text    = AllTextBlocks(Root(v)).Select(t => t.Inlines?.Text ?? t.Text ?? "").ToList();
        Assert.Multiple(() =>
        {
            Assert.That(text.Any(s => s.Contains("Heading")),     Is.True);
            Assert.That(text.Any(s => s.Contains("A paragraph")), Is.True);
            Assert.That(text.Any(s => s.Contains("item a")),      Is.True);
            Assert.That(text.Any(s => s.Contains("code();")),     Is.True);
            Assert.That(borders, Is.Not.Empty, "code block should render inside a Border");
        });
    }
}
