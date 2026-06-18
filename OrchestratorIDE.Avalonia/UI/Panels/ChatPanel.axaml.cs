// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Research;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.UI.Panels;

/// <summary>Research-focused "Just Chat" panel. Final responses are rendered as markdown.</summary>
public partial class ChatPanel : UserControl
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    public OllamaClient? OllamaClient { get; set; }

    // ── State ─────────────────────────────────────────────────────────────────
    private ChatEngine?              _engine;
    private CancellationTokenSource? _cts;
    private TextBox?                 _streamBox;
    private bool                     _isSending;
    private Border?                  _lastToolChip;

    // ── Construction ──────────────────────────────────────────────────────────

    public ChatPanel()
    {
        InitializeComponent();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public void SetModels(IReadOnlyList<string> models, string activeModel)
    {
        CbModel.Items.Clear();
        foreach (var m in models) CbModel.Items.Add(m);
        var idx = Enumerable.Range(0, models.Count)
                            .FirstOrDefault(i => models[i] == activeModel, -1);
        CbModel.SelectedIndex = idx >= 0 ? idx : (models.Count > 0 ? 0 : -1);
    }

    public void SetActiveModel(string model)
    {
        var idx = CbModel.Items.IndexOf(model);
        if (idx >= 0) CbModel.SelectedIndex = idx;
    }

    // ── Model selector ────────────────────────────────────────────────────────

    private void CbModel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var model = CbModel.SelectedItem as string;
        if (string.IsNullOrEmpty(model) || OllamaClient is null) return;
        if (_engine is null) _engine = new ChatEngine(new OllamaRuntime(OllamaClient), model);
        else                  _engine.Model = model;
    }

    // ── Input handling ────────────────────────────────────────────────────────

    private void TbInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers != KeyModifiers.Shift)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private void BtnSend_Click(object? sender, RoutedEventArgs e)
        => _ = SendAsync();

    // ── Send / engine loop ────────────────────────────────────────────────────

    private async Task SendAsync()
    {
        var text = TbInput.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(text) || _isSending) return;

        if (OllamaClient is null) return;
        var model = CbModel.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(model)) return;

        _engine       ??= new ChatEngine(new OllamaRuntime(OllamaClient), model);
        _engine.Model   = model;

        // Hide welcome card on first send
        if (BdrWelcome.IsVisible)
        {
            ChatStack.Children.Remove(BdrWelcome);
            BdrWelcome.IsVisible = false;
        }

        TbInput.Clear();
        _isSending        = true;
        BtnSend.IsEnabled = false;

        AppendUserBubble(text);

        _streamBox = CreateStreamTextBox();
        AppendAssistantBubble(_streamBox);

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

            _isSending              = false;
            BtnSend.IsEnabled       = true;
            BdrSearching.IsVisible  = false;
        }
    }

    // ── Engine event handlers ─────────────────────────────────────────────────

    private void OnToken(string token)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamBox is not null) _streamBox.Text += token;
            ScrollToBottom();
        });
    }

    private void OnToolStart(string toolName, string argsJson)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            TbSearching.Text       = toolName == "web_search" ? "🔍  Searching…" : "📄  Fetching page…";
            BdrSearching.IsVisible = true;
            InsertToolChip(toolName, argsJson);
            ScrollToBottom();
        });
    }

    private void OnToolComplete(string toolName, string resultSnippet)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            BdrSearching.IsVisible = false;
            UpdateLastToolChip(toolName, resultSnippet);
            if (_streamBox is not null) _streamBox.Text = "";
        });
    }

    private void OnTurnComplete(string finalText)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            BdrSearching.IsVisible = false;

            if (_streamBox is not null)
            {
                var box    = _streamBox;
                var bubble = box.Parent as Border;
                _streamBox = null;

                if (bubble is not null)
                    bubble.Child = new MarkdownView { Text = finalText };
                else
                {
                    // Fallback: parent detached or structure changed; keep as plain text.
                    box.Text      = finalText;
                    box.IsReadOnly = true;
                }
            }

            ScrollToBottom();
        });
    }

    private void OnEngineError(string error)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamBox is not null) _streamBox.Text = $"⚠  {error}";
            BdrSearching.IsVisible = false;
        });
    }

    // ── Bubble builders ───────────────────────────────────────────────────────

    private void AppendUserBubble(string text)
    {
        var tb = new TextBlock
        {
            Text         = text,
            Foreground   = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
            FontSize     = 13,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        };
        ChatStack.Children.Add(new Border
        {
            Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A)),
            BorderThickness     = new Avalonia.Thickness(1),
            CornerRadius        = new Avalonia.CornerRadius(8, 8, 2, 8),
            Padding             = new Avalonia.Thickness(12, 8),
            Margin              = new Avalonia.Thickness(60, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth            = 680,
            Child               = tb,
        });
    }

    private void AppendAssistantBubble(Control content)
    {
        ChatStack.Children.Add(MakeAssistantBubble(content));
    }

    private static Border MakeAssistantBubble(Control content) => new()
    {
        Background          = new SolidColorBrush(Color.FromRgb(0x11, 0x16, 0x11)),
        BorderBrush         = new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x1E)),
        BorderThickness     = new Avalonia.Thickness(1),
        CornerRadius        = new Avalonia.CornerRadius(8, 8, 8, 2),
        Padding             = new Avalonia.Thickness(14, 10),
        Margin              = new Avalonia.Thickness(0, 4, 60, 4),
        HorizontalAlignment = HorizontalAlignment.Left,
        MaxWidth            = 780,
        Child               = content,
    };

    private static TextBox CreateStreamTextBox()
    {
        var tb = new TextBox
        {
            Background      = Brushes.Transparent,
            BorderThickness = new Avalonia.Thickness(0),
            Foreground      = new SolidColorBrush(Color.FromRgb(0xC8, 0xD4, 0xC8)),
            FontSize        = 13,
            IsReadOnly      = true,
            TextWrapping    = Avalonia.Media.TextWrapping.Wrap,
        };
        // Disable the internal scroll bar so the outer ChatScroll handles paging
        ScrollViewer.SetVerticalScrollBarVisibility(tb, ScrollBarVisibility.Disabled);
        return tb;
    }

    // ── Tool call chips ───────────────────────────────────────────────────────

    private void InsertToolChip(string toolName, string argsJson)
    {
        var icon  = toolName == "web_search" ? "🔍" : "📄";
        var label = FormatToolLabel(toolName, argsJson);

        var tb = new TextBlock
        {
            Text         = $"{icon}  {label}",
            Foreground   = new SolidColorBrush(Color.FromRgb(0x55, 0x77, 0x44)),
            FontSize     = 11,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
        };
        var chip = new Border
        {
            Background          = new SolidColorBrush(Color.FromRgb(0x0E, 0x16, 0x0E)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x1E, 0x2E, 0x1E)),
            BorderThickness     = new Avalonia.Thickness(1),
            CornerRadius        = new Avalonia.CornerRadius(4),
            Padding             = new Avalonia.Thickness(8, 4),
            Margin              = new Avalonia.Thickness(0, 2, 60, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child               = tb,
        };

        // Insert before the current streaming bubble
        var parent = _streamBox?.Parent as Control;
        int idx    = parent is null ? -1 : ChatStack.Children.IndexOf(parent);
        if (idx >= 0)
            ChatStack.Children.Insert(idx, chip);
        else
            ChatStack.Children.Add(chip);

        _lastToolChip = chip;
    }

    private void UpdateLastToolChip(string toolName, string result)
    {
        if (_lastToolChip?.Child is TextBlock tb)
        {
            var icon  = toolName == "web_search" ? "🔍" : "📄";
            var count = toolName == "web_search"
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
            var args = System.Text.Json.JsonSerializer
                .Deserialize<Dictionary<string, object?>>(argsJson);
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

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _engine?.ClearHistory();
        _streamBox    = null;
        _lastToolChip = null;

        ChatStack.Children.Clear();
        BdrWelcome.IsVisible = true;
        ChatStack.Children.Add(BdrWelcome);
    }

    // ── Export / Save ─────────────────────────────────────────────────────────

    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        if (_engine is null || _engine.History.Count == 0) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title             = "Save Chat",
            SuggestedFileName = $"chat-{DateTime.Now:yyyy-MM-dd-HHmm}.md",
            FileTypeChoices   =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"] },
                new FilePickerFileType("Text")     { Patterns = ["*.txt"] },
            ],
        });

        if (file is null) return;

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
                OrchestratorIDE.Models.MessageRole.Tool      => "> 🔧 *Tool result (hidden)*",
                _                                             => ""
            });
            sb.AppendLine();
        }

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        catch { /* non-fatal — storage write failure */ }
    }

    // ── Scroll helpers ────────────────────────────────────────────────────────

    private void ScrollToBottom()
        => Dispatcher.UIThread.InvokeAsync(() => ChatScroll.ScrollToEnd());
}
