// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Side-by-side comparison of two Model Wiki entries (v1.4 roadmap item).
/// Rows: identity, role scores (numeric — higher value highlighted), GOBLIN
/// MIND probe results, and routing recommendations. Pure read view over the
/// already-merged entries handed in by ModelWikiWindow.
/// </summary>
public partial class ModelCompareWindow : Window
{
    private readonly List<ModelWikiEntry> _entries;
    private bool _ready;   // suppress renders until both pickers are seeded

    public ModelCompareWindow(List<ModelWikiEntry> entries, string? leftId = null, string? rightId = null)
    {
        InitializeComponent();
        _entries = entries;

        foreach (var e in _entries)
        {
            CbLeft.Items.Add(e.ModelId);
            CbRight.Items.Add(e.ModelId);
        }

        CbLeft.SelectedItem  = leftId  ?? _entries.FirstOrDefault(x => x.IsInstalled)?.ModelId
                                       ?? _entries.FirstOrDefault()?.ModelId;
        CbRight.SelectedItem = rightId ?? _entries.FirstOrDefault(x => x.IsInstalled &&
                                              !Equals(x.ModelId, CbLeft.SelectedItem))?.ModelId
                                       ?? _entries.Skip(1).FirstOrDefault()?.ModelId;
        _ready = true;
        Render();
    }

    private void Picker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Render();
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render()
    {
        var left  = _entries.FirstOrDefault(x => Equals(x.ModelId, CbLeft.SelectedItem));
        var right = _entries.FirstOrDefault(x => Equals(x.ModelId, CbRight.SelectedItem));
        if (left is null || right is null) return;

        RowsGrid.Children.Clear();
        RowsGrid.RowDefinitions.Clear();
        _row = 0;

        var rl = ModelWikiService.GetRoutingRecommendation(left);
        var rr = ModelWikiService.GetRoutingRecommendation(right);

        Section("IDENTITY");
        TextRow("Display name", left.DisplayName, right.DisplayName);
        TextRow("Installed",    left.IsInstalled ? "✅ yes" : "—", right.IsInstalled ? "✅ yes" : "—");
        TextRow("Speed",        left.SpeedLabel, right.SpeedLabel);
        TextRow("Min VRAM",     left.VramLabel,  right.VramLabel);
        TextRow("Primary role", left.PrimaryRole, right.PrimaryRole);

        Section("ROLE SCORES (0–10)");
        ScoreRow("Boss",       left.Profile.BossScore,       right.Profile.BossScore);
        ScoreRow("Coder",      left.Profile.CoderScore,      right.Profile.CoderScore);
        ScoreRow("Researcher", left.Profile.ResearcherScore, right.Profile.ResearcherScore);
        ScoreRow("Tester",     left.Profile.TesterScore,     right.Profile.TesterScore);

        Section("GOBLIN MIND PROBE");
        TextRow("Dispatch mode", ProbeText(left, p => p.RecommendedMode.ToString()),
                                 ProbeText(right, p => p.RecommendedMode.ToString()));
        TextRow("Tool-call format", ProbeText(left, p => p.FormatProfile?.PreferredFormat.ToString() ?? "—"),
                                    ProbeText(right, p => p.FormatProfile?.PreferredFormat.ToString() ?? "—"));
        TextRow("Categories", ProbeText(left, p => p.CategoryProfile?.ShortSummary ?? "—"),
                              ProbeText(right, p => p.CategoryProfile?.ShortSummary ?? "—"));

        Section("ROUTING RECOMMENDATION");
        MarkRow("Boss",            rl.Boss, rr.Boss);
        MarkRow("Coder",           rl.Coder, rr.Coder);
        MarkRow("Researcher",      rl.Researcher, rr.Researcher);
        MarkRow("Tester",          rl.Tester, rr.Tester);
        MarkRow("Single-agent",    rl.SingleAgent, rr.SingleAgent);
        MarkRow("Swarm worker",    rl.SwarmWorker, rr.SwarmWorker);
        MarkRow("Long write_file", rl.LongWriteFile, rr.LongWriteFile);
    }

    private static string ProbeText(ModelWikiEntry e, Func<Services.ToolCalls.ToolCallProfile, string> f)
        => e.ProbeProfile is { } p ? f(p) : "not probed";

    // ── Row builders ──────────────────────────────────────────────────────────

    private int _row;

    private void Section(string title)
    {
        RowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tb = new TextBlock
        {
            Text = title, FontFamily = new FontFamily("Consolas"), FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("Br.Accent.Green"),
            Margin = new Thickness(0, _row == 0 ? 4 : 16, 0, 6),
        };
        Grid.SetRow(tb, _row); Grid.SetColumn(tb, 0); Grid.SetColumnSpan(tb, 3);
        RowsGrid.Children.Add(tb);
        _row++;
    }

    private void TextRow(string label, string a, string b)
        => AddRow(label,
            Cell(a, "#D4D4D4", false),
            Cell(b, "#D4D4D4", false));

    private void ScoreRow(string label, int a, int b)
        => AddRow(label,
            Cell(a.ToString(), a >= b ? "#4ACA4A" : "#888888", a > b),
            Cell(b.ToString(), b >= a ? "#4ACA4A" : "#888888", b > a));

    private void MarkRow(string label, string a, string b)
    {
        static (string txt, string color) M(string v) => v switch
        {
            "Yes"     => ("✅ Yes",     "#4ACA4A"),
            "Limited" => ("⚠ Limited", "#E8A030"),
            "No"      => ("❌ No",      "#E84040"),
            _         => ("— Unknown",  "#888888"),
        };
        var (ta, ca) = M(a); var (tb, cb) = M(b);
        AddRow(label, Cell(ta, ca, false), Cell(tb, cb, false));
    }

    private TextBlock Cell(string text, string hex, bool bold) => new()
    {
        Text = text, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
        FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private void AddRow(string label, TextBlock a, TextBlock b)
    {
        RowsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var border = new Border
        {
            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1A1A1A")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(0, 5, 0, 5),
        };
        Grid.SetRow(border, _row); Grid.SetColumn(border, 0); Grid.SetColumnSpan(border, 3);
        RowsGrid.Children.Add(border);

        var lbl = new TextBlock
        {
            Text = label, FontFamily = new FontFamily("Segoe UI"), FontSize = 12,
            Foreground = (Brush)FindResource("Br.Text.Muted"),
            Margin = new Thickness(0, 5, 8, 5),
        };
        Grid.SetRow(lbl, _row); Grid.SetColumn(lbl, 0);
        RowsGrid.Children.Add(lbl);

        a.Margin = new Thickness(0, 5, 8, 5);
        b.Margin = new Thickness(0, 5, 0, 5);
        Grid.SetRow(a, _row); Grid.SetColumn(a, 1); RowsGrid.Children.Add(a);
        Grid.SetRow(b, _row); Grid.SetColumn(b, 2); RowsGrid.Children.Add(b);
        _row++;
    }
}
