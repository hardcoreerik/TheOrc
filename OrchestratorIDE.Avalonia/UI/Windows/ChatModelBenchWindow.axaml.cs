// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Runs ModelBenchCorpus (general capability + "uncensored ability" over-refusal cases) against
/// any subset of currently-installed Ollama models and shows a ranked results table. Separate
/// from ModelBenchmarkWindow on purpose -- that window's "Phase 2: run a new benchmark" stub is
/// specifically for Context Fabric quality benchmarking against depot GGUF files (citation
/// precision, segment coverage, boundary stitching), a genuinely different domain from general
/// chat-quality/refusal testing. Reusing that window's UI for this would misrepresent CF-7
/// testing as general chat testing.
/// </summary>
public partial class ChatModelBenchWindow : Window
{
    private readonly OllamaClient  _ollama;
    private readonly string?       _workspaceRoot;
    private readonly IModelRuntime _runtime;

    private readonly Dictionary<string, CheckBox> _modelCheckboxes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;

    public ChatModelBenchWindow(OllamaClient ollama, string? workspaceRoot = null)
    {
        _ollama        = ollama;
        _workspaceRoot = workspaceRoot;
        _runtime       = new OllamaRuntime(ollama);
        InitializeComponent();
        Opened += OnOpened;
        Closing += (_, _) => _runCts?.Cancel();
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        await LoadModelsAsync();
        LoadLastReport();
    }

    // ── Model list ────────────────────────────────────────────────────────────

    private async Task LoadModelsAsync()
    {
        TbStatus.Text = "Loading installed models…";
        List<string> models;
        try
        {
            models = await _ollama.GetInstalledModelsAsync();
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Failed to list models: {ex.Message}";
            return;
        }

        ModelListPanel.Children.Clear();
        _modelCheckboxes.Clear();
        foreach (var model in models.OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            var cb = new CheckBox
            {
                Content    = model,
                IsChecked  = true,
                Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
                FontSize   = 12,
                Margin     = new Avalonia.Thickness(0, 1),
            };
            _modelCheckboxes[model] = cb;
            ModelListPanel.Children.Add(cb);
        }

        TbStatus.Text = $"{models.Count} model{(models.Count == 1 ? "" : "s")} installed";
    }

    private void BtnSelectAll_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var cb in _modelCheckboxes.Values) cb.IsChecked = true;
    }

    private void BtnSelectNone_Click(object? sender, RoutedEventArgs e)
    {
        foreach (var cb in _modelCheckboxes.Values) cb.IsChecked = false;
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();

    // ── Run ───────────────────────────────────────────────────────────────────

    private async void BtnRun_Click(object? sender, RoutedEventArgs e)
    {
        if (_runCts is not null)
        {
            // Already running -- treat as a cancel request.
            _runCts.Cancel();
            return;
        }

        var selected = _modelCheckboxes
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => kv.Key)
            .ToList();
        if (selected.Count == 0)
        {
            TbStatus.Text = "Select at least one model to bench.";
            return;
        }

        _runCts        = new CancellationTokenSource();
        BtnRun.Content = "■  Stop";
        ResultsPanel.Children.Clear();
        var totalCases = ModelBenchCorpus.AllCases.Count * selected.Count;
        var completed  = 0;

        try
        {
            var report = await ModelBenchRunner.RunAsync(
                _runtime, selected,
                onCaseStart: (model, testCase) => Dispatcher.UIThread.Post(() =>
                    TbStatus.Text = $"Testing {model} — {testCase.Category} ({completed + 1}/{totalCases})…"),
                onCaseComplete: _ => Dispatcher.UIThread.Post(() => completed++),
                ct: _runCts.Token);

            RenderResults(report);
            TbStatus.Text = $"Done — {selected.Count} model{(selected.Count == 1 ? "" : "s")}, {report.Results.Count} cases.";

            try
            {
                var path = await ModelBenchReportStore.WriteAsync(report, _workspaceRoot);
                TbStatus.Text += $"  Saved: {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                TbStatus.Text += $"  (save failed: {ex.Message})";
            }
        }
        catch (OperationCanceledException)
        {
            TbStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Bench failed: {ex.Message}";
        }
        finally
        {
            _runCts?.Dispose();
            _runCts        = null;
            BtnRun.Content = "▶  Run Bench";
        }
    }

    private void LoadLastReport()
    {
        var reports = ModelBenchReportStore.LoadAll(_workspaceRoot);
        if (reports.Count > 0)
        {
            RenderResults(reports[0]);
            TbStatus.Text = $"Showing last run: {reports[0].GeneratedUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
    }

    // ── Rendering ─────────────────────────────────────────────────────────────

    private void RenderResults(ModelBenchReport report)
    {
        ResultsPanel.Children.Clear();

        var ranked = report.Summaries
            .OrderByDescending(s => (s.CapabilityScore ?? 0) + (s.UncensoredScore ?? 0))
            .ToList();

        if (ranked.Count == 0)
        {
            ResultsPanel.Children.Add(new TextBlock
            {
                Text       = "No results yet — pick models on the left and click Run Bench.",
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
                FontSize   = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin     = new Avalonia.Thickness(0, 20, 0, 0),
            });
            return;
        }

        for (var i = 0; i < ranked.Count; i++)
            ResultsPanel.Children.Add(BuildSummaryRow(i + 1, ranked[i]));
    }

    private static Border BuildSummaryRow(int rank, ModelBenchModelSummary summary)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*,110,110,60") };

        grid.Children.Add(new TextBlock
        {
            Text = $"#{rank}", Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x77, 0x66)),
            FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
        });

        var nameLabel = new TextBlock
        {
            Text = summary.Model, Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            FontSize = 13, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(nameLabel, 1);
        grid.Children.Add(nameLabel);

        var capChip = ScoreChip("Capability", summary.CapabilityScore, summary.CapabilityPassed, summary.CapabilityTotal);
        Grid.SetColumn(capChip, 2);
        grid.Children.Add(capChip);

        var uncChip = ScoreChip("Uncensored", summary.UncensoredScore, summary.UncensoredPassed, summary.UncensoredTotal);
        Grid.SetColumn(uncChip, 3);
        grid.Children.Add(uncChip);

        var errLabel = new TextBlock
        {
            Text = summary.Errors > 0 ? $"{summary.Errors} err" : "",
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x40, 0x40)),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(errLabel, 4);
        grid.Children.Add(errLabel);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x14, 0x0D)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(4),
            Padding         = new Avalonia.Thickness(10, 8),
            Child           = grid,
        };
    }

    private static Border ScoreChip(string label, double? score, int passed, int total)
    {
        var pct   = score.HasValue ? (int)Math.Round(score.Value * 100) : 0;
        var color = !score.HasValue ? "#666666" : pct >= 80 ? "#4ACA4A" : pct >= 50 ? "#E8A030" : "#E84040";
        var parsed = Color.Parse(color);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(25, parsed.R, parsed.G, parsed.B)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(80, parsed.R, parsed.G, parsed.B)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius    = new Avalonia.CornerRadius(3),
            Padding         = new Avalonia.Thickness(6, 3),
            Margin          = new Avalonia.Thickness(4, 0),
            Child           = new StackPanel
            {
                Spacing  = 0,
                Children =
                {
                    new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)), FontSize = 9 },
                    new TextBlock
                    {
                        Text = score.HasValue ? $"{pct}% ({passed}/{total})" : "n/a",
                        Foreground = new SolidColorBrush(parsed), FontSize = 12, FontWeight = FontWeight.SemiBold,
                        FontFamily = new FontFamily("Consolas"),
                    },
                },
            },
        };
    }
}
