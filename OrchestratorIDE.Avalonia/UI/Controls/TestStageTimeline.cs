// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Horizontal stage pipeline for the visual test runner: one node per <see cref="TestRunStage"/>
/// connected by progress lines. Status is never colour-only — every node carries a glyph
/// (✓ completed, ! warning, ✕ failed, ▶ active, – skipped/cancelled, ○ queued) and, when space
/// allows, its label and detail underneath. When stages are too dense for labels (e.g. one stage
/// per benched model across 30 models) only the ACTIVE stage's label is drawn, centred below the
/// strip, and nodes compress to ticks. Immediate-mode like HiveConstellationView; the only
/// animation is a soft pulse on the active node, disabled by <see cref="LiteMode"/>.
/// </summary>
public sealed class TestStageTimeline : Control
{
    private IReadOnlyList<TestRunStage> _stages = [];
    private readonly DispatcherTimer _anim;
    private double _pulse;
    private bool _attached;

    public TestStageTimeline()
    {
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _anim.Tick += (_, _) => { _pulse += 0.18; InvalidateVisual(); };
        AttachedToVisualTree   += (_, _) => { _attached = true;  SyncTimer(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; SyncTimer(); };
    }

    private bool _liteMode;
    public bool LiteMode
    {
        get => _liteMode;
        set { _liteMode = value; SyncTimer(); InvalidateVisual(); }
    }

    /// <summary>Replaces the stage list. The same list instance may be pushed repeatedly (the
    /// telemetry model mutates stages in place); every push just triggers a repaint.</summary>
    public void SetStages(IReadOnlyList<TestRunStage> stages)
    {
        _stages = stages ?? [];
        SyncTimer();
        InvalidateVisual();
    }

    private bool HasActiveStage()
    {
        foreach (var s in _stages)
            if (s.Status == TestStageStatus.Active) return true;
        return false;
    }

    private void SyncTimer()
    {
        var animate = !_liteMode && _attached && HasActiveStage();
        if (animate && !_anim.IsEnabled) _anim.Start();
        else if (!animate && _anim.IsEnabled) _anim.Stop();
    }

    // ── Palette (TheOrc brand) ────────────────────────────────────────────────
    private static readonly Color Accent   = Color.FromRgb(0x76, 0xB9, 0x00);
    private static readonly Color Done     = Color.FromRgb(0x4A, 0x7A, 0x20);
    private static readonly Color WarnCol  = Color.FromRgb(0xCC, 0xA7, 0x00);
    private static readonly Color ErrCol   = Color.FromRgb(0xF4, 0x47, 0x47);
    private static readonly Color Queued   = Color.FromRgb(0x33, 0x47, 0x3B);
    private static readonly Color TextDim  = Color.FromRgb(0x7A, 0x8A, 0x6A);
    private static readonly Color TextMain = Color.FromRgb(0xD4, 0xD4, 0xD4);

    private static (Color color, string glyph) StyleFor(TestStageStatus s) => s switch
    {
        TestStageStatus.Active    => (Accent,  "▶"),
        TestStageStatus.Completed => (Done,    "✓"),
        TestStageStatus.Warning   => (WarnCol, "!"),
        TestStageStatus.Failed    => (ErrCol,  "✕"),
        TestStageStatus.Skipped   => (Queued,  "–"),
        TestStageStatus.Cancelled => (Queued,  "–"),
        _                         => (Queued,  "○"),
    };

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 60 || h < 30 || _stages.Count == 0) return;

        const double margin = 18;
        double usable = w - margin * 2;
        double step = _stages.Count > 1 ? usable / (_stages.Count - 1) : 0;
        bool dense = step < 78 && _stages.Count > 1;
        double nodeY = dense ? h * 0.42 : Math.Min(h * 0.38, 22);
        double r = dense ? 4 : 7;

        // Connectors first (under the nodes): segment i joins node i and i+1, coloured by the
        // furthest-completed side so completed ground is visibly "kept".
        for (int i = 0; i + 1 < _stages.Count; i++)
        {
            double x1 = margin + step * i, x2 = margin + step * (i + 1);
            bool litLeft = _stages[i].Status is TestStageStatus.Completed or TestStageStatus.Warning
                or TestStageStatus.Failed or TestStageStatus.Active;
            var pen = new Pen(new SolidColorBrush(litLeft ? Done : Queued, litLeft ? 0.8 : 0.5), 1.4);
            ctx.DrawLine(pen, new Point(x1 + r + 2, nodeY), new Point(x2 - r - 2, nodeY));
        }

        TestRunStage? active = null;
        double activeX = 0;
        for (int i = 0; i < _stages.Count; i++)
        {
            var stage = _stages[i];
            double x = _stages.Count == 1 ? w / 2 : margin + step * i;
            var (color, glyph) = StyleFor(stage.Status);

            if (stage.Status == TestStageStatus.Active)
            {
                active = stage;
                activeX = x;
                double pulseR = r + 3 + (_liteMode ? 0 : 1.6 * (1 + Math.Sin(_pulse)));
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Accent, 0.35), 1.4), new Point(x, nodeY), pulseR, pulseR);
            }

            var fill = stage.Status == TestStageStatus.Queued
                ? null
                : new SolidColorBrush(color, stage.Status == TestStageStatus.Active ? 1.0 : 0.85);
            ctx.DrawEllipse(fill, new Pen(new SolidColorBrush(color), 1.4), new Point(x, nodeY), r, r);

            if (!dense)
            {
                DrawCentered(ctx, glyph, x, nodeY - 7.5, 10,
                    stage.Status == TestStageStatus.Queued ? TextDim : Colors.White);
                var labelCol = stage.Status switch
                {
                    TestStageStatus.Active => TextMain,
                    TestStageStatus.Failed => ErrCol,
                    _                      => TextDim,
                };
                DrawCentered(ctx, Ellipsize(stage.Label, step > 0 ? step : w, 10), x, nodeY + r + 5, 10, labelCol);
                if (stage.Detail.Length > 0)
                    DrawCentered(ctx, stage.Detail, x, nodeY + r + 19, 9, TextDim);
            }
        }

        // Dense mode: one shared caption line for the active stage.
        if (dense && active is not null)
        {
            var caption = active.Detail.Length > 0 ? $"{active.Label} — {active.Detail}" : active.Label;
            DrawCentered(ctx, Ellipsize(caption, w - 20, 11), Math.Clamp(activeX, w * 0.2, w * 0.8), nodeY + r + 8, 11, TextMain);
        }
    }

    private static string Ellipsize(string text, double widthPx, double fontSize)
    {
        // Cheap width heuristic (~0.62em per char for the default font) — good enough for labels.
        int maxChars = Math.Max(4, (int)(widthPx / (fontSize * 0.62)));
        return text.Length <= maxChars ? text : text[..(maxChars - 1)] + "…";
    }

    private static readonly Typeface Face = new("Consolas");

    private static void DrawCentered(DrawingContext ctx, string text, double cx, double top, double size, Color color)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Face, size, new SolidColorBrush(color));
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, top));
    }
}
