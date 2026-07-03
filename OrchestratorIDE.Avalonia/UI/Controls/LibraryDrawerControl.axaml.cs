// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrchestratorIDE.Services;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// CF-5 Library drawer — code-behind-only (no MVVM framework), matching the rest of the
/// Avalonia UI's pattern of building dynamic content in C# (see ChatPanel.axaml.cs bubble
/// builders) rather than data-binding against a view model.
/// </summary>
public partial class LibraryDrawerControl : UserControl
{
    public event Action<string>? CorpusAttachRequested;
    public event Action? CorpusDetachRequested;
    public event Action<string>? StorageChangeRequested;

    private LibraryViewModel? _vm;
    private FabricIndexingOrchestrator? _orchestrator;
    private FabricWebImporter? _webImporter;
    private string? _attachedCorpusId;
    private bool _webFindMode;
    private IReadOnlyList<WebImportCandidate> _webResults = [];
    private readonly Dictionary<string, IndexProgressViewModel> _progressByDocument = [];
    private IReadOnlyList<ConversationNotebookEntry> _notebookEntries = [];
    private string? _lastError;

    // Spinner cycled while a document is in the Reading/Reducing stage -- native-model reads
    // take 15-20s per segment, so the per-segment count alone can look hung to a user watching
    // the card; this gives a cheap "still alive" cue independent of segment completion.
    private static readonly string[] IndexingSpinnerGlyphs = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private readonly DispatcherTimer _pulseTimer;
    private int _pulseTick;

    public LibraryDrawerControl()
    {
        InitializeComponent();
        BuildHeader();
        BuildStorageFooter();
        Render();

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _pulseTimer.Tick += (_, _) =>
        {
            _pulseTick++;
            if (_progressByDocument.Values.Any(p => p.Stage is IndexStageKind.Reading or IndexStageKind.Reducing))
                Render();
        };
        _pulseTimer.Start();
    }

    public void Attach(LibraryViewModel vm, FabricIndexingOrchestrator orchestrator, FabricWebImporter? webImporter)
    {
        _vm = vm;
        _orchestrator = orchestrator;
        _webImporter = webImporter;
        _vm.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Render);
        _vm.Corpora.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Render);
        _vm.SearchResults.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Render);
        Render();
    }

    public void SetAttachedCorpus(string? corpusId)
    {
        _attachedCorpusId = corpusId;
        Render();
    }

    public void UpdateNotebook(IReadOnlyList<ConversationNotebookEntry> entries)
    {
        _notebookEntries = entries;
        Render();
    }

    // ── Header: title + add-source dropdown + search box ───────────────────

    private void BuildHeader()
    {
        HeaderStack.Children.Clear();
        HeaderStack.Margin = new Thickness(12, 10, 12, 8);
        HeaderStack.Spacing = 8;

        var titleRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var title = new TextBlock
        {
            Text = "📚 Library",
            FontSize = 13,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(0xC8, 0xD4, 0xC8),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var addBtn = new Button
        {
            Content = "+ Add source ▾",
            Background = Brush(0x16, 0x26, 0x16),
            BorderBrush = Brush(0x2A, 0x4A, 0x2A),
            Foreground = Brush(0xC4, 0xE8, 0x9A),
            FontSize = 11.5,
            FontWeight = FontWeight.SemiBold,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 5),
            BorderThickness = new Thickness(1),
        };
        Avalonia.Automation.AutomationProperties.SetAutomationId(addBtn, "OrcChat.Library.AddSource");
        var fromFile = new MenuItem { Header = "From file…" };
        Avalonia.Automation.AutomationProperties.SetAutomationId(fromFile, "OrcChat.Library.AddFromFile");
        fromFile.Click += async (_, _) =>
        {
            try { await AddFromFileAsync(); }
            catch (Exception ex) { ShowError("Adding source from file", ex); }
        };
        var fromFolder = new MenuItem { Header = "From folder…" };
        fromFolder.Click += async (_, _) =>
        {
            try { await AddFromFolderAsync(); }
            catch (Exception ex) { ShowError("Adding sources from folder", ex); }
        };
        var findWeb = new MenuItem { Header = "Find on the web…" };
        findWeb.Click += (_, _) => { _webFindMode = true; Render(); };
        addBtn.ContextMenu = new ContextMenu { ItemsSource = new[] { fromFile, fromFolder, findWeb } };
        addBtn.Click += (_, _) => addBtn.ContextMenu?.Open(addBtn);

        Grid.SetColumn(title, 0);
        Grid.SetColumn(addBtn, 1);
        titleRow.Children.Add(title);
        titleRow.Children.Add(addBtn);
        HeaderStack.Children.Add(titleRow);

        var searchBox = new TextBox
        {
            PlaceholderText = "Search the library…",
            Background = Brush(0x0B, 0x0F, 0x0B),
            BorderBrush = Brush(0x1E, 0x2E, 0x1E),
            Foreground = Brush(0xD4, 0xD4, 0xD4),
            CornerRadius = new CornerRadius(7),
            FontSize = 11.5,
            Padding = new Thickness(10, 6),
        };
        searchBox.TextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.SearchQuery = searchBox.Text ?? "";
        };
        HeaderStack.Children.Add(searchBox);
    }

    private async Task AddFromFileAsync()
    {
        if (_vm is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add source to library",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Supported sources") { Patterns = ["*.pdf", "*.txt", "*.md"] },
            },
        });
        var file = files.Count > 0 ? files[0] : null;
        if (file is null) return;

        var corpusId = _attachedCorpusId ?? EnsureDefaultCorpus();
        await ImportAndIndexAsync(corpusId, file.Path.LocalPath);
    }

    private async Task AddFromFolderAsync()
    {
        if (_vm is null) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Add folder of sources to library",
            AllowMultiple = false,
        });
        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;

        var corpusId = _attachedCorpusId ?? EnsureDefaultCorpus();
        var dir = folder.Path.LocalPath;
        if (!Directory.Exists(dir)) return;

        foreach (var path in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(p => p.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ||
                                 p.EndsWith(".md", StringComparison.OrdinalIgnoreCase)))
        {
            await ImportAndIndexAsync(corpusId, path);
        }
    }

    private string EnsureDefaultCorpus()
    {
        var existing = _vm!.Corpora.FirstOrDefault();
        if (existing is not null) return existing.CorpusId;
        return _vm.CreateCorpus("Library").CorpusId;
    }

    private async Task ImportAndIndexAsync(string corpusId, string path)
    {
        if (_vm is null) return;
        try
        {
            var import = await _vm.ImportFileAsync(corpusId, path);
            BeginIndexing(import.Document.DocumentId);
        }
        catch
        {
            // Import failures surface via the document's own status on next Refresh().
        }
    }

    private void BeginIndexing(string documentId)
    {
        if (_orchestrator is null) return;
        // Re-entrancy guard -- without this, a double-click on "Re-index corpus" or a second
        // import landing on the same document while the first is still running fires two
        // concurrent IndexDocumentAsync calls racing the same SQLite rows (claims replace +
        // reduce), and the second BeginIndexing's progress VM orphans the first one's.
        if (_progressByDocument.TryGetValue(documentId, out var existing) &&
            existing.Stage is not IndexStageKind.Complete and not IndexStageKind.Failed)
        {
            return;
        }

        var progress = new IndexProgressViewModel(documentId);
        _progressByDocument[documentId] = progress;
        progress.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Render);

        var reporter = new Progress<IndexStageEvent>(progress.Apply);
        _ = _orchestrator.IndexDocumentAsync(documentId, readOnly: false, reporter)
            .ContinueWith(_ => Dispatcher.UIThread.Post(() => { _vm?.Refresh(); Render(); }));
    }

    /// <summary>
    /// Scoped retry for a document's previously-failed segments -- the "Repair" affordance,
    /// using FabricIndexingOrchestrator.RetryFailedAsync instead of re-reading every segment
    /// the way "Re-index corpus" (BeginIndexing) does.
    /// </summary>
    private void BeginRetry(string documentId, IReadOnlyList<string> failedSegmentIds)
    {
        if (_orchestrator is null) return;
        if (_progressByDocument.TryGetValue(documentId, out var existing) &&
            existing.Stage is not IndexStageKind.Complete and not IndexStageKind.Failed)
        {
            return;
        }

        var progress = new IndexProgressViewModel(documentId);
        _progressByDocument[documentId] = progress;
        progress.PropertyChanged += (_, _) => Dispatcher.UIThread.Post(Render);

        var reporter = new Progress<IndexStageEvent>(progress.Apply);
        _ = _orchestrator.RetryFailedAsync(documentId, failedSegmentIds, readOnly: false, reporter)
            .ContinueWith(_ => Dispatcher.UIThread.Post(() => { _vm?.Refresh(); Render(); }));
    }

    // ── Storage footer ───────────────────────────────────────────────────────

    private void BuildStorageFooter()
    {
        StorageFooter.BorderBrush = Brush(0x16, 0x20, 0x16);
        StorageFooter.Background = Brush(0x0A, 0x0D, 0x0A);
        StorageFooter.BorderThickness = new Thickness(0, 1, 0, 0);
        StorageFooter.Padding = new Thickness(13, 10);

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        var icon = new TextBlock { Text = "🗄", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        var pathText = new TextBlock
        {
            Text = _vm?.StoragePath ?? "",
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            FontSize = 10,
            Foreground = Brush(0x8A, 0x9A, 0x84),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var changeBtn = new Button
        {
            Content = "Change…",
            FontSize = 10.5,
            Padding = new Thickness(8, 3),
            Background = Brush(0x0E, 0x16, 0x0E),
            BorderBrush = Brush(0x1E, 0x2E, 0x1E),
            Foreground = Brush(0x9F, 0xB8, 0x90),
        };
        changeBtn.Click += async (_, _) => await ChangeStorageAsync(pathText);

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(pathText, 1);
        Grid.SetColumn(changeBtn, 2);
        row.Children.Add(icon);
        row.Children.Add(pathText);
        row.Children.Add(changeBtn);
        StorageFooter.Child = row;
    }

    private async Task ChangeStorageAsync(TextBlock pathText)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose library storage folder",
            AllowMultiple = false,
        });
        var folder = folders.Count > 0 ? folders[0] : null;
        if (folder is null) return;

        var newPath = folder.Path.LocalPath;
        pathText.Text = newPath;
        if (_vm is not null) _vm.StoragePath = newPath;
        StorageChangeRequested?.Invoke(newPath);
    }

    // ── Body: corpus cards / search results / notebook / web-find ──────────

    private void Render()
    {
        BodyStack.Children.Clear();

        if (_lastError is not null)
            BodyStack.Children.Add(BuildErrorBanner(_lastError));

        if (_webFindMode)
        {
            BodyStack.Children.Add(BuildWebFindPanel());
            return;
        }

        if (_vm is not null && _vm.SearchResults.Count > 0)
            BodyStack.Children.Add(BuildSearchResultsSection());

        foreach (var corpus in _vm?.Corpora ?? [])
            BodyStack.Children.Add(BuildCorpusCard(corpus));

        BodyStack.Children.Add(BuildNotebookSection());
    }

    /// <summary>
    /// Surfaces a failure from one of the async-void Click handlers below instead of letting
    /// it become an unhandled exception on the UI thread (the exact BLOCKER-class bug this
    /// codebase's own ChatPanel.CopyBubbleTextAsync comment documents fixing once already).
    /// </summary>
    private void ShowError(string action, Exception ex)
    {
        _lastError = $"{action} failed: {ex.Message}";
        Dispatcher.UIThread.Post(Render);
    }

    private Control BuildErrorBanner(string message)
    {
        var dismiss = new Button { Content = "✕", FontSize = 10, Padding = new Thickness(4), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Foreground = Brush(0xC0, 0x61, 0x4F) };
        dismiss.Click += (_, _) => { _lastError = null; Render(); };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new TextBlock { Text = message, FontSize = 10.5, Foreground = Brush(0xC0, 0x61, 0x4F), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(dismiss, 1);
        row.Children.Add(text);
        row.Children.Add(dismiss);
        return new Border
        {
            Background = Brush(0x1E, 0x11, 0x0E), BorderBrush = Brush(0x3A, 0x1E, 0x18), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), Margin = new Thickness(0, 0, 0, 6), Child = row,
        };
    }

    private Control BuildSearchResultsSection()
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock
        {
            Text = "SEARCH RESULTS",
            FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brush(0x5A, 0x6B, 0x5A),
            LetterSpacing = 1.2,
        });
        foreach (var hit in _vm!.SearchResults)
        {
            var row = new Border
            {
                Background = Brush(0x0E, 0x14, 0x0E),
                BorderBrush = Brush(0x1A, 0x24, 0x1A),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8),
                Child = new TextBlock
                {
                    Text = $"{hit.DisplayName} · {Snippet(hit.Text)}",
                    FontSize = 10.5, Foreground = Brush(0x8A, 0x9A, 0x84), TextWrapping = TextWrapping.Wrap,
                },
            };
            stack.Children.Add(row);
        }
        return stack;
    }

    private static string Snippet(string text) => text.Length <= 80 ? text : text[..80] + "…";

    private Control BuildCorpusCard(CorpusCardViewModel corpus)
    {
        var (bg, border) = corpus.Status switch
        {
            "indexing" => (Brush(0x12, 0x10, 0x0A), Brush(0x4A, 0x3C, 0x18)),
            "stale" => (Brush(0x0E, 0x12, 0x0E), Brush(0x44, 0x37, 0x14)),
            _ => (Brush(0x10, 0x1A, 0x10), Brush(0x2A, 0x4A, 0x2A)),
        };

        var stack = new StackPanel { Spacing = 6 };

        var headerRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var name = new TextBlock
        {
            Text = corpus.Name, FontSize = 12.5, FontWeight = FontWeight.SemiBold, Foreground = Brush(0xD8, 0xE4, 0xD0),
        };
        Grid.SetColumn(name, 0);
        headerRow.Children.Add(name);

        var statusPill = BuildStatusPill(corpus);
        Grid.SetColumn(statusPill, 1);
        headerRow.Children.Add(statusPill);
        stack.Children.Add(headerRow);

        stack.Children.Add(new TextBlock
        {
            Text = $"{corpus.TotalSegments} segments",
            FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
            FontSize = 10, Foreground = Brush(0x6E, 0x80, 0x68),
        });

        var actionsRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (corpus.Status == "ready")
        {
            var isAttached = string.Equals(corpus.CorpusId, _attachedCorpusId, StringComparison.Ordinal);
            var attachBtn = new Button
            {
                Content = isAttached ? "✕ Detach" : "Attach",
                FontSize = 10.5, Padding = new Thickness(8, 3),
                Background = isAttached ? Brush(0x1E, 0x11, 0x0E) : Brush(0x13, 0x25, 0x13),
                BorderBrush = isAttached ? Brush(0x3A, 0x1E, 0x18) : Brush(0x2F, 0x4F, 0x2F),
                Foreground = isAttached ? Brush(0xC0, 0x61, 0x4F) : Brush(0x9F, 0xD3, 0x7F),
            };
            Avalonia.Automation.AutomationProperties.SetAutomationId(attachBtn, isAttached ? "OrcChat.Library.Detach" : "OrcChat.Library.Attach");
            attachBtn.Click += (_, _) =>
            {
                if (isAttached) CorpusDetachRequested?.Invoke();
                else CorpusAttachRequested?.Invoke(corpus.CorpusId);
            };
            actionsRow.Children.Add(attachBtn);
        }
        else if (corpus.Status == "stale")
        {
            var reindexBtn = new Button
            {
                Content = "↻ Re-index corpus",
                FontSize = 10.5, Padding = new Thickness(8, 3),
                Background = Brush(0x1A, 0x14, 0x0B), BorderBrush = Brush(0x44, 0x37, 0x14), Foreground = Brush(0xC9, 0x8A, 0x4B),
            };
            reindexBtn.Click += (_, _) =>
            {
                foreach (var doc in corpus.Documents)
                    ReindexDocumentAsync(doc.DocumentId);
            };
            actionsRow.Children.Add(reindexBtn);
        }
        if (actionsRow.Children.Count > 0)
            stack.Children.Add(actionsRow);

        var activeProgress = _progressByDocument.Values.FirstOrDefault(p =>
            corpus.Documents.Any(d => d.DocumentId == p.DocumentId) &&
            p.Stage is not IndexStageKind.Complete and not IndexStageKind.Failed);
        if (activeProgress is not null)
        {
            var progressRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
            if (activeProgress.Stage is IndexStageKind.Reading or IndexStageKind.Reducing)
            {
                progressRow.Children.Add(new TextBlock
                {
                    Text = IndexingSpinnerGlyphs[_pulseTick % IndexingSpinnerGlyphs.Length],
                    FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                    FontSize = 10, Foreground = Brush(0x8A, 0x7E, 0x60),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            progressRow.Children.Add(new TextBlock
            {
                Text = activeProgress.StageLabel,
                FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                FontSize = 10, Foreground = Brush(0x8A, 0x7E, 0x60),
            });
            stack.Children.Add(progressRow);
        }

        // A completed run can still have left some segments failed -- offer the scoped
        // retry (FabricIndexingOrchestrator.RetryFailedAsync) instead of forcing a full
        // re-read of every segment via "Re-index corpus".
        var failedProgress = _progressByDocument.Values.FirstOrDefault(p =>
            corpus.Documents.Any(d => d.DocumentId == p.DocumentId) &&
            p.Stage == IndexStageKind.Complete && p.FailedSegmentIds.Count > 0);
        if (failedProgress is not null)
        {
            var repairRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 4, 0, 0) };
            var failedLabel = new TextBlock
            {
                Text = $"{failedProgress.FailedSegmentIds.Count} failed segments",
                FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                FontSize = 10, Foreground = Brush(0xD6, 0xA8, 0x5A), VerticalAlignment = VerticalAlignment.Center,
            };
            var repairBtn = new Button
            {
                Content = "Repair", FontSize = 10.5, Padding = new Thickness(8, 3),
                Background = Brush(0x1A, 0x14, 0x0B), BorderBrush = Brush(0x44, 0x37, 0x14), Foreground = Brush(0xC9, 0x8A, 0x4B),
            };
            var documentId = failedProgress.DocumentId;
            var failedIds = failedProgress.FailedSegmentIds.ToArray();
            repairBtn.Click += (_, _) => BeginRetry(documentId, failedIds);
            Grid.SetColumn(failedLabel, 0);
            Grid.SetColumn(repairBtn, 1);
            repairRow.Children.Add(failedLabel);
            repairRow.Children.Add(repairBtn);
            stack.Children.Add(repairRow);
        }

        return new Border
        {
            Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9), Padding = new Thickness(11, 12), Child = stack,
        };
    }

    private void ReindexDocumentAsync(string documentId) => BeginIndexing(documentId);

    private static Border BuildStatusPill(CorpusCardViewModel corpus)
    {
        var (text, fg, bg, border) = corpus.Status switch
        {
            "indexing" => ("● Indexing", Brush(0xD6, 0xA8, 0x5A), Brush(0x22, 0x1B, 0x0C), Brush(0x4A, 0x3C, 0x18)),
            "stale" => ("↻ Stale", Brush(0xC9, 0x8A, 0x4B), Brush(0x1A, 0x14, 0x0B), Brush(0x44, 0x37, 0x14)),
            "failed" => ("⚠ Failed", Brush(0xC0, 0x61, 0x4F), Brush(0x1E, 0x11, 0x0E), Brush(0x3A, 0x1E, 0x18)),
            _ => ("● Ready", Brush(0x9F, 0xD3, 0x7F), Brush(0x13, 0x25, 0x13), Brush(0x2F, 0x4F, 0x2F)),
        };
        return new Border
        {
            Background = bg, BorderBrush = border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(999), Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text, FontFamily = new FontFamily("JetBrains Mono, Consolas, monospace"),
                FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = fg,
            },
        };
    }

    private Control BuildNotebookSection()
    {
        var stack = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(new TextBlock
        {
            Text = "NOTEBOOK · THIS CHAT",
            FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Brush(0x5A, 0x6B, 0x5A), LetterSpacing = 1.2,
        });
        foreach (var entry in _notebookEntries.TakeLast(5))
        {
            stack.Children.Add(new Border
            {
                BorderBrush = Brush(0x24, 0x34, 0x24), BorderThickness = new Thickness(2, 0, 0, 0),
                Padding = new Thickness(8, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = entry.ClaimText, FontSize = 10.5, Foreground = Brush(0x8A, 0x9A, 0x84), TextWrapping = TextWrapping.Wrap,
                },
            });
        }
        return stack;
    }

    // ── Web-find sub-view ─────────────────────────────────────────────────

    private Control BuildWebFindPanel()
    {
        var stack = new StackPanel { Spacing = 8 };

        var backBtn = new Button { Content = "← Back to library", FontSize = 10.5, Background = Avalonia.Media.Brushes.Transparent };
        backBtn.Click += (_, _) => { _webFindMode = false; _webResults = []; Render(); };
        stack.Children.Add(backBtn);

        var input = new TextBox { PlaceholderText = "Book title, author, or URL…", FontSize = 11.5 };
        var searchBtn = new Button { Content = "Search", FontSize = 11 };
        searchBtn.Click += async (_, _) =>
        {
            if (_webImporter is null || string.IsNullOrWhiteSpace(input.Text)) return;
            try
            {
                _webResults = await _webImporter.SearchAsync(input.Text!).ConfigureAwait(true);
                Render();
            }
            catch (Exception ex) { ShowError("Web search", ex); }
        };
        stack.Children.Add(input);
        stack.Children.Add(searchBtn);

        foreach (var candidate in _webResults)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock { Text = candidate.Title, FontSize = 11.5, Foreground = Brush(0xD8, 0xE4, 0xD0), TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = $"{candidate.Format.ToUpperInvariant()} · {candidate.Attribution}", FontSize = 10, Foreground = Brush(0x6E, 0x80, 0x68) });
            var addBtn = new Button { Content = "Add to library", FontSize = 10.5 };
            addBtn.Click += async (_, _) =>
            {
                if (_vm is null) return;
                try
                {
                    var corpusId = _attachedCorpusId ?? EnsureDefaultCorpus();
                    var result = await _webImporter!.DownloadAndImportAsync(corpusId, candidate).ConfigureAwait(true);
                    _vm.Refresh();
                    BeginIndexing(result.ImportResult.Document.DocumentId);
                    _webFindMode = false;
                    _webResults = [];
                    Render();
                }
                catch (Exception ex) { ShowError($"Downloading '{candidate.Title}'", ex); }
            };
            row.Children.Add(addBtn);
            stack.Children.Add(new Border
            {
                Background = Brush(0x0E, 0x14, 0x0E), BorderBrush = Brush(0x1A, 0x24, 0x1A), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7), Padding = new Thickness(8), Child = row,
            });
        }

        stack.Children.Add(new TextBlock
        {
            Text = "Only add content you are licensed to use. OrcChat does not redistribute corpus content outside this machine.",
            FontSize = 9.5, Foreground = Brush(0x5E, 0x6E, 0x5A), TextWrapping = TextWrapping.Wrap,
        });

        return stack;
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b) => new(Color.FromRgb(r, g, b));
}
