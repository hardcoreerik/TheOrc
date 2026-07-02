// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// One node in the constellation, as the panel hands it to the view. Layout (X/Y/R) is filled
/// in by the view at render time; the panel supplies identity, role, and state.
/// </summary>
public sealed class HiveNodeVisual
{
    public required HiveHost Host { get; init; }
    public string Name      { get; init; } = "";
    /// <summary>"warchief" | "boss" | "coder" | "research" | "ui" | "tester" | "reviewer" |
    /// "observer" | "worker". "boss" = sovereign, not yet in any hive. "observer" = paired
    /// but holds no write/exec authority — both render as a hollow ring, see DrawNode.</summary>
    public string Role      { get; init; } = "worker";
    public bool   IsCenter  { get; init; }   // the centered/crowned node = the Warchief (whichever machine that is)
    public bool   IsLocal   { get; init; }   // this is THIS machine ("This PC") — drives the self-action menu + label,
                                             // independent of IsCenter (a worker sees the Warchief centered, itself on the ring)
    /// <summary>"online" | "offline" | "working" | "returning".</summary>
    public string State     { get; init; } = "online";
    public string SubLabel  { get; init; } = "";   // small line under the name (role · VRAM)

    // Filled by the view during layout — used for hit-testing.
    internal double X, Y, R;
}

public sealed class HiveNodeEventArgs : EventArgs
{
    public required HiveNodeVisual Node { get; init; }
    public Point Position { get; init; }
}

/// <summary>
/// Immediate-mode constellation renderer — the "living neural swarm" look approved in
/// .grok/HIVE_VISUALS.md (Concept 2.1). Draws per-role node SHAPES (crowned hexagon Warchief,
/// diamond Coder, circle Researcher, rounded-square UIDeveloper, triangle Tester, pentagon
/// Reviewer), glow, a breathing Warchief core, and ambient signal particles along the edges.
///
/// This replaces the previous Border/Shape-per-node approach (which can only draw rectangles)
/// with a single <see cref="Control"/> + <see cref="Render"/> override, so non-rectangular
/// shapes, crowns, and glow are possible at all. Animation runs on the control's own ~50 ms
/// timer and is frozen by <see cref="LiteMode"/> (shapes still draw, motion stops) so a thin
/// node like the 1.8 GB Pi stays cheap.
/// </summary>
public sealed class HiveConstellationView : Control
{
    private IReadOnlyList<HiveNodeVisual> _nodes = [];
    private readonly DispatcherTimer _anim;
    private double _t;          // particle phase (0..2, modulo)
    private double _breath;     // warchief breathing phase

    // Directional work pulses: one bright particle per REAL task event, so you can see work
    // actually move to/from a specific machine (claim = out to the worker, complete = back to
    // the Warchief), distinct from the always-on ambient particles. Added on the UI thread from
    // HivePanel.PollEvents and drained in Render — same thread, no locking needed.
    private sealed class EdgePulse
    {
        public string Worker = "";
        public bool   Inbound;      // true = worker→Warchief (result returning); false = Warchief→worker
        public long   StartMs;
    }
    private readonly List<EdgePulse> _pulses = [];
    private const double PulseDurationMs = 1150;

    /// <summary>
    /// Fires a one-shot directional pulse along the edge for <paramref name="workerName"/>.
    /// inbound=false animates Warchief→worker (task just claimed); inbound=true animates
    /// worker→Warchief (result just completed). No-op if the worker isn't a peer on the ring
    /// (e.g. the Warchief executing its own unit has no edge to itself).
    /// </summary>
    public void PulseEdge(string workerName, bool inbound)
    {
        if (string.IsNullOrWhiteSpace(workerName)) return;
        _pulses.Add(new EdgePulse { Worker = workerName, Inbound = inbound, StartMs = Environment.TickCount64 });
        // Cap so a burst can't grow unbounded; oldest fade first anyway.
        if (_pulses.Count > 96) _pulses.RemoveRange(0, _pulses.Count - 96);
        if (!_liteMode && !_anim.IsEnabled && _nodes.Count > 0) _anim.Start();
        InvalidateVisual();
    }

    // Exact case-insensitive match is the norm. The only reason to accept a prefix is NetBIOS
    // truncation: WorkerId is Environment.MachineName, which the network layer caps at 15 chars,
    // so a fuller node name (e.g. "hardcorelaptopmsi") can legitimately show up truncated
    // ("HARDCORELAPTOPM"). Restrict the prefix path to that case only — the shorter side must be
    // a FULL prefix of the longer AND be exactly 15 chars — so two distinct machines that merely
    // share a stem (e.g. "HARDCOREPC"/"HARDCOREPI") never cross-match (Codex review MINOR).
    private static bool WorkerMatchesNode(string nodeName, string worker)
    {
        if (string.IsNullOrEmpty(nodeName) || string.IsNullOrEmpty(worker)) return false;
        if (nodeName.Equals(worker, StringComparison.OrdinalIgnoreCase)) return true;
        var shorter = nodeName.Length <= worker.Length ? nodeName : worker;
        var longer  = nodeName.Length <= worker.Length ? worker : nodeName;
        return shorter.Length == 15 && longer.Length > 15
            && longer.StartsWith(shorter, StringComparison.OrdinalIgnoreCase);
    }

    public HiveConstellationView()
    {
        _anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _anim.Tick += (_, _) =>
        {
            _t = (_t + 1.0 / 50.0) % 2.0;
            _breath += 2.0 * Math.PI / 70.0;
            InvalidateVisual();
        };
    }

    /// <summary>Raised on left-click of a node (panel opens its detail).</summary>
    public event EventHandler<HiveNodeEventArgs>? NodeClicked;
    /// <summary>Raised on right-click of a node (panel opens its role/context menu).</summary>
    public event EventHandler<HiveNodeEventArgs>? NodeRightClicked;

    private bool _liteMode;
    public bool LiteMode
    {
        get => _liteMode;
        set { _liteMode = value; SyncTimer(); InvalidateVisual(); }
    }

    public void SetNodes(IReadOnlyList<HiveNodeVisual> nodes)
    {
        _nodes = nodes ?? [];
        SyncTimer();
        InvalidateVisual();
    }

    private void SyncTimer()
    {
        var animate = !_liteMode && _nodes.Count > 0;
        if (animate && !_anim.IsEnabled) _anim.Start();
        else if (!animate && _anim.IsEnabled) _anim.Stop();
    }

    // ── Palette ────────────────────────────────────────────────────────────────
    private static readonly Color BgGlow   = Color.FromArgb(0x66, 0x14, 0x28, 0x1c);
    private static readonly Color Online   = Color.FromRgb(0x4e, 0xe3, 0x9b);
    private static readonly Color Core     = Color.FromRgb(0x5a, 0xf2, 0xc0);
    private static readonly Color Working  = Color.FromRgb(0xf5, 0xb7, 0x3d);
    private static readonly Color Returning= Color.FromRgb(0x5a, 0xb0, 0xf0);
    private static readonly Color Offline  = Color.FromRgb(0x33, 0x47, 0x3b);
    private static readonly Color Crown    = Color.FromRgb(0xff, 0xd7, 0x6a);
    private static readonly Color LabelOn  = Color.FromRgb(0x8f, 0xe9, 0xb8);
    private static readonly Color LabelOff = Color.FromRgb(0x3f, 0x5a, 0x4a);
    private static readonly Color SubLabel = Color.FromRgb(0x2f, 0x6a, 0x4a);

    private static Color StateColor(HiveNodeVisual n) => n.State switch
    {
        "offline"   => Offline,
        "working"   => Working,
        "returning" => Returning,
        _           => n.Role == "warchief" ? Core : Online,
    };

    public override void Render(DrawingContext ctx)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w < 50 || h < 50 || _nodes.Count == 0) return;

        double cx = w / 2, cy = h / 2;
        var center = _nodes.FirstOrDefault(n => n.IsCenter);
        var peers  = _nodes.Where(n => !n.IsCenter).ToList();
        double radius = Math.Max(120, Math.Min(w, h) / 2 - 110);

        // Layout: center node at middle, peers on a ring. Cache X/Y/R for hit-testing.
        if (center is not null) { center.X = cx; center.Y = cy; center.R = 19; }
        for (int i = 0; i < peers.Count; i++)
        {
            double a = -Math.PI / 2 + i * 2 * Math.PI / Math.Max(1, peers.Count);
            peers[i].X = cx + radius * Math.Cos(a);
            peers[i].Y = cy + radius * Math.Sin(a);
            peers[i].R = 14;
        }

        // Edges center→peer (faint; dashed when offline).
        if (center is not null)
        {
            foreach (var p in peers)
            {
                bool off = p.State == "offline" || center.State == "offline";
                var pen = new Pen(new SolidColorBrush(off ? Offline : Online, off ? 0.18 : 0.22), 1,
                    off ? new DashStyle(new double[] { 4, 4 }, 0) : null);
                ctx.DrawLine(pen, new Point(center.X, center.Y), new Point(p.X, p.Y));
            }

            // Ambient signal particles along live edges (frozen in Lite Mode).
            if (!LiteMode)
            {
                bool outbound = _t <= 1.0;
                double prog = outbound ? _t : 2.0 - _t;
                var pColor = outbound ? Working : Online;
                foreach (var p in peers.Where(p => p.State != "offline"))
                {
                    double x = center.X + (p.X - center.X) * prog;
                    double y = center.Y + (p.Y - center.Y) * prog;
                    DrawGlowDot(ctx, x, y, 6, pColor, 0.5);
                    ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xea, 0xff, 0xf5)), null, new Point(x, y), 2, 2);
                }
            }
        }

        // Directional work pulses along a specific worker's edge (drawn over the ambient
        // particles, under the nodes). This is the "see work moving to/from that PC" effect.
        if (center is not null && _pulses.Count > 0 && !LiteMode)
        {
            long now = Environment.TickCount64;
            _pulses.RemoveAll(pl => now - pl.StartMs > PulseDurationMs);
            foreach (var pl in _pulses)
            {
                var peer = peers.FirstOrDefault(p => WorkerMatchesNode(p.Name, pl.Worker));
                if (peer is null) continue;
                double prog  = (now - pl.StartMs) / PulseDurationMs;       // 0..1 over lifetime
                double frac  = pl.Inbound ? 1.0 - prog : prog;            // inbound rides peer→center
                double x     = center.X + (peer.X - center.X) * frac;
                double y     = center.Y + (peer.Y - center.Y) * frac;
                double alpha = 1.0 - prog;                                // fade as it travels
                var col      = pl.Inbound ? Returning : Working;          // blue back / amber out
                // Bright comet: outer glow + a small white-hot core.
                DrawGlowDot(ctx, x, y, 13, col, 0.6 * alpha);
                ctx.DrawEllipse(new SolidColorBrush(Color.FromRgb(0xff, 0xff, 0xff), Math.Max(0, alpha)),
                    null, new Point(x, y), 3.4, 3.4);
            }
        }

        foreach (var p in peers) DrawNode(ctx, p, false);
        if (center is not null) DrawNode(ctx, center, true);
    }

    private void DrawNode(DrawingContext ctx, HiveNodeVisual n, bool isCenter)
    {
        var color = StateColor(n);
        double breath = (isCenter && !LiteMode) ? 1.0 + 0.06 * Math.Sin(_breath) : 1.0;
        double r = n.R * breath;

        // Boss (sovereign, not yet in a hive) and Observer (paired, no write/exec authority)
        // both render as a hollow ring rather than the usual filled silhouette -- "Observer /
        // unassigned → thin ring" per .grok/HIVE_VISUALS.md; Boss reuses the same deliberately
        // lighter treatment since it's also not commanding or being commanded by anything yet.
        bool isHollow = n.Role is "observer" or "boss";

        // Glow (concentric translucent rings — version-safe, no radial-gradient brush).
        if (n.State != "offline")
            DrawGlowDot(ctx, n.X, n.Y, r * 2.6, color, isHollow ? 0.18 : 0.42);

        // Warchief breathing pulse rings.
        if (n.Role == "warchief" && !LiteMode)
        {
            for (int k = 0; k < 3; k++)
            {
                double rr = r + 9 + k * 9 + (_t * 11) % 9;
                double alpha = 0.16 - k * 0.045;
                ctx.DrawEllipse(null, new Pen(new SolidColorBrush(Core, alpha), 1),
                    new Point(n.X, n.Y), rr, rr);
            }
        }

        // The role shape.
        var geo = ShapeFor(n.Role, n.X, n.Y, r);
        if (isHollow)
        {
            var ringStroke = new Pen(new SolidColorBrush(n.State == "offline" ? Offline : color), 1.6);
            ctx.DrawGeometry(null, ringStroke, geo);
        }
        else
        {
            var fill = new SolidColorBrush(color);
            var stroke = new Pen(new SolidColorBrush(n.State == "offline" ? Offline : Colors.White, 0.25), 1);
            ctx.DrawGeometry(fill, stroke, geo);
        }

        // Small specular highlight.
        if (n.State != "offline" && !isHollow)
            ctx.DrawEllipse(new SolidColorBrush(Colors.White, 0.55), null,
                new Point(n.X - r * 0.28, n.Y - r * 0.28), r * 0.26, r * 0.26);

        // Crown on the Warchief.
        if (n.Role == "warchief") DrawCrown(ctx, n.X, n.Y, r);

        // Labels.
        var nameBrush = new SolidColorBrush(
            n.State == "offline" ? LabelOff : (n.Role == "warchief" ? new SolidColorBrush(Color.FromRgb(0xae, 0xf9, 0xd4)).Color : LabelOn));
        DrawCenteredText(ctx, n.Name, n.X, n.Y + r + 6, isCenter ? 12 : 11, nameBrush);
        if (n.SubLabel.Length > 0)
            DrawCenteredText(ctx, n.SubLabel, n.X, n.Y + r + 6 + (isCenter ? 17 : 16), 9, new SolidColorBrush(SubLabel));
    }

    // ── Geometry helpers ─────────────────────────────────────────────────────────

    private static Geometry ShapeFor(string role, double x, double y, double r)
    {
        return role switch
        {
            "warchief" => Polygon(x, y, r, 6, -Math.PI / 2 + Math.PI / 6),  // hexagon (+ crown)
            "boss"     => Polygon(x, y, r, 6, -Math.PI / 2 + Math.PI / 6),  // hollow hexagon (sovereign, no hive yet)
            "coder"    => Polygon(x, y, r, 4, -Math.PI / 2),                // diamond
            "tester"   => Triangle(x, y, r),
            "reviewer" => Polygon(x, y, r, 5, -Math.PI / 2),               // pentagon
            "ui"       => RoundedSquare(x, y, r),
            "research" => Circle(x, y, r),
            "observer" => Circle(x, y, r),                                  // hollow ring (paired, no authority)
            _          => Polygon(x, y, r, 6, -Math.PI / 2 + Math.PI / 6),  // worker/all-lanes → plain hexagon
        };
    }

    private static Geometry Polygon(double x, double y, double r, int sides, double startAngle)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        for (int i = 0; i < sides; i++)
        {
            double a = startAngle + i * 2 * Math.PI / sides;
            var pt = new Point(x + Math.Cos(a) * r, y + Math.Sin(a) * r);
            if (i == 0) c.BeginFigure(pt, true);
            else c.LineTo(pt);
        }
        c.EndFigure(true);
        return g;
    }

    private static Geometry Triangle(double x, double y, double r)
    {
        var g = new StreamGeometry();
        using var c = g.Open();
        c.BeginFigure(new Point(x, y - r), true);
        c.LineTo(new Point(x + r * 0.92, y + r * 0.72));
        c.LineTo(new Point(x - r * 0.92, y + r * 0.72));
        c.EndFigure(true);
        return g;
    }

    private static Geometry RoundedSquare(double x, double y, double r)
        => new RectangleGeometry(new Rect(x - r, y - r, r * 2, r * 2), r * 0.42, r * 0.42);

    private static Geometry Circle(double x, double y, double r)
        => new EllipseGeometry(new Rect(x - r, y - r, r * 2, r * 2));

    private static void DrawCrown(DrawingContext ctx, double x, double y, double r)
    {
        double cy = y - r - 7, wd = r * 0.85;
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(x - wd, cy + 5), true);
            c.LineTo(new Point(x - wd, cy));
            c.LineTo(new Point(x - wd * 0.5, cy + 3));
            c.LineTo(new Point(x, cy - 3));
            c.LineTo(new Point(x + wd * 0.5, cy + 3));
            c.LineTo(new Point(x + wd, cy));
            c.LineTo(new Point(x + wd, cy + 5));
            c.EndFigure(true);
        }
        ctx.DrawGeometry(new SolidColorBrush(Crown), null, g);
    }

    private static void DrawGlowDot(DrawingContext ctx, double x, double y, double r, Color color, double maxAlpha)
    {
        // Three concentric translucent ellipses fake a radial glow cheaply.
        for (int k = 3; k >= 1; k--)
        {
            double rr = r * k / 3.0;
            double a = maxAlpha * (1.0 - (k - 1) / 3.0) * 0.6;
            ctx.DrawEllipse(new SolidColorBrush(color, a), null, new Point(x, y), rr, rr);
        }
    }

    private static readonly Typeface Mono = new("Consolas");

    private static void DrawCenteredText(DrawingContext ctx, string text, double cx, double top, double size, IBrush brush)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Mono, size, brush);
        ctx.DrawText(ft, new Point(cx - ft.Width / 2, top));
    }

    // ── Hit-testing / interaction ────────────────────────────────────────────────

    private HiveNodeVisual? HitTest(Point p)
    {
        foreach (var n in _nodes)
        {
            double dx = p.X - n.X, dy = p.Y - n.Y, rr = n.R + 8;
            if (dx * dx + dy * dy <= rr * rr) return n;
        }
        return null;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var pt = e.GetCurrentPoint(this);
        var node = HitTest(pt.Position);
        if (node is null) return;
        if (pt.Properties.IsRightButtonPressed)
            NodeRightClicked?.Invoke(this, new HiveNodeEventArgs { Node = node, Position = pt.Position });
        else if (pt.Properties.IsLeftButtonPressed)
            NodeClicked?.Invoke(this, new HiveNodeEventArgs { Node = node, Position = pt.Position });
    }
}
