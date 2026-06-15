// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using OrchestratorIDE.Core;
using OrchestratorIDE.Research;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Research-focused chat panel — "Just Chat" mode.
///
/// Features:
///  • Model picker dropdown (all installed models)
///  • Web search + page fetch tool loop (native or ReAct per model capability)
///  • Markdown-rendered assistant responses with clickable hyperlinks
///  • Tool call chips showing which tools ran
///  • Save conversation to markdown
/// </summary>
public partial class ChatPanel : UserControl
{
    // ── Dependencies (set by MainWindow) ──────────────────────────────────────
    public OllamaClient? OllamaClient { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────
    private ChatEngine?          _engine;
    private CancellationTokenSource? _cts;
    private TextBox?             _streamBox;   // current streaming textbox
    private bool                 _isSending;

    // ── Construction ──────────────────────────────────────────────────────────

    public ChatPanel()
    {
        InitializeComponent();
    }

    // ── Public API (called by MainWindow) ─────────────────────────────────────

    /// <summary>Populate the model dropdown. Call this when installed models change.</summary>
    public void SetModels(IReadOnlyList<string> models, string activeModel)
    {
        CbModel.Items.Clear();
        foreach (var m in models)
            CbModel.Items.Add(m);

        // Select the active model (or first if not found)
        var idx = Enumerable.Range(0, models.Count)
                            .FirstOrDefault(i => models[i] == activeModel, -1);
        CbModel.SelectedIndex = idx >= 0 ? idx : (models.Count > 0 ? 0 : -1);
    }

    /// <summary>Called when MainWindow switches to a different model (external change).</summary>
    public void SetActiveModel(string model)
    {
        var idx = CbModel.Items.IndexOf(model);
        if (idx >= 0) CbModel.SelectedIndex = idx;
    }

    // ── Model selector ────────────────────────────────────────────────────────

    private void CbModel_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var model = CbModel.SelectedItem as string;
        if (string.IsNullOrEmpty(model) || OllamaClient is null) return;

        // Re-create engine for new model (keeps history if model changes mid-conversation)
        if (_engine is null)
            _engine = new ChatEngine(OllamaClient, model);
        else
            _engine.Model = model;
    }

    // ── Input handling ────────────────────────────────────────────────────────

    private void TbInput_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter sends; Shift+Enter inserts newline
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void BtnSend_Click(object sender, RoutedEventArgs e)
        => _ = SendAsync();

    // ── Send / engine loop ────────────────────────────────────────────────────

    private async Task SendAsync()
    {
        var text = TbInput.Text.Trim();
        if (string.IsNullOrEmpty(text) || _isSending) return;

        // Ensure engine is ready
        if (OllamaClient is null) return;
        var model = CbModel.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(model)) return;

        _engine ??= new ChatEngine(OllamaClient, model);
        _engine.Model = model;

        // Hide welcome card on first message
        BdrWelcome.Visibility = Visibility.Collapsed;

        // Clear input
        TbInput.Clear();
        _isSending = true;
        BtnSend.IsEnabled = false;

        // Append user bubble
        AppendUserBubble(text);

        // Create the streaming textbox for the assistant response
        _streamBox = CreateStreamTextBox();
        AppendAssistantBubble(_streamBox);

        // Wire engine events
        _cts = new CancellationTokenSource();

        _engine.OnToken        += OnToken;
        _engine.OnToolStart    += OnToolStart;
        _engine.OnToolComplete += OnToolComplete;
        _engine.OnTurnComplete += OnTurnComplete;
        _engine.OnError        += OnEngineError;

        try
        {
            await _engine.SendAsync(text, _cts.Token);
        }
        finally
        {
            _engine.OnToken        -= OnToken;
            _engine.OnToolStart    -= OnToolStart;
            _engine.OnToolComplete -= OnToolComplete;
            _engine.OnTurnComplete -= OnTurnComplete;
            _engine.OnError        -= OnEngineError;

            _isSending = false;
            BtnSend.IsEnabled = true;
            BdrSearching.Visibility = Visibility.Collapsed;
        }
    }

    // ── Engine event handlers ─────────────────────────────────────────────────

    private void OnToken(string token)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_streamBox is not null)
                _streamBox.Text += token;
            ScrollToBottom();
        });
    }

    private void OnToolStart(string toolName, string argsJson)
    {
        Dispatcher.InvokeAsync(() =>
        {
            // Show the searching indicator in the header
            TbSearching.Text          = toolName == "web_search" ? "🔍  Searching…" : "📄  Fetching page…";
            BdrSearching.Visibility   = Visibility.Visible;

            // Insert a tool chip above the stream box
            InsertToolChip(toolName, argsJson, running: true);
            ScrollToBottom();
        });
    }

    private void OnToolComplete(string toolName, string resultSnippet)
    {
        Dispatcher.InvokeAsync(() =>
        {
            BdrSearching.Visibility = Visibility.Collapsed;

            // Update the last tool chip to show completion
            UpdateLastToolChip(toolName, resultSnippet);

            // Clear the stream box — the engine is about to start a new response
            if (_streamBox is not null)
            {
                _streamBox.Text = "";
            }
        });
    }

    private void OnTurnComplete(string finalText)
    {
        Dispatcher.InvokeAsync(() =>
        {
            BdrSearching.Visibility = Visibility.Collapsed;

            // Replace the streaming textbox with a rendered FlowDocumentScrollViewer
            if (_streamBox is not null && ChatStack.Children.Contains(_streamBox.Parent as UIElement ?? _streamBox))
            {
                var parent = _streamBox.Parent as UIElement;
                int idx    = parent is null ? -1 : ChatStack.Children.IndexOf(parent);

                var rendered = RenderMarkdown(finalText);

                if (idx >= 0)
                    ChatStack.Children.RemoveAt(idx);

                var bubble = WrapAssistantBubble(rendered);
                if (idx >= 0 && idx <= ChatStack.Children.Count)
                    ChatStack.Children.Insert(idx, bubble);
                else
                    ChatStack.Children.Add(bubble);

                _streamBox = null;
            }

            ScrollToBottom();
        });
    }

    private void OnEngineError(string error)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_streamBox is not null)
                _streamBox.Text = $"⚠  {error}";
            BdrSearching.Visibility = Visibility.Collapsed;
        });
    }

    // ── Bubble builders ───────────────────────────────────────────────────────

    private void AppendUserBubble(string text)
    {
        var tb = new TextBlock
        {
            Text            = text,
            Foreground      = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize        = 13,
            FontFamily      = new FontFamily("Segoe UI"),
            TextWrapping    = TextWrapping.Wrap,
        };
        var border = new Border
        {
            Style = (Style)FindResource("UserBubble"),
            Child = tb,
        };
        ChatStack.Children.Add(border);
    }

    private void AppendAssistantBubble(TextBox streamBox)
    {
        var bubble = new Border
        {
            Style = (Style)FindResource("AssistantBubble"),
            Child = streamBox,
        };
        ChatStack.Children.Add(bubble);
    }

    private UIElement WrapAssistantBubble(UIElement content)
    {
        return new Border
        {
            Style = (Style)FindResource("AssistantBubble"),
            Child = content,
        };
    }

    private static TextBox CreateStreamTextBox()
    {
        return new TextBox
        {
            Background          = Brushes.Transparent,
            BorderThickness     = new Thickness(0),
            Foreground          = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xC8)),
            FontFamily          = new FontFamily("Segoe UI"),
            FontSize            = 13,
            IsReadOnly          = true,
            TextWrapping        = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
    }

    private static FlowDocumentScrollViewer RenderMarkdown(string text)
    {
        var doc = MarkdownFlowDocument.Parse(text);
        return new FlowDocumentScrollViewer
        {
            Document            = doc,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            IsSelectionEnabled  = true,
        };
    }

    // ── Tool call chips ───────────────────────────────────────────────────────

    private Border? _lastToolChip;

    private void InsertToolChip(string toolName, string argsJson, bool running)
    {
        var icon  = toolName == "web_search" ? "🔍" : "📄";
        var label = FormatToolLabel(toolName, argsJson);

        var tb = new TextBlock
        {
            Text       = $"{icon}  {label}",
            Foreground = new SolidColorBrush(Color.FromRgb(0x55, 0x77, 0x44)),
            FontSize   = 11,
            FontFamily = new FontFamily("Segoe UI"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var chip = new Border
        {
            Style = (Style)FindResource("ToolChip"),
            Child = tb,
        };

        // Insert before the current stream box
        if (_streamBox?.Parent is UIElement parent)
        {
            int idx = ChatStack.Children.IndexOf(parent);
            if (idx >= 0)
                ChatStack.Children.Insert(idx, chip);
            else
                ChatStack.Children.Add(chip);
        }
        else
        {
            ChatStack.Children.Add(chip);
        }

        _lastToolChip = chip;
    }

    private void UpdateLastToolChip(string toolName, string result)
    {
        if (_lastToolChip?.Child is TextBlock tb)
        {
            var icon     = toolName == "web_search" ? "🔍" : "📄";
            var count    = toolName == "web_search"
                ? $"→ {CountLines(result)} results"
                : $"→ {result.Length:N0} chars";
            tb.Text      = $"{icon}  {toolName}  {count}";
            tb.Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x66, 0x33));
        }
    }

    private static string FormatToolLabel(string toolName, string argsJson)
    {
        try
        {
            var args = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(argsJson);
            if (args?.TryGetValue("query", out var q) == true && q is not null)
                return $"web_search(\"{Truncate(q.ToString()!, 50)}\")";
            if (args?.TryGetValue("url", out var u) == true && u is not null)
                return $"fetch_page({Truncate(u.ToString()!, 60)})";
        }
        catch { /* fall through */ }
        return toolName;
    }

    private static int CountLines(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    // ── Clear ─────────────────────────────────────────────────────────────────

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _engine?.ClearHistory();
        _streamBox = null;

        // Remove all children except the welcome card
        ChatStack.Children.Clear();
        BdrWelcome.Visibility = Visibility.Visible;
        ChatStack.Children.Add(BdrWelcome);
    }

    // ── Export / Save ─────────────────────────────────────────────────────────

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_engine is null || _engine.History.Count == 0) return;

        var dlg = new SaveFileDialog
        {
            Title      = "Save Chat",
            Filter     = "Markdown|*.md|Text|*.txt",
            FileName   = $"chat-{DateTime.Now:yyyy-MM-dd-HHmm}.md",
        };
        if (dlg.ShowDialog() != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"# Research Chat — {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"Model: {_engine.Model}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var msg in _engine.History)
        {
            sb.AppendLine(msg.Role switch
            {
                OrchestratorIDE.Models.MessageRole.User      => $"**You:** {msg.Content}",
                OrchestratorIDE.Models.MessageRole.Assistant => $"**Assistant:**\n\n{msg.Content}",
                OrchestratorIDE.Models.MessageRole.Tool      => $"> 🔧 *Tool result (hidden)*",
                _                                             => ""
            });
            sb.AppendLine();
        }

        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Save failed: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Scroll helpers ────────────────────────────────────────────────────────

    private void ScrollToBottom()
        => ChatScroll.ScrollToEnd();
}
