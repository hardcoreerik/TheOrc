using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Text;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// The Pit Boss Training Wizard — guides the user through a conversational
/// Q&amp;A session (up to 8 rounds) to produce a fully-specified TrainingPlan.
///
/// Layout: chat Q&amp;A on the left, live plan preview on the right.
/// When the plan is confirmed the user can Save (writes JSON to training_pit/plans/)
/// or Launch (kicks off dataset generation, then LoRA training).
/// </summary>
public partial class PitBossPanel : UserControl
{
    // ── Public properties (set by MainWindow before navigation) ──────────────
    public string WorkspaceRoot { get; set; } = "";
    public string OllamaHost    { get; set; } = "http://localhost:11434";
    public string OllamaModel   { get; set; } = "qwen2.5-coder:14b";

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action?               BackRequested;
    public event Action<TrainingPlan>? PlanLaunched;
    public event Action<string>?       StatusChanged;

    // ── Internal state ────────────────────────────────────────────────────────
    private PitBossService?                        _svc;
    private readonly List<(string role, string text)> _history = [];
    private int                                    _round      = 0;
    private bool                                   _thinking   = false;
    private TrainingPlan?                          _plan       = null;
    private CancellationTokenSource                _cts        = new();

    // Goal suggestion chips shown on first question
    private static readonly (string Label, string Value)[] _goalChips =
    [
        ("code review",        "I want to become a better code reviewer"),
        ("C# expert",          "I want deeper understanding of C# and .NET patterns"),
        ("delegate tasks",     "I want to delegate and break down tasks like a senior engineer"),
        ("Python mastery",     "I want stronger Python skills including libraries and idioms"),
        ("SQL & databases",    "I want to write better SQL queries and understand database design"),
        ("test writer",        "I want to write thorough, meaningful unit and integration tests"),
        ("technical writer",   "I want to write clearer documentation, comments, and READMEs"),
        ("architect",          "I want to think like a software architect — design patterns, trade-offs, scale"),
        ("philosopher",        "I want to reason and explain ideas like a Socratic philosopher"),
        ("custom persona",     "I want to train a completely custom persona or style"),
    ];

    public PitBossPanel()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _svc = new PitBossService(OllamaHost, OllamaModel);
        TbModelLabel.Text = OllamaModel;
        ModelDot.Fill     = new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x2A));

        PopulateGoalChips();
        _ = StartConversationAsync();
    }

    // ── Conversation start ────────────────────────────────────────────────────

    private async Task StartConversationAsync()
    {
        SetThinking(true);
        SetStatus("Pit Boss is warming up…");

        var sb = new StringBuilder();
        AppendBotBubble(""); // placeholder; text is streamed in
        var bubble = GetLastBotBubble();

        await foreach (var chunk in _svc!.OpeningAsync(_cts.Token))
        {
            sb.Append(chunk);
            Dispatcher.Invoke(() =>
            {
                if (bubble is not null) bubble.Text = sb.ToString();
            });
        }

        // Record the opening as assistant turn
        _history.Add(("assistant", sb.ToString()));
        _round = 0;

        SetThinking(false);
        SetStatus("Step 1 of 8 — tell the Pit Boss your goal.");
        UpdateStepIndicator(1);
        TbInput.IsEnabled = true;
        BtnSend.IsEnabled = true;
        TbInput.Focus();
    }

    // ── User sends a message ──────────────────────────────────────────────────

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

        // After 8 rounds (or if user says yes/confirm) → synthesize plan
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

        // Ask next question
        var sb = new StringBuilder();
        AppendBotBubble("");
        var bubble = GetLastBotBubble();

        await foreach (var chunk in _svc!.NextQuestionAsync(_history, _round, _cts.Token))
        {
            sb.Append(chunk);
            Dispatcher.Invoke(() => { if (bubble is not null) bubble.Text = sb.ToString(); });
        }

        _history.Add(("assistant", sb.ToString()));

        var step = Math.Min(_round + 1, 8);
        UpdateStepIndicator(step);
        SetStatus($"Step {step} of 8");

        // After round 5 show a "I have enough, generate plan" chip
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
            $"**Goal:** {plan.Goal}\n" +
            $"**Dataset:** {plan.DatasetTarget:N0} examples via {plan.DatasetSource}\n" +
            $"**Adapter:** {plan.AdapterName}\n" +
            $"**Estimate:** {plan.EstimateText}\n\n" +
            $"Review the plan on the right. Click **Save Plan** to keep it, or **Launch Training** to start now.");

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
        PlanEmpty.Visibility = Visibility.Collapsed;
        PlanStack.Visibility = Visibility.Visible;

        PlanGoal.Text    = plan.Goal;
        PlanPersona.Text = string.IsNullOrWhiteSpace(plan.Persona) ? "—" : plan.Persona;

        PlanDataset.Text = $"{plan.DatasetTarget:N0} examples  ·  {plan.DatasetSource}  ·  {plan.DatasetGenModel}";
        PlanLanguages.Text = plan.Languages.Count > 0
            ? string.Join("  ·  ", plan.Languages)
            : "any language";
        PlanModel.Text   = $"{plan.BaseModel}  →  {plan.AdapterName}";
        PlanEstimate.Text = plan.EstimateText;

        // Task mix bars
        var mixItems = plan.TaskMix
            .OrderByDescending(kv => kv.Value)
            .Select(kv => new TaskMixRow(kv.Key, kv.Value))
            .ToList();
        PlanTaskMix.ItemsSource = mixItems;

        if (!string.IsNullOrWhiteSpace(plan.Notes))
        {
            NotesLabel.Visibility = Visibility.Visible;
            PlanNotes.Text        = plan.Notes;
            PlanNotes.Visibility  = Visibility.Visible;
        }

        HiveBadge.Visibility = (plan.Hive?.Enabled == true) ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Button handlers ───────────────────────────────────────────────────────

    private void BtnBack_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        BackRequested?.Invoke();
    }

    private void BtnSend_Click(object sender, RoutedEventArgs e)
        => _ = SendMessageAsync(TbInput.Text);

    private void TbInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            _ = SendMessageAsync(TbInput.Text);
        }
    }

    private void TbInput_TextChanged(object sender, TextChangedEventArgs e)
        => BtnSend.IsEnabled = !_thinking && TbInput.Text.Trim().Length > 0;

    private void BtnSavePlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        PitBossService.SavePlan(_plan, WorkspaceRoot);
        SetStatus($"Plan saved to training_pit/plans/{_plan.PlanFileName}");
        AppendBotBubble($"💾  Plan saved to `training_pit/plans/{_plan.PlanFileName}`");
        StatusChanged?.Invoke($"Plan saved: {_plan.PlanFileName}");
    }

    private void BtnLaunchPlan_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is null) return;
        PitBossService.SavePlan(_plan, WorkspaceRoot);
        PlanLaunched?.Invoke(_plan);
        AppendBotBubble("🚀  Training pipeline launched! Check the Training Pit for progress.");
        SetStatus("Training launched.");
        BtnLaunchPlan.IsEnabled = false;
    }

    // ── Chip factory ──────────────────────────────────────────────────────────

    private void PopulateGoalChips()
    {
        ChipPanel.Children.Clear();
        foreach (var (label, value) in _goalChips)
        {
            var btn = new Button
            {
                Style      = FindResource("ChipBtn") as Style,
                Content    = label,
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x14)),
                BorderBrush= new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x70)),
                Tag        = value,
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
        // Add a "Generate Plan Now" shortcut chip if not already there
        if (ChipPanel.Children.OfType<Button>().Any(b => b.Tag as string == "__generate__"))
            return;

        var btn = new Button
        {
            Style      = FindResource("ChipBtn") as Style,
            Content    = "✅ Generate Plan Now",
            Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x10)),
            BorderBrush= new SolidColorBrush(Color.FromRgb(0x3A, 0x6A, 0x20)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
            Tag        = "__generate__",
        };
        btn.Click += (_, _) => _ = SynthesizePlanAsync();

        ChipPanel.Children.Insert(0, btn);
    }

    private void HideChips() => ChipRow.Visibility = Visibility.Collapsed;

    // ── Chat bubble helpers ───────────────────────────────────────────────────

    private void AppendUserBubble(string text)
    {
        var border = new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x2A)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x5A)),
            BorderThickness = new Thickness(1),
            CornerRadius  = new CornerRadius(8, 8, 2, 8),
            Padding       = new Thickness(12, 8, 12, 8),
            Margin        = new Thickness(60, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        border.Child = new TextBlock
        {
            Text           = text,
            Foreground     = new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xE8)),
            FontSize       = 12,
            TextWrapping   = TextWrapping.Wrap,
        };
        ChatStack.Children.Add(border);
        ScrollToBottom();
    }

    private void AppendBotBubble(string text)
    {
        var tb = new TextBlock
        {
            Text           = text,
            Foreground     = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize       = 12,
            TextWrapping   = TextWrapping.Wrap,
        };
        var border = new Border
        {
            Background    = new SolidColorBrush(Color.FromRgb(0x14, 0x1E, 0x14)),
            BorderBrush   = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
            BorderThickness = new Thickness(1),
            CornerRadius  = new CornerRadius(8, 8, 8, 2),
            Padding       = new Thickness(12, 8, 12, 8),
            Margin        = new Thickness(0, 0, 60, 10),
        };
        border.Child = tb;

        // Bot label
        var label = new TextBlock
        {
            Text       = "🤖  Pit Boss",
            FontSize   = 9,
            Foreground = new SolidColorBrush(Color.FromRgb(0x50, 0x80, 0x40)),
            Margin     = new Thickness(0, 0, 0, 3),
        };
        var wrap = new StackPanel();
        wrap.Children.Add(label);
        wrap.Children.Add(border);
        ChatStack.Children.Add(wrap);
        ScrollToBottom();
    }

    private TextBlock? GetLastBotBubble()
    {
        if (ChatStack.Children.Count == 0) return null;
        var last = ChatStack.Children[^1];
        if (last is StackPanel sp && sp.Children.Count > 0
            && sp.Children[^1] is Border b && b.Child is TextBlock tb)
            return tb;
        return null;
    }

    private void ScrollToBottom()
    {
        Dispatcher.InvokeAsync(() => ChatScroll.ScrollToBottom(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    // ── UI helpers ────────────────────────────────────────────────────────────

    private void SetThinking(bool on)
    {
        _thinking         = on;
        TbInput.IsEnabled = !on;
        if (on) BtnSend.IsEnabled = false;
        ModelDot.Fill = new SolidColorBrush(on
            ? Color.FromRgb(0x80, 0x60, 0x10)   // amber — thinking
            : Color.FromRgb(0x3A, 0x6A, 0x2A));  // green — idle
    }

    private void SetStatus(string msg)
    {
        Dispatcher.Invoke(() => TbStatus.Text = msg);
        StatusChanged?.Invoke(msg);
    }

    private void UpdateStepIndicator(int step)
    {
        const int total = 8;
        var dots = string.Concat(Enumerable.Range(1, total).Select(i => i <= step ? "●" : "○"));
        Dispatcher.Invoke(() =>
        {
            TbStepIndicator.Text = $"{dots.Replace("", " ").Trim()}   step {step} of {total}";
        });
    }
}

// ── View-model for task mix bar chart ────────────────────────────────────────

public class TaskMixRow
{
    public string Key     { get; }
    public string PctText { get; }
    public double BarWidth { get; }

    public TaskMixRow(string key, double weight)
    {
        Key      = key;
        PctText  = $"{weight * 100:F0}%";
        BarWidth = Math.Max(4, weight * 180); // scale to 180px max
    }
}
