// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.Models;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Visually rich capability-test dialog.
///
/// Shows the user exactly what the model is doing at every stage:
///   Phase strip  — Idle → Sending → Model thinking → Received → Analyzing → Done
///   Test cards   — one card per test; live state (Queued/Running/Pass/Fail/Partial)
///   Activity feed — timestamped, color-coded event log
///
/// ModelCapabilityTestService emits structured [PHASE:…] / [RESULT:…] tokens
/// that drive the phase strip and card states; plain text is shown in the feed.
/// </summary>
public partial class ModelCapabilityTestDialog : Window
{
    // ── State ─────────────────────────────────────────────────────────────────

    private readonly AppSettings           _settings;
    private CancellationTokenSource?       _cts;
    private readonly DispatcherTimer       _clockTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private DateTime                       _runStart;
    private readonly List<TestCardState>   _cards  = [];

    // ── Colors ────────────────────────────────────────────────────────────────

    private static readonly Color CActive   = Color.FromArgb(0xFF, 0x76, 0xB9, 0x00);  // NVIDIA green
    private static readonly Color CInactive = Color.FromArgb(0xFF, 0x22, 0x2A, 0x22);
    private static readonly Color CPass     = Color.FromArgb(0xFF, 0x4A, 0xCA, 0x4A);
    private static readonly Color CFail     = Color.FromArgb(0xFF, 0xE8, 0x40, 0x40);
    private static readonly Color CPartial  = Color.FromArgb(0xFF, 0xE8, 0xA0, 0x30);
    private static readonly Color CRunning  = Color.FromArgb(0xFF, 0x76, 0xB9, 0x00);
    private static readonly Color CMuted    = Color.FromArgb(0xFF, 0x7A, 0x8A, 0x6A);
    private static readonly Color CWhite    = Color.FromArgb(0xFF, 0xD4, 0xD4, 0xD4);

    // ── Construction ──────────────────────────────────────────────────────────

    public ModelCapabilityTestDialog(AppSettings settings, string? prefilledModelId = null)
    {
        InitializeComponent();
        _settings = settings;

        if (!string.IsNullOrWhiteSpace(prefilledModelId))
            TbModelId.Text = prefilledModelId;

        // Elapsed clock timer
        _clockTimer.Tick += (_, _) =>
        {
            if (_runStart != default)
                TbElapsed.Text = $"⏱ {(DateTime.UtcNow - _runStart).TotalSeconds:F1}s";
        };

        // Paint all phases inactive on startup
        SetAllPhasesInactive();
    }

    // ── Run ───────────────────────────────────────────────────────────────────

    private async void BtnRun_Click(object sender, RoutedEventArgs e)
    {
        var modelId = TbModelId.Text.Trim();
        if (string.IsNullOrWhiteSpace(modelId))
        {
            SetStatus("⚠ Enter a model ID first.");
            return;
        }

        var selectedTag = (CbTestLevel.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
        var testIds = selectedTag == "All"
            ? new[] { ModelCapabilityTestService.TestIdSmall,
                      ModelCapabilityTestService.TestIdMedium,
                      ModelCapabilityTestService.TestIdLarge }
            : new[] { selectedTag };

        // Reset UI
        BtnRun.IsEnabled   = false;
        BtnCancel.IsEnabled = true;
        PnlLog.Children.Clear();
        _cards.Clear();
        PnlTestCards.Children.Clear();
        SetAllPhasesInactive();
        TbElapsed.Text = "";
        TbPhaseDone.Text = "Done";

        // Build test cards
        foreach (var tid in testIds)
            AddTestCard(tid);

        // Start elapsed clock
        _runStart = DateTime.UtcNow;
        _clockTimer.Start();

        _cts = new CancellationTokenSource();
        var results = new List<ModelCapabilityTestResult>();

        try
        {
            var host = _settings.OllamaHost ?? "http://192.168.1.15:11434";
            var passed = 0;

            for (int i = 0; i < testIds.Length; i++)
            {
                var testId = testIds[i];
                SetCardState(testId, CardState.Running);
                AddFeedSeparator(testId);
                SetStatus($"Running {testId} ({i + 1}/{testIds.Length})…");

                var progress = new Progress<string>(msg => Dispatcher.Invoke(() =>
                    HandleProgressMessage(msg, testId)));

                try
                {
                    var result = await ModelCapabilityTestService.RunAsync(
                        modelId, testId, host, progress, _cts.Token);

                    results.Add(result);

                    var state = result.Result switch
                    {
                        "pass"    => CardState.Pass,
                        "partial" => CardState.Partial,
                        _         => CardState.Fail,
                    };
                    SetCardState(testId, state, result);
                    if (result.Result == "pass") passed++;
                }
                catch (OperationCanceledException)
                {
                    SetCardState(testId, CardState.Fail);
                    AddFeedLine("■ Cancelled.", CMuted);
                    break;
                }
                catch (Exception ex)
                {
                    SetCardState(testId, CardState.Fail);
                    AddFeedLine($"✗ Error: {ex.Message}", CFail);
                }
            }

            // Final phase — done
            var allPass = results.All(r => r.Result == "pass");
            var anyFail = results.Any(r => r.Result == "fail");
            SetPhaseActive(PhaseDone, allPass ? CPass : anyFail ? CFail : CPartial);
            TbPhaseDone.Text = allPass ? "✅ All passed"
                             : anyFail ? "❌ Failures"
                             : "⚠ Partial";

            var totalS = (DateTime.UtcNow - _runStart).TotalSeconds;
            SetStatus($"Done — {passed}/{results.Count} passed  ·  {totalS:F1}s total");
        }
        finally
        {
            _clockTimer.Stop();
            BtnRun.IsEnabled    = true;
            BtnCancel.IsEnabled = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("Cancelling…");
        BtnCancel.IsEnabled = false;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    // ── Progress message parsing ──────────────────────────────────────────────

    private void HandleProgressMessage(string msg, string testId)
    {
        // Structured control tokens
        if (msg.StartsWith("[PHASE:sending]"))
        {
            SetAllPhasesInactive();
            SetPhaseActive(PhaseSending);
            SetPhaseActive(PhaseWaiting, Color.FromArgb(0x44, 0x76, 0xB9, 0x00));
            AddFeedLine($"  📡 {StripTag(msg)}", CMuted, timestamp: true);
            return;
        }
        if (msg.StartsWith("[PHASE:received:"))
        {
            var bytes = ParseTagInt(msg, "PHASE:received:");
            SetPhaseActive(PhaseReceived);
            TbPhaseReceived.Text = $"📥  {bytes:N0} chars";
            AddFeedLine($"  📥 {StripTag(msg)}", CWhite, timestamp: true);
            return;
        }
        if (msg.StartsWith("[PHASE:analyzing]"))
        {
            SetPhaseActive(PhaseAnalyzing);
            AddFeedLine($"  🔍 {StripTag(msg)}", CMuted, timestamp: true);
            return;
        }
        if (msg.StartsWith("[RESULT:pass]"))
        {
            AddFeedLine($"  ✅ PASS", CPass, bold: true, timestamp: true);
            return;
        }
        if (msg.StartsWith("[RESULT:partial]"))
        {
            AddFeedLine($"  ⚠  PARTIAL", CPartial, bold: true, timestamp: true);
            return;
        }
        if (msg.StartsWith("[RESULT:fail]"))
        {
            AddFeedLine($"  ❌ FAIL", CFail, bold: true, timestamp: true);
            return;
        }

        // Classify plain messages by content
        var (text, color) = ClassifyMessage(msg);
        AddFeedLine($"  {text}", color, timestamp: false);
    }

    private static (string text, Color color) ClassifyMessage(string msg)
    {
        if (msg.Contains("✅") || msg.Contains("written") && !msg.Contains("not"))
            return (msg, CPass);
        if (msg.Contains("❌") || msg.Contains("TRUNCATED") || msg.Contains("not written"))
            return (msg, CFail);
        if (msg.Contains("truncated", StringComparison.OrdinalIgnoreCase))
            return (msg, CPartial);
        if (msg.Contains("Error", StringComparison.OrdinalIgnoreCase))
            return (msg, CFail);
        return (msg, CMuted);
    }

    private static string StripTag(string msg)
    {
        // Remove [TAG:value] prefix
        var bracket = msg.IndexOf(']');
        return bracket >= 0 ? msg[(bracket + 1)..].TrimStart() : msg;
    }

    private static long ParseTagInt(string msg, string prefix)
    {
        try
        {
            var start = msg.IndexOf(prefix) + prefix.Length;
            var end   = msg.IndexOf(']', start);
            if (start > 0 && end > start && long.TryParse(msg[start..end], out var v)) return v;
        }
        catch { /* ok */ }
        return 0;
    }

    // ── Test cards ────────────────────────────────────────────────────────────

    private enum CardState { Queued, Running, Pass, Fail, Partial }

    private record TestCardState(
        string TestId,
        Border  CardBorder,
        TextBlock StateBadge,
        TextBlock MetricsText,
        Border StateBorder
    );

    private void AddTestCard(string testId)
    {
        var label = testId switch
        {
            "FileWriteSmall"  => "Small\n~20 chars",
            "FileWriteMedium" => "Medium\n~1.5 KB",
            "FileWriteLarge"  => "Large\n~5 KB",
            _                 => testId,
        };

        // Card border
        var card = new Border
        {
            Width           = 160,
            Background      = new SolidColorBrush(CInactive),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x3A, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 0, 8, 0),
        };

        var stack = new StackPanel();

        // State badge
        var stateBorder = new Border
        {
            CornerRadius    = new CornerRadius(2),
            Padding         = new Thickness(6, 2, 6, 2),
            Margin          = new Thickness(0, 0, 0, 6),
            Background      = new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x66, 0x66, 0x66)),
            BorderThickness = new Thickness(1),
        };
        var stateBadge = new TextBlock
        {
            Text       = "QUEUED",
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 10,
            FontWeight = FontWeights.Bold,
        };
        stateBorder.Child = stateBadge;
        stack.Children.Add(stateBorder);

        // Label
        stack.Children.Add(new TextBlock
        {
            Text         = label,
            Foreground   = new SolidColorBrush(CWhite),
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize     = 12,
            FontWeight   = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        // Metrics
        var metrics = new TextBlock
        {
            Text       = "—",
            Foreground = new SolidColorBrush(CMuted),
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            TextWrapping = TextWrapping.Wrap,
        };
        stack.Children.Add(metrics);

        card.Child = stack;
        PnlTestCards.Children.Add(card);

        _cards.Add(new TestCardState(testId, card, stateBadge, metrics, stateBorder));
    }

    private void SetCardState(string testId, CardState state,
                              ModelCapabilityTestResult? result = null)
    {
        var card = _cards.FirstOrDefault(c => c.TestId == testId);
        if (card == null) return;

        var (badgeText, badgeColor, borderColor, bgColor) = state switch
        {
            CardState.Running => ("RUNNING", CRunning, CActive, Color.FromArgb(0xFF, 0x0D, 0x18, 0x0D)),
            CardState.Pass    => ("PASS",    CPass,    CPass,   Color.FromArgb(0xFF, 0x07, 0x14, 0x07)),
            CardState.Fail    => ("FAIL",    CFail,    CFail,   Color.FromArgb(0xFF, 0x18, 0x07, 0x07)),
            CardState.Partial => ("PARTIAL", CPartial, CPartial,Color.FromArgb(0xFF, 0x18, 0x12, 0x05)),
            _                 => ("QUEUED",  CMuted,   Color.FromArgb(0xFF, 0x66, 0x66, 0x66), CInactive),
        };

        card.CardBorder.Background  = new SolidColorBrush(bgColor);
        card.CardBorder.BorderBrush = new SolidColorBrush(borderColor);
        card.StateBadge.Text        = badgeText;
        card.StateBadge.Foreground  = new SolidColorBrush(badgeColor);
        card.StateBorder.Background = new SolidColorBrush(
            Color.FromArgb(0x33, badgeColor.R, badgeColor.G, badgeColor.B));
        card.StateBorder.BorderBrush = new SolidColorBrush(badgeColor);

        if (result != null)
        {
            var parts = new List<string>();
            if (result.ActualFileSizeBytes > 0)
                parts.Add($"{result.ActualFileSizeBytes:N0}B");
            parts.Add($"JSON:{(result.ValidJson ? "✓" : "✗")}");
            parts.Add($"File:{(result.FileWritten ? "✓" : "✗")}");
            if (result.Truncated) parts.Add("TRUNC!");
            card.MetricsText.Text       = string.Join("  ", parts);
            card.MetricsText.Foreground = new SolidColorBrush(badgeColor);
        }
        else if (state == CardState.Running)
        {
            card.MetricsText.Text       = "● running…";
            card.MetricsText.Foreground = new SolidColorBrush(CRunning);
        }
    }

    // ── Phase strip ───────────────────────────────────────────────────────────

    private void SetAllPhasesInactive()
    {
        foreach (var phase in new[] { PhaseIdle, PhaseSending, PhaseWaiting,
                                      PhaseReceived, PhaseAnalyzing, PhaseDone })
        {
            phase.Background = new SolidColorBrush(CInactive);
            phase.BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x2A, 0x3A, 0x2A));
            phase.BorderThickness = new Thickness(1);
            if (phase.Child is TextBlock tb)
                tb.Foreground = new SolidColorBrush(CMuted);
        }
        // Idle always shows as dim-active when not running
        PhaseIdle.Background  = new SolidColorBrush(Color.FromArgb(0xFF, 0x14, 0x20, 0x14));
        if (PhaseIdle.Child is TextBlock idle)
            idle.Foreground = new SolidColorBrush(CMuted);
    }

    private void SetPhaseActive(Border phase,
                                Color? overrideColor = null)
    {
        var col = overrideColor ?? CActive;
        phase.Background  = new SolidColorBrush(
            Color.FromArgb(0x22, col.R, col.G, col.B));
        phase.BorderBrush = new SolidColorBrush(col);
        phase.BorderThickness = new Thickness(1);
        if (phase.Child is TextBlock tb)
            tb.Foreground = new SolidColorBrush(col);
    }

    // ── Activity feed ─────────────────────────────────────────────────────────

    private void AddFeedSeparator(string testId)
    {
        var label = testId switch
        {
            "FileWriteSmall"  => "FileWrite Small  (~20 chars)",
            "FileWriteMedium" => "FileWrite Medium (~1.5 KB)",
            "FileWriteLarge"  => "FileWrite Large  (~5 KB)",
            _                 => testId,
        };

        PnlLog.Children.Add(new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0xFF, 0x0D, 0x18, 0x0D)),
            BorderBrush     = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x3E, 0x1E)),
            BorderThickness = new Thickness(0, 1, 0, 1),
            Padding         = new Thickness(0, 4, 0, 4),
            Margin          = new Thickness(0, 8, 0, 4),
            Child           = new TextBlock
            {
                Text       = $"━━  {label}  ━━",
                Foreground = new SolidColorBrush(CActive),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 11,
                FontWeight = FontWeights.SemiBold,
            },
        });
        LogScroll.ScrollToEnd();
    }

    private void AddFeedLine(string text, Color color,
                             bool bold = false, bool timestamp = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };

        if (timestamp)
        {
            row.Children.Add(new TextBlock
            {
                Text       = $"{DateTime.Now:HH:mm:ss.f}  ",
                Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x4A, 0x3A)),
                FontFamily = new FontFamily("Consolas"),
                FontSize   = 10,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        row.Children.Add(new TextBlock
        {
            Text         = text,
            Foreground   = new SolidColorBrush(color),
            FontFamily   = new FontFamily("Consolas"),
            FontSize     = 11,
            FontWeight   = bold ? FontWeights.Bold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
        });

        PnlLog.Children.Add(row);
        LogScroll.ScrollToEnd();
    }

    // ── Status / helpers ──────────────────────────────────────────────────────

    private void SetStatus(string text) => TbStatus.Text = text;

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _cts?.Cancel();
        base.OnClosed(e);
    }
}
