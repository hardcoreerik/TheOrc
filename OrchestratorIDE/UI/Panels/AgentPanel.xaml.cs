using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.UI.Panels;

public partial class AgentPanel : UserControl
{
    // Injected by MainWindow
    public AgentLoop?        Loop    { get; set; }
    public ProjectSession?   Session { get; set; }
    public Action<string>?   OnStatusChanged { get; set; }

    // Fires after every send/receive cycle — use to auto-save session
    public event Action? ConversationChanged;

    // Fires when user clicks the workspace badge — MainWindow opens folder picker
    public event Action? WorkspaceChangeRequested;

    // Fires when user clicks the rules badge — MainWindow opens the rules file in the editor
    public event Action? RulesEditRequested;

    // Fires when user clicks Workspace Rules — MainWindow opens workspace rules editor
    public event Action? WorkspaceRulesRequested;

    // Fires when user clicks Global Agent badge — MainWindow opens global agent picker
    public event Action? GlobalAgentRequested;

    // Fires as the user types in the input box — MainWindow refreshes the
    // token-cost estimate badge. Wired in the constructor.
    public event Action? InputTextChanged;

    /// <summary>Pending (unsent) input text, for token-cost estimation.</summary>
    public string PendingInputText => TbInput?.Text ?? "";

    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<MessageVm> _messages = [];

    // Current streaming assistant bubble
    private MessageVm? _streamingBubble;

    // Running session token total
    private int _sessionPromptTokens    = 0;
    private int _sessionCompleteTokens  = 0;

    public AgentPanel()
    {
        InitializeComponent();
        MsgList.ItemsSource = _messages;
        TbInput.TextChanged += (_, _) => InputTextChanged?.Invoke();

        // Startup greeting
        _messages.Add(new MessageVm
        {
            Role    = MessageRole.System,
            Content = "Orchestrator IDE ready.\n\n"
                    + "● Plan mode  — ask the agent to plan a task. Review before executing.\n"
                    + "▶ Execute mode — agent runs with tools, each file write shows a diff for approval.\n\n"
                    + "Tip: Start with Plan mode. Ctrl+K opens the command palette.",
            Status  = MessageStatus.Complete,
        });
    }

    // ── Public: set mode from outside (command palette) ──────────────────
    public void SetMode(bool isPlan)
    {
        if (isPlan) RbPlan.IsChecked = true;
        else        RbExec.IsChecked = true;
    }

    // ── Public: update token display ──────────────────────────────────────
    public void SetTokenDisplay(int used, int max)
    {
        Dispatcher.InvokeAsync(() =>
        {
            var pct = max > 0 ? (double)used / max : 0;
            TbTokens.Text = used > 0 ? $"{used:N0}/{max/1000}k" : "0 tokens";

            // Update mini progress bar (parent Border is 80px wide)
            CtxBar.Width = Math.Max(0, Math.Min(80, 80 * pct));
            CtxBar.Background = pct > 0.85
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0x47, 0x47))
                : pct > 0.70
                    ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xA7, 0x00))
                    : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x56, 0x9C, 0xD6));
        });
    }

    // ── Workspace badge ───────────────────────────────────────────────────

    /// <summary>
    /// Called by MainWindow whenever the workspace path changes (open folder,
    /// settings save, startup). Updates badge label + colour.
    /// confirmed = user explicitly opened this folder (not a loaded default).
    /// </summary>
    public void SetWorkspace(string path, bool confirmed)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Label = last folder segment (or drive root if at top)
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                      Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = path;   // e.g. "F:\"
            WsLabel.Text = name;

            // Depth check — how many segments from the drive root?
            var depth = path.TrimEnd(Path.DirectorySeparatorChar)
                            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                            .Length;

            if (!confirmed)
            {
                // Amber — loaded from settings, not explicitly opened
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10));
                WsBadge.ToolTip    = "No project folder open — click to choose one before executing";
            }
            else if (depth <= 1)
            {
                // Red — root or drive letter, very dangerous
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x10, 0x10));
                WsBadge.ToolTip    = "⚠ Root drive selected — click to choose a safer project folder";
            }
            else
            {
                // Teal — good, specific project folder
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x26));
                WsBadge.ToolTip    = $"Workspace: {path}\nClick to change";
            }
        });
    }

    private void WsBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => WorkspaceChangeRequested?.Invoke();

    // ── Rules badge ───────────────────────────────────────────────────────

    /// <summary>
    /// Called by MainWindow when AgentLoop.OnRulesLoaded fires.
    /// filePath = null means no rules file was found (badge hidden).
    /// filePath = path means a rules file is active (badge shown with filename).
    /// </summary>
    public void SetRulesStatus(string? filePath)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (string.IsNullOrEmpty(filePath))
            {
                RulesBadge.Visibility = Visibility.Collapsed;
            }
            else
            {
                RulesLabel.Text       = Path.GetFileName(filePath);
                RulesBadge.ToolTip    = $"Rules active: {filePath}\nClick to edit";
                RulesBadge.Visibility = Visibility.Visible;
            }
        });
    }

    private void RulesBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => RulesEditRequested?.Invoke();

    // ── Workspace Rules button ────────────────────────────────────────────

    private void WorkspaceRulesBtn_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => WorkspaceRulesRequested?.Invoke();

    // ── Global Agent badge ────────────────────────────────────────────────

    private void GlobalAgentBadge_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => GlobalAgentRequested?.Invoke();

    /// <summary>
    /// Called by MainWindow to update the Global Agent badge label (preset name).
    /// </summary>
    public void SetGlobalAgentLabel(string presetName)
    {
        Dispatcher.InvokeAsync(() =>
        {
            GlobalAgentLabel.Text = presetName;
        });
    }

    // ── Kept for backwards-compat (pentest is now a preset, not a button) ─
    public void SetPentestActive(bool active) { /* no-op — pentest is now a workspace preset */ }

    // ── AutoSend (file-based IPC — called from MainWindow.HandleFlaUICmd) ──
    /// <summary>
    /// Sets Execute mode, loads the prompt into TbInput, then fires BtnSend_Click
    /// directly — bypasses IValueProvider.SetValue which truncates at ~383 chars.
    /// Must be called on the UI thread (MainWindow dispatches before calling this).
    /// </summary>
    public void AutoSend(string prompt)
    {
        RbExec.IsChecked = true;
        TbInput.Text     = prompt;
        BtnSend_Click(this, new RoutedEventArgs());
    }

    /// <summary>
    /// Pre-fills the input box with <paramref name="prompt"/> without sending.
    /// The user can review/edit before pressing Send.
    /// Used by the Self-Improve scan so the agent doesn't auto-run.
    /// </summary>
    public void InjectUserMessage(string prompt)
    {
        TbInput.Text = prompt;
        TbInput.Focus();
        TbInput.CaretIndex = TbInput.Text.Length;
    }

    // ── Send ──────────────────────────────────────────────────────────────
    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        var prompt = TbInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || Loop == null || Session == null) return;

        // ── Guard: block Execute if no folder has been explicitly opened ──
        if (RbExec.IsChecked == true && Session.IsWorkspaceConfirmed != true)
        {
            _messages.Add(new MessageVm
            {
                Role    = MessageRole.System,
                Content = "⚠  No project folder open.\n\n"
                        + "The agent needs an explicit workspace before it can create or modify files.\n\n"
                        + "Click the  📁 folder badge  in the toolbar below, or use the "
                        + "File Explorer panel on the left to open a project folder.\n\n"
                        + "Once a folder is open the badge turns teal and Execute is unlocked.",
                Status  = MessageStatus.Error,
            });
            ScrollToBottom();
            return;
        }

        TbInput.Text      = "";
        BtnSend.IsEnabled = false;
        BtnStop.IsEnabled = true;

        // Add user bubble
        AddMessage(MessageRole.User, prompt);

        // Add assistant streaming bubble
        _streamingBubble = new MessageVm
        {
            Role    = MessageRole.Assistant,
            Content = "",
            Status  = MessageStatus.Streaming
        };
        _messages.Add(_streamingBubble);
        ScrollToBottom();

        _cts = new CancellationTokenSource();
        OnStatusChanged?.Invoke(RbPlan.IsChecked == true ? "Planning…" : "Running…");

        try
        {
            if (RbPlan.IsChecked == true)
            {
                await Loop.PlanAsync(Session, prompt, _cts.Token);
                // Content is already streamed token-by-token via OnToken → AppendStreamingToken
                _streamingBubble.Status = MessageStatus.Complete;

                // Prime the input with execute prompt
                RbExec.IsChecked = true;
                TbInput.Text     = "[Execute the above plan]";
                TbInput.Focus();
                TbInput.SelectAll();
            }
            else
            {
                await Loop.ExecuteAsync(Session, prompt, _cts.Token);
                // Content is already streamed token-by-token via OnToken → AppendStreamingToken
                _streamingBubble.Status = MessageStatus.Complete;
            }
        }
        catch (OperationCanceledException)
        {
            if (_streamingBubble != null)
            {
                _streamingBubble.Content += "\n\n[Stopped by user]";
                _streamingBubble.Status   = MessageStatus.Error;
            }
        }
        catch (Exception ex)
        {
            if (_streamingBubble != null)
            {
                _streamingBubble.Content = $"[Error] {ex.Message}";
                _streamingBubble.Status  = MessageStatus.Error;
            }
        }
        finally
        {
            _streamingBubble      = null;
            BtnSend.IsEnabled     = true;
            BtnStop.IsEnabled     = false;
            OnStatusChanged?.Invoke("Ready");
            ScrollToBottom();
            ConversationChanged?.Invoke();  // Trigger auto-save
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnStop.IsEnabled = false;
    }

    private void TbInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            BtnSend_Click(sender, new RoutedEventArgs());
        }
        // Shift+Enter = newline (AcceptsReturn handles it but we need to NOT intercept it)
        // So we only intercept plain Enter above. Shift+Enter falls through naturally.
    }

    private void TbInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TbPlaceholder != null)
            TbPlaceholder.Visibility = string.IsNullOrEmpty(TbInput.Text)
                ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Streaming token append (called from AgentLoop.OnToken) ──────────
    public void AppendStreamingToken(string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_streamingBubble != null)
            {
                _streamingBubble.Content += token;
                ScrollToBottom();
            }
        });
    }

    // ── Token usage (called from AgentLoop.OnUsage) ───────────────────────
    public void OnTokensUsed(int promptTokens, int completionTokens)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Update bubble badge
            if (_streamingBubble != null)
                _streamingBubble.CompletionTokens += completionTokens;

            // Accumulate session totals
            _sessionPromptTokens   += promptTokens;
            _sessionCompleteTokens += completionTokens;

            // Update toolbar display
            var total = _sessionPromptTokens + _sessionCompleteTokens;
            TbTokens.Text = $"↳ {_sessionCompleteTokens:N0} out  ·  {total:N0} total";
        });
    }

    // ── Diff approval flow ────────────────────────────────────────────────
    public void ShowDiff(string filePath, string oldText, string newText, string reason,
        Action onApproved, Action onRejected)
    {
        Dispatcher.Invoke(() =>
        {
            var viewer = new DiffViewer();
            viewer.Load(filePath, oldText, newText, reason);
            viewer.Approved += () => { HideDiff(); onApproved(); };
            viewer.Rejected += () => { HideDiff(); onRejected(); };

            DiffPanel.Child      = viewer;
            DiffPanel.Visibility = Visibility.Visible;
            ScrollToBottom();
        });
    }

    private void HideDiff()
    {
        DiffPanel.Child      = null;
        DiffPanel.Visibility = Visibility.Collapsed;
    }

    // ── Shell approval card ───────────────────────────────────────────────

    /// <summary>
    /// Shows the ShellApprovalCard in the diff panel slot for run_shell and
    /// other non-write_file tool calls. Returns true if approved, false if rejected.
    /// Replaces the old MessageBox.Show approval dialog.
    /// </summary>
    public Task<bool> ShowShellApproval(OrchestratorIDE.Models.ToolCall call)
    {
        var tcs = new TaskCompletionSource<bool>();

        Dispatcher.Invoke(() =>
        {
            var card = new OrchestratorIDE.UI.Controls.ShellApprovalCard(call);

            card.Resolved += approved =>
            {
                HideDiff();
                tcs.TrySetResult(approved);
            };

            DiffPanel.Child      = card;
            DiffPanel.Visibility = Visibility.Visible;
            ScrollToBottom();
        });

        return tcs.Task;
    }

    // ── Unknown tool card (Layer 2) ───────────────────────────────────────

    /// <summary>
    /// Shows the UnknownToolCard in the diff panel slot and returns a Task that
    /// resolves with the result string once the user makes a choice.
    /// Called from MainWindow which wires it to ToolRegistry.OnUnknownTool.
    /// </summary>
    public Task<string> ShowUnknownToolCard(
        OrchestratorIDE.Models.ToolCall call,
        IEnumerable<string> registeredTools)
    {
        var tcs = new TaskCompletionSource<string>();

        Dispatcher.Invoke(() =>
        {
            var card = new OrchestratorIDE.UI.Controls.UnknownToolCard(call, registeredTools);

            card.Resolved += result =>
            {
                HideDiff();
                tcs.TrySetResult(result);
            };

            DiffPanel.Child      = card;
            DiffPanel.Visibility = Visibility.Visible;
            ScrollToBottom();
        });

        return tcs.Task;
    }

    // ── Chat right-click handlers ─────────────────────────────────────────

    private void MsgCopyMessage_Click(object sender, RoutedEventArgs e)
    {
        // Walk up to find the DataContext (MessageVm)
        var el  = (sender as System.Windows.FrameworkElement)?.DataContext
               ?? (e.OriginalSource as System.Windows.FrameworkElement)?.DataContext;
        if (el is MessageVm vm)
            System.Windows.Clipboard.SetText($"[{vm.RoleLabel}]\n{vm.Content}");
    }

    private void MsgCopyAll_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var vm in _messages)
        {
            sb.AppendLine($"[{vm.RoleLabel}]");
            sb.AppendLine(vm.Content);
            sb.AppendLine();
        }
        System.Windows.Clipboard.SetText(sb.ToString());
    }

    private void MsgSaveChat_Click(object sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Chat Log — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine();
        foreach (var vm in _messages)
        {
            sb.AppendLine($"## {vm.RoleLabel}");
            sb.AppendLine(vm.Content);
            sb.AppendLine();
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title      = "Save Chat Log",
            FileName   = $"TheOrc_Chat_{DateTime.Now:yyyyMMdd_HHmmss}",
            DefaultExt = ".md",
            Filter     = "Markdown (*.md)|*.md|Text (*.txt)|*.txt|All files|*.*",
        };
        if (dlg.ShowDialog() == true)
            File.WriteAllText(dlg.FileName, sb.ToString(), System.Text.Encoding.UTF8);
    }

    // ── Helpers ───────────────────────────────────────────────────────────
    private void AddMessage(MessageRole role, string content)
    {
        _messages.Add(new MessageVm { Role = role, Content = content });
        ScrollToBottom();
    }

    private void ScrollToBottom() =>
        Dispatcher.InvokeAsync(() => MsgScroll.ScrollToBottom());

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (TbMode == null) return;
        TbMode.Text       = RbPlan.IsChecked == true ? "● PLAN" : "▶ EXECUTE";
        TbMode.Foreground = RbPlan.IsChecked == true
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    }
}

// ── Message view model ────────────────────────────────────────────────────

public class MessageVm : System.ComponentModel.INotifyPropertyChanged
{
    private string _content = "";
    private MessageStatus _status = MessageStatus.Pending;
    private int _completionTokens = 0;

    public MessageRole   Role    { get; init; }
    public MessageStatus Status
    {
        get => _status;
        set { _status = value; OnPropChanged(nameof(Status)); }
    }
    public string Content
    {
        get => _content;
        set { _content = value; OnPropChanged(nameof(Content)); }
    }

    public int CompletionTokens
    {
        get => _completionTokens;
        set
        {
            _completionTokens = value;
            OnPropChanged(nameof(CompletionTokens));
            OnPropChanged(nameof(TokenLabel));
            OnPropChanged(nameof(HasTokenLabel));
        }
    }
    public string TokenLabel  => _completionTokens > 0 ? $"↳ {_completionTokens:N0} tokens" : "";
    public Visibility HasTokenLabel => _completionTokens > 0 && Role == MessageRole.Assistant
        ? Visibility.Visible : Visibility.Collapsed;

    public string RoleLabel => Role switch
    {
        MessageRole.User      => "YOU",
        MessageRole.Assistant => "AGENT",
        MessageRole.System    => "SYSTEM",
        MessageRole.Tool      => "TOOL",
        _ => "?"
    };
    /// <summary>
    /// Segoe MDL2 Assets glyphs — readable on dark background, Windows-native.
    ///   E77B = Contact/Person,  E8D4 = Robot,  E7EF = Code/Terminal,  E7BA = System/Info
    /// </summary>
    public string RoleIcon => Role switch
    {
        MessageRole.User      => "",   // Contact
        MessageRole.Assistant => "",   // Robot
        MessageRole.Tool      => "",   // Code
        _                     => ""    // Info
    };
    public Brush RoleColor => Role switch
    {
        MessageRole.User      => new SolidColorBrush(Color.FromRgb(0x70, 0xB0, 0xE8)),  // brighter blue
        MessageRole.Assistant => new SolidColorBrush(Color.FromRgb(0x60, 0xD9, 0xC0)),  // brighter teal
        MessageRole.Tool      => new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0x80)),  // bright yellow
        _                     => new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0))   // mid-grey
    };
    public Brush BubbleBg => Role == MessageRole.User
        ? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
        : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public List<string> ToolBadges { get; set; } = [];
    public Visibility HasTools => ToolBadges.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged(string name) =>
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}
