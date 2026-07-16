// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Runs ModelBenchCorpus (general capability + "uncensored ability" over-refusal cases) against
/// any subset of currently-installed Ollama models and shows a ranked results table. Separate
/// from ModelBenchmarkWindow on purpose -- that window's "Phase 2: run a new benchmark" stub is
/// specifically for Context Fabric quality benchmarking against depot GGUF files (citation
/// precision, segment coverage, boundary stitching), a genuinely different domain from general
/// chat-quality/refusal testing. Reusing that window's UI for this would misrepresent CF-7
/// testing as general chat testing.
///
/// Visual test runner architecture (layers, see also Core/Runtime):
///   1. execution      — ModelBenchRunner (source of truth, UI-free)
///   2. telemetry      — TestRunTelemetryModel (normalized stages/samples/feed, UI-free)
///   3. estimation     — TestTimeEstimator (remaining time + confidence, UI-free)
///   4. presentation   — NeuralFlowVisualizer + TestStageTimeline + this window's status strip
///   5. interaction    — Run / Pause (TestRunPauseGate) / Cancel / lite-mode / details toggles
/// The visual layer only CONSUMES state: every callback funnels through the telemetry model on
/// the UI thread, and the runner never knows the visualizer exists.
/// </summary>
public partial class ChatModelBenchWindow : Window
{
    private readonly string?       _workspaceRoot;
    private readonly IModelRuntime _runtime;
    private readonly AppSettings?  _settings;

    private readonly Dictionary<string, CheckBox> _modelCheckboxes = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _runCts;

    private readonly TestRunTelemetryModel _telemetry = new();
    private readonly TestTimeEstimator     _estimator = new();
    private readonly TestRunPauseGate      _pauseGate = new();

    // Ticks only while a run is active: refreshes elapsed/remaining countdowns between samples.
    private readonly DispatcherTimer _statusTick;
    private TestTimeEstimate? _lastEstimate;
    private DateTimeOffset    _lastEstimateAtUtc;

    /// <summary>
    /// Normalized run state, exposed read-only so headless tests (and future automation)
    /// can observe the run without reaching into private UI fields.
    /// </summary>
    public TestRunTelemetryModel Telemetry => _telemetry;

    /// <param name="runtime">Overrides the Ollama-backed runtime — used by headless tests to
    /// drive a full visual run against a scripted runtime (ContextFabricScriptedRuntime
    /// discipline: never exercise a real model in tests).</param>
    public ChatModelBenchWindow(OllamaClient ollama, string? workspaceRoot = null, AppSettings? settings = null,
        IModelRuntime? runtime = null)
    {
        _workspaceRoot = workspaceRoot;
        _runtime       = runtime ?? new OllamaRuntime(ollama);
        _settings      = settings;
        InitializeComponent();

        _telemetry.Updated += OnTelemetryUpdated;
        _statusTick = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTick.Tick += (_, _) => RefreshTimeTexts();

        TglLiteMode.IsChecked = _settings?.BenchLiteMode == true;
        ApplyLiteMode();

        Opened += OnOpened;
        Closing += (_, _) =>
        {
            _runCts?.Cancel();
            _pauseGate.Resume();   // a paused run must still observe the cancel
            _statusTick.Stop();
        };
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
            // Via the runtime abstraction (not _ollama directly) so an injected test runtime
            // controls the model list too. OllamaRuntime just delegates to the client.
            models = await _runtime.GetInstalledModelsAsync();
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

    // ── Interaction controls ──────────────────────────────────────────────────

    private void BtnPause_Click(object? sender, RoutedEventArgs e)
    {
        if (_runCts is null) return;
        if (_pauseGate.IsPaused)
        {
            _pauseGate.Resume();
            _telemetry.ResumeRun();
            BtnPause.Content = "⏸  Pause";
        }
        else
        {
            _pauseGate.Pause();
            _telemetry.PauseRun();
            _telemetry.AddActivity(TestActivityKind.Info, "Holding at the next case boundary (in-flight case finishes first).");
            BtnPause.Content = "▶  Resume";
        }
    }

    private void BtnCancel_Click(object? sender, RoutedEventArgs e)
    {
        if (_runCts is null) return;
        _telemetry.BeginCancel();
        _runCts.Cancel();
        _pauseGate.Resume();   // release a paused run so cancellation propagates immediately
    }

    private void TglLiteMode_Changed(object? sender, RoutedEventArgs e)
    {
        ApplyLiteMode();
        if (_settings is not null)
        {
            _settings.BenchLiteMode = TglLiteMode.IsChecked == true;
            _settings.Save();
        }
    }

    private void ApplyLiteMode()
    {
        var lite = TglLiteMode.IsChecked == true;
        // A hidden visualizer is forced lite so its render timer stops entirely.
        Viz.LiteMode      = lite || TglShowViz.IsChecked != true;
        Timeline.LiteMode = lite;
    }

    private void TglShowViz_Changed(object? sender, RoutedEventArgs e)
    {
        if (VizHost is null || Viz is null) return;   // fires during InitializeComponent
        VizHost.IsVisible = TglShowViz.IsChecked == true;
        ApplyLiteMode();
    }

    private void TglDetails_Changed(object? sender, RoutedEventArgs e)
    {
        if (DetailStatsRow is null) return;
        DetailStatsRow.IsVisible = TglDetails.IsChecked == true;
    }

    private void BtnOpenReports_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var dir = ModelBenchReportStore.ResolveDirectory(_workspaceRoot);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Could not open report folder: {ex.Message}";
        }
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    private static string StageIdFor(string model) => $"model:{model}";

    private static string ShortModelName(string model)
    {
        // "hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF:Q4_K_M" → last path segment.
        var slash = model.LastIndexOf('/');
        return slash >= 0 && slash < model.Length - 1 ? model[(slash + 1)..] : model;
    }

    private async void BtnRun_Click(object? sender, RoutedEventArgs e)
    {
        if (_runCts is not null) return;   // already running (Run is disabled, belt-and-braces)

        var selected = _modelCheckboxes
            .Where(kv => kv.Value.IsChecked == true)
            .Select(kv => kv.Key)
            .ToList();
        if (selected.Count == 0)
        {
            TbStatus.Text = "Select at least one model to bench.";
            return;
        }

        _runCts = new CancellationTokenSource();
        SetRunControls(running: true);
        ResultsPanel.Children.Clear();

        var cases = ModelBenchCorpus.AllCases;
        var stages = new List<(string, string)> { ("init", "Initialize") };
        stages.AddRange(selected.Select(m => (StageIdFor(m), ShortModelName(m))));
        stages.Add(("report", "Save report"));

        _estimator.Reset();
        _lastEstimate = null;
        _telemetry.StartRun(stages, totalSamples: cases.Count * selected.Count);
        _telemetry.StageStarted("init", 0, "Preparing corpus and runtime…");
        _telemetry.AddActivity(TestActivityKind.Info,
            $"Corpus '{ModelBenchCorpus.DefaultCorpusName}': {cases.Count} cases × {selected.Count} model{(selected.Count == 1 ? "" : "s")}.");
        _telemetry.StageEnded("init", TestStageStatus.Completed, $"{cases.Count} cases ready");
        _statusTick.Start();

        // Per-model verdict tally for honest stage end-states (reset on each model start;
        // only ever touched on the UI thread via the posted callbacks below).
        var tally = new ModelTally();

        try
        {
            var report = await ModelBenchRunner.RunAsync(
                _runtime, selected,
                onModelStart: model => Dispatcher.UIThread.Post(() =>
                {
                    tally.Fails = tally.Errors = 0;
                    _telemetry.StageStarted(StageIdFor(model), cases.Count, $"Loading {ShortModelName(model)}…");
                }),
                onCaseStart: (model, testCase) => Dispatcher.UIThread.Post(() =>
                    _telemetry.SampleStarted($"{ShortModelName(model)} — {testCase.Category}: generating response…")),
                onCaseComplete: result => Dispatcher.UIThread.Post(() => OnCaseComplete(result, tally)),
                onModelComplete: model => Dispatcher.UIThread.Post(() =>
                {
                    var status = tally.Errors >= cases.Count      ? TestStageStatus.Failed
                        : tally.Fails + tally.Errors > 0          ? TestStageStatus.Warning
                        : TestStageStatus.Completed;
                    _telemetry.StageEnded(StageIdFor(model), status,
                        $"{cases.Count - tally.Fails - tally.Errors}/{cases.Count} ok");
                }),
                pauseGate: _pauseGate,
                ct: _runCts.Token);

            _telemetry.StageStarted("report", 0, "Writing report…");
            RenderResults(report);
            string summary;
            try
            {
                var path = await ModelBenchReportStore.WriteAsync(report, _workspaceRoot);
                _telemetry.StageEnded("report", TestStageStatus.Completed, Path.GetFileName(path));
                summary = $"Done — {selected.Count} model{(selected.Count == 1 ? "" : "s")}, {report.Results.Count} cases. Saved {Path.GetFileName(path)}.";
            }
            catch (Exception ex)
            {
                _telemetry.StageEnded("report", TestStageStatus.Warning, $"save failed: {ex.Message}");
                summary = $"Done — {report.Results.Count} cases (report save failed: {ex.Message}).";
            }

            _telemetry.EndRun(TestRunPhase.Completed, summary);
            TbStatus.Text = summary;
        }
        catch (OperationCanceledException)
        {
            _telemetry.EndRun(TestRunPhase.Cancelled,
                $"Cancelled after {_telemetry.CompletedSamples}/{_telemetry.TotalSamples} cases.");
            TbStatus.Text = "Cancelled.";
        }
        catch (Exception ex)
        {
            _telemetry.EndRun(TestRunPhase.Failed, $"Bench failed: {ex.Message}");
            TbStatus.Text = $"Bench failed: {ex.Message}";
        }
        finally
        {
            _statusTick.Stop();
            RefreshTimeTexts();     // final frozen values
            _pauseGate.Resume();
            _runCts?.Dispose();
            _runCts = null;
            SetRunControls(running: false);
        }
    }

    /// <summary>Per-model fail/error tally, mutated only on the UI thread.</summary>
    private sealed class ModelTally
    {
        public int Fails;
        public int Errors;
    }

    private void OnCaseComplete(ModelBenchCaseResult result, ModelTally tally)
    {
        _estimator.AddSample(result.Duration);
        _lastEstimate      = _estimator.GetEstimate(_telemetry.TotalSamples - _telemetry.CompletedSamples - 1);
        _lastEstimateAtUtc = DateTimeOffset.UtcNow;

        var (kind, errored) = result.Verdict switch
        {
            ModelBenchVerdict.Pass    => (TestActivityKind.Success, false),
            ModelBenchVerdict.Refused => (TestActivityKind.Warning, false),
            ModelBenchVerdict.Fail    => (TestActivityKind.Failure, false),
            _                         => (TestActivityKind.Failure, true),
        };
        if (errored) tally.Errors++;
        else if (kind != TestActivityKind.Success) tally.Fails++;

        // Feed only non-pass outcomes (plus errors) so the feed stays signal, not a wall of ✓.
        string? feedMessage = result.Verdict switch
        {
            ModelBenchVerdict.Pass    => null,
            ModelBenchVerdict.Refused => $"{ShortModelName(result.Model)} refused '{result.TestCase.CaseId}' ({result.Duration.TotalSeconds:0.0}s)",
            ModelBenchVerdict.Fail    => $"{ShortModelName(result.Model)} failed '{result.TestCase.CaseId}' ({result.Duration.TotalSeconds:0.0}s)",
            _                         => $"{ShortModelName(result.Model)} error on '{result.TestCase.CaseId}': {Truncate(result.ErrorMessage, 80)}",
        };
        _telemetry.SampleCompleted(kind, errored, feedMessage);
        Viz.PulseSample(kind);
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : s.Length <= max ? s : s[..max] + "…";

    private void SetRunControls(bool running)
    {
        BtnRun.IsEnabled    = !running;
        BtnPause.IsEnabled  = running;
        BtnCancel.IsEnabled = running;
        BtnPause.Content    = "⏸  Pause";
        BtnSelectAll.IsEnabled = BtnSelectNone.IsEnabled = !running;
        foreach (var cb in _modelCheckboxes.Values) cb.IsEnabled = !running;
    }

    // ── Telemetry → presentation ─────────────────────────────────────────────

    private void OnTelemetryUpdated()
    {
        Viz.SetState(new NeuralFlowVisualizer.FlowState(
            _telemetry.Phase,
            _telemetry.TotalSamples,
            _telemetry.CompletedSamples,
            _telemetry.PassedSamples,
            _telemetry.WarningSamples,
            _telemetry.FailedSamples,
            _telemetry.CurrentOperation,
            _telemetry.Phase == TestRunPhase.Failed ? _telemetry.CurrentOperation : null));
        Timeline.SetStages(_telemetry.Stages);

        TbOperation.Text = _telemetry.Phase == TestRunPhase.Idle
            ? "Idle — pick models on the left and press Run Bench."
            : _telemetry.CurrentOperation;
        TbOperation.Foreground = new SolidColorBrush(_telemetry.Phase switch
        {
            TestRunPhase.Failed    => Color.FromRgb(0xF4, 0x47, 0x47),
            TestRunPhase.Completed => Color.FromRgb(0x76, 0xB9, 0x00),
            _                      => Color.FromRgb(0x7A, 0x8A, 0x6A),
        });

        TbPhase.Text = _telemetry.Phase switch
        {
            TestRunPhase.Idle       => "Idle",
            TestRunPhase.Running    => "Running",
            TestRunPhase.Paused     => "Paused",
            TestRunPhase.Cancelling => "Cancelling…",
            TestRunPhase.Completed  => "Completed",
            TestRunPhase.Failed     => "Failed",
            TestRunPhase.Cancelled  => "Cancelled",
            _                       => _telemetry.Phase.ToString(),
        };
        TbProgress.Text = _telemetry.TotalSamples == 0
            ? "—"
            : $"{_telemetry.CompletedSamples}/{_telemetry.TotalSamples}  ({_telemetry.FractionComplete * 100:0}%)";
        TbVerdicts.Text = _telemetry.TotalSamples == 0
            ? "—"
            : $"{_telemetry.PassedSamples} / {_telemetry.WarningSamples} / {_telemetry.FailedSamples} / {_telemetry.ErrorSamples}";

        RefreshTimeTexts();
        RefreshFeed();
    }

    private void RefreshTimeTexts()
    {
        TbElapsed.Text = _telemetry.RunStartedUtc is null ? "—" : FormatSpan(_telemetry.Elapsed);

        if (_telemetry.Phase is TestRunPhase.Completed or TestRunPhase.Cancelled or TestRunPhase.Failed)
        {
            TbRemaining.Text = _telemetry.Phase == TestRunPhase.Completed ? "0:00" : "—";
            TbEta.Text = "—";
        }
        else if (_telemetry.Phase is TestRunPhase.Idle || _lastEstimate is null)
        {
            TbRemaining.Text = _telemetry.IsRunActive ? "Calculating…" : "—";
            TbEta.Text = "—";
        }
        else if (_lastEstimate.Remaining is null)
        {
            TbRemaining.Text = "Calculating…";
            TbEta.Text = "—";
        }
        else
        {
            // Count down between samples: published estimate minus wall time since it was made.
            // A stalled case bottoms out at 0:00 rather than inventing new precision.
            var sinceEstimate = _telemetry.Phase == TestRunPhase.Paused
                ? TimeSpan.Zero
                : DateTimeOffset.UtcNow - _lastEstimateAtUtc;
            var shown = _lastEstimate.Remaining.Value - sinceEstimate;
            if (shown < TimeSpan.Zero) shown = TimeSpan.Zero;
            var conf = _lastEstimate.Confidence switch
            {
                TestEstimateConfidence.Low    => "±low",
                TestEstimateConfidence.Medium => "±med",
                TestEstimateConfidence.High   => "±high",
                _                             => "…",
            };
            TbRemaining.Text = $"≈{FormatSpan(shown)}  {conf}";
            TbEta.Text = _telemetry.Phase == TestRunPhase.Paused
                ? "paused"
                : (DateTimeOffset.Now + shown).ToString("HH:mm:ss");
        }

        var avg    = _lastEstimate?.AveragePerSample;
        var recent = _lastEstimate?.RecentPerSample;
        TbAvgCase.Text    = avg is null ? "—" : $"{avg.Value.TotalSeconds:0.0}s";
        TbRecentCase.Text = recent is null ? "—" : $"{recent.Value.TotalSeconds:0.0}s";
        TbThroughput.Text = avg is { TotalSeconds: > 0.001 }
            ? $"{60 / avg.Value.TotalSeconds:0.0} cases/min"
            : "—";
        TbBasis.Text = _lastEstimate?.Basis ?? "—";
    }

    private static string FormatSpan(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    private void RefreshFeed()
    {
        // Rebuild the visible tail (per-case cadence, not per-frame, so this stays cheap).
        const int visible = 60;
        FeedPanel.Children.Clear();
        var entries = _telemetry.Activity;
        for (var i = Math.Max(0, entries.Count - visible); i < entries.Count; i++)
        {
            var entry = entries[i];
            var (glyph, color) = entry.Kind switch
            {
                TestActivityKind.Success => ("✓", Color.FromRgb(0x76, 0xB9, 0x00)),
                TestActivityKind.Warning => ("!", Color.FromRgb(0xCC, 0xA7, 0x00)),
                TestActivityKind.Failure => ("✕", Color.FromRgb(0xF4, 0x47, 0x47)),
                _                        => ("·", Color.FromRgb(0x7A, 0x8A, 0x6A)),
            };
            FeedPanel.Children.Add(new TextBlock
            {
                Text = $"{entry.TimestampUtc.ToLocalTime():HH:mm:ss}  {glyph}  {entry.Message}",
                Foreground = new SolidColorBrush(color),
                FontSize = 10.5,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        FeedScroll.ScrollToEnd();
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
