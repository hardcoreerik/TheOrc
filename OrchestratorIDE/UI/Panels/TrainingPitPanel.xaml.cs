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
    // Long-term professional targets (TRAINING_PIT_GUIDE "Dataset Size Targets")
    private const int TrainGoal = 1000, EvalGoal = 200;

    public string WorkspaceRoot { get; set; } = "";

    public event Action<string>? OnActivity;
    public event Action<string>? StatusChanged;

    /// <summary>Fires when live capture activity starts/stops or the queue size
    /// changes — MainWindow uses it for the title-bar pill badge.</summary>
    public event Action<bool, int>? LiveStateChanged;

    /// <summary>Fires when the user clicks the Pit Boss button.
    /// MainWindow handles the navigation to PitBossPanel.</summary>
    public event Action? PitBossRequested;

    public ObservableCollection<QueueItem> Queue { get; } = new();

    private string _pitRoot = "";
    private Process? _harvestProcess;
    private Process? _reviewProcess;
    private FileSystemWatcher? _stagingWatcher;
    private FileSystemWatcher? _reviewStagingWatcher;
    private FileSystemWatcher? _manifestWatcher;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _registryTimer;
    private DateTime _lastCaptureTime = DateTime.MinValue;
    private DateTime _lastManifestRefresh = DateTime.MinValue;
    private bool _liveActive;

    /// <summary>Ollama host pulled from AppSettings; refreshed on each load so
    /// changes propagate without restarting the panel.</summary>
    public string OllamaHost { get; set; } = "http://localhost:11434";

    public TrainingPitPanel()
    {
        InitializeComponent();
        QueueList.ItemsSource = Queue;
        Loaded += (_, _) => Refresh();

        // Live monitoring starts at construction, not first show, so the
        // title-bar badge works even if the user never opens this panel.
        // WorkspaceRoot is not assigned yet here (object initializer runs after
        // the ctor) — EnsureLiveMonitor is idempotent and is retried from
        // Refresh() once the real workspace is known.
        _pitRoot = ResolvePitRoot(WorkspaceRoot);
        EnsureLiveMonitor();
    }

    // ── Live activity monitor ─────────────────────────────────────────────
    //
    // Activity is detected from the staging FOLDER, not from process handles:
    // captures land there whether they come from this panel's harvest button,
    // a terminal farm run, or the GUI swarm. Fresh file within 3 minutes =
    // the pit is actively collecting.

    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(3);

    private void EnsureLiveMonitor()
    {
        if (_pitRoot.Length == 0) return;
        var staging = Path.Combine(_pitRoot, ".orc", "swarm", "dataset-staging");
        if (_stagingWatcher is null && Directory.Exists(staging))
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

        // Review captures land in review-staging/ from tools/review-capture.ps1.
        // Watch the folder so the count + latest line updates in real time.
        var reviewStaging = Path.Combine(_pitRoot, ".orc", "swarm", "review-staging");
        if (_reviewStagingWatcher is null)
        {
            // Create the dir if missing so the watcher has something to attach
            // to even before the first capture lands.
            try { Directory.CreateDirectory(reviewStaging); } catch { }
            if (Directory.Exists(reviewStaging))
            {
                _reviewStagingWatcher = new FileSystemWatcher(reviewStaging, "review_capture_*.json")
                {
                    EnableRaisingEvents = true,
                };
                _reviewStagingWatcher.Created += (_, _) => Dispatcher.BeginInvoke(RefreshReviewCaptures);
                _reviewStagingWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(RefreshReviewCaptures);
            }
        }

        // Manifest changes (CLI approve/reject from review_captures.py or
        // Codex external runs) must show up without a manual Refresh click —
        // that was the "UI stuck at 900" bug from the morning of 2026-06-13.
        var manifestDir = Path.Combine(_pitRoot, "training_pit", "datasets", "manifests");
        if (_manifestWatcher is null && Directory.Exists(manifestDir))
        {
            _manifestWatcher = new FileSystemWatcher(manifestDir, "reviewed_v1.json")
            {
                NotifyFilter        = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            // Debounce — atomic writes fire several events in quick succession.
            _manifestWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (DateTime.Now - _lastManifestRefresh < TimeSpan.FromSeconds(1)) return;
                _lastManifestRefresh = DateTime.Now;
                Refresh();
            });
        }

        if (_liveTimer is null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _liveTimer.Tick += (_, _) => UpdateLiveState();
            _liveTimer.Start();
        }

        // Models list comes from Ollama HTTP, not the filesystem — poll on a
        // slow timer so models added via `ollama pull` show up without restart.
        if (_registryTimer is null)
        {
            _registryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _registryTimer.Tick += async (_, _) => await ReloadModelsAsync();
            _registryTimer.Start();
        }
        UpdateLiveState();
    }

    // ── VRAM meter ────────────────────────────────────────────────────────
    // Polled on the same 5 s live timer; nvidia-smi runs off the UI thread and
    // overlapping queries are skipped. Hidden entirely on non-NVIDIA boxes.

    private bool _vramQueryBusy;
    private bool _vramUnavailable;

    private void UpdateVramMeter()
    {
        if (_vramQueryBusy || _vramUnavailable) return;
        _vramQueryBusy = true;

        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                var line = p?.StandardOutput.ReadLine();
                p?.WaitForExit(3000);
                if (line is null) { _vramUnavailable = true; return; }

                var parts = line.Split(',');
                if (parts.Length < 2 ||
                    !double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var used) ||
                    !double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var total) || total <= 0) return;

                // The driver returns garbage (e.g. 17592181866194 MiB) under heavy
                // memory pressure — discard impossible samples, keep the last sane one.
                if (used < 0 || used > total * 1.05 || total > 512 * 1024) return;

                var pct = used / total * 100;
                Dispatcher.BeginInvoke(() =>
                {
                    PnlVram.Visibility = Visibility.Visible;
                    TbVram.Text   = $"VRAM {used / 1024:F1}/{total / 1024:F1} GB";
                    VramBar.Value = pct;
                    VramBar.Foreground = new SolidColorBrush(
                        pct >= 92 ? Color.FromRgb(0xF4, 0x47, 0x47) :
                        pct >= 75 ? Color.FromRgb(0xE8, 0xA0, 0x30) :
                                    Color.FromRgb(0x76, 0xB9, 0x00));
                });
            }
            catch { _vramUnavailable = true; }
            finally { _vramQueryBusy = false; }
        });
    }

    private void UpdateLiveState()
    {
        UpdateVramMeter();
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
            // Captures are landing from somewhere we didn't launch (terminal farm,
            // GUI swarm). Starting a harvest now would double-farm the same goals.
            BtnStartHarvest.IsEnabled = false;
            BtnStartHarvest.ToolTip   = "Captures are already arriving from another run — wait for it to finish.";
        }
        else
        {
            HarvestStatus.Text = "not running";
            HarvestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            BtnStartHarvest.IsEnabled = true;
            BtnStartHarvest.ToolTip   = null;
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
        EnsureLiveMonitor();   // workspace may have just become known (see ctor note)
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

            // Datasets + adapters from disk; models hit Ollama HTTP (slow path).
            var datasets = Services.TrainingPitRegistry.LoadDatasets(_pitRoot);
            var adapters = Services.TrainingPitRegistry.LoadAdapters(_pitRoot);

            Dispatcher.Invoke(() =>
            {
                PhaseText.Text = train >= TrainGate && eval >= EvalGate && neg >= NegGate
                    ? $"All gates met — {train:N0} train · {eval} eval · {neg} neg"
                    : $"Collecting — {train} of {TrainGate} train · {eval} of {EvalGate} eval · {neg} of {NegGate} neg";

                RenderDatasets(datasets);
                RenderAdapters(adapters);
                RefreshReviewCaptures();

                Queue.Clear();
                foreach (var it in items) Queue.Add(it);
                QueueCount.Text = Queue.Count == 0 ? "nothing waiting" : $"{Queue.Count} waiting";

                UpdateHarvestUi();
            });

            // Models load is async — don't block the rest of the panel on it.
            _ = ReloadModelsAsync();
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
    private void BtnPitBoss_Click(object s, RoutedEventArgs e) => PitBossRequested?.Invoke();

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
        if (ForgeRunning)
        {
            OnActivity?.Invoke("Not starting night harvest — ORC ACADEMY is training on the GPU.");
            HarvestStatus.Text = "refused — academy is training on the GPU";
            return;
        }
        if (ReviewRunning)
        {
            OnActivity?.Invoke("Not starting night harvest — a review is using Ollama/GPU.");
            HarvestStatus.Text = "refused — review is using the GPU";
            return;
        }
        if (_liveActive)
        {
            // Belt-and-braces: the button should already be disabled in this state.
            OnActivity?.Invoke("Not starting night harvest — captures are already arriving from another run.");
            return;
        }

        // Read picker values; fall back to script defaults when the dropdowns
        // are still loading (Ollama may not have answered yet on a fast click).
        var gen   = (CbGenModel.SelectedItem   as HarvestModelOption)?.Name ?? "qwen2.5-coder:14b";
        var judge = (CbJudgeModel.SelectedItem as HarvestModelOption)?.Name ?? "qwen2.5-coder:14b";

        if (!int.TryParse(TbGoalsPerCycle.Text, out var goals) || goals < 1 || goals > 200)
        {
            OnActivity?.Invoke("Night harvest: goals/cycle must be a number between 1 and 200.");
            HarvestStatus.Text = "invalid goals/cycle — must be 1–200";
            return;
        }

        var duration = (CbHarvestDuration.SelectedItem as ComboBoxItem)?.Tag as string ?? "dawn";
        var durationFlag = duration switch
        {
            "stopped" => "-UntilStopped",
            "2"       => "-Hours 2",
            "4"       => "-Hours 4",
            "8"       => "-Hours 8",
            _         => "",                  // empty = script default "until dawn"
        };

        var script = Path.Combine(_pitRoot, "training_pit", "scripts", "night_harvest.ps1");
        var args   = $"-ExecutionPolicy Bypass -File \"{script}\" " +
                     $"-GenModel \"{gen}\" -JudgeModel \"{judge}\" -GoalsPerCycle {goals} {durationFlag}";

        _harvestProcess = Process.Start(new ProcessStartInfo
        {
            FileName         = "pwsh",
            Arguments        = args,
            WorkingDirectory = _pitRoot,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        });

        var label = duration switch
        {
            "stopped" => "until stopped",
            "2" or "4" or "8" => $"for {duration} hours",
            _ => "until dawn",
        };
        OnActivity?.Invoke($"Night harvest started — gen={gen}, judge={judge}, {goals}/cycle, {label}");
        UpdateHarvestUi();
    }

    // ── Harvest picker population ─────────────────────────────────────────
    //
    // Generator dropdown blocks boss/gemma families (generate_goals.py refuses
    // those — the boss would feed itself its own distribution). Judge dropdown
    // is unrestricted. Duration dropdown is static and only built once.

    private bool _harvestDurationInit;

    private void PopulateHarvestPickers(List<Services.TrainingPitRegistry.OllamaModelInfo> models)
    {
        if (!_harvestDurationInit)
        {
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "Dawn (06:00)", Tag = "dawn", IsSelected = true });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "2 hours",      Tag = "2"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "4 hours",      Tag = "4"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "8 hours",      Tag = "8"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "Stop manually", Tag = "stopped" });
            _harvestDurationInit = true;
        }

        var prevGen   = (CbGenModel.SelectedItem   as HarvestModelOption)?.Name;
        var prevJudge = (CbJudgeModel.SelectedItem as HarvestModelOption)?.Name;

        var genOpts = models.Select(m => new HarvestModelOption
        {
            Name      = m.Name,
            SizeText  = $"{m.SizeGb:F1} GB",
            IsBlocked = IsBossFamily(m.Name),
        }).ToList();

        var judgeOpts = models.Select(m => new HarvestModelOption
        {
            Name      = m.Name,
            SizeText  = $"{m.SizeGb:F1} GB",
            IsBlocked = false,           // judge can be anything
        }).ToList();

        CbGenModel.ItemsSource   = genOpts;
        CbJudgeModel.ItemsSource = judgeOpts;

        // Restore previous selection if still present, else prefer
        // qwen2.5-coder:14b (the project's blessed default), else the first
        // non-blocked entry.
        CbGenModel.SelectedItem = genOpts.FirstOrDefault(o => o.Name == prevGen && !o.IsBlocked)
                               ?? genOpts.FirstOrDefault(o => o.Name == "qwen2.5-coder:14b")
                               ?? genOpts.FirstOrDefault(o => !o.IsBlocked);
        CbJudgeModel.SelectedItem = judgeOpts.FirstOrDefault(o => o.Name == prevJudge)
                                 ?? judgeOpts.FirstOrDefault(o => o.Name == "qwen2.5-coder:14b")
                                 ?? judgeOpts.FirstOrDefault();
    }

    /// <summary>Same rule as generate_goals.py: refuse the boss family so the
    /// generator can't seed the dataset with its own output distribution.</summary>
    private static bool IsBossFamily(string modelName) =>
        modelName.Contains("boss", StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("gemma", StringComparison.OrdinalIgnoreCase);

    private void CbGenModel_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // Defensive: if the user opens the dropdown via keyboard and lands on
        // a blocked item, snap back to the previous valid selection.
        if (CbGenModel.SelectedItem is HarvestModelOption opt && opt.IsBlocked)
        {
            OnActivity?.Invoke($"{opt.Name} can't be the generator — boss family is excluded by design.");
            var fallback = CbGenModel.ItemsSource?.Cast<HarvestModelOption>().FirstOrDefault(o => !o.IsBlocked);
            CbGenModel.SelectedItem = fallback;
        }
    }

    // ── Forge picker population ──────────────────────────────────────────
    //
    // Base model dropdown carries Ollama names + their HF repo mapping.
    // Selecting one auto-fills TbHfRepo; the user can override the box
    // directly to use any HF repo (no mapping required).
    //
    // Dataset dropdown shows the JSONLs grouped by version stem (v1, v2…).
    // Picking one auto-suggests an output adapter name in TbOutputName.

    private void PopulateForgePickers(
        List<Services.TrainingPitRegistry.OllamaModelInfo> models,
        List<Services.TrainingPitRegistry.DatasetInfo>     datasets)
    {
        // Base models — option carries HF mapping; untrainable ones are
        // greyed out (GGUF quants, models with no HF equivalent yet).
        var prevBase = (CbBaseModel.SelectedItem as BaseModelOption)?.OllamaName;
        var baseOpts = models.Select(m => new BaseModelOption
        {
            OllamaName = m.Name,
            HfRepo     = MapOllamaToHfRepo(m.Name),
            Reason     = ReasonForUntrainable(m.Name),
        }).ToList();
        CbBaseModel.ItemsSource = baseOpts;
        CbBaseModel.SelectedItem = baseOpts.FirstOrDefault(o => o.OllamaName == prevBase && o.HfRepo.Length > 0)
                                ?? baseOpts.FirstOrDefault(o => o.OllamaName == "gemma4:12b")
                                ?? baseOpts.FirstOrDefault(o => o.HfRepo.Length > 0);

        // Datasets — option carries derived train/eval paths.
        var prevDs = (CbDataset.SelectedItem as DatasetOption)?.Name;
        var dsOpts = datasets.Select(d => new DatasetOption(d, Path.Combine(_pitRoot, "training_pit", "datasets"))).ToList();
        CbDataset.ItemsSource = dsOpts;
        CbDataset.SelectedItem = dsOpts.FirstOrDefault(o => o.Name == prevDs) ?? dsOpts.FirstOrDefault();
    }

    private void CbBaseModel_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (CbBaseModel.SelectedItem is not BaseModelOption opt) return;
        if (opt.HfRepo.Length == 0)
        {
            // No mapping — leave the box at whatever the user had so they can
            // type a repo themselves. Hint that this model isn't auto-mapped.
            OnActivity?.Invoke($"{opt.OllamaName}: {opt.Reason} Type a HF repo path in the box to override.");
            return;
        }
        TbHfRepo.Text = opt.HfRepo;
        SuggestOutputName();
    }

    private void CbDataset_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        SuggestOutputName();
        // Provenance line + dataset counts inside ORC ACADEMY follow the
        // selection so picking v2 doesn't keep showing v1 numbers.
        RefreshForge();
    }

    /// <summary>Pick an output folder name from the active base + dataset
    /// pickers, but only when the user hasn't already typed something custom.
    /// Avoids stomping a hand-edited name on every selection change.</summary>
    private void SuggestOutputName()
    {
        var current = TbOutputName.Text.Trim();
        if (current.Length > 0 && current != "lora_v1" && !current.StartsWith("lora_", StringComparison.Ordinal))
            return;

        var ds   = CbDataset.SelectedItem   as DatasetOption;
        var bm   = CbBaseModel.SelectedItem as BaseModelOption;
        if (ds is null) return;

        var baseTag = bm?.OllamaName?.Split(':')[0].Replace('.', '_').Replace('/', '_') ?? "";
        TbOutputName.Text = baseTag.Length > 0 ? $"lora_{ds.Name}_{baseTag}" : $"lora_{ds.Name}";
    }

    /// <summary>Best-effort Ollama→HuggingFace mapping for common open
    /// instruction-tuned models. Returns "" if the model isn't trainable as
    /// a HF base (GGUF quants, missing repo, custom local builds).</summary>
    private static string MapOllamaToHfRepo(string ollama)
    {
        // hf.co/org/repo:tag → org/repo, but only if it's not a GGUF quant.
        if (ollama.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            if (ollama.Contains("gguf", StringComparison.OrdinalIgnoreCase) ||
                ollama.Contains("-qat-",  StringComparison.OrdinalIgnoreCase)) return "";
            var withoutTag = ollama.Split(':')[0];               // hf.co/org/repo
            var parts      = withoutTag.Split('/', 3);
            return parts.Length == 3 ? $"{parts[1]}/{parts[2]}" : "";
        }

        // Common Ollama hub names → canonical HF repos.
        var name = ollama.ToLowerInvariant();
        return name switch
        {
            "gemma4:12b"            => "google/gemma-4-12b-it",
            "gemma4:e4b"            => "google/gemma-4-e4b-it",
            "qwen2.5-coder:14b"     => "Qwen/Qwen2.5-Coder-14B-Instruct",
            "qwen2.5-coder:7b"      => "Qwen/Qwen2.5-Coder-7B-Instruct",
            "qwen2.5:14b-instruct"  => "Qwen/Qwen2.5-14B-Instruct",
            "llama3.1:8b"           => "meta-llama/Llama-3.1-8B-Instruct",
            "mistral-small:latest"  => "mistralai/Mistral-Small-Instruct-2409",
            "deepseek-coder-v2:16b" => "deepseek-ai/DeepSeek-Coder-V2-Lite-Instruct",
            "phi4-mini:latest"      => "microsoft/Phi-4-mini-instruct",
            "nemotron-3-nano:4b"    => "nvidia/Nemotron-Mini-4B-Instruct",
            _                       => "",
        };
    }

    private static string ReasonForUntrainable(string ollama)
    {
        if (ollama.Contains("gguf", StringComparison.OrdinalIgnoreCase) ||
            ollama.Contains("-qat-",  StringComparison.OrdinalIgnoreCase)) return "GGUF quant — not trainable as a HF base.";
        if (ollama.StartsWith("theorc-", StringComparison.OrdinalIgnoreCase)) return "custom local build — no HF source.";
        if (ollama.Contains("nomic-embed", StringComparison.OrdinalIgnoreCase)) return "embedding model — not a chat base.";
        return "no HF mapping yet.";
    }

    private void BtnStopHarvest_Click(object s, RoutedEventArgs e)
    {
        File.WriteAllText(StopFilePath, "");
        OnActivity?.Invoke("Night harvest stop requested — finishing the current plan");
        HarvestStatus.Text = "stopping after current plan…";
    }

    // ── WARCHIEF FORGE — Phase 3 training control ─────────────────────────

    private Process? _forgeProcess;
    private DispatcherTimer? _forgeTimer;
    private bool _forgeDotOn;

    /// <summary>Folder under training_pit/outputs/ that the panel currently
    /// tracks. Defaults to the legacy "lora_v1" name so refreshing without a
    /// new run still shows the existing adapter. StartForge updates this when
    /// the user picks a different output name.</summary>
    private string _forgeOutName = "lora_v1";

    private string ForgeOutDir   => Path.Combine(_pitRoot, "training_pit", "outputs", _forgeOutName);
    private string ProgressPath  => Path.Combine(ForgeOutDir, "progress.json");
    private string SummaryPath   => Path.Combine(ForgeOutDir, "training_summary.json");
    private string ForgeLogPath  => Path.Combine(ForgeOutDir, "forge.log");
    private bool   ForgeRunning  => _forgeProcess is { HasExited: false };
    private bool   HasCheckpoint =>
        Directory.Exists(Path.Combine(ForgeOutDir, "checkpoints")) &&
        Directory.GetDirectories(Path.Combine(ForgeOutDir, "checkpoints"), "checkpoint-*").Length > 0;

    private void ExpForge_Expanded(object s, RoutedEventArgs e) => RefreshForge();

    /// <summary>Dataset provenance line + button states + last-run result.</summary>
    private void RefreshForge()
    {
        try
        {
            int Lines(string name)
            {
                var p = Path.Combine(_pitRoot, "training_pit", "datasets", name);
                return File.Exists(p) ? File.ReadLines(p).Count(l => l.Length > 0) : 0;
            }

            // Follow whichever dataset is selected in the picker. The manifest
            // is per-version (reviewed_v1.json, reviewed_v2.json …) so the
            // decision count tracks the same selection.
            var version = (CbDataset.SelectedItem as DatasetOption)?.Name ?? "v1";
            int decided = 0;
            var manifest = Path.Combine(_pitRoot, "training_pit", "datasets", "manifests", $"reviewed_{version}.json");
            if (File.Exists(manifest))
                decided = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest))
                    .RootElement.GetProperty("entries").EnumerateObject().Count();

            ForgeDataset.Text =
                $"Dataset {version}: {Lines($"train_{version}.jsonl"):N0} train · {Lines($"eval_{version}.jsonl")} eval · " +
                $"{Lines($"negative_{version}.jsonl")} negative  —  {decided:N0} human-reviewed decisions in the manifest";
        }
        catch (Exception ex) { ForgeDataset.Text = $"Dataset: unavailable ({ex.Message})"; }

        // Re-attach to a trainer that outlived an app restart: a fresh heartbeat
        // names a live pid we can adopt for tracking and Stop.
        if (!ForgeRunning && File.Exists(ProgressPath) &&
            DateTime.Now - File.GetLastWriteTime(ProgressPath) < TimeSpan.FromMinutes(2))
        {
            try
            {
                var beat = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ProgressPath)).RootElement;
                var proc = Process.GetProcessById(beat.GetProperty("pid").GetInt32());
                if (!proc.HasExited && proc.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                {
                    _forgeProcess = proc;
                    ForgeDot.Visibility = Visibility.Visible;
                    ForgeStatus.Text = "Re-attached to a running training process…";
                    OnActivity?.Invoke("🏛 Academy re-attached to a training run that survived an app restart.");
                    _forgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _forgeTimer.Tick -= ForgeTimer_Tick;
                    _forgeTimer.Tick += ForgeTimer_Tick;
                    _forgeTimer.Start();
                }
            }
            catch { /* pid gone or not ours — nothing to adopt */ }
        }

        BtnForgeResume.IsEnabled = !ForgeRunning && HasCheckpoint;
        BtnForgeStart.IsEnabled  = !ForgeRunning;
        BtnForgeStop.IsEnabled   = ForgeRunning;

        if (!ForgeRunning && File.Exists(SummaryPath))
        {
            try
            {
                var sum = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SummaryPath)).RootElement;
                ForgeBadge.Text  = $"✓ adapter trained — eval loss {sum.GetProperty("eval_loss").GetDouble():F3}";
                ForgeStatus.Text = $"Last run: {sum.GetProperty("train_examples").GetInt32()} examples, " +
                                   $"{sum.GetProperty("minutes").GetDouble():F0} min, finished {sum.GetProperty("finished").GetString()}";
            }
            catch { /* partial summary — ignore */ }
        }
    }

    private void BtnForgeStart_Click(object s, RoutedEventArgs e)  => StartForge(resume: false);
    private void BtnForgeResume_Click(object s, RoutedEventArgs e) => StartForge(resume: true);

    private void StartForge(bool resume)
    {
        if (ForgeRunning) return;
        if (HarvestRunning || _liveActive)
        {
            OnActivity?.Invoke("🏛 Academy refused: the harvest is using the GPU. Stop it first.");
            ForgeStatus.Text = "Refused — harvest owns the GPU. Stop the harvest first.";
            return;
        }
        if (ReviewRunning)
        {
            OnActivity?.Invoke("🏛 Academy refused: a branch review is using Ollama/GPU. Wait for it to finish.");
            ForgeStatus.Text = "Refused — review owns the GPU. Wait for it to finish.";
            return;
        }

        // ── Read picker selections ────────────────────────────────────────
        // HF repo is the source of truth for which base model gets fine-tuned.
        // Pickers auto-fill it, but the user can override directly in the box.
        var hfRepo = TbHfRepo.Text.Trim();
        if (hfRepo.Length == 0)
        {
            OnActivity?.Invoke("🏛 Academy refused: HF repo path is empty. Pick a base model or type a repo.");
            ForgeStatus.Text = "Refused — HF repo is empty";
            return;
        }

        var outputName = TbOutputName.Text.Trim();
        if (outputName.Length == 0 || outputName.Contains(' ') || outputName.Contains('/') || outputName.Contains('\\'))
        {
            OnActivity?.Invoke("🏛 Academy refused: output name must be a single folder name (no spaces, no slashes).");
            ForgeStatus.Text = "Refused — invalid output name";
            return;
        }

        var dataset = CbDataset.SelectedItem as DatasetOption;
        var trainJsonl = dataset?.TrainPath
            ?? Path.Combine(_pitRoot, "training_pit", "datasets", "train_v1.jsonl");
        var evalJsonl  = dataset?.EvalPath
            ?? Path.Combine(_pitRoot, "training_pit", "datasets", "eval_v1.jsonl");

        // Point all ForgeOutDir-derived paths at the user's chosen output name.
        _forgeOutName = outputName;
        var adapterDir = Path.Combine(ForgeOutDir, "adapter");
        Directory.CreateDirectory(ForgeOutDir);
        try { File.Delete(ProgressPath); } catch { }
        if (!resume) { try { File.Delete(SummaryPath); } catch { } }

        var cap  = (CbVramCap.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "0";
        var args = $"-u \"{Path.Combine(_pitRoot, "training_pit", "scripts", "train_lora.py")}\"" +
                   $" --base \"{hfRepo}\"" +
                   $" --train \"{trainJsonl}\"" +
                   $" --eval \"{evalJsonl}\"" +
                   $" --out \"{adapterDir}\"";
        if (ChkDryRun.IsChecked == true) args += " --dry-run";
        if (cap != "0")                  args += $" --vram-cap {cap}";
        if (resume)                      args += " --resume";

        // Launch through cmd with FILE redirection, not .NET pipes: a piped
        // child dies with the app (broken-pipe kill once the buffer fills —
        // observed 2026-06-11: an app restart silently killed a loading
        // trainer at 86%). File-redirected, the trainer survives app restarts
        // and RefreshForge re-attaches to it via the heartbeat pid.
        File.WriteAllText(ForgeLogPath, $"=== forge run {DateTime.Now:yyyy-MM-dd HH:mm} (resume={resume}) ===\n");
        var psi = new ProcessStartInfo
        {
            FileName         = "cmd.exe",
            Arguments        = $"/c python {args} >> \"{ForgeLogPath}\" 2>&1",
            WorkingDirectory = _pitRoot,
            UseShellExecute  = false,
            CreateNoWindow   = true,
        };
        try
        {
            _forgeProcess = Process.Start(psi);
        }
        catch (Exception ex)
        {
            _forgeProcess = null;
            ForgeStatus.Text = $"Failed to launch the trainer ({ex.Message})";
            OnActivity?.Invoke($"🏛 Academy launch failed: {ex.Message}");
            return;
        }
        if (_forgeProcess is null)
        {
            ForgeStatus.Text = "Failed to launch the trainer.";
            return;
        }

        OnActivity?.Invoke($"🏛 ORC ACADEMY {(resume ? "resumed" : "started")}" +
                           (cap != "0" ? $" (VRAM cap {cap} GB)" : "") +
                           (ChkDryRun.IsChecked == true ? " — dry run" : ""));
        ForgeBadge.Text   = "";
        ForgeStatus.Text  = "Launching trainer…";
        ForgeBar.Value    = 0;
        ForgeBar.IsIndeterminate = true;
        ForgeDot.Visibility = Visibility.Visible;
        RefreshForge();

        _forgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _forgeTimer.Tick -= ForgeTimer_Tick;
        _forgeTimer.Tick += ForgeTimer_Tick;
        _forgeTimer.Start();
    }

    private void BtnForgeStop_Click(object s, RoutedEventArgs e)
    {
        if (!ForgeRunning) return;
        try
        {
            _forgeProcess!.Kill(entireProcessTree: true);
            // Kill is asynchronous — wait briefly so ForgeRunning reads false
            // and the buttons settle into the stopped state (codex finding).
            _forgeProcess.WaitForExit(5000);
        }
        catch { }
        OnActivity?.Invoke("🏛 Academy stopped — checkpoints kept; Resume continues from the last one.");
        ForgeStatus.Text = "Stopped. Checkpoints kept — Resume continues from the last one.";
        ForgeDone();
    }

    /// <summary>Heartbeat poll: progress bar, metrics, hang watchdog, exit detection.</summary>
    private void ForgeTimer_Tick(object? s, EventArgs e)
    {
        // Process gone? Decide success/failure from summary + log, then stop polling.
        if (!ForgeRunning)
        {
            var ok = File.Exists(SummaryPath) &&
                     File.GetLastWriteTime(SummaryPath) > DateTime.Now.AddMinutes(-5);
            if (ok)
            {
                OnActivity?.Invoke("🏛 Academy finished — adapter saved.");
            }
            else
            {
                var tail = File.Exists(ForgeLogPath)
                    ? string.Join(" ", File.ReadLines(ForgeLogPath).TakeLast(3))
                    : "no log";
                ForgeStatus.Text = Truncate($"Trainer exited unexpectedly — {tail}", 160);
                OnActivity?.Invoke("🏛 Academy exited unexpectedly — see training_pit/outputs/lora_v1/forge.log");
            }
            ForgeDone();
            return;
        }

        // Pulse the dot
        _forgeDotOn = !_forgeDotOn;
        ForgeDot.Opacity = _forgeDotOn ? 1.0 : 0.35;

        if (!File.Exists(ProgressPath)) return;
        try
        {
            // Liveness = freshest of heartbeat OR trainer log output: evaluation
            // phases beat rarely but stream tqdm into forge.log constantly
            // (false "possibly hung" observed during the 87-example eval).
            var beatAge = DateTime.Now - File.GetLastWriteTime(ProgressPath);
            if (File.Exists(ForgeLogPath))
            {
                var logAge = DateTime.Now - File.GetLastWriteTime(ForgeLogPath);
                if (logAge < beatAge) beatAge = logAge;
            }
            var p = System.Text.Json.JsonDocument.Parse(File.ReadAllText(ProgressPath)).RootElement;
            var status = p.GetProperty("status").GetString() ?? "?";

            // Hang watchdog: thresholds depend on phase (model load legitimately
            // produces no beats for a while; training beats every ≤5 steps).
            var limit = status is "loading_model" or "starting"
                ? TimeSpan.FromMinutes(25) : TimeSpan.FromMinutes(10);
            if (beatAge > limit)
            {
                ForgeDot.Fill    = System.Windows.Media.Brushes.Red;
                ForgeStatus.Text = $"⚠ Possibly hung — no heartbeat for {beatAge.TotalMinutes:F0} min " +
                                   $"(phase: {status}). Stop is safe; checkpoints are kept.";
                return;
            }
            ForgeDot.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
                .ConvertFromString("#E8A030")!;

            int step = p.TryGetProperty("step", out var st) ? st.GetInt32() : 0;
            int max  = p.TryGetProperty("max_steps", out var mx) ? mx.GetInt32() : 0;
            ForgeBar.IsIndeterminate = max <= 0;
            if (max > 0) ForgeBar.Value = Math.Min(100.0, step * 100.0 / max);

            ForgeStatus.Text = status switch
            {
                "starting"      => "Preparing — loading dataset…",
                "loading_model" => "Loading base model (4-bit quantization)… this takes a few minutes",
                "training"      => $"Training — step {step}/{max}",
                "evaluating"    => $"Evaluating at step {step}…",
                "final_eval"    => "Final evaluation…",
                "saving"        => "Saving adapter…",
                "done"          => "Done.",
                _               => status,
            };

            string M(string k) => p.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? v.GetDouble().ToString("F3") : "";
            var bits = new[] { ("loss", M("loss")), ("eval", M("eval_loss")), ("ep", M("epoch")) }
                .Where(t => t.Item2.Length > 0).Select(t => $"{t.Item1} {t.Item2}");
            ForgeMetrics.Text = string.Join("  ·  ", bits);
        }
        catch { /* transient parse race with the writer */ }
    }

    private void ForgeDone()
    {
        _forgeTimer?.Stop();
        ForgeBar.IsIndeterminate = false;
        ForgeDot.Visibility = Visibility.Collapsed;
        ForgeDot.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter()
            .ConvertFromString("#E8A030")!;
        RefreshForge();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private Task<(int Code, string Output)> RunPythonAsync(string args) => Task.Run(async () =>
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
        // Read both streams async BEFORE waiting, or a synchronous ReadToEnd
        // blocks forever and the timeout below is never reached.
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return (-1, "review tool timed out after 30s — manifest unchanged, retry from CLI if it persists");
        }
        p.WaitForExit();   // flush async stream readers
        return (p.ExitCode, await stdout + await stderr);
    });

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    // ── Registry rendering (Datasets / Adapters / Models row) ─────────────

    private void RenderDatasets(List<Services.TrainingPitRegistry.DatasetInfo> datasets)
    {
        DatasetList.ItemsSource = datasets;
        DatasetCount.Text = datasets.Count.ToString();
        DatasetEmpty.Visibility = datasets.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Forge dataset picker shares this source.
        _cachedDatasets = datasets;
        PopulateForgePickersIfReady();
    }

    private List<Services.TrainingPitRegistry.DatasetInfo>     _cachedDatasets = new();
    private List<Services.TrainingPitRegistry.OllamaModelInfo> _cachedModels   = new();

    private void PopulateForgePickersIfReady()
    {
        // Dataset picker must populate even when Ollama is unreachable — if
        // we gated both on models being loaded, StartForge would silently
        // fall back to the hardcoded train_v1.jsonl/eval_v1.jsonl defaults
        // and train on the wrong (or missing) dataset (Codex review,
        // 2026-06-13). Each picker fills as soon as ITS data source is ready.
        if (_cachedDatasets.Count > 0)
            PopulateForgePickers(_cachedModels, _cachedDatasets);
    }

    private void RenderAdapters(List<Services.TrainingPitRegistry.AdapterInfo> adapters)
    {
        var rows = adapters.Select(a => new AdapterRow(a)).ToList();
        AdapterList.ItemsSource = rows;
        AdapterCount.Text = adapters.Count.ToString();
        AdapterEmpty.Visibility = adapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Review captures ──────────────────────────────────────────────────
    //
    // Counts review_capture_*.json files in .orc/swarm/review-staging and
    // shows progress toward two thresholds:
    //   50  → enough to start an experimental fine-tune
    //   200 → enough for the real theorc-reviewer:v1 adapter
    //
    // No conversion or training is triggered from this UI — that's a manual
    // step (review_dataset.py + train_lora.py) once the count is high enough.

    private const int ReviewExperimentalGate = 50;
    private const int ReviewProductionGate   = 200;

    private void RefreshReviewCaptures()
    {
        if (_pitRoot.Length == 0) return;
        var info = Services.TrainingPitRegistry.LoadReviewCaptures(_pitRoot);

        ReviewProgressBar.Maximum = info.Count >= ReviewExperimentalGate
            ? ReviewProductionGate
            : ReviewExperimentalGate;
        ReviewProgressBar.Value = Math.Min(info.Count, (int)ReviewProgressBar.Maximum);

        var sinceText = info.Count == 0
            ? "never"
            : HumanizeAge(DateTime.Now - info.LatestAt) + " ago";
        ReviewStatus.Text = $"{info.Count} staged · last: {sinceText}";

        ReviewThreshold.Text = info.Count >= ReviewProductionGate
            ? $"{info.Count} captures — ready for the real reviewer adapter"
            : info.Count >= ReviewExperimentalGate
                ? $"{info.Count} of {ReviewProductionGate} toward the production adapter"
                : $"{info.Count} of {ReviewExperimentalGate} toward an experimental adapter";

        ReviewLatest.Text = info.LatestSummary.Length > 0
            ? $"latest: {info.LatestSummary}"
            : "";

        // Button states: disable both review buttons while GPU is in use.
        var gpuFree = !ReviewRunning && !HarvestRunning && !ForgeRunning;
        BtnReviewNow.IsEnabled        = gpuFree;
        BtnCaptureIncrement.IsEnabled = gpuFree;
        if (!gpuFree)
        {
            var reason = ReviewRunning   ? "Review already running."
                       : HarvestRunning ? "Harvest is using the GPU — wait for it to finish."
                                        : "Training is using the GPU — wait for it to finish.";
            BtnReviewNow.ToolTip        = reason;
            BtnCaptureIncrement.ToolTip = reason;
        }

        // Update the uncaptured-commits indicator from the capture marker.
        RefreshCaptureMarkerStatus();
    }

    /// <summary>
    /// Reads .orc/capture-marker.json and shows how many commits have
    /// landed since the last captured SHA.
    /// </summary>
    private void RefreshCaptureMarkerStatus()
    {
        try
        {
            if (string.IsNullOrEmpty(_pitRoot)) return;
            var markerPath = Path.Combine(_pitRoot, ".orc", "capture-marker.json");
            if (!File.Exists(markerPath)) { CaptureMarkerStatus.Text = ""; return; }

            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(markerPath));
            var lastSha = doc.RootElement.TryGetProperty("last_captured_sha", out var sha)
                ? sha.GetString() ?? "" : "";
            var total   = doc.RootElement.TryGetProperty("total_captures", out var t)
                ? t.GetInt32() : 0;

            if (string.IsNullOrEmpty(lastSha)) { CaptureMarkerStatus.Text = ""; return; }

            var psi = new ProcessStartInfo("git", $"rev-list --count {lastSha}..HEAD")
            {
                WorkingDirectory       = _pitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { CaptureMarkerStatus.Text = ""; return; }
            var countStr = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();

            if (!int.TryParse(countStr, out var uncaptured))
            { CaptureMarkerStatus.Text = ""; return; }

            CaptureMarkerStatus.Text = uncaptured == 0
                ? $"marker: {lastSha} · all commits captured ({total} total)"
                : $"marker: {lastSha} · {uncaptured} new commit{(uncaptured == 1 ? "" : "s")} since last capture → ⬇ to capture";
        }
        catch { CaptureMarkerStatus.Text = ""; }
    }

    private bool ReviewRunning => _reviewProcess is { HasExited: false };

    /// <summary>Best-effort range for "current branch vs remote upstream".
    /// Returns "" when the workspace isn't a git repo, has no remote, or no
    /// upstream is set — in which case the caller should fall back to
    /// reviewing the staged diff.</summary>
    private string DetectBranchRange()
    {
        try
        {
            // Resolve the upstream of the current branch (e.g. origin/master).
            // If the user is on detached HEAD or has no upstream, this fails
            // and we return "" — caller falls back to staged review.
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref --symbolic-full-name @{u}")
            {
                WorkingDirectory       = _pitRoot,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var upstream = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            if (p.ExitCode != 0 || upstream.Length == 0) return "";

            // upstream..HEAD reviews "what this branch adds beyond origin".
            return $"{upstream}..HEAD";
        }
        catch { return ""; }
    }

    private static string HumanizeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24)   return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private void BtnReviewNow_Click(object s, RoutedEventArgs e)
    {
        if (ReviewRunning) return;
        if (HarvestRunning || ForgeRunning)
        {
            OnActivity?.Invoke("🔍 Review refused — harvest or training is using the GPU.");
            return;
        }

        var script = Path.Combine(_pitRoot, "tools", "review-capture.ps1");
        if (!File.Exists(script))
        {
            OnActivity?.Invoke($"🔍 Review tool missing — expected {script}");
            return;
        }

        // Fire-and-forget. The FileSystemWatcher on review-staging picks up
        // the new capture when it lands, so we don't need to wait or stream.
        BtnReviewNow.IsEnabled = false;
        BtnReviewNow.Content   = "🔍 Reviewing…";

        // Honor what the button label says: "Review current branch" means
        // diff the current branch against its remote tracking branch (origin
        // default). Without -Range, review-capture.ps1 only reviews
        // `git diff --cached`, silently ignoring unstaged + branch changes
        // (Codex review, 2026-06-13). Falls back to staged when no remote
        // branch range applies (detached HEAD, no upstream).
        var range = DetectBranchRange();
        var args  = range.Length > 0
            ? $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Range \"{range}\""
            : $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        OnActivity?.Invoke(range.Length > 0
            ? $"🔍 Review started — Codex + TheOrc on {range}."
            : "🔍 Review started — Codex + TheOrc on staged changes (no branch range detected).");

        try
        {
            _reviewProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = "pwsh",
                Arguments        = args,
                WorkingDirectory = _pitRoot,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            });
        }
        catch (Exception ex)
        {
            OnActivity?.Invoke($"🔍 Review launch failed: {ex.Message}");
            BtnReviewNow.IsEnabled = true;
            BtnReviewNow.Content   = "▶ Review branch";
            return;
        }

        if (_reviewProcess is null)
        {
            BtnReviewNow.IsEnabled = true;
            BtnReviewNow.Content   = "▶ Review branch";
            return;
        }

        // Restore button text when the process exits, regardless of outcome.
        _reviewProcess.EnableRaisingEvents = true;
        _reviewProcess.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            BtnReviewNow.Content = "▶ Review branch";
            RefreshReviewCaptures();
            OnActivity?.Invoke($"🔍 Review finished (exit {_reviewProcess?.ExitCode}).");
        });
    }

    /// <summary>
    /// Calls auto-capture.ps1 — computes range from the capture marker
    /// (.orc/capture-marker.json) and runs Codex + TheOrc on commits
    /// since the last captured SHA.
    /// </summary>
    private void BtnCaptureIncrement_Click(object s, RoutedEventArgs e)
    {
        if (ReviewRunning) return;
        if (HarvestRunning || ForgeRunning)
        {
            OnActivity?.Invoke("⬇ Capture refused — harvest or training is using the GPU.");
            return;
        }

        var script = Path.Combine(_pitRoot, "tools", "auto-capture.ps1");
        if (!File.Exists(script))
        {
            OnActivity?.Invoke("⬇ auto-capture.ps1 not found — check tools\\auto-capture.ps1.");
            return;
        }

        BtnCaptureIncrement.Content   = "⬇ Capturing…";
        BtnCaptureIncrement.IsEnabled = false;
        BtnReviewNow.IsEnabled        = false;

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -OllamaHost \"{OllamaHost}\"";
        OnActivity?.Invoke("⬇ Capture increment started — Codex + TheOrc on commits since last marker.");

        try
        {
            _reviewProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = "pwsh",
                Arguments        = args,
                WorkingDirectory = _pitRoot,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            });
        }
        catch (Exception ex)
        {
            OnActivity?.Invoke($"⬇ Capture launch failed: {ex.Message}");
            BtnCaptureIncrement.Content   = "⬇ Capture increment";
            BtnCaptureIncrement.IsEnabled = true;
            BtnReviewNow.IsEnabled        = true;
            return;
        }

        if (_reviewProcess is null)
        {
            BtnCaptureIncrement.Content   = "⬇ Capture increment";
            BtnCaptureIncrement.IsEnabled = true;
            BtnReviewNow.IsEnabled        = true;
            return;
        }

        _reviewProcess.EnableRaisingEvents = true;
        _reviewProcess.Exited += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            var exitCode = _reviewProcess?.ExitCode ?? -1;
            BtnCaptureIncrement.Content = "⬇ Capture increment";
            RefreshReviewCaptures();
            RefreshCaptureMarkerStatus();
            OnActivity?.Invoke(exitCode switch
            {
                0 => "⬇ Capture complete — marker advanced.",
                1 => "⬇ No new commits since last capture — nothing to do.",
                2 => "⬇ Partial capture saved (one reviewer failed).",
                3 => "⬇ Both reviewers failed — marker NOT advanced.",
                _ => $"⬇ Capture finished (exit {exitCode})."
            });
        });
    }

    private async Task ReloadModelsAsync()
    {
        var models = await Services.TrainingPitRegistry.LoadModelsAsync(OllamaHost);
        Dispatcher.Invoke(() =>
        {
            var rows = models.Select(m => new ModelRow(m)).ToList();
            ModelList.ItemsSource = rows;
            ModelCount.Text = models.Count.ToString();
            ModelEmpty.Visibility = models.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // Harvest pickers reuse the same model list.
            PopulateHarvestPickers(models);

            // Forge base-model picker also consumes the same list; defer
            // population until both models AND datasets are loaded so the
            // output-name suggestion has both sides to work with.
            _cachedModels = models;
            PopulateForgePickersIfReady();
        });
    }
}

/// <summary>Option in the harvest generator/judge dropdowns. Carries an
/// IsBlocked flag so the gen picker can disable boss/gemma entries.</summary>
public class HarvestModelOption
{
    public string Name { get; init; } = "";
    public string SizeText { get; init; } = "";
    public bool   IsBlocked { get; init; }

    public string Display => IsBlocked ? $"{Name}  (boss family — excluded)" : Name;
    public Brush  TextBrush => IsBlocked
        ? (Brush)new BrushConverter().ConvertFromString("#666666")!
        : (Brush)new BrushConverter().ConvertFromString("#D4D4D4")!;
}

/// <summary>Option in the Forge base-model dropdown. Maps an Ollama name to
/// its HuggingFace repo (empty when no mapping exists yet — the user can
/// still type a repo in the override box).</summary>
public class BaseModelOption
{
    public string OllamaName { get; init; } = "";
    public string HfRepo { get; init; } = "";
    public string Reason { get; init; } = "";

    public string Display => HfRepo.Length > 0
        ? $"{OllamaName}  →  {HfRepo}"
        : $"{OllamaName}  ({Reason})";
    public Brush TextBrush => HfRepo.Length > 0
        ? (Brush)new BrushConverter().ConvertFromString("#D4D4D4")!
        : (Brush)new BrushConverter().ConvertFromString("#666666")!;
}

/// <summary>Option in the Forge dataset dropdown. Holds the derived
/// train/eval JSONL paths so StartForge can pass them straight to
/// train_lora.py without re-deriving from the version stem.</summary>
public class DatasetOption
{
    public string Name { get; }
    public string TrainPath { get; }
    public string EvalPath { get; }
    public string CountsText { get; }
    public string DisplayLabel { get; }

    public DatasetOption(Services.TrainingPitRegistry.DatasetInfo info, string datasetsDir)
    {
        Name = info.Name;

        if (info.IsNewConvention)
        {
            // New convention: file IS the training set; look for matching eval sibling
            TrainPath = info.FilePath;
            // Eval sibling: same name with ".eval.jsonl" suffix (opt-in; may not exist)
            var dir   = Path.GetDirectoryName(info.FilePath) ?? datasetsDir;
            EvalPath  = Path.Combine(dir, info.Name + ".eval.jsonl");
            var tag   = info.DataType.Length > 0 ? $"[{info.DataType}]" : "";
            var src   = info.Source.Length  > 0 ? $"{info.Source}" : info.Name;
            DisplayLabel = $"{src}{tag}";
        }
        else
        {
            TrainPath    = Path.Combine(datasetsDir, $"train_{info.Name}.jsonl");
            EvalPath     = Path.Combine(datasetsDir, $"eval_{info.Name}.jsonl");
            DisplayLabel = info.Name;
        }

        var evalNote = File.Exists(EvalPath) ? $" · {info.EvalCount} eval" : "";
        CountsText = $"{info.TrainCount:N0} train{evalNote}";
    }
}

/// <summary>View-model wrapper around AdapterInfo: precomputes display
/// strings and tier badge colors so the XAML stays simple.</summary>
public class AdapterRow
{
    public string Name { get; }
    public string BaseModelShort { get; }
    public string MetricsLine { get; }
    public string Tier { get; }
    public Brush  TierBg { get; }
    public Brush  TierFg { get; }

    public AdapterRow(Services.TrainingPitRegistry.AdapterInfo a)
    {
        Name = a.Name;
        var bm = a.BaseModel;
        // Strip org prefix so "google/gemma-4-12b-it" reads as "gemma-4-12b-it".
        var slash = bm.LastIndexOf('/');
        BaseModelShort = "base: " + (slash >= 0 ? bm[(slash + 1)..] : bm);

        var loss = a.EvalLoss is double l ? $"loss {l:F3}" : "no eval";
        var when = a.Finished == default ? "" : a.Finished.ToString("MM-dd HH:mm");
        MetricsLine = $"{a.TrainExamples} ex · {loss} · {when}";

        Tier = a.Tier;
        (TierBg, TierFg) = a.Tier switch
        {
            "Trusted"    => (BrushFromHex("#1F3D00"), BrushFromHex("#76B900")),
            "Promoted"   => (BrushFromHex("#1A2A00"), BrushFromHex("#CCA700")),
            _            => (BrushFromHex("#1A1A1A"), BrushFromHex("#999999")),
        };
    }

    private static SolidColorBrush BrushFromHex(string hex) =>
        (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
}

/// <summary>View-model wrapper around OllamaModelInfo.</summary>
public class ModelRow
{
    public string NameShort { get; }
    public string SizeText { get; }

    public ModelRow(Services.TrainingPitRegistry.OllamaModelInfo m)
    {
        // hf.co/org/repo:tag is the long form Ollama uses for HF pulls.
        // Show the last meaningful segment so the list stays readable.
        var name = m.Name;
        if (name.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0) name = "hf:" + name[(lastSlash + 1)..];
        }
        NameShort = name;
        SizeText = $"{m.SizeGb:F1} GB";
    }
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

/// <summary>Collapses when the bound bool is <c>true</c> (inverse of
/// WPF's built-in BooleanToVisibilityConverter).</summary>
[System.Windows.Data.ValueConversion(typeof(bool), typeof(System.Windows.Visibility))]
public class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is System.Windows.Visibility.Collapsed;
}
