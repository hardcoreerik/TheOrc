using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Training Pit panel — Phase 2.5 dataset accumulation dashboard.
///
/// Read paths are native C# (manifest JSON, staging captures, triage TSVs).
/// Write paths (approve / reject) shell out to review_captures.py so the
/// manifest stays the single source of truth and the CLI and GUI can never
/// disagree about what a decision means.
/// </summary>
public partial class TrainingPitPanel : UserControl
{
    private const int TrainGate = 150, EvalGate = 20, NegGate = 25;

    public string WorkspaceRoot { get; set; } = "";

    public event Action<string>? OnActivity;
    public event Action<string>? StatusChanged;

    /// <summary>Fires when live capture activity starts/stops or the queue size
    /// changes — MainWindow uses it for the title-bar pill badge.</summary>
    public event Action<bool, int>? LiveStateChanged;

    public ObservableCollection<QueueItem> Queue { get; } = new();

    private string _pitRoot = "";
    private Process? _harvestProcess;
    private FileSystemWatcher? _stagingWatcher;
    private DispatcherTimer? _liveTimer;
    private DateTime _lastCaptureTime = DateTime.MinValue;
    private bool _liveActive;

    public TrainingPitPanel()
    {
        InitializeComponent();
        QueueList.ItemsSource = Queue;
        Loaded += (_, _) => Refresh();

        // Live monitoring starts at construction, not first show, so the
        // title-bar badge works even if the user never opens this panel.
        _pitRoot = ResolvePitRoot(WorkspaceRoot);
        InitLiveMonitor();
    }

    // ── Live activity monitor ─────────────────────────────────────────────
    //
    // Activity is detected from the staging FOLDER, not from process handles:
    // captures land there whether they come from this panel's harvest button,
    // a terminal farm run, or the GUI swarm. Fresh file within 3 minutes =
    // the pit is actively collecting.

    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(3);

    private void InitLiveMonitor()
    {
        if (_pitRoot.Length == 0) return;
        var staging = Path.Combine(_pitRoot, ".orc", "swarm", "dataset-staging");
        if (Directory.Exists(staging))
        {
            _stagingWatcher = new FileSystemWatcher(staging, "plan_capture_*.json")
            {
                EnableRaisingEvents = true,
            };
            _stagingWatcher.Created += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                _lastCaptureTime = DateTime.Now;
                Refresh();                       // new capture → queue + counters move
            });
            _lastCaptureTime = Directory.GetFiles(staging, "plan_capture_*.json")
                .Select(File.GetLastWriteTime)
                .DefaultIfEmpty(DateTime.MinValue)
                .Max();
        }

        _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _liveTimer.Tick += (_, _) => UpdateLiveState();
        _liveTimer.Start();
        UpdateLiveState();
    }

    private void UpdateLiveState()
    {
        var active = HarvestRunning || DateTime.Now - _lastCaptureTime < LiveWindow;

        if (active != _liveActive)
        {
            _liveActive = active;
            LiveDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (active)
            {
                var pulse = new DoubleAnimation(1.0, 0.25, TimeSpan.FromSeconds(0.9))
                {
                    AutoReverse    = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                };
                LiveDot.BeginAnimation(OpacityProperty, pulse);
            }
            else
            {
                LiveDot.BeginAnimation(OpacityProperty, null);
            }
        }

        if (HarvestRunning)
        {
            UpdateHarvestUi();   // shows the harvest log tail
        }
        else if (active)
        {
            var age = (int)(DateTime.Now - _lastCaptureTime).TotalSeconds;
            HarvestStatus.Text = $"collecting now — last plan {age}s ago";
            HarvestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
        }
        else
        {
            HarvestStatus.Text = "not running";
            HarvestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            BtnStartHarvest.IsEnabled = true;
            BtnStopHarvest.IsEnabled  = false;
        }

        LiveStateChanged?.Invoke(active, Queue.Count);
    }

    // ── Data loading ──────────────────────────────────────────────────────

    /// <summary>
    /// The Training Pit lives in the TheOrc repo, which is not necessarily the
    /// open workspace. Prefer the workspace when it has a training_pit folder,
    /// otherwise walk up from the exe (dev builds run from bin/ inside the repo).
    /// </summary>
    private static string ResolvePitRoot(string workspaceRoot)
    {
        if (!string.IsNullOrEmpty(workspaceRoot) &&
            Directory.Exists(Path.Combine(workspaceRoot, "training_pit")))
            return workspaceRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "training_pit")))
                return dir.FullName;
        return "";
    }

    public void Refresh()
    {
        _pitRoot = ResolvePitRoot(WorkspaceRoot);
        if (_pitRoot.Length == 0)
        {
            PhaseText.Text = "training_pit not found — open the TheOrc repo as workspace";
            return;
        }
        Task.Run(LoadAll);
    }

    private void LoadAll()
    {
        try
        {
            var manifestPath = Path.Combine(_pitRoot, "training_pit", "datasets", "manifests", "reviewed_v1.json");
            int train = 0, eval = 0, neg = 0;
            var decided = new HashSet<string>();

            if (File.Exists(manifestPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var e in doc.RootElement.GetProperty("entries").EnumerateObject())
                {
                    decided.Add(e.Name);
                    if (e.Value.GetProperty("decision").GetString() != "approved") continue;
                    switch (e.Value.GetProperty("split").GetString())
                    {
                        case "train":    train++; break;
                        case "eval":     eval++;  break;
                        case "negative": neg++;   break;
                    }
                }
            }

            var triage = LoadTriage();
            var items  = LoadQueue(decided, triage);

            Dispatcher.Invoke(() =>
            {
                TrainCount.Text = $"{train} of {TrainGate}";
                EvalCount.Text  = $"{eval} of {EvalGate}" + (eval >= EvalGate ? " ✓" : "");
                NegCount.Text   = $"{neg} of {NegGate}"  + (neg  >= NegGate  ? " ✓" : "");
                TrainBar.Maximum = TrainGate; TrainBar.Value = train;
                EvalBar.Maximum  = EvalGate;  EvalBar.Value  = eval;
                NegBar.Maximum   = NegGate;   NegBar.Value   = neg;
                EvalCount.Foreground = new SolidColorBrush(eval >= EvalGate ? Color.FromRgb(0x76, 0xB9, 0x00) : Color.FromRgb(0xD4, 0xD4, 0xD4));
                NegCount.Foreground  = new SolidColorBrush(neg  >= NegGate  ? Color.FromRgb(0x76, 0xB9, 0x00) : Color.FromRgb(0xD4, 0xD4, 0xD4));

                PhaseText.Text = train >= TrainGate && eval >= EvalGate && neg >= NegGate
                    ? "All gates met — ready for Phase 3 preflight"
                    : "Collecting examples — training not started";

                Queue.Clear();
                foreach (var it in items) Queue.Add(it);
                QueueCount.Text = Queue.Count == 0 ? "nothing waiting" : $"{Queue.Count} waiting";

                UpdateHarvestUi();
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() => StatusChanged?.Invoke($"Training Pit load failed: {ex.Message}"));
        }
    }

    /// <summary>Judge verdicts, keyed by staging-relative capture path.</summary>
    private Dictionary<string, (string Risk, string Issues)> LoadTriage()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        var dir = Path.Combine(_pitRoot, "training_pit");
        if (!Directory.Exists(dir)) return map;
        foreach (var tsv in Directory.GetFiles(dir, "batch_*_triage.tsv"))
            foreach (var line in File.ReadLines(tsv).Skip(1))
            {
                var c = line.Split('\t');
                if (c.Length >= 5) map[c[3].Replace('/', '\\')] = (c[0], c[4]);
            }
        return map;
    }

    private List<QueueItem> LoadQueue(HashSet<string> decided,
        Dictionary<string, (string Risk, string Issues)> triage)
    {
        var items   = new List<QueueItem>();
        var staging = Path.Combine(_pitRoot, ".orc", "swarm", "dataset-staging");
        if (!Directory.Exists(staging)) return items;

        foreach (var file in Directory.GetFiles(staging, "plan_capture_*.json"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;
                var exampleId = root.GetProperty("example_id").GetString() ?? "";
                if (decided.Contains(exampleId)) continue;

                var goal  = root.GetProperty("goal").GetString() ?? "";
                var m     = Regex.Match(Path.GetFileName(file), @"_(\d+)\.json$");
                var score = m.Success ? int.Parse(m.Groups[1].Value) : -1;

                var chips = new List<string>();
                if (root.TryGetProperty("plan", out var plan) &&
                    plan.TryGetProperty("tasks", out var tasks))
                    foreach (var t in tasks.EnumerateArray())
                        chips.Add($"{t.GetProperty("role").GetString()} · {t.GetProperty("title").GetString()}");

                var rel = Path.GetRelativePath(_pitRoot, file);
                triage.TryGetValue(rel, out var verdict);

                items.Add(new QueueItem
                {
                    ExampleId   = exampleId,
                    CapturePath = rel,
                    Goal        = goal,
                    Score       = score,
                    TaskChips   = chips,
                    Risk        = verdict.Risk ?? "",
                    JudgeNote   = string.IsNullOrEmpty(verdict.Issues) ? "" : $"Judge: {verdict.Issues}",
                });
            }
            catch { /* unreadable capture — skip, CLI tools will report it */ }
        }

        // Riskiest first, then strongest score first within a band.
        int RiskRank(string r) => r switch { "high" => 0, "medium" => 1, "low" => 2, _ => 3 };
        return items.OrderBy(i => RiskRank(i.Risk)).ThenByDescending(i => i.Score).ToList();
    }

    // ── Review actions (write path = review_captures.py, always) ─────────

    private async void BtnKeepSilver_Click(object s, RoutedEventArgs e) =>
        await DecideAsync((QueueItem)((FrameworkElement)s).Tag, approve: true, quality: "silver");

    private async void BtnKeepGold_Click(object s, RoutedEventArgs e) =>
        await DecideAsync((QueueItem)((FrameworkElement)s).Tag, approve: true, quality: "gold");

    private async void BtnReject_Click(object s, RoutedEventArgs e) =>
        await DecideAsync((QueueItem)((FrameworkElement)s).Tag, approve: false, quality: "");

    private async Task DecideAsync(QueueItem item, bool approve, string quality)
    {
        var args = approve
            ? $"training_pit/scripts/review_captures.py --approve \"{item.CapturePath}\" --split train --quality {quality} --note \"GUI review\""
            : $"training_pit/scripts/review_captures.py --reject \"{item.CapturePath}\" --note \"GUI review reject\"";

        var (code, output) = await RunPythonAsync(args);
        if (code == 0)
        {
            Queue.Remove(item);
            QueueCount.Text = Queue.Count == 0 ? "nothing waiting" : $"{Queue.Count} waiting";
            OnActivity?.Invoke(approve
                ? $"Kept {item.ExampleId} as {quality}"
                : $"Threw out {item.ExampleId}");
            if (approve) Refresh();   // gate counters moved
        }
        else
        {
            StatusChanged?.Invoke($"Review failed (exit {code}): {Truncate(output, 160)}");
        }
    }

    private void BtnOpenCapture_Click(object s, RoutedEventArgs e)
    {
        var item = (QueueItem)((FrameworkElement)s).Tag;
        Process.Start(new ProcessStartInfo(Path.Combine(_pitRoot, item.CapturePath)) { UseShellExecute = true });
    }

    private void QueueHeader_Click(object s, RoutedEventArgs e)
    {
        var item = (QueueItem)((FrameworkElement)s).Tag;
        item.IsExpanded = !item.IsExpanded;
    }

    private void BtnRefresh_Click(object s, RoutedEventArgs e) => Refresh();

    // ── Night harvest control ─────────────────────────────────────────────

    private string StopFilePath => Path.Combine(_pitRoot, ".orc", "swarm", "HARVEST_STOP");

    private bool HarvestRunning => _harvestProcess is { HasExited: false };

    private void UpdateHarvestUi()
    {
        BtnStartHarvest.IsEnabled = !HarvestRunning;
        BtnStopHarvest.IsEnabled  = HarvestRunning;
        if (!HarvestRunning)
        {
            HarvestStatus.Text = "not running";
            return;
        }

        // Latest log line gives cycle/progress without any IPC.
        var logDir = Path.Combine(_pitRoot, ".orc", "swarm", "night_harvest");
        var latest = Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "harvest_*.log").OrderByDescending(f => f).FirstOrDefault()
            : null;
        HarvestStatus.Text = latest != null
            ? Truncate(File.ReadLines(latest).LastOrDefault() ?? "running…", 110)
            : "running…";
    }

    private void BtnStartHarvest_Click(object s, RoutedEventArgs e)
    {
        if (HarvestRunning) return;
        var script = Path.Combine(_pitRoot, "training_pit", "scripts", "night_harvest.ps1");
        _harvestProcess = Process.Start(new ProcessStartInfo
        {
            FileName         = "pwsh",
            Arguments        = $"-ExecutionPolicy Bypass -File \"{script}\"",
            WorkingDirectory = _pitRoot,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        });
        OnActivity?.Invoke("Night harvest started — runs until dawn or until stopped");
        UpdateHarvestUi();
    }

    private void BtnStopHarvest_Click(object s, RoutedEventArgs e)
    {
        File.WriteAllText(StopFilePath, "");
        OnActivity?.Invoke("Night harvest stop requested — finishing the current plan");
        HarvestStatus.Text = "stopping after current plan…";
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Task<(int Code, string Output)> RunPythonAsync(string args) => Task.Run(() =>
    {
        var psi = new ProcessStartInfo
        {
            FileName               = "python",
            Arguments              = args,
            WorkingDirectory       = _pitRoot,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return (p.ExitCode, output);
    });

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}

/// <summary>One undecided capture in the review queue.</summary>
public class QueueItem : INotifyPropertyChanged
{
    public string ExampleId   { get; init; } = "";
    public string CapturePath { get; init; } = "";
    public string Goal        { get; init; } = "";
    public int    Score       { get; init; }
    public List<string> TaskChips { get; init; } = new();
    public string Risk        { get; init; } = "";
    public string JudgeNote   { get; init; } = "";

    public string Summary   => Goal.Length <= 90 ? Goal : Goal[..90] + "…";
    public string ScoreText => Score >= 0 ? $"score {Score}" : "";
    public bool   HasJudgeNote => JudgeNote.Length > 0;

    public string RiskLabel => Risk switch
    {
        "high"   => "check closely",
        "medium" => "worth a look",
        "low"    => "looks clean",
        _        => "not judged",
    };
    public Brush RiskBg => new SolidColorBrush(Risk switch
    {
        "high"   => Color.FromRgb(0x2A, 0x0A, 0x0A),
        "medium" => Color.FromRgb(0x1A, 0x2A, 0x00),
        "low"    => Color.FromRgb(0x1F, 0x3D, 0x00),
        _        => Color.FromRgb(0x11, 0x11, 0x11),
    });
    public Brush RiskFg => new SolidColorBrush(Risk switch
    {
        "high"   => Color.FromRgb(0xF4, 0x47, 0x47),
        "medium" => Color.FromRgb(0xCC, 0xA7, 0x00),
        "low"    => Color.FromRgb(0x76, 0xB9, 0x00),
        _        => Color.FromRgb(0x99, 0x99, 0x99),
    });

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
