// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BenchmarkRunner.Core;
using BenchmarkRunner.Models;
using Microsoft.Win32;

namespace BenchmarkRunner;

public partial class MainWindow : Window
{
    private readonly List<TestRun> _runs;
    private TestRun? _currentRun;
    private int _selectedSlots = 3;
    private string _selectedTrust = "Standard";

    // Predefined model suggestions
    private static readonly string[] KnownModels =
    [
        "qwen2.5-coder:14b",
        "gemma4:12b",
        "nemotron-3-nano:4b-q8_0",
        "devstral:24b",
        "qwen2.5-coder:7b",
        "mistral:7b",
    ];

    public MainWindow()
    {
        InitializeComponent();
        _runs = RunHistory.Load();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Populate benchmark dropdown
        CbBenchmark.ItemsSource = BenchmarkDef.All.Select(b => b.Name).ToList();
        CbBenchmark.SelectedIndex = 0;

        // Populate model combos
        CbBossModel.ItemsSource       = KnownModels;
        CbWorkerModel.ItemsSource     = KnownModels;
        CbResearcherModel.ItemsSource = KnownModels;

        // Load current settings
        try
        {
            var (boss, worker, researcher, slots, trust, ws) = SettingsPatcher.Read();
            CbBossModel.Text       = boss;
            CbWorkerModel.Text     = worker;
            CbResearcherModel.Text = researcher;
            TbWorkspace.Text       = ws;
            SetSlots(slots);
            SetTrust(trust);

            TbSettingsPath.Text = $"settings.json at %APPDATA%\\OrchestratorIDE";
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load settings: {ex.Message}");
        }

        RefreshHistory();
    }

    // ── Benchmark selection ───────────────────────────────────────────────────

    private void CbBenchmark_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CbBenchmark.SelectedIndex < 0) return;
        var def = BenchmarkDef.All[CbBenchmark.SelectedIndex];
        TbBenchmarkHint.Text = def.Description;
    }

    // ── Slot buttons ──────────────────────────────────────────────────────────

    private void BtnSlot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        _selectedSlots = int.Parse(btn.Tag!.ToString()!);
        SetSlots(_selectedSlots);
    }

    private void SetSlots(int n)
    {
        _selectedSlots = n;
        BtnSlot1.IsChecked = n == 1;
        BtnSlot2.IsChecked = n == 2;
        BtnSlot3.IsChecked = n == 3;
        BtnSlot4.IsChecked = n == 4;
        TbSlotsHint.Text   = n < 3 ? "⚠ Swarm needs ≥ 3 slots" : "Swarm needs ≥ 3";
        TbSlotsHint.Foreground = n < 3
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));
    }

    // ── Trust buttons ─────────────────────────────────────────────────────────

    private void BtnTrust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton btn) return;
        _selectedTrust = btn.Tag!.ToString()!;
        SetTrust(_selectedTrust);
    }

    private void SetTrust(string trust)
    {
        _selectedTrust          = trust;
        BtnTrustPlan.IsChecked     = trust == "Plan";
        BtnTrustGuarded.IsChecked  = trust == "Guarded";
        BtnTrustStandard.IsChecked = trust == "Standard";
        BtnTrustFullAuto.IsChecked = trust == "FullAuto";
    }

    // ── Browse workspace ──────────────────────────────────────────────────────

    private void BtnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title           = "Select Workspace Folder",
            InitialDirectory = TbWorkspace.Text.Length > 0
                ? TbWorkspace.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (dlg.ShowDialog() == true)
            TbWorkspace.Text = dlg.FolderName;
    }

    // ── Configure button ─────────────────────────────────────────────────────

    private void BtnConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplySettings(out var error, out _))
        {
            MessageBox.Show(error, "Configure Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        SetStatus("Settings written to settings.json — launch TheOrc manually.");
    }

    // ── Configure + Launch button ─────────────────────────────────────────────

    private void BtnLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (!TryApplySettings(out var error, out var researcherModel))
        {
            MessageBox.Show(error, "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Find and launch TheOrc exe
        // BaseDirectory = Tools\BenchmarkRunner\bin\Debug\net10.0-windows\ → 5 levels up = repo root
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "OrchestratorIDE", "bin", "Debug", "net10.0-windows", "OrchestratorIDE.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "OrchestratorIDE", "bin", "Release", "net10.0-windows", "OrchestratorIDE.exe"),
        };

        var exePath = candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
        if (exePath is null)
        {
            MessageBox.Show(
                "Could not find OrchestratorIDE.exe — build the main project first.\n\nSearched:\n" +
                string.Join("\n", candidates.Select(Path.GetFullPath)),
                "Exe Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        SetStatus($"Launched: {exePath}");

        // Start a new run record
        _currentRun = new TestRun
        {
            BenchmarkId     = BenchmarkDef.All[CbBenchmark.SelectedIndex].Id,
            BenchmarkName   = BenchmarkDef.All[CbBenchmark.SelectedIndex].Name,
            BossModel       = CbBossModel.Text.Trim(),
            WorkerModel     = CbWorkerModel.Text.Trim(),
            ResearcherModel = researcherModel,
            Slots           = _selectedSlots,
            TrustLevel      = _selectedTrust,
            Workspace       = TbWorkspace.Text.Trim(),
            StartedAt       = DateTime.Now,
        };
        TbHeaderStatus.Text = $"Run started: {_currentRun.BenchmarkName} @ {_currentRun.StartedAt:HH:mm}";
    }

    // ── Scan results ──────────────────────────────────────────────────────────

    private void BtnScan_Click(object sender, RoutedEventArgs e)
    {
        var ws = TbWorkspace.Text.Trim();
        if (string.IsNullOrWhiteSpace(ws) || !Directory.Exists(ws))
        {
            MessageBox.Show("Set a valid workspace folder first.", "Workspace Missing",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetStatus("Scanning…");
        try
        {
            var result = ResultScanner.Scan(ws);
            RenderChecklist(result);
            TbScanPath.Text = result.RunDir.Length > 0
                ? $"Run dir: {result.RunDir}"
                : "No run dirs found in workspace/.orc/swarm/runs/";

            var autoScore = result.AutoScore;
            TbAutoScore.Foreground = autoScore >= 80
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : autoScore >= 50
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33));
            TbAutoScore.Text = $"{autoScore}/100 auto-scored";

            if (_currentRun is not null)
                _currentRun.ScanResult = result;

            SetStatus($"Scan complete. Auto-score: {autoScore}/100");
        }
        catch (Exception ex)
        {
            SetStatus($"Scan error: {ex.Message}");
        }
    }

    // ── View / Copy prompt ────────────────────────────────────────────────────

    private void BtnViewPrompt_Click(object sender, RoutedEventArgs e)
    {
        if (CbBenchmark.SelectedIndex < 0) return;
        var def = BenchmarkDef.All[CbBenchmark.SelectedIndex];

        var dlg = new PromptViewer(def.Name, def.Prompt);
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    // ── Save run ──────────────────────────────────────────────────────────────

    private void BtnSaveRun_Click(object sender, RoutedEventArgs e)
    {
        _currentRun ??= new TestRun
        {
            BenchmarkId     = CbBenchmark.SelectedIndex >= 0 ? BenchmarkDef.All[CbBenchmark.SelectedIndex].Id   : "",
            BenchmarkName   = CbBenchmark.SelectedIndex >= 0 ? BenchmarkDef.All[CbBenchmark.SelectedIndex].Name : "",
            BossModel       = CbBossModel.Text.Trim(),
            WorkerModel     = CbWorkerModel.Text.Trim(),
            ResearcherModel = CbResearcherModel.Text.Trim(),
            Slots           = _selectedSlots,
            TrustLevel      = _selectedTrust,
            Workspace       = TbWorkspace.Text.Trim(),
        };

        if (int.TryParse(TbManualScore.Text.Trim(), out var ms) && ms >= 0 && ms <= 100)
            _currentRun.ManualScore = ms;

        _currentRun.Notes = TbNotes.Text.Trim();

        RunHistory.AddOrUpdate(_runs, _currentRun);
        RefreshHistory();
        SetStatus($"Saved run: {_currentRun.BenchmarkName} — {_currentRun.DisplayScore}/100 ({_currentRun.Status})");
    }

    // ── History ───────────────────────────────────────────────────────────────

    private void RefreshHistory()
    {
        DgHistory.ItemsSource = null;
        DgHistory.ItemsSource = _runs;
    }

    private void DgHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DgHistory.SelectedItem is not TestRun run) return;
        _currentRun = run;

        // Reload config fields from selected run
        CbBossModel.Text       = run.BossModel;
        CbWorkerModel.Text     = run.WorkerModel;
        CbResearcherModel.Text = string.IsNullOrWhiteSpace(run.ResearcherModel) ? run.WorkerModel : run.ResearcherModel;
        TbWorkspace.Text       = run.Workspace;
        SetSlots(run.Slots);
        SetTrust(run.TrustLevel);
        TbManualScore.Text = run.ManualScore?.ToString() ?? "";
        TbNotes.Text       = run.Notes;

        var benchIdx = Enumerable.Range(0, BenchmarkDef.All.Count).FirstOrDefault(i => BenchmarkDef.All[i].Id == run.BenchmarkId, -1);
        if (benchIdx >= 0) CbBenchmark.SelectedIndex = benchIdx;

        if (run.ScanResult is not null)
            RenderChecklist(run.ScanResult);
    }

    private void BtnClearHistory_Click(object sender, RoutedEventArgs e)
    {
        var r = MessageBox.Show("Clear all run history? This cannot be undone.", "Clear History",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (r != MessageBoxResult.Yes) return;
        _runs.Clear();
        RunHistory.Save(_runs);
        RefreshHistory();
        SetStatus("Run history cleared.");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private bool TryApplySettings(out string error, out string resolvedResearcher)
    {
        var boss       = CbBossModel.Text.Trim();
        var worker     = CbWorkerModel.Text.Trim();
        var researcher = CbResearcherModel.Text.Trim();
        var ws         = TbWorkspace.Text.Trim();
        resolvedResearcher = string.IsNullOrWhiteSpace(researcher) ? worker : researcher;

        if (string.IsNullOrWhiteSpace(boss))   { error = "Boss model is required.";   return false; }
        if (string.IsNullOrWhiteSpace(worker)) { error = "Coder model is required.";  return false; }

        if (!SettingsPatcher.SettingsExist)
        {
            error = "settings.json not found — launch TheOrc at least once first.";
            return false;
        }

        try
        {
            SettingsPatcher.Apply(boss, worker, _selectedSlots, _selectedTrust, ws, "swarm", resolvedResearcher);
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void RenderChecklist(ResultCheck r)
    {
        SpChecklist.Children.Clear();
        SpScoreBreakdown.Children.Clear();
        SpScoreBreakdown.Visibility = Visibility.Collapsed;

        var checks = ResultScanner.CheckList(r).ToList();
        foreach (var (label, found, points) in checks)
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };

            var dot = new TextBlock
            {
                Text       = found ? "✔" : "✘",
                Foreground = found
                    ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33)),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 13,
                Width      = 20,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var pts = new TextBlock
            {
                Text       = $"+{points}",
                Foreground = found
                    ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                Width      = 30,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(pts, Dock.Right);

            var lbl = new TextBlock
            {
                Text       = label,
                Foreground = found
                    ? new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8))
                    : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                FontSize   = 11,
                Margin     = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            row.Children.Add(dot);
            row.Children.Add(pts);
            row.Children.Add(lbl);
            SpChecklist.Children.Add(row);
        }

        // Total row
        var total = new TextBlock
        {
            Text       = $"AUTO-SCORE: {r.AutoScore} / 100",
            Foreground = r.AutoScore >= 80
                ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
                : r.AutoScore >= 50
                    ? new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0xCC, 0x33, 0x33)),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 13,
            FontWeight = FontWeights.Bold,
            Margin     = new Thickness(0, 12, 0, 0),
        };
        SpChecklist.Children.Add(total);

        if (r.RunDir.Length > 0)
        {
            SpChecklist.Children.Add(new TextBlock
            {
                Text       = r.RunDir,
                Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                FontSize   = 9,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 6, 0, 0),
            });
        }
    }

    private void SetStatus(string msg) => TbStatus.Text = msg;
}
