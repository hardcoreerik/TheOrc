# OrchestratorIDE — Project Log

> Native C# WPF AI coding assistant. Windows-only. Local Ollama models. Zero subscription cost.
> **Project root:** `F:\Ai\OrchestratorIDE\OrchestratorIDE\`
> **Run command:** `dotnet run` from project root, OR launch the exe directly
> **Build single exe:** `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`
> **Ollama host:** `http://192.168.1.15:11434` (NEWCOREPC, RTX 5070 Ti 16GB)

---

## Quick-Pickup Summary (read this first after a context break)

**Current state: App builds and RUNS. Phase 1 + 2 substantially complete.**

The goal is a **standout native AI coding IDE** — not browser-based, not Electron. Built in C# + WPF (.NET 10).
Core differentiators vs Cursor/Copilot/Cline:
- Zero cost (local Ollama only, cloud optional)
- Native WPF = 2ms input latency, ~600MB RAM, instant startup
- Trust-first: every file change shows a diff, every command needs approval
- Plan → Review → Execute split (agent proposes, you approve)
- Model profiles: auto-switches toolset/system-prompt per model
- Git checkpoint before every agent run (LibGit2Sharp)
- Context meter: visible token progress bar in input area
- Keyboard-first: Ctrl+K command palette (Phase 3)

Research basis: Claude Code (126K stars), Cline (61K), Aider (40K), Zed editor patterns.
Primary failure modes to avoid: sycophancy, invisible costs, rogue agents, UI instability.

**Installed Ollama models on NEWCOREPC (confirmed):**
- `qwen2.5-coder:14b` ← ★ auto-selected as default (best coder)
- `qwen2.5-coder:7b` ← faster coder
- `hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M` ← strong agent
- `hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M` ← large/capable
- `hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M` ← reasoning
- `hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M` ← fast
- `hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0` ← agentic
- `gemma4:e4b`, `llama3.1:8b`, `phi4-mini:latest`, `qwen2.5:14b-instruct`

---

## Architecture

```
OrchestratorIDE/
├── Core/
│   ├── OllamaClient.cs        ← SSE streaming, tool call parsing, EOF fixed
│   ├── AgentLoop.cs           ← Plan/Execute modes, OnToken event for live UI
│   ├── ModelProfiles.cs       ← All 12 installed models profiled + fuzzy match
│   ├── ToolRegistry.cs        ← Tool registration, toolset filter, approval gate
│   └── ContextManager.cs      ← Token counting, 70%/85% warnings
├── Tools/
│   ├── FileTools.cs           ← Read/write with diff preview hook
│   ├── ShellTools.cs          ← PowerShell with deny-list + approval
│   ├── SearchTools.cs         ← ripgrep + fallback regex grep
│   └── TestTools.cs           ← dotnet/pytest/npm/cargo/go auto-detect
├── Trust/
│   ├── GitCheckpoint.cs       ← LibGit2Sharp auto-stage before runs
│   ├── ApprovalQueue.cs       ← TaskCompletionSource async approval gate
│   ├── RulesLoader.cs         ← .agent.md / AGENT.md / .clinerules auto-load
│   └── SessionStore.cs        ← JSON %APPDATA% persistence + crash recovery
├── UI/
│   ├── Controls/
│   │   ├── DiffViewer.xaml/.cs       ← DiffPlex diff with Approve/Reject buttons
│   │   └── ModelPickerPopup.xaml/.cs ← Model switcher flyout (click status bar)
│   └── Panels/
│       ├── FileExplorerPanel.xaml/.cs ← TreeView, folder open, file icons
│       └── AgentPanel.xaml/.cs        ← Chat bubbles, streaming, Plan/Execute toggle
├── Models/
│   ├── AgentMessage.cs        ← MessageRole, MessageStatus, content
│   ├── ToolCall.cs            ← ToolCallStatus, Arguments, DiffPreview
│   └── ProjectSession.cs      ← Serializable session state, AgentMode enum
├── App.xaml                   ← Dark theme palette + global styles
├── MainWindow.xaml/.cs        ← Root layout, all service wiring
└── PROJECT_LOG.md             ← THIS FILE
```

---

## Session History

### Session 1 — 2026-06-05
**Completed:**
- [x] Research: landscape analysis of top AI coding tools (Claude Code, Cline, Aider, Zed)
- [x] Decided: C# + WPF (.NET 10), Windows-only, full rewrite
- [x] Created WPF project at `F:\Ai\OrchestratorIDE\OrchestratorIDE`
- [x] Installed NuGet packages: AvalonEditB 1.2.0, DiffPlex 1.9.0, LibGit2Sharp 0.31.0
- [x] Created all Core, Tools, Trust, UI, Models files
- [x] Built full app scaffold — all files compile
- [x] **First successful launch** (PID 23624) — app window confirmed via Get-Process

**Bugs fixed in session 1:**
- `PlaceholderText` not valid in WPF → changed to Tag
- `yield return` inside catch block → `string? sendError` pattern
- Missing `using System.IO` → added global usings in .csproj
- `ignoreWhitespace` param missing in DiffPlex 1.9.0 → removed
- `TextBlock` type not found → added using
- `ActivityBtnStyle` XamlParseException → moved to App.xaml
- `FileTreeItemStyle` XamlParseException → moved to App.xaml

### Session 2 — 2026-06-05 (continuation)
**Completed:**
- [x] App relaunched after context reset — confirmed PID 28048 running
- [x] Ollama connection verified: 12 models found on first launch
- [x] **Live token streaming to chat bubble**: Added `OnToken` event to AgentLoop; wired in MainWindow
- [x] **Model picker popup**: `ModelPickerPopup.xaml/.cs` — flyout above status bar, click any model to switch
- [x] **ModelProfiles updated**: All 12 installed models profiled with correct toolsets/descriptions
- [x] **Fuzzy match improved**: Handles `hf.co/bartowski/...` style names via last path segment
- [x] **Auto model select**: On startup picks best available coder (`qwen2.5-coder:14b` → ...)
- [x] **Welcome message**: AgentPanel shows startup tips when empty
- [x] **Input placeholder**: Transparent overlay TextBlock with "Ask the agent anything…"
- [x] **Context progress bar**: Mini colored bar in input toolbar (blue/amber/red at 70%/85%)
- [x] **EOF streaming fix**: Changed `while (!reader.EndOfStream)` → `ReadLineAsync` null check (CA2024)
- [x] Ollama API verified via PowerShell: `qwen2.5-coder:7b` responded "Hello from Orchestrator IDE." in 7 tokens

**Build result:** ✅ 0 errors, 6 warnings (all NuGet version pins, safe to ignore)

---

## TODO List

### Phase 1 — Core Engine ✅ DONE
- [x] Build and wire all core services
- [x] App launches with dark theme
- [x] File explorer loads workspace
- [x] Ollama streaming client
- [x] Activity log shows startup events

### Phase 2 — UX & Model Picker ✅ DONE
- [x] Live token streaming → chat bubbles
- [x] Model picker popup (status bar click)
- [x] Model profiles for all 12 installed models
- [x] Auto-select best model on startup
- [x] Welcome message + input placeholder
- [x] Context progress bar in input toolbar
- [ ] Test full Plan → Execute flow with real Ollama prompt (needs user interaction)
- [ ] Test DiffViewer approval flow end-to-end
- [ ] Test Git checkpoint fires before execute

### Phase 3 — Command Palette + Keyboard ✅ DONE
- [x] Ctrl+K command palette with fuzzy search
- [x] All actions registered (model switch, folder open, Plan/Execute, Settings, New Session)
- [x] Keyboard shortcut for model switching
- [x] Keyboard shortcut for Plan/Execute toggle
- [ ] Vim-style j/k navigation in file explorer

### Phase 4 — Code Editor (AvalonEdit integration)
- [ ] Open file from FileExplorer → show in AvalonEditB editor pane
- [ ] Syntax highlighting for .cs, .py, .ts, .json
- [ ] Read-only / editable toggle
- [ ] Jump-to-line when agent reads a file

### Phase 5 — Polish + Sidebar Features ✅ MOSTLY DONE
- [x] Settings panel (Ollama host, model, toggles, workspace, Test Connection)
- [x] Status bar: git branch display
- [x] Single-file publish + desktop shortcut
- [x] Workspace guard (Option D) — badge, Execute block, ConfirmWorkspace flow
- [x] Session auto-save + crash recovery
- [x] Token counter per message + session total
- [x] Text selection in chat bubbles (Ctrl+C works)
- [ ] Checkpoint browser (sidebar list of [agent] commits + Restore button)
- [ ] Session history browser in sidebar
- [ ] Shell approval cards (inline card instead of MessageBox)
- [ ] Windows 11 Mica/Acrylic theme (cosmetic, low priority)

### Phase 6 — Testing Infrastructure
- [x] Two-stage Auto-Verify (build check always + test run only if test files found)
- [x] `--autotest` flag (Option B) — headless pipeline test, CI-friendly
  - Boots services, confirms workspace, runs Plan → Execute, auto-approves diffs
  - Checks file created on disk with expected content
  - Exits 0 (pass) / 1 (fail), prints structured results
- [ ] **FlaUI UI automation (Option A)** — true end-to-end, clicks real buttons
  - NuGet: `FlaUI.Core`, `FlaUI.UIA3`
  - Add `AutomationId` to key XAML controls: TbInput, BtnSend, RbPlan, RbExec, WsBadge
  - Separate test project: `OrchestratorIDE.UITests`
  - Tests: startup model detection, workspace guard, Plan streaming, Execute + DiffViewer approve, file-on-disk check
  - Run against published exe: `dotnet test` launches and drives the real app
  - See: https://github.com/FlaUI/FlaUI

### Phase 7 — Dynamic Tool System (Hot-load + Self-Improvement)

**Goal:** Agent can discover it needs a tool that doesn't exist, and either fix itself
or ask the user to implement it — without stopping the workflow.

**Layer 1 ✅ DONE — Rich not-found feedback**
- `ToolRegistry.BuildRichNotFoundMessage()` — lists every available tool with purpose + usage examples
- Context-aware hints: `create_project` → `run_shell("dotnet new winforms -n ...")`,
  `install_package` → `run_shell("dotnet add package ...")`, etc.
- Model reads the result and self-corrects on the next step. Zero UI change needed.

**Layer 2 ✅ DONE — UnknownToolCard UI**
- `UI/Controls/UnknownToolCard.xaml/.cs`
- Shown in the diff-panel slot whenever agent calls an unregistered tool
- Three actions:
  - **Auto-translate** → feeds Layer-1 message to model, it self-corrects
  - **Skip** → injects "(tool skipped)" and continues the run
  - **Implement it…** → stub, triggers Layer 3 (see below)
- Wired via `ToolRegistry.OnUnknownTool` → `AgentPanel.ShowUnknownToolCard`

**Layer 3 — Roslyn Hot-load Tool Editor (next major milestone)**
- Clicking "Implement it…" opens a tool editor sidebar (uses our AvalonEdit panel)
- Editor pre-filled with a `Func<Dictionary<string,object?>, CancellationToken, Task<string>>` template
- "Let Agent Write It" — spawns mini inner-loop: agent writes the handler in the editor
- "Register & Resume" — compiles with Roslyn scripting at runtime, drops into ToolRegistry, paused step resumes
- NuGet needed: `Microsoft.CodeAnalysis.CSharp.Scripting`
- Key files: `UI/Panels/ToolEditorPanel.xaml/.cs`, `Core/ToolCompiler.cs`
- This closes the self-improvement loop: agent discovers need → writes tool → uses it immediately

**Layer 4 — .agent.md knowledge file (enabler for self-improvement)**
- `F:\Ai\OrchestratorIDE\.agent.md` — loaded by RulesLoader and injected into execute system prompt
- Contents: project structure map, build commands, coding conventions, key files per area, what NOT to touch
- Without this, agent working on its own source code is flying blind
- Write once, agent keeps it updated as the project grows

### Backlog (post-v1)
- [ ] Inline diff editing (edit proposed diff before approving)
- [ ] Background agent (fire task, get notified when done)
- [ ] Token cost estimator (show estimated usage before long runs)
- [ ] Devstral:24b integration (128k context agent tasks)
- [ ] Multi-workspace support
- [ ] fetch_url tool (for documentation lookup) ✅ DONE

---

## Key Design Decisions (locked)

| Decision | Choice | Reason |
|---|---|---|
| Framework | C# + WPF .NET 10 | Native Windows, 2ms latency, single .exe |
| Code editor | AvalonEditB 1.2.0 | WPF-native, used in SharpDevelop/ILSpy |
| Diff engine | DiffPlex 1.9.0 | Mature, actively maintained |
| Git | LibGit2Sharp 0.31.0 | No shell-out needed |
| AI backend | Ollama (local-first) | Zero cost, privacy |
| API compat | OpenAI-compatible `/v1/chat/completions` | Works with Ollama + cloud fallback |
| Trust model | Approve-before-apply | #1 differentiator from Cursor |
| Agent modes | Plan → Review → Execute | Cline + Kiro pattern |
| Token streaming | `AgentLoop.OnToken` event → `AgentPanel.AppendStreamingToken` | Live chat bubble update |
| Model picker | `ModelPickerPopup` flyout on status bar click | Instant visual model switch |

---

## Common Issues & Fixes

| Problem | Fix |
|---|---|
| WPF resource forward-reference crash | Always define shared styles in `App.xaml`, never `Window.Resources` or inline `UserControl.Resources` |
| `yield return` inside catch block | Use `string? error = null` outside catch, check after |
| `reader.EndOfStream` blocks async | Use `ReadLineAsync` returning null for EOF instead |
| Model name `hf.co/...` not in profiles | Improved fuzzy match: split on `/`, match last segment base name |
| Chrome disk cache (for Python app) | Not fixable programmatically — open in fresh incognito tab |

---

## Reference Links

- AvalonEdit: https://github.com/icsharpcode/AvalonEdit
- DiffPlex: https://github.com/mmanela/diffplex
- LibGit2Sharp: https://github.com/libgit2/libgit2sharp
- Ollama API: http://192.168.1.15:11434 (NEWCOREPC, RTX 5070 Ti 16GB VRAM)
- Python Orchestrator (reference app): `F:\Ai\TheOrchestrator\`
