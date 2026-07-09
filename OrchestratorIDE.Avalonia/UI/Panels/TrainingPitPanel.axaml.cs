// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Panels;

public partial class TrainingPitPanel : UserControl
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const int TrainGate = 150, EvalGate = 20, NegGate = 25;
    private const int TrainGoal = 1000, EvalGoal = 200;
    private const int ReviewExperimentalGate = 50, ReviewProductionGate = 200;
    private static readonly TimeSpan LiveWindow = TimeSpan.FromMinutes(3);

    // ── Public properties ─────────────────────────────────────────────────────
    public string WorkspaceRoot  { get; set; } = "";
    public string OllamaHost     { get; set; } = "http://localhost:11434";
    public string ModelDepotRoot { get; set; } = "";

    // ── Events ────────────────────────────────────────────────────────────────
    public event Action<string>?          OnActivity;
    public event Action<string>?          StatusChanged;
    public event Action<bool, int>?       LiveStateChanged;
    public event Action?                  PitBossRequested;
    /// <summary>
    /// Fired when the user clicks "Load adapter into Native Runtime".
    /// Args: (baseModelPath, loraPath) — both absolute paths to GGUF files.
    /// </summary>
    public event Action<string, string>?  ActivateAdapterRequested;

    // ── Queue ─────────────────────────────────────────────────────────────────
    public ObservableCollection<QueueItem> Queue { get; } = new();

    // ── Internal state ────────────────────────────────────────────────────────
    private string _pitRoot = "";
    private Process?        _harvestProcess;
    private Process?        _reviewProcess;
    private DispatcherTimer? _liveTimer;
    private DispatcherTimer? _registryTimer;
    private DateTime _lastCaptureTime  = DateTime.MinValue;
    private DateTime _lastManifestRefresh = DateTime.MinValue;
    private bool _liveActive;

    private FileSystemWatcher? _stagingWatcher;
    private FileSystemWatcher? _reviewStagingWatcher;
    private FileSystemWatcher? _manifestWatcher;

    private List<Services.TrainingPitRegistry.DatasetInfo>     _cachedDatasets = [];
    private List<Services.TrainingPitRegistry.OllamaModelInfo> _cachedModels   = [];

    private bool _harvestDurationInit;
    private bool _vramQueryBusy;
    private bool _vramUnavailable;

    // Forge
    private Process?        _forgeProcess;
    private DispatcherTimer? _forgeTimer;
    private bool _forgeDotOn;
    private string _forgeOutName = "lora_v4";

    private string ForgeOutDir  => Path.Combine(_pitRoot, "training_pit", "outputs", _forgeOutName);
    private string ProgressPath => Path.Combine(ForgeOutDir, "progress.json");
    private string SummaryPath  => Path.Combine(ForgeOutDir, "training_summary.json");
    private string ForgeLogPath => Path.Combine(ForgeOutDir, "forge.log");
    private bool   ForgeRunning   => _forgeProcess is { HasExited: false };

    // Generator
    private Process?         _genProcess;
    private DispatcherTimer? _genTimer;
    private bool   _genDotOn;
    private string _genKey = "v4gold";
    private bool   _genTargetIsToolcaller;
    private string _genBackend = "native";   // "native" | "claude" | "ollama"

    private string GenOutDir       => Path.Combine(_pitRoot, "training_pit", "outputs", $"gen_{_genKey}");
    private string GenProgressPath => Path.Combine(GenOutDir, "gen_progress.json");
    private string GenLogPath      => Path.Combine(GenOutDir, "gen.log");
    private bool   GenRunning     => _genProcess is { HasExited: false };
    private bool   HarvestRunning => _harvestProcess is { HasExited: false };
    private bool   ReviewRunning  => _reviewProcess is { HasExited: false };
    private string StopFilePath   => Path.Combine(_pitRoot, ".orc", "swarm", "HARVEST_STOP");

    private bool HasCheckpoint =>
        Directory.Exists(Path.Combine(ForgeOutDir, "checkpoints")) &&
        Directory.GetDirectories(Path.Combine(ForgeOutDir, "checkpoints"), "checkpoint-*").Length > 0;

    // ── Construction ──────────────────────────────────────────────────────────

    public TrainingPitPanel()
    {
        InitializeComponent();
        QueueList.ItemsSource = Queue;
        Loaded += (_, _) => Refresh();
    }

    // ── EnsureLiveMonitor ─────────────────────────────────────────────────────

    private void EnsureLiveMonitor()
    {
        if (_liveTimer is null)
        {
            _liveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _liveTimer.Tick += (_, _) => UpdateLiveState();
            _liveTimer.Start();
        }

        if (_stagingWatcher is null)
        {
            var stagingDir = Path.Combine(_pitRoot, ".orc", "swarm", "dataset-staging");
            if (Directory.Exists(stagingDir))
            {
                _stagingWatcher = new FileSystemWatcher(stagingDir, "plan_capture_*.json")
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                _stagingWatcher.Created += (_, _) => Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastCaptureTime = DateTime.Now;
                    Refresh();
                });
            }
        }

        if (_reviewStagingWatcher is null)
        {
            var reviewDir = Path.Combine(_pitRoot, ".orc", "swarm", "review-staging");
            if (Directory.Exists(reviewDir))
            {
                _reviewStagingWatcher = new FileSystemWatcher(reviewDir, "review_capture_*.json")
                {
                    NotifyFilter = NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                _reviewStagingWatcher.Created += (_, _) => Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastCaptureTime = DateTime.Now;
                    RefreshReviewCaptures();
                });
            }
        }

        if (_manifestWatcher is null)
        {
            var manifestDir = Path.Combine(_pitRoot, "training_pit", "datasets", "manifests");
            if (Directory.Exists(manifestDir))
            {
                _manifestWatcher = new FileSystemWatcher(manifestDir, "reviewed_*.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite,
                    EnableRaisingEvents = true,
                };
                _manifestWatcher.Changed += (_, _) =>
                {
                    var now = DateTime.Now;
                    if (now - _lastManifestRefresh < TimeSpan.FromSeconds(1)) return;
                    _lastManifestRefresh = now;
                    Dispatcher.UIThread.InvokeAsync(() => Refresh());
                };
            }
        }

        if (_registryTimer is null)
        {
            _registryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _registryTimer.Tick += async (_, _) => await ReloadModelsAsync();
            _registryTimer.Start();
        }

        UpdateLiveState();
    }

    // ── VRAM meter ────────────────────────────────────────────────────────────

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

                if (used < 0 || used > total * 1.05 || total > 512 * 1024) return;

                var pct = used / total * 100;
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PnlVram.IsVisible = true;
                    TbVram.Text       = $"VRAM {used / 1024:F1}/{total / 1024:F1} GB";
                    VramBar.Value     = pct;
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
            LiveDot.IsVisible = active;
        }

        if (HarvestRunning)
        {
            UpdateHarvestUi();
        }
        else if (active)
        {
            var age = (int)(DateTime.Now - _lastCaptureTime).TotalSeconds;
            HarvestStatus.Text       = $"collecting now — last plan {age}s ago";
            HarvestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
            BtnStartHarvest.IsEnabled = false;
            ToolTip.SetTip(BtnStartHarvest, "Captures are already arriving from another run — wait for it to finish.");
        }
        else
        {
            HarvestStatus.Text       = "not running";
            HarvestStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
            BtnStartHarvest.IsEnabled = true;
            ToolTip.SetTip(BtnStartHarvest, null);
            BtnStopHarvest.IsEnabled  = false;
        }

        LiveStateChanged?.Invoke(active, Queue.Count);
    }

    // ── Data loading ──────────────────────────────────────────────────────────

    private static bool IsRealPitRoot(string root) =>
        Directory.Exists(Path.Combine(root, "training_pit", "foundry"));

    private static string ResolvePitRoot(string workspaceRoot)
    {
        if (!string.IsNullOrEmpty(workspaceRoot) && IsRealPitRoot(workspaceRoot))
            return workspaceRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
            if (IsRealPitRoot(dir.FullName))
                return dir.FullName;
        return "";
    }

    public void LaunchFromPlan(TrainingPlan plan, string datasetPath)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            Refresh();
            if (ForgeRunning)
            {
                OnActivity?.Invoke($"⚠️ Forge already running — plan '{plan.AdapterName}' queued manually.");
                return;
            }
            var hfRepo = MapOllamaToHfRepo(plan.BaseModel);
            if (hfRepo.Length == 0) hfRepo = plan.BaseModel;
            TbHfRepo.Text   = hfRepo;
            TbOutputName.Text = plan.AdapterName.Length > 0 ? plan.AdapterName : $"lora_{plan.PlanId}";

            if (!string.IsNullOrWhiteSpace(datasetPath))
            {
                var dsName = Path.GetFileNameWithoutExtension(datasetPath);
                var match  = (CbDataset.ItemsSource as IEnumerable<DatasetOptionAva>)?
                    .FirstOrDefault(d => d.TrainPath == datasetPath || d.Name == dsName);
                if (match is not null)
                    CbDataset.SelectedItem = match;
            }

            OnActivity?.Invoke($"🚀 Pit Boss handed off to Forge: {plan.Goal}");
            BtnForgeStart?.BringIntoView();
            StartForge(resume: false);
        });
    }

    public void Refresh()
    {
        _pitRoot = ResolvePitRoot(WorkspaceRoot);
        if (_pitRoot.Length == 0)
        {
            PhaseText.Text = "training_pit not found — open the TheOrc repo as workspace";
            return;
        }
        EnsureLiveMonitor();
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
            var datasets = Services.TrainingPitRegistry.LoadDatasets(_pitRoot);
            var adapters = Services.TrainingPitRegistry.LoadAdapters(_pitRoot);

            Dispatcher.UIThread.InvokeAsync(() =>
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

                // Pipeline stages are laid out side-by-side (not lazily expanded), so populate
                // all three immediately instead of waiting for an Expander.Expanded that no
                // longer exists.
                RefreshGen();
                RefreshForge();
                RefreshFoundry();
                RefreshArena();
            });

            _ = ReloadModelsAsync();
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.InvokeAsync(() => StatusChanged?.Invoke($"Training Pit load failed: {ex.Message}"));
        }
    }

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

                var goal = root.GetProperty("goal").GetString() ?? "";
                var m    = Regex.Match(Path.GetFileName(file), @"_(\d+)\.json$");
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
            catch { }
        }

        int RiskRank(string r) => r switch { "high" => 0, "medium" => 1, "low" => 2, _ => 3 };
        return items.OrderBy(i => RiskRank(i.Risk)).ThenByDescending(i => i.Score).ToList();
    }

    // ── Review queue actions ──────────────────────────────────────────────────

    private void QueueHeader_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is QueueItem item)
            item.IsExpanded = !item.IsExpanded;
    }

    private async void BtnKeepSilver_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is QueueItem item)
            await DecideAsync(item, approve: true, quality: "silver");
    }

    private async void BtnKeepGold_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is QueueItem item)
            await DecideAsync(item, approve: true, quality: "gold");
    }

    private async void BtnReject_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is QueueItem item)
            await DecideAsync(item, approve: false, quality: "");
    }

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
            OnActivity?.Invoke(approve ? $"Kept {item.ExampleId} as {quality}" : $"Threw out {item.ExampleId}");
            if (approve) Refresh();
        }
        else
        {
            StatusChanged?.Invoke($"Review failed (exit {code}): {Truncate(output, 160)}");
        }
    }

    private void BtnOpenCapture_Click(object? s, RoutedEventArgs e)
    {
        if (s is Button btn && btn.Tag is QueueItem item)
            Process.Start(new ProcessStartInfo(Path.Combine(_pitRoot, item.CapturePath)) { UseShellExecute = true });
    }

    private void BtnRefresh_Click(object? s, RoutedEventArgs e) => Refresh();

    private void BtnPitBoss_Click(object? s, RoutedEventArgs e) => PitBossRequested?.Invoke();

    // ── Night harvest ─────────────────────────────────────────────────────────

    private void UpdateHarvestUi()
    {
        BtnStartHarvest.IsEnabled = !HarvestRunning;
        BtnStopHarvest.IsEnabled  = HarvestRunning;
        if (!HarvestRunning) { HarvestStatus.Text = "not running"; return; }

        var logDir = Path.Combine(_pitRoot, ".orc", "swarm", "night_harvest");
        var latest = Directory.Exists(logDir)
            ? Directory.GetFiles(logDir, "harvest_*.log").OrderByDescending(f => f).FirstOrDefault()
            : null;
        HarvestStatus.Text = latest != null
            ? Truncate(File.ReadLines(latest).LastOrDefault() ?? "running…", 110)
            : "running…";
    }

    private void BtnStartHarvest_Click(object? s, RoutedEventArgs e)
    {
        if (HarvestRunning) return;
        if (ForgeRunning)   { OnActivity?.Invoke("Not starting harvest — Forge is using the GPU."); return; }
        if (FoundryRunning) { OnActivity?.Invoke("Not starting harvest — Foundry is using the GPU."); return; }
        if (ArenaRunning)   { OnActivity?.Invoke("Not starting harvest — Arena benchmark is using the GPU."); return; }
        if (ReviewRunning)  { OnActivity?.Invoke("Not starting harvest — a review is using the GPU."); return; }
        if (_liveActive)    { OnActivity?.Invoke("Not starting harvest — captures already arriving."); return; }

        var gen   = (CbGenModel.SelectedItem   as HarvestModelOptionAva)?.Name ?? "qwen2.5-coder:14b";
        var judge = (CbJudgeModel.SelectedItem as HarvestModelOptionAva)?.Name ?? "qwen2.5-coder:14b";

        if (!int.TryParse(TbGoalsPerCycle.Text, out var goals) || goals < 1 || goals > 200)
        {
            OnActivity?.Invoke("Night harvest: goals/cycle must be 1–200.");
            return;
        }

        var duration = (CbHarvestDuration.SelectedItem as ComboBoxItem)?.Tag as string ?? "dawn";
        var durationFlag = duration switch
        {
            "stopped" => "-UntilStopped",
            "2"       => "-Hours 2",
            "4"       => "-Hours 4",
            "8"       => "-Hours 8",
            _         => "",
        };

        var script = Path.Combine(_pitRoot, "training_pit", "scripts", "night_harvest.ps1");
        var args   = $"-ExecutionPolicy Bypass -File \"{script}\" " +
                     $"-GenModel \"{gen}\" -JudgeModel \"{judge}\" -GoalsPerCycle {goals} {durationFlag}";

        _harvestProcess = Process.Start(new ProcessStartInfo
        {
            FileName = "pwsh", Arguments = args, WorkingDirectory = _pitRoot,
            UseShellExecute = false, CreateNoWindow = true,
        });

        OnActivity?.Invoke($"Night harvest started — gen={gen}, judge={judge}, {goals}/cycle");
        UpdateHarvestUi();
    }

    private void BtnStopHarvest_Click(object? s, RoutedEventArgs e)
    {
        File.WriteAllText(StopFilePath, "");
        OnActivity?.Invoke("Night harvest stop requested — finishing the current plan");
        HarvestStatus.Text = "stopping after current plan…";
    }

    private void CbGenModel_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (CbGenModel.SelectedItem is HarvestModelOptionAva opt && opt.IsBlocked)
        {
            OnActivity?.Invoke($"{opt.Name} can't be the generator — boss family is excluded.");
            var fallback = (CbGenModel.ItemsSource as IEnumerable<HarvestModelOptionAva>)
                ?.FirstOrDefault(o => !o.IsBlocked);
            CbGenModel.SelectedItem = fallback;
        }
    }

    private void PopulateHarvestPickers(List<Services.TrainingPitRegistry.OllamaModelInfo> models)
    {
        if (!_harvestDurationInit)
        {
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "Dawn (06:00)", Tag = "dawn" });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "2 hours",      Tag = "2"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "4 hours",      Tag = "4"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "8 hours",      Tag = "8"    });
            CbHarvestDuration.Items.Add(new ComboBoxItem { Content = "Stop manually", Tag = "stopped" });
            CbHarvestDuration.SelectedIndex = 0;
            _harvestDurationInit = true;
        }

        var prevGen   = (CbGenModel.SelectedItem   as HarvestModelOptionAva)?.Name;
        var prevJudge = (CbJudgeModel.SelectedItem as HarvestModelOptionAva)?.Name;

        var genOpts = models.Select(m => new HarvestModelOptionAva
        {
            Name = m.Name, SizeText = $"{m.SizeGb:F1} GB", IsBlocked = IsBossFamily(m.Name),
        }).ToList();
        var judgeOpts = models.Select(m => new HarvestModelOptionAva
        {
            Name = m.Name, SizeText = $"{m.SizeGb:F1} GB", IsBlocked = false,
        }).ToList();

        CbGenModel.ItemsSource   = genOpts;
        CbJudgeModel.ItemsSource = judgeOpts;

        CbGenModel.SelectedItem = genOpts.FirstOrDefault(o => o.Name == prevGen && !o.IsBlocked)
                               ?? genOpts.FirstOrDefault(o => o.Name == "qwen2.5-coder:14b")
                               ?? genOpts.FirstOrDefault(o => !o.IsBlocked);
        CbJudgeModel.SelectedItem = judgeOpts.FirstOrDefault(o => o.Name == prevJudge)
                                 ?? judgeOpts.FirstOrDefault(o => o.Name == "qwen2.5-coder:14b")
                                 ?? judgeOpts.FirstOrDefault();
    }

    private static bool IsBossFamily(string modelName) =>
        modelName.Contains("boss",  StringComparison.OrdinalIgnoreCase) ||
        modelName.Contains("gemma", StringComparison.OrdinalIgnoreCase);

    // ── Forge ─────────────────────────────────────────────────────────────────

    private void RefreshForge()
    {
        try
        {
            int Lines(string name) =>
                File.Exists(Path.Combine(_pitRoot, "training_pit", "datasets", name))
                    ? File.ReadLines(Path.Combine(_pitRoot, "training_pit", "datasets", name)).Count(l => l.Length > 0) : 0;

            var version = (CbDataset.SelectedItem as DatasetOptionAva)?.Name ?? "v1";
            int decided = 0;
            var manifest = Path.Combine(_pitRoot, "training_pit", "datasets", "manifests", $"reviewed_{version}.json");
            if (File.Exists(manifest))
                decided = JsonDocument.Parse(File.ReadAllText(manifest))
                    .RootElement.GetProperty("entries").EnumerateObject().Count();

            ForgeDataset.Text =
                $"Dataset {version}: {Lines($"train_{version}.jsonl"):N0} train · {Lines($"eval_{version}.jsonl")} eval · " +
                $"{Lines($"negative_{version}.jsonl")} negative  —  {decided:N0} human-reviewed decisions";
        }
        catch (Exception ex) { ForgeDataset.Text = $"Dataset: unavailable ({ex.Message})"; }

        if (!ForgeRunning && File.Exists(ProgressPath) &&
            DateTime.Now - File.GetLastWriteTime(ProgressPath) < TimeSpan.FromMinutes(2))
        {
            try
            {
                var beat = JsonDocument.Parse(File.ReadAllText(ProgressPath)).RootElement;
                var proc = Process.GetProcessById(beat.GetProperty("pid").GetInt32());
                if (!proc.HasExited && proc.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase))
                {
                    _forgeProcess = proc;
                    ForgeDot.IsVisible = true;
                    ForgeStatus.Text = "Re-attached to a running training process…";
                    OnActivity?.Invoke("🏛 Academy re-attached to a training run that survived an app restart.");
                    _forgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                    _forgeTimer.Tick -= ForgeTimer_Tick;
                    _forgeTimer.Tick += ForgeTimer_Tick;
                    _forgeTimer.Start();
                }
            }
            catch { }
        }

        BtnForgeResume.IsEnabled = !ForgeRunning && HasCheckpoint;
        BtnForgeStart.IsEnabled  = !ForgeRunning;
        BtnForgeStop.IsEnabled   = ForgeRunning;

        if (!ForgeRunning && File.Exists(SummaryPath))
        {
            try
            {
                var sum = JsonDocument.Parse(File.ReadAllText(SummaryPath)).RootElement;
                ForgeBadge.Text  = $"✓ adapter trained — eval loss {sum.GetProperty("eval_loss").GetDouble():F3}";
                ForgeStatus.Text = $"Last run: {sum.GetProperty("train_examples").GetInt32()} examples, " +
                                   $"{sum.GetProperty("minutes").GetDouble():F0} min, finished {sum.GetProperty("finished").GetString()}";
            }
            catch { }
        }
    }

    private void CbBaseModel_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (CbBaseModel.SelectedItem is not BaseModelOptionAva opt) return;
        if (opt.HfRepo.Length == 0) { OnActivity?.Invoke($"{opt.OllamaName}: {opt.Reason}"); return; }
        TbHfRepo.Text = opt.HfRepo;
        SuggestOutputName();
    }

    private void CbDataset_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        SuggestOutputName();
        RefreshForge();
    }

    private void SuggestOutputName()
    {
        var current = TbOutputName.Text?.Trim() ?? "";
        if (current.Length > 0 && current != "lora_v4" && !current.StartsWith("lora_", StringComparison.Ordinal))
            return;

        var ds  = CbDataset.SelectedItem   as DatasetOptionAva;
        var bm  = CbBaseModel.SelectedItem as BaseModelOptionAva;
        if (ds is null) return;

        var baseTag = bm?.OllamaName?.Split(':')[0].Replace('.', '_').Replace('/', '_') ?? "";
        TbOutputName.Text = baseTag.Length > 0 ? $"lora_{ds.Name}_{baseTag}" : $"lora_{ds.Name}";
    }

    private void BtnForgeStart_Click(object? s, RoutedEventArgs e)  => StartForge(resume: false);
    private void BtnForgeResume_Click(object? s, RoutedEventArgs e) => StartForge(resume: true);

    private void StartForge(bool resume)
    {
        if (ForgeRunning) return;
        if (HarvestRunning || _liveActive) { OnActivity?.Invoke("🏛 Academy refused: harvest owns the GPU."); return; }
        if (ReviewRunning)                  { OnActivity?.Invoke("🏛 Academy refused: review owns the GPU."); return; }
        if (FoundryRunning)                 { OnActivity?.Invoke("🏛 Academy refused: Foundry owns the GPU."); return; }

        var hfRepo = TbHfRepo.Text?.Trim() ?? "";
        if (hfRepo.Length == 0) { OnActivity?.Invoke("🏛 Academy refused: HF repo is empty."); return; }

        var outputName = TbOutputName.Text?.Trim() ?? "";
        if (outputName.Length == 0 || outputName.Contains(' ') || outputName.Contains('/') || outputName.Contains('\\'))
        { OnActivity?.Invoke("🏛 Academy refused: invalid output name."); return; }

        var dataset    = CbDataset.SelectedItem as DatasetOptionAva;
        var trainJsonl = dataset?.TrainPath ?? Path.Combine(_pitRoot, "training_pit", "datasets", "train_v4gold_merged.jsonl");
        var evalJsonl  = dataset?.EvalPath  ?? Path.Combine(_pitRoot, "training_pit", "datasets", "eval_v3gold.jsonl");

        _forgeOutName = outputName;
        var adapterDir = Path.Combine(ForgeOutDir, "adapter");
        Directory.CreateDirectory(ForgeOutDir);
        try { File.Delete(ProgressPath); } catch { }
        if (!resume) { try { File.Delete(SummaryPath); } catch { } }

        var capContent = (CbVramCap.SelectedItem as ComboBoxItem)?.Content as string ?? "No cap";
        var cap = capContent switch { "12 GB" => "12", "10 GB" => "10", "8 GB" => "8", "6 GB" => "6", _ => "0" };

        var scriptPath = Path.Combine(_pitRoot, "training_pit", "scripts", "train_lora.py");

        File.WriteAllText(ForgeLogPath, $"=== forge run {DateTime.Now:yyyy-MM-dd HH:mm} (resume={resume}) ===\n");
        ProcessStartInfo psi;
        string? script = null;
#if WINDOWS
        {
            // Windows: shell-redirect to log file via cmd.exe (paths are double-quoted)
            var winArgs = $"-u \"{scriptPath}\" --base \"{hfRepo}\" --train \"{trainJsonl}\" --eval \"{evalJsonl}\" --out \"{adapterDir}\"";
            if (ChkDryRun.IsChecked == true) winArgs += " --dry-run";
            if (cap != "0")                  winArgs += $" --vram-cap {cap}";
            if (resume)                      winArgs += " --resume";
            psi = new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/c python {winArgs} >> \"{ForgeLogPath}\" 2>&1",
                WorkingDirectory = _pitRoot, UseShellExecute = false, CreateNoWindow = true,
            };
        }
#else
        try
        {
            // Non-Windows: write a temp shell script so that:
            // 1) all args are shell-quoted (no injection), and
            // 2) log-file redirection is owned by the shell, not this process
            //    (the log stays open even if the Avalonia app crashes mid-training).
            static string ShQ(string s) => "'" + s.Replace("'", "'\\''") + "'";
            var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python3";
            script = Path.Combine(Path.GetTempPath(), $"orc_forge_{DateTime.Now:yyyyMMdd_HHmmss}.sh");
            var sb = new System.Text.StringBuilder("#!/bin/sh\n");
            sb.Append(ShQ(python)).Append(" -u ").Append(ShQ(scriptPath));
            sb.Append(" --base ").Append(ShQ(hfRepo));
            sb.Append(" --train ").Append(ShQ(trainJsonl));
            sb.Append(" --eval ").Append(ShQ(evalJsonl));
            sb.Append(" --out ").Append(ShQ(adapterDir));
            if (ChkDryRun.IsChecked == true) sb.Append(" --dry-run");
            if (cap != "0") sb.Append(" --vram-cap ").Append(cap);
            if (resume)     sb.Append(" --resume");
            sb.Append(" >> ").Append(ShQ(ForgeLogPath)).Append(" 2>&1\n");
            File.WriteAllText(script, sb.ToString());
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh", WorkingDirectory = _pitRoot,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(script);
        }
        catch (Exception ex)
        {
            ForgeStatus.Text = $"Failed to prepare trainer script ({ex.Message})";
            OnActivity?.Invoke($"🏛 Academy launch failed: {ex.Message}");
            return;
        }
#endif
        try   { _forgeProcess = Process.Start(psi); }
        catch (Exception ex)
        {
            if (script is not null) try { File.Delete(script); } catch { }
            _forgeProcess = null;
            ForgeStatus.Text = $"Failed to launch the trainer ({ex.Message})";
            OnActivity?.Invoke($"🏛 Academy launch failed: {ex.Message}");
            return;
        }
        if (_forgeProcess is null)
        {
            if (script is not null) try { File.Delete(script); } catch { }
            ForgeStatus.Text = "Failed to launch the trainer."; return;
        }
        if (script is not null)
        {
            var s = script;
            _forgeProcess.EnableRaisingEvents = true;
            _forgeProcess.Exited += (_, _) => { try { File.Delete(s); } catch { } };
        }

        OnActivity?.Invoke($"🏛 ORC ACADEMY {(resume ? "resumed" : "started")}" +
                           (cap != "0" ? $" (VRAM cap {cap} GB)" : "") +
                           (ChkDryRun.IsChecked == true ? " — dry run" : ""));
        ForgeBadge.Text            = "";
        ForgeStatus.Text           = "Launching trainer…";
        ForgeBar.Value             = 0;
        ForgeBar.IsIndeterminate   = true;
        ForgeDot.IsVisible         = true;
        RefreshForge();

        _forgeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _forgeTimer.Tick -= ForgeTimer_Tick;
        _forgeTimer.Tick += ForgeTimer_Tick;
        _forgeTimer.Start();
    }

    private void BtnForgeStop_Click(object? s, RoutedEventArgs e)
    {
        if (!ForgeRunning) return;
        try { _forgeProcess!.Kill(entireProcessTree: true); _forgeProcess.WaitForExit(5000); } catch { }
        OnActivity?.Invoke("🏛 Academy stopped — checkpoints kept.");
        ForgeStatus.Text = "Stopped. Checkpoints kept — Resume continues from the last one.";
        ForgeDone();
    }

    private void ForgeTimer_Tick(object? s, EventArgs e)
    {
        if (!ForgeRunning)
        {
            var ok = File.Exists(SummaryPath) &&
                     File.GetLastWriteTime(SummaryPath) > DateTime.Now.AddMinutes(-5);
            if (ok) OnActivity?.Invoke("🏛 Academy finished — adapter saved.");
            else
            {
                var tail = File.Exists(ForgeLogPath)
                    ? string.Join(" ", File.ReadLines(ForgeLogPath).TakeLast(3)) : "no log";
                ForgeStatus.Text = Truncate($"Trainer exited unexpectedly — {tail}", 160);
                OnActivity?.Invoke("🏛 Academy exited unexpectedly — see forge.log");
            }
            ForgeDone();
            return;
        }

        _forgeDotOn = !_forgeDotOn;
        ForgeDot.Opacity = _forgeDotOn ? 1.0 : 0.35;
        if (!File.Exists(ProgressPath)) return;

        try
        {
            var beatAge = DateTime.Now - File.GetLastWriteTime(ProgressPath);
            if (File.Exists(ForgeLogPath))
            {
                var logAge = DateTime.Now - File.GetLastWriteTime(ForgeLogPath);
                if (logAge < beatAge) beatAge = logAge;
            }

            var p = JsonDocument.Parse(File.ReadAllText(ProgressPath)).RootElement;
            var status = p.GetProperty("status").GetString() ?? "?";

            var limit = status is "loading_model" or "starting"
                ? TimeSpan.FromMinutes(25) : TimeSpan.FromMinutes(10);
            if (beatAge > limit)
            {
                ForgeDot.Fill    = new SolidColorBrush(Colors.Red);
                ForgeStatus.Text = $"⚠ Possibly hung — no heartbeat for {beatAge.TotalMinutes:F0} min (phase: {status}).";
                return;
            }
            ForgeDot.Fill = new SolidColorBrush(Color.Parse("#E8A030"));

            int step = p.TryGetProperty("step",      out var st) ? st.GetInt32() : 0;
            int max  = p.TryGetProperty("max_steps", out var mx) ? mx.GetInt32() : 0;
            ForgeBar.IsIndeterminate = max <= 0;
            if (max > 0) ForgeBar.Value = Math.Min(100.0, step * 100.0 / max);

            ForgeStatus.Text = status switch
            {
                "starting"      => "Preparing — loading dataset…",
                "loading_model" => "Loading base model (4-bit quantization)…",
                "training"      => $"Training — step {step}/{max}",
                "evaluating"    => $"Evaluating at step {step}…",
                "final_eval"    => "Final evaluation…",
                "saving"        => "Saving adapter…",
                "done"          => "Done.",
                _               => status,
            };

            string M(string k) => p.TryGetProperty(k, out var v) &&
                v.ValueKind == JsonValueKind.Number ? v.GetDouble().ToString("F3") : "";
            var bits = new[] { ("loss", M("loss")), ("eval", M("eval_loss")), ("ep", M("epoch")) }
                .Where(t => t.Item2.Length > 0).Select(t => $"{t.Item1} {t.Item2}");
            ForgeMetrics.Text = string.Join("  ·  ", bits);
        }
        catch { }
    }

    private void ForgeDone()
    {
        _forgeTimer?.Stop();
        ForgeBar.IsIndeterminate = false;
        ForgeDot.IsVisible       = false;
        ForgeDot.Fill            = new SolidColorBrush(Color.Parse("#E8A030"));
        RefreshForge();
    }

    // ── Generator ─────────────────────────────────────────────────────────────

    private void PopulateGenModelPicker(List<Services.TrainingPitRegistry.OllamaModelInfo> models)
    {
        var prevSel = (CbGenDatasetModel.SelectedItem as HarvestModelOptionAva)?.Name;
        var opts = models.Select(m => new HarvestModelOptionAva
        {
            Name = m.Name, SizeText = $"{m.SizeGb:F1} GB", IsBlocked = false,
        }).ToList();
        CbGenDatasetModel.ItemsSource = opts;
        CbGenDatasetModel.SelectedItem =
            opts.FirstOrDefault(o => o.Name == prevSel) ??
            opts.FirstOrDefault(o => o.Name == "qwen2.5-coder:14b") ??
            opts.FirstOrDefault(o => !IsBossFamily(o.Name)) ??
            opts.FirstOrDefault();
    }


    private void RefreshGen()
    {
        BtnGenStart.IsEnabled = !GenRunning;
        BtnGenStop.IsEnabled  = GenRunning;

        if (!GenRunning && File.Exists(GenProgressPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(GenProgressPath));
                var p   = doc.RootElement;
                var sts = p.GetProperty("status").GetString() ?? "";
                var gen = p.TryGetProperty("generated", out var g) ? g.GetInt32() : 0;
                var rej = p.TryGetProperty("rejected",  out var r) ? r.GetInt32() : 0;
                if (sts == "done")
                {
                    GenBadge.Text  = $"✓ {gen} generated";
                    GenStatus.Text = $"Done — {gen} examples, {rej} rejected";
                    GenBar.Value   = 100;
                    if (_genTargetIsToolcaller)
                    {
                        BtnGenLoadFoundry.IsVisible = true;
                        BtnGenLoadAcademy.IsVisible = false;
                    }
                    else
                    {
                        BtnGenLoadAcademy.IsVisible = true;
                        BtnGenLoadFoundry.IsVisible = false;
                    }
                }
            }
            catch { }
        }

        // Populate notes box from existing .meta.json sidecar
        if (!GenRunning && TbGenNotes.Text?.Length == 0)
        {
            var dsDir = Path.Combine(_pitRoot, "training_pit", "datasets");
            var existing = Services.TrainingPitRegistry.ReadMetaDescription(dsDir, _genKey);
            if (existing.Length > 0) TbGenNotes.Text = existing;
        }

        var trainFile = Path.Combine(_pitRoot, "training_pit", "datasets", $"train_{_genKey}.jsonl");
        BtnGenViewFile.IsEnabled   = File.Exists(trainFile);
        BtnGenOpenFolder.IsEnabled = Directory.Exists(GenOutDir);
    }

    private void BtnGenStart_Click(object? s, RoutedEventArgs e)
    {
        if (GenRunning) return;
        if (ForgeRunning)   { OnActivity?.Invoke("🧪 Generator refused: Forge is using the GPU."); return; }
        if (FoundryRunning) { OnActivity?.Invoke("🧪 Generator refused: Foundry is using the GPU."); return; }
        if (ArenaRunning)   { OnActivity?.Invoke("🧪 Generator refused: Arena benchmark is using the GPU."); return; }

        // Native and Claude backends don't need an Ollama model selected
        var activeBackend = _genTargetIsToolcaller ? _genBackend : "ollama";
        var needsOllamaModel = activeBackend == "ollama";
        var model = (CbGenDatasetModel.SelectedItem as HarvestModelOptionAva)?.Name ?? "";
        if (needsOllamaModel && model.Length == 0)
        { OnActivity?.Invoke("🧪 Generator: select a model first."); return; }

        var key = TbGenKey.Text?.Trim() ?? "";
        if (key.Length == 0 || key.Contains(' ') || key.Contains('/') || key.Contains('\\'))
        { OnActivity?.Invoke("🧪 Generator: dataset key must be a single word (e.g. v4gold)."); return; }

        if (!int.TryParse(TbGenCount.Text, out var count) || count < 10 || count > 2000)
        { OnActivity?.Invoke("🧪 Generator: count must be 10–2000."); return; }

        _genKey = key;
        Directory.CreateDirectory(GenOutDir);
        try { File.Delete(GenProgressPath); } catch { }

        var scriptPath = _genTargetIsToolcaller
            ? Path.Combine(_pitRoot, "training_pit", "foundry", "scripts", "generate_toolcaller_dataset.py")
            : Path.Combine(_pitRoot, "training_pit", "scripts", "generate_v4gold.py");
        if (!File.Exists(scriptPath))
        { OnActivity?.Invoke($"🧪 Generator: script not found at {scriptPath}"); return; }

        File.WriteAllText(GenLogPath, $"=== generate run {DateTime.Now:yyyy-MM-dd HH:mm} ===\n");

        // Build backend-specific argument fragment shared by both OS paths
        string BackendArgs() => activeBackend switch
        {
            "native" => "--api native",
            "claude" => "--api claude",
            _        => $"--api ollama --model \"{model}\" --ollama-host \"{OllamaHost}\"",
        };

        ProcessStartInfo psi;
        string? tmpScript = null;
#if WINDOWS
        {
            string coreArgs = _genTargetIsToolcaller
                ? $"-u \"{scriptPath}\" {BackendArgs()} --count {count} --key \"{key}\""
                : $"-u \"{scriptPath}\" --model \"{model}\" --count {count} --key \"{key}\" --ollama-host \"{OllamaHost}\"";
            psi = new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/c python {coreArgs} >> \"{GenLogPath}\" 2>&1",
                WorkingDirectory = _pitRoot, UseShellExecute = false, CreateNoWindow = true,
            };
        }
#else
        try
        {
            static string ShQ(string v) => "'" + v.Replace("'", "'\\''") + "'";
            var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python3";
            tmpScript = Path.Combine(Path.GetTempPath(), $"orc_gen_{DateTime.Now:yyyyMMdd_HHmmss}.sh");
            var sb = new System.Text.StringBuilder("#!/bin/sh\n");
            sb.Append(ShQ(python)).Append(" -u ").Append(ShQ(scriptPath));
            if (_genTargetIsToolcaller)
            {
                switch (activeBackend)
                {
                    case "native":
                        sb.Append(" --api native");
                        break;
                    case "claude":
                        sb.Append(" --api claude");
                        break;
                    default:
                        sb.Append(" --api ollama");
                        sb.Append(" --model ").Append(ShQ(model));
                        sb.Append(" --ollama-host ").Append(ShQ(OllamaHost));
                        break;
                }
            }
            else
            {
                sb.Append(" --model ").Append(ShQ(model));
                sb.Append(" --ollama-host ").Append(ShQ(OllamaHost));
            }
            sb.Append($" --count {count}");
            sb.Append(" --key ").Append(ShQ(key));
            sb.Append(" >> ").Append(ShQ(GenLogPath)).Append(" 2>&1\n");
            File.WriteAllText(tmpScript, sb.ToString());
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh", WorkingDirectory = _pitRoot,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(tmpScript);
        }
        catch (Exception ex)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            OnActivity?.Invoke($"🧪 Generator launch failed: {ex.Message}");
            return;
        }
#endif
        try { _genProcess = Process.Start(psi); }
        catch (Exception ex)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            OnActivity?.Invoke($"🧪 Generator launch failed: {ex.Message}");
            return;
        }
        if (_genProcess is null)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            OnActivity?.Invoke("🧪 Generator failed to start.");
            return;
        }
        if (tmpScript is not null)
        {
            var ts = tmpScript;
            _genProcess.EnableRaisingEvents = true;
            _genProcess.Exited += (_, _) => { try { File.Delete(ts); } catch { } };
        }

        var backendLabel = _genTargetIsToolcaller
            ? activeBackend switch { "native" => "native-runtime", "claude" => "claude-haiku", _ => $"ollama:{model}" }
            : $"ollama:{model}";
        OnActivity?.Invoke($"🧪 Generator started — backend={backendLabel}, target={count}, key={key}");
        GenBadge.Text          = "";
        GenStatus.Text         = "Starting…";
        GenBar.Value           = 0;
        GenBar.IsIndeterminate = true;
        GenDot.IsVisible       = true;
        RefreshGen();

        _genTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _genTimer.Tick -= GenTimer_Tick;
        _genTimer.Tick += GenTimer_Tick;
        _genTimer.Start();

        BtnGenStart.IsEnabled = false;
        BtnGenStop.IsEnabled  = true;
    }

    private void BtnGenStop_Click(object? s, RoutedEventArgs e)
    {
        if (!GenRunning) return;
        try { _genProcess!.Kill(entireProcessTree: true); _genProcess.WaitForExit(5000); } catch { }
        OnActivity?.Invoke("🧪 Generator stopped — partial dataset kept.");
        GenStatus.Text = "Stopped.";
        GenDone();
    }

    private void GenTimer_Tick(object? s, EventArgs e)
    {
        if (!GenRunning)
        {
            var ok = File.Exists(GenProgressPath) &&
                     File.GetLastWriteTime(GenProgressPath) > DateTime.Now.AddMinutes(-2);
            if (ok)
            {
                OnActivity?.Invoke("🧪 Generator finished — dataset saved.");
                Refresh(); // new JSONL pair now shows up in the Forge dataset picker
            }
            else
            {
                var tail = File.Exists(GenLogPath)
                    ? string.Join(" ", File.ReadLines(GenLogPath).TakeLast(2)) : "no log";
                GenStatus.Text = Truncate($"Generator exited unexpectedly — {tail}", 160);
                OnActivity?.Invoke("🧪 Generator exited unexpectedly — check gen.log");
            }
            GenDone();
            return;
        }

        _genDotOn  = !_genDotOn;
        GenDot.Opacity = _genDotOn ? 1.0 : 0.35;

        if (!File.Exists(GenProgressPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(GenProgressPath));
            var p        = doc.RootElement;
            var sts      = p.GetProperty("status").GetString() ?? "?";
            var gen      = p.TryGetProperty("generated",  out var g)  ? g.GetInt32()  : 0;
            var rej      = p.TryGetProperty("rejected",   out var r)  ? r.GetInt32()  : 0;
            var tgt      = p.TryGetProperty("target",     out var t)  ? t.GetInt32()  : 0;
            var lastGoal = p.TryGetProperty("last_goal",  out var lg) ? lg.GetString() ?? "" : "";

            GenBar.IsIndeterminate = tgt <= 0;
            if (tgt > 0) GenBar.Value = Math.Min(100.0, gen * 100.0 / tgt);

            var goalSnip = lastGoal.Length > 60 ? lastGoal[..60] + "…" : lastGoal;
            GenStatus.Text = sts switch
            {
                "starting"   => "Starting…",
                "generating" => $"Generating {gen}/{tgt} — {goalSnip}",
                "done"       => $"Done — {gen} generated, {rej} rejected",
                _            => sts,
            };
            GenMetrics.Text = tgt > 0 ? $"{gen}/{tgt}  ·  {rej} rej" : "";
        }
        catch { }
    }

    private void GenDone()
    {
        _genTimer?.Stop();
        _genProcess?.Dispose();
        _genProcess = null;
        GenBar.IsIndeterminate = false;
        GenDot.IsVisible       = false;
        RefreshGen();
    }

    private void DatasetOpenFolder_Click(object? s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.Tag is not DatasetInfoAva item) return;
        var folder = item.FilePath.Length > 0
            ? (Directory.Exists(item.FilePath) ? item.FilePath : Path.GetDirectoryName(item.FilePath) ?? "")
            : Path.Combine(_pitRoot, "training_pit", "datasets");
        if (folder.Length > 0 && Directory.Exists(folder))
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
    }

    private void BtnGenSaveNotes_Click(object? s, RoutedEventArgs e)
    {
        var notes = TbGenNotes.Text?.Trim() ?? "";
        var dsDir = Path.Combine(_pitRoot, "training_pit", "datasets");
        Services.TrainingPitRegistry.WriteMetaDescription(dsDir, _genKey, notes);
        OnActivity?.Invoke($"🧪 Notes saved for dataset '{_genKey}'.");
        Refresh();
    }

    private void BtnGenOpenFolder_Click(object? s, RoutedEventArgs e)
    {
        if (Directory.Exists(GenOutDir))
            Process.Start(new ProcessStartInfo(GenOutDir) { UseShellExecute = true })?.Dispose();
        else
            OnActivity?.Invoke("🧪 Generator: output folder not found — generate a dataset first.");
    }

    private void BtnGenViewFile_Click(object? s, RoutedEventArgs e)
    {
        var trainFile = Path.Combine(_pitRoot, "training_pit", "datasets", $"train_{_genKey}.jsonl");
        if (File.Exists(trainFile))
            Process.Start(new ProcessStartInfo(trainFile) { UseShellExecute = true })?.Dispose();
        else
            OnActivity?.Invoke($"🧪 Generator: train_{_genKey}.jsonl not found.");
    }

    private void CbGenTarget_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        _genTargetIsToolcaller = CbGenTarget.SelectedIndex == 1;

        if (_genTargetIsToolcaller)
        {
            TbGenTargetNote.Text = "Synthetic toolcaller examples covering all 4 decision types (call/no_tool/clarify/unsupported) across all 4 worker roles. Output goes to training_pit/datasets/toolcaller/ — feeds THE FOUNDRY (Stage 3).";
            TbGenFieldHint.Text  = "Requires the frozen tool schema (training_pit/schemas/toolcaller_v0_frozen_tools.json). The decision type is predetermined per example — the model only fills in realistic request text and arguments.";
            if (TbGenKey.Text is "v4gold" or "")
                TbGenKey.Text = "toolcaller";
            BorderGenBackend.IsVisible = true;
        }
        else
        {
            TbGenTargetNote.Text = "Synthetic Warchief boss plans for the orchestration role. Output feeds ORC ACADEMY (Stage 2).";
            TbGenFieldHint.Text  = "Every CODER/UIDEVELOPER task must name its output file(s) in the title — plans that omit filenames are rejected automatically.";
            if (TbGenKey.Text is "toolcaller" or "")
                TbGenKey.Text = "v4gold";
            BorderGenBackend.IsVisible = false;
        }

        // Reset post-generation buttons — a fresh target selection means a fresh run is coming
        BtnGenLoadAcademy.IsVisible  = false;
        BtnGenLoadFoundry.IsVisible  = false;
    }

    private void CbGenBackend_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        _genBackend = CbGenBackend.SelectedIndex switch
        {
            0 => "native",
            1 => "claude",
            _ => "ollama",
        };
        TbGenBackendNote.Text = _genBackend switch
        {
            "native" => "Fully local — uses whatever model is loaded in the native runtime (port 8080). Requires a ≥3B model.",
            "claude" => "Uses claude-haiku-4-5-20251001. Fast, high quality, no local GPU required. Reads ANTHROPIC_API_KEY env var.",
            _        => $"Uses local Ollama model selected below. Requires Ollama at {OllamaHost}.",
        };
    }

    private void BtnGenLoadAcademy_Click(object? s, RoutedEventArgs e)
    {
        var key = TbGenKey.Text?.Trim() ?? _genKey;
        if (key.Length == 0) return;
        var dsDir     = Path.Combine(_pitRoot, "training_pit", "datasets");
        var trainPath = Path.Combine(dsDir, $"train_{key}.jsonl");
        var match = (CbDataset.ItemsSource as IEnumerable<DatasetOptionAva>)
            ?.FirstOrDefault(d => d.TrainPath == trainPath || d.Name == key);
        if (match is not null)
        {
            CbDataset.SelectedItem = match;
            OnActivity?.Invoke($"→ Dataset '{key}' loaded into ORC ACADEMY — configure the base model in Stage 2 and click Start training.");
        }
        else
        {
            _ = ReloadModelsAsync().ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                var m2 = (CbDataset.ItemsSource as IEnumerable<DatasetOptionAva>)
                    ?.FirstOrDefault(d => d.TrainPath == trainPath || d.Name == key);
                if (m2 is not null) { CbDataset.SelectedItem = m2; OnActivity?.Invoke($"→ Dataset '{key}' loaded into ORC ACADEMY."); }
                else OnActivity?.Invoke($"→ Dataset '{key}' not found — check training_pit/datasets/train_{key}.jsonl exists.");
            }), TaskScheduler.Default);
        }
    }

    private void BtnGenLoadFoundry_Click(object? s, RoutedEventArgs e)
    {
        RefreshFoundry();
        OnActivity?.Invoke("⚒ Toolcaller captures added — check THE FOUNDRY gate card, then click 'Validate captures' → 'Export dataset' → 'Train toolcaller'.");
    }

    // ── Registry rendering ────────────────────────────────────────────────────

    private void RenderDatasets(List<Services.TrainingPitRegistry.DatasetInfo> datasets)
    {
        DatasetList.ItemsSource  = datasets.Select(DatasetInfoAva.From).ToList();
        DatasetCount.Text        = datasets.Count.ToString();
        DatasetEmpty.IsVisible   = datasets.Count == 0;
        _cachedDatasets = datasets;
        PopulateForgePickersIfReady();
    }

    private void PopulateForgePickersIfReady()
    {
        if (_cachedDatasets.Count > 0)
            PopulateForgePickers(_cachedModels, _cachedDatasets);
    }

    private void RenderAdapters(List<Services.TrainingPitRegistry.AdapterInfo> adapters)
    {
        var rows = adapters.Select(a => new AdapterRowAva(a)).ToList();
        AdapterList.ItemsSource = rows;
        AdapterCount.Text       = adapters.Count.ToString();
        AdapterEmpty.IsVisible  = adapters.Count == 0;
    }

    private async Task ReloadModelsAsync()
    {
        var models = await Services.TrainingPitRegistry.LoadModelsAsync(OllamaHost);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var rows = models.Select(m => new ModelRowAva(m)).ToList();
            ModelList.ItemsSource = rows;
            ModelCount.Text       = models.Count.ToString();
            ModelEmpty.IsVisible  = models.Count == 0;
            PopulateHarvestPickers(models);
            PopulateGenModelPicker(models);
            _cachedModels = models;
            PopulateForgePickersIfReady();
        });
    }

    private void PopulateForgePickers(
        List<Services.TrainingPitRegistry.OllamaModelInfo> models,
        List<Services.TrainingPitRegistry.DatasetInfo>     datasets)
    {
        var prevBase = (CbBaseModel.SelectedItem as BaseModelOptionAva)?.OllamaName;
        var baseOpts = models.Select(m => new BaseModelOptionAva
        {
            OllamaName = m.Name,
            HfRepo     = MapOllamaToHfRepo(m.Name),
            Reason     = ReasonForUntrainable(m.Name),
        }).ToList();
        CbBaseModel.ItemsSource = baseOpts;
        CbBaseModel.SelectedItem =
            baseOpts.FirstOrDefault(o => o.OllamaName == prevBase && o.HfRepo.Length > 0) ??
            baseOpts.FirstOrDefault(o => o.OllamaName == "gemma4:12b") ??
            baseOpts.FirstOrDefault(o => o.HfRepo.Length > 0);

        var prevDs = (CbDataset.SelectedItem as DatasetOptionAva)?.Name;
        var dsDir  = Path.Combine(_pitRoot, "training_pit", "datasets");
        var dsOpts = datasets.Select(d => new DatasetOptionAva(d, dsDir)).ToList();
        CbDataset.ItemsSource = dsOpts;
        CbDataset.SelectedItem = dsOpts.FirstOrDefault(o => o.Name == prevDs) ?? dsOpts.FirstOrDefault();
    }

    // ── Review captures ───────────────────────────────────────────────────────

    private void RefreshReviewCaptures()
    {
        if (_pitRoot.Length == 0) return;
        var info = Services.TrainingPitRegistry.LoadReviewCaptures(_pitRoot);

        ReviewProgressBar.Maximum = info.Count >= ReviewExperimentalGate
            ? ReviewProductionGate : ReviewExperimentalGate;
        ReviewProgressBar.Value = Math.Min(info.Count, (int)ReviewProgressBar.Maximum);

        var sinceText = info.Count == 0 ? "never" : HumanizeAge(DateTime.Now - info.LatestAt) + " ago";
        ReviewStatus.Text = $"{info.Count} staged · last: {sinceText}";

        ReviewThreshold.Text = info.Count >= ReviewProductionGate
            ? $"{info.Count} captures — ready for the real reviewer adapter"
            : info.Count >= ReviewExperimentalGate
                ? $"{info.Count} of {ReviewProductionGate} toward the production adapter"
                : $"{info.Count} of {ReviewExperimentalGate} toward an experimental adapter";
        ReviewLatest.Text = info.LatestSummary.Length > 0 ? $"latest: {info.LatestSummary}" : "";

        var gpuFree = !ReviewRunning && !HarvestRunning && !ForgeRunning && !FoundryRunning;
        BtnReviewNow.IsEnabled        = gpuFree;
        BtnCaptureIncrement.IsEnabled = gpuFree;
        if (!gpuFree)
        {
            var reason = ReviewRunning   ? "Review already running."
                       : HarvestRunning ? "Harvest is using the GPU."
                                        : "Training is using the GPU.";
            ToolTip.SetTip(BtnReviewNow,        reason);
            ToolTip.SetTip(BtnCaptureIncrement, reason);
        }

        RefreshCaptureMarkerStatus();
    }

    private void RefreshCaptureMarkerStatus()
    {
        try
        {
            if (string.IsNullOrEmpty(_pitRoot)) return;
            var markerPath = Path.Combine(_pitRoot, ".orc", "capture-marker.json");
            if (!File.Exists(markerPath)) { CaptureMarkerStatus.Text = ""; return; }

            using var doc = JsonDocument.Parse(File.ReadAllText(markerPath));
            var lastSha = doc.RootElement.TryGetProperty("last_captured_sha", out var sha)
                ? sha.GetString() ?? "" : "";
            var total = doc.RootElement.TryGetProperty("total_captures", out var t) ? t.GetInt32() : 0;
            if (string.IsNullOrEmpty(lastSha)) { CaptureMarkerStatus.Text = ""; return; }

            var psi = new ProcessStartInfo("git", $"rev-list --count {lastSha}..HEAD")
            {
                WorkingDirectory = _pitRoot, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) { CaptureMarkerStatus.Text = ""; return; }
            var countStr = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit();

            if (!int.TryParse(countStr, out var uncaptured)) { CaptureMarkerStatus.Text = ""; return; }
            CaptureMarkerStatus.Text = uncaptured == 0
                ? $"marker: {lastSha} · all commits captured ({total} total)"
                : $"marker: {lastSha} · {uncaptured} new commit{(uncaptured == 1 ? "" : "s")} since last capture → ⬇ to capture";
        }
        catch { CaptureMarkerStatus.Text = ""; }
    }

    private string DetectBranchRange()
    {
        try
        {
            var psi = new ProcessStartInfo("git", "rev-parse --abbrev-ref --symbolic-full-name @{u}")
            {
                WorkingDirectory = _pitRoot, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return "";
            var upstream = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return (p.ExitCode != 0 || upstream.Length == 0) ? "" : $"{upstream}..HEAD";
        }
        catch { return ""; }
    }

    private void BtnReviewNow_Click(object? s, RoutedEventArgs e)
    {
        if (ReviewRunning || HarvestRunning || ForgeRunning || FoundryRunning)
        {
            OnActivity?.Invoke("🔍 Review refused — GPU is in use.");
            return;
        }
        var script = Path.Combine(_pitRoot, "tools", "review-capture.ps1");
        if (!File.Exists(script)) { OnActivity?.Invoke($"🔍 Review tool missing: {script}"); return; }

        BtnReviewNow.IsEnabled = false;
        BtnReviewNow.Content   = "🔍 Reviewing…";

        var range = DetectBranchRange();
        var args  = range.Length > 0
            ? $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -Range \"{range}\""
            : $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"";
        OnActivity?.Invoke(range.Length > 0
            ? $"🔍 Review started — {range}."
            : "🔍 Review started — staged changes.");

        try
        {
            _reviewProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh", Arguments = args, WorkingDirectory = _pitRoot,
                UseShellExecute = false, CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            OnActivity?.Invoke($"🔍 Review launch failed: {ex.Message}");
            BtnReviewNow.IsEnabled = true;
            BtnReviewNow.Content   = "▶ Review branch";
            return;
        }

        if (_reviewProcess is null) { BtnReviewNow.IsEnabled = true; BtnReviewNow.Content = "▶ Review branch"; return; }
        _reviewProcess.EnableRaisingEvents = true;
        _reviewProcess.Exited += (_, _) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            BtnReviewNow.Content = "▶ Review branch";
            RefreshReviewCaptures();
            OnActivity?.Invoke($"🔍 Review finished (exit {_reviewProcess?.ExitCode}).");
        });
    }

    private void BtnCaptureIncrement_Click(object? s, RoutedEventArgs e)
    {
        if (ReviewRunning || HarvestRunning || ForgeRunning || FoundryRunning)
        {
            OnActivity?.Invoke("⬇ Capture refused — GPU is in use.");
            return;
        }
        var script = Path.Combine(_pitRoot, "tools", "auto-capture.ps1");
        if (!File.Exists(script)) { OnActivity?.Invoke($"⬇ auto-capture.ps1 not found: {script}"); return; }

        BtnCaptureIncrement.Content   = "⬇ Capturing…";
        BtnCaptureIncrement.IsEnabled = false;
        BtnReviewNow.IsEnabled        = false;

        var args = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -OllamaHost \"{OllamaHost}\"";
        OnActivity?.Invoke("⬇ Capture increment started — commits since last marker.");

        try
        {
            _reviewProcess = Process.Start(new ProcessStartInfo
            {
                FileName = "pwsh", Arguments = args, WorkingDirectory = _pitRoot,
                UseShellExecute = false, CreateNoWindow = true,
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
        _reviewProcess.Exited += (_, _) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var exitCode = _reviewProcess?.ExitCode ?? -1;
            BtnCaptureIncrement.Content = "⬇ Capture increment";
            RefreshReviewCaptures();
            RefreshCaptureMarkerStatus();
            OnActivity?.Invoke(exitCode switch
            {
                0 => "⬇ Capture complete — marker advanced.",
                1 => "⬇ No new commits since last capture.",
                2 => "⬇ Partial capture saved.",
                3 => "⬇ Both reviewers failed — marker NOT advanced.",
                _ => $"⬇ Capture finished (exit {exitCode}).",
            });
        });
    }

    // ── THE FOUNDRY (specialist model training suite) ─────────────────────────
    // Wraps training_pit/foundry/: per-specialist recipe configs + gated scripts.
    // First proof track: theorc-toolcaller (docs/THEORC_TOOLCALLER_V0.md).

    private Process?         _foundryProcess;
    private DispatcherTimer? _foundryTimer;
    private bool _foundryDotOn;

    // ── ARENA (benchmark) ─────────────────────────────────────────────────────
    private Process?         _arenaProcess;
    private DispatcherTimer? _arenaTimer;
    private bool   _arenaDotOn;
    private string _arenaOutDir = "";

    private bool ArenaRunning => _arenaProcess is { HasExited: false };

    private string FoundryDir          => Path.Combine(_pitRoot, "training_pit", "foundry");
    private string FoundryConfigPath   => Path.Combine(FoundryDir, "configs", "toolcaller_v0.json");
    private string FoundryOutDir       => Path.Combine(_pitRoot, "training_pit", "outputs", "foundry_toolcaller_v0");
    private string FoundryProgressPath => Path.Combine(FoundryOutDir, "progress.json");
    private string FoundrySummaryPath  => Path.Combine(FoundryOutDir, "training_summary.json");
    private string FoundryLogPath      => Path.Combine(FoundryOutDir, "foundry.log");
    private string ToolcallerStagingDir  => Path.Combine(_pitRoot, ".orc", "swarm", "dataset-staging", "toolcaller");
    private string ToolcallerAcceptedDir => Path.Combine(_pitRoot, "training_pit", "datasets", "toolcaller");
    private bool   FoundryRunning => _foundryProcess is { HasExited: false };


    private void RefreshFoundry()
    {
        // Track list from the recipe configs — the configs are the registry.
        var configsDir = Path.Combine(FoundryDir, "configs");
        var tracks = new List<FoundryTrackAva>();
        if (Directory.Exists(configsDir))
        {
            foreach (var file in Directory.GetFiles(configsDir, "*.json"))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(file));
                    var root = doc.RootElement;
                    tracks.Add(FoundryTrackAva.From(
                        root.TryGetProperty("display_name", out var n) ? n.GetString() ?? "" : Path.GetFileNameWithoutExtension(file),
                        root.TryGetProperty("foundry_track", out var t) ? t.GetString() ?? "" : "",
                        root.TryGetProperty("status", out var st) ? st.GetString() ?? "" : "",
                        root.TryGetProperty("size_hypothesis", out var sz) ? sz.GetString() ?? "" : "",
                        root.TryGetProperty("base_model", out var bm) &&
                        bm.TryGetProperty("hf_repo", out var hf) ? hf.GetString() ?? "" : ""));
                }
                catch { /* an unparseable recipe just doesn't render */ }
            }
        }
        // docs/THEORC_FOUNDRY.md follow-on order, active tracks first within it
        string[] order = ["theorc-toolcaller", "theorc-dataset-judge", "theorc-fabric",
                          "theorc-router", "theorc-reviewer", "theorc-boss"];
        FoundryTrackList.ItemsSource = tracks
            .OrderBy(t => { var i = Array.IndexOf(order, t.Track); return i < 0 ? order.Length : i; })
            .ToList();
        FoundryBadge.Text = tracks.Count(t => t.IsActive) is var active && active > 0
            ? $"{active} active / {tracks.Count} tracks" : "";

        // Toolcaller pipeline counts
        int staged   = Directory.Exists(ToolcallerStagingDir)  ? Directory.GetFiles(ToolcallerStagingDir,  "*.json").Length : 0;
        int accepted = Directory.Exists(ToolcallerAcceptedDir) ? Directory.GetFiles(ToolcallerAcceptedDir, "*.json").Length : 0;
        var exported = "not exported";
        var metaPath = Path.Combine(_pitRoot, "training_pit", "datasets", "toolcaller_v0.meta.json");
        if (File.Exists(metaPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var m = doc.RootElement;
                exported = $"exported train {m.GetProperty("train_count").GetInt32()} / " +
                           $"eval {m.GetProperty("eval_count").GetInt32()}";
            }
            catch { exported = "meta unreadable"; }
        }
        UpdateFoundryGateCard(staged, accepted, exported);

        if (!FoundryRunning && File.Exists(FoundrySummaryPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FoundrySummaryPath));
                var sum = doc.RootElement;
                var dry = sum.TryGetProperty("dry_run", out var d) && d.GetBoolean();
                FoundryStatus.Text = (dry ? "Dry run verified " : "Adapter trained ") +
                                     sum.GetProperty("finished").GetString() +
                                     $" — eval_loss {sum.GetProperty("eval_loss").GetDouble():0.####}";

                if (!dry)
                    RefreshFoundryActivateCard();
            }
            catch { }
        }
    }

    private void RefreshFoundryActivateCard()
    {
        var loraGguf   = FindFoundryLoraGguf();
        var baseGguf   = FindFoundryBaseGguf();
        var visible    = loraGguf is not null && baseGguf is not null;
        BorderFoundryActivate.IsVisible = visible;
        if (visible)
            TbFoundryActivateInfo.Text =
                $"Adapter ready: {Path.GetFileName(loraGguf)}\n" +
                $"Base model:    {Path.GetFileName(baseGguf)}";
    }

    private string? FindFoundryLoraGguf()
    {
        // Prefer the depot, fall back to workspace outputs
        var depotLora = string.IsNullOrEmpty(ModelDepotRoot) ? null
            : Directory.EnumerateFiles(ModelDepotRoot, "theorc-toolcaller-*lora*.gguf")
                       .OrderByDescending(File.GetLastWriteTimeUtc)
                       .FirstOrDefault();
        if (depotLora is not null) return depotLora;

        return Directory.EnumerateFiles(FoundryOutDir, "*lora*.gguf", SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
    }

    private string? FindFoundryBaseGguf()
    {
        if (string.IsNullOrEmpty(ModelDepotRoot)) return null;
        return Directory.EnumerateFiles(ModelDepotRoot, "Qwen2.5-1.5B*.gguf")
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
    }

    private void UpdateFoundryGateCard(int staged, int accepted, string exported)
    {
        const int TrainMin = 150, EvalMin = 30;

        static string GateIcon(bool ok)  => ok ? "✓" : "●";
        static IBrush GateBrush(int val, int gate) => new SolidColorBrush(Color.Parse(
            val >= gate ? "#80C0A0" : val >= gate / 2 ? "#E8A030" : "#E06040"));

        var stagedBrush = GateBrush(staged, TrainMin);
        GateStagedIcon.Text       = GateIcon(staged >= TrainMin);
        GateStagedIcon.Foreground = stagedBrush;
        GateStagedLabel.Text      = staged >= TrainMin
            ? $"{staged} staged captures ✓"
            : $"{staged} staged captures (need {TrainMin - staged} more)";
        GateStagedLabel.Foreground = stagedBrush;
        GateStagedBar.Value       = Math.Min(staged, TrainMin);
        GateStagedBar.Foreground  = stagedBrush;

        var acceptedBrush = GateBrush(accepted, EvalMin);
        GateAcceptedIcon.Text       = GateIcon(accepted >= EvalMin);
        GateAcceptedIcon.Foreground = acceptedBrush;
        GateAcceptedLabel.Text      = accepted >= EvalMin
            ? $"{accepted} accepted ✓"
            : $"{accepted} accepted (need {EvalMin} for eval split)";
        GateAcceptedLabel.Foreground = acceptedBrush;

        var exportOk = exported != "not exported" && exported != "meta unreadable";
        GateExportIcon.Text       = GateIcon(exportOk);
        GateExportIcon.Foreground = new SolidColorBrush(Color.Parse(exportOk ? "#80C0A0" : "#E06040"));
        GateExportLabel.Text      = exportOk ? $"Exported: {exported}" : "Dataset not exported yet — click Export";
        GateExportLabel.Foreground = new SolidColorBrush(Color.Parse(exportOk ? "#A8CC80" : "#999999"));

        // ETA: Qwen2.5-1.5B LoRA r=16, 3 epochs, effective batch 8 (~0.3–1.2 s/step on GPU)
        var samples = Math.Max(staged, accepted);
        if (samples > 0)
        {
            var steps  = (int)Math.Ceiling(samples * 3.0 / 8.0);
            var minSec = (int)(steps * 0.3);
            var maxSec = (int)(steps * 1.2);
            GateTrainEta.Text = samples >= TrainMin
                ? $"Est. training run: {FmtSec(minSec)}–{FmtSec(maxSec)}  ({steps} optimizer steps · Qwen2.5-1.5B LoRA)"
                : $"At {samples} captures: ~{FmtSec(minSec)}–{FmtSec(maxSec)} — collect {TrainMin - samples} more to unlock training";
        }
        else
        {
            GateTrainEta.Text = "Run swarm sessions → 'Capture increment' above to stage toolcaller examples";
        }
    }

    private static string FmtSec(int sec)
        => sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s";

    private async void BtnFoundryValidate_Click(object? s, RoutedEventArgs e)
    {
        var capturesDir = Directory.Exists(ToolcallerStagingDir) &&
                          Directory.GetFiles(ToolcallerStagingDir, "*.json").Length > 0
            ? ToolcallerStagingDir
            : ToolcallerAcceptedDir;
        if (!Directory.Exists(capturesDir) || Directory.GetFiles(capturesDir, "*.json").Length == 0)
        {
            FoundryStatus.Text = "No toolcaller captures found — run swarm sessions to stage organic captures first.";
            return;
        }

        // The apphost is platform-specific: .exe on Windows, extensionless elsewhere
        var benchExe = OperatingSystem.IsWindows() ? "toolcaller-bench.exe" : "toolcaller-bench";
        var bench = new[]
        {
            Path.Combine(_pitRoot, "Tools", "ToolcallerBench", "bin", "Release", "net10.0", benchExe),
            Path.Combine(_pitRoot, "Tools", "ToolcallerBench", "bin", "Debug",   "net10.0", benchExe),
        }.FirstOrDefault(File.Exists);
        if (bench is null)
        {
            FoundryStatus.Text = "toolcaller-bench not built — run: dotnet build Tools/ToolcallerBench -c Release";
            return;
        }

        BtnFoundryValidate.IsEnabled = false;
        FoundryStatus.Text = "Validating captures against the frozen inventory…";
        var tools = Path.Combine(_pitRoot, "training_pit", "schemas", "toolcaller_v0_frozen_tools.json");
        var (code, output) = await RunProcessAsync(bench,
            $"--suite validate --captures \"{capturesDir}\" --tools \"{tools}\"");
        BtnFoundryValidate.IsEnabled = true;

        var verdictLine = output.Split('\n').FirstOrDefault(l => l.StartsWith("Verdict:"))?.Trim();
        FoundryStatus.Text = code switch
        {
            0 => $"✔ {verdictLine ?? "PASS"} — captures satisfy every mechanical admission gate.",
            2 => $"✗ {verdictLine ?? "FAIL"} — see report under .orc/toolcaller-bench.",
            _ => Truncate($"Validator error (exit {code}) — {output}", 160),
        };
        OnActivity?.Invoke($"⚒ Foundry validate: {(code == 0 ? "PASS" : code == 2 ? "FAIL" : "error")}");
    }

    private async void BtnFoundryExport_Click(object? s, RoutedEventArgs e)
    {
        var script = Path.Combine(FoundryDir, "scripts", "export_toolcaller_dataset.py");
        if (!File.Exists(script)) { FoundryStatus.Text = $"Exporter not found: {script}"; return; }

        BtnFoundryExport.IsEnabled = false;
        FoundryStatus.Text = "Exporting accepted captures (validator gate runs first)…";
        var python = OperatingSystem.IsWindows()
            ? "python" : Environment.GetEnvironmentVariable("PYTHON") ?? "python3";
        var (code, output) = await RunProcessAsync(python, $"-u \"{script}\"");
        BtnFoundryExport.IsEnabled = true;

        var tail = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        FoundryStatus.Text = code == 0
            ? "✔ Export complete — dataset appears in the FORGE picker after refresh."
            : Truncate($"Export blocked (exit {code}) — {tail}", 160);
        OnActivity?.Invoke($"⚒ Foundry export: {(code == 0 ? "done" : "blocked")}");
        RefreshFoundry();
        if (code == 0) Refresh();
    }

    private void BtnFoundryTrain_Click(object? s, RoutedEventArgs e)
    {
        if (FoundryRunning) return;
        if (ForgeRunning)                  { OnActivity?.Invoke("⚒ Foundry refused: ORC ACADEMY owns the GPU."); return; }
        if (GenRunning)                    { OnActivity?.Invoke("⚒ Foundry refused: generator owns the GPU."); return; }
        if (ArenaRunning)                  { OnActivity?.Invoke("⚒ Foundry refused: Arena benchmark owns the GPU."); return; }
        if (HarvestRunning || _liveActive) { OnActivity?.Invoke("⚒ Foundry refused: harvest owns the GPU."); return; }
        if (ReviewRunning)                 { OnActivity?.Invoke("⚒ Foundry refused: review owns the GPU."); return; }
        if (!File.Exists(FoundryConfigPath))
        { FoundryStatus.Text = $"Recipe not found: {FoundryConfigPath}"; return; }

        var dryRun = ChkFoundryDryRun.IsChecked == true;
        var scriptPath = Path.Combine(FoundryDir, "scripts", "train_foundry.py");
        Directory.CreateDirectory(FoundryOutDir);
        try { File.Delete(FoundryProgressPath); } catch { }
        // A stale summary would repaint over this run's blocked/failed status in FoundryDone()
        try { File.Delete(FoundrySummaryPath); } catch { }

        File.WriteAllText(FoundryLogPath,
            $"=== foundry run {DateTime.Now:yyyy-MM-dd HH:mm} (dry={dryRun}) ===\n");

        ProcessStartInfo psi;
        string? tmpScript = null;
#if WINDOWS
        {
            var winArgs = $"-u \"{scriptPath}\" --config \"{FoundryConfigPath}\"" +
                          (dryRun ? " --dry-run" : " --confirm-experiment");
            psi = new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/c python {winArgs} >> \"{FoundryLogPath}\" 2>&1",
                WorkingDirectory = _pitRoot, UseShellExecute = false, CreateNoWindow = true,
            };
        }
#else
        try
        {
            static string ShQ(string v) => "'" + v.Replace("'", "'\\''") + "'";
            var python = Environment.GetEnvironmentVariable("PYTHON") ?? "python3";
            tmpScript = Path.Combine(Path.GetTempPath(), $"orc_foundry_{DateTime.Now:yyyyMMdd_HHmmss}.sh");
            var sb = new System.Text.StringBuilder("#!/bin/sh\n");
            sb.Append(ShQ(python)).Append(" -u ").Append(ShQ(scriptPath));
            sb.Append(" --config ").Append(ShQ(FoundryConfigPath));
            sb.Append(dryRun ? " --dry-run" : " --confirm-experiment");
            sb.Append(" >> ").Append(ShQ(FoundryLogPath)).Append(" 2>&1\n");
            File.WriteAllText(tmpScript, sb.ToString());
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh", WorkingDirectory = _pitRoot,
                UseShellExecute = false, CreateNoWindow = true,
            };
            psi.ArgumentList.Add(tmpScript);
        }
        catch (Exception ex)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            OnActivity?.Invoke($"⚒ Foundry launch failed: {ex.Message}");
            return;
        }
#endif
        try { _foundryProcess = Process.Start(psi); }
        catch (Exception ex)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            _foundryProcess = null;
            FoundryStatus.Text = $"Failed to launch the trainer ({ex.Message})";
            return;
        }
        if (_foundryProcess is null)
        {
            if (tmpScript is not null) try { File.Delete(tmpScript); } catch { }
            FoundryStatus.Text = "Failed to launch the trainer.";
            return;
        }
        if (tmpScript is not null)
        {
            var ts = tmpScript;
            _foundryProcess.EnableRaisingEvents = true;
            _foundryProcess.Exited += (_, _) => { try { File.Delete(ts); } catch { } };
        }

        OnActivity?.Invoke($"⚒ Foundry {(dryRun ? "dry run" : "training experiment")} started — theorc-toolcaller");
        FoundryStatus.Text         = "Launching gated trainer…";
        FoundryBar.Value           = 0;
        FoundryBar.IsIndeterminate = true;
        FoundryDot.IsVisible       = true;
        BtnFoundryTrain.IsEnabled  = false;
        BtnFoundryStop.IsEnabled   = true;

        _foundryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _foundryTimer.Tick -= FoundryTimer_Tick;
        _foundryTimer.Tick += FoundryTimer_Tick;
        _foundryTimer.Start();
    }

    private void BtnFoundryStop_Click(object? s, RoutedEventArgs e)
    {
        if (!FoundryRunning) return;
        try { _foundryProcess!.Kill(entireProcessTree: true); _foundryProcess.WaitForExit(5000); } catch { }
        OnActivity?.Invoke("⚒ Foundry stopped — checkpoints kept.");
        FoundryStatus.Text = "Stopped. Checkpoints kept under outputs/foundry_toolcaller_v0.";
        FoundryDone();
    }

    private void FoundryTimer_Tick(object? s, EventArgs e)
    {
        if (!FoundryRunning)
        {
            // Read the final heartbeat before declaring the outcome
            var status = "";
            try
            {
                if (File.Exists(FoundryProgressPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(FoundryProgressPath));
                    status = doc.RootElement.GetProperty("status").GetString() ?? "";
                }
            }
            catch { }
            if (status == "done")
            {
                OnActivity?.Invoke("⚒ Foundry run finished.");
            }
            else if (status.StartsWith("blocked"))
            {
                var tail = File.Exists(FoundryLogPath)
                    ? string.Join(" ", File.ReadLines(FoundryLogPath)
                        .Where(l => l.Contains("[x]") || l.Contains("BLOCKED")).Take(3)) : "";
                FoundryStatus.Text = Truncate($"Blocked by the Foundry gate — {tail}", 200);
                OnActivity?.Invoke("⚒ Foundry blocked by preflight — see foundry.log");
            }
            else
            {
                var tail = File.Exists(FoundryLogPath)
                    ? string.Join(" ", File.ReadLines(FoundryLogPath).TakeLast(2)) : "no log";
                FoundryStatus.Text = Truncate($"Trainer exited unexpectedly — {tail}", 200);
                OnActivity?.Invoke("⚒ Foundry exited unexpectedly — check foundry.log");
            }
            FoundryDone();
            return;
        }

        _foundryDotOn = !_foundryDotOn;
        FoundryDot.Opacity = _foundryDotOn ? 1.0 : 0.35;

        if (!File.Exists(FoundryProgressPath)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(FoundryProgressPath));
            var p    = doc.RootElement;
            var sts  = p.GetProperty("status").GetString() ?? "?";
            var step = p.TryGetProperty("step",      out var st) ? st.GetInt32() : 0;
            var max  = p.TryGetProperty("max_steps", out var mx) ? mx.GetInt32() : 0;
            var loss = p.TryGetProperty("loss", out var lo) && lo.ValueKind == JsonValueKind.Number
                       ? lo.GetDouble() : (double?)null;

            FoundryBar.IsIndeterminate = max <= 0;
            if (max > 0) FoundryBar.Value = Math.Min(100.0, step * 100.0 / max);

            FoundryStatus.Text = sts switch
            {
                "starting"      => "Running preflight gates…",
                "loading_model" => "Loading base model…",
                "training"      => $"Training step {step}/{max}",
                "evaluating"    => $"Evaluating at step {step}…",
                "final_eval"    => "Final evaluation…",
                "saving"        => "Saving adapter…",
                "done"          => "Done.",
                _               => sts,
            };
            FoundryMetrics.Text = loss is not null ? $"loss {loss:0.####}" : "";
        }
        catch { }
    }

    private void FoundryDone()
    {
        _foundryTimer?.Stop();
        _foundryProcess?.Dispose();
        _foundryProcess = null;
        FoundryBar.IsIndeterminate = false;
        FoundryDot.IsVisible       = false;
        BtnFoundryTrain.IsEnabled  = true;
        BtnFoundryStop.IsEnabled   = false;
        RefreshFoundry();
    }

    private void BtnFoundryFolder_Click(object? s, RoutedEventArgs e)
    {
        if (Directory.Exists(FoundryDir))
            Process.Start(new ProcessStartInfo(FoundryDir) { UseShellExecute = true })?.Dispose();
    }

    // ── ARENA ─────────────────────────────────────────────────────────────────

    private void RefreshArena()
    {
        var outputsRoot = Path.Combine(_pitRoot, "training_pit", "outputs");
        var evalPath    = Path.Combine(_pitRoot, "training_pit", "datasets", "eval_toolcaller_v0.jsonl");
        var options     = new List<ArenaAdapterOptionAva>();

        if (Directory.Exists(outputsRoot) && File.Exists(evalPath))
        {
            foreach (var dir in Directory.GetDirectories(outputsRoot, "foundry_toolcaller_*")
                                         .OrderByDescending(d => d))
            {
                var adapterDir  = Path.Combine(dir, "adapter");
                var configPath  = Path.Combine(adapterDir, "adapter_config.json");
                if (!File.Exists(configPath)) continue;

                string? baseModelId = null;
                try
                {
                    using var doc  = JsonDocument.Parse(File.ReadAllText(configPath));
                    baseModelId    = doc.RootElement.TryGetProperty("base_model_name_or_path", out var bm)
                                    ? bm.GetString() : null;
                }
                catch { }

                var name      = Path.GetFileName(dir);
                var outDir    = Path.Combine(dir, "arena");
                var hasResult = File.Exists(Path.Combine(outDir, "results.json"));
                options.Add(new ArenaAdapterOptionAva
                {
                    Display      = hasResult ? $"✓ {name}" : name,
                    AdapterDir   = adapterDir,
                    EvalPath     = evalPath,
                    OutDir       = outDir,
                    HasResult    = hasResult,
                    BaseModelId  = baseModelId ?? "",
                });

                // Base model option (one per unique base model)
                if (baseModelId is not null &&
                    !options.Any(o => o.BaseOnly && o.BaseModelId == baseModelId))
                {
                    var baseOutDir = Path.Combine(outputsRoot, "arena_baseline",
                                                  baseModelId.Replace('/', '_').Replace(':', '_'));
                    options.Add(new ArenaAdapterOptionAva
                    {
                        Display     = $"BASE  {baseModelId.Split('/').Last()}  (no adapter)",
                        AdapterDir  = adapterDir,
                        EvalPath    = evalPath,
                        OutDir      = baseOutDir,
                        HasResult   = File.Exists(Path.Combine(baseOutDir, "results.json")),
                        BaseOnly    = true,
                        BaseModelId = baseModelId,
                    });
                }
            }
        }

        var prevOut = (CbArenaAdapter.SelectedItem as ArenaAdapterOptionAva)?.OutDir;
        CbArenaAdapter.ItemsSource   = options;
        CbArenaAdapter.SelectedItem  =
            options.FirstOrDefault(o => o.OutDir == prevOut) ??
            options.FirstOrDefault(o => !o.BaseOnly);

        BtnArenaRun.IsEnabled  = !ArenaRunning && options.Any(o => !o.BaseOnly);
        BtnArenaStop.IsEnabled = ArenaRunning;

        if (CbArenaAdapter.SelectedItem is ArenaAdapterOptionAva sel)
            TryShowArenaResults(sel.OutDir);
    }

    private void CbArenaAdapter_SelectionChanged(object? s, SelectionChangedEventArgs e)
    {
        if (CbArenaAdapter.SelectedItem is ArenaAdapterOptionAva opt)
            TryShowArenaResults(opt.OutDir);
    }

    private void TryShowArenaResults(string outDir)
    {
        var resultsPath = Path.Combine(outDir, "results.json");
        if (!File.Exists(resultsPath))
        {
            if (!ArenaRunning)
            {
                BorderArenaResults.IsVisible = false;
                ArenaResultBadge.IsVisible   = false;
            }
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(resultsPath));
            var root = doc.RootElement;
            var finished = root.TryGetProperty("finished", out var f) ? f.GetString() ?? "" : "";
            RenderArenaResults(root.GetProperty("metrics"), finished);
        }
        catch { }
    }

    private void RenderArenaResults(JsonElement m, string finished)
    {
        static string Pct(JsonElement m, string key) =>
            m.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                ? $"{v.GetDouble() * 100:F0}%" : "—";

        TbArenaDecisionAcc.Text = Pct(m, "decision_accuracy");
        TbArenaJsonValid.Text   = Pct(m, "json_validity");
        TbArenaToolPrec.Text    = Pct(m, "tool_precision");
        TbArenaArgMatch.Text    = Pct(m, "arg_exact_match");

        ArenaResultBadge.IsVisible   = true;
        ArenaResultBadgeText.Text    = TbArenaDecisionAcc.Text;
        BorderArenaResults.IsVisible = true;
        ArenaResultFooter.Text       = finished.Length > 0 ? $"evaluated {finished}" : "";

        // Per-class F1 bars
        var classRows = new List<ArenaClassRowAva>();
        if (m.TryGetProperty("per_class", out var pc))
        {
            var styles = new Dictionary<string, (string fg, string bar)>
            {
                ["call"]        = ("#A080FF", "#6040E0"),
                ["no_tool"]     = ("#80C080", "#409040"),
                ["clarify"]     = ("#80C0E0", "#4090A0"),
                ["unsupported"] = ("#E8C080", "#A08030"),
            };
            foreach (var cls in new[] { "call", "no_tool", "clarify", "unsupported" })
            {
                if (!pc.TryGetProperty(cls, out var cv)) continue;
                var f1    = cv.TryGetProperty("f1",    out var fv) ? fv.GetDouble() : 0.0;
                var count = cv.TryGetProperty("count", out var cv2) ? cv2.GetInt32()  : 0;
                var tp    = cv.TryGetProperty("tp",    out var tv) ? tv.GetInt32()  : 0;
                var (fg, bar) = styles.GetValueOrDefault(cls, ("#AAAAAA", "#666666"));
                classRows.Add(new ArenaClassRowAva
                {
                    Label       = cls,
                    TpCountText = $"{tp}/{count}",
                    F1Text      = $"F1 {f1:F3}",
                    F1Pct       = f1 * 100.0,
                    LabelBrush  = new SolidColorBrush(Color.Parse(fg)),
                    BarBrush    = new SolidColorBrush(Color.Parse(bar)),
                });
            }
        }
        ArenaClassList.ItemsSource = classRows;
    }

    private void BtnArenaRun_Click(object? s, RoutedEventArgs e)
    {
        if (ArenaRunning)   return;
        if (ForgeRunning)   { OnActivity?.Invoke("⚔ Arena refused: Forge is using the GPU."); return; }
        if (FoundryRunning) { OnActivity?.Invoke("⚔ Arena refused: Foundry is using the GPU."); return; }

        if (CbArenaAdapter.SelectedItem is not ArenaAdapterOptionAva opt)
        { OnActivity?.Invoke("⚔ Arena: select an adapter first."); return; }

        if (!File.Exists(opt.EvalPath))
        { OnActivity?.Invoke($"⚔ Arena: eval file not found — {opt.EvalPath}"); return; }

        var scriptPath = Path.Combine(_pitRoot, "training_pit", "foundry", "scripts", "eval_toolcaller.py");
        if (!File.Exists(scriptPath))
        { OnActivity?.Invoke($"⚔ Arena: eval script not found at {scriptPath}"); return; }

        _arenaOutDir = opt.OutDir;
        Directory.CreateDirectory(_arenaOutDir);

        var logPath = Path.Combine(_arenaOutDir, "arena.log");
        File.WriteAllText(logPath, $"=== arena run {DateTime.Now:yyyy-MM-dd HH:mm} ===\n");

        var baseOnlyFlag = opt.BaseOnly ? " --base-only" : "";
        var psi = new ProcessStartInfo
        {
            FileName         = "cmd.exe",
            Arguments        = $"/c python -u \"{scriptPath}\"" +
                               $" --adapter \"{opt.AdapterDir}\"" +
                               $" --eval \"{opt.EvalPath}\"" +
                               $" --out \"{opt.OutDir}\"" +
                               $"{baseOnlyFlag}" +
                               $" >> \"{logPath}\" 2>&1",
            WorkingDirectory = _pitRoot, UseShellExecute = false, CreateNoWindow = true,
        };

        try { _arenaProcess = Process.Start(psi); }
        catch (Exception ex) { OnActivity?.Invoke($"⚔ Arena launch failed: {ex.Message}"); return; }

        if (_arenaProcess is null) { OnActivity?.Invoke("⚔ Arena: failed to launch."); return; }

        var label = opt.BaseOnly
            ? $"base-model baseline ({opt.BaseModelId.Split('/').Last()})"
            : $"adapter {Path.GetFileName(Path.GetDirectoryName(opt.AdapterDir) ?? opt.AdapterDir)}";
        OnActivity?.Invoke($"⚔ Arena started — {label} vs {Path.GetFileName(opt.EvalPath)}");
        ArenaStatus.Text         = "Launching evaluator — loading model…";
        ArenaBar.IsIndeterminate = true;
        ArenaDot.IsVisible       = true;
        BtnArenaRun.IsEnabled    = false;
        BtnArenaStop.IsEnabled   = true;
        BorderArenaResults.IsVisible = false;

        _arenaTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _arenaTimer.Tick -= ArenaTimer_Tick;
        _arenaTimer.Tick += ArenaTimer_Tick;
        _arenaTimer.Start();
    }

    private void BtnArenaStop_Click(object? s, RoutedEventArgs e)
    {
        if (!ArenaRunning) return;
        try { _arenaProcess!.Kill(entireProcessTree: true); _arenaProcess.WaitForExit(3000); } catch { }
        OnActivity?.Invoke("⚔ Arena stopped.");
        ArenaStatus.Text = "Stopped.";
        ArenaDone();
    }

    private void BtnArenaFolder_Click(object? s, RoutedEventArgs e)
    {
        var dir = _arenaOutDir.Length > 0 && Directory.Exists(_arenaOutDir)
                  ? _arenaOutDir
                  : Path.Combine(_pitRoot, "training_pit", "outputs");
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }

    private void ArenaTimer_Tick(object? s, EventArgs e)
    {
        if (!ArenaRunning)
        {
            var resultsPath = Path.Combine(_arenaOutDir, "results.json");
            if (File.Exists(resultsPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(resultsPath));
                    var root = doc.RootElement;
                    var fin  = root.TryGetProperty("finished", out var f) ? f.GetString() ?? "" : "";
                    RenderArenaResults(root.GetProperty("metrics"), fin);
                    OnActivity?.Invoke($"⚔ Arena finished — decision accuracy: {TbArenaDecisionAcc.Text}");
                    RefreshArena(); // refresh picker so completed adapters show ✓
                }
                catch { OnActivity?.Invoke("⚔ Arena finished but results unreadable."); }
            }
            else
            {
                var logPath = Path.Combine(_arenaOutDir, "arena.log");
                var tail = File.Exists(logPath)
                    ? string.Join(" ", File.ReadLines(logPath).TakeLast(2)) : "no log";
                ArenaStatus.Text = Truncate($"Arena exited unexpectedly — {tail}", 180);
                OnActivity?.Invoke("⚔ Arena exited unexpectedly — check arena.log");
            }
            ArenaDone();
            return;
        }

        _arenaDotOn    = !_arenaDotOn;
        ArenaDot.Opacity = _arenaDotOn ? 1.0 : 0.35;

        var progressPath = Path.Combine(_arenaOutDir, "progress.json");
        if (!File.Exists(progressPath)) return;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(progressPath));
            var p     = doc.RootElement;
            var sts   = p.GetProperty("status").GetString() ?? "?";
            var step  = p.TryGetProperty("step",  out var st) ? st.GetInt32() : 0;
            var total = p.TryGetProperty("total", out var t)  ? t.GetInt32()  : 0;

            ArenaBar.IsIndeterminate = sts is "loading_model";
            if (!ArenaBar.IsIndeterminate && total > 0)
                ArenaBar.Value = Math.Min(100.0, step * 100.0 / total);

            ArenaStatus.Text = sts switch
            {
                "loading_model" => "Loading model and adapter (first run may take 30–60s)…",
                "evaluating"    => total > 0 ? $"Evaluating {step}/{total}…" : "Evaluating…",
                "done"          => "Done.",
                _               => sts,
            };

            // Show live partial results once we have ≥10 samples
            if (step >= 10 &&
                p.TryGetProperty("metrics", out var m) &&
                m.ValueKind == JsonValueKind.Object &&
                m.TryGetProperty("decision_accuracy", out _))
            {
                RenderArenaResults(m, "in progress");
                BorderArenaResults.IsVisible = true;
            }
        }
        catch { }
    }

    private void ArenaDone()
    {
        _arenaTimer?.Stop();
        _arenaProcess?.Dispose();
        _arenaProcess        = null;
        ArenaBar.IsIndeterminate = false;
        ArenaDot.IsVisible   = false;
        ArenaDot.Fill        = new SolidColorBrush(Color.Parse("#6040E0"));
        BtnArenaRun.IsEnabled  = true;
        BtnArenaStop.IsEnabled = false;
    }

    private void BtnFoundryActivate_Click(object? s, RoutedEventArgs e)
    {
        var lora = FindFoundryLoraGguf();
        var base_ = FindFoundryBaseGguf();
        if (lora is null || base_ is null)
        {
            FoundryStatus.Text = "GGUF files not found — run 'Train toolcaller' then ensure the depot has Qwen2.5-1.5B.";
            return;
        }
        ActivateAdapterRequested?.Invoke(base_, lora);
        FoundryStatus.Text = $"⚡ Sent to native runtime — restart llama.cpp backend to apply.";
    }

    private Task<(int Code, string Output)> RunProcessAsync(string fileName, string args) =>
        Task.Run(async () =>
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName, Arguments = args,
                // toolcaller-bench defaults its report dir to CWD/.orc/toolcaller-bench —
                // it must land under the pit root, not wherever the app was launched from
                WorkingDirectory = _pitRoot,
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true,
            };
            Process? p;
            try { p = Process.Start(psi); }
            catch (Exception ex) { return (-1, $"failed to launch {fileName}: {ex.Message}"); }
            if (p is null) return (-1, $"failed to launch {fileName}");
            using var _ = p;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-1, "process timed out after 120s");
            }
            p.WaitForExit();
            return (p.ExitCode, await stdout + await stderr);
        });

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Task<(int Code, string Output)> RunPythonAsync(string args) => Task.Run(async () =>
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python", Arguments = args, WorkingDirectory = _pitRoot,
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return (-1, "review tool timed out after 30s");
        }
        p.WaitForExit();
        return (p.ExitCode, await stdout + await stderr);
    });

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    private static string HumanizeAge(TimeSpan age)
    {
        if (age.TotalSeconds < 60) return $"{(int)age.TotalSeconds}s";
        if (age.TotalMinutes < 60) return $"{(int)age.TotalMinutes}m";
        if (age.TotalHours < 24)   return $"{(int)age.TotalHours}h";
        return $"{(int)age.TotalDays}d";
    }

    private static string MapOllamaToHfRepo(string ollama)
    {
        if (ollama.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            if (ollama.Contains("gguf", StringComparison.OrdinalIgnoreCase) ||
                ollama.Contains("-qat-",  StringComparison.OrdinalIgnoreCase)) return "";
            var parts = ollama.Split(':')[0].Split('/', 3);
            return parts.Length == 3 ? $"{parts[1]}/{parts[2]}" : "";
        }
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
        if (ollama.Contains("gguf",        StringComparison.OrdinalIgnoreCase) ||
            ollama.Contains("-qat-",        StringComparison.OrdinalIgnoreCase)) return "GGUF quant — not trainable.";
        if (ollama.StartsWith("theorc-",   StringComparison.OrdinalIgnoreCase)) return "custom local build — no HF source.";
        if (ollama.Contains("nomic-embed", StringComparison.OrdinalIgnoreCase)) return "embedding model — not a chat base.";
        return "no HF mapping yet.";
    }
}

// ── View-model types (Avalonia versions — use IBrush instead of WPF Brush) ───

public sealed class FoundryTrackAva
{
    public string Name           { get; init; } = "";
    public string Track          { get; init; } = "";
    public bool   IsActive       { get; init; }
    public string StatusLabel    { get; init; } = "";
    public string SizeHypothesis { get; init; } = "";
    public string BaseModel      { get; init; } = "";
    public IBrush StatusBg       { get; init; } = Brushes.Transparent;
    public IBrush StatusFg       { get; init; } = Brushes.Gray;

    public static FoundryTrackAva From(string name, string track, string status,
                                       string sizeHypothesis, string baseModel)
    {
        var active = status == "active";
        return new FoundryTrackAva
        {
            Name           = name,
            Track          = track,
            IsActive       = active,
            StatusLabel    = active ? "ACTIVE" : "TEMPLATE",
            SizeHypothesis = sizeHypothesis,
            BaseModel      = baseModel,
            StatusBg       = new SolidColorBrush(Color.Parse(active ? "#1F3D00" : "#1A1A1A")),
            StatusFg       = new SolidColorBrush(Color.Parse(active ? "#76B900" : "#777777")),
        };
    }
}

public sealed class DatasetInfoAva
{
    public string Name            { get; init; } = "";
    public string TotalCount      { get; init; } = "";
    public bool   IsNewConvention { get; init; }
    public string Role            { get; init; } = "";
    public string DataType        { get; init; } = "";
    public string Source          { get; init; } = "";
    public string CountsLine      { get; init; } = "";
    public bool   InProgress      { get; init; }
    public string Notes           { get; init; } = "";
    public string FilePath        { get; init; } = "";
    public bool   HasNotes        => Notes.Length > 0;

    public static DatasetInfoAva From(Services.TrainingPitRegistry.DatasetInfo d) => new()
    {
        Name            = d.Name,
        TotalCount      = d.TotalCount.ToString(),
        IsNewConvention = d.IsNewConvention,
        Role            = d.Role,
        DataType        = d.DataType,
        Source          = d.Source,
        CountsLine      = $"train {d.TrainCount}  eval {d.EvalCount}  neg {d.NegCount}",
        InProgress      = d.InProgress,
        Notes           = d.Notes,
        FilePath        = d.FilePath,
    };
}

public class HarvestModelOptionAva
{
    public string Name      { get; init; } = "";
    public string SizeText  { get; init; } = "";
    public bool   IsBlocked { get; init; }

    public string Display    => IsBlocked ? $"{Name}  (boss family — excluded)" : Name;
    public IBrush TextBrush  => IsBlocked
        ? new SolidColorBrush(Color.Parse("#666666"))
        : new SolidColorBrush(Color.Parse("#D4D4D4"));
}

public class BaseModelOptionAva
{
    public string OllamaName { get; init; } = "";
    public string HfRepo     { get; init; } = "";
    public string Reason     { get; init; } = "";

    public string Display   => HfRepo.Length > 0 ? $"{OllamaName}  →  {HfRepo}" : $"{OllamaName}  ({Reason})";
    public IBrush TextBrush => HfRepo.Length > 0
        ? new SolidColorBrush(Color.Parse("#D4D4D4"))
        : new SolidColorBrush(Color.Parse("#666666"));
}

public class DatasetOptionAva
{
    public string Name         { get; }
    public string TrainPath    { get; }
    public string EvalPath     { get; }
    public string CountsText   { get; }
    public string DisplayLabel { get; }

    public DatasetOptionAva(Services.TrainingPitRegistry.DatasetInfo info, string datasetsDir)
    {
        Name = info.Name;
        if (info.IsNewConvention)
        {
            TrainPath    = info.FilePath;
            var dir      = Path.GetDirectoryName(info.FilePath) ?? datasetsDir;
            EvalPath     = Path.Combine(dir, info.Name + ".eval.jsonl");
            var tag      = info.DataType.Length > 0 ? $"[{info.DataType}]" : "";
            var src      = info.Source.Length  > 0 ? info.Source : info.Name;
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

public class AdapterRowAva
{
    public string Name          { get; }
    public string BaseModelShort { get; }
    public string MetricsLine   { get; }
    public string Tier          { get; }
    public IBrush TierBg        { get; }
    public IBrush TierFg        { get; }

    public AdapterRowAva(Services.TrainingPitRegistry.AdapterInfo a)
    {
        Name = a.Name;
        var slash = a.BaseModel.LastIndexOf('/');
        BaseModelShort = "base: " + (slash >= 0 ? a.BaseModel[(slash + 1)..] : a.BaseModel);
        var loss = a.EvalLoss is double l ? $"loss {l:F3}" : "no eval";
        var when = a.Finished == default ? "" : a.Finished.ToString("MM-dd HH:mm");
        MetricsLine = $"{a.TrainExamples} ex · {loss} · {when}";
        Tier = a.Tier;
        (TierBg, TierFg) = a.Tier switch
        {
            "Trusted"  => ((IBrush)new SolidColorBrush(Color.Parse("#1F3D00")),
                           (IBrush)new SolidColorBrush(Color.Parse("#76B900"))),
            "Promoted" => ((IBrush)new SolidColorBrush(Color.Parse("#1A2A00")),
                           (IBrush)new SolidColorBrush(Color.Parse("#CCA700"))),
            _          => ((IBrush)new SolidColorBrush(Color.Parse("#1A1A1A")),
                           (IBrush)new SolidColorBrush(Color.Parse("#999999"))),
        };
    }
}

public class ArenaAdapterOptionAva
{
    public string Display     { get; init; } = "";
    public string AdapterDir  { get; init; } = "";
    public string EvalPath    { get; init; } = "";
    public string OutDir      { get; init; } = "";
    public string BaseModelId { get; init; } = "";
    public bool   HasResult   { get; init; }
    public bool   BaseOnly    { get; init; }

    public IBrush TextBrush => new SolidColorBrush(Color.Parse(
        BaseOnly ? "#777777" : HasResult ? "#A080FF" : "#D4D4D4"));
}

public class ArenaClassRowAva
{
    public string Label       { get; init; } = "";
    public string TpCountText { get; init; } = "";
    public string F1Text      { get; init; } = "";
    public double F1Pct       { get; init; }
    public IBrush LabelBrush  { get; init; } = Brushes.Gray;
    public IBrush BarBrush    { get; init; } = Brushes.Gray;
}

public class ModelRowAva
{
    public string NameShort { get; }
    public string SizeText  { get; }

    public ModelRowAva(Services.TrainingPitRegistry.OllamaModelInfo m)
    {
        var name = m.Name;
        if (name.StartsWith("hf.co/", StringComparison.OrdinalIgnoreCase))
        {
            var lastSlash = name.LastIndexOf('/');
            if (lastSlash >= 0) name = "hf:" + name[(lastSlash + 1)..];
        }
        NameShort = name;
        SizeText  = $"{m.SizeGb:F1} GB";
    }
}

public class QueueItem : INotifyPropertyChanged
{
    public string ExampleId   { get; init; } = "";
    public string CapturePath { get; init; } = "";
    public string Goal        { get; init; } = "";
    public int    Score       { get; init; }
    public List<string> TaskChips { get; init; } = [];
    public string Risk        { get; init; } = "";
    public string JudgeNote   { get; init; } = "";

    public string Summary    => Goal.Length <= 90 ? Goal : Goal[..90] + "…";
    public string ScoreText  => Score >= 0 ? $"score {Score}" : "";
    public bool   HasJudgeNote => JudgeNote.Length > 0;

    public string RiskLabel => Risk switch
    {
        "high"   => "check closely",
        "medium" => "worth a look",
        "low"    => "looks clean",
        _        => "not judged",
    };

    public IBrush RiskBg => new SolidColorBrush(Risk switch
    {
        "high"   => Color.FromRgb(0x2A, 0x0A, 0x0A),
        "medium" => Color.FromRgb(0x1A, 0x2A, 0x00),
        "low"    => Color.FromRgb(0x1F, 0x3D, 0x00),
        _        => Color.FromRgb(0x11, 0x11, 0x11),
    });

    public IBrush RiskFg => new SolidColorBrush(Risk switch
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
