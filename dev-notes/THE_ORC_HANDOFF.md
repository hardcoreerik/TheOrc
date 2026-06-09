# 🗡 Give This to Your A.I.!
### TheOrc — Full Architecture & Context Document

> **What this is:** Paste this document into any AI (Claude, GPT-4o, Gemini, local models, etc.)
> to instantly give it deep knowledge of TheOrc's architecture, design system, tools, and conventions.
> Your AI can then contribute code, fix bugs, add features, or generate a personalised `.agent.md` —
> without you having to explain the project from scratch.
>
> **Project:** [https://github.com/hardcoreerik/TheOrc](https://github.com/hardcoreerik/TheOrc)
> **Version:** v1.0.0 · .NET 10 · WPF · Windows only

---

## SECTION 1 — WHAT THEORC IS

**OrchestratorIDE** ("TheOrc") is a native C# WPF (.NET 10) AI coding IDE for Windows.
It runs entirely on local models via llama.cpp or Ollama — zero cloud dependency, zero subscription cost.

### Core value proposition vs. Cursor / Copilot / Cline

| Differentiator | Implementation |
|---|---|
| Zero cost | Local-only inference — llama.cpp or Ollama, user-configurable host |
| Native speed | WPF, not Electron — 2ms input latency, ~600MB RAM, instant startup |
| Trust-first | Every file write shows a full diff. Every shell command needs approval. |
| Plan → Review → Execute | Agent proposes, human approves, then it acts |
| Full transparency | Activity log streams every tool call, token count, git SHA |
| Model profiles | Agent toolset and system prompt auto-switch per model capability |
| First-run wizard | Detects GPU, downloads runtime + model, writes personalised `.agent.md` |
| Portable zip bootstrap | Both exes in one zip — works on completely fresh machines, no prior AI tools needed |

### What it is NOT
- Not a web app, not Electron, not browser-based
- Not a cloud product — inference host is user-configurable, never hardcoded
- Not autonomous — every destructive action requires explicit human approval

---

## SECTION 2 — ARCHITECTURE

### Tech stack
- **Language:** C# 13 / .NET 10
- **UI framework:** WPF (Windows Presentation Foundation)
- **NuGet packages:**
  - `AvalonEditB 1.2.0` — code editor component
  - `DiffPlex 1.9.0` — line-by-line diff engine
  - `LibGit2Sharp 0.31.0` — git integration (no shell-out)
  - `Microsoft.CodeAnalysis.CSharp 4.13.0` — Roslyn (for tool compiler)
  - `System.Text.Json 10.0.8` — settings + session serialisation
- **AI backend:** llama.cpp (local server) or Ollama via OpenAI-compatible `/v1/chat/completions` API (SSE streaming)
- **Build:** `dotnet publish -c Release -r win-x64 --self-contained` → single exe

### Projects in the solution

```
OrchestratorIDE.slnx
├── OrchestratorIDE/          ← Main WPF app (the AI coding IDE)
├── OrchestratorSetup/        ← WPF installer wizard
└── OrchestratorIDE.UITests/  ← FlaUI automation test suite (26/26 passing)
```

### Service dependency graph

```
MainWindow
├── OllamaClient            ← SSE streaming, tool call parsing (JSON + text-format fallback)
│                             Supports both llama.cpp and Ollama backends
├── LlamaServerManager      ← Spawns/monitors llama-server.exe process (llama.cpp backend)
├── ToolRegistry            ← Tool registration, unknown-tool handling (Layer 1+2), approval gate
│   └── ApprovalQueue       ← TaskCompletionSource async gate (pauses agent loop)
├── AgentLoop               ← Plan/Execute step loop, refusal guard, rules injection
│   ├── OllamaClient
│   ├── ToolRegistry
│   ├── ContextManager      ← Token counting, 70%/85%/100% warnings
│   ├── GitCheckpoint       ← LibGit2Sharp auto-commit before every Execute run
│   └── RulesLoader         ← Loads .agent.md / AGENT.md / .clinerules from workspace
├── SessionStore            ← JSON persistence to %APPDATA%\OrchestratorIDE\sessions\
│
├── FileExplorerPanel       ← TreeView, FileSelected event → opens in editor
├── AgentPanel              ← Chat bubbles, streaming, Plan/Execute toggle, DiffPanel slot
├── CodeEditorPanel         ← AvalonEditB multi-tab, drag-to-split
├── CheckpointBrowserPanel  ← [agent] git commit list, Restore button
├── SessionBrowserPanel     ← All saved sessions, click to resume
├── SettingsPanel           ← Ollama/llama.cpp settings, agent file regeneration
└── FirstRunWindow          ← One-time setup wizard: hardware detection + .agent.md generation
```

### Agent loop flow (critical to understand)

```
User sends prompt
│
├─ PlanAsync (if Plan mode)
│   ├── _rules.LoadAsync(workspaceRoot) → inject .agent.md into system prompt
│   ├── BuildPlanSystemPrompt(profile, rulesText)
│   ├── Stream model response token by token → OnToken → AgentPanel bubble
│   ├── StripFakeToolBlocks(plan) → remove hallucinated JSON tool calls
│   └── Add clean plan to session.Messages for Execute phase
│
└─ ExecuteAsync (if Execute mode)
    ├── _rules.LoadAsync(workspaceRoot) → inject .agent.md into system prompt
    ├── _git.CheckpointAsync → creates [agent] commit before touching anything
    ├── STEP LOOP (max steps per model profile):
    │   ├── Call model → stream tokens → parse tool calls
    │   ├── Fallback: TryParseTextToolCalls() for text-format JSON tool calls
    │   ├── No tool calls + refusal text? → inject nudge message, continue
    │   ├── For each tool call:
    │   │   ├── write_file → ShowDiff (suspends loop, user approves/rejects)
    │   │   ├── run_shell → ShowShellApproval (suspends loop, user approves/rejects)
    │   │   └── unknown tool → OnUnknownTool → ShowUnknownToolCard (user chooses)
    │   └── Tool result injected → next step
    └── Done → emit final response
```

### File structure

```
OrchestratorIDE/                     ← Main app project root
├── OrchestratorIDE.csproj
├── App.xaml / App.xaml.cs           ← Global dark theme styles (ALL brushes defined here)
├── MainWindow.xaml / .cs            ← Root layout + all service wiring
│
├── Core\
│   ├── AgentLoop.cs                 ← Plan/Execute loop, refusal guard, nudge
│   ├── AgentFileGenerator.cs        ← Generates personalised .agent.md from hardware + profile
│   ├── OllamaClient.cs              ← SSE streaming, JSON+text tool call parsing
│   ├── ModelProfiles.cs             ← 12+ model profiles, fuzzy match, auto-select
│   ├── ToolRegistry.cs              ← Tool registration, Layer 1/2 unknown-tool handling
│   ├── ContextManager.cs            ← Token counting, progress bar feed
│   ├── UpdateChecker.cs             ← Silent background update check vs GitHub Releases
│   └── AppSettings.cs               ← All user settings, JSON persistence to %APPDATA%
│
├── Tools\
│   ├── FileTools.cs                 ← read_file, write_file (diff hook)
│   ├── ShellTools.cs                ← run_shell (PowerShell, deny-list, approval)
│   ├── SearchTools.cs               ← grep_code, get_outline
│   └── TestTools.cs                 ← run_tests (dotnet/pytest/npm auto-detect)
│
├── Trust\
│   ├── GitCheckpoint.cs             ← Auto-stage + commit before runs, rollback
│   ├── ApprovalQueue.cs             ← Async approval gate (TaskCompletionSource)
│   ├── RulesLoader.cs               ← .agent.md / AGENT.md / .clinerules loader
│   └── SessionStore.cs              ← JSON sessions at %APPDATA%\OrchestratorIDE\
│
├── UI\
│   ├── FirstRunWindow.xaml/.cs      ← First-launch wizard: hardware, name, agent file
│   ├── Controls\
│   │   ├── DiffViewer.xaml/.cs      ← DiffPlex diff, Approve/Reject
│   │   ├── ModelPickerPopup.xaml/.cs← Model switcher flyout
│   │   ├── CommandPalette.xaml/.cs  ← Ctrl+K fuzzy search
│   │   ├── UnknownToolCard.xaml/.cs ← Layer 2: unknown tool handler
│   │   └── ShellApprovalCard.xaml/.cs ← Inline shell command approval
│   └── Panels\
│       ├── AgentPanel.xaml/.cs      ← Chat, streaming, diff slot
│       ├── FileExplorerPanel.xaml/.cs ← Workspace tree view
│       ├── CodeEditorPanel.xaml/.cs ← AvalonEditB multi-tab editor
│       ├── CheckpointBrowserPanel.xaml/.cs ← Git checkpoint list
│       ├── SessionBrowserPanel.xaml/.cs ← Session history list
│       └── SettingsPanel.xaml/.cs   ← App settings
│
└── Models\
    ├── AgentMessage.cs              ← MessageRole, MessageStatus, content
    ├── ToolCall.cs                  ← ToolCallStatus, Arguments dict, IsTextFormat
    └── ProjectSession.cs            ← Serializable session state
```

---

## SECTION 3 — DESIGN SPECIFICATION

### Window dimensions
- Default: 1300 × 800px, `WindowState="Normal"`
- Minimum sidebar width: 140px (resizable via GridSplitter)
- Agent panel minimum: 300px wide

### Layout zones

| Zone | Height/Width | Color token | Notes |
|---|---|---|---|
| Title bar | H: 32px | `#323233` | Custom — ⬡ icon + model badge |
| Menu bar | H: 22px | `#2D2D2D` | File / Edit / View / Agent |
| Activity bar | W: 44px | `#333333` | Icon strip, 4 buttons |
| Sidebar | W: 220px default, min 140px | `Br.Bg.Sidebar = #333333` | Swapped by activity buttons |
| Sidebar/main splitter | W: 4px | `Br.Border = #474747` | Horizontal drag |
| Agent panel | W: `*` (fills remaining) | `Br.Bg.App = #1E1E1E` | Min 300px |
| Code editor pane | W: 0 initially → `*` when open | `Br.Bg.App = #1E1E1E` | Hidden until file opened |
| Agent/editor splitter | W: 4px | `Br.Border` | Hidden until editor opens |
| Activity log | H: `*` at 1:2 ratio to main | `Br.Bg.Panel = #252526` | Streams tool events |
| Horizontal splitter | H: 4px | `Br.Border` | Between main and activity log |
| Status bar | H: 24px | `Br.Bg.StatusBar = #007ACC` | VS Code blue |

### Design token reference (complete)

| Token Key | Hex Value | Usage |
|---|---|---|
| `Br.Bg.App` | `#1E1E1E` | Main background, editor background |
| `Br.Bg.Panel` | `#252526` | Activity log background, agent input area |
| `Br.Bg.Sidebar` | `#333333` | Sidebar, activity bar |
| `Br.Bg.StatusBar` | `#007ACC` | Bottom status bar (VS Code blue) |
| `Br.Bg.Input` | `#3C3C3C` | Text inputs, inactive badges, toggle track |
| `Br.Bg.Hover` | `#2A2D2E` | Tree item hover, activity button hover |
| `Br.Bg.Active` | `#094771` | Selected tree item, active tabs, menu hover, toggle on |
| `Br.Border` | `#474747` | All dividers, splitters, borders |
| `Br.Text.Primary` | `#CCCCCC` | Normal text |
| `Br.Text.Default` | `#CCCCCC` | Alias for Text.Primary (some panels) |
| `Br.Text.Muted` | `#858585` | Labels, hints, timestamps, disabled |
| `Br.Text.White` | `#FFFFFF` | Status bar text, selected item text |
| `Br.Accent.Blue` | `#569CD6` | Keywords, primary actions, links |
| `Br.Accent.Green` | `#4EC9B0` | Type names, Execute mode, success, teal badges |
| `Br.Warning` | `#CCA700` | Plan mode indicator, amber warning badges |
| `Br.Error` | `#F44747` | Error state, Stop button, reject actions |

**Additional one-off colors (not in App.xaml tokens):**
- Menu bar background: `#2D2D2D`
- Tab inactive background: `#2D2D2D` | Tab active: `#1E1E1E`
- Tab close button hover: bg `#3A1010`, fg `#F47070`
- Shell approval "Approve": bg `#1A3A1A`, fg `#6DBF67`, border `#2A5A2A`
- Shell approval "Reject": bg `#3A1010`, fg `#F47070`, border `#5A1A1A`
- Unknown tool card header: bg `#3A2800`, border `#664400`, text `#CCA700`
- Rules badge: bg `#102A20`, text `#4EC9B0`

### Global typography

```
Base font:     Consolas, 'Cascadia Code', monospace
Base size:     12px
Muted labels:  10px (SectionHeader, timestamps, badge labels)
Chat role:     10px Bold (YOU / AGENT / SYSTEM)
Activity log:  11px
Title bar:     12px SemiBold
Status bar:    11px
```

### States and interactions

| Element | State | Behavior |
|---|---|---|
| Workspace badge | No workspace | Amber text, `#3A3010` bg |
| Workspace badge | Root drive | Red text, `#3A1010` bg |
| Workspace badge | Good folder | Teal text, `#102A26` bg |
| Rules badge | No rules file | Collapsed (hidden) |
| Rules badge | Rules loaded | Visible, teal, shows filename |
| Context bar | 0–70% | Blue (`#569CD6`) |
| Context bar | 70–85% | Amber (`#CCA700`) |
| Context bar | 85–100% | Red (`#F44747`) |
| Send button | Idle | Enabled, `#094771` bg |
| Stop button | Running | Enabled, `#3A1A1A` bg |
| DiffPanel | Default | Collapsed (`Height=0`) |
| DiffPanel | Pending approval | Visible, max-height 340px |
| Editor pane | Default | `ColEditor Width=0`, Collapsed |
| Editor pane | File opened | `ColEditor Width=*`, Visible |

### Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+K` | Open command palette |
| `Ctrl+O` | Open folder |
| `Ctrl+N` | New session |
| `Ctrl+Shift+E` | Show file explorer |
| `Ctrl+Shift+C` | Toggle code editor |
| `Ctrl+Shift+R` | Open/create rules file |
| `Ctrl+=` | Increase editor font |
| `Ctrl+-` | Decrease editor font |
| `Ctrl+0` | Reset editor font |
| `Enter` | Send message (in input) |
| `Shift+Enter` | New line in input |

---

## SECTION 4 — TOOL SYSTEM

### The registered tools

| Tool | Arguments | Requires Approval | Notes |
|---|---|---|---|
| `read_file` | `path` | No | Reads any file on disk |
| `write_file` | `path`, `content`, `reason?` | Yes — DiffViewer | Shows old/new diff |
| `list_files` | `directory`, `pattern?` | No | Directory listing |
| `run_shell` | `command` | Yes — ShellApprovalCard | PowerShell, deny-list |
| `grep_code` | `pattern`, `path?`, `file_pattern?` | No | ripgrep + regex fallback |
| `get_outline` | `path` | No | Class/method structure |
| `run_tests` | `path?` | No | dotnet/pytest/npm auto-detect |
| `ask_user` | `question` | No — shows dialog | Pauses agent, user types answer |

### Model toolsets (ToolSet enum)

| ToolSet | Available tools | Used by |
|---|---|---|
| `Minimal` | read_file, list_files, run_shell | Small/fast models |
| `Coding` | All tools | Default (qwen2.5-coder etc.) |
| `Full` | All registered tools | Large/capable models |

### Layer 1/2 unknown tool recovery

When agent calls an unregistered tool:
- **Layer 1**: `ToolRegistry.BuildRichNotFoundMessage()` — lists all tools + context-aware hints
  - e.g. `create_project` → `run_shell("dotnet new winforms -n Name")`
- **Layer 2**: `UnknownToolCard` UI — Auto-translate / Skip / Implement options shown to user
- **Layer 3**: Roslyn hot-load (ToolCompiler.cs + ToolEditorPanel) — compile + register at runtime

### Refusal guard

If model returns no tool calls AND content contains any of:
`"i'm sorry"`, `"i cannot"`, `"i am unable"`, `"as an ai"`, `"step-by-step guide"`, `"open visual studio"`, `"manually implement"`

→ Inject hard nudge: *"Stop. Do NOT give instructions. Use write_file and run_shell tools RIGHT NOW."*
→ Max 1 nudge per run (steps 1-2 only)

---

## SECTION 5 — DATA FLOW

### Settings persistence

```
AppSettings
├── OllamaHost: string           ← e.g. "http://localhost:11434"
├── Backend: InferenceBackend    ← LlamaCpp or Ollama
├── DefaultModel: string
├── MaxStepsOverride: int        ← 0 = use model profile default
├── AutoVerify: bool
├── AutoCheckpoint: bool
├── CheckForUpdates: bool
├── DefaultWorkspace: string
├── DetectedGpuName: string      ← Written by installer
├── DetectedVramGb: double       ← Written by installer
├── DetectedRuntime: string      ← Written by installer (cuda12/vulkan/avx2/cpu)
├── DetectedCudaVersion: string  ← Written by installer
├── AgentUserName: string        ← From first-run wizard
├── AgentExtraContext: string    ← From first-run wizard
└── FirstRunComplete: bool       ← false on first launch → triggers wizard

Storage: %APPDATA%\OrchestratorIDE\settings.json
```

### Session persistence

```
ProjectSession
├── Id: Guid (new each session)
├── WorkspaceRoot: string
├── IsWorkspaceConfirmed: bool [JsonIgnore]  (must re-confirm each launch)
├── ActiveModel: string
├── Messages: List<AgentMessage>  ← last 40 injected into context
├── Mode: AgentMode (Plan/Execute)
├── PlanText: string?
├── CreatedAt, LastActivityAt: DateTime
├── TotalTokensUsed: int
└── LastCheckpointSha: string?

Storage: %APPDATA%\OrchestratorIDE\sessions\{Guid}.json
Crash recovery: loads most recent session on startup
```

### Workspace confirmation guard

Execute mode is blocked until `IsWorkspaceConfirmed = true`.
Becomes true ONLY when user explicitly opens a folder.
Badge: amber (unconfirmed) → teal (confirmed).

### Git checkpoint lifecycle

```
ExecuteAsync called
→ GitCheckpoint.CheckpointAsync(workspaceRoot)
  → if git repo: stage all, commit "[agent] Pre-agent checkpoint — HH:mm:ss"
  → returns SHA (stored in session.LastCheckpointSha)
→ User can restore via CheckpointBrowserPanel → RollbackAsync(sha) → hard reset
```

### First-run wizard flow

```
App starts → MainWindow.OnLoadedAsync()
→ If !settings.FirstRunComplete:
  → FirstRunWindow opens (modal)
  → Hardware detection (GPU name, VRAM, runtime variant, CUDA version)
  → User enters name + optional extra context
  → Live preview of generated .agent.md
  → Save → AgentFileGenerator.GenerateAsync(settings, workspaceRoot)
    → Writes {workspaceRoot}\.agent.md
    → settings.FirstRunComplete = true
    → settings saved

→ From Settings panel: "Regenerate Agent File" opens wizard again
```

### Portable zip / bootstrap flow

```
User extracts portable zip (both OrchestratorIDE.exe + OrchestratorSetup.exe)
→ Runs OrchestratorIDE.exe
→ OnLoadedAsync: tries to connect to backend
→ If 0 models returned AND OrchestratorSetup.exe found alongside exe:
  → Dialog: "No AI runtime detected — run setup wizard now?"
  → Yes → Process.Start(OrchestratorSetup.exe), app shuts down
  → OrchestratorSetup runs → detects GPU → downloads runtime + model
  → Installer skips OrchestratorIDE.exe download (already present)
  → After install: user launches OrchestratorIDE.exe normally
```

---

## SECTION 6 — BUILD & OPERATIONS

```powershell
# From solution root (where OrchestratorIDE.slnx lives)

# Build everything
dotnet build OrchestratorIDE.slnx

# Publish main app (self-contained single exe)
dotnet publish OrchestratorIDE/OrchestratorIDE.csproj `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:DebugType=none

# Publish installer
dotnet publish OrchestratorSetup/OrchestratorSetup.csproj `
  -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -p:DebugType=none

# Run UI tests (FlaUI — 26/26 passing)
dotnet test OrchestratorIDE.UITests/OrchestratorIDE.UITests.csproj
# App must NOT be running when you run these
```

### Adding a new tool

1. Write handler in `Tools\YourTools.cs`:
   ```csharp
   public static async Task<string> MyTool(Dictionary<string, object?> args, CancellationToken ct)
   {
       var param = args.GetValueOrDefault("param_name")?.ToString() ?? "";
       // ... do work ...
       return result;
   }
   ```

2. Register in `MainWindow.RegisterAllTools()`:
   ```csharp
   _registry.Register(new ToolDefinition {
       Name        = "my_tool",
       Description = "What it does",
       Parameters  = new() { { "param_name", "string" } },
       Required    = ["param_name"],
       Handler     = YourTools.MyTool
   });
   ```

3. Add to `ToolRegistry.GetForProfile()` allowed list for `Coding` or `Full`
4. Add a hint in `ToolRegistry.BuildHint()` for common hallucinated aliases

### WPF gotchas (hard-won lessons)

| Problem | Fix |
|---|---|
| All styles must be in App.xaml | Window.Resources causes forward-reference crash |
| Equal-width tabs | `UniformGrid Rows="1"` as ItemsPanel + `HorizontalScrollBarVisibility="Disabled"` on wrapper |
| `yield return` in catch | Use `string? err = null` outside catch, check after |
| Async UI approval | `TaskCompletionSource<T>` bridges agent loop pause → UI choice → resume |
| SSE streaming EOF | Check `ReadLineAsync()` for null, not `reader.EndOfStream` |
| Border not in UIA3 tree | Put `AutomationProperties.AutomationId` on inner TextBlock/Button, not Border |
| Shared styles elsewhere | ALWAYS define in App.xaml — never in UserControl.Resources |

---

## SECTION 7 — CRITICAL CONSTRAINTS

These hard rules must be preserved in all future development:

```
1. NO hardcoded local IP addresses in any .cs file
   - Inference host comes from settings: _settings.OllamaHost / _settings.InferenceBaseUrl
   - Any IP literal in source = violation

2. NO hardcoded local drive paths in any .cs file
   - Use workspaceRoot from session, %APPDATA% for user data, AppContext.BaseDirectory for app-relative
   - Absolute paths (C:\, D:\, etc.) must never appear in source

3. Approval gate must NEVER be bypassed
   - write_file → always DiffViewer approval
   - run_shell → always ShellApprovalCard approval
   - This is the core trust feature — bypassing it breaks the entire safety model

4. App.xaml global styles must not be broken
   - Brushes like Br.Bg.App, Br.Text.Muted, Br.Border are referenced everywhere
   - Never rename, remove, or move them from App.xaml
   - Never define shared styles in Window.Resources or UserControl.Resources

5. Build must stay at 0 errors
   - Warnings acceptable (NuGet pins, nullable)
   - NEVER commit code that fails to build
```

---

## SECTION 8 — CURRENT RELEASE STATUS

**v1.0.0 — shipped**

| Phase | Status | What shipped |
|---|---|---|
| Core engine | ✅ | WPF scaffold, Ollama client, all services |
| Streaming | ✅ | SSE token streaming, model picker, context bar |
| Command palette | ✅ | Ctrl+K fuzzy search, all keyboard shortcuts |
| Code editor | ✅ | AvalonEditB multi-tab, drag-to-split, syntax legend |
| Settings & persistence | ✅ | Git checkpoints, session history, workspace guard |
| Tool system L1+L2 | ✅ | Rich not-found hints, UnknownToolCard UI |
| llama.cpp backend | ✅ | LlamaServerManager, local process spawning |
| FlaUI test suite | ✅ | 26/26 passing |
| OrchestratorSetup installer | ✅ | GPU detect, runtime + model download, shortcuts |
| First-run wizard | ✅ | Hardware summary, personalised .agent.md generation |
| Portable zip bootstrap | ✅ | Both exes in zip, auto-launch setup on fresh machine |
| GitHub Release pipeline | ✅ | CI build + sign + publish via Actions |

### Open / next

```
[ ] Tool system L3 — Roslyn hot-load
    "Implement it…" in UnknownToolCard → ToolEditorPanel opens
    → user edits handler → "Register & Resume" compiles + loads at runtime
    Key files: UI/Panels/ToolEditorPanel.xaml/.cs, Core/ToolCompiler.cs

[ ] Self-improvement loop
    Set workspace = TheOrc source
    Confirm .agent.md loads (📋 badge appears)
    Have agent improve its own source end-to-end

[ ] Inline diff editing
    Edit agent's proposed file changes before approving

[ ] Background agent
    Fire task, get notification when done

[ ] Windows 11 Mica/Acrylic theme

[ ] Multi-workspace support
```

---

## SECTION 9 — TEMPLATE .agent.md FOR THEORC CONTRIBUTORS

> If you are an AI being given this document to help build TheOrc, copy the block below
> and place it in the workspace root as `.agent.md`. This file is auto-loaded into every
> Plan and Execute system prompt.

```markdown
# OrchestratorIDE (.agent.md) — Agent Knowledge File
# TheOrc — Native C# WPF AI Coding IDE  ·  v1.0.0

> Auto-loaded into every Plan and Execute system prompt.
> Update this file whenever architecture changes, a tool is added, or a phase completes.

---

## Project Identity

**What it is:** Native C# WPF (.NET 10) AI coding IDE for Windows  
**Philosophy:** Zero cost (local inference), trust-first (approve before act), Plan→Review→Execute  
**Repo:** https://github.com/hardcoreerik/TheOrc  
**Version:** v1.0.0

---

## Build & Run

```powershell
# From solution root
dotnet build OrchestratorIDE.slnx          # Must be 0 errors before every commit

# Publish main app
dotnet publish OrchestratorIDE/OrchestratorIDE.csproj `
  -c Release -r win-x64 --self-contained -p:PublishSingleFile=true

# Run UI tests (app must be closed)
dotnet test OrchestratorIDE.UITests/
```

---

## Key Rules — Never Violate

```
SECURITY:
  ✗ No hardcoded IP addresses in any .cs file
  ✗ No hardcoded absolute drive paths in any .cs file
  ✗ Never bypass the approval gate for write_file or run_shell
  → Inference host: _settings.OllamaHost / _settings.InferenceBaseUrl

STYLES:
  ✗ Never define shared styles outside App.xaml
  ✓ ALL shared brushes/styles MUST live in App.xaml
  → Forward-reference crash otherwise

BUILD:
  ✓ Every commit must have 0 build errors
  ✓ Run dotnet build before committing

TRUST MODEL:
  ✓ write_file → always DiffViewer approval
  ✓ run_shell → always ShellApprovalCard approval
```

---

## The Available Tools

| Tool | Purpose | Approval needed |
|---|---|---|
| `read_file` | Read any file | No |
| `write_file` | Write/overwrite file | YES — shows full diff |
| `list_files` | Directory listing | No |
| `run_shell` | PowerShell command | YES — shows command card |
| `grep_code` | Regex search across files | No |
| `get_outline` | Class/method structure | No |
| `run_tests` | Run test suite | No |
| `ask_user` | Ask user a question | No — shows dialog |

**NEVER use tools that don't exist.**  
Instead of `create_project` → `run_shell("dotnet new winforms -n Name")`  
Instead of `install_package` → `run_shell("dotnet add package Name")`  
Instead of `build_project` → `run_shell("dotnet build")`

---

## Design Tokens

| Token | Hex | Usage |
|---|---|---|
| `Br.Bg.App` | `#1E1E1E` | Main background |
| `Br.Bg.Panel` | `#252526` | Secondary panels |
| `Br.Bg.Sidebar` | `#333333` | Sidebar, activity bar |
| `Br.Bg.Active` | `#094771` | Selection, active tabs |
| `Br.Bg.Input` | `#3C3C3C` | Text inputs, badges |
| `Br.Border` | `#474747` | All dividers and borders |
| `Br.Text.Primary` | `#CCCCCC` | Normal text |
| `Br.Text.Muted` | `#858585` | Labels, timestamps, hints |
| `Br.Accent.Green` | `#4EC9B0` | Execute mode, success |
| `Br.Warning` | `#CCA700` | Plan mode, warnings |
| `Br.Error` | `#F44747` | Errors, Stop, reject |

---

## WPF Gotchas

| Situation | Right approach |
|---|---|
| Equal-width tabs | `UniformGrid Rows="1"` as ItemsPanel + disabled H-scroll on wrapper |
| Async UI approval | `TaskCompletionSource<T>` — pause agent, resolve in event handler |
| `yield return` in catch | Not allowed — use `string? err = null` outside try/catch |
| UIA3 automation ID | Put on TextBlock/Button, not Border — Border not exposed in tree |
| Shared style definition | App.xaml ONLY — never Window.Resources or UserControl.Resources |

---

## Phase Status

| Phase | Status |
|---|---|
| Core engine, WPF scaffold | ✅ Done |
| Streaming, model picker, context bar | ✅ Done |
| Ctrl+K palette, keyboard shortcuts | ✅ Done |
| AvalonEditB editor, multi-tab, drag-split | ✅ Done |
| Settings, git, session persist, checkpoint/session browsers | ✅ Done |
| Tool system L1+L2 (hints + UnknownToolCard) | ✅ Done |
| llama.cpp backend (LlamaServerManager) | ✅ Done |
| FlaUI test suite (26/26) | ✅ Done |
| OrchestratorSetup installer | ✅ Done |
| First-run wizard + .agent.md generation | ✅ Done |
| Portable zip bootstrap | ✅ Done |
| Tool system L3 — Roslyn hot-load | ⬜ Next |

*Last updated: v1.0.0 — update when architecture changes, tools are added, or phases complete.*
```

---

*TheOrc — [https://github.com/hardcoreerik/TheOrc](https://github.com/hardcoreerik/TheOrc)*  
*100% local AI. No cloud. No subscriptions. No limits.*
