# TheOrc — Avalonia Migration

Cross-platform UI migration: WPF (`net10.0-windows`) → Avalonia 12 (`net10.0`)

**Branch:** `feature/avalonia`  
**Target version:** v1.7.0  
**Started:** 2026-06-15

---

## Status at a glance

| Phase | Name | Status |
|-------|------|--------|
| 0 | Scaffold — blank Avalonia window | 🟡 In progress |
| 1 | Service layer decoupling | ⬜ Not started |
| 2 | Code editor (AvalonEditB → AvaloniaEdit) | ⬜ Not started |
| 3A | Panels — batch A (simple) | ⬜ Not started |
| 3B | Panels — batch B (medium) | ⬜ Not started |
| 3C | Panels — batch C (complex) | ⬜ Not started |
| 4 | Dialogs, windows, controls | ⬜ Not started |
| 5 | MainWindow + App (full IDE layout) | ⬜ Not started |
| 6 | Markdown renderer | ⬜ Not started |
| 7 | Review, tests, ship v1.7.0 | ⬜ Not started |

---

## Project structure

```
OrchestratorIDE/            ← WPF project (net10.0-windows) — lives on master, stays as reference
OrchestratorIDE.Avalonia/   ← NEW Avalonia project (net10.0) — feature/avalonia branch
OrchestratorIDE.Daemon/     ← Headless daemon (net10.0) — unchanged throughout
OrchestratorIDE.UnitTests/  ← Headless unit tests — updated in Phase 7
OrchestratorIDE.UITests/    ← Headless UI tests — updated in Phase 7
```

Services, Core, Models, and HIVE files are shared from the WPF project into the Avalonia project via `<Compile Include>` (same pattern as the Daemon) — **no separate library project, no large refactor**.

---

## Phase 0 — Scaffold

**Goal:** `dotnet run` from `OrchestratorIDE.Avalonia/` produces a blank branded window. Daemon still builds. 121/121 tests green.

**Files created:**

| File | Notes |
|------|-------|
| `OrchestratorIDE.Avalonia/OrchestratorIDE.Avalonia.csproj` | Avalonia 12.0.4, net10.0, no -windows |
| `OrchestratorIDE.Avalonia/Program.cs` | `AppBuilder.Configure<App>().UsePlatformDetect()` |
| `OrchestratorIDE.Avalonia/App.axaml` | FluentTheme dark + full brand colour palette |
| `OrchestratorIDE.Avalonia/App.axaml.cs` | `AvaloniaXamlLoader.Load` + `MainWindow` wiring |
| `OrchestratorIDE.Avalonia/MainWindow.axaml` | Blank branded window, status bar placeholder |
| `OrchestratorIDE.Avalonia/MainWindow.axaml.cs` | `InitializeComponent()` in constructor |
| `OrchestratorIDE.slnx` | Added `OrchestratorIDE.Avalonia` project |

**Codex findings:**

| Severity | Finding | Resolution |
|----------|---------|-----------|
| BLOCKER | `MainWindow` missing `InitializeComponent()` — XAML never loads | ✅ Fixed: constructor calls `InitializeComponent()` |

**Gate:** Build clean ✅ · Daemon clean ✅ · 121/121 ✅ · Codex BLOCKER fixed ✅ · **Commit pending**

---

## Phase 1 — Service layer decoupling

**Goal:** All 54 WPF-coupled C# service/core files compile under `net10.0` (no `System.Windows` references). `dotnet build` of the Avalonia project clean. Tests still green.

**Approach:** mass-replace via Codex, file by file:

| WPF API | Avalonia replacement |
|---------|---------------------|
| `System.Windows.Threading.Dispatcher.Invoke/InvokeAsync` | `Avalonia.Threading.Dispatcher.UIThread.InvokeAsync` |
| `Application.Current.Dispatcher` | `Avalonia.Threading.Dispatcher.UIThread` |
| `System.Windows.MessageBox.Show(...)` | Static helper → `await MessageBoxHelper.ShowAsync(msg)` using Avalonia's `IMsBoxWindow` from `MsBox.Avalonia` |
| `Microsoft.Win32.SaveFileDialog` / `OpenFileDialog` | `StorageProvider.SaveFilePickerAsync` / `OpenFilePickerAsync` |
| `System.Windows.Media.BitmapImage` | `Avalonia.Media.Imaging.Bitmap` |
| `System.Windows.Media.Imaging.WriteableBitmap` | `Avalonia.Media.Imaging.WriteableBitmap` |
| `System.Windows.Media.Imaging.RenderTargetBitmap` | `Avalonia.Media.Imaging.RenderTargetBitmap` |
| `System.Windows.Visibility` enum | `bool` + `IsVisible` property in Avalonia |
| `System.Windows.Media.SolidColorBrush` | `Avalonia.Media.SolidColorBrush` |
| `System.Windows.Media.Color` | `Avalonia.Media.Color` |
| `ProtectedData` (DPAPI) | Already abstracted via `SecretProtection.Current` — no change |
| `SharpAvi` capture | `#if WINDOWS` guard — stubs on Linux/macOS (Phase 5) |
| `Research/MarkdownFlowDocument.cs` (FlowDocument) | Stub — replaced in Phase 6 |

**Files to migrate (54 total):**

*Core services (8):*
- [ ] `Core/ScreenRecorder.cs` — WPF Dispatcher + RenderTargetBitmap (`#if WINDOWS` guard)
- [ ] `Core/ToolRegistry.cs` — Dispatcher
- [ ] `Core/LlamaServerManager.cs` — Dispatcher
- [ ] `Services/PlanExecutorService.cs` — Dispatcher
- [ ] `Services/SelfUpdater.cs` — MessageBox
- [ ] `Research/MarkdownFlowDocument.cs` — FlowDocument (stub, Phase 6)
- [ ] `Services/Models/ModelDownloadService.cs` — Dispatcher + MessageBox
- [ ] `Agents/SwarmSession.cs` — Application.Current + Dispatcher

*HIVE services (already platform-neutral post-v1.6.x — verify only):*
- [ ] `Services/Hive/*.cs` (all) — confirm no remaining `System.Windows` refs after ShutdownCallback

*Tools (3):*
- [ ] `Tools/FileTools.cs` — Dispatcher (approval dialogs)
- [ ] `Tools/ShellTools.cs` — Dispatcher
- [ ] `Tools/SearchTools.cs` — Dispatcher

*All 27 panel/dialog/window code-behind files* — dependency only (no direct WPF logic); added to compile-include list in Phase 3–4.

**Gate:** `dotnet build OrchestratorIDE.Avalonia` clean (shared service files compile) · 121/121 green

---

## Phase 2 — Code editor

**Goal:** `CodeEditorPanel` and `ToolEditorPanel` running under Avalonia with syntax highlighting.

**Package swap:**

| Old | New |
|-----|-----|
| `AvalonEditB 1.2.0` (WPF-only) | `Avalonia.AvaloniaEdit 12.0.0` (same authors, official port) |

**Files:**
- [ ] `UI/Panels/CodeEditorPanel.axaml` + `.axaml.cs` — migrate XAML namespace, swap `TextEditor` control
- [ ] `UI/Panels/ToolEditorPanel.axaml` + `.axaml.cs` — same

**Key API differences (AvalonEditB → AvaloniaEdit):**

| WPF | Avalonia |
|-----|---------|
| `ICSharpCode.AvalonEditB.TextEditor` | `AvaloniaEdit.TextEditor` |
| `SyntaxHighlighting` property | Same name, same `HighlightingManager.Instance` |
| `TextArea.Caret.Line` | Same |
| Code folding strategy | `FoldingManager.Install(editor.TextArea)` — same API |

**Gate:** File loads in editor, syntax highlighting renders, Roslyn diagnostics in ToolEditorPanel intact

---

## Phase 3 — Panels

**Goal:** All 13 panels converted. Each is added to the Avalonia project via `<Compile Include>` and its XAML file converted to `.axaml`.

### Batch A — Simple (no streaming, no complex state)
- [ ] `UI/Panels/FileExplorerPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/SettingsPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/CheckpointBrowserPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/SessionBrowserPanel.axaml` + `.axaml.cs`

### Batch B — Medium (async updates, event wiring)
- [ ] `UI/Panels/AgentPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/ChatPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/UpdatePanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/WarmUpEditorWindow.axaml` + `.axaml.cs`

### Batch C — Complex (HIVE integration, swarm state, training pipeline)
- [ ] `UI/Panels/HivePanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/SwarmBoardPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/PitBossPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/TrainingPitPanel.axaml` + `.axaml.cs`
- [ ] `UI/Panels/ToolEditorPanel.axaml` + `.axaml.cs`

**Gate per batch:** panel instantiates, renders, key interactions work (no crash)

---

## Phase 4 — Dialogs, windows, controls

**Goal:** All secondary windows and reusable controls converted.

### Dialogs (5)
- [ ] `UI/Dialogs/AgentBuilderDialog.axaml` + `.axaml.cs`
- [ ] `UI/Dialogs/GlobalAgentDialog.axaml` + `.axaml.cs`
- [ ] `UI/Dialogs/SandboxBypassDialog.axaml` + `.axaml.cs`
- [ ] `UI/Dialogs/SelfUpdateDialog.axaml` + `.axaml.cs`
- [ ] `UI/Dialogs/WorkspaceRulesDialog.axaml` + `.axaml.cs`

### Specialized windows (7)
- [ ] `UI/Windows/HelpWindow.axaml` + `.axaml.cs`
- [ ] `UI/Windows/ModelCapabilityTestDialog.axaml` + `.axaml.cs`
- [ ] `UI/Windows/ModelCompareWindow.axaml` + `.axaml.cs`
- [ ] `UI/Windows/ModelDownloaderWindow.axaml` + `.axaml.cs`
- [ ] `UI/Windows/ModelLibraryWindow.axaml` + `.axaml.cs`
- [ ] `UI/Windows/ModelWikiWindow.axaml` + `.axaml.cs`
- [ ] `UI/Windows/WarmUpEditorWindow.axaml` + `.axaml.cs`

### User controls (6)
- [ ] `UI/Controls/CommandPalette.axaml` + `.axaml.cs`
- [ ] `UI/Controls/DiffViewer.axaml` + `.axaml.cs`
- [ ] `UI/Controls/ModelPickerPopup.axaml` + `.axaml.cs`
- [ ] `UI/Controls/ShellApprovalCard.axaml` + `.axaml.cs`
- [ ] `UI/Controls/UnknownToolCard.axaml` + `.axaml.cs`
- [ ] `UI/Controls/UserInputDialog.axaml` + `.axaml.cs`

**Gate:** All open/close correctly, interactions work

---

## Phase 5 — MainWindow + App (full IDE layout)

**Goal:** Full IDE layout wired. App boots with all panels. HIVE starts. Keyboard shortcuts work.

**Files:**
- [ ] `MainWindow.axaml` — full layout: activity bar + sidebar + main content area
- [ ] `MainWindow.axaml.cs` — service wiring (2 589-line migration, largest single file)
- [ ] `App.axaml` — full style overrides (ContextMenu, ToggleButton, MenuItem, etc.)
- [ ] `FirstRunWindow.axaml` + `.axaml.cs`

**Key WPF → Avalonia substitutions in MainWindow:**

| WPF | Avalonia |
|-----|---------|
| `KeyDown` + `e.Key == Key.F1` | `KeyBindings` collection in XAML or `AddHandler(KeyDownEvent, ...)` |
| `RenderTargetBitmap` (screenshot) | `Avalonia.Media.Imaging.RenderTargetBitmap` |
| `Application.Current.Shutdown()` | `(Application.Current.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()` — already abstracted via `ShutdownCallback` |
| `Style.Triggers` / `ControlTemplate.Triggers` | Avalonia `ControlTheme` + `Styles` with `:pointerover`, `:checked`, `:disabled` pseudo-classes |
| `DropShadowEffect` | `BoxShadow` property on `Border` |
| `Dispatcher.InvokeAsync` | `Dispatcher.UIThread.InvokeAsync` |
| `Visibility.Collapsed` | `IsVisible = false` |

**Screen recording (`ScreenRecorder.cs`):**
- Windows: SharpAvi stays, `RenderTargetBitmap` replaced with Avalonia equivalent
- Linux/macOS: stub that logs "recording not supported on this platform" — Phase 5 ships this as `#if WINDOWS`
- Full cross-platform capture is a separate future feature

**Gate:** Full app boots, all panels switch, HIVE mesh starts, F12 records on Windows

---

## Phase 6 — Markdown renderer

**Goal:** AI response messages render with headings, code blocks, bold/italic, lists.

**Replacement for `MarkdownFlowDocument.cs` (WPF FlowDocument):**

| Option | Notes |
|--------|-------|
| `Markdown.Avalonia` NuGet | Ready-to-use Avalonia markdown control — preferred |
| `MarkdownSharp` + custom TextBlock rendering | Fallback if Markdown.Avalonia has gaps |

**Files:**
- [ ] `Research/MarkdownFlowDocument.cs` — rewrite or replace with `Markdown.Avalonia` control
- [ ] `UI/Panels/AgentPanel.axaml` — wire markdown renderer for AI message bubbles
- [ ] `UI/Panels/ChatPanel.axaml` — same

**Gate:** AI response with code block, heading, and list renders correctly

---

## Phase 7 — Review, tests, ship

**Goal:** All tests green under Avalonia, Codex review clean, v1.7.0 tagged.

- [ ] Update `OrchestratorIDE.UnitTests` to reference Avalonia project
- [ ] Update `OrchestratorIDE.UITests` — Avalonia headless test mode (`HeadlessUnitTestSession`)
- [ ] Final Codex review of entire `feature/avalonia` branch diff
- [ ] 121/121+ green
- [ ] Push branch, open PR, merge to master
- [ ] Tag v1.7.0

---

## Key decisions log

| Date | Decision | Reason |
|------|----------|--------|
| 2026-06-15 | Separate `OrchestratorIDE.Avalonia` project, not retarget WPF in place | WPF project stays as reference during migration; incremental panel migration without breakage |
| 2026-06-15 | Avalonia 12.0.4 (latest stable) | Matches `Avalonia.AvaloniaEdit 12.0.0` |
| 2026-06-15 | `<Compile Include>` for service sharing (no separate Core library) | Same pattern as Daemon — zero refactoring of existing service layer |
| 2026-06-15 | SharpAvi stays with `#if WINDOWS` guard | Cross-platform capture is a separate future feature, not a blocker |
| 2026-06-15 | `Markdown.Avalonia` for Phase 6 | FlowDocument has no Avalonia equivalent; purpose-built package preferred |

---

## WPF → Avalonia XAML cheat sheet

> Use this as a quick-reference during panel conversions.

### Namespace swap (every XAML file)
```xml
<!-- WPF -->
xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"

<!-- Avalonia -->
xmlns="https://github.com/avaloniaui"
```

### Common control renames
| WPF | Avalonia | Notes |
|-----|---------|-------|
| `<TextBlock>` | `<TextBlock>` | Same |
| `<TextBox>` | `<TextBox>` | Same |
| `<Button>` | `<Button>` | Same |
| `<StackPanel>` | `<StackPanel>` | Same |
| `<Grid>` | `<Grid>` | Same |
| `<Border>` | `<Border>` | Same; `CornerRadius` same |
| `<ScrollViewer>` | `<ScrollViewer>` | Same |
| `<ListBox>` | `<ListBox>` | Same |
| `<TreeView>` | `<TreeView>` | Same |
| `<ComboBox>` | `<ComboBox>` | Same |
| `<ToggleButton>` | `<ToggleButton>` | Same |
| `<CheckBox>` | `<CheckBox>` | Same |
| `<Separator>` | `<Separator>` | Same |
| `<Menu>` | `<Menu>` | Same |
| `<MenuItem>` | `<MenuItem>` | Same |
| `<ContextMenu>` | `<ContextMenu>` | Same |
| `<Popup>` | `<Popup>` | Same |
| `<Canvas>` | `<Canvas>` | Same |
| `<ItemsControl>` | `<ItemsControl>` | Same |
| `<TabControl>` | `<TabControl>` | Same |
| `<Expander>` | `<Expander>` | Same |
| `<ProgressBar>` | `<ProgressBar>` | Same |

### Property differences
| WPF | Avalonia |
|-----|---------|
| `Visibility="Collapsed"` | `IsVisible="False"` |
| `HorizontalAlignment="Stretch"` | Same |
| `TextWrapping="Wrap"` | Same |
| `FontWeight="Bold"` | Same |
| `SnapsToDevicePixels="True"` | `RenderOptions.BitmapInterpolationMode="None"` or omit |
| `Effect` → `DropShadowEffect` | `BoxShadow` on `Border` |
| `AllowsTransparency="True"` on Window | `TransparencyLevelHint="Transparent"` |
| `WindowStyle="None"` | `SystemDecorations="None"` |

### Style differences
| WPF | Avalonia |
|-----|---------|
| `<Style TargetType="Button">` | `<Style Selector="Button">` |
| `<Style.Triggers>` | Avalonia `Styles` with pseudo-class selectors |
| `<Trigger Property="IsMouseOver" Value="True">` | `<Style Selector="Button:pointerover">` |
| `<Trigger Property="IsChecked" Value="True">` | `<Style Selector="ToggleButton:checked">` |
| `<Trigger Property="IsEnabled" Value="False">` | `<Style Selector="Button:disabled">` |
| `ControlTemplate.Triggers` | `ControlTheme` |
| `<Setter TargetName="x">` | Not needed — selectors target the element directly |

### Data binding
```xml
<!-- WPF -->
{Binding Path=SomeProperty, RelativeSource={RelativeSource AncestorType=Window}}

<!-- Avalonia -->
{Binding SomeProperty, RelativeSource={RelativeSource AncestorType=Window}}
```
(`Path=` is optional in both but Avalonia omits it by convention)

---

## Scope summary

| Category | Count | Phase |
|----------|-------|-------|
| C# service/core files (WPF refs) | 54 | 1 |
| XAML files total | 37 | 0–5 |
| — Panels | 13 | 3 |
| — Dialogs | 5 | 4 |
| — Specialized windows | 7 | 4 |
| — User controls | 6 | 4 |
| — Root windows | 3 | 5 |
| — Test windows | 2 | 7 |
| NuGet package swaps | 2 | 0+2 |
| New NuGet packages | 1 (Markdown.Avalonia) | 6 |
