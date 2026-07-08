// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

public partial class ModelBenchmarkWindow : Window
{
    private readonly AppSettings _settings;
    private readonly string      _repoRoot;
    private List<ModelBenchmarkRecord> _records = [];

    public ModelBenchmarkWindow(AppSettings settings, string repoRoot = "")
    {
        _settings = settings;
        _repoRoot = string.IsNullOrWhiteSpace(repoRoot)
            ? ResolveRepoRoot()
            : repoRoot;
        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        LoadAndDisplay(scanAdversarial: true);
    }

    private void BtnScan_Click(object? sender, RoutedEventArgs e)
        => LoadAndDisplay(scanAdversarial: true);

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Data loading ──────────────────────────────────────────────────────────

    private void LoadAndDisplay(bool scanAdversarial)
    {
        TbStatus.Text = "Scanning…";
        BtnScan.IsEnabled = false;

        Task.Run(() =>
        {
            var saved = ModelBenchmarkStore.LoadSaved();

            if (scanAdversarial && Directory.Exists(Path.Combine(_repoRoot, ".orc", "adversarial")))
            {
                var scanned = ModelBenchmarkStore.ScanAdversarialDir(_repoRoot);
                // Merge scanned into saved — scanned wins for the same (model + benchmarkId + generationId) key
                // but we don't persist scanned entries automatically (Phase 1 is read-only display)
                saved = MergeRecords(saved, scanned);
            }

            return saved;
        }).ContinueWith(t =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                BtnScan.IsEnabled = true;
                if (t.IsFaulted)
                {
                    TbStatus.Text = $"Scan failed: {t.Exception?.GetBaseException().Message}";
                    return;
                }
                _records = t.Result;
                Render();
            });
        }, TaskScheduler.Default);
    }

    private static List<ModelBenchmarkRecord> MergeRecords(
        List<ModelBenchmarkRecord> saved,
        List<ModelBenchmarkRecord> scanned)
    {
        // Keyed by (modelFilename, benchmarkId, tier) — saved takes precedence if run dates match
        var map = new Dictionary<string, ModelBenchmarkRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in scanned)
            map[RecordKey(r)] = r;
        foreach (var r in saved)
            map[RecordKey(r)] = r;   // saved wins
        return [.. map.Values.OrderByDescending(r => r.RunDate)];
    }

    private static string RecordKey(ModelBenchmarkRecord r) =>
        $"{r.ModelFilename}|{r.BenchmarkId}|{r.BenchmarkTier}|{r.RunDate:O}";

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void Render()
    {
        ItemsPanel.Children.Clear();

        // Group records by model filename, most-recently-run first
        var byModel = _records
            .GroupBy(r => r.ModelFilename, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Max(r => r.RunDate))
            .ToList();

        // Also collect GGUF files in the depot that have no benchmark record
        var depotFiles = GetDepotModelFiles();
        var benchmarkedModels = new HashSet<string>(
            byModel.Select(g => g.Key), StringComparer.OrdinalIgnoreCase);

        // Render benchmarked models first
        foreach (var group in byModel)
        {
            var sorted = group.OrderByDescending(r => r.RunDate).ToList();
            ItemsPanel.Children.Add(BuildModelCard(group.Key, sorted));
        }

        // Unbenchmarked depot models
        foreach (var file in depotFiles.OrderBy(f => f))
        {
            if (benchmarkedModels.Contains(Path.GetFileName(file))) continue;
            ItemsPanel.Children.Add(BuildUnbenchmarkedRow(file));
        }

        if (ItemsPanel.Children.Count == 0)
        {
            ItemsPanel.Children.Add(new TextBlock
            {
                Text = "No benchmark results found. Click 'Scan CF Runs' to import results from .orc/adversarial/, " +
                       "or run a new benchmark (coming in Phase 2).",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Avalonia.Thickness(0, 20, 0, 0),
            });
        }

        var modelCount = byModel.Count;
        var runCount   = _records.Count;
        TbStatus.Text = $"{modelCount} model{(modelCount == 1 ? "" : "s")}, {runCount} benchmark run{(runCount == 1 ? "" : "s")}";
    }

    private Border BuildModelCard(string modelFilename, List<ModelBenchmarkRecord> runs)
    {
        var panel = new StackPanel { Spacing = 8 };

        // Model header row
        panel.Children.Add(BuildModelHeaderRow(modelFilename));

        // Run rows
        foreach (var run in runs)
            panel.Children.Add(BuildRunRow(run));

        return new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x0D)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(5),
            Padding       = new Avalonia.Thickness(14, 12),
            Child         = panel,
        };
    }

    private static Border BuildModelHeaderRow(string modelFilename)
    {
        var label = new TextBlock
        {
            Text       = modelFilename,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize   = 14,
            FontWeight = FontWeight.SemiBold,
        };

        return new Border
        {
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Padding         = new Avalonia.Thickness(0, 0, 0, 8),
            Child           = label,
        };
    }

    private static Border BuildRunRow(ModelBenchmarkRecord run)
    {
        var verdictColor = Color.Parse(run.VerdictColor);
        var verdictBg    = Color.FromArgb(35, verdictColor.R, verdictColor.G, verdictColor.B);

        // Verdict badge
        var badge = new Border
        {
            Background    = new SolidColorBrush(verdictBg),
            BorderBrush   = new SolidColorBrush(verdictColor),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(3),
            Padding       = new Avalonia.Thickness(7, 2),
            Margin        = new Avalonia.Thickness(0, 0, 10, 0),
            Child         = new TextBlock
            {
                Text       = run.VerdictLabel,
                Foreground = new SolidColorBrush(verdictColor),
                FontSize   = 11,
                FontWeight = FontWeight.Bold,
            },
        };

        // Tier + date header
        var tierDate = new TextBlock
        {
            Text       = $"{run.TierLabel}  ·  {run.RunDate:yyyy-MM-dd HH:mm}",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new DockPanel { Margin = new Avalonia.Thickness(0, 0, 0, 6) };
        DockPanel.SetDock(badge,   Avalonia.Controls.Dock.Left);
        DockPanel.SetDock(tierDate, Avalonia.Controls.Dock.Left);
        header.Children.Add(badge);
        header.Children.Add(tierDate);

        // Metrics row
        var metrics = new WrapPanel { Orientation = Orientation.Horizontal };
        metrics.Children.Add(MetricChip("Questions",  $"{run.QuestionsPassed}/{run.QuestionsTotal}",
            run.QuestionPassPct >= 70 ? "#4ACA4A" : run.QuestionPassPct >= 40 ? "#E8A030" : "#E84040"));
        metrics.Children.Add(MetricChip("Citations",  $"{run.CitationPrecisionPct}%",
            run.CitationPrecisionPct >= 90 ? "#4ACA4A" : "#E8A030"));
        metrics.Children.Add(MetricChip("Segments",   $"{run.SegmentCoveragePct}%",
            run.SegmentCoveragePct >= 95 ? "#4ACA4A" : run.SegmentCoveragePct >= 80 ? "#E8A030" : "#E84040"));
        metrics.Children.Add(MetricChip("Stitching",  $"{(int)Math.Round(run.BoundaryStitchPassRate * 100)}%",
            run.BoundaryStitchPassRate >= 1.0 ? "#4ACA4A" : run.BoundaryStitchPassRate > 0 ? "#E8A030" : "#E84040"));

        if (!string.IsNullOrWhiteSpace(run.Machine))
            metrics.Children.Add(MetricChip("Machine", run.Machine, "#6688AA"));

        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(header);
        body.Children.Add(metrics);
        if (!string.IsNullOrWhiteSpace(run.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text       = run.Notes,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 11,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                Margin     = new Avalonia.Thickness(0, 4, 0, 0),
            });
        }

        return new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x08, 0x0D, 0x08)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x1A, 0x28, 0x1A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(4),
            Padding       = new Avalonia.Thickness(10, 8),
            Child         = body,
        };
    }

    private static Border BuildUnbenchmarkedRow(string filePath)
    {
        var info = new FileInfo(filePath);
        var size = info.Length < 1_073_741_824
            ? $"{info.Length / 1_048_576.0:F0} MB"
            : $"{info.Length / 1_073_741_824.0:F2} GB";

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(new TextBlock
        {
            Text       = Path.GetFileName(filePath),
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x66)),
            FontSize   = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var sizeLabel = new TextBlock
        {
            Text       = $"{size} · Not benchmarked",
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)),
            FontSize   = 11,
            FontFamily = new FontFamily("Consolas"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
        };
        Grid.SetColumn(sizeLabel, 1);
        grid.Children.Add(sizeLabel);

        return new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x0A)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x18, 0x20, 0x18)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(4),
            Padding       = new Avalonia.Thickness(12, 8),
            Child         = grid,
        };
    }

    private static Border MetricChip(string label, string value, string colorHex)
    {
        var color = Color.Parse(colorHex);
        return new Border
        {
            Background    = new SolidColorBrush(Color.FromArgb(25, color.R, color.G, color.B)),
            BorderBrush   = new SolidColorBrush(Color.FromArgb(80, color.R, color.G, color.B)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius  = new Avalonia.CornerRadius(3),
            Padding       = new Avalonia.Thickness(7, 3),
            Margin        = new Avalonia.Thickness(0, 0, 6, 4),
            Child         = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing     = 5,
                Children    =
                {
                    new TextBlock
                    {
                        Text       = label,
                        Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                        FontSize   = 10,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                    new TextBlock
                    {
                        Text       = value,
                        Foreground = new SolidColorBrush(color),
                        FontSize   = 11,
                        FontWeight = FontWeight.SemiBold,
                        FontFamily = new FontFamily("Consolas"),
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            },
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private List<string> GetDepotModelFiles()
    {
        var dir = _settings.ResolvedModelStoragePath;
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return [];
        return [.. Directory.GetFiles(dir, "*.gguf")];
    }

    private static string ResolveRepoRoot()
    {
        // Walk up from the executable looking for the .orc directory
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, ".orc"))) return dir;
            var parent = Directory.GetParent(dir)?.FullName;
            if (parent is null || parent == dir) break;
            dir = parent;
        }
        return AppContext.BaseDirectory;
    }
}
