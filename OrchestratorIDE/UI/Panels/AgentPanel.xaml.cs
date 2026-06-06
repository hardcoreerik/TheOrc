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

    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<MessageVm> _messages = [];

    // Current streaming assistant bubble
    private MessageVm? _streamingBubble;

    public AgentPanel()
    {
        InitializeComponent();
        MsgList.ItemsSource = _messages;

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

    // ── Send ──────────────────────────────────────────────────────────────
    private async void BtnSend_Click(object sender, RoutedEventArgs e)
    {
        var prompt = TbInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || Loop == null || Session == null) return;

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

    // ── Streaming token append (called from AgentLoop activity events) ────
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

    public string RoleLabel => Role switch
    {
        MessageRole.User      => "YOU",
        MessageRole.Assistant => "AGENT",
        MessageRole.System    => "SYSTEM",
        MessageRole.Tool      => "TOOL",
        _ => "?"
    };
    public string RoleIcon => Role switch
    {
        MessageRole.User      => "👤",
        MessageRole.Assistant => "⚡",
        MessageRole.Tool      => "⚙",
        _ => "·"
    };
    public Brush RoleColor => Role switch
    {
        MessageRole.User      => new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6)),
        MessageRole.Assistant => new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0)),
        MessageRole.Tool      => new SolidColorBrush(Color.FromRgb(0xDC, 0xDC, 0xAA)),
        _ => Brushes.Gray
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
