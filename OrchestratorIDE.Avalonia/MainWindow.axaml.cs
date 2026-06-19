// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;
using OrchestratorIDE.UI;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Panels;
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE;

public partial class MainWindow : Window
{
    // ── Services ──────────────────────────────────────────────────────────
    private readonly OllamaClient       _ollama;
    private readonly ApprovalQueue      _approvals;
    private readonly ToolRegistry       _registry;
    private readonly ContextManager     _context;
    private readonly GitCheckpoint      _git;
    private readonly RulesLoader        _rules;
    private readonly AgentLoop          _loop;
    private readonly SessionStore       _store;
    private readonly UnavailableFeatureRouter _unavailable;
    private          LlamaServerManager?  _llamaServer;
    private          ModelStatusService?  _modelStatus;
    private          Services.Hive.HiveBeacon?      _hiveBeacon;
    private          Services.Hive.HiveNodeServer?  _hiveNodeServer;
    private          Services.Hive.HiveRpcWorker?   _hiveRpcWorker;
    private          Services.Hive.HiveTaskQueue?   _hiveTaskQueue;
    private          Services.Hive.HiveWorkerAgent? _hiveWorkerAgent;
    private          Services.Data.SqliteStore?       _sqlStore;
    private          Services.Data.PlanRepository?   _planRepo;
    private          Services.Data.RunRepository?    _runRepo;
    private          Services.Data.DatasetRepository? _datasetRepo;
    private readonly Services.CodeGraph.CodeGraphService _codeGraph = new();
    private bool _windowClosed;

    // ── State ─────────────────────────────────────────────────────────────
    private ProjectSession           _session;
    private AppSettings              _settings = AppSettings.Load();
    private string                   _pendingReleaseUrl = "";
    private List<string>             _installedModels = [];
    private readonly ObservableCollection<ActivityEvent> _activityItems = [];
    private readonly List<ActivityEvent>                 _allActivityItems = [];

    // ── Panels ────────────────────────────────────────────────────────────
    private readonly FileExplorerPanel       _explorerPanel;
    private readonly AgentPanel              _agentPanel;
    private readonly SettingsPanel           _settingsPanel;
    private readonly CodeEditorPanel         _editorPanel;
    private readonly CheckpointBrowserPanel  _checkpointPanel;
    private readonly SessionBrowserPanel     _sessionPanel;
    private readonly ToolEditorPanel         _toolEditorPanel;
    private readonly ToolCompiler            _toolCompiler;
    private readonly SwarmBoardPanel         _swarmPanel;
    private readonly ChatPanel               _chatPanel;
    private readonly TrainingPitPanel        _pitPanel;
    private readonly HivePanel               _hivePanel    = new();
    private readonly UpdatePanel             _updatePanel  = new();
    private PitBossPanel?                    _pitBossPanel;

    // ── Screen recorder (stub on non-WPF builds) ──────────────────────────
    private readonly ScreenRecorder _recorder = new();
    private double _editorFontSize = 13.0;

    public MainWindow()
    {
        InitializeComponent();

        // Keyboard shortcuts — attached before panels are constructed
        AddHandler(KeyDownEvent, Window_KeyDown, RoutingStrategies.Tunnel);

        _ollama      = new OllamaClient(_settings.InferenceBaseUrl, _settings.Backend);
        _llamaServer = BuildServerManager(_settings);

        // Tidy up on close
        Closed += (_, _) =>
        {
            _windowClosed = true;
            _codeGraph.Dispose();
            _llamaServer?.Stop();
            _recorder.Stop();
            _modelStatus?.Stop();
            _modelStatus?.Dispose();
            _hiveBeacon?.Dispose();
            _hiveNodeServer?.Dispose();
            _hiveRpcWorker?.Dispose();
            _hiveTaskQueue?.Dispose();
            _hiveWorkerAgent?.Dispose();
            _flaUIWatcher?.Dispose();
        };

        // Recorder events → status bar (stubs fire nothing on non-WPF)
        _recorder.OnTick    += t    => Dispatcher.UIThread.InvokeAsync(() => TbRecordingTime.Text = $"REC {t}");
        _recorder.OnStopped += path => Dispatcher.UIThread.InvokeAsync(() =>
        {
            BdrRecording.IsVisible = false;
            AddActivity(new ActivityEvent(ActivityKind.Info, "Recording saved",
                Path.GetFileName(path), DateTime.Now));
            SetStatus("Recording saved — F12 to record again");
        });

        _approvals = new ApprovalQueue();
        _registry  = new ToolRegistry(_approvals);
        _context   = new ContextManager(32_768);
        _git       = new GitCheckpoint();
        _rules     = new RulesLoader();
        _store     = new SessionStore();
        _unavailable = new UnavailableFeatureRouter(AddActivity);
        _loop      = new AgentLoop(new OllamaRuntime(_ollama), _registry, _context, _git, _rules);

        _session = new ProjectSession
        {
            WorkspaceRoot = !string.IsNullOrEmpty(_settings.DefaultWorkspace)
                ? _settings.DefaultWorkspace
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ActiveModel = _settings.DefaultModel,
        };

        RegisterAllTools();

        // ── Build panels ──────────────────────────────────────────────────
        _explorerPanel = new FileExplorerPanel();
        _explorerPanel.WorkspaceChanged += path => ConfirmWorkspace(path);
        _explorerPanel.FileSelected     += path =>
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Open", Path.GetFileName(path), DateTime.Now));
            ShowEditorPane();
            _editorPanel?.OpenFile(path);
        };

        _editorPanel = new CodeEditorPanel();
        _editorPanel.ClosePane += HideEditorPane;
        EditorContent.Content   = _editorPanel;

        _agentPanel = new AgentPanel
        {
            Loop            = _loop,
            Session         = _session,
            OnStatusChanged = msg => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg)),
        };

        _loop.OnToken += token => _agentPanel.AppendStreamingToken(token);
        _loop.OnUsage += (p, c) => _agentPanel.OnTokensUsed(p, c);

        _agentPanel.WorkspaceChangeRequested += () => _explorerPanel.PromptOpenFolder();
        _agentPanel.ConversationChanged      += async () =>
        {
            try { await _store.SaveAsync(_session); }
            catch { /* non-fatal */ }
        };

        // Activity log
        ActivityLog.ItemsSource = _activityItems;
        UpdateVerbosityButtons(_settings.ActivityVerbosity);
        _loop.Activity += ev => Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ev.Verbosity > _settings.ActivityVerbosity) { _allActivityItems.Add(ev); return; }
            _activityItems.Add(ev);
            if (_activityItems.Count > 2000) _activityItems.RemoveAt(0);
            ScrollActivityToEnd();
        });

        // Context meter
        _context.UsageChanged       += () => Dispatcher.UIThread.InvokeAsync(UpdateContextDisplay);
        _agentPanel.InputTextChanged += UpdateContextDisplay;

        ShowBuildStamp();

        // Approval gate
        _approvals.ApprovalRequested   += OnApprovalRequested;
        _approvals.PendingCountChanged += OnApprovalPendingCountChanged;

        // Unknown tool card
        _registry.OnUnknownTool = async call =>
        {
            var names = _registry.GetRegisteredNames();
            return await _agentPanel.ShowUnknownToolCard(call, names);
        };

        // Rules badge + pentest auto-switch
        _loop.OnRulesLoaded += filePath =>
        {
            _agentPanel.SetRulesStatus(filePath);
            var isPentest = filePath != null && PentestRules.IsPentestTemplate(filePath);
            _agentPanel.SetPentestActive(isPentest);
            if (isPentest && _settings.AutoModelSwitch)
            {
                var secModel = GetBestSecurityModel();
                if (secModel != null && !secModel.Equals(_session.ActiveModel, StringComparison.OrdinalIgnoreCase))
                {
                    OnModelSelected(secModel);
                    AddActivity(new ActivityEvent(ActivityKind.Info, "Pentest Mode",
                        $"Auto-switched to {ModelProfiles.Get(secModel).Name} for security research",
                        DateTime.Now));
                }
            }
        };

        _agentPanel.RulesEditRequested       += OpenRulesFile;
        _agentPanel.WorkspaceRulesRequested  += OpenWorkspaceRules;
        _agentPanel.GlobalAgentRequested     += OpenGlobalAgentPicker;
        RefreshGlobalAgentBadge();

        _settingsPanel = new SettingsPanel(_ollama);
        _settingsPanel.LoadSettings(_settings);
        _settingsPanel.SettingsSaved                += OnSettingsSaved;
        _settingsPanel.CheckUpdatesRequested        += async () => await Menu_CheckUpdatesAsync(force: true);
        _settingsPanel.RegenerateAgentFileRequested += async () => await RegenerateAgentFileAsync();
        _settingsPanel.OpenFolderAsWorkspaceRequested += folder => _ = OpenWorkspaceAsync(folder);
        _settingsPanel.ActivityRequested            += ev => Dispatcher.UIThread.InvokeAsync(() => AddActivity(ev));
        _settingsPanel.ScanAnalysisReady            += prompt =>
        {
            BtnAgent_Click(this, new RoutedEventArgs());
            _agentPanel?.InjectUserMessage(prompt);
        };

        _checkpointPanel = new CheckpointBrowserPanel(_git);
        _checkpointPanel.CheckpointRestored += sha =>
        {
            _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
            AddActivity(new ActivityEvent(ActivityKind.Git, "Restored", $"Hard-reset to {sha[..8]}", DateTime.Now));
        };

        _sessionPanel = new SessionBrowserPanel(_store);
        _sessionPanel.SessionSelected += ResumeSession;

        _toolCompiler    = new ToolCompiler(_registry);
        _toolEditorPanel = new ToolEditorPanel
        {
            Compiler      = _toolCompiler,
            WorkspaceRoot = _session.WorkspaceRoot,
        };

        _swarmPanel = new SwarmBoardPanel
        {
            Ollama        = _ollama,
            ActiveModel   = _session.ActiveModel,
            WorkspaceRoot = _session.WorkspaceRoot,
            Settings      = _settings,
        };
        _swarmPanel.StatusChanged += msg  => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg));
        _swarmPanel.OnActivity    += msg  => Dispatcher.UIThread.InvokeAsync(() =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "Swarm", msg, DateTime.Now)));
        // Dialog delegates (Avalonia panel uses callbacks instead of WPF events)
        _swarmPanel.AlertAsync   = (title, msg) => DialogHelper.ShowInfoAsync(this, title, msg);
        _swarmPanel.ConfirmAsync = (title, msg) => DialogHelper.ShowYesNoAsync(this, title, msg);

        _chatPanel = new ChatPanel { OllamaClient = _ollama };

        _pitPanel = new TrainingPitPanel { WorkspaceRoot = _session.WorkspaceRoot };
        _pitPanel.StatusChanged   += msg => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg));
        _pitPanel.OnActivity      += msg => Dispatcher.UIThread.InvokeAsync(() =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "Training Pit", msg, DateTime.Now)));
        _pitPanel.LiveStateChanged += (active, waiting) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            PitLiveDot.IsVisible  = active;
            PitQueueBadge.Text    = waiting > 0 ? waiting.ToString() : "";
        });
        _pitPanel.PitBossRequested += () => Dispatcher.UIThread.InvokeAsync(ShowPitBoss);

        // Default layout: explorer in sidebar, agent in main
        SidebarContent.Content = _explorerPanel;
        _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
        MainContent.Content    = _agentPanel;

        _agentPanel.SetWorkspace(_session.WorkspaceRoot, confirmed: false);
        UpdateModeToggle("single");
        UpdateStatusBar();

        Loaded += async (_, _) => await OnLoadedAsync();
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async Task OnLoadedAsync()
    {
        // ── llama.cpp server ─────────────────────────────────────────────
        if (_settings.Backend == InferenceBackend.LlamaCpp && _llamaServer != null)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp",
                "Starting local inference server…", DateTime.Now));
            var ready = await _llamaServer.StartAsync(ct: default);
            if (!ready)
                AddActivity(new ActivityEvent(ActivityKind.Warning, "llama.cpp",
                    "Server failed to start — check RuntimePath and ModelPath in Settings.", DateTime.Now));
        }

        var backendLabel = _settings.Backend == InferenceBackend.LlamaCpp ? "llama.cpp" : "Ollama";
        AddActivity(new ActivityEvent(ActivityKind.Info, "Startup",
            $"Checking {backendLabel} connection…", DateTime.Now));

        var models = await _ollama.GetInstalledModelsAsync();

        if (models.Count > 0)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, backendLabel,
                $"{models.Count} model(s): {string.Join(", ", models.Take(3))}", DateTime.Now));

            _installedModels = models;

            var isSwarmMode = _settings.LastMode == "swarm";
            var lastUsed = isSwarmMode
                ? (_settings.LastSwarmModel.Length  > 0 ? _settings.LastSwarmModel  : _settings.DefaultModel)
                : (_settings.LastSingleModel.Length > 0 ? _settings.LastSingleModel : _settings.DefaultModel);
            string best;

            if (_settings.RestoreLastModel &&
                !string.IsNullOrEmpty(lastUsed) &&
                models.Contains(lastUsed, StringComparer.OrdinalIgnoreCase))
            {
                best = lastUsed;
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model", $"Restored: {best}", DateTime.Now));
            }
            else if (_settings.AutoModelSwitch)
            {
                var preferred = new[]
                {
                    "qwen2.5-coder:14b", "qwen2.5-coder:7b", "gemma4:12b",
                    "nemotron-mini-4b-q5", "nemotron-3-nano:4b-q8_0",
                    "nemotron-3-nano:4b", "qwen2.5-coder:3b", "qwen2.5:14b-instruct",
                    "gemma4:e4b", "llama3.1:8b",
                };
                best = preferred.FirstOrDefault(p => models.Contains(p, StringComparer.OrdinalIgnoreCase))
                    ?? models.First();
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model", $"Auto-selected: {best}", DateTime.Now));
            }
            else
            {
                best = models.First();
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model", $"Active: {best}", DateTime.Now));
            }

            _session.ActiveModel    = best;
            _swarmPanel.ActiveModel = best;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _chatPanel.SetModels(_installedModels, best);
                UpdateStatusBar();
                RestoreLastMode();
                ApplyTrustLevel(_settings.TrustLevel);
            });
        }
        else
        {
            var hint = _settings.Backend == InferenceBackend.LlamaCpp
                ? "No models found — check LlamaCppRuntimePath and LlamaCppModelPath in Settings."
                : "No models found — check Ollama host connection in Settings.";
            AddActivity(new ActivityEvent(ActivityKind.Warning, backendLabel, hint, DateTime.Now));
            await TryLaunchBundledSetupAsync();
        }

        // Auto-update check (silent background)
        _ = Task.Run(async () =>
        {
            var result = await UpdateChecker.CheckAsync(_settings);
            if (result?.UpdateAvailable == true)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ShowUpdateBadge(result));
                AddActivity(new ActivityEvent(ActivityKind.Info, "Update",
                    $"v{result.LatestVersion} available — Help → Check for Updates", DateTime.Now));
            }
        });

        // Restore last session
        var saved = await _store.LoadLatestAsync();
        if (saved != null)
        {
            _session = saved;
            _agentPanel.Session = _session;
            AddActivity(new ActivityEvent(ActivityKind.Info, "Recovered",
                $"Session from {saved.LastActivityAt:g}", DateTime.Now));
            // ConfirmWorkspace rebinds all services (tools, checkpoint panel, SQL store, swarm)
            await Dispatcher.UIThread.InvokeAsync(() => ConfirmWorkspace(saved.WorkspaceRoot));
        }

        // Model status polling
        _modelStatus = new ModelStatusService(_settings);
        _modelStatus.OnUpdate += snap => Dispatcher.UIThread.InvokeAsync(() =>
        {
            var line = snap.ShortStatusLine + snap.VramDisplay;
            TxtSbModelStatus.Text  = line;
            SbModelStatus.IsVisible = !string.IsNullOrEmpty(line);
        });
        _modelStatus.Start(TimeSpan.FromSeconds(8));

        // HIVE MIND
        if (_settings.HiveMindEnabled)
            _ = Task.Run(async () => await StartHiveAsync());

        // CLI overrides (--workspace, --autoapprove)
        var cliArgs  = Environment.GetCommandLineArgs();
        var wsArgIdx = Array.IndexOf(cliArgs, "--workspace");
        if (wsArgIdx >= 0 && wsArgIdx + 1 < cliArgs.Length)
        {
            var wsPath = cliArgs[wsArgIdx + 1];
            Directory.CreateDirectory(wsPath);
            ConfirmWorkspace(wsPath);
            AddActivity(new ActivityEvent(ActivityKind.Info, "Workspace",
                $"CLI workspace: {wsPath}", DateTime.Now));
        }

        if (Array.IndexOf(cliArgs, "--autoapprove") >= 0)
        {
            _approvals.AutoApprove = true;
            AddActivity(new ActivityEvent(ActivityKind.Info, "AutoApprove",
                "Auto-approve enabled — write_file approved without UI (FlaUI test mode)", DateTime.Now));
            WatchForFlaUICommands();
        }

        // Auto-open last workspace
        if (wsArgIdx < 0)
        {
            var lastWs = _settings.RecentWorkspaces.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(lastWs) && lastWs != _session.WorkspaceRoot)
                await Dispatcher.UIThread.InvokeAsync(() => ConfirmWorkspace(lastWs));
            else if (!string.IsNullOrEmpty(_session.WorkspaceRoot)
                     && Directory.Exists(_session.WorkspaceRoot)
                     && !_session.IsWorkspaceConfirmed)
                await Dispatcher.UIThread.InvokeAsync(() => ConfirmWorkspace(_session.WorkspaceRoot));
            await Dispatcher.UIThread.InvokeAsync(RebuildRecentMenu);
        }

        // First-run wizard (Phase 4 dialog — skipped until ported)
        if (!_settings.FirstRunComplete && !cliArgs.Contains("--autoapprove"))
        {
            _unavailable.Report(
                "First Run",
                "First-run wizard",
                "Edit .agent.md manually or via Agent -> Workspace Rules.");
        }

        InitDataLayer(_session.WorkspaceRoot);
    }

    // ── HIVE MIND startup ─────────────────────────────────────────────────

    private async Task StartHiveAsync()
    {
        var models  = _installedModels.Count > 0 ? _installedModels : [];
        var vramMb  = (int)(_settings.DetectedVramGb * 1024);
        var name    = Environment.MachineName;

        int rpcPort = 0;
        if (!string.IsNullOrEmpty(_settings.LlamaCppRuntimePath))
        {
            _hiveRpcWorker = new Services.Hive.HiveRpcWorker
            {
                RuntimePath = _settings.LlamaCppRuntimePath,
                Port        = Services.Hive.HiveRpcWorker.DefaultPort,
            };
            _hiveRpcWorker.OnLog += msg =>
                AddActivity(new ActivityEvent(ActivityKind.Info, "RPC Worker", msg, DateTime.Now));
            if (_hiveRpcWorker.IsAvailable && _hiveRpcWorker.Start())
                rpcPort = Services.Hive.HiveRpcWorker.DefaultPort;
        }

        var lanes = rpcPort > 0
            ? new[] { "inference", "coder", "researcher", "rpc_worker" }
            : new[] { "inference", "coder", "researcher" };

        var ollamaUrlForPeers = _settings.OllamaHost;
        if (ollamaUrlForPeers.Contains("localhost") || ollamaUrlForPeers.Contains("127.0.0.1"))
        {
            var lanIp = Services.Hive.HiveRpcWorker.LocalAddresses().FirstOrDefault();
            if (lanIp is not null)
            {
                var ollamaPort = new Uri(ollamaUrlForPeers).Port;
                ollamaUrlForPeers = $"http://{lanIp}:{ollamaPort}";
            }
        }

        var info = new Services.Hive.HiveNodeInfo(
            name, ollamaUrlForPeers, [.. models], vramMb, vramMb, lanes, rpcPort);

        _hiveNodeServer = new Services.Hive.HiveNodeServer();
        _hiveNodeServer.ShutdownCallback = () =>
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt)
                    lt.Shutdown();
            });
        _hiveNodeServer.OnPairingRequestReceived += (sessionId, pairingReq) =>
            Dispatcher.UIThread.InvokeAsync(() => _hivePanel.OnPairingRequest(sessionId, pairingReq));
        _hivePanel.NodeServer = _hiveNodeServer;
        _hiveNodeServer.Start(info);

        _hivePanel.LocalNodeId = Services.Hive.HiveIdentity.Load().NodeId;

        if (_hiveNodeServer.ElectionService is { } election)
            election.OnStateChanged += (state, warchiefNodeId) =>
                Dispatcher.UIThread.InvokeAsync(() =>
                    _hivePanel.OnElectionStateChanged(state, warchiefNodeId));

        _hiveBeacon = new Services.Hive.HiveBeacon();
        _hiveBeacon.Start(name, ollamaUrlForPeers, models, vramMb);
        _hiveBeacon.OnNodeSeen += msg =>
            Dispatcher.UIThread.InvokeAsync(() => _hivePanel.OnBeaconNodeSeen(msg));

        if (_settings.HiveDistributedSwarm)
        {
            _hiveTaskQueue = new Services.Hive.HiveTaskQueue();
            _hiveTaskQueue.OnLog += msg =>
                AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE Queue", msg, DateTime.Now));
            _hiveTaskQueue.Start(new Services.Hive.HiveSessionContext
            {
                SessionId   = name,
                ProjectGoal = "",
            }, _settings.HiveTaskQueuePort);
            await Dispatcher.UIThread.InvokeAsync(() => _swarmPanel.HiveTaskQueue = _hiveTaskQueue);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _hivePanel.EventBus       = _hiveTaskQueue?.Events;
                _hivePanel.WarchiefBaseUrl = _hiveTaskQueue?.BaseUrl;
            });
        }

        if (_settings.HiveWorkerMode && string.IsNullOrEmpty(_settings.HiveWarchiefUrl))
            AddActivity(new ActivityEvent(ActivityKind.Warning, "HIVE Worker",
                "⚠ Worker mode is enabled but Warchief URL is empty — set it in Settings → HIVE MIND.", DateTime.Now));

        if (_settings.HiveWorkerMode && !string.IsNullOrEmpty(_settings.HiveWarchiefUrl))
        {
            _hiveWorkerAgent = new Services.Hive.HiveWorkerAgent
            {
                WorkerId        = name,
                WorkerUrl       = _settings.InferenceBaseUrl,
                WarchiefUrl     = _settings.HiveWarchiefUrl,
                WarchiefNodeId  = ResolveWarchiefNodeId(_settings.HiveWarchiefUrl),
                Lanes           = string.IsNullOrWhiteSpace(_settings.HiveWorkerLanes)
                                    ? []
                                    : _settings.HiveWorkerLanes
                                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                        .Select(l => l.Trim()).ToArray(),
                Runtime         = BuildModelRuntime(),
                CoderModel      = _settings.LastWorkerModel,
                ResearcherModel = _settings.LastResearcherModel,
            };
            _hiveWorkerAgent.OnLog += msg =>
                AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE Worker", msg, DateTime.Now));
            _hiveWorkerAgent.Start();
        }

        var rpcNote    = rpcPort > 0                    ? $" · RPC :{rpcPort}"                           : "";
        var queueNote  = _settings.HiveDistributedSwarm ? $" · Warchief :{_settings.HiveTaskQueuePort}"  : "";
        var workerNote = _settings.HiveWorkerMode        ? $" · Worker→{_settings.HiveWarchiefUrl}"       : "";
        AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
            $"Active as {name}{rpcNote}{queueNote}{workerNote}", DateTime.Now));
    }

    // ── SQLite data layer ─────────────────────────────────────────────────

    private void InitDataLayer(string workspaceRoot)
    {
        if (string.IsNullOrEmpty(workspaceRoot) || !Directory.Exists(workspaceRoot)) return;

        Services.Swarm.DatasetCapture.Repository = null;
        Services.PitBossService.PlanRepo         = null;
        Services.TrainingPitRegistry.DatasetRepo  = null;
        Services.Hive.HiveTaskQueue.Repository    = null;
        _planRepo = null; _runRepo = null; _datasetRepo = null;

        try
        {
            _sqlStore = new Services.Data.SqliteStore(workspaceRoot);
            _sqlStore.Initialize();

            var captures = new Services.Data.CaptureRepository(_sqlStore);
            var triage   = new Services.Data.TriageRepository(_sqlStore);
            _planRepo    = new Services.Data.PlanRepository(_sqlStore);
            _runRepo     = new Services.Data.RunRepository(_sqlStore);
            _datasetRepo = new Services.Data.DatasetRepository(_sqlStore);

            Services.Swarm.DatasetCapture.Repository  = captures;
            Services.PitBossService.PlanRepo           = _planRepo;
            Services.TrainingPitRegistry.DatasetRepo   = _datasetRepo;
            Services.Hive.HiveTaskQueue.Repository     = new Services.Data.HiveRepository(_sqlStore);

            foreach (var stale in _runRepo.ActiveRuns())
                _runRepo.UpdateStatus(stale.RunId, "stale");

            var planRepo = _planRepo;
            Task.Run(() =>
            {
                try
                {
                    var datasetRepo = _datasetRepo;
                    var r = new Services.Data.MetadataImporter(
                        workspaceRoot, captures, triage, planRepo, datasetRepo).ImportAll();
                    Dispatcher.UIThread.InvokeAsync(() => AddActivity(new ActivityEvent(ActivityKind.Info, "Data",
                        $"SQLite ready — {r.Captures} captures, {r.Triage} triage rows, {r.Plans} plans, {r.Datasets} datasets indexed"
                        + (r.CaptureErrors + r.TriageErrors + r.PlanErrors + r.DatasetErrors > 0
                            ? $" ({r.CaptureErrors + r.TriageErrors + r.PlanErrors + r.DatasetErrors} skipped)" : ""),
                        DateTime.Now)));
                }
                catch (Exception ex)
                {
                    Dispatcher.UIThread.InvokeAsync(() => AddActivity(new ActivityEvent(ActivityKind.Info, "Data",
                        $"SQLite backfill failed (non-fatal): {ex.Message}", DateTime.Now)));
                }
            });
        }
        catch (Exception ex)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Data",
                $"SQLite init failed (non-fatal): {ex.Message}", DateTime.Now));
        }
    }

    // ── FlaUI IPC ─────────────────────────────────────────────────────────

    private System.IO.FileSystemWatcher? _flaUIWatcher;

    private void WatchForFlaUICommands()
    {
        var dir = _session.WorkspaceRoot;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        _flaUIWatcher = new System.IO.FileSystemWatcher(dir, ".flaui_cmd")
        {
            NotifyFilter        = System.IO.NotifyFilters.FileName | System.IO.NotifyFilters.LastWrite,
            EnableRaisingEvents = true,
        };
        _flaUIWatcher.Created += (_, e) => HandleFlaUICmd(e.FullPath);
        _flaUIWatcher.Changed += (_, e) => HandleFlaUICmd(e.FullPath);
        AddActivity(new ActivityEvent(ActivityKind.Info, "FlaUI IPC",
            $"Watching {dir}\\.flaui_cmd for test commands", DateTime.Now));
    }

    private void HandleFlaUICmd(string path)
    {
        try
        {
            System.Threading.Thread.Sleep(150);
            if (!File.Exists(path)) return;
            var prompt = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
            File.Delete(path);
            if (string.IsNullOrWhiteSpace(prompt)) return;
            Dispatcher.UIThread.InvokeAsync(() => _agentPanel.AutoSend(prompt));
        }
        catch { /* best-effort */ }
    }

    // ── Agent file regeneration ───────────────────────────────────────────

    public async Task RegenerateAgentFileAsync()
    {
        _unavailable.Report(
            "Agent File",
            "FirstRunWindow",
            "Edit .agent.md directly to regenerate.");
        await Task.CompletedTask;
    }

    // ── Portable-zip bootstrap ────────────────────────────────────────────

    private async Task TryLaunchBundledSetupAsync()
    {
        var setupExe = Path.Combine(AppContext.BaseDirectory, "OrchestratorSetup.exe");
        if (!File.Exists(setupExe)) return;

        AddActivity(new ActivityEvent(ActivityKind.Info, "Setup",
            "OrchestratorSetup.exe found — no runtime detected on this machine.", DateTime.Now));

        var yes = await DialogHelper.ShowYesNoAsync(this,
            "Welcome to TheOrc — First-Time Setup",
            "No AI runtime was detected on this machine.\n\n" +
            "OrchestratorSetup.exe is included and will automatically install the " +
            "llama.cpp runtime and download a model sized for your GPU.\n\n" +
            "Run the setup wizard now?");

        if (yes)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = setupExe,
                UseShellExecute = true,
            });
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lt)
                lt.Shutdown();
        }
    }

    // ── Tool registration ─────────────────────────────────────────────────

    private void RegisterAllTools()
    {
        var ws = _session.WorkspaceRoot;

        Func<string, string, string, CancellationToken, Task<bool>> sandboxBypass =
            async (toolName, escapedPath, sandboxRoot, ct) =>
            {
                if (ct.IsCancellationRequested)
                    return false;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var _ = ct.Register(() => tcs.TrySetResult(false));

                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    try
                    {
                        var allowed = await DialogHelper.ShowYesNoAsync(
                            this,
                            "Sandbox Escape Attempt",
                            $"{PathSandbox.EscapeLabel(toolName, escapedPath)}\n\n" +
                            $"Sandbox root:\n{sandboxRoot}\n\n" +
                            $"Requested path:\n{escapedPath}\n\n" +
                            "Allow this single operation?");

                        AddActivity(new ActivityEvent(
                            allowed ? ActivityKind.Warning : ActivityKind.Info,
                            "Sandbox",
                            allowed
                                ? $"{toolName} sandbox escape allowed once."
                                : $"{toolName} sandbox escape denied.",
                            DateTime.Now));

                        tcs.TrySetResult(!ct.IsCancellationRequested && allowed);
                    }
                    catch (Exception ex)
                    {
                        AddActivity(new ActivityEvent(
                            ActivityKind.Warning,
                            "Sandbox",
                            $"Sandbox escape prompt failed: {ex.Message}",
                            DateTime.Now));
                        tcs.TrySetResult(false);
                    }
                });

                return await tcs.Task;
            };

        FileTools.Register(_registry, ws,
            onDiffPreview: async (path, oldContent, newContent, reason, ct) =>
            {
                switch (_approvals.Level)
                {
                    case TrustLevel.FullAuto:
                    case TrustLevel.Standard:
                        return true;
                    case TrustLevel.Plan:
                        return false;
                    default:
                        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        ct.Register(() => tcs.TrySetResult(false));
                        await Dispatcher.UIThread.InvokeAsync(() =>
                            _agentPanel.ShowDiff(
                                path, oldContent, newContent, reason,
                                onApproved: () => tcs.TrySetResult(true),
                                onRejected: () => tcs.TrySetResult(false)));
                        return await tcs.Task;
                }
            },
            onSandboxBypass: sandboxBypass);

        ShellTools.Register(_registry, ws, onSandboxBypass: sandboxBypass);
        SearchTools.Register(_registry, ws);
        GraphTools.Register(_registry, ws);
        TestTools.Register(_registry, ws);
        WebTools.Register(_registry);
        RegisterAskUserTool();
    }

    private void RegisterAskUserTool()
    {
        _registry.OnAskUser = async (question, ct) =>
        {
            await Task.CompletedTask;
            return _unavailable.BlockAskUser(question);
        };

        _registry.Register(new ToolDefinition
        {
            Name        = "ask_user",
            Description =
                "Pause and ask the user a question. " +
                "Returns exactly what the user types. " +
                "Use this for open-ended answers: paths, names, preferences.",
            Parameters = new()
            {
                ["question"] = new ToolParameter("string",
                    "The question to display. Be specific."),
            },
            Required         = ["question"],
            RequiresApproval = false,
            Handler = async (args, ct) =>
            {
                var question = args.TryGetValue("question", out var q) ? q?.ToString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(question))
                    return "[ERROR] ask_user requires a non-empty 'question' argument.";
                if (_registry.OnAskUser is null)
                    return "[No UI available — user must answer manually in the chat.]";

                AddActivity(new ActivityEvent(ActivityKind.Info, "ask_user",
                    $"Waiting for user: {question[..Math.Min(60, question.Length)]}…", DateTime.Now));
                var answer = await _registry.OnAskUser(question, ct);
                AddActivity(new ActivityEvent(ActivityKind.Info, "ask_user",
                    $"User answered: {answer}", DateTime.Now));
                return answer;
            }
        });
    }

    // ── Approval gate ─────────────────────────────────────────────────────

    private void OnApprovalRequested(PendingApproval pending)
    {
        var tc = pending.Call;

        if (tc.Name == "write_file")
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                var pathArg  = tc.Arguments.TryGetValue("path",    out var p) ? p?.ToString() ?? "" : "";
                var content  = tc.Arguments.TryGetValue("content", out var c) ? c?.ToString() ?? "" : "";
                var reason   = tc.Arguments.TryGetValue("reason",  out var r) ? r?.ToString() ?? "" : "";
                var fullPath = Path.IsPathRooted(pathArg)
                    ? pathArg : Path.Combine(_session.WorkspaceRoot, pathArg);
                var oldText  = File.Exists(fullPath) ? File.ReadAllText(fullPath) : "";

                _agentPanel.ShowDiff(fullPath, oldText, content, reason,
                    onApproved: () =>
                    {
                        _approvals.Approve(pending);
                        Dispatcher.UIThread.InvokeAsync(() => _editorPanel.RefreshFile(fullPath));
                    },
                    onRejected: () => _approvals.Reject(pending));
            });
        }
        else
        {
            _ = Task.Run(async () =>
            {
                var approved = await _agentPanel.ShowShellApproval(tc);
                if (approved) _approvals.Approve(pending);
                else          _approvals.Reject(pending);
            });
        }
    }

    // ── Trust level ───────────────────────────────────────────────────────

    private void ApplyTrustLevel(TrustLevel level)
    {
        _approvals.Level     = level;
        _settings.TrustLevel = level;
        _settings.Save();

        var allPills = new[] { BtnTrustPlan, BtnTrustGuarded, BtnTrustStandard, BtnTrustFullAuto };
        var tags     = new[] { "Plan", "Guarded", "Standard", "FullAuto" };
        var colors   = new[] { "#4A9FD9", "#76B900", "#CCA700", "#F44747" };

        for (int i = 0; i < allPills.Length; i++)
        {
            bool active = tags[i] == level.ToString();
            allPills[i].Background = active
                ? new SolidColorBrush(Color.Parse(colors[i]))
                : Brushes.Transparent;
            allPills[i].Foreground = active
                ? Brushes.Black
                : new SolidColorBrush(Color.Parse("#666666"));
        }

        if (level == TrustLevel.Plan)
            _agentPanel.SetMode(isPlan: true);

        SetStatus(level == TrustLevel.FullAuto
            ? "⚡ Full Auto — all tools run without prompts"
            : $"{TrustLevelInfo.Icon(level)} {TrustLevelInfo.Label(level)} mode active");
    }

    private void BtnTrust_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { Tag: string tag } && Enum.TryParse<TrustLevel>(tag, out var level))
            ApplyTrustLevel(level);
    }

    // ── Status-bar approval chips ─────────────────────────────────────────

    private void OnApprovalPendingCountChanged()
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var pending = _approvals.Pending;
            if (pending.Count == 0)
            {
                BdrApprovalChip.IsVisible = false;
            }
            else
            {
                var first = pending[0];
                TbApprovalChipLabel.Text = first.Call.Name == "write_file"
                    ? $"⏳  Diff pending  ·  {Path.GetFileName(first.Call.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "")}"
                    : $"⏳  {first.Call.Name} needs approval";
                BdrApprovalChip.IsVisible = true;
            }
        });
    }

    private void BtnStatusApprove_Click(object? sender, RoutedEventArgs e) => _approvals.ApproveFirst();
    private void BtnStatusReject_Click(object? sender, RoutedEventArgs e)  => _approvals.RejectFirst();

    // ── Activity log ──────────────────────────────────────────────────────

    private void AddActivity(ActivityEvent ev) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        _allActivityItems.Add(ev);
        if (_allActivityItems.Count > 5000) _allActivityItems.RemoveAt(0);

        if (ev.Verbosity <= _settings.ActivityVerbosity)
        {
            _activityItems.Add(ev);
            if (_activityItems.Count > 2000) _activityItems.RemoveAt(0);
            ScrollActivityToEnd();
        }
    });

    private void ScrollActivityToEnd()
    {
        // Post to ensure layout has been updated before we scroll
        Dispatcher.UIThread.Post(
            () => ActivityScroll.Offset = new Vector(0, double.MaxValue),
            DispatcherPriority.Background);
    }

    private void SetActivityVerbosity(int level)
    {
        _settings.ActivityVerbosity = level;
        _settings.Save();
        UpdateVerbosityButtons(level);
        _activityItems.Clear();
        foreach (var ev in _allActivityItems.Where(e => e.Verbosity <= level).TakeLast(2000))
            _activityItems.Add(ev);
        ScrollActivityToEnd();
    }

    private void UpdateVerbosityButtons(int level)
    {
        if (VerbBtn1 == null) return;
        var btns = new[] { VerbBtn1, VerbBtn2, VerbBtn3, VerbBtn4, VerbBtn5 };
        for (int i = 0; i < btns.Length; i++)
        {
            btns[i].Background = (i + 1 == level)
                ? new SolidColorBrush(Color.Parse("#1E90FF"))
                : Brushes.Transparent;
            btns[i].Foreground = (i + 1 == level)
                ? Brushes.White
                : new SolidColorBrush(Color.Parse("#999999"));
        }
    }

    private void VerbBtn_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var level))
            SetActivityVerbosity(level);
    }

    private async void ActivityCopyDetail_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ActivityEvent ev
            && TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            await cb.SetTextAsync(ev.Detail);
    }

    private async void ActivityCopyFull_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem mi && mi.DataContext is ActivityEvent ev
            && TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            await cb.SetTextAsync($"[{ev.Timestamp:HH:mm:ss}] {ev.Label}: {ev.Detail}");
    }

    private void ActivityClearLog_Click(object? sender, RoutedEventArgs e)
    {
        _activityItems.Clear();
        _allActivityItems.Clear();
    }

    private async void ActivitySaveOutput_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var ext  = mi.Tag as string ?? "txt";
        var lines = _allActivityItems;
        string content;

        if (ext == "md")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("# Activity Log");
            sb.AppendLine($"**Workspace:** {_session.WorkspaceRoot}");
            sb.AppendLine($"**Model:** {_session.ActiveModel}");
            sb.AppendLine($"**Exported:** {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("| Time | Kind | Label | Detail |");
            sb.AppendLine("|------|------|-------|--------|");
            foreach (var ev in lines)
                sb.AppendLine($"| {ev.Timestamp:HH:mm:ss} | {ev.Kind} | {ev.Label} | {ev.Detail.Replace("|", "\\|").Replace("\n", " ")} |");
            content = sb.ToString();
        }
        else
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Activity Log — {_session.WorkspaceRoot} — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('-', 80));
            foreach (var ev in lines)
                sb.AppendLine($"[{ev.Timestamp:HH:mm:ss}] {ev.Kind,-11} {ev.Label,-20} {ev.Detail}");
            content = sb.ToString();
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
        {
            Title           = "Save Activity Log",
            SuggestedFileName = $"TheOrc_Activity_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExtension  = ext,
            FileTypeChoices = ext switch
            {
                "md"  => [new Avalonia.Platform.Storage.FilePickerFileType("Markdown") { Patterns = ["*.md"]  }],
                "log" => [new Avalonia.Platform.Storage.FilePickerFileType("Log file") { Patterns = ["*.log"] }],
                _     => [new Avalonia.Platform.Storage.FilePickerFileType("Text file") { Patterns = ["*.txt"] }],
            },
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(content);
            AddActivity(new ActivityEvent(ActivityKind.Info, "Log saved", file.Name, DateTime.Now));
        }
    }

    private void ActivityDetail_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var el = sender as Control;
        var ev = el?.DataContext as ActivityEvent;
        if (ev?.HasFilePath != true) return;

        var parts = ev.Detail.Split(' ', '\t', '\n');
        var path  = parts.FirstOrDefault(p => p.Length > 3 &&
            (p.Contains(":\\") || p.Contains(":/")) &&
            File.Exists(p.Trim('"', '\'')));
        if (path == null) return;

        ShowEditorPane();
        _editorPanel.OpenFile(path.Trim('"', '\''));
        e.Handled = true;
    }

    // ── Status + context ──────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        var profile = ModelProfiles.Get(_session.ActiveModel);
        SbModel.Text   = $"⚙ {profile.Name} · {profile.ToolSet.ToString().ToLower()}";
        SbWorkspace.Text = $"📁 {Path.GetFileName(_session.WorkspaceRoot)}";
        TbModelBadge.Text = $"{profile.Name} · {profile.ToolSet.ToString().ToLower()}";
        ToolTip.SetTip(TbModelBadge, _session.ActiveModel);

        Task.Run(async () =>
        {
            var branch = await _git.GetBranchAsync(_session.WorkspaceRoot);
            await Dispatcher.UIThread.InvokeAsync(() =>
                SbBranch.Text = branch != null ? $"⬡ {branch}" : "");
        });
    }

    private void ShowBuildStamp()
    {
        var asm  = System.Reflection.Assembly.GetExecutingAssembly();
        var info = (Attribute.GetCustomAttribute(asm,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        var ver   = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "?";
        var sha   = parts.Length > 1 ? parts[1] : "no-git";
        SbBuild.Text = $"v{ver} · {sha}";
        var exe = Environment.ProcessPath;
        ToolTip.SetTip(SbBuild, $"Build {info}\n{exe}\nbuilt {(exe != null ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "?")}");
        if (sha.EndsWith("-dirty"))
            SbBuild.Foreground = new SolidColorBrush(Color.Parse("#CCA700"));
    }

    private void UpdateContextDisplay()
    {
        var est = TokenCostEstimator.Estimate(
            _context.UsedTokens, _context.MaxTokens, _agentPanel?.PendingInputText ?? "");

        TbContextPct.Text = est.InputTokens > 0
            ? $"{_context.UsagePercent:F0}% ctx · next ≈{est.TotalTokens / 1000.0:F1}k tok"
            : $"{_context.UsagePercent:F0}% ctx";
        ToolTip.SetTip(TbContextPct, est.Summary(tokensPerSecond: 25));
        TbContextPct.Foreground = _context.IsCritical || !est.FitsContext
            ? new SolidColorBrush(Color.Parse("#F44747"))
            : _context.IsWarning
                ? new SolidColorBrush(Color.Parse("#CCA700"))
                : new SolidColorBrush(Color.Parse("#7A8A6A"));
        _agentPanel?.SetTokenDisplay(_context.UsedTokens, _context.MaxTokens);
    }

    private void SetStatus(string msg) => SbStatus.Text = msg;

    // ── Screen recording ──────────────────────────────────────────────────

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();
        }
        else
        {
            try
            {
                _recorder.Start(this);
                BdrRecording.IsVisible  = true;
                TbRecordingTime.Text    = "REC 00:00";
                AddActivity(new ActivityEvent(ActivityKind.Info, "Recording",
                    "Screen recording started — F12 to stop", DateTime.Now));
                SetStatus("Recording… press F12 to stop");
            }
            catch (Exception ex)
            {
                SetStatus($"Recording failed: {ex.Message}");
            }
        }
    }

    private void BdrRecording_Click(object? sender, PointerPressedEventArgs e) => ToggleRecording();

    private void BdrScreenshot_Click(object? sender, PointerPressedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", "Screenshots");
            Directory.CreateDirectory(dir);

            var fileName = $"TheOrc_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var filePath = Path.Combine(dir, fileName);

            var w = (int)Bounds.Width;
            var h = (int)Bounds.Height;
            if (w <= 0 || h <= 0) return;

            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));
            rtb.Render(this);
            rtb.Save(filePath);

            SetStatus($"📷 Screenshot saved → {fileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Screenshot failed: {ex.Message}");
        }
    }

    private void Menu_OpenRecordings(object? sender, RoutedEventArgs e) => ScreenRecorder.OpenRecordingsFolder();
    private void Menu_ToggleRecording(object? sender, RoutedEventArgs e) => ToggleRecording();

    // ── Keyboard shortcuts ────────────────────────────────────────────────

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.F1 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            HelpWindow.ShowGuide(this);
        }
        if (e.Key == Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            ToggleRecording();
        }
        if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            OpenCommandPalette();
        }
        if (e.Key == Key.E && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            SidebarContent.Content = _explorerPanel;
        }
        if (e.Key == Key.C && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            ToggleEditorPane();
        }
        if (e.Key == Key.R && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            e.Handled = true;
            OpenRulesFile();
        }
        if (e.Key == Key.N && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            Menu_NewSession(this, new RoutedEventArgs());
        }
        if (e.Key == Key.O && e.KeyModifiers == KeyModifiers.Control)
        {
            e.Handled = true;
            _explorerPanel.OpenFolderDialog();
        }
    }

    // ── Command palette ───────────────────────────────────────────────────

    private void OpenCommandPalette()
    {
        var commands = BuildCommands();
        CmdPaletteContent.RegisterCommands(commands);
        CmdPaletteContent.CommandSelected -= OnPaletteCommand;
        CmdPaletteContent.CommandSelected += OnPaletteCommand;
        CmdPaletteContent.Dismissed       -= ClosePalette;
        CmdPaletteContent.Dismissed       += ClosePalette;
        CmdPalettePopup.PlacementTarget    = this;
        CmdPalettePopup.IsOpen             = true;
    }

    private void ClosePalette() => CmdPalettePopup.IsOpen = false;

    private void OnPaletteCommand(PaletteCommand cmd)
    {
        switch (cmd.Id)
        {
            case "show.explorer":    SidebarContent.Content = _explorerPanel; break;
            case "mode.plan":        _agentPanel.SetMode(isPlan: true);  break;
            case "mode.execute":     _agentPanel.SetMode(isPlan: false); break;
            case "model.picker":     SbModel_Click(this, null!); break;
            case "open.folder":      _explorerPanel.OpenFolderDialog(); break;
            case "edit.rules":       OpenRulesFile(); break;
            case "pentest.enable":
            case "workspace.rules":  OpenWorkspaceRules(); break;
            case "show.settings":    BtnSettings_Click(this, new RoutedEventArgs()); break;
            case "tool.editor":      BtnTools_Click(this, new RoutedEventArgs()); break;
            case "tool.new":
                BtnTools_Click(this, new RoutedEventArgs());
                _toolEditorPanel.Compiler      = _toolCompiler;
                _toolEditorPanel.WorkspaceRoot = _session.WorkspaceRoot;
                break;
            case "session.new":
                _session = new ProjectSession { WorkspaceRoot = _session.WorkspaceRoot, ActiveModel = _session.ActiveModel };
                _agentPanel.Session = _session;
                AddActivity(new ActivityEvent(ActivityKind.Info, "Session", "New session started", DateTime.Now));
                break;
            case "model.switch.coder14":  OnModelSelected("qwen2.5-coder:14b"); break;
            case "model.switch.coder7":   OnModelSelected("qwen2.5-coder:7b");  break;
            case "model.switch.gemma":    OnModelSelected("gemma4:e4b"); break;
            case "model.switch.llama":    OnModelSelected("llama3.1:8b"); break;
            case "model.switch.hermes":   OnModelSelected("hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M"); break;
            case "model.switch.mistral":  OnModelSelected("mistral-small"); break;
            case "model.switch.dolphin7": OnModelSelected("hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M"); break;
            case "model.switch.deepseek": OnModelSelected("deepseek-coder-v2:16b"); break;
            case "model.switch.security": var sec = GetBestSecurityModel(); if (sec != null) OnModelSelected(sec); break;
        }
    }

    private IEnumerable<PaletteCommand> BuildCommands()
    {
        var list = new List<PaletteCommand>
        {
            new() { Id="show.explorer",   Label="Show File Explorer",      Detail="Open the workspace file browser",                             Icon="📁", Shortcut="Ctrl+Shift+E", SortOrder=10, Keywords=["file","explorer","sidebar"] },
            new() { Id="mode.plan",       Label="Switch to Plan mode",     Detail="Agent produces a plan for review — no tools run",             Icon="●",  Shortcut="",             SortOrder=20, Keywords=["plan","mode"] },
            new() { Id="mode.execute",    Label="Switch to Execute mode",  Detail="Agent uses tools and can write files",                         Icon="▶",  Shortcut="",             SortOrder=21, Keywords=["execute","run","mode"] },
            new() { Id="model.picker",    Label="Change Model…",           Detail="Open the model picker flyout",                                Icon="⚙",  Shortcut="",             SortOrder=30, Keywords=["model","switch","llm"] },
            new() { Id="open.folder",     Label="Open Folder…",            Detail="Choose a new workspace folder",                               Icon="📁", Shortcut="",             SortOrder=35, Keywords=["folder","open","workspace"] },
            new() { Id="edit.rules",      Label="Edit Rules File",         Detail="Open .agent.md in the code editor",                           Icon="📋", Shortcut="Ctrl+Shift+R", SortOrder=37, Keywords=["rules","agent","md"] },
            new() { Id="workspace.rules", Label="Workspace Rules…",        Detail="Apply a rules preset or edit .agent.md for this workspace",   Icon="📝", Shortcut="",             SortOrder=38, Keywords=["workspace","rules","pentest","security"] },
            new() { Id="show.settings",   Label="Settings",                Detail="Configure Ollama host, model, workspace",                     Icon="⚙",  Shortcut="",             SortOrder=39, Keywords=["settings","config","ollama"] },
            new() { Id="session.new",     Label="New Session",             Detail="Clear conversation history and start fresh",                  Icon="⬡",  Shortcut="",             SortOrder=40, Keywords=["new","clear","session"] },
            new() { Id="tool.editor",     Label="Open Tool Editor",        Detail="Write and hot-load custom tools",                             Icon="🔧", Shortcut="",             SortOrder=55, Keywords=["tool","custom","plugin","code"] },
            new() { Id="tool.new",        Label="New Custom Tool",         Detail="Scaffold a new ICustomTool class",                            Icon="🔧", Shortcut="",             SortOrder=56, Keywords=["tool","new","scaffold","custom"] },
            new() { Id="model.switch.security", Label="Use Best Security Model", Detail="Auto-selects highest-priority security research model", Icon="🛡️", Shortcut="", SortOrder=59, Keywords=["security","pentest","best","auto"] },
        };

        if (_installedModels.Contains("qwen2.5-coder:14b"))
            list.Add(new() { Id="model.switch.coder14", Label="Use Qwen2.5-Coder 14B",   Detail="Best speed/quality balance for coding",         Icon="⚡", SortOrder=50, Keywords=["qwen","coder","14b"] });
        if (_installedModels.Contains("qwen2.5-coder:7b"))
            list.Add(new() { Id="model.switch.coder7",  Label="Use Qwen2.5-Coder 7B",    Detail="Faster coder, good for quick edits",            Icon="⚡", SortOrder=51, Keywords=["qwen","coder","7b"] });
        if (_installedModels.Contains("gemma4:e4b"))
            list.Add(new() { Id="model.switch.gemma",   Label="Use Gemma 4 (E4B)",        Detail="Very fast, 32k context",                        Icon="⚡", SortOrder=52, Keywords=["gemma","fast"] });
        if (_installedModels.Contains("llama3.1:8b"))
            list.Add(new() { Id="model.switch.llama",   Label="Use Llama 3.1 8B",         Detail="Fast general chat model",                       Icon="⚡", SortOrder=53, Keywords=["llama","fast","chat"] });

        return list;
    }

    // ── Activity bar ──────────────────────────────────────────────────────

    private void BtnExplorer_Click(object? sender, RoutedEventArgs e) =>
        SidebarContent.Content = _explorerPanel;

    private void BtnAgent_Click(object? sender, RoutedEventArgs e)
    {
        MainContent.Content    = _agentPanel;
        _sessionPanel.Refresh();
        SidebarContent.Content = _sessionPanel;
    }

    private void BtnCheckpoints_Click(object? sender, RoutedEventArgs e)
    {
        _checkpointPanel.SetWorkspace(_session.WorkspaceRoot);
        SidebarContent.Content = _checkpointPanel;
    }

    private void BtnTools_Click(object? sender, RoutedEventArgs e)
    {
        MainContent.Content    = _toolEditorPanel;
        SidebarContent.Content = _explorerPanel;
    }

    // ── Mode toggle ───────────────────────────────────────────────────────

    private void BtnModeSingle_Click(object? sender, RoutedEventArgs e) => SetMode("single");
    private void BtnModeSwarm_Click(object? sender, RoutedEventArgs e)  => SetMode("swarm");
    private void BtnModeChat_Click(object? sender, RoutedEventArgs e)   => SetMode("chat");
    private void BtnModePit_Click(object? sender, RoutedEventArgs e)    => SetMode("pit");
    private void BtnModeHive_Click(object? sender, RoutedEventArgs e)   => SetMode("hive");
    private void BtnModeUpdate_Click(object? sender, RoutedEventArgs e) => SetMode("update");

    private void SetMode(string mode)
    {
        if (_settings.LastMode == "single") _settings.LastSingleModel = _session.ActiveModel;
        else if (_settings.LastMode == "swarm") _settings.LastSwarmModel = _session.ActiveModel;

        if (mode == "swarm")
        {
            var swarmModel = BestSwarmModel(_settings.LastSwarmModel);
            if (swarmModel != _session.ActiveModel)
                ApplyModelSwitch(swarmModel, saveToSingleSlot: false);

            _swarmPanel.ActiveModel   = _session.ActiveModel;
            _swarmPanel.WorkspaceRoot = _session.WorkspaceRoot;
            _swarmPanel.LocalUrl      = _settings.OllamaHost;
            _swarmPanel.PopulateModelPickers(_installedModels);
            _swarmPanel.RefreshGate();
            MainContent.Content    = _swarmPanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "chat")
        {
            _chatPanel.OllamaClient = _ollama;
            _chatPanel.SetModels(_installedModels, _session.ActiveModel);
            MainContent.Content    = _chatPanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "hive")
        {
            _hivePanel.LocalUrl = _settings.OllamaHost;
            _hivePanel.Refresh();
            _hivePanel.OnApplyRpcWorkers        -= OnApplyRpcWorkers;
            _hivePanel.OnApplyRpcWorkers        += OnApplyRpcWorkers;
            _hivePanel.OnWarchiefTargetSelected -= OnWarchiefTargetSelected;
            _hivePanel.OnWarchiefTargetSelected += OnWarchiefTargetSelected;
            MainContent.Content    = _hivePanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "update")
        {
            var identity  = Services.Hive.HiveIdentity.Load();
            var election  = _hiveNodeServer?.ElectionService;
            _updatePanel.Settings    = _settings;
            _updatePanel.LocalNodeId = identity.NodeId;
            _updatePanel.IsWarchief  = election?.WarchiefNodeId == identity.NodeId;
            _updatePanel.Refresh();
            MainContent.Content    = _updatePanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "pit")
        {
            _pitPanel.WorkspaceRoot = _session.WorkspaceRoot;
            _pitPanel.OllamaHost    = _settings.OllamaHost;
            _pitPanel.Refresh();
            MainContent.Content    = _pitPanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "pitboss")
        {
            ShowPitBoss();
        }
        else
        {
            var singleModel = BestSingleModel(_settings.LastSingleModel);
            if (singleModel != _session.ActiveModel)
                ApplyModelSwitch(singleModel, saveToSingleSlot: true);
            MainContent.Content    = _agentPanel;
            SidebarContent.Content = _sessionPanel;
            _sessionPanel.Refresh();
        }

        UpdateModeToggle(mode);
        _settings.LastMode = mode;
        _settings.Save();
    }

    private void ShowPitBoss()
    {
        _pitBossPanel = new PitBossPanel
        {
            WorkspaceRoot = _session.WorkspaceRoot,
            OllamaHost    = _settings.OllamaHost,
            OllamaModel   = _settings.PitBossModel,
            RunRepo       = _runRepo,
            PlanRepo      = _planRepo,
        };
        _pitBossPanel.BackRequested += () => Dispatcher.UIThread.InvokeAsync(() => SetMode("pit"));
        _pitBossPanel.StatusChanged += msg => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg));
        _pitBossPanel.ForgeHandoff  += (plan, datasetPath) => Dispatcher.UIThread.InvokeAsync(() =>
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Pit Boss",
                $"Dataset ready, launching Forge: {plan.Goal}", DateTime.Now));
            SetStatus($"Handing off to Forge: {plan.AdapterName}");
            SetMode("pit");
            _pitPanel.LaunchFromPlan(plan, datasetPath);
        });
        MainContent.Content    = _pitBossPanel;
        SidebarContent.Content = _explorerPanel;
    }

    private string BestSwarmModel(string lastSwarm)
    {
        if (!string.IsNullOrEmpty(lastSwarm) &&
            _installedModels.Contains(lastSwarm, StringComparer.OrdinalIgnoreCase))
            return lastSwarm;
        var nemotron = _installedModels
            .FirstOrDefault(m => m.Contains("nemotron", StringComparison.OrdinalIgnoreCase));
        return nemotron ?? _session.ActiveModel;
    }

    private string BestSingleModel(string lastSingle)
    {
        if (!string.IsNullOrEmpty(lastSingle) &&
            _installedModels.Contains(lastSingle, StringComparer.OrdinalIgnoreCase))
            return lastSingle;
        var preferred = new[]
        {
            "qwen2.5-coder:14b", "qwen2.5-coder:7b", "gemma4:12b",
            "qwen2.5-coder:3b", "gemma4:e4b", "llama3.1:8b",
        };
        return preferred.FirstOrDefault(p =>
                   _installedModels.Contains(p, StringComparer.OrdinalIgnoreCase))
               ?? _session.ActiveModel;
    }

    private void UpdateModeToggle(string mode)
    {
        var activeGreen = new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x00));
        var activeFg    = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
        var inactiveBg  = Brushes.Transparent;
        var inactiveFg  = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        BtnModeSingle.Background = mode == "single" ? activeGreen : inactiveBg;
        BtnModeSingle.Foreground = mode == "single" ? activeFg    : inactiveFg;
        BtnModeSwarm.Background  = mode == "swarm"  ? activeGreen : inactiveBg;
        BtnModeSwarm.Foreground  = mode == "swarm"  ? activeFg    : inactiveFg;
        BtnModeChat.Background   = mode == "chat"   ? activeGreen : inactiveBg;
        BtnModeChat.Foreground   = mode == "chat"   ? activeFg    : inactiveFg;
        BtnModePit.Background    = mode == "pit"    ? activeGreen : inactiveBg;
        BtnModePit.Foreground    = mode == "pit"    ? activeFg    : inactiveFg;
        BtnModeHive.Background   = mode == "hive"   ? activeGreen : inactiveBg;
        BtnModeHive.Foreground   = mode == "hive"   ? activeFg    : inactiveFg;
        BtnModeUpdate.Background = mode == "update" ? activeGreen : inactiveBg;
        BtnModeUpdate.Foreground = mode == "update" ? activeFg    : inactiveFg;
    }

    private void RestoreLastMode()
    {
        var mode = _settings.LastMode switch
        {
            "swarm"  => "swarm",
            "chat"   => "chat",
            "pit"    => "pit",
            "hive"   => "hive",
            "update" => "update",
            _        => "single",
        };
        SetMode(mode);
    }

    private void BtnSettings_Click(object? sender, RoutedEventArgs e)
    {
        _settingsPanel.LoadSettings(_settings);
        SidebarContent.Content = _settingsPanel;
    }

    private void ResumeSession(ProjectSession session)
    {
        _session = session;
        _agentPanel.Loop    = _loop;
        _agentPanel.Session = _session;
        _agentPanel.SetWorkspace(_session.WorkspaceRoot, confirmed: false);
        _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
        _checkpointPanel.SetWorkspace(_session.WorkspaceRoot);
        UpdateStatusBar();
        SidebarContent.Content = _explorerPanel;
        AddActivity(new ActivityEvent(ActivityKind.Info, "Session",
            $"Resumed: {Path.GetFileName(_session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))}", DateTime.Now));
    }

    private Task OpenWorkspaceAsync(string path)
    {
        Dispatcher.UIThread.InvokeAsync(() => ConfirmWorkspace(path));
        return Task.CompletedTask;
    }

    private void ConfirmWorkspace(string path)
    {
        _session.WorkspaceRoot        = path;
        _session.IsWorkspaceConfirmed = true;
        RegisterAllTools();
        _explorerPanel.LoadWorkspace(path);
        _agentPanel.SetWorkspace(path, confirmed: true);
        _checkpointPanel.SetWorkspace(path);
        _toolEditorPanel.WorkspaceRoot = path;
        _swarmPanel.WorkspaceRoot = path;
        _swarmPanel.RefreshGate();
        UpdateStatusBar();
        _ = AutoLoadWorkspaceToolsAsync(path);

        _settings.DefaultWorkspace = path;
        _settings.RecentWorkspaces.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentWorkspaces.Insert(0, path);
        if (_settings.RecentWorkspaces.Count > 10)
            _settings.RecentWorkspaces.RemoveRange(10, _settings.RecentWorkspaces.Count - 10);
        _settings.Save();

        Dispatcher.UIThread.InvokeAsync(RebuildRecentMenu);
        AddActivity(new ActivityEvent(ActivityKind.Info, "Workspace",
            $"Opened: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}", DateTime.Now));
        InitDataLayer(path);

        // CodeGraph: attach to the confirmed workspace's DB, then start the background index.
        // Attach is called here (not inside InitDataLayer) so standalone InitDataLayer calls
        // at startup — before workspace confirmation — don't trigger a premature index.
        if (_sqlStore is { } store)
        {
            _codeGraph.OnStatus -= OnCodeGraphStatus;
            _codeGraph.OnStatus += OnCodeGraphStatus;
            _codeGraph.Attach(new Services.CodeGraph.Data.GraphRepository(store));
            _codeGraph.StartIndexing(path);
        }
    }

    private void OnCodeGraphStatus(string msg)
    {
        if (_windowClosed) return;
        Dispatcher.UIThread.InvokeAsync(() =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "CodeGraph", msg, DateTime.Now)));
    }

    private async void SwitchToRecentWorkspace(string path)
    {
        if (!Directory.Exists(path))
        {
            await DialogHelper.ShowInfoAsync(this, "Open Recent",
                $"Folder not found:\n{path}\n\nIt will be removed from the recent list.");
            _settings.RecentWorkspaces.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RebuildRecentMenu();
            return;
        }
        ConfirmWorkspace(path);
    }

    private void RebuildRecentMenu()
    {
        MiOpenRecent.Items.Clear();
        var valid = _settings.RecentWorkspaces.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        MiOpenRecent.IsEnabled = valid.Count > 0;
        foreach (var p in valid)
        {
            var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = p;
            var item = new MenuItem { Header = name };
            ToolTip.SetTip(item, p);
            var captured = p;
            item.Click += (_, _) => SwitchToRecentWorkspace(captured);
            MiOpenRecent.Items.Add(item);
        }
    }

    private async Task AutoLoadWorkspaceToolsAsync(string workspaceRoot)
    {
        var results = await _toolCompiler.ScanAndLoadAllAsync(workspaceRoot);
        foreach (var (file, ok, error) in results)
        {
            if (ok)
                AddActivity(new ActivityEvent(ActivityKind.Info, "Custom Tool",
                    $"Auto-loaded: {file}", DateTime.Now));
            else
                AddActivity(new ActivityEvent(ActivityKind.Warning, "Custom Tool",
                    $"Failed to load {file}: {error}", DateTime.Now));
        }
        if (results.Count > 0)
            await Dispatcher.UIThread.InvokeAsync(() =>
                _toolEditorPanel.RefreshLoadedBadge(_toolCompiler.LoadedToolNames));
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        var oldBackend = _settings.Backend;
        _settings = newSettings;

        if (newSettings.Backend != oldBackend ||
            (newSettings.Backend == InferenceBackend.LlamaCpp &&
             _llamaServer != null &&
             (_llamaServer.ModelPath   != newSettings.LlamaCppModelPath ||
              _llamaServer.RuntimePath != newSettings.LlamaCppRuntimePath ||
              _llamaServer.Port        != newSettings.LlamaCppPort)))
        {
            _llamaServer?.Stop();
            _llamaServer = BuildServerManager(newSettings);
            _ollama.Host    = newSettings.InferenceBaseUrl;
            _ollama.Backend = newSettings.Backend;

            if (newSettings.Backend == InferenceBackend.LlamaCpp && _llamaServer != null)
            {
                AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp",
                    "Restarting server with new settings…", DateTime.Now));
                _ = _llamaServer.StartAsync();
            }
        }
        else
        {
            _ollama.Host = newSettings.InferenceBaseUrl;
        }

        if (!string.IsNullOrEmpty(newSettings.DefaultWorkspace)
            && newSettings.DefaultWorkspace != _session.WorkspaceRoot)
        {
            // Use ConfirmWorkspace so checkpoint panel, tool editor, swarm, and SQL store stay in sync
            ConfirmWorkspace(newSettings.DefaultWorkspace);
        }

        UpdateStatusBar();
        var backendTag = newSettings.Backend == InferenceBackend.LlamaCpp
            ? $"llama.cpp → port {newSettings.LlamaCppPort}"
            : $"Ollama → {newSettings.OllamaHost}";
        AddActivity(new ActivityEvent(ActivityKind.Info, "Settings",
            $"Saved — {backendTag}", DateTime.Now));
    }

    // ── Backend helpers ───────────────────────────────────────────────────

    private LlamaServerManager? BuildServerManager(AppSettings s)
    {
        if (s.Backend != InferenceBackend.LlamaCpp) return null;
        var mgr = new LlamaServerManager
        {
            RuntimePath = s.LlamaCppRuntimePath,
            ModelPath   = s.LlamaCppModelPath,
            Port        = s.LlamaCppPort,
            GpuLayers   = s.LlamaCppGpuLayers,
            ContextSize = s.LlamaCppContextSize,
            Threads     = s.LlamaCppThreads,
        };
        mgr.OnLog += msg =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp", msg, DateTime.Now));
        mgr.OnStatusChanged += running =>
            Dispatcher.UIThread.InvokeAsync(() =>
                SetStatus(running ? "⚙ llama.cpp server running" : "⚠ llama.cpp server stopped"));
        return mgr;
    }

    private IModelRuntime BuildModelRuntime() =>
        _settings.Backend == InferenceBackend.LlamaCpp && _llamaServer is not null
            ? new LlamaCppServerRuntime(_llamaServer)
            : new OllamaRuntime(_ollama);

    // ── HIVE MIND C2: Apply RPC workers ──────────────────────────────────

    private async void OnApplyRpcWorkers(IReadOnlyList<string> endpoints)
    {
        if (_settings.Backend != InferenceBackend.LlamaCpp || _llamaServer is null)
        {
            await DialogHelper.ShowInfoAsync(this, "HIVE MIND — RPC",
                "RPC VRAM chaining requires the llama.cpp backend.\n\n" +
                "Switch to llama.cpp in Settings → Backend, then try again.");
            return;
        }

        AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
            $"Applying RPC workers: {string.Join(", ", endpoints)}", DateTime.Now));

        var previousEndpoints = _llamaServer.RpcEndpoints.ToArray();
        _llamaServer.Stop();
        _llamaServer.RpcEndpoints = [.. endpoints];

        var ready = await _llamaServer.StartAsync();
        if (ready)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
                $"⚡ RPC chain active — {endpoints.Count} worker(s) contributing VRAM", DateTime.Now));
        }
        else
        {
            _llamaServer.Stop();
            _llamaServer.RpcEndpoints = [.. previousEndpoints];
            _ = _llamaServer.StartAsync();
            AddActivity(new ActivityEvent(ActivityKind.Warning, "HIVE MIND",
                "RPC chain failed — restored previous configuration.", DateTime.Now));
        }
    }

    // ── HIVE MIND: Warchief target ────────────────────────────────────────

    private async void OnWarchiefTargetSelected(string url)
    {
        _settings.HiveWarchiefUrl = url;
        _settings.HiveWorkerMode  = true;
        _settings.Save();
        AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
            $"🎯 Warchief target set to {url}. Worker mode enabled. Restart TheOrc to connect.", DateTime.Now));
        await DialogHelper.ShowInfoAsync(this, "HIVE MIND — Warchief Target",
            $"Warchief target set to:\n  {url}\n\n" +
            "Worker mode has been enabled. Restart TheOrc for the worker agent to connect.");
    }

    private static string ResolveWarchiefNodeId(string warchiefUrl)
    {
        if (!Uri.TryCreate(warchiefUrl, UriKind.Absolute, out var uri)) return "";
        var host = uri.Host;
        if (string.IsNullOrEmpty(host)) return "";

        var peers = Services.Hive.HivePeerStore.Default.All()
            .Where(p => !p.Revoked && !string.IsNullOrEmpty(p.LastKnownAddress))
            .ToList();

        var match = peers.FirstOrDefault(p =>
            p.LastKnownAddress.Split(':')[0].Equals(host, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match.NodeId;

        try
        {
            var addresses = System.Net.Dns.GetHostAddresses(host);
            foreach (var addr in addresses)
            {
                var addrStr = addr.ToString();
                match = peers.FirstOrDefault(p => p.LastKnownAddress.StartsWith(addrStr + ":"));
                if (match is not null) return match.NodeId;
            }
        }
        catch { /* DNS failure is non-fatal */ }

        return "";
    }

    // ── Model picker ──────────────────────────────────────────────────────

    private void SbModel_Click(object? sender, PointerPressedEventArgs? e)
    {
        if (_installedModels.Count == 0) return;

        ModelPickerContent.Load(_installedModels, _session.ActiveModel);
        ModelPickerContent.ModelSelected -= OnModelSelected;
        ModelPickerContent.ModelSelected += OnModelSelected;
        ModelPickerContent.Dismissed     -= CloseModelPicker;
        ModelPickerContent.Dismissed     += CloseModelPicker;

        ModelPickerPopup.PlacementTarget = SbModel;
        ModelPickerPopup.IsOpen          = true;
        ModelPickerContent.Focus();
    }

    private void OnModelSelected(string modelId)
    {
        CloseModelPicker();
        ApplyModelSwitch(modelId, saveToSingleSlot: _settings.LastMode != "swarm");
    }

    private void ApplyModelSwitch(string modelId, bool saveToSingleSlot)
    {
        _session.ActiveModel    = modelId;
        _swarmPanel.ActiveModel = modelId;
        _swarmPanel.RefreshGate();
        _chatPanel.SetActiveModel(modelId);

        var isSpecialty = modelId.Contains("hermes",     StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("heretic",    StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("uncensored", StringComparison.OrdinalIgnoreCase);

        if (!isSpecialty)
        {
            if (saveToSingleSlot) _settings.LastSingleModel = modelId;
            else                  _settings.LastSwarmModel  = modelId;
            _settings.DefaultModel = modelId;
        }
        _settings.Save();
        RegisterAllTools();
        UpdateStatusBar();
        AddActivity(new ActivityEvent(ActivityKind.Info, "Model", $"Switched to: {modelId}", DateTime.Now));
    }

    private void CloseModelPicker() => ModelPickerPopup.IsOpen = false;

    // ── Editor split pane ─────────────────────────────────────────────────

    private bool _editorVisible = false;

    private void ShowEditorPane()
    {
        if (_editorVisible) return;
        _editorVisible = true;
        EditorHostGrid.ColumnDefinitions[1].Width = new GridLength(4);
        EditorHostGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
        SplitterEditor.IsVisible  = true;
        EditorContent.IsVisible   = true;
    }

    private void HideEditorPane()
    {
        _editorVisible = false;
        EditorHostGrid.ColumnDefinitions[1].Width = new GridLength(0);
        EditorHostGrid.ColumnDefinitions[2].Width = new GridLength(0);
        SplitterEditor.IsVisible  = false;
        EditorContent.IsVisible   = false;
    }

    private void ToggleEditorPane()
    {
        if (_editorVisible) HideEditorPane();
        else                ShowEditorPane();
    }

    // ── Update badge ──────────────────────────────────────────────────────

    private void ShowUpdateBadge(UpdateChecker.UpdateResult result)
    {
        _pendingReleaseUrl         = result.ReleaseUrl;
        TbUpdateBadge.Text         = $"↑ v{result.LatestVersion} available";
        BdrUpdateBadge.IsVisible   = true;
        UpdateDot.IsVisible        = true;
        Title = $"Orchestrator IDE  v{result.CurrentVersion}";
    }

    private void BdrUpdateBadge_Click(object? sender, PointerPressedEventArgs e)
        => OpenSelfUpdateDialog(_settings.LastKnownLatestVersion ?? "");

    private void OpenSelfUpdateDialog(string latestVersion)
    {
        SelfUpdateWindow.ShowWindow(this, _settings);
        AddActivity(new ActivityEvent(ActivityKind.Info, "Update",
            $"Opened update window (latest known: v{latestVersion}).", DateTime.Now));
    }

    // ── Menu handlers — File ──────────────────────────────────────────────

    private void Menu_OpenFolder(object? sender, RoutedEventArgs e) => _explorerPanel.OpenFolderDialog();

    private void Menu_OpenExplorer(object? sender, RoutedEventArgs e)
        => FileExplorerPanel.RevealInExplorer(_session.WorkspaceRoot);

    private void Menu_NewSession(object? sender, RoutedEventArgs e)
    {
        _session = new ProjectSession
        {
            WorkspaceRoot = _session.WorkspaceRoot,
            ActiveModel   = _session.ActiveModel,
        };
        _agentPanel.Session = _session;
        AddActivity(new ActivityEvent(ActivityKind.Info, "Session", "New session started", DateTime.Now));
    }

    private void Menu_Exit(object? sender, RoutedEventArgs e) => Close();

    // ── Menu handlers — Edit ──────────────────────────────────────────────

    private void Menu_CommandPalette(object? sender, RoutedEventArgs e) => OpenCommandPalette();
    private void Menu_Settings(object? sender, RoutedEventArgs e)       => BtnSettings_Click(sender, e);

    // ── Menu handlers — View ──────────────────────────────────────────────

    private void Menu_ShowExplorer(object? sender, RoutedEventArgs e)  => SidebarContent.Content = _explorerPanel;
    private void Menu_ToggleEditor(object? sender, RoutedEventArgs e)  => ToggleEditorPane();
    private void Menu_EditRules(object? sender, RoutedEventArgs e)     => OpenRulesFile();

    private async void OpenRulesFile()
    {
        var path = _rules.FindRulesFile(_session.WorkspaceRoot);

        if (path == null)
        {
            var yes = await DialogHelper.ShowYesNoAsync(this, "No Rules File Found",
                $"No rules file found in:\n{_session.WorkspaceRoot}\n\nCreate a default .agent.md now?");
            if (!yes) return;

            path = Path.Combine(_session.WorkspaceRoot, ".agent.md");
            var projectName = Path.GetFileName(_session.WorkspaceRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            File.WriteAllText(path, RulesLoader.DefaultTemplate(projectName));
        }

        ShowEditorPane();
        _editorPanel.OpenFile(path);
    }

    private void Menu_WordWrap(object? sender, RoutedEventArgs e)
    {
        MiWordWrap.IsChecked = !MiWordWrap.IsChecked;
        _editorPanel.SetWordWrap(MiWordWrap.IsChecked == true);
    }

    private void Menu_FontBigger(object? sender, RoutedEventArgs e)
    {
        _editorFontSize = Math.Min(32, _editorFontSize + 1);
        _editorPanel.SetFontSize(_editorFontSize);
    }

    private void Menu_FontSmaller(object? sender, RoutedEventArgs e)
    {
        _editorFontSize = Math.Max(8, _editorFontSize - 1);
        _editorPanel.SetFontSize(_editorFontSize);
    }

    private void Menu_FontReset(object? sender, RoutedEventArgs e)
    {
        _editorFontSize = 13.0;
        _editorPanel.SetFontSize(_editorFontSize);
    }

    // ── Menu handlers — Agent ─────────────────────────────────────────────

    private void Menu_ModePlan(object? sender, RoutedEventArgs e)    => _agentPanel.SetMode(isPlan: true);
    private void Menu_ModeExecute(object? sender, RoutedEventArgs e) => _agentPanel.SetMode(isPlan: false);
    private void Menu_ChangeModel(object? sender, RoutedEventArgs e) => SbModel_Click(sender, null);

    // ── Workspace / agent rules ───────────────────────────────────────────

    private void OpenWorkspaceRules()
    {
        _unavailable.Report(
            "Workspace Rules",
            "AgentBuilderDialog",
            "Edit .agent.md directly.");
    }

    private void OpenGlobalAgentPicker()
    {
        _unavailable.Report("Global Agent", "AgentBuilderDialog");
    }

    private void HandleAgentBuilderResult(object target) { /* stubs for post-Phase 4 wire-up */ }

    private void RefreshGlobalAgentBadge()
    {
        var path = AgentPresets.GlobalAgentPath;
        if (!File.Exists(path)) { _agentPanel.SetGlobalAgentLabel("No global agent"); return; }
        var content   = File.ReadAllText(path);
        var firstLine = content.TrimStart().Split('\n').FirstOrDefault() ?? "";
        var name = firstLine.TrimStart('#').Trim();
        _agentPanel.SetGlobalAgentLabel(string.IsNullOrEmpty(name) ? "Custom" : name);
    }

    private string? GetBestSecurityModel()
        => ModelProfiles.SecurityPreference
            .FirstOrDefault(p => _installedModels.Contains(p, StringComparer.OrdinalIgnoreCase));

    // ── Menu handlers — Models ────────────────────────────────────────────

    private void Menu_ModelChoose(object? sender, RoutedEventArgs e)  => SbModel_Click(this, null);

    private void Menu_ModelDownload(object? sender, RoutedEventArgs e)
    {
        _unavailable.Report("Models", "ModelDownloaderWindow");
    }

    private async void Menu_WarmUp(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var role = mi.Tag?.ToString() ?? "worker";
        SetStatus($"Warming up {role} model…");
        using var svc = new ModelWarmUpService(_settings);
        try
        {
            await svc.WarmUpAsync(role,
                msg => Dispatcher.UIThread.InvokeAsync(() => SetStatus(msg)),
                _ => { });
        }
        catch (Exception ex)
        {
            SetStatus($"Warm-up error: {ex.Message}");
        }
    }

    private void Menu_WarmUpEdit(object? sender, RoutedEventArgs e)
    {
        var win = new WarmUpEditorWindow(_settings);
        win.Show(this);
    }

    private void Menu_ModelLibrary(object? sender, RoutedEventArgs e)
    {
        _unavailable.Report("Models", "ModelLibraryWindow");
    }

    private void Menu_ModelWiki(object? sender, RoutedEventArgs e)
    {
        _unavailable.Report("Models", "ModelWikiWindow");
    }

    private void Menu_RunModelCapabilityTest(object? sender, RoutedEventArgs e)
    {
        _unavailable.Report("Models", "ModelCapabilityTestDialog");
    }

    private void Menu_RunToolProbe(object? sender, RoutedEventArgs e)
    {
        _unavailable.Report("Models", "ToolCallTestWindow");
    }

    // ── Menu handlers — Help ──────────────────────────────────────────────

    private void Menu_HelpTopics(object? sender, RoutedEventArgs e)
        => HelpWindow.ShowGuide(this);

    private void Menu_HelpDocumentation(object? sender, RoutedEventArgs e)
        => HelpWindow.ShowGuide(this, "USER_GUIDE.md");

    private void Menu_HelpTroubleshooting(object? sender, RoutedEventArgs e)
        => HelpWindow.ShowGuide(this, "TROUBLESHOOTING.md");

    private void Menu_HelpModelGuide(object? sender, RoutedEventArgs e)
        => HelpWindow.ShowGuide(this, "MODEL_GUIDE.md");

    private void Menu_HelpTrainingPitGuide(object? sender, RoutedEventArgs e)
        => HelpWindow.ShowGuide(this, "TRAINING_PIT_GUIDE.md");

    private async Task Menu_CheckUpdatesAsync(bool force)
    {
        SetStatus("Checking for updates…");
        var result = await UpdateChecker.CheckAsync(_settings, force: force);

        if (result == null)
        {
            SetStatus("Update check failed — check your internet connection.");
            return;
        }

        if (result.UpdateAvailable)
        {
            ShowUpdateBadge(result);
            SetStatus($"v{result.LatestVersion} available");
            var yes = await DialogHelper.ShowYesNoAsync(this, "Update Available",
                $"A new version of TheOrc is available!\n\n" +
                $"  Current:  v{result.CurrentVersion}\n" +
                $"  Latest:   v{result.LatestVersion} — {result.ReleaseName}\n\n" +
                "Click Yes to open the release page, No to dismiss.");
            if (yes) UpdateChecker.OpenReleasePage(result.ReleaseUrl);
        }
        else
        {
            BdrUpdateBadge.IsVisible = false;
            SetStatus($"You're up to date — v{result.CurrentVersion}");
            if (force)
                await DialogHelper.ShowInfoAsync(this, "No Updates Available",
                    $"TheOrc is up to date.\n\nVersion: v{result.CurrentVersion}");
        }
    }

    private void Menu_CheckUpdates(object? sender, RoutedEventArgs e) =>
        _ = Menu_CheckUpdatesAsync(force: true);

    private void Menu_ReleaseNotes(object? sender, RoutedEventArgs e)
        => UpdateChecker.OpenReleasePage(_pendingReleaseUrl);

    private void Menu_GitHub(object? sender, RoutedEventArgs e)
        => UpdateChecker.OpenReleasePage("https://github.com/hardcoreerik/TheOrc");

    // ── Model status click ────────────────────────────────────────────────

    private void SbModelStatus_Click(object? sender, PointerPressedEventArgs e)
        => Menu_ModelDownload(this, new RoutedEventArgs());
}
