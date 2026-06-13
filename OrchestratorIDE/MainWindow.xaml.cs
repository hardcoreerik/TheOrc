using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;
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
    private          LlamaServerManager?  _llamaServer;
    private          ModelStatusService?  _modelStatus;
    private          Services.Hive.HiveBeacon?     _hiveBeacon;
    private          Services.Hive.HiveNodeServer? _hiveNodeServer;
    private          Services.Hive.HiveRpcWorker?  _hiveRpcWorker;

    // ── State ─────────────────────────────────────────────────────────────
    private ProjectSession           _session;
    private AppSettings              _settings = AppSettings.Load();
    private string                   _pendingReleaseUrl = "";
    private List<string>             _installedModels = [];
    private readonly ObservableCollection<ActivityEvent> _activityItems = [];
    private readonly List<ActivityEvent>                 _allActivityItems = [];  // full unfiltered log

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
    private readonly UI.Panels.ChatPanel     _chatPanel;
    private readonly UI.Panels.TrainingPitPanel _pitPanel;
    private readonly UI.Panels.HivePanel _hivePanel = new();

    // ── Screen recorder ───────────────────────────────────────────────────
    private readonly ScreenRecorder _recorder = new();

    // Editor font size (shared across sessions, adjustable via View menu)
    private double _editorFontSize = 13.0;

    public MainWindow()
    {
        InitializeComponent();

        // Boot services — configure inference client from saved settings
        _ollama    = new OllamaClient(_settings.InferenceBaseUrl, _settings.Backend);
        _llamaServer = BuildServerManager(_settings);

        // Stop llama-server and recorder on window close
        Closed += (_, _) =>
        {
            _llamaServer?.Stop();
            _recorder.Stop();
            _modelStatus?.Stop();
            _modelStatus?.Dispose();
            _hiveBeacon?.Dispose();
            _hiveNodeServer?.Dispose();
            _hiveRpcWorker?.Dispose();
        };

        // Recorder events → status bar
        _recorder.OnTick    += t => Dispatcher.Invoke(() => TbRecordingTime.Text = $"REC {t}");
        _recorder.OnStopped += path => Dispatcher.Invoke(() =>
        {
            BdrRecording.Visibility = Visibility.Collapsed;
            AddActivity(new ActivityEvent(ActivityKind.Info, "Recording saved", System.IO.Path.GetFileName(path), DateTime.Now));
            SetStatus("Recording saved — F12 to record again");
        });
        _approvals = new ApprovalQueue();
        _registry  = new ToolRegistry(_approvals);
        _context   = new ContextManager(32_768);
        _git       = new GitCheckpoint();
        _rules     = new RulesLoader();
        _store     = new SessionStore();
        _loop      = new AgentLoop(_ollama, _registry, _context, _git, _rules);

        // Default session — use saved settings, refine model in OnLoadedAsync
        _session = new ProjectSession
        {
            WorkspaceRoot = !string.IsNullOrEmpty(_settings.DefaultWorkspace)
                ? _settings.DefaultWorkspace
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ActiveModel = _settings.DefaultModel,
        };

        // Register tools — wire diff preview into AgentPanel
        RegisterAllTools();

        // Build panels
        _explorerPanel = new FileExplorerPanel();
        _explorerPanel.WorkspaceChanged += path => ConfirmWorkspace(path);
        _explorerPanel.FileSelected += path =>
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Open", Path.GetFileName(path), DateTime.Now));
            ShowEditorPane();
            _editorPanel?.OpenFile(path);
        };

        // Code editor — wires up close-pane button
        _editorPanel = new CodeEditorPanel();
        _editorPanel.ClosePane += HideEditorPane;
        EditorContent.Content   = _editorPanel;

        _agentPanel = new AgentPanel
        {
            Loop              = _loop,
            Session           = _session,
            OnStatusChanged   = msg => Dispatcher.Invoke(() => SetStatus(msg)),
        };

        // Wire live token streaming → chat bubble
        _loop.OnToken += token => _agentPanel.AppendStreamingToken(token);

        // Wire token usage → bubble badge + session counter
        _loop.OnUsage += (p, c) => _agentPanel.OnTokensUsed(p, c);

        // Badge click → open folder picker (same as explorer panel open)
        _agentPanel.WorkspaceChangeRequested += () => _explorerPanel.PromptOpenFolder();

        // Auto-save session after every message cycle
        _agentPanel.ConversationChanged += async () =>
        {
            try { await _store.SaveAsync(_session); }
            catch { /* non-fatal */ }
        };

        // Wire activity log
        ActivityLog.ItemsSource = _activityItems;
        UpdateVerbosityButtons(_settings.ActivityVerbosity);
        _loop.Activity += ev => Dispatcher.InvokeAsync(() =>
        {
            _activityItems.Add(ev);
            if (_activityItems.Count > 500) _activityItems.RemoveAt(0);
            ActivityScroll.ScrollToBottom();
        });

        // Context meter
        _context.UsageChanged       += () => Dispatcher.Invoke(UpdateContextDisplay);
        _agentPanel.InputTextChanged += UpdateContextDisplay;   // live next-request estimate

        ShowBuildStamp();

        // Approval gate — use diff viewer in AgentPanel for write_file, dialog for shell
        _approvals.ApprovalRequested  += OnApprovalRequested;
        _approvals.PendingCountChanged += OnApprovalPendingCountChanged;

        // Layer 2: unknown tool card — shown in the diff panel slot
        _registry.OnUnknownTool = async call =>
        {
            var names = _registry.GetRegisteredNames();
            return await _agentPanel.ShowUnknownToolCard(call, names);
        };

        // Rules badge + pentest button + security model auto-switch
        _loop.OnRulesLoaded += filePath =>
        {
            _agentPanel.SetRulesStatus(filePath);

            var isPentest = filePath != null && PentestRules.IsPentestTemplate(filePath);
            _agentPanel.SetPentestActive(isPentest);

            // Auto-switch to best available security research model when a pentest
            // workspace is detected. Gated by AutoModelSwitch setting.
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

        // Rules badge click → open the rules file in the code editor
        _agentPanel.RulesEditRequested += OpenRulesFile;

        // Workspace Rules button → open rules editor for this workspace
        _agentPanel.WorkspaceRulesRequested += OpenWorkspaceRules;

        // Global Agent badge → open global agent picker
        _agentPanel.GlobalAgentRequested += OpenGlobalAgentPicker;

        // Initialize global agent badge label
        RefreshGlobalAgentBadge();

        // Build settings panel
        _settingsPanel = new SettingsPanel(_ollama);
        _settingsPanel.LoadSettings(_settings);
        _settingsPanel.SettingsSaved                += OnSettingsSaved;
        _settingsPanel.CheckUpdatesRequested        += async () =>
            await Menu_CheckUpdatesAsync(force: true);
        _settingsPanel.RegenerateAgentFileRequested += async () =>
            await RegenerateAgentFileAsync();

        _settingsPanel.OpenFolderAsWorkspaceRequested += folder =>
            _ = OpenWorkspaceAsync(folder);

        _settingsPanel.ScanAnalysisReady += prompt =>
        {
            // Switch to agent view and inject the scan prompt as a user message
            BtnAgent_Click(this, new System.Windows.RoutedEventArgs());
            _agentPanel?.InjectUserMessage(prompt);
        };

        // Checkpoint browser
        _checkpointPanel = new CheckpointBrowserPanel(_git);
        _checkpointPanel.CheckpointRestored += sha =>
        {
            // Reload the file explorer and editor after a restore
            _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
            AddActivity(new ActivityEvent(ActivityKind.Git, "Restored", $"Hard-reset to {sha[..8]}", DateTime.Now));
        };

        // Session history browser
        _sessionPanel = new SessionBrowserPanel(_store);
        _sessionPanel.SessionSelected += ResumeSession;

        // Tool editor + compiler (Phase 7)
        _toolCompiler    = new ToolCompiler(_registry);
        _toolEditorPanel = new ToolEditorPanel
        {
            Compiler      = _toolCompiler,
            WorkspaceRoot = _session.WorkspaceRoot,
        };

        // Swarm board panel — multi-agent hub-and-spoke (Phase 8)
        _swarmPanel = new SwarmBoardPanel
        {
            Ollama        = _ollama,
            ActiveModel   = _session.ActiveModel,
            WorkspaceRoot = _session.WorkspaceRoot,
            Settings      = _settings,
        };
        _swarmPanel.StatusChanged            += msg  => Dispatcher.Invoke(() => SetStatus(msg));
        _swarmPanel.OnActivity               += msg  => Dispatcher.Invoke(() =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "Swarm", msg, DateTime.Now)));
        _swarmPanel.WorkspaceChangeRequested += ()   => Dispatcher.Invoke(_explorerPanel.OpenFolderDialog);
        _swarmPanel.BossModelChanged         += model => Dispatcher.Invoke(() =>
        {
            _session.ActiveModel     = model;
            _swarmPanel.ActiveModel  = model;
            _settings.LastSwarmModel = model;
            _settings.Save();
            UpdateStatusBar();
        });
        _swarmPanel.WorkerModelChanged       += model => Dispatcher.Invoke(() =>
        {
            _settings.LastWorkerModel = model;
            _settings.Save();
        });
        _swarmPanel.ResearcherModelChanged   += model => Dispatcher.Invoke(() =>
        {
            _settings.LastResearcherModel = model;
            _settings.Save();
        });

        // Chat panel — research-focused chat with web search tools
        _chatPanel = new UI.Panels.ChatPanel
        {
            OllamaClient = _ollama,
        };

        // Training Pit panel — dataset farming dashboard (Phase 2.5)
        _pitPanel = new UI.Panels.TrainingPitPanel
        {
            WorkspaceRoot = _session.WorkspaceRoot,
        };
        _pitPanel.StatusChanged += msg => Dispatcher.Invoke(() => SetStatus(msg));
        _pitPanel.OnActivity    += msg => Dispatcher.Invoke(() =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "Training Pit", msg, DateTime.Now)));
        _pitPanel.LiveStateChanged += (active, waiting) => Dispatcher.Invoke(() =>
        {
            PitLiveDot.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            PitQueueBadge.Text    = waiting > 0 ? waiting.ToString() : "";
        });

        // Default sidebar = explorer
        SidebarContent.Content = _explorerPanel;
        _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);

        // Main area = agent panel (single mode until OnLoadedAsync restores last mode)
        MainContent.Content = _agentPanel;

        // Show unconfirmed badge on startup (default workspace, not explicitly opened)
        _agentPanel.SetWorkspace(_session.WorkspaceRoot, confirmed: false);

        // Set initial toggle state — always starts "single"; RestoreLastMode() updates it
        UpdateModeToggle("single");

        UpdateStatusBar();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async Task OnLoadedAsync()
    {
        // ── Start llama.cpp server if that backend is selected ────────────
        if (_settings.Backend == InferenceBackend.LlamaCpp && _llamaServer != null)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp",
                "Starting local inference server…", DateTime.Now));

            var ready = await _llamaServer.StartAsync(ct: default);
            if (!ready)
            {
                AddActivity(new ActivityEvent(ActivityKind.Warning, "llama.cpp",
                    "Server failed to start — check RuntimePath and ModelPath in Settings.", DateTime.Now));
            }
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

            // ── Model selection priority ──────────────────────────────────
            // 1. Per-mode last-used model — only when RestoreLastModel is enabled.
            //    Falls back to DefaultModel for backwards compatibility.
            // 2. Quality-ordered preferred list (AutoModelSwitch fallback).
            // 3. Whatever is first in the installed list.
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
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model",
                    $"Restored: {best}", DateTime.Now));
            }
            else if (_settings.AutoModelSwitch)
            {
                // General-purpose coding models only — security/uncensored models
                // (Hermes, Heretic, etc.) are NOT auto-selected; they must be
                // chosen explicitly via the command palette or model picker.
                var preferred = new[] {
                    "qwen2.5-coder:14b",
                    "qwen2.5-coder:7b",
                    "gemma4:12b",
                    "nemotron-mini-4b-q5",
                    "nemotron-3-nano:4b-q8_0",
                    "nemotron-3-nano:4b",
                    "qwen2.5-coder:3b",
                    "qwen2.5:14b-instruct",
                    "gemma4:e4b",
                    "llama3.1:8b",
                };
                best = preferred.FirstOrDefault(p => models.Contains(p, StringComparer.OrdinalIgnoreCase))
                    ?? models.First();
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model",
                    $"Auto-selected: {best}", DateTime.Now));
            }
            else
            {
                best = models.First();
                AddActivity(new ActivityEvent(ActivityKind.Info, "Model",
                    $"Active: {best}", DateTime.Now));
            }

            _session.ActiveModel    = best;
            _swarmPanel.ActiveModel = best;
            Dispatcher.Invoke(() =>
            {
                // Populate chat panel model picker with all installed models
                _chatPanel.SetModels(_installedModels, best);
                UpdateStatusBar();
                // Restore last mode — demote swarm silently if gate not satisfied
                RestoreLastMode();
                // Restore saved trust level
                ApplyTrustLevel(_settings.TrustLevel);
            });
        }
        else
        {
            var hint = _settings.Backend == InferenceBackend.LlamaCpp
                ? "No models found — check LlamaCppRuntimePath and LlamaCppModelPath in Settings."
                : "No models found — check Ollama host connection in Settings.";
            AddActivity(new ActivityEvent(ActivityKind.Warning, backendLabel, hint, DateTime.Now));

            // ── Portable-zip bootstrap: offer to run the bundled setup wizard ──
            // When the user extracted the portable zip, OrchestratorSetup.exe sits
            // next to OrchestratorIDE.exe.  If we find it and have no working
            // backend, offer to launch it so a completely fresh machine can be
            // configured without any manual steps.
            TryLaunchBundledSetup();
        }

        // ── Auto-update check (silent, background) ────────────────────────
        _ = Task.Run(async () =>
        {
            var result = await UpdateChecker.CheckAsync(_settings);
            if (result?.UpdateAvailable == true)
            {
                Dispatcher.Invoke(() => ShowUpdateBadge(result));
                AddActivity(new ActivityEvent(ActivityKind.Info, "Update",
                    $"v{result.LatestVersion} available — Help → Check for Updates", DateTime.Now));
            }
        });

        var saved = await _store.LoadLatestAsync();
        if (saved != null)
        {
            _session = saved;
            _agentPanel.Session = _session;
            AddActivity(new ActivityEvent(ActivityKind.Info, "Recovered",
                $"Session from {saved.LastActivityAt:g}", DateTime.Now));
            _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
            Dispatcher.Invoke(UpdateStatusBar);
        }

        // ── Model status polling ─────────────────────────────────────────────
        _modelStatus = new ModelStatusService(_settings);
        _modelStatus.OnUpdate += snap => Dispatcher.InvokeAsync(() =>
        {
            var line = snap.ShortStatusLine + snap.VramDisplay;
            TxtSbModelStatus.Text       = line;
            SbModelStatus.Visibility    = string.IsNullOrEmpty(line)
                ? Visibility.Collapsed : Visibility.Visible;
        });
        _modelStatus.Start(TimeSpan.FromSeconds(8));

        // ── HIVE MIND: start beacon + node server (+ optional RPC worker) ───────
        if (_settings.HiveMindEnabled)
            _ = Task.Run(async () =>
            {
                var models = _installedModels.Count > 0 ? _installedModels : [];
                var vramMb = (int)(_settings.DetectedVramGb * 1024);
                var name   = Environment.MachineName;

                // HIVE MIND C2: start llama-rpc-server if the runtime is present.
                // This lets a coordinator offload model layers onto this machine's GPU.
                int rpcPort = 0;
                if (!string.IsNullOrEmpty(_settings.LlamaCppRuntimePath))
                {
                    _hiveRpcWorker = new Services.Hive.HiveRpcWorker
                    {
                        RuntimePath = _settings.LlamaCppRuntimePath,
                        Port        = Services.Hive.HiveRpcWorker.DefaultPort,
                    };
                    _hiveRpcWorker.OnLog += msg => AddActivity(
                        new ActivityEvent(ActivityKind.Info, "RPC Worker", msg, DateTime.Now));

                    if (_hiveRpcWorker.IsAvailable && _hiveRpcWorker.Start())
                        rpcPort = Services.Hive.HiveRpcWorker.DefaultPort;
                }

                var lanes = rpcPort > 0
                    ? new[] { "inference", "coder", "researcher", "rpc_worker" }
                    : new[] { "inference", "coder", "researcher" };

                var info = new Services.Hive.HiveNodeInfo(
                    name, _settings.OllamaHost, [.. models], vramMb, vramMb, lanes, rpcPort);

                _hiveNodeServer = new Services.Hive.HiveNodeServer();
                _hiveNodeServer.Start(info);

                _hiveBeacon = new Services.Hive.HiveBeacon();
                _hiveBeacon.Start(name, _settings.OllamaHost, models, vramMb);

                _hiveBeacon.OnNodeSeen += msg => Dispatcher.InvokeAsync(() =>
                {
                    if (_hivePanel.IsLoaded) _hivePanel.OnBeaconNodeSeen(msg);
                });

                await Task.CompletedTask;
                var rpcNote = rpcPort > 0 ? $" · RPC worker on :{rpcPort}" : "";
                AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
                    $"Active as {name}{rpcNote}", DateTime.Now));
            });

        // ── CLI overrides (--workspace, --autotest) ───────────────────────
        // Used by FlaUI / CI tests for headless autonomous runs.
        //   --workspace <path>  auto-confirms a workspace without a folder picker
        //   --autoapprove       sets AutoApprove = true so write_file never blocks
        //   (Note: --autotest is intercepted by App.xaml.cs and opens AutoTestWindow instead
        //    of MainWindow — do NOT use --autotest here)
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
            // Start watching for file-based IPC commands from the FlaUI test harness.
            // The test writes a prompt to <workspace>/.flaui_cmd; we read it and call
            // AutoSend directly — bypassing IValueProvider.SetValue which truncates at ~383 chars.
            WatchForFlaUICommands();
        }

        // ── Auto-open last workspace on startup ───────────────────────────
        // If no --workspace CLI arg was given, auto-confirm the most recent
        // workspace so Execute mode is unlocked immediately on startup.
        if (wsArgIdx < 0)
        {
            var lastWs = _settings.RecentWorkspaces.FirstOrDefault(Directory.Exists);
            if (!string.IsNullOrEmpty(lastWs) && lastWs != _session.WorkspaceRoot)
            {
                // Already in the session — just confirm it
                Dispatcher.Invoke(() => ConfirmWorkspace(lastWs));
            }
            else if (!string.IsNullOrEmpty(_session.WorkspaceRoot)
                     && Directory.Exists(_session.WorkspaceRoot)
                     && !_session.IsWorkspaceConfirmed)
            {
                // No explicit recent list yet — confirm whatever the session has
                Dispatcher.Invoke(() => ConfirmWorkspace(_session.WorkspaceRoot));
            }
            Dispatcher.Invoke(RebuildRecentMenu);
        }

        // ── First-run personalisation wizard ──────────────────────────────
        // Skipped in --autoapprove mode (FlaUI headless tests) to avoid a modal
        // dialog blocking the main window during automated runs.
        if (!_settings.FirstRunComplete && !cliArgs.Contains("--autoapprove"))
        {
            Dispatcher.Invoke(() =>
            {
                var wizard = new UI.FirstRunWindow(_settings, _session.WorkspaceRoot, _installedModels);
                wizard.Owner = this;
                var saved2 = wizard.ShowDialog();
                if (saved2 == true)
                    AddActivity(new ActivityEvent(ActivityKind.Info, "Agent File",
                        $"Personalised .agent.md written to {_session.WorkspaceRoot}", DateTime.Now));
            });
        }
    }

    // ── File-based IPC (FlaUI test harness) ──────────────────────────────

    private System.IO.FileSystemWatcher? _flaUIWatcher;

    /// <summary>
    /// Watches the workspace for a ".flaui_cmd" file written by the FlaUI test harness.
    /// When detected, reads the file, deletes it, and calls _agentPanel.AutoSend(prompt)
    /// on the UI thread — completely bypasses IValueProvider.SetValue truncation.
    /// Only called when --autoapprove is present (test mode).
    /// </summary>
    private void WatchForFlaUICommands()
    {
        var dir = _session.WorkspaceRoot;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;

        _flaUIWatcher = new System.IO.FileSystemWatcher(dir, ".flaui_cmd")
        {
            NotifyFilter        = System.IO.NotifyFilters.FileName
                                | System.IO.NotifyFilters.LastWrite,
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
            // Brief delay so the writer can fully flush the file before we read it
            System.Threading.Thread.Sleep(150);
            if (!File.Exists(path)) return;
            var prompt = File.ReadAllText(path, System.Text.Encoding.UTF8).Trim();
            File.Delete(path);
            if (string.IsNullOrWhiteSpace(prompt)) return;

            Dispatcher.Invoke(() => _agentPanel.AutoSend(prompt));
        }
        catch { /* best-effort; test harness will detect if agent didn't start */ }
    }

    // ── Agent file regeneration (called from Settings panel) ─────────────

    public async Task RegenerateAgentFileAsync()
    {
        var wizard = new UI.FirstRunWindow(_settings, _session.WorkspaceRoot, _installedModels);
        wizard.Owner = this;
        var result = wizard.ShowDialog();
        if (result == true)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Agent File",
                $"Agent file regenerated in {_session.WorkspaceRoot}", DateTime.Now));

            // Reload rules so the agent picks up the new file immediately
            await _rules.LoadAsync(_session.WorkspaceRoot);
        }
    }

    // ── Portable-zip bootstrap ────────────────────────────────────────────

    /// <summary>
    /// If OrchestratorSetup.exe is found next to this exe (portable-zip layout)
    /// and no AI backend is reachable, prompt the user and optionally launch it.
    /// The app exits after handing off to the installer so both can't run at once.
    /// </summary>
    private void TryLaunchBundledSetup()
    {
        var setupExe = Path.Combine(AppContext.BaseDirectory, "OrchestratorSetup.exe");
        if (!File.Exists(setupExe)) return;

        AddActivity(new ActivityEvent(ActivityKind.Info, "Setup",
            "OrchestratorSetup.exe found — no runtime detected on this machine.", DateTime.Now));

        Dispatcher.Invoke(() =>
        {
            var result = MessageBox.Show(
                "No AI runtime was detected on this machine.\n\n" +
                "OrchestratorSetup.exe is included in this package and will automatically " +
                "install the llama.cpp runtime and download a model sized for your GPU — " +
                "no prior AI tools required.\n\n" +
                "Run the setup wizard now?",
                "Welcome to TheOrc — First-Time Setup",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.Yes);

            if (result == MessageBoxResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName        = setupExe,
                    UseShellExecute = true,   // run as normal user launch (respects UAC)
                });

                // Hand off to the installer — close so both don't run simultaneously.
                Application.Current.Shutdown();
            }
        });
    }

    // ── Tool registration ─────────────────────────────────────────────────

    private void RegisterAllTools()
    {
        var ws = _session.WorkspaceRoot;

        // ── Sandbox bypass delegate ───────────────────────────────────────
        Func<string, string, string, CancellationToken, Task<bool>> sandboxBypass =
            async (toolName, escapedPath, sandboxRoot, ct) =>
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                              System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                ct.Register(() => tcs.TrySetResult(false));

                await Dispatcher.InvokeAsync(() =>
                {
                    var dlg = new OrchestratorIDE.UI.Dialogs.SandboxBypassDialog(
                                  toolName, escapedPath, sandboxRoot, "Agent")
                    {
                        Owner = this
                    };
                    tcs.TrySetResult(dlg.ShowDialog() == true);
                });

                return await tcs.Task;
            };

        // Async diff approval gate — write_file calls this and waits for the user's decision.
        // Guarded: show DiffViewer and await Approve/Reject.
        // Standard/FullAuto: auto-approve.
        // Plan: reject without showing UI.
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

                    default: // Guarded — show diff viewer, await user decision
                        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
                                      System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
                        ct.Register(() => tcs.TrySetResult(false));
                        await Dispatcher.InvokeAsync(() =>
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
        TestTools.Register(_registry, ws);
        WebTools.Register(_registry);
        RegisterAskUserTool();
    }

    /// <summary>
    /// Registers the ask_user tool and wires it to UserInputDialog.
    /// The agent calls ask_user(question) → modal dialog pops up →
    /// user types their answer → agent continues with that string.
    /// </summary>
    private void RegisterAskUserTool()
    {
        // Wire the hook so the tool can show the dialog on the UI thread
        _registry.OnAskUser = async (question, ct) =>
        {
            string answer = "[CANCELLED]";
            await Dispatcher.InvokeAsync(() =>
            {
                var dlg = new OrchestratorIDE.UI.Controls.UserInputDialog(question)
                {
                    Owner = this
                };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.Answer))
                    answer = dlg.Answer;
            });
            return answer;
        };

        _registry.Register(new ToolDefinition
        {
            Name        = "ask_user",
            Description =
                "Pause and ask the user a question. " +
                "Returns exactly what the user types. " +
                "Use this whenever you need the user's preference before taking an action — " +
                "e.g. 'Install system-wide (C:\\esp-idf) or workspace-only? (type system or workspace)'. " +
                "Do NOT ask yes/no with this tool — use the approval gate for that. " +
                "Use ask_user for open-ended answers: paths, names, preferences.",
            Parameters = new()
            {
                ["question"] = new ToolParameter("string",
                    "The question to display. Be specific. If there are expected values " +
                    "include them in parentheses at the end, e.g. \"(type 'system' or 'workspace')\"."),
            },
            Required         = ["question"],
            RequiresApproval = false,   // the dialog IS the interaction — no extra gate needed
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
            Dispatcher.Invoke(() =>
            {
                // Read old file for diff
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
                        // Refresh editor tab if this file is open
                        Dispatcher.InvokeAsync(() => _editorPanel.RefreshFile(fullPath));
                    },
                    onRejected: () => _approvals.Reject(pending));
            });
        }
        else
        {
            // run_shell and all other tools — inline approval card (no MessageBox)
            _ = Task.Run(async () =>
            {
                var approved = await _agentPanel.ShowShellApproval(tc);
                if (approved) _approvals.Approve(pending);
                else          _approvals.Reject(pending);
            });
        }
    }

    // ── Trust level ───────────────────────────────────────────────────────────

    /// <summary>
    /// Applies a trust level: updates ApprovalQueue, highlights the active pill,
    /// forces Plan-mode in AgentPanel when Plan is selected, and persists the choice.
    /// </summary>
    private void ApplyTrustLevel(Trust.TrustLevel level)
    {
        _approvals.Level     = level;
        _settings.TrustLevel = level;
        _settings.Save();

        // Highlight active pill — reset all then light the selected one
        var allPills = new[] { BtnTrustPlan, BtnTrustGuarded, BtnTrustStandard, BtnTrustFullAuto };
        var tags     = new[] { "Plan", "Guarded", "Standard", "FullAuto" };
        var colors   = new[] { "#4A9FD9", "#76B900", "#CCA700", "#F44747" };

        for (int i = 0; i < allPills.Length; i++)
        {
            bool active = tags[i] == level.ToString();
            allPills[i].Background = active
                ? (System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(colors[i]))
                : System.Windows.Media.Brushes.Transparent;
            allPills[i].Foreground = active
                ? System.Windows.Media.Brushes.Black
                : new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x66, 0x66, 0x66));
        }

        // Plan mode forces the agent panel into plan-only mode
        if (level == Trust.TrustLevel.Plan)
            _agentPanel.SetMode(isPlan: true);

        // Full Auto gets a brief warning flash on the status text
        if (level == Trust.TrustLevel.FullAuto)
            SetStatus("⚡ Full Auto — all tools run without prompts");
        else
            SetStatus($"{Trust.TrustLevelInfo.Icon(level)} {Trust.TrustLevelInfo.Label(level)} mode active");
    }

    private void BtnTrust_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }) return;
        if (Enum.TryParse<Trust.TrustLevel>(tag, out var level))
            ApplyTrustLevel(level);
    }

    // ── Status-bar approval chips ─────────────────────────────────────────────

    private void OnApprovalPendingCountChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            var pending = _approvals.Pending;
            if (pending.Count == 0)
            {
                BdrApprovalChip.Visibility = Visibility.Collapsed;
            }
            else
            {
                var first = pending[0];
                TbApprovalChipLabel.Text = first.Call.Name == "write_file"
                    ? $"⏳  Diff pending  ·  {Path.GetFileName(first.Call.Arguments.TryGetValue("path", out var p) ? p?.ToString() ?? "" : "")}"
                    : $"⏳  {first.Call.Name} needs approval";
                BdrApprovalChip.Visibility = Visibility.Visible;
            }
        });
    }

    private void BtnStatusApprove_Click(object sender, RoutedEventArgs e)
    {
        _approvals.ApproveFirst();
        // The DiffPanel in AgentPanel will clean itself up via its own Approved event
    }

    private void BtnStatusReject_Click(object sender, RoutedEventArgs e)
    {
        _approvals.RejectFirst();
    }

    // ── Activity log helpers ──────────────────────────────────────────────

    private void AddActivity(ActivityEvent ev) => Dispatcher.InvokeAsync(() =>
    {
        // Always keep the full log (cap at 5000)
        _allActivityItems.Add(ev);
        if (_allActivityItems.Count > 5000) _allActivityItems.RemoveAt(0);

        // Only display if within current verbosity
        if (ev.Verbosity <= _settings.ActivityVerbosity)
        {
            _activityItems.Add(ev);
            if (_activityItems.Count > 2000) _activityItems.RemoveAt(0);
            ActivityScroll.ScrollToBottom();
        }
    });

    /// <summary>
    /// Change verbosity level, rebuild filtered display from full log, persist setting.
    /// </summary>
    private void SetActivityVerbosity(int level)
    {
        _settings.ActivityVerbosity = level;
        _settings.Save();
        UpdateVerbosityButtons(level);

        // Rebuild display from full log
        _activityItems.Clear();
        foreach (var ev in _allActivityItems.Where(e => e.Verbosity <= level).TakeLast(2000))
            _activityItems.Add(ev);
        ActivityScroll.ScrollToBottom();
    }

    private void UpdateVerbosityButtons(int level)
    {
        if (VerbBtn1 == null) return;
        var btns = new[] { VerbBtn1, VerbBtn2, VerbBtn3, VerbBtn4, VerbBtn5 };
        for (int i = 0; i < btns.Length; i++)
        {
            btns[i].Background = (i + 1 == level)
                ? new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#1E90FF"))
                : System.Windows.Media.Brushes.Transparent;
            btns[i].Foreground = (i + 1 == level)
                ? System.Windows.Media.Brushes.White
                : new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#999999"));
        }
    }

    private void VerbBtn_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag && int.TryParse(tag, out var level))
            SetActivityVerbosity(level);
    }

    private void ActivityCopyDetail_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi &&
            mi.DataContext is ActivityEvent ev)
            System.Windows.Clipboard.SetText(ev.Detail);
    }

    private void ActivityCopyFull_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi &&
            mi.DataContext is ActivityEvent ev)
            System.Windows.Clipboard.SetText($"[{ev.Timestamp:HH:mm:ss}] {ev.Label}: {ev.Detail}");
    }

    private void ActivityClearLog_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _activityItems.Clear();
        _allActivityItems.Clear();
    }

    private void ActivitySaveOutput_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        var ext = mi.Tag as string ?? "txt";

        // Build content from the FULL log (not just filtered view)
        var lines = _allActivityItems;
        string content;

        if (ext == "md")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"# Activity Log");
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

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title            = "Save Activity Log",
            FileName         = $"TheOrc_Activity_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt       = $".{ext}",
            Filter           = ext switch
            {
                "md"  => "Markdown (*.md)|*.md|All files (*.*)|*.*",
                "log" => "Log file (*.log)|*.log|All files (*.*)|*.*",
                _     => "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            },
            InitialDirectory = string.IsNullOrEmpty(_session.WorkspaceRoot)
                ? Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
                : _session.WorkspaceRoot,
        };

        if (dlg.ShowDialog() == true)
        {
            File.WriteAllText(dlg.FileName, content, System.Text.Encoding.UTF8);
            AddActivity(new ActivityEvent(ActivityKind.Info, "Log saved",
                dlg.FileName, DateTime.Now));
        }
    }

    private void ActivityLog_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Find the ActivityEvent from the clicked element
        var el = e.OriginalSource as System.Windows.FrameworkElement;
        var ev = el?.DataContext as ActivityEvent;
        if (ev == null) return;

        // Clickable file paths — open in editor
        if (ev.HasFilePath)
        {
            // Extract first path-like token from Detail
            var parts = ev.Detail.Split(' ', '\t', '\n');
            var path  = parts.FirstOrDefault(p => p.Length > 3 &&
                (p.Contains(":\\") || p.Contains(":/")) &&
                System.IO.File.Exists(p.Trim('"', '\'')));
            if (path != null)
            {
                ShowEditorPane();
                _editorPanel.OpenFile(path.Trim('"', '\''));
                e.Handled = true;
            }
        }
    }

    // ── Status + context ──────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        var profile = ModelProfiles.Get(_session.ActiveModel);
        SbModel.Text      = $"⚙ {profile.Name} · {profile.ToolSet.ToString().ToLower()}";
        SbWorkspace.Text  = $"📁 {Path.GetFileName(_session.WorkspaceRoot)}";
        // Title bar chip: use friendly name so long HF model IDs don't overflow
        TbModelBadge.Text    = $"{profile.Name} · {profile.ToolSet.ToString().ToLower()}";
        TbModelBadge.ToolTip = _session.ActiveModel; // full ID on hover

        // Update git branch (async, non-blocking)
        Task.Run(async () =>
        {
            var branch = await _git.GetBranchAsync(_session.WorkspaceRoot);
            Dispatcher.Invoke(() =>
                SbBranch.Text = branch != null ? $"⬡ {branch}" : "");
        });
    }

    /// <summary>
    /// Status-bar build stamp: "v1.2.3 · a1b2c3d" (+ "-dirty" when built from an
    /// uncommitted tree). Proves at a glance which build is running — the fix
    /// for the stale-exe incidents. Source: AssemblyInformationalVersion,
    /// stamped by the StampGitCommit msbuild target.
    /// </summary>
    private void ShowBuildStamp()
    {
        var asm  = System.Reflection.Assembly.GetExecutingAssembly();
        var info = (Attribute.GetCustomAttribute(asm,
                typeof(System.Reflection.AssemblyInformationalVersionAttribute))
            as System.Reflection.AssemblyInformationalVersionAttribute)?
            .InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        var ver   = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : "?";
        var sha   = parts.Length > 1 ? parts[1] : "no-git";

        SbBuild.Text = $"v{ver} · {sha}";
        var exe = Environment.ProcessPath;
        SbBuild.ToolTip = $"Build {info}\n{exe}\nbuilt {(exe != null ? File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm") : "?")}";
        if (sha.EndsWith("-dirty"))
            SbBuild.Foreground = (Brush)FindResource("Br.Warning");
    }

    private void UpdateContextDisplay()
    {
        // Token cost estimate for the NEXT request: context + pending input +
        // an assumed response budget, with a rough local-inference ETA.
        var est = TokenCostEstimator.Estimate(
            _context.UsedTokens, _context.MaxTokens, _agentPanel?.PendingInputText ?? "");

        TbContextPct.Text = est.InputTokens > 0
            ? $"{_context.UsagePercent:F0}% ctx · next ≈{est.TotalTokens / 1000.0:F1}k tok"
            : $"{_context.UsagePercent:F0}% ctx";
        TbContextPct.ToolTip = est.Summary(tokensPerSecond: 25);
        TbContextPct.Foreground = _context.IsCritical || !est.FitsContext
            ? (Brush)FindResource("Br.Error")
            : _context.IsWarning
                ? (Brush)FindResource("Br.Warning")
                : (Brush)FindResource("Br.Text.Muted");
        _agentPanel.SetTokenDisplay(_context.UsedTokens, _context.MaxTokens);
    }

    private void SetStatus(string msg) => SbStatus.Text = msg;

    // ── Screen recording ──────────────────────────────────────────────────

    private void ToggleRecording()
    {
        if (_recorder.IsRecording)
        {
            _recorder.Stop();   // OnStopped event updates UI
        }
        else
        {
            try
            {
                _recorder.Start(this);
                BdrRecording.Visibility = Visibility.Visible;
                TbRecordingTime.Text    = "REC 00:00";
                AddActivity(new ActivityEvent(ActivityKind.Info, "Recording", "Screen recording started — F12 to stop", DateTime.Now));
                SetStatus("Recording… press F12 to stop");
            }
            catch (Exception ex)
            {
                SetStatus($"Recording failed: {ex.Message}");
            }
        }
    }

    private void BdrRecording_Click(object sender, MouseButtonEventArgs e)
        => ToggleRecording();

    private void BdrScreenshot_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", "Screenshots");
            Directory.CreateDirectory(dir);

            var fileName = $"TheOrc_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png";
            var filePath = Path.Combine(dir, fileName);

            // Capture the window using the same RenderTargetBitmap approach as ScreenRecorder
            static int SnapEven(double v) { int i = (int)v; return i % 2 == 0 ? i : i - 1; }
            int w = SnapEven(ActualWidth);
            int h = SnapEven(ActualHeight);
            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            rtb.Render(this);

            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
            using var stream = System.IO.File.OpenWrite(filePath);
            encoder.Save(stream);

            SetStatus($"📷 Screenshot saved → {fileName}");
        }
        catch (Exception ex)
        {
            SetStatus($"Screenshot failed: {ex.Message}");
        }
    }

    private void Menu_OpenRecordings(object sender, RoutedEventArgs e)
        => ScreenRecorder.OpenRecordingsFolder();

    private void Menu_ToggleRecording(object sender, RoutedEventArgs e)
        => ToggleRecording();

    // ── Keyboard shortcuts ────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // F1 — open in-app help
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            UI.Windows.HelpWindow.ShowGuide(this);
        }

        // F12 — toggle screen recording
        if (e.Key == Key.F12 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ToggleRecording();
        }

        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            OpenCommandPalette();
        }
        // Ctrl+Shift+E = file explorer
        if (e.Key == Key.E && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            SidebarContent.Content = _explorerPanel;
        }
        // Ctrl+Shift+C = toggle code editor pane
        if (e.Key == Key.C && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            ToggleEditorPane();
        }
        // Ctrl+Shift+R = open rules file in editor
        if (e.Key == Key.R && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            OpenRulesFile();
        }
        // Ctrl+N = new session
        if (e.Key == Key.N && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            Menu_NewSession(this, null!);
        }
        // Ctrl+O = open folder
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
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

        CmdPalettePopup.PlacementTarget = this;
        CmdPalettePopup.IsOpen          = true;
    }

    private void ClosePalette() => CmdPalettePopup.IsOpen = false;

    private void OnPaletteCommand(PaletteCommand cmd)
    {
        switch (cmd.Id)
        {
            case "show.explorer":     SidebarContent.Content = _explorerPanel; break;
            case "mode.plan":         _agentPanel.SetMode(isPlan: true); break;
            case "mode.execute":      _agentPanel.SetMode(isPlan: false); break;
            case "model.picker":      SbModel_Click(this, null!); break;
            case "open.folder":       _explorerPanel.OpenFolderDialog(); break;
            case "edit.rules":        OpenRulesFile(); break;
            case "pentest.enable":
            case "workspace.rules":   OpenWorkspaceRules(); break;
            case "show.settings":     BtnSettings_Click(this, null!); break;
            case "tool.editor":       BtnTools_Click(this, null!); break;
            case "tool.new":
                BtnTools_Click(this, null!);
                _toolEditorPanel.Compiler      = _toolCompiler;
                _toolEditorPanel.WorkspaceRoot = _session.WorkspaceRoot;
                break;
            case "session.new":
                _session = new ProjectSession { WorkspaceRoot = _session.WorkspaceRoot, ActiveModel = _session.ActiveModel };
                _agentPanel.Session = _session;
                AddActivity(new ActivityEvent(ActivityKind.Info, "Session", "New session started", DateTime.Now));
                break;
            case "model.switch.coder14":   OnModelSelected("qwen2.5-coder:14b"); break;
            case "model.switch.coder7":    OnModelSelected("qwen2.5-coder:7b"); break;
            case "model.switch.gemma":     OnModelSelected("gemma4:e4b"); break;
            case "model.switch.llama":     OnModelSelected("llama3.1:8b"); break;
            case "model.switch.hermes":    OnModelSelected("hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M"); break;
            case "model.switch.mistral":   OnModelSelected("mistral-small"); break;
            case "model.switch.dolphin7":  OnModelSelected("hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M"); break;
            case "model.switch.deepseek":  OnModelSelected("deepseek-coder-v2:16b"); break;
            case "model.switch.security":  var sec = GetBestSecurityModel(); if (sec != null) OnModelSelected(sec); break;
        }
    }

    private IEnumerable<PaletteCommand> BuildCommands()
    {
        var list = new List<PaletteCommand>
        {
            new() { Id="show.explorer",   Label="Show File Explorer",       Detail="Open the workspace file browser", Icon="📁", Shortcut="Ctrl+Shift+E", SortOrder=10, Keywords=["file","explorer","sidebar"] },
            new() { Id="mode.plan",       Label="Switch to Plan mode",      Detail="Agent produces a plan for review — no tools run", Icon="●", Shortcut="", SortOrder=20, Keywords=["plan","mode"] },
            new() { Id="mode.execute",    Label="Switch to Execute mode",   Detail="Agent uses tools and can write files", Icon="▶", Shortcut="", SortOrder=21, Keywords=["execute","run","mode"] },
            new() { Id="model.picker",    Label="Change Model…",            Detail="Open the model picker flyout", Icon="⚙", Shortcut="", SortOrder=30, Keywords=["model","switch","llm"] },
            new() { Id="open.folder",     Label="Open Folder…",             Detail="Choose a new workspace folder", Icon="📁", Shortcut="", SortOrder=35, Keywords=["folder","open","workspace"] },
            new() { Id="edit.rules",      Label="Edit Rules File",          Detail="Open .agent.md in the code editor (Ctrl+Shift+R)", Icon="📋", Shortcut="Ctrl+Shift+R", SortOrder=37, Keywords=["rules","agent","md","knowledge"] },
            new() { Id="workspace.rules", Label="Workspace Rules…",          Detail="Apply a rules preset or edit .agent.md for this workspace", Icon="📝", Shortcut="", SortOrder=38, Keywords=["workspace","rules","agent","md","pentest","security","preset","template"] },
            new() { Id="show.settings",   Label="Settings",                 Detail="Configure Ollama host, model, workspace", Icon="⚙", Shortcut="", SortOrder=39, Keywords=["settings","config","ollama","host"] },
            new() { Id="session.new",     Label="New Session",              Detail="Clear conversation history and start fresh", Icon="⬡", Shortcut="", SortOrder=40, Keywords=["new","clear","session","reset"] },
            new() { Id="tool.editor",     Label="Open Tool Editor",         Detail="Write and hot-load custom tools the agent can use (Phase 7)", Icon="🔧", Shortcut="", SortOrder=55, Keywords=["tool","custom","plugin","code","editor","hot","load","compile"] },
            new() { Id="tool.new",        Label="New Custom Tool",          Detail="Scaffold a new ICustomTool class in the tool editor", Icon="🔧", Shortcut="", SortOrder=56, Keywords=["tool","new","scaffold","custom","plugin","create"] },
        };

        // Quick model switch commands — standard coding models
        if (_installedModels.Contains("qwen2.5-coder:14b"))
            list.Add(new() { Id="model.switch.coder14", Label="Use Qwen2.5-Coder 14B",  Detail="Best speed/quality balance for coding tasks", Icon="⚡", SortOrder=50, Keywords=["qwen","coder","14b"] });
        if (_installedModels.Contains("qwen2.5-coder:7b"))
            list.Add(new() { Id="model.switch.coder7",  Label="Use Qwen2.5-Coder 7B",   Detail="Faster coder, good for quick edits", Icon="⚡", SortOrder=51, Keywords=["qwen","coder","7b","fast"] });
        if (_installedModels.Contains("gemma4:e4b"))
            list.Add(new() { Id="model.switch.gemma",   Label="Use Gemma 4 (E4B)",       Detail="Very fast, 32k context", Icon="⚡", SortOrder=52, Keywords=["gemma","fast"] });
        if (_installedModels.Contains("llama3.1:8b"))
            list.Add(new() { Id="model.switch.llama",   Label="Use Llama 3.1 8B",        Detail="Fast general chat model", Icon="⚡", SortOrder=53, Keywords=["llama","fast","chat"] });

        // Quick model switch commands — security research models
        if (_installedModels.Contains("hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M"))
            list.Add(new() { Id="model.switch.hermes",   Label="Use Hermes 4 14B",        Detail="NousResearch — purpose-built agentic, minimal restrictions, best tool calling", Icon="🛡️", SortOrder=60, Keywords=["hermes","security","agentic","nousresearch","unrestricted"] });
        if (_installedModels.Any(m => m.StartsWith("mistral-small", StringComparison.OrdinalIgnoreCase)))
            list.Add(new() { Id="model.switch.mistral",  Label="Use Mistral Small 24B",   Detail="128k context, native tool calling, fewer RLHF restrictions", Icon="🛡️", SortOrder=61, Keywords=["mistral","security","tools","128k"] });
        if (_installedModels.Contains("hf.co/bartowski/dolphin-2.9.2-qwen2-7b-GGUF:Q5_K_M"))
            list.Add(new() { Id="model.switch.dolphin7", Label="Use Dolphin 2.9 Qwen2 7B", Detail="Explicitly uncensored fine-tune, fast, good tool calling", Icon="🛡️", SortOrder=62, Keywords=["dolphin","uncensored","security","fast"] });
        if (_installedModels.Any(m => m.StartsWith("deepseek-coder-v2", StringComparison.OrdinalIgnoreCase)))
            list.Add(new() { Id="model.switch.deepseek", Label="Use DeepSeek-Coder V2 16B", Detail="Strong on security tooling and algorithms, 160k context", Icon="🛡️", SortOrder=63, Keywords=["deepseek","coder","security","code"] });
        // Always show "best security model" shortcut
        list.Add(new() { Id="model.switch.security", Label="Use Best Security Model",   Detail="Auto-selects highest-priority available security research model", Icon="🛡️", SortOrder=59, Keywords=["security","pentest","best","auto","unrestricted","hacking"] });

        return list;
    }

    // ── Activity bar ─────────────────────────────────────────────────────

    private void BtnExplorer_Click(object sender, RoutedEventArgs e) =>
        SidebarContent.Content = _explorerPanel;

    private void BtnAgent_Click(object sender, RoutedEventArgs e)
    {
        // Restore agent panel to the main content area in case the tool editor
        // was shown there (BtnTools_Click puts _toolEditorPanel into MainContent).
        MainContent.Content = _agentPanel;
        _sessionPanel.Refresh();
        SidebarContent.Content = _sessionPanel;
    }

    private void BtnCheckpoints_Click(object sender, RoutedEventArgs e)
    {
        _checkpointPanel.SetWorkspace(_session.WorkspaceRoot);
        SidebarContent.Content = _checkpointPanel;
    }

    private void BtnTools_Click(object sender, RoutedEventArgs e)
    {
        // Tool editor takes the full main content area (needs space for editor + diagnostics)
        MainContent.Content = _toolEditorPanel;
        SidebarContent.Content = _explorerPanel;  // keep explorer in sidebar
    }

    // ── Mode toggle ───────────────────────────────────────────────────────

    private void BtnModeSingle_Click(object sender, RoutedEventArgs e) => SetMode("single");
    private void BtnModeSwarm_Click(object sender, RoutedEventArgs e)  => SetMode("swarm");
    private void BtnModeChat_Click(object sender, RoutedEventArgs e)   => SetMode("chat");
    private void BtnModePit_Click(object sender, RoutedEventArgs e)    => SetMode("pit");
    private void BtnModeHive_Click(object sender, RoutedEventArgs e)  => SetMode("hive");

    /// <summary>
    /// Switches between Single Agent and Swarm modes.
    /// Each mode remembers its own last-used model independently.
    /// Swarm gate warning is shown inside the panel; we still navigate there.
    /// </summary>
    private void SetMode(string mode)
    {
        // ── Save current model to the bucket we're leaving ───────────────────
        if (_settings.LastMode == "single")
            _settings.LastSingleModel = _session.ActiveModel;
        else if (_settings.LastMode == "swarm")
            _settings.LastSwarmModel  = _session.ActiveModel;

        if (mode == "swarm")
        {
            // ── Restore the last Swarm model (default: first nemotron found) ─
            var swarmModel = BestSwarmModel(_settings.LastSwarmModel);
            if (swarmModel != _session.ActiveModel)
                ApplyModelSwitch(swarmModel, saveToSingleSlot: false);

            _swarmPanel.ActiveModel   = _session.ActiveModel;
            _swarmPanel.WorkspaceRoot = _session.WorkspaceRoot;
            _swarmPanel.LocalUrl      = _settings.OllamaHost;
            _swarmPanel.SetModels(_installedModels, _session.ActiveModel, _settings.LastWorkerModel, _settings.LastResearcherModel);
            _swarmPanel.SetHiveHosts(Services.Hive.HiveHosts.Load(_settings.OllamaHost));
            _swarmPanel.Refresh();

            MainContent.Content    = _swarmPanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "chat")
        {
            // ── Chat mode: populate model picker, show panel ───────────────────
            _chatPanel.OllamaClient = _ollama;
            _chatPanel.SetModels(_installedModels, _session.ActiveModel);

            MainContent.Content    = _chatPanel;
            SidebarContent.Content = _explorerPanel;  // explorer still useful for context
        }
        else if (mode == "hive")
        {
            _hivePanel.LocalUrl = _settings.OllamaHost;
            _hivePanel.Refresh();
            // Wire RPC apply once (guard against double-subscribe on re-entry).
            _hivePanel.OnApplyRpcWorkers -= OnApplyRpcWorkers;
            _hivePanel.OnApplyRpcWorkers += OnApplyRpcWorkers;
            MainContent.Content    = _hivePanel;
            SidebarContent.Content = _explorerPanel;
        }
        else if (mode == "pit")
        {
            // ── Training Pit: dataset dashboard, no model switch needed ────────
            _pitPanel.WorkspaceRoot = _session.WorkspaceRoot;
            _pitPanel.Refresh();

            MainContent.Content    = _pitPanel;
            SidebarContent.Content = _explorerPanel;
        }
        else
        {
            // ── Restore the last Single model ─────────────────────────────────
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

    /// <summary>
    /// Returns the best available model for Swarm mode.
    /// Priority: last-used swarm model → first installed nemotron → current model.
    /// </summary>
    private string BestSwarmModel(string lastSwarm)
    {
        if (!string.IsNullOrEmpty(lastSwarm) &&
            _installedModels.Contains(lastSwarm, StringComparer.OrdinalIgnoreCase))
            return lastSwarm;

        // Fall back to the first installed nemotron variant
        var nemotron = _installedModels
            .FirstOrDefault(m => m.Contains("nemotron", StringComparison.OrdinalIgnoreCase));
        return nemotron ?? _session.ActiveModel;
    }

    /// <summary>
    /// Returns the best available model for Single mode.
    /// Priority: last-used single model → quality-ordered preferred list → current model.
    /// </summary>
    private string BestSingleModel(string lastSingle)
    {
        if (!string.IsNullOrEmpty(lastSingle) &&
            _installedModels.Contains(lastSingle, StringComparer.OrdinalIgnoreCase))
            return lastSingle;

        // Quality-ordered fallback (non-swarm coding models first)
        var preferred = new[]
        {
            "qwen2.5-coder:14b", "qwen2.5-coder:7b", "gemma4:12b",
            "qwen2.5-coder:3b",  "gemma4:e4b",        "llama3.1:8b",
        };
        return preferred.FirstOrDefault(p =>
                   _installedModels.Contains(p, StringComparer.OrdinalIgnoreCase))
               ?? _session.ActiveModel;
    }

    /// <summary>
    /// Updates the title-bar mode pill to reflect the active mode.
    /// Active = NVIDIA green. Inactive = muted grey.
    /// </summary>
    private void UpdateModeToggle(string mode)
    {
        var activeGreen  = new SolidColorBrush(Color.FromRgb(0x1F, 0x3D, 0x00));
        var activeFg     = new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00));
        var inactiveBg   = new SolidColorBrush(Colors.Transparent);
        var inactiveFg   = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));

        BtnModeSingle.Background = mode == "single" ? activeGreen  : inactiveBg;
        BtnModeSingle.Foreground = mode == "single" ? activeFg     : inactiveFg;
        BtnModeSwarm.Background  = mode == "swarm"  ? activeGreen  : inactiveBg;
        BtnModeSwarm.Foreground  = mode == "swarm"  ? activeFg     : inactiveFg;
        BtnModeChat.Background   = mode == "chat"   ? activeGreen  : inactiveBg;
        BtnModeChat.Foreground   = mode == "chat"   ? activeFg     : inactiveFg;
        BtnModePit.Background    = mode == "pit"    ? activeGreen  : inactiveBg;
        BtnModePit.Foreground    = mode == "pit"    ? activeFg     : inactiveFg;
    }

    /// <summary>
    /// Called after startup model selection. Restores the last-used mode.
    /// Silently falls back to single if swarm gate is not satisfied.
    /// </summary>
    private void RestoreLastMode()
    {
        // Chat mode is not persisted — it opens fresh each session.
        // Swarm is restored only if its gate is satisfied; otherwise single.
        var mode = _settings.LastMode == "swarm" ? "swarm" : "single";
        SetMode(mode);
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        _settingsPanel.LoadSettings(_settings);   // Refresh before showing
        SidebarContent.Content = _settingsPanel;
    }

    /// <summary>
    /// Resumes a previously saved session — replaces the current session and
    /// updates the workspace, agent panel, and status bar.
    /// </summary>
    private void ResumeSession(ProjectSession session)
    {
        _session = session;
        _agentPanel.Loop    = _loop;
        _agentPanel.Session = _session;

        // Workspace is loaded but not re-confirmed — user must click to unlock Execute
        _agentPanel.SetWorkspace(_session.WorkspaceRoot, confirmed: false);
        _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
        _checkpointPanel.SetWorkspace(_session.WorkspaceRoot);
        UpdateStatusBar();

        // Switch back to main agent view
        SidebarContent.Content = _explorerPanel;

        AddActivity(new ActivityEvent(ActivityKind.Info, "Session",
            $"Resumed: {Path.GetFileName(_session.WorkspaceRoot.TrimEnd(Path.DirectorySeparatorChar))}", DateTime.Now));
    }

    /// <summary>
    /// Called whenever the user explicitly opens a folder — confirms the workspace
    /// and unlocks Execute mode. Also saves it as the last-used folder.
    /// </summary>
    /// <summary>
    /// Opens <paramref name="path"/> as the active workspace, usable from any thread
    /// (dispatches back to the UI thread internally).
    /// Called by SettingsPanel when the user clicks "Open in Agent".
    /// </summary>
    private Task OpenWorkspaceAsync(string path)
    {
        Dispatcher.Invoke(() => ConfirmWorkspace(path));
        return Task.CompletedTask;
    }

    private void ConfirmWorkspace(string path)
    {
        _session.WorkspaceRoot          = path;
        _session.IsWorkspaceConfirmed   = true;
        RegisterAllTools();
        _explorerPanel.LoadWorkspace(path);
        _agentPanel.SetWorkspace(path, confirmed: true);
        _checkpointPanel.SetWorkspace(path);   // keep checkpoint list in sync
        _toolEditorPanel.WorkspaceRoot  = path; // keep tool editor in sync
        _swarmPanel.WorkspaceRoot       = path; // dismiss workspace warning in swarm mode
        _swarmPanel.Refresh();
        UpdateStatusBar();

        // Auto-load any tools saved in .orc/tools/ for this workspace
        _ = AutoLoadWorkspaceToolsAsync(path);

        // Persist as default + add to recent workspaces list (max 10)
        _settings.DefaultWorkspace = path;
        _settings.RecentWorkspaces.RemoveAll(p =>
            p.Equals(path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentWorkspaces.Insert(0, path);
        if (_settings.RecentWorkspaces.Count > 10)
            _settings.RecentWorkspaces.RemoveRange(10, _settings.RecentWorkspaces.Count - 10);
        _settings.Save();

        // Rebuild the File → Open Recent submenu
        Dispatcher.InvokeAsync(RebuildRecentMenu);

        AddActivity(new ActivityEvent(ActivityKind.Info, "Workspace",
            $"Opened: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}", DateTime.Now));
    }

    /// <summary>
    /// Switch to a recent workspace — same as ConfirmWorkspace but with
    /// a friendly error if the folder has since been deleted.
    /// </summary>
    private void SwitchToRecentWorkspace(string path)
    {
        if (!Directory.Exists(path))
        {
            MessageBox.Show($"Folder not found:\n{path}\n\nIt will be removed from the recent list.",
                "Open Recent", MessageBoxButton.OK, MessageBoxImage.Warning);
            _settings.RecentWorkspaces.RemoveAll(p =>
                p.Equals(path, StringComparison.OrdinalIgnoreCase));
            _settings.Save();
            RebuildRecentMenu();
            return;
        }
        ConfirmWorkspace(path);
    }

    /// <summary>
    /// Rebuilds the File → Open Recent submenu from <see cref="AppSettings.RecentWorkspaces"/>.
    /// Must be called on the UI thread.
    /// </summary>
    private void RebuildRecentMenu()
    {
        MiOpenRecent.Items.Clear();
        var valid = _settings.RecentWorkspaces.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        MiOpenRecent.IsEnabled = valid.Count > 0;
        foreach (var p in valid)
        {
            var name = Path.GetFileName(p.TrimEnd(Path.DirectorySeparatorChar,
                                                   Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = p;
            var item = new MenuItem { Header = name, ToolTip = p };
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
            Dispatcher.Invoke(() => _toolEditorPanel.RefreshLoadedBadge(_toolCompiler.LoadedToolNames));
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        var oldBackend = _settings.Backend;
        _settings = newSettings;

        // ── Apply backend change ──────────────────────────────────────────
        if (newSettings.Backend != oldBackend ||
            (newSettings.Backend == InferenceBackend.LlamaCpp &&
             _llamaServer != null &&
             (_llamaServer.ModelPath    != newSettings.LlamaCppModelPath ||
              _llamaServer.RuntimePath  != newSettings.LlamaCppRuntimePath ||
              _llamaServer.Port         != newSettings.LlamaCppPort)))
        {
            // Stop the old server (if any) before switching
            _llamaServer?.Stop();
            _llamaServer = BuildServerManager(newSettings);

            // Point the inference client at the correct URL + backend
            _ollama.Host    = newSettings.InferenceBaseUrl;
            _ollama.Backend = newSettings.Backend;

            // Start new server in the background (don't block UI)
            if (newSettings.Backend == InferenceBackend.LlamaCpp && _llamaServer != null)
            {
                AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp",
                    "Restarting server with new settings…", DateTime.Now));
                _ = _llamaServer.StartAsync();
            }
        }
        else
        {
            // Ollama host may have changed — apply live
            _ollama.Host = newSettings.InferenceBaseUrl;
        }

        // Settings workspace change updates the default but does NOT confirm —
        // the user must still explicitly open the folder this session.
        if (!string.IsNullOrEmpty(newSettings.DefaultWorkspace))
        {
            _session.WorkspaceRoot = newSettings.DefaultWorkspace;
            RegisterAllTools();
            _explorerPanel.LoadWorkspace(newSettings.DefaultWorkspace);
            // Keep badge amber (unconfirmed) — don't call ConfirmWorkspace here
            _agentPanel.SetWorkspace(newSettings.DefaultWorkspace, confirmed: false);
        }

        UpdateStatusBar();
        var backendTag = newSettings.Backend == InferenceBackend.LlamaCpp
            ? $"llama.cpp → port {newSettings.LlamaCppPort}"
            : $"Ollama → {newSettings.OllamaHost}";
        AddActivity(new ActivityEvent(ActivityKind.Info, "Settings",
            $"Saved — {backendTag}", DateTime.Now));
    }

    // ── Backend helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Creates and wires a LlamaServerManager from settings.
    /// Returns null if the backend is Ollama (no server to manage).
    /// </summary>
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

        // Forward server logs to the activity panel
        mgr.OnLog += msg =>
            AddActivity(new ActivityEvent(ActivityKind.Info, "llama.cpp", msg, DateTime.Now));

        mgr.OnStatusChanged += running =>
            Dispatcher.InvokeAsync(() =>
                SetStatus(running ? "⚙ llama.cpp server running" : "⚠ llama.cpp server stopped"));

        return mgr;
    }

    // ── HIVE MIND C2: Apply RPC workers ──────────────────────────────────────

    /// <summary>
    /// Restarts llama-server with the supplied RPC endpoints so their GPUs contribute
    /// VRAM to this machine's inference. Only applies when Backend == LlamaCpp.
    /// </summary>
    private async void OnApplyRpcWorkers(IReadOnlyList<string> endpoints)
    {
        if (_settings.Backend != InferenceBackend.LlamaCpp || _llamaServer is null)
        {
            MessageBox.Show(
                "RPC VRAM chaining requires the llama.cpp backend.\n\n" +
                "Switch to llama.cpp in Settings → Backend, then try again.",
                "HIVE MIND — RPC", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddActivity(new ActivityEvent(ActivityKind.Info, "HIVE MIND",
            $"Applying RPC workers: {string.Join(", ", endpoints)}", DateTime.Now));

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
            AddActivity(new ActivityEvent(ActivityKind.Warning, "HIVE MIND",
                "RPC chain failed — check worker nodes are reachable and firewall allows port 50052.", DateTime.Now));
        }
    }

    private void AgentMode_Changed(object sender, RoutedEventArgs e) { }

    private void SbModel_Click(object sender, MouseButtonEventArgs e)
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

    /// <summary>
    /// Core model-switch logic shared by OnModelSelected and SetMode.
    /// Saves to the per-mode bucket (single or swarm) and the legacy DefaultModel field.
    /// </summary>
    private void ApplyModelSwitch(string modelId, bool saveToSingleSlot)
    {
        _session.ActiveModel    = modelId;
        _swarmPanel.ActiveModel = modelId;
        _swarmPanel.Refresh();
        _chatPanel.SetActiveModel(modelId);

        // Specialty/security models (hermes, heretic, uncensored) are session-only —
        // don't persist them to any saved-model slot so they never become the default.
        var isSpecialty = modelId.Contains("hermes",     StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("heretic",    StringComparison.OrdinalIgnoreCase)
                       || modelId.Contains("uncensored", StringComparison.OrdinalIgnoreCase);

        if (!isSpecialty)
        {
            if (saveToSingleSlot)
                _settings.LastSingleModel = modelId;
            else
                _settings.LastSwarmModel  = modelId;

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
        _editorVisible               = true;
        ColEditorSplitter.Width      = new GridLength(4);
        ColEditor.Width              = new GridLength(1, GridUnitType.Star);
        SplitterEditor.Visibility    = Visibility.Visible;
        EditorContent.Visibility     = Visibility.Visible;
    }

    private void HideEditorPane()
    {
        _editorVisible               = false;
        ColEditorSplitter.Width      = new GridLength(0);
        ColEditor.Width              = new GridLength(0);
        SplitterEditor.Visibility    = Visibility.Collapsed;
        EditorContent.Visibility     = Visibility.Collapsed;
    }

    private void ToggleEditorPane()
    {
        if (_editorVisible) HideEditorPane();
        else                ShowEditorPane();
    }

    // ── Menu handlers — File ──────────────────────────────────────────────

    private void Menu_OpenFolder(object sender, RoutedEventArgs e)
        => _explorerPanel.OpenFolderDialog();

    private void Menu_OpenExplorer(object sender, RoutedEventArgs e)
        => FileExplorerPanel.RevealInExplorer(_session.WorkspaceRoot);

    private void Menu_NewSession(object sender, RoutedEventArgs e)
    {
        _session = new ProjectSession
        {
            WorkspaceRoot = _session.WorkspaceRoot,
            ActiveModel   = _session.ActiveModel,
        };
        _agentPanel.Session = _session;
        AddActivity(new ActivityEvent(ActivityKind.Info, "Session", "New session started", DateTime.Now));
    }

    private void Menu_Exit(object sender, RoutedEventArgs e)
        => Close();

    // ── Menu handlers — Edit ──────────────────────────────────────────────

    private void Menu_CommandPalette(object sender, RoutedEventArgs e)
        => OpenCommandPalette();

    private void Menu_Settings(object sender, RoutedEventArgs e)
        => BtnSettings_Click(sender, e);

    // ── Menu handlers — View ──────────────────────────────────────────────

    private void Menu_ShowExplorer(object sender, RoutedEventArgs e)
        => SidebarContent.Content = _explorerPanel;

    private void Menu_ToggleEditor(object sender, RoutedEventArgs e)
        => ToggleEditorPane();

    private void Menu_EditRules(object sender, RoutedEventArgs e)
        => OpenRulesFile();

    /// <summary>
    /// Opens the workspace rules file (.agent.md etc.) in the code editor.
    /// If none exists, offers to create one from the default template.
    /// </summary>
    private void OpenRulesFile()
    {
        var path = _rules.FindRulesFile(_session.WorkspaceRoot);

        if (path == null)
        {
            // No rules file — offer to create .agent.md from default template
            var result = MessageBox.Show(
                $"No rules file found in:\n{_session.WorkspaceRoot}\n\n" +
                "Create a default .agent.md now?",
                "No Rules File Found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            path = Path.Combine(_session.WorkspaceRoot, ".agent.md");
            var projectName = Path.GetFileName(_session.WorkspaceRoot.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            File.WriteAllText(path, RulesLoader.DefaultTemplate(projectName));
        }

        ShowEditorPane();
        _editorPanel.OpenFile(path);
    }

    /// <summary>
    /// Returns the best installed model for security research work,
    /// using the priority order defined in ModelProfiles.SecurityPreference.
    /// Returns null if none of the preferred models are installed.
    /// </summary>
    private string? GetBestSecurityModel()
        => ModelProfiles.SecurityPreference
            .FirstOrDefault(p => _installedModels.Contains(p, StringComparer.OrdinalIgnoreCase));

    // ── Workspace Rules ───────────────────────────────────────────────────

    /// <summary>
    /// Opens the unified Agent Builder dialog targeting workspace rules.
    /// Supports AI-assisted generation, presets, and manual editing of .agent.md.
    /// </summary>
    private void OpenWorkspaceRules()
    {
        var dlg = new OrchestratorIDE.UI.Dialogs.AgentBuilderDialog(
            _ollama, _session.ActiveModel, _session.WorkspaceRoot);
        dlg.Owner = this;

        if (dlg.ShowDialog() == true)
        {
            HandleAgentBuilderResult(dlg.AppliedTarget);
        }
    }

    // ── Global Agent ──────────────────────────────────────────────────────

    /// <summary>
    /// Opens the unified Agent Builder dialog targeting the global agent.
    /// The workspace Apply button is still available so the user can apply to both.
    /// </summary>
    private void OpenGlobalAgentPicker()
    {
        var dlg = new OrchestratorIDE.UI.Dialogs.AgentBuilderDialog(
            _ollama, _session.ActiveModel, _session.WorkspaceRoot);
        dlg.Owner = this;

        if (dlg.ShowDialog() == true)
        {
            HandleAgentBuilderResult(dlg.AppliedTarget);
        }
    }

    /// <summary>
    /// Shared post-apply logic — refreshes rules, badge, and editor depending on what was written.
    /// </summary>
    private void HandleAgentBuilderResult(OrchestratorIDE.UI.Dialogs.AgentBuilderTarget target)
    {
        switch (target)
        {
            case OrchestratorIDE.UI.Dialogs.AgentBuilderTarget.WorkspaceRules:
                _ = _loop.RefreshRulesAsync(_session.WorkspaceRoot);
                _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);
                var rulesPath = _rules.FindRulesFile(_session.WorkspaceRoot);
                if (rulesPath != null)
                {
                    ShowEditorPane();
                    _editorPanel.OpenFile(rulesPath);
                }
                AddActivity(new ActivityEvent(ActivityKind.Info, "Workspace Rules",
                    $"Rules updated for {Path.GetFileName(_session.WorkspaceRoot)}", DateTime.Now));
                break;

            case OrchestratorIDE.UI.Dialogs.AgentBuilderTarget.GlobalAgent:
                RefreshGlobalAgentBadge();
                AddActivity(new ActivityEvent(ActivityKind.Info, "Global Agent",
                    "Global agent updated", DateTime.Now));
                break;
        }
    }

    private void RefreshGlobalAgentBadge()
    {
        var path = AgentPresets.GlobalAgentPath;
        if (!File.Exists(path))
        {
            _agentPanel.SetGlobalAgentLabel("No global agent");
            return;
        }

        // Try to match content to a known preset name
        var content = File.ReadAllText(path);
        var firstLine = content.TrimStart().Split('\n').FirstOrDefault() ?? "";
        var name = firstLine.TrimStart('#').Trim();
        _agentPanel.SetGlobalAgentLabel(string.IsNullOrEmpty(name) ? "Custom" : name);
    }

    // ── Kept for command-palette compat ───────────────────────────────────
    private Task EnablePentestModeAsync()
    {
        // Pentest is now a preset — route through workspace rules dialog
        OpenWorkspaceRules();
        return Task.CompletedTask;
    }

    private void Menu_WordWrap(object sender, RoutedEventArgs e)
        => _editorPanel.SetWordWrap(MiWordWrap.IsChecked);

    private void Menu_FontBigger(object sender, RoutedEventArgs e)
    {
        _editorFontSize = Math.Min(32, _editorFontSize + 1);
        _editorPanel.SetFontSize(_editorFontSize);
    }

    private void Menu_FontSmaller(object sender, RoutedEventArgs e)
    {
        _editorFontSize = Math.Max(8, _editorFontSize - 1);
        _editorPanel.SetFontSize(_editorFontSize);
    }

    private void Menu_FontReset(object sender, RoutedEventArgs e)
    {
        _editorFontSize = 13.0;
        _editorPanel.SetFontSize(_editorFontSize);
    }

    // ── Menu handlers — Agent ─────────────────────────────────────────────

    private void Menu_ModePlan(object sender, RoutedEventArgs e)
        => _agentPanel.SetMode(isPlan: true);

    private void Menu_ModeExecute(object sender, RoutedEventArgs e)
        => _agentPanel.SetMode(isPlan: false);

    private void Menu_ChangeModel(object sender, RoutedEventArgs e)
        => SbModel_Click(sender, null!);

    // ── Update badge ──────────────────────────────────────────────────────

    private void ShowUpdateBadge(UpdateChecker.UpdateResult result)
    {
        _pendingReleaseUrl    = result.ReleaseUrl;
        TbUpdateBadge.Text    = $"↑ v{result.LatestVersion} available";
        BdrUpdateBadge.Visibility = Visibility.Visible;

        // Also update the window title while we're here
        Title = $"Orchestrator IDE  v{result.CurrentVersion}";
    }

    private void BdrUpdateBadge_Click(object sender, MouseButtonEventArgs e)
        => UpdateChecker.OpenReleasePage(_pendingReleaseUrl);

    // ── Menu handlers — Help ──────────────────────────────────────────────

    private async void Menu_CheckUpdates(object sender, RoutedEventArgs e)
        => await Menu_CheckUpdatesAsync(force: true);

    /// <summary>
    /// Shared update-check logic used by both the menu item and
    /// the Settings panel "Check Now" button.
    /// </summary>
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

            var answer = MessageBox.Show(
                $"A new version of TheOrc is available!\n\n" +
                $"  Current:  v{result.CurrentVersion}\n" +
                $"  Latest:   v{result.LatestVersion} — {result.ReleaseName}\n\n" +
                "Open release page in browser?",
                "Update Available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (answer == MessageBoxResult.Yes)
                UpdateChecker.OpenReleasePage(result.ReleaseUrl);
        }
        else
        {
            BdrUpdateBadge.Visibility = Visibility.Collapsed;
            SetStatus($"You're up to date — v{result.CurrentVersion}");
            if (force) // only show the "up to date" dialog when the user explicitly clicked
            {
                MessageBox.Show(
                    $"TheOrc is up to date.\n\nVersion: v{result.CurrentVersion}",
                    "No Updates Available",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }
    }

    private void Menu_ReleaseNotes(object sender, RoutedEventArgs e)
        => UpdateChecker.OpenReleasePage(_pendingReleaseUrl);

    private void Menu_GitHub(object sender, RoutedEventArgs e)
        => UpdateChecker.OpenReleasePage("https://github.com/hardcoreerik/TheOrc");

    private void Menu_HelpTopics(object sender, RoutedEventArgs e)
        => UI.Windows.HelpWindow.ShowGuide(this);

    private void Menu_HelpDocumentation(object sender, RoutedEventArgs e)
        => UI.Windows.HelpWindow.ShowGuide(this, "USER_GUIDE.md");

    private void Menu_HelpTroubleshooting(object sender, RoutedEventArgs e)
        => UI.Windows.HelpWindow.ShowGuide(this, "TROUBLESHOOTING.md");

    private void Menu_HelpModelGuide(object sender, RoutedEventArgs e)
        => UI.Windows.HelpWindow.ShowGuide(this, "MODEL_GUIDE.md");

    private void Menu_HelpTrainingPitGuide(object sender, RoutedEventArgs e)
        => UI.Windows.HelpWindow.ShowGuide(this, "TRAINING_PIT_GUIDE.md");

    // ── Models menu ───────────────────────────────────────────────────────────

    private void Menu_ModelChoose(object sender, RoutedEventArgs e)
        => SbModel_Click(this, null!);   // reuse existing model picker

    private void Menu_ModelDownload(object sender, RoutedEventArgs e)
    {
        var win = new ModelDownloaderWindow(_settings) { Owner = this };
        win.ShowDialog();
        // Refresh installed models list and status bar after potential download
        _ = Task.Run(async () =>
        {
            var models = await _ollama.GetInstalledModelsAsync();
            Dispatcher.Invoke(() =>
            {
                _installedModels = models;
                _chatPanel.SetModels(models, _session.ActiveModel);
                UpdateStatusBar();
            });
        });
    }

    private async void Menu_WarmUp(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem mi) return;
        var role = mi.Tag?.ToString() ?? "worker";

        SetStatus($"Warming up {role} model…");
        using var svc = new ModelWarmUpService(_settings);
        try
        {
            await svc.WarmUpAsync(
                role,
                msg => Dispatcher.InvokeAsync(() => SetStatus(msg)),
                _ => { });
        }
        catch (Exception ex)
        {
            SetStatus($"Warm-up error: {ex.Message}");
        }
    }

    private void Menu_WarmUpEdit(object sender, RoutedEventArgs e)
    {
        var win = new WarmUpEditorWindow(_settings) { Owner = this };
        win.Show();
    }

    private void Menu_ModelLibrary(object sender, RoutedEventArgs e)
    {
        var win = new ModelLibraryWindow(_settings) { Owner = this };
        win.Show();
    }

    private void Menu_ModelWiki(object sender, RoutedEventArgs e)
    {
        // Single-instance: if a Model Wiki window is already open, activate it
        // rather than stacking another one. This prevents duplicate windows that
        // confuse both the user and the FlaUI test suite.
        foreach (Window w in System.Windows.Application.Current.Windows)
        {
            if (w is OrchestratorIDE.UI.Windows.ModelWikiWindow existing)
            {
                existing.Activate();
                if (existing.WindowState == WindowState.Minimized)
                    existing.WindowState = WindowState.Normal;
                return;
            }
        }

        var win = new OrchestratorIDE.UI.Windows.ModelWikiWindow(_settings) { Owner = this };
        win.Show();
    }

    private void Menu_RunModelCapabilityTest(object sender, RoutedEventArgs e)
    {
        var win = new OrchestratorIDE.UI.Windows.ModelCapabilityTestDialog(_settings) { Owner = this };
        win.ShowDialog();
    }

    private void Menu_RunToolProbe(object sender, RoutedEventArgs e)
    {
        var win = new OrchestratorIDE.Tests.ToolCallTestWindow(_settings)
        {
            Owner  = this,
            Ollama = _ollama,
        };
        win.Show();
    }

    // ── Model status bar click ─────────────────────────────────────────────────

    private void SbModelStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Menu_ModelDownload(this, null!);

    // ── Keyboard shortcuts (extend existing handler) ───────────────────────

    // stubs kept for XAML compat
    private void BtnSend_Click(object sender, RoutedEventArgs e) { }
    private void BtnStop_Click(object sender, RoutedEventArgs e) { }
}
