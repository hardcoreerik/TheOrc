// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Panels;

public partial class AgentPanel : UserControl
{
    // ── Injected by MainWindow ────────────────────────────────────────────────
    public AgentLoop?      Loop    { get; set; }
    public ProjectSession? Session { get; set; }
    public Action<string>? OnStatusChanged { get; set; }

    public event Action? ConversationChanged;
    public event Action? WorkspaceChangeRequested;
    public event Action? RulesEditRequested;
    public event Action? WorkspaceRulesRequested;
    public event Action? GlobalAgentRequested;
    public event Action? InputTextChanged;

    public string PendingInputText => TbInput?.Text ?? "";

    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<MessageVm> _messages = [];
    private MessageVm? _streamingBubble;
    private int _sessionPromptTokens   = 0;
    private int _sessionCompleteTokens = 0;

    public AgentPanel()
    {
        InitializeComponent();
        MsgList.ItemsSource = _messages;
        TbInput.TextChanged += (_, _) => InputTextChanged?.Invoke();

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

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetMode(bool isPlan)
    {
        if (isPlan) RbPlan.IsChecked = true;
        else        RbExec.IsChecked = true;
    }

    public void SetTokenDisplay(int used, int max)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var pct = max > 0 ? (double)used / max : 0;
            TbTokens.Text = used > 0 ? $"{used:N0}/{max / 1000}k" : "0 tokens";
            CtxBar.Width  = Math.Max(0, Math.Min(80, 80 * pct));
            CtxBar.Background = pct > 0.85
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47))
                : pct > 0.70
                    ? new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00))
                    : new SolidColorBrush(Color.FromRgb(0x56, 0x9C, 0xD6));
        });
    }

    // ── Workspace badge ───────────────────────────────────────────────────────

    public void SetWorkspace(string path, bool confirmed)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar,
                                                      Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name)) name = path;
            WsLabel.Text = name;

            var depth = path.TrimEnd(Path.DirectorySeparatorChar)
                            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries)
                            .Length;

            if (!confirmed)
            {
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x30, 0x10));
                ToolTip.SetTip(WsBadge, "No project folder open — click to choose one before executing");
            }
            else if (depth <= 1)
            {
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x10, 0x10));
                ToolTip.SetTip(WsBadge, "⚠ Root drive selected — click to choose a safer project folder");
            }
            else
            {
                WsLabel.Foreground = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
                WsBadge.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x2A, 0x26));
                ToolTip.SetTip(WsBadge, $"Workspace: {path}\nClick to change");
            }
        });
    }

    private void WsBadge_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        WorkspaceChangeRequested?.Invoke();
    }

    // ── Rules badge ───────────────────────────────────────────────────────────

    public void SetRulesStatus(string? filePath)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (string.IsNullOrEmpty(filePath))
            {
                RulesBadge.IsVisible = false;
            }
            else
            {
                RulesLabel.Text      = Path.GetFileName(filePath);
                ToolTip.SetTip(RulesBadge, $"Rules active: {filePath}\nClick to edit");
                RulesBadge.IsVisible = true;
            }
        });
    }

    private void RulesBadge_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        RulesEditRequested?.Invoke();
    }

    private void WorkspaceRulesBtn_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        WorkspaceRulesRequested?.Invoke();
    }

    private void GlobalAgentBadge_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        GlobalAgentRequested?.Invoke();
    }

    public void SetGlobalAgentLabel(string presetName)
        => Dispatcher.UIThread.InvokeAsync(() => GlobalAgentLabel.Text = presetName);

    public void SetPentestActive(bool active) { /* no-op — pentest is now a workspace preset */ }

    // ── AutoSend / InjectUserMessage ──────────────────────────────────────────

    public void AutoSend(string prompt)
    {
        RbExec.IsChecked = true;
        TbInput.Text     = prompt;
        BtnSend_Click(this, new RoutedEventArgs());
    }

    public void InjectUserMessage(string prompt)
    {
        TbInput.Text       = prompt;
        TbInput.Focus();
        TbInput.CaretIndex = TbInput.Text.Length;
    }

    // ── Send ──────────────────────────────────────────────────────────────────

    private async void BtnSend_Click(object? sender, RoutedEventArgs e)
    {
        var prompt = TbInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(prompt) || Loop == null || Session == null) return;

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

        AddMessage(MessageRole.User, prompt);

        _streamingBubble = new MessageVm
        {
            Role   = MessageRole.Assistant,
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
                _streamingBubble.Status = MessageStatus.Complete;

                RbExec.IsChecked = true;
                TbInput.Text     = "[Execute the above plan]";
                TbInput.Focus();
                TbInput.SelectAll();
            }
            else
            {
                await Loop.ExecuteAsync(Session, prompt, _cts.Token);
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
            ConversationChanged?.Invoke();
        }
    }

    private void BtnStop_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        BtnStop.IsEnabled = false;
    }

    private void TbInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            BtnSend_Click(sender, new RoutedEventArgs());
        }
    }

    private void TbInput_TextChanged(object? sender, TextChangedEventArgs e) { /* Watermark handles placeholder */ }

    // ── Streaming ─────────────────────────────────────────────────────────────

    public void AppendStreamingToken(string token)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamingBubble != null)
            {
                _streamingBubble.Content += token;
                ScrollToBottom();
            }
        });
    }

    public void OnTokensUsed(int promptTokens, int completionTokens)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamingBubble != null)
                _streamingBubble.CompletionTokens += completionTokens;

            _sessionPromptTokens   += promptTokens;
            _sessionCompleteTokens += completionTokens;

            var total = _sessionPromptTokens + _sessionCompleteTokens;
            TbTokens.Text = $"↳ {_sessionCompleteTokens:N0} out  ·  {total:N0} total";
        });
    }

    // ── Diff / approval slots (Phase 4 — stubs until controls are ported) ─────

    public void ShowDiff(string filePath, string oldText, string newText, string reason,
        Action onApproved, Action onRejected)
    {
        // TODO Phase 4: port DiffViewer to Avalonia. Auto-reject for safety until then.
        onRejected();
    }

    public Task<bool> ShowShellApproval(OrchestratorIDE.Models.ToolCall call)
    {
        // TODO Phase 4: port ShellApprovalCard to Avalonia.
        return Task.FromResult(false);
    }

    public Task<string> ShowUnknownToolCard(
        OrchestratorIDE.Models.ToolCall call,
        IEnumerable<string> registeredTools)
    {
        // TODO Phase 4: port UnknownToolCard to Avalonia.
        return Task.FromResult("");
    }

    // ── Context menu handlers ─────────────────────────────────────────────────

    private async void MsgCopyMessage_Click(object? sender, RoutedEventArgs e)
    {
        var vm = (sender as Control)?.DataContext as MessageVm;
        if (vm is null) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            await cb.SetTextAsync($"[{vm.RoleLabel}]\n{vm.Content}");
    }

    private async void MsgCopyAll_Click(object? sender, RoutedEventArgs e)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var vm in _messages)
        {
            sb.AppendLine($"[{vm.RoleLabel}]");
            sb.AppendLine(vm.Content);
            sb.AppendLine();
        }
        if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            await cb.SetTextAsync(sb.ToString());
    }

    private async void MsgSaveChat_Click(object? sender, RoutedEventArgs e)
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

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Chat Log",
            SuggestedFileName = $"TheOrc_Chat_{DateTime.Now:yyyyMMdd_HHmmss}",
            FileTypeChoices   =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Text")     { Patterns = ["*.txt"] },
            ],
        });

        if (file != null)
        {
            await using var stream = await file.OpenWriteAsync();
            var bytes = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
            await stream.WriteAsync(bytes);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void AddMessage(MessageRole role, string content)
    {
        _messages.Add(new MessageVm { Role = role, Content = content });
        ScrollToBottom();
    }

    private void ScrollToBottom()
        => Dispatcher.UIThread.InvokeAsync(() => MsgScroll.ScrollToEnd());

    private void ModeChanged(object? sender, RoutedEventArgs e)
    {
        if (TbMode == null) return;
        TbMode.Text       = RbPlan.IsChecked == true ? "● PLAN" : "▶ EXECUTE";
        TbMode.Foreground = RbPlan.IsChecked == true
            ? new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0));
    }
}

// ── Message view model ────────────────────────────────────────────────────────

public class MessageVm : INotifyPropertyChanged
{
    private string _content = "";
    private MessageStatus _status = MessageStatus.Pending;
    private int _completionTokens = 0;

    public MessageRole   Role   { get; init; }
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

    public string TokenLabel     => _completionTokens > 0 ? $"↳ {_completionTokens:N0} tokens" : "";
    public bool   HasTokenLabel  => _completionTokens > 0 && Role == MessageRole.Assistant;

    public string RoleLabel => Role switch
    {
        MessageRole.User      => "YOU",
        MessageRole.Assistant => "AGENT",
        MessageRole.System    => "SYSTEM",
        MessageRole.Tool      => "TOOL",
        _                     => "?"
    };
    public string RoleIcon => Role switch
    {
        MessageRole.User      => "👤",
        MessageRole.Assistant => "🤖",
        MessageRole.Tool      => "🔧",
        _                     => "ℹ"
    };
    public IBrush RoleColor => Role switch
    {
        MessageRole.User      => new SolidColorBrush(Color.FromRgb(0x70, 0xB0, 0xE8)),
        MessageRole.Assistant => new SolidColorBrush(Color.FromRgb(0x60, 0xD9, 0xC0)),
        MessageRole.Tool      => new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0x80)),
        _                     => new SolidColorBrush(Color.FromRgb(0xA0, 0xA0, 0xA0))
    };
    public IBrush BubbleBg => Role == MessageRole.User
        ? new SolidColorBrush(Color.FromRgb(0x25, 0x25, 0x26))
        : new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));

    public List<string> ToolBadges { get; set; } = [];
    public bool HasTools => ToolBadges.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
