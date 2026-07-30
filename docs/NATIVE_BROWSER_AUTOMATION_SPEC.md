# TheOrc Native Browser Automation — Implementation Spec

> **Relationship to existing docs:** [`docs/NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md`](NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md)
> already names Browser Automation as Rank 1 and sketches its Phase 0 (contracts/capability
> model) and Phase 1 (browser automation pack) at product-strategy level. This document does for
> those two phases what [`docs/NATIVE_RUNTIME_V2_SPEC.md`](NATIVE_RUNTIME_V2_SPEC.md) did for
> `RUNTIME_PHASE0_SPEC.md`: makes them concrete against the actual current code, with real file
> paths, real contracts, and a phase plan with verification tied to real test names. It does not
> replace the Function Pack Plan's Phases 2-6 — those still apply as written and are out of scope
> here.
>
> **Status: design only, zero code written.** Written in response to an explicit request (2026-07-30)
> to produce a scoped spec before starting a multi-day build, matching this repo's established
> "design doc → phased implementation → verification" pattern rather than jumping straight to code.

---

## 0. Purpose, scope, non-goals

### 0.1 Purpose

Give TheOrc's native runtime a first-class, cross-surface (OrcChat, headless AgentLoop, HIVE
native-agent workers) way to control a real browser: navigate, click, type, wait, extract text/DOM,
screenshot, and download — the single largest gap between "a local model host" and "a local
operator" per the Function Pack Plan's own framing.

### 0.2 Sources

- [`docs/NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md`](NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md) — the
  product-level phase plan this spec makes concrete (Phases 0 and 1 only).
- [`docs/NATIVE_RUNTIME_V2_SPEC.md`](NATIVE_RUNTIME_V2_SPEC.md) — the style/rigor template, and the
  runtime this tool surface must plug into (`IModelRuntime`/`IRoleRuntime`, `NativeRoleRuntime`,
  `HeadlessAgentLoop`).
- Current tool-calling architecture, researched directly against the code on 2026-07-30 (file paths
  throughout §1 below).

### 0.3 Explicitly out of scope for this document

- Writing any code. This is a design doc.
- Phases 2-6 of the Function Pack Plan (image/OCR, workspace intelligence, bounded shell, artifact
  export, typed-result polish) — each deserves its own pass through this same current-code gap
  analysis when its turn comes; guessing their contracts now would just be more stale design debt.
- A generic plugin/extension marketplace for third-party tools.
- Arbitrary JavaScript evaluation in the automated page as a first-class tool primitive (see §4,
  open question 3).
- Multi-tab / multi-context browser session pooling — single browser, single page per task/turn is
  the initial target; pooling is a real future optimization, not a v1 requirement.

---

## 1. Current state — tool-calling architecture gap analysis

This is not greenfield in the way "no Playwright reference in the codebase" might suggest — there
is a real, working tool-calling architecture already. The gap is that it has **two parallel
surfaces with different contracts**, and neither has a typed result shape yet. Both need to accept
browser automation without inventing a third convention.

### 1.1 Interactive surface (OrcChat / Chat / Swarm, via `ToolRegistry`)

- `ToolDefinition` (`OrchestratorIDE/Core/OllamaClient.cs:434-464`) is the unit of registration:
  `Name`, `Description`, `Parameters` (`Dictionary<string, ToolParameter>`), `Required`,
  `RequiresApproval`, and a closure-based `Handler: Func<Dictionary<string, object?>,
  CancellationToken, Task<string>>`. Result is always a plain string, conventionally prefixed
  `[OK]`/`[ERROR]`/`[POLICY BLOCKED]`/`[SANDBOX BLOCKED]`/`[REJECTED]`.
- Tools are grouped into static `Register(ToolRegistry, workspaceRoot, ...)` methods per family
  (`OrchestratorIDE/Tools/{FileTools,ShellTools,SearchTools,TestTools,WebTools,GraphTools,
  FabricTools}.cs`) — a `BrowserTools.cs` in the same family is the natural home for the
  interactive-surface tools.
- `ToolRegistry` (`OrchestratorIDE/Core/ToolRegistry.cs`) filters by `ModelProfile.ToolSet`
  (`Minimal`/`Coding`/`Full`, an allow-list by name) and is the sole `ExecuteAsync` entry point.
- **Approval is two coexisting mechanisms, not one**: (a) `ToolDefinition.RequiresApproval` +
  `Trust/ApprovalQueue.cs`'s `RequestApprovalAsync` (queues a `PendingApproval` with a
  `TaskCompletionSource<bool>`, resolved by trust level or a UI callback) — this is what
  `ShellTools.Register` uses (`RequiresApproval = true` unconditionally); (b) a per-tool
  `onDiffPreview` callback that `FileTools.Register`'s `write_file` uses directly, bypassing the
  queue's `RequiresApproval` flag entirely. A new tool family should pick ONE of these, not invent
  a third — see §2.3 for which.
- Independent of both: `Trust/PathSandbox.IsInsideSandbox` (workspace confinement, with an
  `onSandboxBypass` escape-hatch callback) and `Trust/ToolPolicyEngine.Evaluate` (command-string
  policy blocking, currently wired into `ShellTools`).
- No rich per-call context object — handlers close over `workspaceRoot` and any callbacks at
  `Register()` time; the only per-call object is `ToolCall` (`OrchestratorIDE/Models/ToolCall.cs`),
  which carries `Id`/`Name`/`Arguments`/`Result`/`Status`/`DiffPreview`/`ExplainWhy`, not a live
  resource handle. A browser session (a live page/context) has no existing place to live across
  calls within one turn — see §2.2.

### 1.2 Headless surface (HIVE native-agent workers, campaign execution)

- Does **not** share `ToolRegistry`/`ApprovalQueue` at all — deliberately separate and more
  restricted. `HiveWorkerAgent.ExecuteTaskAsync` → `HiveNativeRoleExecutorAdapter.ExecuteAgentAsync`
  (`Services/Hive/HiveNativeRoleExecutorAdapter.cs:86-107`) builds a `HeadlessAgentLoop`
  (`OrchestratorIDE/Core/Runtime/HeadlessAgentLoop.cs`) with tools from
  `NativeWorkerToolProfile.Create(outputDirectory)` (`Services/Hive/NativeWorkerToolProfile.cs`).
- `HeadlessTool` (`Core/Runtime/HeadlessAgentLoop.cs:18-21`) is the contract:
  `record HeadlessTool(string Name, object Schema, Func<IReadOnlyDictionary<string, object?>,
  CancellationToken, Task<string>> ExecuteAsync)` — same args/ct/string shape as the interactive
  side's `Handler`, just no `RequiresApproval` field at all.
- `HeadlessAgentLoop`'s own doc comment: *"the loop never prompts, auto-approves, or invents shell
  access"* — there is categorically no operator present to click Approve on a Warband. Today's five
  tools (`read_file`, `list_files`, `grep_code`, `write_file`, `run_tests`) are each independently
  sandboxed to an isolated per-task work directory via `Resolve()` (throws `UnauthorizedAccessException`
  on any path escape, `NativeWorkerToolProfile.cs:87-96`) with a 1 MB text-file cap. An unknown tool
  name returns `"[POLICY BLOCKED] Tool '...' is not available on this Warband."`, not an error.
- **Consequence for browser automation**: a headless browser tool set needs its own policy-only
  gating (deny-by-default navigation targets, confined download directory, no arbitrary JS eval,
  bounded step/time budgets) baked into the tool implementation itself — there is no UI round-trip
  to fall back on. This is a real, first-class design constraint, not an afterthought.

### 1.3 Text-based tool-call parsing — TWO conventions, not one

- **JSON-brace convention** (`OrchestratorIDE/Core/ToolCallTextParser.cs`): its own doc comment
  calls it the single source of truth shared by `AgentLoop` and `LLamaSharpRuntime`. Lenient
  balanced-brace scanner over raw model text, string/escape-aware, accepts `name`/`tool`/`function`
  and `arguments`/`args`/`parameters`/`inputs` key aliases, silently skips malformed spans and keeps
  scanning (multiple tool calls per turn). Consumed by `NativeRoleRuntime.StreamRoleCompletionCoreAsync`
  and `HeadlessAgentLoop.ExecuteAsync` directly, and via `AgentLoop.TryParseTextToolCalls`.
- **ReAct XML convention** (`OrchestratorIDE/Research/ChatEngine.cs`, used by OrcChat/"research
  chat"): `<tool_call><name>X</name><args>{...}</args></tool_call>`, taught to the model via
  `OrcChatToolCatalog.BuildReactInstructions`, results re-injected as
  `<tool_result name="...">...</tool_result>`. `ChatEngine` tries native `tool_calls` first, then
  this XML parse, only when `tools.Count > 0`.
- **Consequence**: a browser tool exposed through OrcChat must work with the ReAct XML convention;
  the same tool exposed through the native runtime/headless loop goes through the JSON-brace
  parser. Since both ultimately call the same `Func<args, ct, Task<string>>`-shaped handler, this is
  a non-issue for the tool implementation itself — it only matters for whichever prompt-instruction
  text teaches each surface's model how to call `browser_navigate` et al. Do not build a third
  parsing convention; reuse whichever of the two a given call site already has wired.

### 1.4 No typed result contract exists yet

Every tool today returns a plain string. The Function Pack Plan's own Phase 0 already proposes
adding typed results (text summary / structured rows / artifact refs / screenshot refs /
telemetry) — browser automation is a strong first candidate to actually need this (a screenshot is
not text), so Phase 0 below is written to unblock Phase 1, not deferred as separate unrelated work.

### 1.5 Process-lifecycle precedent

`OrchestratorIDE/Core/LlamaServerManager.cs` is the closest existing template for "wrap an
external process/SDK with real lifecycle management":

- `StartAsync()`: OS-branched binary name (`OperatingSystem.IsWindows() ? "llama-server.exe" :
  "llama-server"`, not `#if WINDOWS`), `ProcessStartInfo` with redirected stdout/stderr → an `OnLog`
  event, poll `/health` every 1.5s until ready or timeout (returns `false`, does not throw).
- `Stop()`: `_process.Kill(entireProcessTree: true)` + bounded `WaitForExit`, always in
  `try/finally`, disposes the handle, fires `OnStatusChanged(false)`.
- `IDisposable` wrapping (`Dispose() => Stop()`), wrapped again by `LlamaCppServerRuntime :
  IModelRuntime, IDisposable`.

Playwright for .NET is a managed SDK, not a bare subprocess TheOrc has to drive with
`ProcessStartInfo` directly — but it still launches and owns real browser processes underneath, and
that ownership needs the same discipline: bounded startup, health/ready signal, guaranteed
cleanup on cancellation/dispose, and OS-appropriate binary resolution (Playwright's own
`playwright install` downloads per-OS browser binaries, analogous to how `LlamaServerManager`
resolves a per-OS server binary today — see §2.4).

### 1.6 Cross-platform status

`docs/INSTALLER_REVAMP_SPEC.md` confirms Windows and macOS platform-installer layers are shipped;
Linux ships Warband (headless daemon) artifacts but not yet a full desktop build. Playwright for
.NET itself supports Windows/Linux/macOS uniformly and runs headless by default — a Warband/daemon
box with no display is not a blocker (headless Chromium/Firefox/WebKit do not need one). The one
existing anti-pattern to explicitly NOT inherit: `ShellTools.cs:95` hardcodes `FileName =
"powershell"` — a Windows-only assumption. Browser automation must be OS-branched from the start,
matching `LlamaServerManager`/`ZipExtractService`'s existing convention, not `ShellTools`'s.

---

## 2. Target design

### 2.1 Phase 0 contracts (shared, both surfaces)

```csharp
// OrchestratorIDE/Core/NativeToolCapability.cs (new)
[Flags]
public enum NativeToolCapability
{
    None               = 0,
    BrowserAutomation  = 1 << 0,
    ImageInput         = 1 << 1,
    Ocr                = 1 << 2,
    ShellExecution     = 1 << 3,
    ArtifactExport     = 1 << 4,
}
```

A capability snapshot is queried the same way by OrcChat and headless loops (Function Pack Plan's
own exit criterion for Phase 0) — a single static/injectable `NativeToolCapabilities.Current` (or
threaded through wherever `ModelProfile`/`RuntimeRole` already flows) that both `ToolRegistry`
filtering and `NativeWorkerToolProfile.Create` consult before including browser tools at all. An
unsupported request (e.g. Playwright browsers never installed) must fail with an explicit,
user-readable reason — never silent omission from the tool list with no explanation.

```csharp
// OrchestratorIDE/Core/ToolResult.cs (new) — additive, does not replace the string convention
public abstract record ToolResult(string Summary);
public sealed record TextToolResult(string Summary) : ToolResult(Summary);
public sealed record ArtifactToolResult(string Summary, string ArtifactPath, string MimeType) : ToolResult(Summary);
public sealed record ScreenshotToolResult(string Summary, string ImagePath, int Width, int Height) : ToolResult(Summary);
```

Existing `Handler`/`HeadlessTool.ExecuteAsync` signatures stay `Task<string>` — nothing about the
existing five HIVE tools or the interactive `FileTools`/`ShellTools` family needs to change. A new
`ToolResult`-returning path is additive: browser tools that need to return a screenshot render
their `Summary` as today's string (backward compatible with both text parsers) while separately
attaching the artifact/screenshot ref through whatever channel §2.1's Phase 0 exit criterion
requires ("tool traces include capability snapshot + runtime backend identity") — the exact
plumbing (a side-channel on `ToolCall`, or a wrapper record) is an implementation decision for
Phase 0's own PR, not fixed here.

### 2.2 Browser session lifecycle

```csharp
// OrchestratorIDE/Core/Browser/BrowserSession.cs (new)
public sealed class BrowserSession : IAsyncDisposable
{
    // Owns one Playwright IBrowser + IBrowserContext + current IPage.
    // Headless by default (required for Warband/daemon boxes with no display);
    // an interactive, non-headless mode is an explicit opt-in setting for local debugging only.
    public static async Task<BrowserSession> LaunchAsync(BrowserSessionOptions options, CancellationToken ct);
    public Task<string> NavigateAsync(string url, CancellationToken ct);
    public Task ClickAsync(string selector, CancellationToken ct);
    public Task TypeAsync(string selector, string text, CancellationToken ct);
    public Task<bool> WaitForAsync(string selectorOrText, TimeSpan timeout, CancellationToken ct);
    public Task<string> ExtractTextAsync(string? selector, CancellationToken ct);
    public Task<string> ScreenshotAsync(string outputPath, CancellationToken ct);
    public Task<string> DownloadAsync(string triggerSelector, string outputDirectory, CancellationToken ct);
    public ValueTask DisposeAsync(); // guaranteed browser process teardown, same discipline as LlamaServerManager.Stop()
}
```

One `BrowserSession` per task/turn (§0.3 — no pooling in v1). Lifetime is owned by whichever caller
creates it: for the interactive surface, captured in the `BrowserTools.Register` closure scoped to
one conversation/session (mirroring how `workspaceRoot` is captured today); for the headless
surface, created and disposed within one `NativeWorkerToolProfile`-equivalent factory call, scoped
to the one task's `outputDirectory`.

### 2.3 Interactive surface: `BrowserTools.cs`

New file, same shape as `FileTools`/`ShellTools`: `Register(ToolRegistry registry, string
workspaceRoot, Func<string, string, string, Task<bool>>? onNavigationApproval)` producing
`ToolDefinition`s for `browser_navigate`, `browser_click`, `browser_type`, `browser_wait`,
`browser_extract`, `browser_screenshot`, `browser_download`.

**Approval mechanism decision: use `ApprovalQueue`, not a diff-preview callback.** Rationale:
`write_file`'s diff-preview callback exists because a diff is the natural approval artifact for a
file change; there is no equivalent single-shot preview for "about to navigate to a URL" or "about
to download a file" — the existing `RequiresApproval` + `ApprovalQueue` path (already what
`ShellTools` uses for exactly this "no natural diff, just a yes/no gate" shape) is the right fit.
Concretely: `browser_navigate` sets `RequiresApproval = true` only when the target URL's origin is
not already one this conversation has approved this session (first navigation to a new origin
prompts; subsequent same-origin clicks/extracts do not re-prompt) — matching how an operator
actually thinks about "am I about to leave a page I already saw." `browser_download` always
requires approval (matches `write_file`'s general caution around writing to disk, though via the
queue rather than a diff). `browser_click`/`browser_type`/`browser_extract`/`browser_screenshot`
on an already-approved page do not re-prompt per call — that would make the tool unusable.

Downloads go through `Trust/PathSandbox.IsInsideSandbox` against the workspace root, same as
`FileTools`; a download destination outside the workspace uses the same `onSandboxBypass` escape
hatch already wired for file tools, not a new mechanism.

### 2.4 Headless surface: browser tools for `NativeWorkerToolProfile`

A parallel `NativeWorkerToolProfile`-style factory (either a new static method on that same class,
or a sibling `NativeWorkerBrowserToolProfile.Create(outputDirectory, policy)`), producing
`HeadlessTool`s with the exact same five-tool sandboxing discipline already established:

- No approval queue at all (matches `HeadlessAgentLoop`'s stated design) — policy is baked in, not
  requested live.
- **Deny-by-default navigation policy**: an allow-list or explicit per-task URL/origin grant passed
  in by whatever constructs the task (the Warchief/campaign definition), not an open "navigate
  anywhere" default. A `HeadlessBrowserPolicy` record (allow-listed origins, max navigations per
  task, download-allowed bool) is a reasonable first cut — exact shape is a Phase 1b implementation
  decision, not fixed here.
- Downloads confined to the same per-task `outputDirectory` `NativeWorkerToolProfile.Resolve()`
  already enforces — reuse that helper, don't reimplement path confinement a second time.
- No arbitrary JS eval tool (§4, open question 3) — the tool set is deliberately the same
  enumerated action list as the interactive surface (navigate/click/type/wait/extract/screenshot/
  download), not an escape hatch to run arbitrary page-context script.
- Bounded step/time budget via the same `HeadlessAgentLimits` (`MaxSteps`, `Timeout`) already
  threaded through `HeadlessAgentLoop.ExecuteAsync` — no new budget mechanism needed.

### 2.5 Cross-platform binary resolution

Playwright for .NET's own `Microsoft.Playwright.Program.Main(["install"])` (or the `playwright.ps1`/
`playwright.sh` driver script it generates) downloads per-OS browser binaries into a
Playwright-managed cache directory — this replaces the "resolve a per-OS executable path" step
`LlamaServerManager.LocateServerExe()`/`ZipExtractService.FindServerExe` handle for `llama-server`,
but the *shape* of the decision is the same: check whether the runtime dependency is already
present, and if not, either fail with a clear "run `playwright install`" message (first cut) or
trigger an installer-driven first-run download (later, matching how the Native Runtime v2.0 spec's
own model-depot download flow works) — this parallel is exactly why §0.2 cites
`NATIVE_RUNTIME_V2_SPEC.md` as a structural template, not just a style one.

---

## 3. Phased implementation roadmap

### Phase 0 — Contracts and capability model — **LANDED 2026-07-30**

**Scope, as actually built:** `NativeToolCapability` flags enum + `NativeToolCapabilities`
(`OrchestratorIDE/Core/NativeToolCapability.cs`) — a process-wide, thread-safe `MarkAvailable`/
`MarkUnavailable(reason)`/`Has`/`Reason` snapshot, deliberately not a settings-only toggle (a
capability being configured-on and a capability being genuinely usable, e.g. Playwright browsers
actually installed, are different questions). `ToolResult` hierarchy
(`OrchestratorIDE/Core/ToolResult.cs`) — `TextToolResult`/`ArtifactToolResult`/
`ScreenshotToolResult`, additive; every existing `Handler`/`HeadlessTool.ExecuteAsync` signature is
unchanged (`Task<string>`). `ToolDefinition` gained a nullable `RequiredCapability` property
(`OllamaClient.cs`); `ToolRegistry` consults it in both `GetForProfile` (advertised-list filtering)
and `ExecuteAsync` (defense-in-depth refusal for a call to a registered-but-unavailable tool, with
the recorded reason in an `[UNAVAILABLE]`-prefixed result string).

**Verified, honestly against what actually landed, not the original wording:**
- `NativeToolCapabilityTests.cs` (9 tests, `OrchestratorIDE.UnitTests`): a synthetic
  capability-gated tool (no real capability-gated tool exists yet -- Phase 1 adds the first one) is
  excluded from `GetForProfile`'s advertised list when unavailable, included when available;
  `ExecuteAsync` refuses with the recorded reason when called anyway (the defense-in-depth case) and
  runs normally when available; `ToolResult` subtypes carry `Summary` correctly through the base
  type.
- **Correction to this section's original wording**: "excluded from both `ToolRegistry`'s set AND
  the headless tool list" overclaimed what Phase 0 alone delivers -- only `ToolRegistry` (the
  interactive surface) is wired as of this landing; `NativeWorkerToolProfile`'s headless
  construction has no capability-gated tool to gate yet and isn't touched until Phase 1b. Caught by
  `grok-review -Mode diff` before landing; `NativeToolCapability.cs`'s own doc comment now states
  this accurately instead of claiming both surfaces already consult it.
- **The serialization round-trip verify bullet did not apply as originally written** -- grepped for
  existing `ToolCall`/tool-result JSON persistence and found none (no serialize-for-replay surface
  exists today for tool results to round-trip through). Verified `ToolResult` subtype property
  access directly instead; the serialization question is deferred to whichever later phase actually
  adds a trace/replay persistence surface, not fabricated here to satisfy the original wording.

Build clean, full 717/730-green suite (9 new, 0 regressed), `grok-review -Mode diff` clean before
landing (one follow-up round: the stale doc-comment claim above, plus a test-isolation fix adding
a `[SetUp]` reset alongside the original `[TearDown]`-only one).

### Phase 1a — Browser automation, interactive surface (OrcChat/Chat/Swarm) — **LANDED 2026-07-30**

**Scope, as actually built:** `BrowserSession` (`OrchestratorIDE/Core/Browser/BrowserSession.cs`) --
headless Chromium, navigate/click/type/waitFor/extractText/screenshot/download, lifecycle
discipline mirroring `LlamaServerManager`, a `_faulted` guard that refuses further use after a
cancelled operation rather than risking silent corruption (found necessary while testing --
`Task.WaitAsync(ct)` stops awaiting but doesn't abort the real in-flight Playwright call).
`BrowserTools.cs` (`OrchestratorIDE/Tools/BrowserTools.cs`) -- the seven `ToolDefinition`s, wired
into all three interactive registration call sites (`MainWindow.RegisterAllTools`,
`OrcChatToolCatalog.CreateWorkspaceTools`, `SwarmSession.BuildWorkerToolRegistry`), each with the
approval posture its own approval-queue wiring actually supports (`requireApprovalForNavigateAndDownload`
parameter -- true where a real UI is wired, false where the registry+queue pair has no
`ApprovalRequested` subscriber and would otherwise hang forever under `Guarded` trust).

**Deviations from this section's original plan, and why:**
- **No `RequiresApproval` per-origin smart-gating.** The original design ("only prompt for the
  first navigation to a new origin") needs dynamic, argument-dependent approval decisions
  `ToolRegistry.ExecuteAsync`'s fixed pre-handler `RequiresApproval` check can't express without
  new plumbing. Simplified to "every navigate/download call prompts" (matches `ShellTools`' own
  unconditional-approval precedent) -- conservative and safe by construction, over-prompting
  rather than under-prompting.
- **No bespoke ReAct XML prompt instructions were written.** `OrcChatToolCatalog.BuildReactInstructions`
  already generates its teaching text generically from whatever tools are registered -- adding
  `browser_*` to `TopToolNames` was sufficient, no new prompt-authoring needed.

**Verified, real evidence (not aspirational):**
- `BrowserSessionTests.cs` (11 tests): a real headless Chromium instance driven against a local
  `HttpListener` fixture page -- navigate/click/type/wait/extract/screenshot all exercised for
  real, plus a real path-traversal attempt against `DownloadAsync` (confirmed confined regardless
  of what a malicious Content-Disposition filename claims) and the poisoned-session-refuses-reuse
  behavior after a genuine cancellation.
- `BrowserToolsTests.cs` (4 tests): the FULL tool-call path through `ToolRegistry.ExecuteAsync`
  (not `BrowserSession` directly) -- a real navigate->extract->screenshot loop, sandbox blocking
  for an out-of-workspace screenshot path, and `Guarded` trust level's `ApprovalRequested` genuinely
  firing and gating execution (not just `AutoApprove`-bypassed like the other tests).
- Six grok-review rounds against the real implementation found and fixed real bugs before landing
  (see the git history for `feat: Phase 1a of Browser Automation spec` and its companion commit for
  `BrowserSession` -- capability-detection flapping, a `Directory.CreateDirectory("")` crash on a
  bare screenshot filename, a download path-traversal gap, a `SessionDisposable` gate-disposal race
  that could orphan a real Chromium process, and a per-workspace-switch session leak in
  `MainWindow.RegisterAllTools`).
- Full suite 732/745 green at Phase 1a's own landing point; zero orphaned Chromium processes
  confirmed via `Get-Process` after repeated test runs.

**Explicitly NOT done (documented, not silently skipped):** cancellation only stops *awaiting* the
underlying Playwright call, not the call itself (Playwright's C# API has no cancellation-token
support natively) -- bounded, documented on `BrowserSession`'s own class comment. A narrow,
low-probability race remains where an in-flight call can hit a disposed session mid-workspace-switch,
surfacing as a clean `[ERROR]` result rather than corruption -- accepted, documented on
`MainWindow.axaml.cs`'s own disposal call site.

### Phase 1b — Browser automation, headless/HIVE surface — **LANDED 2026-07-30**

**Scope, as actually built:** `HeadlessBrowserPolicy` and `NativeWorkerBrowserToolProfile.Create`
(`OrchestratorIDE/Services/Hive/NativeWorkerBrowserToolProfile.cs`) -- deny-by-default (empty
allowed-origins list, downloads disabled), matching `NativeWorkerToolProfile`'s own "no run_shell,
no fetch_url" precedent of simply omitting capabilities a task wasn't scoped for. `Create` returns
`([], NoopDisposable)` entirely -- not tools that fail at call time -- when `BrowserAutomation`
isn't currently available, mirroring the interactive surface's own `GetForProfile` filtering.
Wired into `HiveNativeRoleExecutorAdapter.ExecuteAgentAsync`, disposed in a `finally` right after
the loop fully returns (a headless task has a natural, well-defined lifetime the interactive
surface's cross-conversation registry doesn't).

**Deviation from this section's original plan:** every policy violation returns a STRING result
(`[POLICY BLOCKED] ...`) rather than throwing, unlike `NativeWorkerToolProfile.Resolve()`'s own
`UnauthorizedAccessException`-on-escape convention this section originally said to mirror.
Confirmed while implementing that neither `HeadlessAgentLoop.ExecuteAsync` nor
`HiveNativeRoleExecutorAdapter.ExecuteAgentAsync` wrap an individual tool call in a try/catch -- an
uncaught exception would abort the whole multi-step task rather than let the model see a policy
message and try something else. Pre-existing risk in `NativeWorkerToolProfile` too, not introduced
here, but deliberately not extended into this new code.

**Also not yet wired (documented, real follow-up):** `HiveNativeRoleExecutorAdapter` constructs
`NativeWorkerBrowserToolProfile.Create` with the DEFAULT policy (deny-all) -- `HiveTaskBundle` has
no field yet for a campaign/task to grant specific origins, so every deployed HIVE task's
`browser_navigate` is currently policy-blocked for every URL in production, even though the full
mechanism is built, wired, and tested. Threading real per-task origin grants through
`HiveTaskBundle` and wherever campaigns are actually defined is real follow-up work.

**Verified, real evidence:** `NativeWorkerBrowserToolProfileTests.cs` (8 tests) -- capability-gated
empty-list behavior, deny-by-default policy blocking, an explicit origin grant actually working,
the navigation cap, download policy blocking, sandbox-escape blocking for screenshot paths, and
(the actual Phase 1b exit criterion) one test running the full `HeadlessAgentLoop.ExecuteAsync`
loop against a scripted fake `IRoleRuntime` (matching `CampaignEngineTests`' own established
pattern) that navigates then extracts real page text and asserts the loop correctly fed each real
tool result back into conversation history. Full suite 740/753 green; zero orphaned Chromium
processes confirmed after the run.

### Cross-cutting exit criteria (Function Pack Plan's own Phase 1 bar, restated concretely) — **MET at the registration/tool-execution layer, not full manual UI verification**

(grok-review caught the original "MET" heading overclaiming relative to this section's own body
text below, which already lists what wasn't done -- corrected rather than left inconsistent.)

- OrcChat can perform a multi-step browse/extract/screenshot loop end-to-end on the native runtime
  — `BrowserToolsTests.FullBrowseExtractScreenshotLoop_RunsEndToEnd_ThroughToolRegistry`, run
  through the actual `ToolRegistry`/`OrcChatToolCatalog` wiring OrcChat uses, not `BrowserSession`
  in isolation. (Full OrcChat UI click-through was not separately performed this round -- the
  registration and tool-execution path OrcChat actually calls is what's verified.)
- Headless tests cover at least one deterministic site flow —
  `NativeWorkerBrowserToolProfileTests.HeadlessLoop_NavigatesAndExtracts_ThroughTheRealAgentLoop`.
- Cancellation and timeout behavior are enforced — `BrowserSessionTests`' cancellation/timeout
  tests (interactive surface; the headless surface reuses the same `BrowserSession` underneath, so
  the same behavior applies, not separately re-tested).

---

## 4. Open questions requiring an explicit decision before Phase 1 starts

These are genuine forks, not things this spec can responsibly pick unilaterally:

1. **Playwright dependency delivery.** Ship `Microsoft.Playwright` as a hard `PackageReference` in
   `OrchestratorIDE.csproj` (adds real install size to every build/release, mirrors how
   `LLamaSharp` is already a hard dependency) vs. an optional/lazy-loaded package gated behind the
   `BrowserAutomation` capability flag being enabled at all (keeps the default install lean, adds
   real complexity to the capability-gating logic in §2.1). Recommend the hard-dependency path for
   Phase 0/1a (matches the existing `LLamaSharp` precedent, simplest to get right first), revisit
   lazy-loading only if release size becomes a measured problem.
2. **First-run browser binary provisioning.** Fail loudly with a "run `playwright install`"
   message and a Settings-panel button that shells out to do it (simplest, matches today's
   "opt-in smoke test, manual setup" pattern for native GGUF testing) vs. a fully automatic
   first-run download wired into the existing installer flow (bigger scope, more consistent with
   where the Native Runtime v2.0 model-depot flow already is). Recommend starting with the manual
   button — automate later once the manual flow has real usage data on how often it's actually hit.
3. **Arbitrary JS evaluation as a tool primitive.** Not including it in v1 (§0.3) is a deliberate
   security posture — an `eval_js` tool would let a model-directed action run arbitrary script in
   the automated page's context, which is a materially larger trust surface than a fixed action
   enum, especially on the headless/no-approval HIVE path. This needs an explicit yes/no from
   whoever owns TheOrc's threat model, not an implementation-time judgment call.
4. **Non-headless (visible window) mode.** Useful for local debugging ("show me what the browser
   is doing"), but a visible browser window on a Warband/daemon box makes no sense (no display) and
   on the interactive desktop it's a real UX/security surface (a visible window the model controls,
   with a live approval queue, could be confusing about who's "driving"). Recommend: headless-only
   for Phase 1, revisit non-headless as an explicit local-dev-only toggle later if requested.
5. **Direct `ToolDefinition` registration vs. MCP-server exposure** (added 2026-07-30, per
   [`docs/NATIVE_RUNTIME_FUNCTION_PACK_ADDENDUM.md`](NATIVE_RUNTIME_FUNCTION_PACK_ADDENDUM.md)'s
   proposed Phase 1.5, "MCP-native tool layer"). §2.3/2.4 above design `BrowserTools` as direct
   `ToolDefinition`/`HeadlessTool` registrations, matching every existing tool family
   (`FileTools`, `ShellTools`, etc.) — the path of least resistance and the only one with a working
   precedent in this codebase today. The addendum proposes exposing tools through an MCP host
   instead, so the runtime can discover/compose third-party MCP servers generically rather than
   requiring bespoke `Register()` code per tool family. These are not mutually exclusive (an MCP
   host could wrap the same `BrowserSession` this spec defines), but building BOTH is real added
   scope, and building MCP-first would be a bigger, riskier first step than this spec's plan since
   there is zero existing MCP-host precedent anywhere in this codebase to build on (unlike
   `ToolDefinition`/`ApprovalQueue`, which are proven, working, and already handle approval/
   sandboxing for five other tool families). **Recommendation, not a decision this spec makes
   unilaterally**: ship Phase 1 as designed (direct `ToolDefinition`/`HeadlessTool`) first, since it
   reuses proven infrastructure and unblocks real browser automation sooner; treat MCP-native
   exposure as a genuinely separate, later architectural investment (Phase 1.5) evaluated once
   there's a concrete second or third external tool source that would actually benefit from
   MCP-style discovery, not before. This trades "protocol-generality now" for "working sooner" —
   flagging explicitly rather than picking silently, since it's a real product-direction call.

---

## 5. Traceability

| Function Pack Plan phase | This spec's phase | Status |
|---|---|---|
| Phase 0 — Contracts and capability model | §3 Phase 0 | **Landed 2026-07-30** (interactive surface only — see §3 Phase 0's own correction) |
| Phase 1 — Browser automation pack | §3 Phase 1a (interactive) + 1b (headless) | **Landed 2026-07-30 at the mechanism/registration/tool-execution layer** — both surfaces built, wired into production call sites, and tested end-to-end through `ToolRegistry`/`HeadlessAgentLoop` (see §3's own "Verified, real evidence" for each). NOT done: manual click-through of the actual OrcChat UI, and HIVE production tasks get a deny-all policy until `HiveTaskBundle` gains real per-task origin-grant plumbing (§3 Phase 1b's own "Also not yet wired" note) — the underlying tool is fully built and tested, but effectively inert in production until that follow-up lands. |
| Phase 2 — Image/OCR/multimodal | Out of scope here (§0.3) | Not started |
| Phase 3 — Workspace intelligence | Out of scope here (§0.3) | Not started |
| Phase 4 — Bounded shell/build/test | Out of scope here (§0.3) | Not started |
| Phase 5 — Artifact export | Out of scope here (§0.3) | Not started |
| Phase 6 — Typed results polish | Partially pulled forward into this spec's Phase 0 (§2.1), rest out of scope | Not started |
| Phase 1.5 — MCP-native tool layer (addendum, research-directions tier) | §4 open question 5 — recommendation, not commitment | Not started |
