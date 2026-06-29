# ChatPanel.axaml.cs — CF-5 Edit Instructions

> File: OrchestratorIDE.Avalonia/UI/Panels/ChatPanel.axaml.cs (currently 48 KB)
> Do NOT rewrite the whole file. Make these targeted additions only.

## 1. New private fields (add to the "State" region)

```csharp
// ── CF-5 Library state ────────────────────────────────────────────────────
private CorpusAttachmentState? _corpus;   // null = plain chat; non-null = source-bound
private LibraryViewModel?      _libraryVm;
private FabricAskService?      _askService;
private FabricIndexingOrchestrator? _indexingOrchestrator;
private string _notebookPath = "";
```

## 2. New public setters (add to "Public API" region)

```csharp
// Called by MainWindow after constructing the panel, same lifecycle as SetModels().
public void SetFabricServices(
    FabricAskService askService,
    FabricIndexingOrchestrator indexingOrchestrator,
    LibraryViewModel libraryVm,
    string workspaceRoot)
{
    _askService            = askService;
    _indexingOrchestrator  = indexingOrchestrator;
    _libraryVm             = libraryVm;
    _notebookPath          = ConversationNotebookStore.StorePath(workspaceRoot, "default");
    // Wire library view model to the drawer controls (see AXAML section below)
    RefreshLibraryDrawer();
}
```

## 3. Modify SendAsync — corpus routing branch

Add this branch at the top of SendAsync, BEFORE the engine.SendAsync call:

```csharp
// ── CF-5: route through FabricAskService when a corpus is attached ───────
if (_corpus is not null && _askService is not null)
{
    await SendFabricAsync(_corpus, text, cts.Token);
    return; // skip plain ChatEngine path
}
```

## 4. New method: SendFabricAsync

Add after SendAsync:

```csharp
private async Task SendFabricAsync(
    CorpusAttachmentState corpus,
    string question,
    CancellationToken ct)
{
    if (_askService is null) return;

    try
    {
        var result = await _askService.AskAsync(question, corpus.CorpusId, corpus.Mode, ct)
            .ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamBox is null) return;

            // Build the cited answer bubble (replaces the streaming textbox)
            var bubble = _streamBox.Parent as Border;
            _streamBox = null;
            if (bubble is null) return;

            bubble.Child = BuildCitedAnswerView(result, corpus);
            bubble.Tag   = result.Answer;
        });
    }
    catch (OperationCanceledException) { /* user cancelled */ }
    catch (Exception ex)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_streamBox is not null)
                _streamBox.Text = $"⚠ Library ask failed: {ex.Message}";
        });
    }
}
```

## 5. New method: BuildCitedAnswerView

This builds the rich cited answer control. Returns a StackPanel containing:
- Stale/incomplete banner (if applicable)
- Medical policy banner (if policyProfile != "default")
- Answer prose with inline citation superscripts (use MarkdownView with citation chips)
- Coverage + verification line
- Citation footnote rows

```csharp
private Control BuildCitedAnswerView(FabricAskResult result, CorpusAttachmentState corpus)
{
    var stack = new StackPanel { Spacing = 0 };

    // Stale / incomplete banner
    if (corpus.IsStale || result.SegmentsConsidered < result.SegmentsTotal * 0.5)
    {
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x22, 0x1B, 0x0C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x3C, 0x18)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6),
            Child = new TextBlock
            {
                Text = $"⚠ Index incomplete — answers may miss sections. Coverage: {result.SegmentsConsidered}/{result.SegmentsTotal} segments.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xA8, 0x5A)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap
            }
        });
    }

    // Medical policy banner
    if (!string.Equals(corpus.PolicyProfile, "default", StringComparison.OrdinalIgnoreCase))
    {
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x16, 0x1C)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x42, 0x56)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 6),
            Child = new TextBlock
            {
                Text = "Educational interpretation of the cited source — not professional advice.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xC4, 0xD8)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap
            }
        });
    }

    // Budget overflow notice
    if (!result.FitsBudget)
    {
        stack.Children.Add(new Border
        {
            Padding = new Thickness(12, 5),
            Child = new TextBlock
            {
                Text = "Evidence truncated by token budget — ask a narrower question or switch to Study.",
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xA8, 0x5A)),
                FontSize = 11, TextWrapping = TextWrapping.Wrap
            }
        });
    }

    // Answer prose  
    stack.Children.Add(new Padding(12, 10) { Child = new MarkdownView
    {
        Text = result.Answer,
        LinkClicked = OpenChatLink
    }});

    // Coverage + verification line (see design tokens for exact colors)
    stack.Children.Add(BuildCoverageLine(result));

    // Citation footnotes
    var citIndices = new List<CitationViewModel>();
    foreach (var claim in result.Claims)
        foreach (var cit in BuildCitationViewModels(claim))
            citIndices.Add(cit);

    foreach (var cit in citIndices.DistinctBy(c => c.Index))
        stack.Children.Add(BuildFootnoteRow(cit));

    return stack;
}
```

## 6. Citation click → source preview pane

When a citation footnote row is clicked, open the source preview pane:

```csharp
private void OpenSourcePreview(CitationViewModel cit)
{
    // Show the right-side source preview panel (add SourcePreviewPanel to ChatPanel layout)
    SourcePreviewPanel.IsVisible = true;
    SourcePreviewPanel.LoadCitation(cit, _libraryVm);
}
```

## 7. AXAML changes (ChatPanel.axaml)

Add inside the main Grid/DockPanel, before the existing chat area:
- A 312 px left `Grid.Column` for the library drawer (contains LibraryDrawerControl)
- A GridSplitter (3 px)  
- A 372 px right column for SourcePreviewPanel (Visibility=Collapsed by default)

Create two new UserControls:
- `UI/Controls/LibraryDrawerControl.axaml(.cs)`
- `UI/Controls/SourcePreviewPanel.axaml(.cs)`

Follow the same code-behind-only (no MVVM framework) style as existing controls.

## 8. MainWindow.axaml.cs — wire services

In the region where ChatPanel is constructed and SetModels is called, add:

```csharp
var fabricRepo     = new FabricLibraryRepository(sqliteStore);
var graphRepo      = new DocumentGraphRepository(sqliteStore);
var fabricArtifacts = new ContentAddressedStore(Path.Combine(workspaceRoot, ".orc", "fabric", "objects"));
var libraryService = new FabricLibraryService(fabricRepo, fabricArtifacts);
var searchService  = new FabricSearchService(fabricRepo, graphRepo);
var planner        = new FabricQueryPlanner(searchService, graphRepo);
var packBuilder    = new EvidencePackBuilder(fabricRepo, graphRepo);
var verifier       = new FabricCitationVerifier(fabricRepo);
var nativeReader   = new FabricNativeReaderService(fabricRepo, graphRepo, nativeRoleRuntime);
var reducer        = new FabricReducer(/* see existing FabricReducer constructor */);
var graphImporter  = new FabricEvidenceGraphImporter(fabricRepo, graphRepo);
var askService     = new FabricAskService(planner, packBuilder, verifier, fabricRepo, nativeRoleRuntime);
var orchestrator   = new FabricIndexingOrchestrator(libraryService, nativeReader, reducer, graphImporter, fabricRepo, graphRepo);
var libraryVm      = new LibraryViewModel(libraryService, Path.Combine(workspaceRoot, ".orc", "fabric"));

ChatPanelInstance.SetFabricServices(askService, orchestrator, libraryVm, workspaceRoot);
```
