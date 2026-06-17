// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Pit Boss training wizard — 8-question Q&amp;A → TrainingPlan → Forge handoff.
/// Phase 3C (Avalonia port): Dispatcher.Invoke → Dispatcher.UIThread.InvokeAsync;
/// FindResource → null (inline styling); Visibility → IsVisible.
/// </summary>
public partial class PitBossPanel : UserControl
{
    // ── Public properties ─────────────────────────────────────────────────────
    public string WorkspaceRoot { get; set; } = "";
    public string OllamaHost    { get; set; } = "http://localhost:11434";
    public string OllamaModel   { get; set; } = "hf.co/NousResearch/Hermes-3-Llama-3.2-3B-GGUF:Q4_K_M";
    public Services.Data.RunRepository?  RunRepo  { get; set; }
    public Services.Data.PlanRepository? PlanRepo { get; set; }

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action?                       BackRequested;
    public event Action<TrainingPlan, string>? ForgeHandoff;
    public event Action<string>?               StatusChanged;

    // ── Internal state ────────────────────────────────────────────────────────
    private PitBossService?                          _svc;
    private PlanExecutorService?                     _executor;
    private readonly List<(string role, string text)> _history = [];
    private int                _round    = 0;
    private bool               _thinking = false;
    private TrainingPlan?      _plan     = null;
    private CancellationTokenSource _cts = new();

    private static readonly (string Label, string Value)[] _goalChips =
    [
        ("code review",       "I want to become a better code reviewer"),
        ("C# expert",         "I want deeper understanding of C# and .NET patterns"),
        ("delegate tasks",    "I want to delegate and break down tasks like a senior engineer"),
        ("Python mastery",    "I want stronger Python skills including libraries and idioms"),
        ("SQL & databases",   "I want to write better SQL queries and understand database design"),
        ("test writer",       "I want to write thorough, meaningful unit and integration tests"),
        ("technical writer",  "I want to write clearer documentation, comments, and READMEs"),
        ("architect",         "I want to think like a software architect — design patterns, trade-offs, scale"),
        ("philosopher",       "I want to reason and explain ideas like a Socratic philosopher"),
        ("custom persona",    "I want to train a completely custom persona or style"),
    ];

    // ── Construction ──────────────────────────────────────────────────────────

    public PitBossPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _svc = new PitBossService(OllamaHost, OllamaModel);
        TbModelLabel.Text = OllamaModel;
        ModelDot.Fill     = new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x2A));

        PopulateGoalChips();

        var saved = PlanRepo?.LoadAll();
        if (saved is { Count: > 0 })
            ShowHistory(saved);
        else
        {
            ShowWizard();
            _ = StartConversationAsync();
        }
    }

    // ── Conversation start ────────────────────────────────────────────────────

    private async Task StartConversationAsync()
    {
        SetThinking(true);
        SetStatus("Pit Boss is warming up…");

        // Inject live context (datasets + models) before the first LLM call
        if (_svc is not null)
            _svc.EnvironmentContext = await BuildEnvironmentContextAsync();

        var sb = new StringBuilder();
        AppendBotBubble(""); // placeholder
        var bubble = GetLastBotBubble();

        await foreach (var chunk in _svc!.OpeningAsync(_cts.Token))
        {
            sb.Append(chunk);
            _ = Dispatcher.UIThread.InvokeAsync(() => { if (bubble is not null) bubble.Text = sb.ToString(); });
        }

        _history.Add(("assistant", sb.ToString()));
        _round = 0;

        SetThinking(false);
        SetStatus("Step 1 of 8 — tell the Pit Boss your goal.");
        UpdateStepIndicator(1);
        TbInput.IsEnabled = true;
        BtnSend.IsEnabled = true;
        TbInput.Focus();
    }

    // ── Message send ──────────────────────────────────────────────────────────

    private async Task SendMessageAsync(string text)
    {
        if (_thinking || string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        TbInput.Text = "";
        BtnSend.IsEnabled = false;
        SetThinking(true);

        AppendUserBubble(text);
        _history.Add(("user", text));
        _round++;

        bool shouldSynthesize = _round >= 8
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("confirm", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("yes,", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("looks good", StringComparison.OrdinalIgnoreCase);

        if (shouldSynthesize)
        {
            await SynthesizePlanAsync();
            return;
        }

        var sb = new StringBuilder();
        AppendBotBubble("");
        var bubble = GetLastBotBubble();

        await foreach (var chunk in _svc!.NextQuestionAsync(_history, _round, _cts.Token))
        {
            sb.Append(chunk);
            _ = Dispatcher.UIThread.InvokeAsync(() => { if (bubble is not null) bubble.Text = sb.ToString(); });
        }

        _history.Add(("assistant", sb.ToString()));
        var step = Math.Min(_round + 1, 8);
        UpdateStepIndicator(step);
        SetStatus($"Step {step} of 8");
        if (_round >= 5) ShowGenerateChip();

        SetThinking(false);
        BtnSend.IsEnabled = true;
        TbInput.Focus();
    }

    // ── Plan synthesis ────────────────────────────────────────────────────────

    private async Task SynthesizePlanAsync()
    {
        SetStatus("Synthesizing your training plan…");
        AppendBotBubble("📋  Generating your training plan — one moment…");

        var (plan, rawJson) = await _svc!.SynthesizePlanAsync(_history, WorkspaceRoot, _cts.Token);

        if (plan is null)
        {
            AppendBotBubble("⚠️  I had trouble parsing the plan. Here's the raw JSON — please paste it into plan_custom.json manually:\n\n" + rawJson);
            SetStatus("Plan synthesis failed — see raw JSON above.");
            SetThinking(false);
            BtnSend.IsEnabled = true;
            return;
        }

        _plan = plan;
        _history.Add(("assistant", $"Plan synthesized: {plan.Goal}"));
        AppendBotBubble(
            $"✅  Your training plan is ready!\n\n" +
            $"Goal: {plan.Goal}\nDataset: {plan.DatasetTarget:N0} examples via {plan.DatasetSource}\n" +
            $"Adapter: {plan.AdapterName}\nEstimate: {plan.EstimateText}\n\n" +
            $"Review the plan on the right. Click Save Plan to keep it, or Launch Training to start now.");

        RenderPlanPreview(plan);
        BtnSavePlan.IsEnabled   = true;
        BtnLaunchPlan.IsEnabled = true;
        HideChips();
        SetStatus("Plan ready — Save or Launch.");
        SetThinking(false);
    }

    // ── Plan preview ──────────────────────────────────────────────────────────

    private void RenderPlanPreview(TrainingPlan plan)
    {
        PlanEmpty.IsVisible = false;
        PlanStack.IsVisible = true;

        PlanGoal.Text      = plan.Goal;
        PlanPersona.Text   = string.IsNullOrWhiteSpace(plan.Persona) ? "—" : plan.Persona;
        PlanDataset.Text   = $"{plan.DatasetTarget:N0} examples  ·  {plan.DatasetSource}  ·  {plan.DatasetGenModel}";
        PlanLanguages.Text = plan.Languages.Count > 0
            ? string.Join("  ·  ", plan.Languages) : "any language";
        PlanModel.Text     = $"{plan.BaseModel}  →  {plan.AdapterName}";
        PlanEstimate.Text  = plan.EstimateText;

        var mixItems = plan.TaskMix
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new TaskMixRow(kv.Key, kv.Value))
            .ToList();
        PlanTaskMix.ItemsSource = mixItems;

        if (!string.IsNullOrWhiteSpace(plan.Notes))
        {
            NotesLabel.IsVisible = true;
            PlanNotes.Text       = plan.Notes;
            PlanNotes.IsVisible  = true;
        }

        HiveBadge.IsVisible = plan.Hive?.Enabled == true;
    }

    // ── History view ──────────────────────────────────────────────────────────

    private void ShowHistory(List<Services.Data.PlanRecord> plans)
    {
        WizardBody.IsVisible     = false;
        HistoryBody.IsVisible    = true;
        BtnViewHistory.IsVisible = false;

        PlanHistoryList.Children.Clear();
        foreach (var p in plans)
            PlanHistoryList.Children.Add(BuildPlanCard(p));

        SetStatus("Select a plan to re-launch, or create a new one.");
    }

    private void ShowWizard()
    {
        HistoryBody.IsVisible  = false;
        WizardBody.IsVisible   = true;
        BtnViewHistory.IsVisible = (PlanRepo?.Count() ?? 0) > 0;
    }

    private Control BuildPlanCard(Services.Data.PlanRecord p)
    {
        var dateStr = DateTime.TryParse(p.CreatedAt, out var dt)
            ? dt.ToLocalTime().ToString("yyyy-MM-dd  HH:mm") : p.CreatedAt;
        var metaParts = new List<string> { dateStr };
        if (!string.IsNullOrEmpty(p.Phase))       metaParts.Add(p.Phase);
        if (!string.IsNullOrEmpty(p.AdapterName)) metaParts.Add(p.AdapterName);
        if (p.DatasetTarget > 0)                  metaParts.Add($"{p.DatasetTarget:N0} examples");

        var leftStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        leftStack.Children.Add(new TextBlock
        {
            Text = p.Goal, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        leftStack.Children.Add(new TextBlock
        {
            Text = string.Join("  ·  ", metaParts),
            FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x66)),
            FontFamily = new FontFamily("Consolas"),
        });

        var relaunchBtn = new Button
        {
            Content = "▶  Re-launch",
            Padding = new Thickness(12, 6, 12, 6),
            Background    = new SolidColorBrush(Color.FromRgb(0x3D, 0x2A, 0x00)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0xE8, 0xA0, 0x30)),
            Foreground    = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x80)),
            BorderThickness = new Thickness(1), FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0),
        };
        relaunchBtn.Click += (_, _) => OnRelaunched(p.PlanId);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(leftStack,   0);
        Grid.SetColumn(relaunchBtn, 1);
        grid.Children.Add(leftStack);
        grid.Children.Add(relaunchBtn);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x0D, 0x0D, 0x1A)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2A)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 8),
            Child = grid,
        };
    }

    private void OnRelaunched(string planId)
    {
        var plans = PitBossService.LoadPlans(WorkspaceRoot);
        var plan  = plans.FirstOrDefault(p => p.PlanId == planId);
        ShowWizard();
        ChatStack.Children.Clear();
        _history.Clear();
        _plan = null; _round = 0;
        PlanEmpty.IsVisible    = true;
        PlanStack.IsVisible    = false;
        BtnSavePlan.IsEnabled  = false;
        BtnLaunchPlan.IsEnabled= false;
        ChipRow.IsVisible      = false;

        if (plan is null)
        {
            TbInput.IsEnabled = true;
            AppendBotBubble($"⚠️  Plan file for ID '{planId}' was not found on disk. Starting a fresh wizard instead.");
            SetStatus("Plan file not found — starting new wizard.");
            _ = StartConversationAsync();
            return;
        }

        _plan = plan;
        TbStepIndicator.Text = "Plan loaded from history";
        TbRoundHint.Text     = "";
        TbInput.IsEnabled    = false;
        BtnSend.IsEnabled    = false;
        AppendBotBubble(
            $"📋  Re-loaded saved plan:\n\n" +
            $"Goal: {plan.Goal}\nDataset: {plan.DatasetTarget:N0} examples via {plan.DatasetSource}\n" +
            $"Adapter: {plan.AdapterName}\nEstimate: {plan.EstimateText}\n\n" +
            $"Review the plan on the right, then click Launch Training to start.");
        RenderPlanPreview(plan);
        BtnLaunchPlan.IsEnabled = true;
        SetStatus($"Plan loaded: {plan.AdapterName}");
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void BtnBack_Click(object? s, RoutedEventArgs e) { _cts.Cancel(); BackRequested?.Invoke(); }

    private void BtnNewPlan_Click(object? s, RoutedEventArgs e)
    {
        ShowWizard();
        ChatStack.Children.Clear();
        _history.Clear();
        _plan = null; _round = 0;
        PlanEmpty.IsVisible    = true;
        PlanStack.IsVisible    = false;
        BtnSavePlan.IsEnabled  = false;
        BtnLaunchPlan.IsEnabled= false;
        PopulateGoalChips();
        ChipRow.IsVisible = true;
        TbInput.IsEnabled = true;
        BtnSend.IsEnabled = false;
        _ = StartConversationAsync();
    }

    private void BtnViewHistory_Click(object? s, RoutedEventArgs e)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var saved = PlanRepo?.LoadAll();
        if (saved is { Count: > 0 }) ShowHistory(saved);
    }

    private void BtnSend_Click(object? s, RoutedEventArgs e) => _ = SendMessageAsync(TbInput.Text ?? "");

    private void TbInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            _ = SendMessageAsync(TbInput.Text ?? "");
        }
    }

    private void TbInput_TextChanged(object? sender, TextChangedEventArgs e)
        => BtnSend.IsEnabled = !_thinking && (TbInput.Text?.Trim().Length ?? 0) > 0;

    private void BtnSavePlan_Click(object? s, RoutedEventArgs e)
    {
        if (_plan is null) return;
        PitBossService.SavePlan(_plan, WorkspaceRoot);
        SetStatus($"Plan saved to training_pit/plans/{_plan.PlanFileName}");
        AppendBotBubble($"💾  Plan saved to `training_pit/plans/{_plan.PlanFileName}`");
        StatusChanged?.Invoke($"Plan saved: {_plan.PlanFileName}");
        if (PlanRepo is not null) BtnViewHistory.IsVisible = true;
    }

    private void BtnLaunchPlan_Click(object? s, RoutedEventArgs e)
    {
        if (_plan is null) return;
        BtnLaunchPlan.IsEnabled = false;
        BtnSavePlan.IsEnabled   = false;
        HideChips();

        _executor = new PlanExecutorService { RunRepo = RunRepo };
        _executor.ProgressUpdated += (written, total, phase) =>
            Dispatcher.UIThread.InvokeAsync(() => OnGenProgress(written, total, phase));
        _executor.DatasetReady += path =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppendBotBubble($"✅  Dataset ready: {System.IO.Path.GetFileName(path)}\n\nHanding off to the Forge…");
                SetStatus("Dataset complete — launching Forge…");
            });
        _executor.ForgeReady += (plan, datasetPath) =>
            Dispatcher.UIThread.InvokeAsync(() => ForgeHandoff?.Invoke(plan, datasetPath));
        _executor.Failed += msg =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                AppendBotBubble($"❌  {msg}");
                SetStatus($"Failed: {msg}");
                BtnLaunchPlan.IsEnabled = true;
            });
        _executor.LogLine += msg => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg));

        AppendBotBubble(
            $"🚀  Launching dataset generation ({_plan.DatasetTarget:N0} examples)…\n\n" +
            $"This will take approximately {_plan.EstDatasetHours:F0} hour(s). Progress updates appear here every 5 seconds.");
        SetStatus("Dataset generation started…");
        _ = _executor.StartAsync(_plan, WorkspaceRoot);
    }

    private void OnGenProgress(int written, int total, string phase)
    {
        var pct = total > 0 ? (int)(written * 100.0 / total) : 0;
        SetStatus($"[gen] {written:N0}/{total:N0} examples  ({pct}%)  — {phase}");
    }

    // ── Environment context builder ───────────────────────────────────────────

    private async Task<string> BuildEnvironmentContextAsync()
    {
        var datasets = TrainingPitRegistry.LoadDatasets(WorkspaceRoot);
        var models   = await TrainingPitRegistry.LoadModelsAsync(OllamaHost);

        var dsLines = datasets
            .Where(d => !d.InProgress && d.TrainCount > 0)
            .OrderByDescending(d => d.LastModified)
            .Take(8)
            .Select(d => $"  {d.Name} ({d.TrainCount:N0} train examples)");

        var modelLines = models
            .Take(10)
            .Select(m => $"  {m.Name} ({m.SizeGb:F1} GB)");

        return
            "<ENVIRONMENT>\n" +
            "Available training datasets:\n" +
            string.Join("\n", dsLines.DefaultIfEmpty("  (none found)")) + "\n\n" +
            "Installed Ollama models:\n" +
            string.Join("\n", modelLines.DefaultIfEmpty("  (none found)")) + "\n\n" +
            "Training hardware: RTX 5070 Ti, 16 GB VRAM. QLoRA peak ~12 GB.\n" +
            "</ENVIRONMENT>";
    }

    // ── Chip factory ──────────────────────────────────────────────────────────

    private void PopulateGoalChips()
    {
        ChipPanel.Children.Clear();
        foreach (var (label, value) in _goalChips)
        {
            var btn = new Button
            {
                Content = label, Tag = value,
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 4),
                Background    = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                BorderBrush   = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
                Foreground    = new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x70)),
                BorderThickness = new Thickness(1), FontSize = 11,
            };
            btn.Click += (_, _) =>
            {
                TbInput.Text = btn.Tag as string ?? "";
                _ = SendMessageAsync(TbInput.Text);
            };
            ChipPanel.Children.Add(btn);
        }
    }

    private void ShowGenerateChip()
    {
        if (ChipPanel.Children.OfType<Button>().Any(b => b.Tag as string == "__generate__"))
            return;
        var btn = new Button
        {
            Content = "✅ Generate Plan Now", Tag = "__generate__",
            Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 4),
            Background    = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x10)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x20)),
            Foreground    = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
            BorderThickness = new Thickness(1), FontSize = 11,
        };
        btn.Click += (_, _) => _ = SynthesizePlanAsync();
        ChipPanel.Children.Insert(0, btn);
    }

    private void HideChips() => ChipRow.IsVisible = false;

    // ── Chat bubble helpers ───────────────────────────────────────────────────

    private void AppendUserBubble(string text)
    {
        ChatStack.Children.Add(new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2A)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8, 8, 2, 8),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(60, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = new TextBlock
            {
                Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xE8)),
                FontSize = 12, TextWrapping = TextWrapping.Wrap,
            },
        });
        ScrollToBottom();
    }

    private void AppendBotBubble(string text)
    {
        var tb = new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };
        var wrap = new StackPanel();
        wrap.Children.Add(new TextBlock
        {
            Text = "🤖  Pit Boss", FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x80, 0x40)),
            Margin = new Thickness(0, 0, 0, 3),
        });
        wrap.Children.Add(new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x14)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8, 8, 8, 2),
            Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 60, 0),
            Child = tb,
        });
        ChatStack.Children.Add(wrap);
        ScrollToBottom();
    }

    private TextBlock? GetLastBotBubble()
    {
        if (ChatStack.Children.Count == 0) return null;
        if (ChatStack.Children[^1] is StackPanel sp && sp.Children.Count > 0
            && sp.Children[^1] is Border b && b.Child is TextBlock tb)
            return tb;
        return null;
    }

    private void ScrollToBottom()
        => Dispatcher.UIThread.InvokeAsync(() => ChatScroll.ScrollToEnd());

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetThinking(bool on)
    {
        _thinking         = on;
        TbInput.IsEnabled = !on;
        if (on) BtnSend.IsEnabled = false;
        ModelDot.Fill = new SolidColorBrush(on
            ? Color.FromRgb(0x80, 0x60, 0x10)
            : Color.FromRgb(0x3A, 0x6A, 0x2A));
    }

    private void SetStatus(string msg)
    {
        Dispatcher.UIThread.InvokeAsync(() => TbStatus.Text = msg);
        StatusChanged?.Invoke(msg);
    }

    private void UpdateStepIndicator(int step)
    {
        const int total = 8;
        var dots = string.Concat(Enumerable.Range(1, total).Select(i => i <= step ? "●" : "○"));
        Dispatcher.UIThread.InvokeAsync(() =>
            TbStepIndicator.Text = $"{dots.Replace("", " ").Trim()}   step {step} of {total}");
    }
}

// ── View-model for task mix bar chart ─────────────────────────────────────────

public class TaskMixRow
{
    public string Key     { get; }
    public string PctText { get; }
    public double BarWidth { get; }

    public TaskMixRow(string key, double weight)
    {
        Key      = key;
        PctText  = $"{weight * 100:F0}%";
        BarWidth = Math.Max(4, weight * 180);
    }
}
