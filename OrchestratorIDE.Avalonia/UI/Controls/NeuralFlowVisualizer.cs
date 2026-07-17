// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Immediate-mode "AI hourglass" for the visual test runner — the sibling of
/// <see cref="HiveConstellationView"/> and deliberately built the same way (single Control +
/// Render override + one ~50 ms DispatcherTimer, LiteMode freeze, concentric-ellipse glow).
///
/// Visual metaphor: queued samples are dim data particles in the top chamber; they flow through
/// a small neural network at the waist (input → core → output, with satellite nodes that light
/// up while processing); processed samples settle into the bottom chamber coloured by verdict
/// (pass green, warning amber, fail red). Everything drawn maps to REAL run state fed in via
/// <see cref="SetState"/> and <see cref="PulseSample"/> — the only decorative elements are the
/// ambient waist signals while running and the core breathing, both frozen by LiteMode.
/// It never renders or implies model chain-of-thought; the caption under the core is the
/// telemetry model's observable CurrentOperation string.
///
/// Performance: the timer only runs while a run is active AND the control is attached AND
/// LiteMode is off. Particle counts are capped (<see cref="MaxParticles"/>) with a scale factor
/// for large corpora, so 3 000 samples draw the same number of dots as 120.
/// </summary>
public sealed class NeuralFlowVisualizer : Control
{
    private const int MaxParticles = 120;
    private const double CometDurationMs = 900;

    /// <summary>Point-in-time snapshot the window pushes after each telemetry update.</summary>
    public readonly record struct FlowState(
        TestRunPhase Phase,
        int TotalSamples,
        int CompletedSamples,
        int PassedSamples,
        int WarningSamples,
        int FailedSamples,
        int ErrorSamples,
        string CurrentOperation);

    private FlowState _state = new(TestRunPhase.Idle, 0, 0, 0, 0, 0, 0, "");

    private sealed class Comet
    {
        public long StartMs;
        public Color Color;
    }
    private readonly List<Comet> _comets = [];

    private readonly DispatcherTimer _anim;
    private double _t;        // ambient phase 0..2
    private double _breath;   // core breathing phase
    private bool _attached;

    public NeuralFlowVisualizer()
    {
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _anim.Tick += (_, _) =>
        {
            _t = (_t + 1.0 / 40.0) % 2.0;
            _breath += 2.0 * Math.PI / 70.0;
            InvalidateVisual();
        };
        AttachedToVisualTree   += (_, _) => { _attached = true;  SyncTimer(); };
        DetachedFromVisualTree += (_, _) => { _attached = false; SyncTimer(); };
    }

    private bool _liteMode;
    /// <summary>Reduced motion: freezes all animation; state still renders statically.</summary>
    public bool LiteMode
    {
        get => _liteMode;
        set { _liteMode = value; SyncTimer(); InvalidateVisual(); }
    }

    /// <summary>Push the latest run state. Also resets comet history when a new run starts.
    /// The settled pile itself is derived from the state's verdict tallies at render time, so
    /// there is no per-sample history to lose or under-sample.</summary>
    public void SetState(FlowState state)
    {
        var runRestarted = state.Phase == TestRunPhase.Running && state.CompletedSamples < _state.CompletedSamples;
        if (runRestarted || (state.Phase == TestRunPhase.Running && _state.Phase is TestRunPhase.Idle
                or TestRunPhase.Completed or TestRunPhase.Failed or TestRunPhase.Cancelled))
        {
            _comets.Clear();
        }
        _state = state;
        SyncTimer();
        InvalidateVisual();
    }

    /// <summary>
    /// One sample finished: fires a comet through the network in the verdict's theme colour.
    /// (The settled pile updates from SetState tallies; this is only the travel animation.)
    /// </summary>
    public void PulseSample(TestActivityKind kind)
    {
        if (!_liteMode && _attached)
        {
            _comets.Add(new Comet { StartMs = Environment.TickCount64, Color = VerdictColor(kind) });
            if (_comets.Count > 24) _comets.RemoveRange(0, _comets.Count - 24);
        }
        InvalidateVisual();
    }

    private void SyncTimer()
    {
        var animate = !_liteMode && _attached
            && _state.Phase is TestRunPhase.Running or TestRunPhase.Paused or TestRunPhase.Cancelling;
        if (animate && !_anim.IsEnabled) _anim.Start();
        else if (!animate && _anim.IsEnabled) _anim.Stop();
    }

    // ── Palette (TheOrc brand — matches App.axaml Br.* resources) ────────────────
    private static readonly Color Accent    = Color.FromRgb(0x76, 0xB9, 0x00); // Br.Accent.Green
    private static readonly Color Neon      = Color.FromRgb(0x9D, 0xFF, 0x00); // Br.Accent.Neon
    private static readonly Color WarnCol   = Color.FromRgb(0xCC, 0xA7, 0x00); // Br.Warning
    private static readonly Color ErrCol    = Color.FromRgb(0xF4, 0x47, 0x47); // Br.Error
    private static readonly Color Frame     = Color.FromRgb(0x2E, 0x3A, 0x2E); // Br.Border
    private static readonly Color DimQueued = Color.FromRgb(0x3F, 0x5A, 0x4A);
    private static readonly Color PausedCol = Color.FromRgb(0x56, 0x9C, 0xD6); // Br.Accent.Blue

    private static Color VerdictColor(TestActivityKind kind) => kind switch
    {
        TestActivityKind.Success => Accent,
        TestActivityKind.Warning => WarnCol,
        _                        => ErrCol,
    };

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 80 || h < 80) return;

        double cx = w / 2;
        double topY = 10, waistTop = h * 0.40, waistBot = h * 0.60, botY = h - 10;
        double chamberHalf = Math.Min(w * 0.30, 170);
        double waistHalf   = Math.Min(w * 0.06, 26);

        DrawFrame(ctx, cx, topY, waistTop, waistBot, botY, chamberHalf, waistHalf);
        DrawQueuedParticles(ctx, cx, topY + 14, waistTop - 10, chamberHalf - 16);
        DrawSettledParticles(ctx, cx, waistBot + 10, botY - 14, chamberHalf - 16);
        DrawNetwork(ctx, cx, (waistTop + waistBot) / 2, w);
        DrawComets(ctx, cx, waistTop, waistBot);
    }

    // ── Hourglass frame ──────────────────────────────────────────────────────────

    private void DrawFrame(DrawingContext ctx, double cx, double topY, double waistTop,
        double waistBot, double botY, double chamberHalf, double waistHalf)
    {
        var active = _state.Phase is TestRunPhase.Running or TestRunPhase.Cancelling;
        var frameColor = _state.Phase switch
        {
            TestRunPhase.Failed => ErrCol,
            TestRunPhase.Paused => PausedCol,
            _ when active       => Accent,
            _                   => Frame,
        };
        var pen = new Pen(new SolidColorBrush(frameColor, active ? 0.55 : 0.45), 1.2);

        Geometry Side(double sign)
        {
            var g = new StreamGeometry();
            using var c = g.Open();
            c.BeginFigure(new Point(cx + sign * chamberHalf, topY), false);
            c.CubicBezierTo(
                new Point(cx + sign * chamberHalf, waistTop * 0.75),
                new Point(cx + sign * waistHalf, waistTop),
                new Point(cx + sign * waistHalf, (waistTop + waistBot) / 2));
            c.CubicBezierTo(
                new Point(cx + sign * waistHalf, waistBot),
                new Point(cx + sign * chamberHalf, botY - (botY - waistBot) * 0.25),
                new Point(cx + sign * chamberHalf, botY));
            c.EndFigure(false);
            return g;
        }

        ctx.DrawGeometry(null, pen, Side(-1));
        ctx.DrawGeometry(null, pen, Side(+1));
        ctx.DrawLine(pen, new Point(cx - chamberHalf, topY), new Point(cx + chamberHalf, topY));
        ctx.DrawLine(pen, new Point(cx - chamberHalf, botY), new Point(cx + chamberHalf, botY));
    }

    // ── Particle fields ──────────────────────────────────────────────────────────

    /// <summary>Deterministic pseudo-random 0..1 from an index — stable dot layout, no RNG state.</summary>
    private static double Hash(int i, int salt)
    {
        unchecked
        {
            uint x = (uint)(i * 374761393 + salt * 668265263);
            x = (x ^ (x >> 13)) * 1274126177u;
            return ((x ^ (x >> 16)) & 0xFFFFFF) / (double)0x1000000;
        }
    }

    private void DrawQueuedParticles(DrawingContext ctx, double cx, double yMin, double yMax, double halfW)
    {
        int remaining = Math.Max(0, _state.TotalSamples - _state.CompletedSamples);
        if (remaining == 0 || _state.TotalSamples == 0) return;
        // One dot represents `scale` samples so huge corpora don't multiply draw calls.
        int scale = Math.Max(1, (int)Math.Ceiling(_state.TotalSamples / (double)MaxParticles));
        int dots  = Math.Max(1, remaining / scale);

        var brush = new SolidColorBrush(DimQueued, 0.85);
        for (int i = 0; i < dots; i++)
        {
            // Funnel: dots sit tighter toward the waist. Slight per-dot drift while animating.
            double v  = Hash(i, 11);
            double y  = yMin + v * (yMax - yMin);
            double shrink = 1.0 - 0.72 * Math.Pow(v, 1.6);
            double drift = _liteMode ? 0 : Math.Sin(_t * Math.PI + i) * 1.6;
            double x  = cx + (Hash(i, 23) * 2 - 1) * halfW * shrink + drift;
            double r  = 1.6 + Hash(i, 31) * 1.4;
            ctx.DrawEllipse(brush, null, new Point(x, y + (_liteMode ? 0 : Math.Sin(_t * Math.PI * 2 + i * 0.7) * 1.2)), r, r);
        }
    }

    private void DrawSettledParticles(DrawingContext ctx, double cx, double yMin, double yMax, double halfW)
    {
        int completed = Math.Min(_state.CompletedSamples, Math.Max(_state.TotalSamples, 1));
        if (completed <= 0) return;

        // Derive the capped dot pile proportionally from the verdict TALLIES (never by
        // sampling every Nth verdict — that silently drops failures between the sampled
        // boundaries on large runs; CodeRabbit review). Largest-remainder-free approximation:
        // fail and warning dots each round up so even one failure is always visible.
        int scale = Math.Max(1, (int)Math.Ceiling(_state.TotalSamples / (double)MaxParticles));
        int dots  = Math.Max(1, (int)Math.Round(completed / (double)scale));
        // Infrastructure errors render in the failure colour too — they're non-passes.
        int failLike = _state.FailedSamples + _state.ErrorSamples;
        int failDots = failLike == 0 ? 0
            : Math.Clamp((int)Math.Ceiling(dots * failLike / (double)completed), 1, dots);
        // Guard failDots == dots: Math.Clamp(min:1, max:0) throws, and a 1-dot pile with mixed
        // fail+warning tallies hits exactly that (grok review BLOCKER) — failures win the dot.
        int warnDots = _state.WarningSamples == 0 || failDots >= dots ? 0
            : Math.Clamp((int)Math.Ceiling(dots * _state.WarningSamples / (double)completed), 1, dots - failDots);

        // Fill bottom-up: the pile grows toward the waist as more samples finish, and dots
        // spread wider near the chamber floor (inverted funnel).
        double fillFrac = Math.Min(1.0, dots / (double)MaxParticles);
        for (int i = 0; i < dots; i++)
        {
            double v = Hash(i, 41);                                   // 0 = floor .. 1 = top of pile
            double y = yMax - v * (yMax - yMin) * Math.Min(1.0, fillFrac + 0.15);
            double widen = 0.28 + 0.72 * Math.Clamp((y - yMin) / Math.Max(1, yMax - yMin), 0, 1);
            double x = cx + (Hash(i, 53) * 2 - 1) * halfW * widen;
            // Verdict colours by exact index range: the coordinate hashes above already
            // scatter positions, and range assignment guarantees every allocated failure
            // dot actually renders red — hash-bucket sampling (with replacement) could
            // miss the fail range entirely on small piles (CodeRabbit review).
            var color = i < failDots ? ErrCol
                : i < failDots + warnDots ? WarnCol
                : Accent;
            ctx.DrawEllipse(new SolidColorBrush(color, 0.9), null, new Point(x, y), 2.0, 2.0);
        }

        // Completion glow: full chamber softly lit once done.
        if (_state.Phase == TestRunPhase.Completed)
            DrawGlowDot(ctx, cx, (yMin + yMax) / 2, halfW * 0.9,
                _state.FailedSamples + _state.ErrorSamples > 0 ? WarnCol : Accent, 0.10);
    }

    // ── Waist network ────────────────────────────────────────────────────────────

    private void DrawNetwork(DrawingContext ctx, double cx, double cy, double w)
    {
        bool running = _state.Phase is TestRunPhase.Running or TestRunPhase.Cancelling;
        bool paused  = _state.Phase == TestRunPhase.Paused;
        double spread = Math.Min(w * 0.36, 190);

        // Satellite nodes: observable pipeline steps around the core.
        // (prompt in, generate, score, record out) — lights reflect actual run state.
        var satellites = new (double dx, double dy)[]
        {
            (-spread, 0), (-spread * 0.45, -14), (spread * 0.45, -14), (spread, 0),
        };

        var coreColor = _state.Phase switch
        {
            TestRunPhase.Failed    => ErrCol,
            TestRunPhase.Completed => Accent,
            TestRunPhase.Paused    => PausedCol,
            TestRunPhase.Cancelled => Frame,
            _ when running         => Neon,
            _                      => Frame,
        };

        // Edges core↔satellites.
        foreach (var (dx, dy) in satellites)
        {
            var on = running || _state.Phase == TestRunPhase.Completed;
            var pen = new Pen(new SolidColorBrush(on ? Accent : Frame, on ? 0.35 : 0.25), 1);
            ctx.DrawLine(pen, new Point(cx, cy), new Point(cx + dx, cy + dy));

            // Ambient signals along live edges (decorative motion, frozen by LiteMode/pause).
            if (running && !_liteMode)
            {
                bool outbound = _t <= 1.0;
                double prog = outbound ? _t : 2.0 - _t;
                double px = cx + dx * prog, py = cy + dy * prog;
                DrawGlowDot(ctx, px, py, 4.5, Accent, 0.5);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xEA, 0xFF, 0xF0)), null, new Point(px, py), 1.4, 1.4);
            }
        }

        // Satellite dots.
        foreach (var (dx, dy) in satellites)
        {
            var on = running || paused || _state.Phase == TestRunPhase.Completed;
            var col = on ? Accent : Frame;
            if (on) DrawGlowDot(ctx, cx + dx, cy + dy, 8, col, 0.35);
            ctx.DrawEllipse(new SolidColorBrush(col), null, new Point(cx + dx, cy + dy), 3.2, 3.2);
        }

        // Core hexagon, breathing while running.
        double breath = (running && !_liteMode) ? 1.0 + 0.08 * Math.Sin(_breath) : 1.0;
        double r = 11 * breath;
        if (_state.Phase != TestRunPhase.Idle)
            DrawGlowDot(ctx, cx, cy, r * 2.6, coreColor, paused ? 0.25 : 0.42);
        ctx.DrawGeometry(new SolidColorBrush(coreColor), new Pen(new SolidColorBrush(Colors.White, 0.25), 1),
            Hexagon(cx, cy, r));
    }

    private static Geometry Hexagon(double x, double y, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        for (int i = 0; i < 6; i++)
        {
            double a = -Math.PI / 2 + Math.PI / 6 + i * Math.PI / 3;
            var pt = new Point(x + Math.Cos(a) * r, y + Math.Sin(a) * r);
            if (i == 0) c.BeginFigure(pt, true);
            else c.LineTo(pt);
        }
        c.EndFigure(true);
        return g;
    }

    // ── Sample comets (real per-sample completion events) ────────────────────────

    private void DrawComets(DrawingContext ctx, double cx, double waistTop, double waistBot)
    {
        if (_comets.Count == 0 || _liteMode) return;
        long now = Environment.TickCount64;
        _comets.RemoveAll(c => now - c.StartMs > CometDurationMs);
        double travelTop = waistTop - 26, travelBot = waistBot + 26;
        foreach (var c in _comets)
        {
            double prog  = (now - c.StartMs) / CometDurationMs;      // 0..1
            double y     = travelTop + (travelBot - travelTop) * prog;
            double alpha = 1.0 - Math.Abs(prog - 0.5) * 0.8;
            DrawGlowDot(ctx, cx, y, 9, c.Color, 0.6 * alpha);
            ctx.DrawEllipse(new SolidColorBrush(Colors.White, Math.Max(0, alpha)), null, new Point(cx, y), 2.2, 2.2);
        }
    }

    private static void DrawGlowDot(DrawingContext ctx, double x, double y, double r, Color color, double maxAlpha)
    {
        for (int k = 3; k >= 1; k--)
        {
            double rr = r * k / 3.0;
            double a = maxAlpha * (1.0 - (k - 1) / 3.0) * 0.6;
            ctx.DrawEllipse(new SolidColorBrush(color, a), null, new Point(x, y), rr, rr);
        }
    }
}
