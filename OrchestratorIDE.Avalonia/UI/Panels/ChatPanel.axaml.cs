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
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.UI.Controls;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// General chat panel — Research mode (the original "Just Chat" behavior: web search/fetch
/// tools, research system prompt) or Open mode (no injected system prompt, no tools,
/// user-controlled temperature/top-p; see ChatEngine's Phase B2 generalization). Final
/// responses are rendered as markdown.
/// </summary>
public partial class ChatPanel : UserControl
{
    private enum ChatMode { Research, Open }

    // ── Dependencies ──────────────────────────────────────────────────────────
    public OllamaClient? OllamaClient { get; set; }

    /// <summary>Local Ollama URL, used as the "Local" node's target and to resolve it
    /// when probing/loading the persisted HIVE host list. Same convention as
    /// HivePanel.LocalUrl -- set by MainWindow from AppSettings.OllamaHost.</summary>
    public string LocalUrl { get; set; } = "http://localhost:11434";

    private const string LocalNodeName = "Local";

    // ── State ─────────────────────────────────────────────────────────────────
    private ChatEngine?              _engine;
    private CancellationTokenSource? _cts;
    private TextBox?                 _streamBox;
    private bool                     _isSending;
    private Border?                  _lastToolChip;
    private ChatMode                 _mode = ChatMode.Research;
    private List<HiveHost>           _hiveHosts = [];
    // Tracks the node CbNode was actually resolving to when _engine was last (re)built --
    // NOT just "the last SelectionChanged event," since RefreshHiveHosts() clearing and
    // re-populating Items fires spurious SelectionChanged events even when the user hasn't
    // touched anything, which must NOT wipe an in-progress conversation (see
    // CbNode_SelectionChanged).
    private string                   _lastEngineNodeTarget = LocalNodeName;

    // ── Construction ──────────────────────────────────────────────────────────

    public ChatPanel()
    {
        InitializeComponent();
        CbNode.Items.Add(LocalNodeName);
        CbNode.SelectedIndex = 0;
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

    /// <summary>
    /// Reloads the persisted HIVE host list (the same store HivePanel reads/writes) into
    /// the node picker. Does not probe reachability -- an offline node simply fails the
    /// next send with a normal connection error via OnEngineError, the same failure mode
    /// every other backend call in this panel already has. Call when the panel becomes
    /// visible so hosts added/removed via HivePanel are reflected.
    /// </summary>
    public void RefreshHiveHosts() => RefreshHiveHosts(storePath: null);

    /// <summary>storePath override exists for tests -- HiveHosts.Load's own storePath
    /// parameter is the established seam (see T14_HiveHostsTests) for testing this kind of
    /// logic against a temp file instead of the real persisted host list.</summary>
    internal void RefreshHiveHosts(string? storePath)
    {
        var previouslySelected = CbNode.SelectedItem as string;
        // HiveHosts.Load always includes its own "This PC" entry representing the local
        // machine (see Load's own doc comment) -- filtered out here because this panel's
        // own LocalNodeName entry already represents local, and routes to the INJECTED
        // OllamaClient property rather than a freshly constructed one pointed at the same
        // URL. Keeping both would show two confusingly-identical "local machine" entries.
        _hiveHosts = HiveHosts.Load(LocalUrl, storePath)
            .Where(h => h.Name != "This PC")
            .ToList();

        CbNode.Items.Clear();
        CbNode.Items.Add(LocalNodeName);
        foreach (var h in _hiveHosts) CbNode.Items.Add(h.Name);

        var idx = CbNode.Items.IndexOf(previouslySelected);
        CbNode.SelectedIndex = idx >= 0 ? idx : 0;
    }

    // ── Mode toggle ───────────────────────────────────────────────────────────
    // Switching modes recreates the engine on the next send rather than mutating it in
    // place -- systemPrompt/tools/temperature/topP are constructor-only on ChatEngine
    // (set once, not swappable mid-conversation), and mixing a research-toned exchange
    // with an open one in the same history would send a half-research, half-open
    // conversation to whichever system prompt the NEW mode resolves to. Clearing on
    // switch treats it as starting a fresh conversation, which is what a mode change
    // actually means here.

    private void BtnModeResearch_Click(object? sender, RoutedEventArgs e) => SetMode(ChatMode.Research);
    private void BtnModeOpen_Click(object? sender, RoutedEventArgs e)    => SetMode(ChatMode.Open);

    private void SetMode(ChatMode mode)
    {
        if (_mode == mode) { SyncModeToggleVisuals(); return; }

        _cts?.Cancel();
        _engine = null;
        _mode   = mode;

        BdrOpenControls.IsVisible = mode == ChatMode.Open;
        SyncModeToggleVisuals();
        ResetConversationUi();
    }

    private void SyncModeToggleVisuals()
    {
        BtnModeResearch.IsChecked   = _mode == ChatMode.Research;
        BtnModeOpen.IsChecked       = _mode == ChatMode.Open;
        BtnModeResearch.Foreground  = _mode == ChatMode.Research
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        BtnModeOpen.Foreground      = _mode == ChatMode.Open
            ? new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00))
            : new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    }

    private void ResetConversationUi()
    {
        ChatStack.Children.Clear();
        BdrWelcome.IsVisible    = true;
        TxtWelcomeTip.IsVisible = _mode == ChatMode.Research;   // research-flavored example prompts only
        TxtWelcomeTitle.Text    = _mode == ChatMode.Research ? "👋  Ready to research" : "👋  Open chat";
        TxtWelcomeBody.Text     = _mode == ChatMode.Research
            ? "Ask me anything — I can search the web, read articles, and compile research into structured reports with clickable sources."
            : "No system prompt, no tools — just the model. Set a system prompt above if you want one; leave it empty for a plain, unfiltered conversation.";
        ChatStack.Children.Add(BdrWelcome);
    }

    /// <summary>
    /// Constructs a fresh engine for the current mode, targeting whichever node CbNode has
    /// selected. Research mode uses ChatEngine's own defaults (passing no systemPrompt/tools
    /// overrides at all) to guarantee byte-identical behavior with the original "Just Chat"
    /// panel when targeting Local -- see ChatEngineTests for what those defaults are.
    /// Open-mode SystemPrompt/Temperature/TopP are intentionally NOT read from the controls
    /// here -- SendAsync refreshes them on every send (engine is mutable for exactly this),
    /// so reading them again at construction would just be immediately overwritten and is
    /// one fewer place to keep in sync.
    /// </summary>
    private ChatEngine CreateEngine(string model)
    {
        _lastEngineNodeTarget = CbNode.SelectedItem as string ?? LocalNodeName;
        var runtime = new OllamaRuntime(ResolveOllamaClient());
        return _mode == ChatMode.Research
            ? new ChatEngine(runtime, model)
            : new ChatEngine(runtime, model, systemPrompt: "", tools: []);
    }

    // ── HIVE node routing (Phase B3) ─────────────────────────────────────────────
    // "Local" keeps using the injected OllamaClient property directly (not a freshly
    // constructed one pointed at LocalUrl) so the existing test-injection pattern
    // (`new ChatPanel { OllamaClient = fake }`) keeps working unchanged -- only an
    // explicitly selected remote node constructs a new client.

    private OllamaClient ResolveOllamaClient()
    {
        var target = ResolveTargetNode();
        return target is null ? OllamaClient! : new OllamaClient(target.Url);
    }

    /// <summary>
    /// Pure node-selection logic, kept separate from OllamaClient construction so it's
    /// testable without a live network call -- constructing a real OllamaClient against an
    /// arbitrary URL is harmless (it doesn't connect until first used), but actually
    /// exercising one in a test would either need a live remote node or just be testing
    /// .NET's own HttpClient construction, neither of which is the point. Returns null for
    /// "Local" (the caller falls back to the injected OllamaClient property in that case).
    /// </summary>
    internal HiveHost? ResolveTargetNode()
    {
        var selected = CbNode.SelectedItem as string;
        if (string.IsNullOrEmpty(selected) || selected == LocalNodeName) return null;
        return _hiveHosts.FirstOrDefault(h => h.Name == selected);
    }

    private void CbNode_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var newTarget = CbNode.SelectedItem as string ?? LocalNodeName;
        // RefreshHiveHosts() clears and re-populates Items, which fires this handler even
        // when the user hasn't touched anything -- only treat it as a real switch if the
        // resolved target actually differs from what _engine was last built for, otherwise
        // every hosts-list refresh would silently wipe an in-progress conversation.
        if (newTarget == _lastEngineNodeTarget) return;

        // A real node switch is a backend change, same as a mode switch -- clear the engine
        // so the next send builds a fresh one targeting the newly selected node, and start
        // a new conversation rather than silently continuing history against a different
        // machine's model.
        _cts?.Cancel();
        _engine = null;
        ResetConversationUi();
    }

    // ── Model selector ────────────────────────────────────────────────────────

    private void CbModel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var model = CbModel.SelectedItem as string;
        if (string.IsNullOrEmpty(model) || OllamaClient is null) return;
        if (_engine is null) _engine = CreateEngine(model);
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

        _engine ??= CreateEngine(model);
        // Captured locally and used for the rest of this method instead of the _engine
        // field -- SetMode() can null out _engine and cancel _cts from a mode-toggle click
        // that lands WHILE this method is still awaiting a turn (BtnSend.IsEnabled = false
        // below blocks Send, but not the toggle buttons). Without a local reference, the
        // `finally` block's unsubscribe calls would NRE on a now-null _engine (grok review
        // BLOCKER x2, 2026-06-22) instead of cleanly unsubscribing from the engine THIS
        // call actually started.
        var engine   = _engine;
        engine.Model = model;

        // Open mode's textbox/NumericUpDown values are read fresh on every send (not just
        // at creation) so editing them between messages actually takes effect -- ChatEngine
        // exposes these as mutable properties for exactly this (grok review MINOR,
        // 2026-06-22: values were previously sampled once at engine-creation time and
        // silently ignored on every later edit+send).
        if (_mode == ChatMode.Open)
        {
            engine.SystemPrompt = TbOpenSystemPrompt.Text ?? "";
            engine.Temperature  = (double)(NudOpenTemperature.Value ?? 0.8m);
            engine.TopP         = (double?)(NudOpenTopP.Value);
        }

        // Hide welcome card on first send
        if (BdrWelcome.IsVisible)
        {
            ChatStack.Children.Remove(BdrWelcome);
            BdrWelcome.IsVisible = false;
        }

        TbInput.Clear();
        _isSending             = true;
        BtnSend.IsEnabled      = false;
        BtnModeResearch.IsEnabled = false;
        BtnModeOpen.IsEnabled     = false;

        AppendUserBubble(text);

        _streamBox = CreateStreamTextBox();
        AppendAssistantBubble(_streamBox);

        _cts = new CancellationTokenSource();

        engine.OnToken        += OnToken;
        engine.OnToolStart    += OnToolStart;
        engine.OnToolComplete += OnToolComplete;
        engine.OnTurnComplete += OnTurnComplete;
        engine.OnError        += OnEngineError;

        try
        {
            await engine.SendAsync(text, _cts.Token);
        }
        finally
        {
            engine.OnToken        -= OnToken;
            engine.OnToolStart    -= OnToolStart;
            engine.OnToolComplete -= OnToolComplete;
            engine.OnTurnComplete -= OnTurnComplete;
            engine.OnError        -= OnEngineError;

            _isSending                = false;
            BtnSend.IsEnabled         = true;
            BtnModeResearch.IsEnabled = true;
            BtnModeOpen.IsEnabled     = true;
            BdrSearching.IsVisible    = false;
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

        ResetConversationUi();
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
        var title = _mode == ChatMode.Research ? "Research Chat" : "Open Chat";
        sb.AppendLine($"# {title} — {DateTime.Now:yyyy-MM-dd HH:mm}");
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
