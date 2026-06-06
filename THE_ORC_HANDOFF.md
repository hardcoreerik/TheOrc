# The Orc — Full AI Handoff Document
### Combined Design Spec · Session Insights · Agent.md Blueprint

> **Purpose:** Give this document to any AI (Claude, GPT-4o, Gemini, etc.) to immediately
> understand OrchestratorIDE at a deep level and generate a production-quality `.agent.md`
> for it. Nothing is assumed. Everything is specified.
>
> **Generated:** 2026-06-06 from 11 live sessions + full codebase analysis

---

## SECTION 1 — WHAT THE ORC IS

**OrchestratorIDE** ("The Orc") is a native C# WPF (.NET 10) AI coding IDE for Windows.
It runs entirely on local Ollama models — zero cloud dependency, zero subscription cost.

### The core value proposition vs. Cursor / Copilot / Cline

| Differentiator | Implementation |
|---|---|
| Zero cost | Ollama-only, any model, user-configurable host |
| Native speed | WPF, not Electron — 2ms input latency, ~600MB RAM, instant startup |
| Trust-first | Every file write shows a full diff. Every shell command needs approval. |
| Plan → Review → Execute | Agent proposes, human approves, then it acts |
| Full transparency | Activity log streams every tool call, token count, git SHA |
| Model profiles | Agent's toolset and system prompt auto-switch per model capability |

### What it is NOT
- Not a web app, not Electron, not browser-based
- Not a cloud product — Ollama host is user-configurable and never hardcoded
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
- **AI backend:** Ollama via OpenAI-compatible `/v1/chat/completions` API (SSE streaming)
- **Build:** `dotnet publish -c Release -r win-x64 --self-contained` → single exe

### Service dependency graph

```
MainWindow
├── OllamaClient          ← SSE streaming, tool call parsing (JSON + text-format fallback)
├── ToolRegistry          ← Tool registration, unknown-tool handling (Layer 1+2), approval gate
│   └── ApprovalQueue     ← TaskCompletionSource async gate (pauses agent loop)
├── AgentLoop             ← Plan/Execute step loop, refusal guard, rules injection
│   ├── OllamaClient
│   ├── ToolRegistry
│   ├── ContextManager    ← Token counting, 70%/85%/100% warnings
│   ├── GitCheckpoint     ← LibGit2Sharp auto-commit before every Execute run
│   └── RulesLoader       ← Loads .agent.md / AGENT.md / .clinerules from workspace
├── SessionStore          ← JSON persistence to %APPDATA%\OrchestratorIDE\sessions\
│
├── FileExplorerPanel     ← TreeView, FileSelected event → opens in editor
├── AgentPanel            ← Chat bubbles, streaming, Plan/Execute toggle, DiffPanel slot
├── CodeEditorPanel       ← AvalonEditB multi-tab, drag-to-split
├── CheckpointBrowserPanel← [agent] git commit list, Restore button
├── SessionBrowserPanel   ← All saved sessions, click to resume
└── SettingsPanel         ← Ollama host, model, workspace, Test Connection
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
    ├── STEP LOOP (max 30 steps per model profile):
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
F:\Ai\OrchestratorIDE\
├── .agent.md                    ← Agent knowledge file (this project's rules)
├── THE_ORC_HANDOFF.md           ← THIS DOCUMENT
├── publish\                     ← Single-exe publish output (gitignored)
└── OrchestratorIDE\             ← C# project root
    ├── OrchestratorIDE.csproj
    ├── App.xaml / App.xaml.cs   ← Global dark theme styles
    ├── MainWindow.xaml / .cs    ← Root layout + all service wiring
    ├── PROJECT_LOG.md           ← Human session history log
    │
    ├── Core\
    │   ├── AgentLoop.cs         ← Plan/Execute loop, refusal guard, nudge
    │   ├── OllamaClient.cs      ← SSE streaming, JSON+text tool call parsing
    │   ├── ModelProfiles.cs     ← 12+ model profiles, fuzzy match, auto-select
    │   ├── ToolRegistry.cs      ← Tool registration, Layer 1/2 unknown-tool handling
    │   └── ContextManager.cs    ← Token counting, 70/85/100% warnings
    │
    ├── Tools\
    │   ├── FileTools.cs         ← read_file, write_file (diff hook)
    │   ├── ShellTools.cs        ← run_shell (PowerShell, deny-list, approval)
    │   ├── SearchTools.cs       ← grep_code, get_outline
    │   └── TestTools.cs         ← run_tests (dotnet/pytest/npm auto-detect)
    │
    ├── Trust\
    │   ├── GitCheckpoint.cs     ← Auto-stage + commit before runs, rollback
    │   ├── ApprovalQueue.cs     ← Async approval gate (TaskCompletionSource)
    │   ├── RulesLoader.cs       ← .agent.md / AGENT.md / .clinerules loader
    │   └── SessionStore.cs      ← JSON sessions at %APPDATA%\OrchestratorIDE\
    │
    ├── UI\
    │   ├── Controls\
    │   │   ├── DiffViewer.xaml/.cs          ← DiffPlex diff, Approve/Reject
    │   │   ├── ModelPickerPopup.xaml/.cs    ← Model switcher flyout
    │   │   ├── CommandPalette.xaml/.cs      ← Ctrl+K fuzzy search
    │   │   ├── UnknownToolCard.xaml/.cs     ← Layer 2: unknown tool handler
    │   │   └── ShellApprovalCard.xaml/.cs   ← Inline shell command approval
    │   └── Panels\
    │       ├── AgentPanel.xaml/.cs           ← Chat, streaming, diff slot
    │       ├── FileExplorerPanel.xaml/.cs    ← Workspace tree view
    │       ├── CodeEditorPanel.xaml/.cs      ← AvalonEditB multi-tab editor
    │       ├── CheckpointBrowserPanel.xaml/.cs ← Git checkpoint list
    │       ├── SessionBrowserPanel.xaml/.cs  ← Session history list
    │       └── SettingsPanel.xaml/.cs        ← App settings
    │
    ├── Models\
    │   ├── AgentMessage.cs      ← MessageRole, MessageStatus, content
    │   ├── ToolCall.cs          ← ToolCallStatus, Arguments dict, IsTextFormat
    │   └── ProjectSession.cs    ← Serializable session state
    │
    └── Tests\
        ├── AutoTestRunner.cs    ← --autotest headless CI runner
        └── AutoTestWindow.xaml/.cs ← Headless test window
```

---

## SECTION 3 — DESIGN SPECIFICATION

### Window dimensions
- Default: 1300 × 800px, `WindowState="Normal"`
- Minimum sidebar width: 140px (resizable via GridSplitter)
- Agent panel minimum: 300px wide

### Layout zones (exact pixel measurements)

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
| `Br.Text.Muted` | `#858585` | Labels, hints, timestamps, disabled |
| `Br.Text.White` | `#FFFFFF` | Status bar text, selected item text |
| `Br.Accent.Blue` | `#569CD6` | Keywords, primary actions, links |
| `Br.Accent.Green` | `#4EC9B0` | Type names, Execute mode, success, teal badges |
| `Br.Warning` | `#CCA700` | Plan mode indicator, amber warning badges |
| `Br.Error` | `#F44747` | Error state, Stop button, reject actions |

**Additional one-off colors (not in App.xaml tokens):**
- Menu bar background: `#2D2D2D`
- Tab inactive background: `#2D2D2D`
| Tab active background: `#1E1E1E`
- Tab close button hover bg: `#3A1010` (dark red)
- Tab close button hover fg: `#F47070` (light red)
- Shell approval "Approve" button: bg `#1A3A1A`, fg `#6DBF67`, border `#2A5A2A`
- Shell approval "Reject" button: bg `#3A1010`, fg `#F47070`, border `#5A1A1A`
- Unknown tool card header bg: `#3A2800`, border `#664400`, text `#CCA700`
- Rules badge background: `#102A20`, text `#4EC9B0` (teal)
- Checkpoint restore button: bg `#1A2A3A`, fg `#569CD6`, border `#2A4A6A`

### Global typography

```
Base font:     Consolas, 'Cascadia Code', monospace
Base size:     12px
Muted labels:  10px (SectionHeader, timestamps, badge labels)
Chat role:     10px Bold (YOU / AGENT / SYSTEM)
Activity log:  11px
Title bar:     12px SemiBold
Menu items:    12px
Status bar:    11px
```

### Component: Activity Bar (left strip)

```
Width: 44px
Button size: 44 × 44px
Icons (emoji): 📁 Explorer | ⚡ Session History | ⬡ Checkpoints | ⚙ Settings
Default fg: Br.Text.Muted (#858585)
Hover: bg = Br.Bg.Hover (#2A2D2E), fg = Br.Text.White (#FFFFFF)
No active/selected state indicator (sidebar content is the indicator)
```

### Component: Agent Panel

```
Row 0: Tab bar — H 28px, bg #2D2D2D
  - "⚡ Code Agent" tab (active, bg Br.Bg.Active #094771)
  - TbMode indicator: "● PLAN" amber / "▶ EXECUTE" teal, 10px Bold

Row 1: Message list — fills remaining height
  Each bubble:
    Padding: 12px top/bottom, 8px sides
    User bubbles bg: #252526
    Agent bubbles bg: #1E1E1E
    Role icon column: 28px wide
    Content column: fills remaining
    Role label: 10px Bold (role color)
    Content: read-only TextBox, Consolas 12px, transparent bg, no border
    Token label: 9px muted, shown after agent response completes

Row 2: Diff/Approval panel slot — Collapsed until needed
  Max height: 340px
  Hosts: DiffViewer | ShellApprovalCard | UnknownToolCard

Row 3: GridSplitter — H 4px

Row 4: Input area — H 160px, min 80px
  Background: Br.Bg.Panel (#252526)
  Top border: 1px Br.Border
  Padding: 8px/6px

  Bottom toolbar (DockPanel):
    Left → Plan/Execute RadioButtons in bordered container (bg Br.Bg.Input, cornerRadius 3, padding 2)
    Left → Workspace badge (📁 folder name, color-coded: amber/red/teal)
    Left → Rules badge (📋 .agent.md, teal, hidden until rules loaded)
    Left → Token counter + 80px-wide mini progress bar (blue→amber→red at 70/85%)
    Right → Stop button (70px, bg #3A1A1A, fg Br.Error)
    Right → Send button (84px, bg Br.Bg.Active, fg white, margin-left 6px)

  Text input: AcceptsReturn, Consolas 12px, bg Br.Bg.Input, border 1px Br.Border, padding 8/6
  Placeholder overlay: #555555, 11px, non-interactive
```

### Component: Code Editor Panel

```
Primary pane (always visible when editor open):
  Tab bar: UniformGrid Rows="1" inside ScrollViewer (HorizontalScrollBarVisibility=Disabled)
    → makes all tabs equal-width automatically
  Tab item:
    Active: bg #1E1E1E, fg white
    Inactive: bg #2D2D2D, fg #888888
    Close button: always visible ✕, turns #F47070 on hover with #3A1010 bg
    MinWidth/MaxWidth: NONE (let UniformGrid distribute)
  Split button (⊟): shows in primary tab bar header
  AvalonEditB editor: fills remaining height
  Drop zone overlay: right-edge 40% of primary pane width, shown on drag-over

Secondary pane (hidden until split):
  ColGutter: Width=4 (shows border)
  ColSecondary: Width=* (star sizing)
  Has its own independent tab bar (same style)
  Close pane button (✕) in secondary tab bar header

Footer: filePath | cursor position | language | ? Legend button
Legend popup: full color guide with colored Run text examples
```

### Component: Diff Viewer

```
Shown in DiffPanel slot (Row 2 of AgentPanel)
Built on DiffPlex 1.9.0
Header: shows file path + reason
Added lines: bg #1A3A1A (dark green)
Removed lines: bg #3A1A1A (dark red)
Footer: Approve (green) | Reject (red) buttons
```

### Component: Shell Approval Card

```
Header: H 32px, bg #1A2A3A, border-bottom #2A4A6A
  ⚡ icon (blue) + tool name (blue SemiBold) + " — approval required" (muted)
Body: ScrollViewer
  For each argument: Label (muted 11px) + code block (Consolas 12px, fg #CE9178, bg #141414)
  Reason (if present): italic #D4D4D4
  Safety note: bg #1A1A20, border #333, muted text
Footer: H auto, bg #2D2D2D, border-top 1px
  Approve: 110px, bg #1A3A1A, fg #6DBF67, border #2A5A2A
  Reject: 100px, bg #3A1010, fg #F47070, border #5A1A1A
```

### Component: Unknown Tool Card

```
Header: H 32px, bg #3A2800, border-bottom #664400
  ⚠ icon (amber) + "Unknown Tool Called" (amber SemiBold) + tool name (muted)
Body: ScrollViewer
  "The agent tried to call:" (muted 11px)
  Call preview: Consolas 12px, fg #D69D85 (orange), bg #1A1A1A
  "Arguments:" header + key=value block (Consolas fg #D4D4D4)
  Explanation + 3-option grid with colored bullets
Footer: 3 buttons
  Auto-translate: 130px, bg #1A3A2A, fg #4EC9B0, border #2A5A3A  (teal, primary)
  Skip: 80px, bg #2D2D2D, fg #888888, border #555  (gray)
  Implement it…: 130px, bg #1A2A3A, fg #569CD6, border #2A4A6A  (blue)
```

### Component: Checkpoint Browser Panel

```
Header: H 36px, bg #2D2D2D
  🕐 icon + "Checkpoints" SemiBold + ↻ Refresh button (right-docked)
List: ItemsControl, each entry has:
  Timestamp label (muted 10px) + stripped message (11px)
  DockPanel row: ShortSha (Consolas muted 10px) + ↩ Restore button (right-docked, blue)
  Border-bottom separator between entries
Empty state: TextBlock with explanation (muted, wrapped)
Footer: status text (muted 10px)
```

### Component: Session Browser Panel

```
Header: H 36px, bg #2D2D2D
  🗒 icon + "Session History" SemiBold + ↻ Refresh button
List: each entry is a 2-column grid:
  Left: clickable Border (Hand cursor) → WorkspaceName (11px Bold) + WhenLabel (10px muted) + root path (10px muted, truncated)
  Right: ✕ delete button (26px, transparent bg, #666 fg)
Footer: status text
```

### Component: Status Bar

```
Height: 24px
Background: #007ACC (VS Code blue)
Left: 📁 workspace name (white 11px)
Left: ⎇ branch name (#B0D0FF 11px)
Right: model name (white 11px, clickable → model picker popup)
Center: status text "Ready" / "Planning…" / "Running…"
```

### States and interactions

| Element | State | Behavior |
|---|---|---|
| Activity bar button | Default | Muted icon, transparent bg |
| Activity bar button | Hover | White icon, #2A2D2E bg |
| Tree item | Hover | #2A2D2E bg |
| Tree item | Selected | #094771 bg |
| Tab close button | Default | Visible ✕, default color |
| Tab close button | Hover | #F47070 fg, #3A1010 bg |
| Workspace badge | No workspace | Amber text, #3A3010 bg |
| Workspace badge | Root drive | Red text, #3A1010 bg |
| Workspace badge | Good folder | Teal text, #102A26 bg |
| Rules badge | No rules file | Collapsed (hidden) |
| Rules badge | Rules loaded | Visible, teal, shows filename |
| Context bar | 0–70% | Blue (#569CD6) |
| Context bar | 70–85% | Amber (#CCA700) |
| Context bar | 85–100% | Red (#F44747) |
| Send button | Idle | Enabled, #094771 bg |
| Stop button | Idle | Disabled |
| Stop button | Running | Enabled, #3A1A1A bg |
| DiffPanel | Default | Collapsed (Height=0, Visibility=Collapsed) |
| DiffPanel | Pending approval | Visible, max-height 340px |
| Editor pane | Default | ColEditor Width=0, Collapsed |
| Editor pane | File opened | ColEditor Width=*, Visible |

### Keyboard shortcuts (complete)

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
| `Alt+F4` | Exit |

---

## SECTION 4 — TOOL SYSTEM

### The 7 registered tools

| Tool | Arguments | Requires Approval | Notes |
|---|---|---|---|
| `read_file` | `path` | No | Reads any file on disk |
| `write_file` | `path`, `content`, `reason?` | Yes — DiffViewer | Shows old/new diff |
| `list_files` | `directory`, `pattern?` | No | Directory listing |
| `run_shell` | `command` | Yes — ShellApprovalCard | PowerShell, deny-list |
| `grep_code` | `pattern`, `path?`, `file_pattern?` | No | ripgrep + regex fallback |
| `get_outline` | `path` | No | Class/method structure |
| `run_tests` | `path?` | No | dotnet/pytest/npm auto-detect |

### Model toolsets (ToolSet enum)

| ToolSet | Available tools | Used by |
|---|---|---|
| `Minimal` | read_file, list_files, run_shell | Small/fast models |
| `Coding` | All 7 tools | Default (qwen2.5-coder etc.) |
| `Full` | All registered tools | Large/capable models |

### Layer 1/2 unknown tool recovery

When agent calls an unregistered tool:
- **Layer 1**: `ToolRegistry.BuildRichNotFoundMessage()` — lists all 7 tools + context-aware hints (e.g. `create_project` → `run_shell("dotnet new winforms -n Name")`)
- **Layer 2**: `UnknownToolCard` UI — shown to user; Auto-translate / Skip / Implement options
- **Layer 3**: Roslyn hot-load (planned, not built) — compile + register new tool at runtime

### Refusal guard

If model returns no tool calls AND content contains any of:
`"i'm sorry"`, `"i cannot"`, `"i am unable"`, `"as an ai"`, `"step-by-step guide"`, `"open visual studio"`, `"manually implement"`

→ Inject hard nudge message: *"Stop. Do NOT give instructions. Use write_file and run_shell tools RIGHT NOW."*
→ Max 1 nudge per run (steps 1-2 only)

### Plan-phase fake tool call stripping

When model outputs plan with embedded JSON tool blocks like:
```json
{"name": "create_project", "arguments": {...}}
```
→ `StripFakeToolBlocks()` removes these before adding plan to session history
→ Prevents Execute phase from seeing hallucinated tool calls in context

---

## SECTION 5 — DATA FLOW

### Session persistence

```
ProjectSession
├── Id: Guid (new each session)
├── WorkspaceRoot: string
├── IsWorkspaceConfirmed: bool [JsonIgnore] (must re-confirm each launch)
├── ActiveModel: string
├── Messages: List<AgentMessage>  ← last 40 injected into context
├── ActiveRules: List<string>
├── Mode: AgentMode (Plan/Execute)
├── PlanText: string?
├── CreatedAt, LastActivityAt: DateTime
├── TotalTokensUsed: int
└── LastCheckpointSha: string?

Storage: %APPDATA%\OrchestratorIDE\sessions\{Guid}.json
Auto-saved after every send/receive cycle
Crash recovery: loads most recent session on startup
```

### Workspace confirmation guard

Execute mode is blocked until `IsWorkspaceConfirmed = true`.
It becomes true ONLY when the user explicitly opens a folder (not from loaded settings).
Badge turns amber (unconfirmed) → teal (confirmed) on open.
Agent in Execute mode with unconfirmed workspace → chat bubble error, no tool calls.

### Git checkpoint lifecycle

```
ExecuteAsync called
→ GitCheckpoint.CheckpointAsync(workspaceRoot, "Pre-agent checkpoint")
  → if git repo: stage all, commit "[agent] Pre-agent checkpoint — HH:mm:ss"
  → returns SHA (stored in session.LastCheckpointSha)
→ User can restore via CheckpointBrowserPanel → RollbackAsync(sha) → hard reset
```

### Rules injection lifecycle

```
On every Plan or Execute run:
→ RulesLoader.LoadAsync(workspaceRoot)
  → searches for: .agent.md, AGENT.md, .clinerules, CLAUDE.md, .rules.md
  → returns "[Rules from .agent.md]\n{content}"
→ OnRulesLoaded event fires → AgentPanel.SetRulesStatus(filePath)
  → shows/hides 📋 rules badge in toolbar
→ rulesText injected into system prompt:
  Plan: "Project rules:\n{rulesText}"
  Execute: "Project rules (follow strictly):\n{rulesText}"
```

---

## SECTION 6 — USER WORKING STYLE (from Session Insights)

> Source: 11 analyzed sessions, 121 messages, 58 hours of work, 2026-05-10 to 2026-06-06

### How this user works

**He treats the AI as a full build-deploy-verify partner, not a code generator.**
Sessions average 5+ hours. Tasks are bundled: "ship the feature AND commit AND document."
He works across wildly diverse stacks simultaneously — embedded firmware, pentest tooling,
crypto trading bots, OSINT apps, and OrchestratorIDE itself.

**He intervenes decisively when the AI goes wrong.**
The clearest pattern: he will let the AI iterate through multiple debugging rounds as long
as it's making genuine progress — but he cuts it off immediately when it pursues the wrong
approach. Example: stopped Claude after 40+ minutes of reimplementing display code that
already existed as proven BSP code.

**He values closure.**
Every session ends with clean commits, merged PRs, and persisted config. He treats
each session as something to finalize, not abandon.

**His friction is technical, not communicative.**
When he says "not working" — believe him. Don't defend the existing code. Change it.
The touch code incident: Claude insisted the code was correct through multiple rounds
until he pushed back hard enough. The AI was wrong every time.

### What works well with him

1. **Root-cause debugging** — he stays in the loop and pushes back until genuinely fixed
2. **End-to-end shipping** — styled commits, PRs, persisted memory, handoff prompts
3. **Batch task handling** — he gives room to run on related tasks, doesn't micromanage
4. **Hardware + deployment work** — real devices, ADB, firmware, not just code generation

### What causes friction

1. **Buggy first-pass code** — imports, dependencies, build metadata
2. **Unverified outcomes** — shipping without confirming it actually works
3. **Wrong approach before correction** — especially reimplementing proven code
4. **Defending broken code** — "the code looks correct" when the user reports it isn't

### Rules derived from his working style

```
WHEN USER SAYS "NOT WORKING":
  → Treat as authoritative. Change the implementation.
  → Do NOT conclude existing code is correct.
  → Do NOT ask "are you sure?" — just fix it.

WHEN AN EXISTING SOLUTION EXISTS:
  → Search the codebase first.
  → Adapt proven code rather than reimplementing.
  → Flag explicitly if you're about to re-derive something that exists.

BEFORE DECLARING DONE:
  → Verify the build is clean (0 errors).
  → State what changed and why.
  → If hardware: provide a numbered test procedure.

ON EVERY COMPLETED TASK:
  → Commit with a clean, styled message.
  → Update PROJECT_LOG.md and .agent.md if architecture changed.
  → Never leave sessions mid-stream without a handoff prompt.
```

---

## SECTION 7 — CRITICAL CONSTRAINTS (non-negotiable)

These are hard rules that MUST be preserved across all future development:

```
1. NO hardcoded local IPs in source code
   - Ollama host is at a local IP but it MUST come from user settings, never source
   - Violation: any IP address literal in any .cs file

2. NO hardcoded local drive paths in source code
   - F:\Ai\ or any absolute path MUST NOT appear in .cs files
   - Use workspaceRoot from session, %APPDATA% for user data, relative paths elsewhere

3. GitHub repo is PRIVATE
   - https://github.com/hardcoreerik/The-Orchestrator
   - Never reference it as public

4. Approval gate must NEVER be bypassed
   - write_file always goes through DiffViewer
   - run_shell always goes through ShellApprovalCard
   - This is the core safety feature — bypassing it destroys trust model

5. App.xaml global styles must not be broken
   - Brushes like Br.Bg.App, Br.Text.Muted, Br.Border are used everywhere
   - Never rename, remove, or move them from App.xaml
   - Never define shared styles in Window.Resources (causes forward-reference crash)

6. Build must stay at 0 errors
   - Warnings are acceptable (NuGet version pins, nullable)
   - NEVER commit code that doesn't build
```

---

## SECTION 8 — BUILD & OPERATIONS

```powershell
# All commands run from: F:\Ai\OrchestratorIDE\OrchestratorIDE\

# Build (check errors)
dotnet build

# Run in dev
dotnet run

# Publish single exe
Stop-Process -Name "OrchestratorIDE" -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -r win-x64 --self-contained -o "F:\Ai\OrchestratorIDE\publish"
Start-Process "F:\Ai\OrchestratorIDE\publish\OrchestratorIDE.exe"

# Headless CI test
F:\Ai\OrchestratorIDE\publish\OrchestratorIDE.exe --autotest
# Exits 0 (pass) or 1 (fail)
```

### Adding a new tool (step by step)

1. Write handler: `Task<string> MyTool(Dictionary<string, object?> args, CancellationToken ct)`
2. Register in `MainWindow.RegisterAllTools()`:
   ```csharp
   _registry.Register(new ToolDefinition {
       Name = "my_tool",
       Description = "What it does",
       Parameters = new() { { "param", "string" } },
       Required = ["param"],
       Handler = MyClass.MyTool
   });
   ```
3. Add to `ToolRegistry.GetForProfile()` allowed list for `Coding` or `Full`
4. Add a hint in `ToolRegistry.BuildHint()` for any likely hallucinated alias

### WPF gotchas (hard-won)

| Problem | Fix |
|---|---|
| All styles must be in App.xaml | Window.Resources causes forward-reference crashes |
| Equal-width tabs | `UniformGrid Rows="1"` as ItemsPanel + `HorizontalScrollBarVisibility="Disabled"` on wrapper |
| `yield return` in catch | Use `string? err = null` outside catch, check after |
| Async UI approval | `TaskCompletionSource<T>` bridges agent loop pause → UI choice → resume |
| SSE streaming EOF | Check `ReadLineAsync()` for null, not `reader.EndOfStream` |
| File locked on publish | `Stop-Process -Name "OrchestratorIDE" -Force` before publishing |

---

## SECTION 9 — ROADMAP & OPEN WORK

### Completed phases

| Phase | What shipped |
|---|---|
| 1 | Core engine, WPF scaffold, Ollama client, all services |
| 2 | Live streaming, model picker, context bar, auto-select |
| 3 | Ctrl+K command palette, keyboard shortcuts |
| 4 | AvalonEditB editor, multi-tab, drag-to-split, syntax legend |
| 5 | Settings, git status, session persistence, workspace guard, token counter, checkpoint browser, session browser, shell approval cards |
| 6 | `--autotest` headless CI, two-stage auto-verify |
| 7 L1 | Rich "tool not found" + context-aware hints |
| 7 L2 | UnknownToolCard UI (Auto-translate / Skip / Implement) |
| 7 L4 | `.agent.md` file written, Plan mode rules fix, rules badge, Edit Rules menu |

### Open work (prioritized)

```
HIGH PRIORITY:
[ ] Phase 7 Layer 3 — Roslyn hot-load tool editor
    - "Implement it…" in UnknownToolCard opens tool editor
    - Editor pre-filled with Func<Dictionary<string,object?>, CancellationToken, Task<string>> template
    - "Register & Resume" compiles with Microsoft.CodeAnalysis.CSharp.Scripting
    - Key files: UI/Panels/ToolEditorPanel.xaml/.cs, Core/ToolCompiler.cs

[ ] Self-improvement loop validation
    - Set workspace to F:\Ai\OrchestratorIDE
    - Confirm .agent.md loads (📋 badge appears)
    - Have agent improve its own source code end-to-end

MEDIUM PRIORITY:
[ ] Phase 6 FlaUI automation
    - NuGet: FlaUI.Core, FlaUI.UIA3
    - AutomationId on: TbInput, BtnSend, RbPlan, RbExec, WsBadge
    - Separate project: OrchestratorIDE.UITests
    - Tests: startup, workspace guard, Plan→Execute, DiffViewer approve, file-on-disk

LOW PRIORITY:
[ ] Inline diff editing (edit agent's proposed changes before approving)
[ ] Background agent (fire task, get notification when done)
[ ] Session history browser message preview
[ ] Windows 11 Mica/Acrylic theme
[ ] Devstral:24b integration (128k context)
[ ] Multi-workspace support
```

---

## SECTION 10 — GENERATED .agent.md FOR "THE ORC"

> Copy the content below into `F:\Ai\OrchestratorIDE\.agent.md` to replace the current version.
> This is the optimized version informed by all sessions and the full design spec above.

```markdown
# OrchestratorIDE (.agent.md) — Agent Knowledge File
# "The Orc" — Native C# WPF AI Coding IDE

> Auto-loaded into every Plan and Execute system prompt.
> Update this file whenever you change architecture, add a tool, or learn a new convention.

---

## Who You Are Working For

The user operates as an engineering director-level builder.
He works across embedded hardware, pentest tooling, crypto bots, and native Windows apps simultaneously.
He treats you as a **build-deploy-verify partner**, not a code generator.

### Non-negotiable rules about working with him:
- When he says "not working" — it IS not working. Change the implementation. Do not defend existing code.
- Before declaring done: build must be clean (0 errors), and you must state what changed and why.
- When a working implementation already exists in the repo, adapt it. Never reimpliment from scratch.
- Always commit with a clean styled message when a task completes. Always update PROJECT_LOG.md.
- If you learn something new about the project that isn't in this file, update this file too.

---

## Project Identity

**What it is:** Native C# WPF (.NET 10) AI coding IDE for Windows
**Philosophy:** Zero cost (local Ollama), trust-first (approve before act), Plan→Review→Execute
**GitHub:** Private — https://github.com/hardcoreerik/The-Orchestrator

---

## Build & Run

```powershell
# All commands from: F:\Ai\OrchestratorIDE\OrchestratorIDE\

dotnet build                                    # Check for errors (must be 0)
dotnet run                                      # Dev mode

# Kill → Publish → Launch (do in this order)
Stop-Process -Name "OrchestratorIDE" -Force -ErrorAction SilentlyContinue
dotnet publish -c Release -r win-x64 --self-contained -o "F:\Ai\OrchestratorIDE\publish"
Start-Process "F:\Ai\OrchestratorIDE\publish\OrchestratorIDE.exe"

# CI test (headless)
F:\Ai\OrchestratorIDE\publish\OrchestratorIDE.exe --autotest   # exit 0=pass, 1=fail
```

---

## Project Structure

```
OrchestratorIDE\
├── Core\
│   ├── AgentLoop.cs         ← Plan/Execute loop, refusal guard, rules injection, nudge
│   ├── OllamaClient.cs      ← SSE streaming, JSON + text-format tool call parsing
│   ├── ModelProfiles.cs     ← 12+ model profiles, fuzzy match, auto-select
│   ├── ToolRegistry.cs      ← Tool registration, Layer 1+2 unknown-tool recovery
│   └── ContextManager.cs    ← Token counting, progress bar feed
├── Tools\
│   ├── FileTools.cs         ← read_file, write_file (diff hook)
│   ├── ShellTools.cs        ← run_shell (PowerShell, deny-list, approval gate)
│   ├── SearchTools.cs       ← grep_code, get_outline
│   └── TestTools.cs         ← run_tests (dotnet/pytest/npm auto-detect)
├── Trust\
│   ├── GitCheckpoint.cs     ← [agent] commits before runs, RollbackAsync
│   ├── ApprovalQueue.cs     ← TaskCompletionSource async gate
│   ├── RulesLoader.cs       ← Loads .agent.md first, then AGENT.md, .clinerules…
│   └── SessionStore.cs      ← JSON at %APPDATA%\OrchestratorIDE\sessions\
├── UI\Controls\
│   ├── DiffViewer           ← write_file approval (DiffPlex)
│   ├── ShellApprovalCard    ← run_shell approval (inline card)
│   ├── UnknownToolCard      ← unknown tool: Auto-translate / Skip / Implement
│   ├── ModelPickerPopup     ← flyout above status bar
│   └── CommandPalette       ← Ctrl+K fuzzy search
├── UI\Panels\
│   ├── AgentPanel           ← Chat, streaming, DiffPanel slot, badges
│   ├── FileExplorerPanel    ← Workspace tree
│   ├── CodeEditorPanel      ← AvalonEditB multi-tab, drag-to-split
│   ├── CheckpointBrowserPanel ← [agent] git list + Restore
│   ├── SessionBrowserPanel  ← Saved sessions, click to resume
│   └── SettingsPanel        ← Ollama host, model, workspace
├── App.xaml                 ← ALL global styles and color tokens (never move)
├── MainWindow.xaml/.cs      ← Root layout + all service wiring
└── PROJECT_LOG.md           ← Human session log (update when phases complete)
```

---

## The 7 Available Tools

| Tool | Purpose | Requires Approval |
|---|---|---|
| `read_file` | Read any file | No |
| `write_file` | Write/overwrite file | YES — DiffViewer diff shown |
| `list_files` | Directory listing | No |
| `run_shell` | PowerShell command | YES — ShellApprovalCard shown |
| `grep_code` | ripgrep search | No |
| `get_outline` | Class/method structure | No |
| `run_tests` | Run test suite | No |

**NEVER use tools that don't exist.**
Instead of `create_project` → use `run_shell("dotnet new winforms -n Name")`
Instead of `install_package` → use `run_shell("dotnet add package Name")`
Instead of `design_form` → use `write_file` with XAML content
Instead of `build_project` → use `run_shell("dotnet build")`

---

## Adding a New Tool

1. Write handler in appropriate `Tools\*.cs`:
   `Task<string> MyTool(Dictionary<string, object?> args, CancellationToken ct)`

2. Register in `MainWindow.RegisterAllTools()`:
   ```csharp
   _registry.Register(new ToolDefinition {
       Name = "my_tool",
       Description = "What it does",
       Parameters = new() { { "param_name", "string" } },
       Required = ["param_name"],
       Handler = MyToolsClass.MyTool
   });
   ```

3. Add to `ToolRegistry.GetForProfile()` allowed list for `Coding` or `Full`
4. Add a hint in `ToolRegistry.BuildHint()` if it has common hallucinated aliases
5. Register a keyboard shortcut or command palette entry if it has a UI trigger

---

## Critical Rules — DO NOT VIOLATE

```
SECURITY:
  ✗ No hardcoded local IP addresses in any .cs file
  ✗ No hardcoded local paths (F:\Ai\) in any .cs file
  ✗ Never bypass the approval gate for write_file or run_shell
  → Ollama host comes from settings: _settings.OllamaHost

STYLES:
  ✗ Never define shared styles in Window.Resources or UserControl.Resources
  ✓ ALL shared styles MUST be in App.xaml
  → Forward-reference crash otherwise

TABS:
  ✓ Equal-width tabs require UniformGrid Rows="1" as ItemsPanel
  ✓ Must also have HorizontalScrollBarVisibility="Disabled" on wrapper ScrollViewer
  ✗ Never use MinWidth or MaxWidth on tab items — breaks equal distribution

BUILD:
  ✓ Every commit must have 0 build errors
  ✗ Never commit code that doesn't build
  → dotnet build before every commit

TRUST MODEL:
  ✓ write_file → always suspends via ApprovalQueue → DiffViewer
  ✓ run_shell → always suspends via ApprovalQueue → ShellApprovalCard
  ✗ Never call _approvals.Approve() without waiting for user input
```

---

## WPF Patterns & Gotchas

| Situation | Right approach |
|---|---|
| Need equal-width tabs | `UniformGrid Rows="1"` as ItemsPanel + disabled H-scroll on wrapper |
| Need async UI approval | `TaskCompletionSource<T>` — pause agent, resolve in event handler |
| `yield return` in catch block | Not allowed — use `string? err = null` outside try/catch |
| SSE streaming EOF | Check `ReadLineAsync() == null`, not `reader.EndOfStream` |
| File locked during publish | `Stop-Process -Name "OrchestratorIDE" -Force` first |
| Shared control style | Define in App.xaml only — never inline or in Window.Resources |
| WPF DataTemplate binding | Only bind to public properties on the ItemsSource collection type |

---

## Design Tokens (color reference)

| Token | Hex | When to use |
|---|---|---|
| `Br.Bg.App` | `#1E1E1E` | Main background |
| `Br.Bg.Panel` | `#252526` | Secondary panels |
| `Br.Bg.Sidebar` | `#333333` | Sidebar, activity bar |
| `Br.Bg.Active` | `#094771` | Selection, active tabs, menu hover |
| `Br.Bg.Input` | `#3C3C3C` | Text inputs, badges |
| `Br.Bg.Hover` | `#2A2D2E` | Mouse hover on list items |
| `Br.Border` | `#474747` | All dividers and borders |
| `Br.Text.Primary` | `#CCCCCC` | Normal text |
| `Br.Text.Muted` | `#858585` | Labels, timestamps, hints |
| `Br.Text.White` | `#FFFFFF` | Status bar, active tab text |
| `Br.Accent.Blue` | `#569CD6` | Primary actions, keywords |
| `Br.Accent.Green` | `#4EC9B0` | Execute mode, success, teal elements |
| `Br.Warning` | `#CCA700` | Plan mode, warnings, amber badges |
| `Br.Error` | `#F44747` | Errors, Stop button, reject |
| `Br.Bg.StatusBar` | `#007ACC` | Bottom status bar only |

---

## NuGet Packages (already installed)

| Package | Version | Used in |
|---|---|---|
| AvalonEditB | 1.2.0 | CodeEditorPanel |
| DiffPlex | 1.9.0 | DiffViewer |
| LibGit2Sharp | 0.31.0 | GitCheckpoint |

AvalonEditB XAML namespace:
`xmlns:avalonEdit="clr-namespace:AvalonEditB;assembly=AvalonEditB"`
Syntax highlighting: `HighlightingManager.Instance.GetDefinition("C#")`

---

## Phase Status

| Phase | Status | Description |
|---|---|---|
| 1 | ✅ Done | Core engine, services, WPF scaffold |
| 2 | ✅ Done | Streaming, model picker, context bar |
| 3 | ✅ Done | Ctrl+K palette, keyboard shortcuts |
| 4 | ✅ Done | AvalonEditB editor, multi-tab, drag-split |
| 5 | ✅ Done | Settings, git, session persist, checkpoint/session browsers |
| 6 | ✅ Done | --autotest CI, auto-verify |
| 7 L1 | ✅ Done | Rich tool-not-found + hints |
| 7 L2 | ✅ Done | UnknownToolCard UI |
| 7 L3 | ⬜ Next | Roslyn hot-load: ToolCompiler.cs + ToolEditorPanel.xaml/.cs |
| 7 L4 | ✅ Done | This file + rules badge + Edit Rules menu |

---

## Common Mistakes Reference

| Mistake | What happened | Fix |
|---|---|---|
| Model says "I cannot" | No refusal guard or nudge | AgentLoop.IsRefusal() + nudge already in place |
| Fake JSON in plan | Plan embeds ```json tool calls``` | StripFakeToolBlocks() strips them |
| create_project call | Model hallucinated tool | BuildRichNotFoundMessage() + UnknownToolCard |
| File locked on publish | OrchestratorIDE.exe running | Stop-Process before publish |
| Tab items wrong width | StackPanel as ItemsPanel | UniformGrid Rows="1" |
| Forward-ref crash | Style in Window.Resources | Move to App.xaml |

---

*Last updated: 2026-06-06. Update when architecture changes, tools are added, or phases complete.*
```

---

## APPENDIX — INSTALLED OLLAMA MODELS

```
qwen2.5-coder:14b     ← ★ Default (best coder, auto-selected)
qwen2.5-coder:7b      ← Fast coder
hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M    ← Strong agent
hf.co/bartowski/p-e-w_gpt-oss-20b-heretic-GGUF:Q4_K_M    ← Large/capable
hf.co/bartowski/p-e-w_phi-4-heretic-GGUF:Q4_K_M          ← Reasoning
hf.co/bartowski/p-e-w_Llama-3.1-8B-Instruct-heretic-GGUF:Q4_K_M ← Fast
hf.co/cognitivecomputations/Dolphin3.0-Llama3.1-8B-GGUF:Q4_0    ← Agentic
gemma4:e4b
llama3.1:8b
phi4-mini:latest
qwen2.5:14b-instruct
```

Ollama host: **user-configurable in settings** — NEVER hardcoded in source.

---

*End of THE_ORC_HANDOFF.md — 2026-06-06*
*Generated from 11 live sessions · 58 hours · 2,940+ lines of code shipped*
