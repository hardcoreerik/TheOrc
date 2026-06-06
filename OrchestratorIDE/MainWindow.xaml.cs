using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.Tools;
using OrchestratorIDE.Trust;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE;

public partial class MainWindow : Window
{
    // ── Services ──────────────────────────────────────────────────────────
    private readonly OllamaClient    _ollama;
    private readonly ApprovalQueue   _approvals;
    private readonly ToolRegistry    _registry;
    private readonly ContextManager  _context;
    private readonly GitCheckpoint   _git;
    private readonly RulesLoader     _rules;
    private readonly AgentLoop       _loop;
    private readonly SessionStore    _store;

    // ── State ─────────────────────────────────────────────────────────────
    private ProjectSession           _session;
    private AppSettings              _settings = AppSettings.Load();
    private List<string>             _installedModels = [];
    private readonly ObservableCollection<ActivityEvent> _activityItems = [];

    // ── Panels ────────────────────────────────────────────────────────────
    private readonly FileExplorerPanel       _explorerPanel;
    private readonly AgentPanel              _agentPanel;
    private readonly SettingsPanel           _settingsPanel;
    private readonly CodeEditorPanel         _editorPanel;
    private readonly CheckpointBrowserPanel  _checkpointPanel;
    private readonly SessionBrowserPanel     _sessionPanel;

    // Editor font size (shared across sessions, adjustable via View menu)
    private double _editorFontSize = 13.0;

    public MainWindow()
    {
        InitializeComponent();

        // Boot services — use saved Ollama host immediately
        _ollama    = new OllamaClient(_settings.OllamaHost);
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
            _editorPanel.OpenFile(path);
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
        _loop.Activity += ev => Dispatcher.InvokeAsync(() =>
        {
            _activityItems.Add(ev);
            if (_activityItems.Count > 500) _activityItems.RemoveAt(0);
            ActivityScroll.ScrollToBottom();
        });

        // Context meter
        _context.UsageChanged += () => Dispatcher.Invoke(UpdateContextDisplay);

        // Approval gate — use diff viewer in AgentPanel for write_file, dialog for shell
        _approvals.ApprovalRequested += OnApprovalRequested;

        // Layer 2: unknown tool card — shown in the diff panel slot
        _registry.OnUnknownTool = async call =>
        {
            var names = _registry.GetRegisteredNames();
            return await _agentPanel.ShowUnknownToolCard(call, names);
        };

        // Rules badge: update when agent loads (or fails to find) .agent.md
        _loop.OnRulesLoaded += filePath => _agentPanel.SetRulesStatus(filePath);

        // Rules badge click → open the rules file in the code editor
        _agentPanel.RulesEditRequested += OpenRulesFile;

        // Build settings panel
        _settingsPanel = new SettingsPanel(_ollama);
        _settingsPanel.LoadSettings(_settings);
        _settingsPanel.SettingsSaved += OnSettingsSaved;

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

        // Default sidebar = explorer
        SidebarContent.Content = _explorerPanel;
        _explorerPanel.LoadWorkspace(_session.WorkspaceRoot);

        // Main area = agent panel
        MainContent.Content = _agentPanel;

        // Show unconfirmed badge on startup (default workspace, not explicitly opened)
        _agentPanel.SetWorkspace(_session.WorkspaceRoot, confirmed: false);

        UpdateStatusBar();
        Loaded += async (_, _) => await OnLoadedAsync();
    }

    // ── Startup ───────────────────────────────────────────────────────────

    private async Task OnLoadedAsync()
    {
        AddActivity(new ActivityEvent(ActivityKind.Info, "Startup", "Checking Ollama connection…", DateTime.Now));
        var models = await _ollama.GetInstalledModelsAsync();

        if (models.Count > 0)
        {
            AddActivity(new ActivityEvent(ActivityKind.Info, "Ollama",
                $"{models.Count} models: {string.Join(", ", models.Take(3))}…", DateTime.Now));

            // Auto-select best available coding model (always on fresh start)
            _installedModels = models;
            var preferred = new[] {
                "qwen2.5-coder:14b",
                "qwen2.5-coder:7b",
                "hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M",
                "hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M",
                "qwen2.5:14b-instruct",
                "gemma4:e4b",
                "llama3.1:8b",
            };
            var best = preferred.FirstOrDefault(p => models.Contains(p, StringComparer.OrdinalIgnoreCase))
                    ?? models.First();
            _session.ActiveModel = best;
            AddActivity(new ActivityEvent(ActivityKind.Info, "Model",
                $"Active: {best}", DateTime.Now));
            Dispatcher.Invoke(UpdateStatusBar);
        }
        else
        {
            AddActivity(new ActivityEvent(ActivityKind.Warning, "Ollama",
                "No models found — check connection to Ollama host", DateTime.Now));
        }

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
    }

    // ── Tool registration ─────────────────────────────────────────────────

    private void RegisterAllTools()
    {
        var ws = _session.WorkspaceRoot;

        // Pass diff preview hook so write_file shows the DiffViewer
        FileTools.Register(_registry, ws, onDiffPreview: (path, diff, reason) =>
        {
            // DiffViewer is shown inside AgentPanel — but approval gate handles it
            // Store on the pending ToolCall for the approval dialog to use
        });
        ShellTools.Register(_registry, ws);
        SearchTools.Register(_registry, ws);
        TestTools.Register(_registry, ws);
        WebTools.Register(_registry);
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

    // ── Activity log helpers ──────────────────────────────────────────────

    private void AddActivity(ActivityEvent ev) => Dispatcher.InvokeAsync(() =>
    {
        _activityItems.Add(ev);
        if (_activityItems.Count > 500) _activityItems.RemoveAt(0);
        ActivityScroll.ScrollToBottom();
    });

    // ── Status + context ──────────────────────────────────────────────────

    private void UpdateStatusBar()
    {
        var profile = ModelProfiles.Get(_session.ActiveModel);
        SbModel.Text      = $"⚙ {profile.Name} · {profile.ToolSet.ToString().ToLower()}";
        SbWorkspace.Text  = $"📁 {Path.GetFileName(_session.WorkspaceRoot)}";
        TbModelBadge.Text = $"{_session.ActiveModel} · {profile.ToolSet.ToString().ToLower()}";

        // Update git branch (async, non-blocking)
        Task.Run(async () =>
        {
            var branch = await _git.GetBranchAsync(_session.WorkspaceRoot);
            Dispatcher.Invoke(() =>
                SbBranch.Text = branch != null ? $"⬡ {branch}" : "");
        });
    }

    private void UpdateContextDisplay()
    {
        TbContextPct.Text = $"{_context.UsagePercent:F0}% ctx";
        TbContextPct.Foreground = _context.IsCritical
            ? (Brush)FindResource("Br.Error")
            : _context.IsWarning
                ? (Brush)FindResource("Br.Warning")
                : (Brush)FindResource("Br.Text.Muted");
        _agentPanel.SetTokenDisplay(_context.UsedTokens, _context.MaxTokens);
    }

    private void SetStatus(string msg) => SbStatus.Text = msg;

    // ── Keyboard shortcuts ────────────────────────────────────────────────

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
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
            case "show.settings":     BtnSettings_Click(this, null!); break;
            case "session.new":
                _session = new ProjectSession { WorkspaceRoot = _session.WorkspaceRoot, ActiveModel = _session.ActiveModel };
                _agentPanel.Session = _session;
                AddActivity(new ActivityEvent(ActivityKind.Info, "Session", "New session started", DateTime.Now));
                break;
            case "model.switch.coder14":  OnModelSelected("qwen2.5-coder:14b"); break;
            case "model.switch.coder7":   OnModelSelected("qwen2.5-coder:7b"); break;
            case "model.switch.gemma":    OnModelSelected("gemma4:e4b"); break;
            case "model.switch.llama":    OnModelSelected("llama3.1:8b"); break;
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
            new() { Id="show.settings",   Label="Settings",                 Detail="Configure Ollama host, model, workspace", Icon="⚙", Shortcut="", SortOrder=38, Keywords=["settings","config","ollama","host"] },
            new() { Id="session.new",     Label="New Session",              Detail="Clear conversation history and start fresh", Icon="⬡", Shortcut="", SortOrder=40, Keywords=["new","clear","session","reset"] },
        };

        // Add quick model switch commands for installed models
        if (_installedModels.Contains("qwen2.5-coder:14b"))
            list.Add(new() { Id="model.switch.coder14", Label="Use Qwen2.5-Coder 14B", Detail="Best speed/quality balance for coding tasks", Icon="⚡", SortOrder=50, Keywords=["qwen","coder","14b"] });
        if (_installedModels.Contains("qwen2.5-coder:7b"))
            list.Add(new() { Id="model.switch.coder7",  Label="Use Qwen2.5-Coder 7B",  Detail="Faster coder, good for quick edits", Icon="⚡", SortOrder=51, Keywords=["qwen","coder","7b","fast"] });
        if (_installedModels.Contains("gemma4:e4b"))
            list.Add(new() { Id="model.switch.gemma",   Label="Use Gemma 4 (E4B)",      Detail="Very fast, 32k context", Icon="⚡", SortOrder=52, Keywords=["gemma","fast"] });
        if (_installedModels.Contains("llama3.1:8b"))
            list.Add(new() { Id="model.switch.llama",   Label="Use Llama 3.1 8B",       Detail="Fast general chat model", Icon="⚡", SortOrder=53, Keywords=["llama","fast","chat"] });

        return list;
    }

    // ── Activity bar ─────────────────────────────────────────────────────

    private void BtnExplorer_Click(object sender, RoutedEventArgs e) =>
        SidebarContent.Content = _explorerPanel;

    private void BtnAgent_Click(object sender, RoutedEventArgs e)
    {
        _sessionPanel.Refresh();
        SidebarContent.Content = _sessionPanel;
    }

    private void BtnCheckpoints_Click(object sender, RoutedEventArgs e)
    {
        _checkpointPanel.SetWorkspace(_session.WorkspaceRoot);
        SidebarContent.Content = _checkpointPanel;
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
    private void ConfirmWorkspace(string path)
    {
        _session.WorkspaceRoot          = path;
        _session.IsWorkspaceConfirmed   = true;
        RegisterAllTools();
        _explorerPanel.LoadWorkspace(path);
        _agentPanel.SetWorkspace(path, confirmed: true);
        _checkpointPanel.SetWorkspace(path);   // keep checkpoint list in sync
        UpdateStatusBar();

        // Persist as last-used workspace so next session pre-loads it
        _settings.DefaultWorkspace = path;
        _settings.Save();

        AddActivity(new ActivityEvent(ActivityKind.Info, "Workspace",
            $"Opened: {Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar))}", DateTime.Now));
    }

    private void OnSettingsSaved(AppSettings newSettings)
    {
        _settings = newSettings;

        // Apply Ollama host live
        _ollama.Host = newSettings.OllamaHost;

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
        AddActivity(new ActivityEvent(ActivityKind.Info, "Settings",
            $"Saved — Ollama: {newSettings.OllamaHost}", DateTime.Now));
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
        _session.ActiveModel = modelId;
        RegisterAllTools();   // Re-register with new toolset
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

    // ── Keyboard shortcuts (extend existing handler) ───────────────────────

    // stubs kept for XAML compat
    private void BtnSend_Click(object sender, RoutedEventArgs e) { }
    private void BtnStop_Click(object sender, RoutedEventArgs e) { }
    private void TbInput_KeyDown(object sender, KeyEventArgs e) { }
}
