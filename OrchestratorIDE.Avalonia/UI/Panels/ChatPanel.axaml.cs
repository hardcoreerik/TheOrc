// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Research;
using OrchestratorIDE.Services;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Hive;
using OrchestratorIDE.UI.Controls;
using OrchestratorIDE.UI.ViewModels;
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE.UI.Panels;

/// <summary>CF-5: tracks the corpus currently attached to this conversation, if any.</summary>
public sealed record CorpusAttachmentState(
    string CorpusId,
    string DisplayName,
    string Edition,
    string Mode,                // "Quick" | "Study" (FabricQueryMode casing for the planner)
    string CoverageStatus,      // "complete" | "partial" | "none"
    bool IsStale,
    bool IsReadOnly,
    string PolicyProfile);      // "default" | "medical" | ...

/// <summary>
/// OrcChat — the single merged chat surface (formerly separate Research/Open modes). Web
/// search/fetch tools are always available to the model, but no system prompt is injected by
/// default -- the user controls system prompt/temperature/top-p directly, same as the old
/// Open mode's controls. Final responses are rendered as markdown.
/// </summary>
public partial class ChatPanel : UserControl
{
    // ── Dependencies ──────────────────────────────────────────────────────────
    public IModelRuntime? LocalRuntime { get; set; }
    public Func<HiveHost?, IModelRuntime>? RuntimeResolver { get; set; }

    // Legacy seam retained for tests and the plain Ollama fallback path.
    public OllamaClient? OllamaClient { get; set; }

    /// <summary>Local Ollama URL, used as the "Local" node's target and to resolve it
    /// when probing/loading the persisted HIVE host list. Same convention as
    /// HivePanel.LocalUrl -- set by MainWindow from AppSettings.OllamaHost.</summary>
    public string LocalUrl { get; set; } = "http://localhost:11434";
    public string WorkspaceRoot { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private const string LocalNodeName = "Local";

    // ── State ─────────────────────────────────────────────────────────────────
    private ChatEngine?              _engine;
    private IModelRuntime?           _engineRuntime;
    private CancellationTokenSource? _cts;
    private TextBox?                 _streamBox;
    private bool                     _isSending;
    private Border?                  _lastToolChip;
    private List<HiveHost>           _hiveHosts = [];
    private readonly List<ChatAttachment> _pendingAttachments = [];
    // Tracks the node CbNode was actually resolving to when _engine was last (re)built --
    // NOT just "the last SelectionChanged event," since RefreshHiveHosts() clearing and
    // re-populating Items fires spurious SelectionChanged events even when the user hasn't
    // touched anything, which must NOT wipe an in-progress conversation (see
    // CbNode_SelectionChanged).
    private string                   _lastEngineNodeTarget = LocalNodeName;
    // null = the real persisted-memory file (OpenChatMemory.StorePath). Settable internally
    // so tests can redirect both LoadPersistedMemory and the auto-save-on-edit wiring below
    // to a temp file instead of polluting the user's actual saved system prompt.
    internal string?                 MemoryStorePathOverride { get; set; }

    // ── CF-5 Library state ──────────────────────────────────────────────────────
    private CorpusAttachmentState?      _corpus;        // null = plain chat; non-null = source-bound
    private LibraryViewModel?           _libraryVm;
    private FabricAskService?           _askService;
    private FabricIndexingOrchestrator? _indexingOrchestrator;
    private string                      _notebookPath = "";
    private readonly List<ConversationNotebookEntry> _notebookEntries = [];

    // ── Construction ──────────────────────────────────────────────────────────

    public ChatPanel()
    {
        InitializeComponent();
        CbNode.Items.Add(LocalNodeName);
        CbNode.SelectedIndex = 0;

        LibraryDrawer.CorpusAttachRequested += OnCorpusAttachRequested;
        LibraryDrawer.CorpusDetachRequested += OnCorpusDetachRequested;

        // Auto-saves the system prompt on every edit -- including a preset button's
        // programmatic Text assignment, which also fires TextChanged. This is the actual
        // persistence mechanism: whatever the user puts here (a chosen name, a standing
        // instruction) survives an app restart because IT is what's saved, not some
        // separate "memory" concept the model would need a tool to write to itself (Open
        // mode deliberately has none).
        TbOpenSystemPrompt.TextChanged += (_, _) =>
            OpenChatMemory.SaveSystemPrompt(TbOpenSystemPrompt.Text ?? "", MemoryStorePathOverride);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the persisted Open Chat system prompt into TbOpenSystemPrompt -- call when the
    /// panel becomes visible (same lifecycle as RefreshHiveHosts; MainWindow calls both
    /// together). Safe to call repeatedly: every edit is auto-saved immediately (see the
    /// TextChanged wiring in the constructor), so there's never an "unsaved edit" a reload
    /// could clobber -- reloading just re-reads whatever was already written.
    /// </summary>
    public void LoadPersistedMemory() => LoadPersistedMemory(storePath: null);

    /// <summary>storePath override exists for tests -- same seam shape as
    /// RefreshHiveHosts(string?), avoiding the real persisted file during test runs.</summary>
    internal void LoadPersistedMemory(string? storePath)
    {
        MemoryStorePathOverride = storePath;
        TbOpenSystemPrompt.Text = OpenChatMemory.LoadSystemPrompt(storePath);
    }

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

    /// <summary>
    /// Wires the CF-5 Context Fabric Library services into this panel — called by
    /// MainWindow after construction, same lifecycle as SetModels(). Until this is called,
    /// the library drawer/source-preview rails stay hidden and chat behaves exactly as
    /// before CF-5 (plain ChatEngine path only).
    /// </summary>
    public void SetFabricServices(
        FabricAskService askService,
        FabricIndexingOrchestrator indexingOrchestrator,
        LibraryViewModel libraryVm,
        FabricWebImporter? webImporter,
        string workspaceRoot)
    {
        _askService           = askService;
        _indexingOrchestrator = indexingOrchestrator;
        _libraryVm            = libraryVm;
        _notebookPath         = ConversationNotebookStore.StorePath(workspaceRoot, "default");

        _notebookEntries.Clear();
        _notebookEntries.AddRange(ConversationNotebookStore.Load(_notebookPath));

        LibraryDrawer.Attach(libraryVm, indexingOrchestrator, webImporter);
        LibraryDrawer.UpdateNotebook(_notebookEntries);
    }

    // ── System-prompt presets ────────────────────────────────────────────────────
    // Concrete, behaviorally-specific instructions, not vague dispositions -- "answer
    // without judgment" tested out to have near-zero effect in real use, while a concrete
    // persona/behavior instruction reliably changed output. These are the same shape:
    // a specific behavior the model can actually perform, not an abstract attitude.
    // Replaces the textbox's current text rather than appending -- clicking a preset is
    // meant to hand a novice user a ready-to-send prompt, not build up an ambiguous mix.

    private const string SecurityResearchPreset =
        "You are assisting a security researcher with explicit authorization to test their " +
        "own systems. Answer technical security and penetration-testing questions directly " +
        "and completely -- explain tools, techniques, and exploitation steps in full. Note " +
        "authorization/scope considerations briefly where relevant, but do not refuse or " +
        "hedge on legitimate security research and testing topics.";

    private const string DirectUnfilteredPreset =
        "Answer directly and completely. Do not add disclaimers, warnings, or moral " +
        "commentary unless explicitly asked for them.";

    private const string PlainProsePreset =
        "Respond in plain paragraphs. Do not use markdown formatting, bullet points, or " +
        "numbered lists unless explicitly asked for them.";

    private void BtnPresetSecurity_Click(object? sender, RoutedEventArgs e)    => TbOpenSystemPrompt.Text = SecurityResearchPreset;
    private void BtnPresetDirect_Click(object? sender, RoutedEventArgs e)     => TbOpenSystemPrompt.Text = DirectUnfilteredPreset;
    private void BtnPresetPlainProse_Click(object? sender, RoutedEventArgs e) => TbOpenSystemPrompt.Text = PlainProsePreset;

    private void ResetConversationUi()
    {
        DisposeEngineRuntime();
        ChatStack.Children.Clear();
        BdrWelcome.IsVisible = true;
        ChatStack.Children.Add(BdrWelcome);
    }

    /// <summary>
    /// Constructs a fresh engine targeting whichever node CbNode has selected. Web
    /// search/fetch tools are always available (the ChatEngine default toolset, passing
    /// tools: null) but no system prompt is injected (systemPrompt: "") -- OrcChat never
    /// puts words in the model's mouth, it just gives it tools. SystemPrompt/Temperature/TopP
    /// are intentionally NOT read from the controls here -- SendAsync refreshes them on every
    /// send (engine is mutable for exactly this), so reading them again at construction would
    /// just be immediately overwritten and is one fewer place to keep in sync.
    /// </summary>
    private ChatEngine CreateEngine(string model)
    {
        _lastEngineNodeTarget = CbNode.SelectedItem as string ?? LocalNodeName;
        var runtime = ResolveRuntime();
        _engineRuntime = runtime;
        var tools = OrcChatToolCatalog.CreateWorkspaceTools(WorkspaceRoot);
        return new ChatEngine(runtime, model, systemPrompt: "", tools: tools)
        {
            IncludeDateTimeContext = true,
            ReactInstructions = OrcChatToolCatalog.BuildReactInstructions(tools),
        };
    }

    // ── HIVE node routing (Phase B3) ─────────────────────────────────────────────
    // "Local" keeps using the injected OllamaClient property directly (not a freshly
    // constructed one pointed at LocalUrl) so the existing test-injection pattern
    // (`new ChatPanel { OllamaClient = fake }`) keeps working unchanged -- only an
    // explicitly selected remote node constructs a new client.

    private IModelRuntime ResolveRuntime()
    {
        var target = ResolveTargetNode();
        if (RuntimeResolver is not null)
            return RuntimeResolver(target);

        if (target is not null)
            return new OllamaRuntime(new OllamaClient(target.Url));

        if (LocalRuntime is not null)
            return LocalRuntime;

        if (OllamaClient is not null)
            return new OllamaRuntime(OllamaClient);

        throw new InvalidOperationException("No local chat runtime is configured.");
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
        // machine's model. _streamBox is nulled here too, matching BtnClear_Click -- without
        // it, a tool call still in flight against the OLD node could land its OnToolStart
        // after this reset and find a detached parent, falling through InsertToolChip's Add
        // fallback into the freshly-reset welcome card (grok review MINOR, 2026-06-23).
        _cts?.Cancel();
        _engine    = null;
        _streamBox = null;
        ResetConversationUi();
        _ = RefreshContextLimitAsync();
    }

    // ── Model selector ────────────────────────────────────────────────────────

    private void CbModel_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var model = CbModel.SelectedItem as string;
        if (string.IsNullOrEmpty(model)) return;
        if (_engine is null) _engine = CreateEngine(model);
        else                  _engine.Model = model;
        _ = RefreshContextLimitAsync();
    }

    // ── Context window usage ─────────────────────────────────────────────────────
    // Cached per (node, model) -- GetContextLengthAsync is a real network call (Ollama's
    // /api/show), and the value never changes for a given model, so repeatedly fetching it
    // on every keystroke-adjacent event would be wasteful.

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> _contextLengthCache = new();
    private int? _lastContextLimit;

    /// <summary>
    /// Fire-and-forget from SelectionChanged handlers (`_ = RefreshContextLimitAsync()`),
    /// so this method owns its own exception handling rather than letting a network failure
    /// become an unobserved exception on the UI thread -- same class of issue as the
    /// MenuItem.Click async-void fix elsewhere in this file (grok review BLOCKER,
    /// 2026-06-22), applied proactively here instead of waiting to rediscover it.
    ///
    /// The await for a cache miss resumes on whatever thread the continuation lands on, not
    /// necessarily the UI thread -- _lastContextLimit and the Border/TextBlock mutation in
    /// UpdateContextUsageDisplay must only happen via Dispatcher.UIThread.InvokeAsync, same
    /// as OnUsage below (grok review BLOCKER, 2026-06-22). The cache dictionary itself is a
    /// ConcurrentDictionary so overlapping rapid node/model switches can't corrupt it either.
    /// </summary>
    private async Task RefreshContextLimitAsync()
    {
        try
        {
            var model = CbModel.SelectedItem as string;
            if (string.IsNullOrEmpty(model))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _lastContextLimit = null;
                    UpdateContextUsageDisplay(null, null);
                });
                return;
            }

            var cacheKey = $"{(CbNode.SelectedItem as string) ?? LocalNodeName}|{model}";
            if (!_contextLengthCache.TryGetValue(cacheKey, out var limit))
            {
                limit = await ResolveRuntime().GetContextLengthAsync(model);
                _contextLengthCache[cacheKey] = limit;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                _lastContextLimit = limit;
                UpdateContextUsageDisplay(null, limit);   // no usage yet for the new selection
            });
        }
        catch { /* non-fatal -- the usage indicator just stays hidden */ }
    }

    private void UpdateContextUsageDisplay(int? tokensUsed, int? limit)
    {
        if (limit is null)
        {
            BdrContextUsage.IsVisible = false;
            return;
        }

        BdrContextUsage.IsVisible = true;
        TxtContextUsage.Text = tokensUsed is { } used
            ? $"Context: {used:N0} / {limit:N0} ({used * 100.0 / limit.Value:F0}%)"
            : $"Context: 0 / {limit:N0}";
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
        if ((string.IsNullOrEmpty(text) && _pendingAttachments.Count == 0) || _isSending) return;

        // ── CF-5: route through FabricAskService when a corpus is attached ───────
        // Source-bound asks skip the Ollama model picker entirely (FabricAskService
        // generates via the native IRoleRuntime Reviewer role, not the chat model).
        // Only takes this path with no pending attachments -- FabricAskService has no
        // attachment support, so falling through to the plain ChatEngine path below is what
        // actually sends them instead of silently dropping them.
        if (_corpus is not null && _askService is not null && !string.IsNullOrEmpty(text) && _pendingAttachments.Count == 0)
        {
            await SendFabricAsync(_corpus, text, CancellationToken.None);
            return;
        }

        var model = CbModel.SelectedItem as string ?? "";
        if (string.IsNullOrEmpty(model)) return;

        _engine ??= CreateEngine(model);
        // Captured locally and used for the rest of this method instead of the _engine
        // field -- CbNode_SelectionChanged() can null out _engine and cancel _cts from a
        // node-switch click that lands WHILE this method is still awaiting a turn
        // (BtnSend.IsEnabled = false below blocks Send, but not the node picker). Without a
        // local reference, the `finally` block's unsubscribe calls would NRE on a now-null
        // _engine (grok review BLOCKER x2, 2026-06-22) instead of cleanly unsubscribing from
        // the engine THIS call actually started.
        var engine   = _engine;
        engine.Model = model;

        // Textbox values are read fresh on every send (not just at creation) so editing them
        // between messages actually takes effect -- ChatEngine exposes these as mutable
        // properties for exactly this (grok review MINOR, 2026-06-22: values were previously
        // sampled once at engine-creation time and silently ignored on every later edit+send).
        // Plain TextBox, not NumericUpDown -- see ChatPanel.axaml's comment on that control
        // for why.
        engine.SystemPrompt = TbOpenSystemPrompt.Text ?? "";
        engine.Temperature  = ParseDoubleOrDefault(TbOpenTemperature.Text, 0.8);
        engine.TopP         = ParseDoubleOrDefault(TbOpenTopP.Text, 0.9);
        var pendingAttachments = _pendingAttachments.ToList();
        var promptText = await BuildPromptTextWithAttachmentsAsync(text, pendingAttachments, cts: null);

        // Hide welcome card on first send
        if (BdrWelcome.IsVisible)
        {
            ChatStack.Children.Remove(BdrWelcome);
            BdrWelcome.IsVisible = false;
        }

        TbInput.Clear();
        _isSending        = true;
        BtnSend.IsEnabled = false;
        ClearPendingAttachments();

        AppendUserBubble(text, pendingAttachments);

        _streamBox = CreateStreamTextBox();
        AppendAssistantBubble(_streamBox);

        var cts = new CancellationTokenSource();
        _cts    = cts;

        engine.OnToken        += OnToken;
        engine.OnToolStart    += OnToolStart;
        engine.OnToolComplete += OnToolComplete;
        engine.OnTurnComplete += OnTurnComplete;
        engine.OnError        += OnEngineError;
        engine.OnUsage        += OnUsage;

        try
        {
            await engine.SendAsync(promptText, pendingAttachments.Where(a => a.IsImage).ToList(), cts.Token);
        }
        finally
        {
            engine.OnToken        -= OnToken;
            engine.OnToolStart    -= OnToolStart;
            engine.OnToolComplete -= OnToolComplete;
            engine.OnTurnComplete -= OnTurnComplete;
            engine.OnError        -= OnEngineError;
            engine.OnUsage        -= OnUsage;

            _isSending             = false;
            BtnSend.IsEnabled      = true;
            BdrSearching.IsVisible = false;

            // Only the field's own owner disposes it -- if a node switch or Clear already
            // replaced/cancelled it via _cts (see CbNode_SelectionChanged/BtnClear_Click),
            // those call sites never null the field, so this send's `cts` local is always
            // the right (and only) one to dispose here; nulling the field first prevents a
            // racing Cancel() call elsewhere from throwing ObjectDisposedException on an
            // already-disposed source (grok review MINOR, 2026-06-23).
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    // ── CF-5: source-bound ask path ──────────────────────────────────────────

    private async Task SendFabricAsync(CorpusAttachmentState corpus, string question, CancellationToken ct)
    {
        if (_askService is null) return;

        if (BdrWelcome.IsVisible)
        {
            ChatStack.Children.Remove(BdrWelcome);
            BdrWelcome.IsVisible = false;
        }

        TbInput.Clear();
        _isSending = true;
        BtnSend.IsEnabled = false;

        AppendUserBubble(question, []);
        var placeholder = new TextBlock { Text = "…", Foreground = new SolidColorBrush(Color.FromRgb(0x7E, 0x8C, 0x7E)) };
        var bubble = MakeAssistantBubble(placeholder);
        ChatStack.Children.Add(bubble);
        ScrollToBottom();

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _cts = cts;

        try
        {
            // No ConfigureAwait(false) -- the finally block below touches UI-thread-only
            // controls (BtnSend.IsEnabled), so this must resume on Avalonia's UI-thread
            // SynchronizationContext, same as the rest of this method.
            var result = await _askService.AskAsync(question, corpus.CorpusId, corpus.Mode, cts.Token);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                bubble.Child = BuildCitedAnswerView(result, corpus);
                bubble.Tag = result.Answer;
                ScrollToBottom();
            });
        }
        catch (OperationCanceledException)
        {
            // user cancelled
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
                bubble.Child = new TextBlock
                {
                    Text = $"⚠ Library ask failed: {ex.Message}",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xC0, 0x61, 0x4F)),
                    TextWrapping = TextWrapping.Wrap,
                });
        }
        finally
        {
            _isSending = false;
            BtnSend.IsEnabled = true;
            if (ReferenceEquals(_cts, cts)) _cts = null;
            cts.Dispose();
        }
    }

    private Control BuildCitedAnswerView(FabricAskResult result, CorpusAttachmentState corpus)
    {
        var stack = new StackPanel { Spacing = 6 };

        if (corpus.IsStale || (result.SegmentsTotal > 0 && result.SegmentsConsidered < result.SegmentsTotal * 0.5))
        {
            stack.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1B, 0x0C)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3C, 0x18)),
                BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
                Padding = new Avalonia.Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = $"⚠ Index incomplete — answers may miss sections. Coverage: {result.SegmentsConsidered}/{result.SegmentsTotal} segments.",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xA8, 0x5A)),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
            });
        }

        if (!string.Equals(corpus.PolicyProfile, "default", StringComparison.OrdinalIgnoreCase))
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Educational interpretation of the cited source — not professional advice.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xC4, 0xD8)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (!result.FitsBudget)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "Evidence truncated by token budget — ask a narrower question or switch to Study.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xA8, 0x5A)),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (result.Abstained)
        {
            stack.Children.Add(new TextBlock { Text = result.Answer, TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)) });
        }
        else
        {
            stack.Children.Add(new MarkdownView { Text = result.Answer, LinkClicked = OpenChatLink });
        }

        stack.Children.Add(BuildCoverageLine(result, corpus));

        var citations = BuildCitationViewModels(result);
        foreach (var cit in citations)
            stack.Children.Add(BuildFootnoteRow(cit));

        return stack;
    }

    private static Control BuildCoverageLine(FabricAskResult result, CorpusAttachmentState corpus)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 6, 0, 0),
        };
        var mono = new FontFamily("JetBrains Mono, Consolas, monospace");

        row.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x13, 0x25, 0x13)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2F, 0x4F, 0x2F)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(999),
            Padding = new Avalonia.Thickness(7, 2),
            Child = new TextBlock
            {
                Text = string.Equals(result.Mode, "study", StringComparison.OrdinalIgnoreCase) ? "Study mode" : "Quick mode",
                FontFamily = mono, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x9F, 0xD3, 0x7F)),
            },
        });

        var reopened = result.TriggeredSourceReopen ? $" · reopened {result.SegmentsConsidered} sources" : "";
        row.Children.Add(new TextBlock
        {
            Text = $"considered {result.SegmentsConsidered} / {result.SegmentsTotal} segments{reopened}",
            FontFamily = mono, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x6E, 0x80, 0x68)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var allVerified = result.Claims.Count == 0 || result.Claims.All(c => c.VerificationLabel is "supported" or "partially_supported");
        row.Children.Add(new TextBlock
        {
            Text = allVerified ? "✓ citations verified" : "⚠ coverage incomplete",
            FontFamily = mono, FontSize = 10,
            Foreground = new SolidColorBrush(allVerified ? Color.FromRgb(0x7F, 0xB0, 0x69) : Color.FromRgb(0xD6, 0xA8, 0x5A)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var interpretiveCount = result.Claims.Count(c => c.VerificationLabel == "interpretive");
        if (interpretiveCount > 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = $"✎ {interpretiveCount} interpretive",
                FontFamily = mono, FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0x94, 0xA8)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        return row;
    }

    private List<CitationViewModel> BuildCitationViewModels(FabricAskResult result)
    {
        var list = new List<CitationViewModel>();
        var seen = new HashSet<int>();
        foreach (var claim in result.Claims)
            foreach (var cit in claim.Citations)
            {
                if (!seen.Add(cit.Index)) continue;
                // FabricAskService leaves HeadingPath and DocumentId blank by design ("resolved
                // from repo at render time in the UI layer" -- see
                // FabricAskService.BuildCitationDetail) -- both are resolved here from the
                // segment lookup instead. Previously this passed corpusId in the DocumentId slot,
                // which meant "Open source file" and the preview's document-name display always
                // failed (GetDocument(corpusId) never matches a real document id).
                var segment = _libraryVm?.Repository.GetSegment(cit.SegmentId);
                var heading = string.IsNullOrWhiteSpace(cit.HeadingPath)
                    ? segment?.HeadingPath ?? ""
                    : cit.HeadingPath;
                var documentId = segment?.DocumentId ?? "";
                list.Add(new CitationViewModel(
                    cit.Index, cit.SegmentId, documentId, heading,
                    cit.CharStart, cit.CharEnd, cit.Quote,
                    MapVerificationLabel(cit.VerificationLabel)));
            }
        return list;
    }

    private static string MapVerificationLabel(string fabricLabel) => fabricLabel switch
    {
        FabricCitationVerificationLabel.Supported => "Supported",
        FabricCitationVerificationLabel.PartiallySupported => "PartiallySupported",
        FabricCitationVerificationLabel.Interpretive => "Interpretive",
        FabricCitationVerificationLabel.CitationMismatch => "CitationMismatch",
        _ => "Unverifiable",
    };

    private Control BuildFootnoteRow(CitationViewModel cit)
    {
        var (fg, bg) = cit.IsVerified
            ? (Color.FromRgb(0x9F, 0xD3, 0x7F), Color.FromRgb(0x13, 0x25, 0x13))
            : cit.IsInterpretive
                ? (Color.FromRgb(0x7F, 0x94, 0xA8), Color.FromRgb(0x10, 0x16, 0x1C))
                : (Color.FromRgb(0xC0, 0x61, 0x4F), Color.FromRgb(0x1E, 0x11, 0x0E));

        var row = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x14, 0x0E)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x24, 0x1A)),
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(6),
            Padding = new Avalonia.Thickness(8, 5),
            Margin = new Avalonia.Thickness(0, 2, 0, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Border
                    {
                        Background = new SolidColorBrush(bg),
                        CornerRadius = new Avalonia.CornerRadius(999),
                        Padding = new Avalonia.Thickness(5, 1),
                        Child = new TextBlock { Text = $"{cit.Index}", FontSize = 9.5, Foreground = new SolidColorBrush(fg) },
                    },
                    new TextBlock
                    {
                        // cit.SegmentId already starts with "seg-" -- truncate the raw id
                        // itself rather than prepending another "seg-" on top of it (was
                        // rendering "seg-seg-f27c" instead of "seg-f27c").
                        Text = string.IsNullOrWhiteSpace(cit.HeadingPath)
                            ? $"{ShortSegmentId(cit.SegmentId)} · {cit.CharStart}-{cit.CharEnd} · {cit.VerificationLabel}"
                            : $"{cit.HeadingPath} · {ShortSegmentId(cit.SegmentId)} · {cit.CharStart}-{cit.CharEnd} · {cit.VerificationLabel}",
                        FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                        FontSize = 10, Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x9A, 0x84)),
                    },
                },
            },
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(row, $"OrcChat.Citation.{cit.Index}");
        row.PointerPressed += (_, _) => OpenSourcePreview(cit);
        return row;
    }

    private static string ShortSegmentId(string segmentId) =>
        segmentId.Length > 8 ? segmentId[..8] : segmentId;

    private void OpenSourcePreview(CitationViewModel cit)
    {
        if (_libraryVm is null) return;
        var owner = TopLevel.GetTopLevel(this) as Window;
        var dialog = new SourceCitationWindow(cit, _libraryVm, OnSaveToNotebookRequested);
        if (owner is not null)
            dialog.ShowDialog(owner);
        else
            dialog.Show();
    }

    // ── CF-5: library drawer / corpus bar event handlers ────────────────────

    private void BtnToggleLibrary_Click(object? sender, RoutedEventArgs e)
    {
        var show = !LibraryDrawer.IsVisible;
        LibraryDrawer.IsVisible = show;
        LibraryDrawerSplitter.IsVisible = show;
    }

    private void OnCorpusAttachRequested(string corpusId)
    {
        if (_libraryVm is null) return;
        var corpus = _libraryVm.Corpora.FirstOrDefault(c => c.CorpusId == corpusId);
        if (corpus is null) return;

        _corpus = new CorpusAttachmentState(
            corpusId, corpus.Name, "", FabricQueryMode.Quick, "complete", false, false, "default");

        TxtCorpusName.Text = corpus.Name;
        TxtCorpusStatus.Text = "source-bound · coverage complete";
        BdrCorpusBadge.IsVisible = true;
        StackModeToggle.IsVisible = true;
        SetModeUi(FabricQueryMode.Quick);

        LibraryDrawer.SetAttachedCorpus(corpusId);
    }

    private void OnCorpusDetachRequested()
    {
        _corpus = null;
        BdrCorpusBadge.IsVisible = false;
        StackModeToggle.IsVisible = false;
        TxtModeHint.Text = "";
        LibraryDrawer.SetAttachedCorpus(null);
    }

    private void BtnDetachCorpus_Click(object? sender, RoutedEventArgs e) => OnCorpusDetachRequested();

    private void BtnModeQuick_Click(object? sender, RoutedEventArgs e) => SetModeUi(FabricQueryMode.Quick);
    private void BtnModeStudy_Click(object? sender, RoutedEventArgs e) => SetModeUi(FabricQueryMode.Study);

    private void SetModeUi(string mode)
    {
        if (_corpus is not null) _corpus = _corpus with { Mode = mode };

        var isQuick = mode == FabricQueryMode.Quick;
        BtnModeQuick.Background = isQuick ? new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)) : Brushes.Transparent;
        BtnModeQuick.Foreground = isQuick ? new SolidColorBrush(Color.FromRgb(0xC4, 0xE8, 0x9A)) : new SolidColorBrush(Color.FromRgb(0x7E, 0x8C, 0x7E));
        BtnModeStudy.Background = isQuick ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A));
        BtnModeStudy.Foreground = isQuick ? new SolidColorBrush(Color.FromRgb(0x7E, 0x8C, 0x7E)) : new SolidColorBrush(Color.FromRgb(0xC4, 0xE8, 0x9A));

        TxtModeHint.Text = isQuick
            ? "hybrid retrieval over segments + summaries"
            : "iterative retrieval + targeted rereading";
    }

    private void OnSaveToNotebookRequested(CitationViewModel cit)
    {
        var entry = new ConversationNotebookEntry(
            cit.Quote, [cit], DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
        _notebookEntries.Add(entry);
        if (!string.IsNullOrEmpty(_notebookPath))
            ConversationNotebookStore.Append(_notebookPath, entry);
        LibraryDrawer.UpdateNotebook(_notebookEntries);
    }

    // ── Engine event handlers ─────────────────────────────────────────────────

    /// <summary>
    /// promptTokens already covers the entire conversation sent in this call (system prompt
    /// + full history + the new message) -- not just the new message's own size -- so
    /// prompt+completion from the MOST RECENT call alone is the current total context
    /// size, not something to sum across turns.
    /// </summary>
    private void OnUsage(int promptTokens, int completionTokens)
    {
        // Fire-and-forget InvokeAsync -- the returned Task is never observed, so any
        // exception from UpdateContextUsageDisplay must be caught here rather than left to
        // become an unobserved exception (same class as the async-void MenuItem.Click
        // BLOCKER fixed elsewhere in this file, 2026-06-22).
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try { UpdateContextUsageDisplay(promptTokens + completionTokens, _lastContextLimit); }
            catch { /* non-fatal -- the usage indicator just stays stale */ }
        });
    }

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
            // Clear (BtnClear_Click) and a node switch (CbNode_SelectionChanged) both null
            // _streamBox when they reset the conversation. A tool call still in flight at
            // that moment (the cancellation token is only checked at certain points in the
            // engine's loop, not synchronously) can still fire OnToolStart afterward --
            // without this guard, InsertToolChip's "no active bubble" fallback would Add the
            // chip straight into whatever's in ChatStack now (the freshly-reset welcome
            // card), a stale artifact from a conversation that's already gone (grok review
            // MINOR, 2026-06-23). No anchor bubble means this tool call's chip has nowhere
            // meaningful to go -- drop it.
            if (_streamBox is null) return;

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
                {
                    bubble.Child = new MarkdownView { Text = finalText, LinkClicked = OpenChatLink };
                    // Tag carries the raw markdown source for "Copy as Markdown" -- the
                    // rendered MarkdownView tree has no single string property to read it
                    // back from once it's been split into multiple SelectableTextBlocks.
                    bubble.Tag = finalText;
                }
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

    private void AppendUserBubble(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var content = BuildUserBubbleContent(text, attachments);
        var bubble = new Border
        {
            Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x2A, 0x1A)),
            BorderBrush         = new SolidColorBrush(Color.FromRgb(0x2A, 0x4A, 0x2A)),
            BorderThickness     = new Avalonia.Thickness(1),
            CornerRadius        = new Avalonia.CornerRadius(8, 8, 2, 8),
            Padding             = new Avalonia.Thickness(12, 8),
            Margin              = new Avalonia.Thickness(60, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Right,
            MaxWidth            = 680,
            Child               = content,
            Tag                 = BuildUserBubbleMarkdown(text, attachments),
        };
        bubble.ContextMenu = BuildBubbleContextMenu(bubble);
        ChatStack.Children.Add(bubble);
    }

    private void AppendAssistantBubble(Control content)
    {
        ChatStack.Children.Add(MakeAssistantBubble(content));
    }

    private Border MakeAssistantBubble(Control content)
    {
        var bubble = new Border
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
            // Tag is set to the raw markdown source once streaming completes (OnTurnComplete)
            // -- empty while still streaming, since there's nothing finalized to copy yet.
        };
        bubble.ContextMenu = BuildBubbleContextMenu(bubble);
        return bubble;
    }

    /// <summary>
    /// "Copy" strips markdown syntax for a clean plain-text paste target (email, plain
    /// notes); "Copy as Markdown" copies the bubble's raw source verbatim (Tag), for pasting
    /// into a markdown-aware target (a PR description, a doc, another markdown editor).
    /// Built once per bubble at creation time rather than relying on text selection across
    /// the rendered tree -- MarkdownView splits a message into several SelectableTextBlocks,
    /// so there's no single control whose SelectedText would cover the whole message; native
    /// per-block selection (Ctrl+C) still works for copying a piece of one block.
    /// </summary>
    private ContextMenu BuildBubbleContextMenu(Border bubble)
    {
        var copyPlain = new MenuItem { Header = "Copy" };
        copyPlain.Click += async (_, _) => await CopyBubbleTextAsync(bubble, asMarkdown: false);

        var copyMarkdown = new MenuItem { Header = "Copy as Markdown" };
        copyMarkdown.Click += async (_, _) => await CopyBubbleTextAsync(bubble, asMarkdown: true);

        return new ContextMenu { ItemsSource = new[] { copyPlain, copyMarkdown } };
    }

    /// <summary>
    /// MenuItem.Click's delegate signature is void, so `copyPlain.Click += async (_, _) => ...`
    /// is effectively an async-void handler -- any exception thrown inside (clipboard access
    /// can fail, e.g. another process holding it, or no clipboard backend at all) would not
    /// be observable to any caller and would propagate as an unhandled exception on the UI
    /// thread instead (grok review BLOCKER x2, 2026-06-22). Caught and swallowed here, same
    /// "non-fatal, this is just a convenience action" pattern BtnExport_Click already uses
    /// for its own storage-write failure.
    /// </summary>
    private async Task CopyBubbleTextAsync(Border bubble, bool asMarkdown)
    {
        try
        {
            if (bubble.Tag is not string raw || raw.Length == 0) return;
            var text = asMarkdown ? raw : StripMarkdownForPlainCopy(raw);

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(text);
        }
        catch { /* non-fatal -- clipboard access failure, nothing the user can act on here */ }
    }

    /// <summary>
    /// Best-effort markdown-marker stripper for the "Copy" (plain text) action -- not a full
    /// parser, just removes the syntax MarkdownView itself renders (bold/italic/inline code,
    /// heading/list/quote prefixes) so a plain-text paste target doesn't show raw `**`/`#`/`-`
    /// characters. Good enough for a convenience copy action; not used for anything that
    /// needs exact fidelity (that's what "Copy as Markdown" is for).
    /// </summary>
    internal static string StripMarkdownForPlainCopy(string md) =>
        System.Text.RegularExpressions.Regex.Replace(
            md,
            @"\*\*([^*\n]+)\*\*|__([^_\n]+)__|\*([^*\n]+)\*|_([^_\n]+)_|`([^`\n]+)`|^#{1,6}\s+|^[-*]\s+|^\d+\.\s+|^>\s?",
            m =>
            {
                for (int i = 1; i <= 5; i++)
                    if (m.Groups[i].Success) return m.Groups[i].Value;
                return "";
            },
            System.Text.RegularExpressions.RegexOptions.Multiline);

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

    private Control BuildUserBubbleContent(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        if (attachments.Count == 0)
        {
            return new SelectableTextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                FontSize = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }

        var stack = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(text))
        {
            stack.Children.Add(new SelectableTextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)),
                FontSize = 13,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            });
        }

        if (attachments.Count > 0)
            stack.Children.Add(new MarkdownView { Text = BuildUserBubbleMarkdown("", attachments) });

        return stack;
    }

    private static string BuildUserBubbleMarkdown(string text, IReadOnlyList<ChatAttachment> attachments)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(text))
            sb.AppendLine(text);

        foreach (var attachment in attachments)
        {
            if (attachment.IsImage)
            {
                sb.AppendLine();
                sb.AppendLine($"![{attachment.DisplayName}]({attachment.FilePath})");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine($"- Attached: `{attachment.DisplayName}`");
            }
        }

        return sb.ToString().TrimEnd();
    }

    // ── Tool call chips ───────────────────────────────────────────────────────

    private void InsertToolChip(string toolName, string argsJson)
    {
        var icon  = ToolIcon(toolName);
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
            var icon  = ToolIcon(toolName);
            var count = toolName == "web_search"
                ? $"→ {CountLines(result)} results"
                : toolName == "save_markdown_document"
                    ? "→ saved"
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
            if (args?.TryGetValue("path", out var p) == true && p is not null)
                return $"{toolName}({Truncate(p.ToString()!, 48)})";
            if (args?.TryGetValue("filename", out var f) == true && f is not null)
                return $"{toolName}({Truncate(f.ToString()!, 48)})";
        }
        catch { /* fall through */ }
        return toolName;
    }

    private static string ToolIcon(string toolName) => toolName switch
    {
        "web_search" => "🔍",
        "fetch_page" or "fetch_url" => "📄",
        "read_file" or "write_file" or "save_markdown_document" => "📝",
        "list_files" or "grep_code" or "get_outline" => "📁",
        "run_tests" => "🧪",
        _ => "🛠",
    };

    private static int CountLines(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>Invariant-culture parse with a fallback -- a malformed temp/top-p value (e.g.
    /// the field left empty, or a typo) must not throw and must not silently send 0, which
    /// would be a real, very different sampling behavior than "use the default."</summary>
    private static double ParseDoubleOrDefault(string? text, double fallback) =>
        double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    // ── Clear ─────────────────────────────────────────────────────────────────

    private void BtnClear_Click(object? sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _engine?.ClearHistory();
        _engine       = null;
        _streamBox    = null;
        _lastToolChip = null;
        ClearPendingAttachments();

        ResetConversationUi();
    }

    // ── Export / Save ─────────────────────────────────────────────────────────

    /// <summary>
    /// async void Click handler -- any unhandled exception here (including from
    /// SaveFilePickerAsync, which can throw on some platforms/cancellations) would propagate
    /// as an unhandled exception on the UI thread rather than being observable to any caller,
    /// so the whole body is wrapped in try/catch (grok review BLOCKER, 2026-06-23). The
    /// engine reference is also captured locally before the file-picker await -- a node
    /// switch (CbNode_SelectionChanged) landing during that await nulls the _engine field,
    /// which would otherwise NRE on _engine.Model/_engine.History below (grok review BLOCKER,
    /// 2026-06-23, same class of race already fixed for SendAsync).
    /// </summary>
    private async void BtnExport_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var engine = _engine;
            if (engine is null || engine.History.Count == 0) return;

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
            sb.AppendLine($"# OrcChat — {DateTime.Now:yyyy-MM-dd HH:mm}");
            sb.AppendLine($"Model: {engine.Model}");
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            foreach (var msg in engine.History)
            {
                sb.AppendLine(msg.Role switch
                {
                    OrchestratorIDE.Models.MessageRole.User      => $"**You:** {BuildUserBubbleMarkdown(msg.Content, msg.Attachments)}",
                    OrchestratorIDE.Models.MessageRole.Assistant => $"**Assistant:**\n\n{msg.Content}",
                    OrchestratorIDE.Models.MessageRole.Tool      => "> 🔧 *Tool result (hidden)*",
                    _                                             => ""
                });
                sb.AppendLine();
            }

            await using var stream = await file.OpenWriteAsync();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        }
        catch { /* non-fatal — storage write failure, or the file picker itself failed */ }
    }

    // ── Scroll helpers ────────────────────────────────────────────────────────

    private void ScrollToBottom()
        => Dispatcher.UIThread.InvokeAsync(() => ChatScroll.ScrollToEnd());

    private async void BtnAttach_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach to chat",
                AllowMultiple = true,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"] },
                    new FilePickerFileType("Text and Markdown") { Patterns = ["*.md", "*.markdown", "*.txt", "*.json", "*.yml", "*.yaml", "*.cs", "*.ts", "*.js", "*.py"] },
                    new FilePickerFileType("All files") { Patterns = ["*.*"] },
                ],
            });

            AddAttachmentsFromPaths(files.Select(f => f.TryGetLocalPath()).Where(p => !string.IsNullOrWhiteSpace(p))!);
        }
        catch { /* non-fatal picker failure */ }
    }

    internal void AddAttachmentsFromPaths(IEnumerable<string?> paths)
    {
        foreach (var rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath) || !File.Exists(rawPath)) continue;
            var attachment = ChatAttachment.FromPath(rawPath);
            if (_pendingAttachments.All(a => !a.FilePath.Equals(attachment.FilePath, StringComparison.OrdinalIgnoreCase)))
                _pendingAttachments.Add(attachment);
        }

        RefreshPendingAttachmentsUi();
    }

    private void RefreshPendingAttachmentsUi()
    {
        var items = PendingAttachmentsPanel.Items;
        items.Clear();
        PendingAttachmentsPanel.IsVisible = _pendingAttachments.Count > 0;

        foreach (var attachment in _pendingAttachments)
        {
            var remove = new Button
            {
                Content = "x",
                Background = Brushes.Transparent,
                Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0xAA, 0x60)),
                BorderThickness = new Avalonia.Thickness(0),
                Padding = new Avalonia.Thickness(4, 0),
                Tag = attachment.Id,
            };
            remove.Click += (_, _) => RemovePendingAttachment(attachment.Id);

            var chip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x14, 0x1A, 0x14)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x3A, 0x2A)),
                BorderThickness = new Avalonia.Thickness(1),
                CornerRadius = new Avalonia.CornerRadius(999),
                Padding = new Avalonia.Thickness(8, 4),
                Margin = new Avalonia.Thickness(0, 0, 6, 0),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"{(attachment.IsImage ? "Image" : "File")}: {attachment.DisplayName}",
                            Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xCC, 0x80)),
                            FontSize = 11,
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                        remove,
                    }
                }
            };

            items.Add(chip);
        }
    }

    private void RemovePendingAttachment(Guid attachmentId)
    {
        _pendingAttachments.RemoveAll(a => a.Id == attachmentId);
        RefreshPendingAttachmentsUi();
    }

    private void ClearPendingAttachments()
    {
        _pendingAttachments.Clear();
        RefreshPendingAttachmentsUi();
    }

    private static async Task<string> BuildPromptTextWithAttachmentsAsync(
        string text,
        IReadOnlyList<ChatAttachment> attachments,
        CancellationTokenSource? cts)
    {
        var sb = new StringBuilder(text);
        int remaining = 24_000;

        foreach (var attachment in attachments.Where(a => a.IsTextLike && File.Exists(a.FilePath)))
        {
            if (remaining <= 0) break;

            var content = await File.ReadAllTextAsync(attachment.FilePath, cts?.Token ?? CancellationToken.None);
            if (content.Length > remaining)
                content = content[..remaining];

            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine($"Attached file: {attachment.DisplayName}");
            sb.AppendLine("```");
            sb.AppendLine(content);
            sb.AppendLine("```");

            remaining -= content.Length;
        }

        foreach (var attachment in attachments.Where(a => !a.IsImage && !a.IsTextLike))
        {
            sb.AppendLine();
            sb.AppendLine($"Attached binary file: {attachment.DisplayName} ({attachment.ByteSize:N0} bytes)");
        }

        return sb.ToString();
    }

    private void OpenChatLink(string target)
    {
        try
        {
            if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                return;
            }

            if (File.Exists(target))
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { /* non-fatal convenience action */ }
    }

    private void DisposeEngineRuntime()
    {
        var runtime = _engineRuntime;
        _engineRuntime = null;

        switch (runtime)
        {
            case IAsyncDisposable asyncDisposable:
                _ = asyncDisposable.DisposeAsync();
                break;
            case IDisposable disposable:
                disposable.Dispose();
                break;
        }
    }
}
