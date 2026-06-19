# Native Runtime Switch Plan

Mini-roadmap for actually routing a live call site through the Native Runtime stack
(`LLamaSharpRuntime`, possibly later `ModelDepot`/`SessionManager`/`AdapterManager`/
`RuntimeOrchestrator`) instead of Ollama. Companion to `.grok/RUNTIME_PHASE0_SPEC.md` (the
architecture spec) — this doc is sequencing and decision-making, not design.

**Status as of 2026-06-19: Stage 0 done. Nothing past Stage 0 has started.** Ollama remains
default and fallback everywhere, per `docs/ROADMAP.md`'s standing commitment.

**Revision note (two rounds):** the first draft had three factual errors about call-site state.
The fix for those introduced a *fourth* wrong claim (that `BuildModelRuntime()` is the main
chat's active backend switch — it isn't, it's only used for `HiveWorkerAgent`). Both rounds
caught by Grok review, both verified by reading full method bodies, not just grep snippets, on
this pass. Listing this because the pattern matters more than any single fact: claims about
which call site does what need the full surrounding method read, not a grep match.

---

## Stage 0 — Plumbing (✅ done, overnight 2026-06-18/19)

`IModelRuntime` → `OllamaRuntime` / `LlamaCppServerRuntime` / `LLamaSharpRuntime` (Phases 0-2),
`ModelDepot` (local asset discovery), `SessionManager` (persistent base-model load),
`AdapterManager` (per-role persistent LoRA contexts, §7-verdict-compliant), `RuntimeOrchestrator`
(wires the last three together), `OrcScheduler` (VRAM-budget admission check, not yet wired in).
All landed, all Grok-CLEAN, all unit-tested where a native object isn't required.

**Corrected, fully verified by reading complete method bodies (not grep snippets):**

- **The main chat (`AgentLoop`, `MainWindow.xaml.cs:117`) hardcodes `new OllamaRuntime(_ollama)`
  at construction, once, forever.** There is no `IModelRuntime`-level switch for it. The
  existing Ollama-vs-llama.cpp "Backend" setting works by **mutating the shared `OllamaClient`
  instance's `Backend`/`Host` properties live** (`OnSettingsSaved`, `MainWindow.xaml.cs:1933-1962`:
  `_ollama.Host = ...; _ollama.Backend = ...;`) — `OllamaRuntime` is a thin pass-through wrapper,
  so it inherits whatever the underlying `OllamaClient` is configured to do, but the wrapper
  class itself never changes. **There is no existing switch to extend for this call site** —
  adding Native Runtime here means introducing genuinely new selection logic.
- **`BuildModelRuntime()` (`MainWindow.xaml.cs:2018`) is the only place real `IModelRuntime`
  class-swapping happens today, and it's used for exactly one thing: constructing
  `HiveWorkerAgent`'s `Runtime` property (`MainWindow.xaml.cs:599`).** It already branches on
  `_settings.Backend` between `LlamaCppServerRuntime` and `OllamaRuntime`. **This is the smallest,
  most precedented place to add a third branch for `LLamaSharpRuntime`** — not the main chat.
- **`ChatPanel`/`ChatEngine` constructs `new OllamaRuntime(OllamaClient)`** using the same shared
  `OllamaClient` instance as the main chat — so it inherits the same live `Backend`/`Host`
  mutation, the same way the main chat does. My first-round claim that it "ignores
  `_settings.Backend` entirely" was wrong (Grok caught this); it follows the setting exactly as
  well as the main chat does, via the same shared-instance mechanism, not its own logic.
- **`SwarmSession`'s constructor takes `IModelRuntime` directly from its caller** — *not fully
  traced this round*; I'm flagging this rather than asserting it, after getting two other claims
  wrong already. Before Stage 3 touches `SwarmSession`, re-verify who constructs it and what
  they pass in, by reading that call site's full body, not a grep match.

**The actual gap, restated accurately:** nobody has ever constructed an `LLamaSharpRuntime` and
handed it to any call site, and `LLamaSharpRuntime` itself has never run end-to-end against a
real model inside the app. The §7 spike harness (`.grok/spike-assets/HotSwapSpike/`) exercised
raw `BatchedExecutor` directly — it never touched `LLamaSharpRuntime`'s own logic
(embedded-template detection, `StatelessExecutor`-per-call streaming, text-format tool-call
parsing). That's Stage 1.

**Separate architectural note:** `RuntimeOrchestrator` does **not** implement `IModelRuntime` —
its method shape is role-and-conversation-based (`GetConversationForRoleAsync` returning a
`TrackedConversation`), not the `StreamCompletionAsync(model, history, tools, ...)` shape every
existing call site expects. Plugging the full Phase 3 stack into `BuildModelRuntime()` would
need a new adapter class implementing `IModelRuntime` on top of `RuntimeOrchestrator` — not yet
built, not needed for Stage 1/2/3, which use `LLamaSharpRuntime` directly.

---

## Stage 1 — Manual smoke test of `LLamaSharpRuntime` itself (no UI changes)

**✅ Done, 2026-06-19. PASSED.** Harness at `.grok/spike-assets/RuntimeSmokeTest/`, raw output
committed at `stage1-results.log` in that directory. References the real `OrchestratorIDE.dll`
via `ProjectReference` (not a reimplementation) so this exercised the actual shipped class.

**Result:**
1. Plain message, no tools — coherent output: *"Hello, I'm here to assist you, and I'd like to
   mention the color blue."* `GetStats()`: 41.2 tok/s, 343ms TTFT — plausible.
2. Message + a `get_weather` tool definition — model correctly emitted a text-format tool call
   and `ParseToolCalls` correctly extracted it: `get_weather(city=Tokyo)`. The tool-injection
   block and text-format parsing both work end-to-end against a real model, for the first time.
3. Second `StreamCompletionAsync` call with the same input — byte-for-byte identical output to
   the first call, confirming the `_hasEmbeddedTemplate` cache doesn't introduce inconsistency
   on repeat calls.

**One minor cosmetic finding, not a blocker:** both Test 1 and Test 3's output start with a
stray `"assistant\n\n"` — a role tag leaking into the visible generation, likely from how the
ChatML fallback (or the embedded template) renders the assistant turn boundary. Doesn't break
anything downstream (the tool-call parser in Test 2 worked fine despite similar leading text),
but would look wrong if ever streamed directly into a chat UI without trimming. Worth a small
follow-up to strip leading role tokens from `StreamCompletionAsync`'s output before Stage 2/3,
not blocking either.

**Real, separate finding this run surfaced:** neither `OrchestratorIDE.csproj` nor
`OrchestratorIDE.Avalonia.csproj` reference any `LLamaSharp.Backend.*` native package — only the
managed `LLamaSharp` package. The harness failed immediately on `LoadModelAsync` with `"The type
initializer for 'LLama.Native.NativeApi' threw an exception"` until `LLamaSharp.Backend.Cuda12.Windows`
was added to the harness's own `.csproj`. **This means `LLamaSharpRuntime` would fail with the
same exception in the shipped app today, regardless of Stage 3's call-site decision** — the
backend package needs to land in `OrchestratorIDE.csproj`/`OrchestratorIDE.Avalonia.csproj`
before Stage 3 can do anything real, not after. `LLamaSharpRuntime.cs`'s own doc comment already
flagged this as a known gap ("not bundled here — install via NuGet or system PATH"), but this is
the first time anything has concretely hit it. **Decision needed before Stage 2/3:** bundle CUDA
backend only (GPU-required, smaller download surface), CPU backend only (works everywhere,
slower), or both with a settings-driven choice (more installer complexity, matches how Ollama
itself handles CPU/GPU today).

---

## Stage 2 — Opt-in test surface in Settings (no live-chat changes)

**Goal:** let a human (you) manually verify the same thing Stage 1 verified programmatically,
through the UI, without touching anything the app currently depends on.

**Corrected project location:** the "MODEL DEPOT" scan section and "NATIVE RUNTIME" telemetry
section built earlier this session exist **only in `OrchestratorIDE.Avalonia/UI/Panels/
SettingsPanel.axaml`**. The primary/shipping WPF app's `OrchestratorIDE/UI/Panels/SettingsPanel.xaml`
has neither — confirmed by direct grep, zero matches. Per `.agents.md`, WPF is the primary
shipping app and Avalonia is the cross-platform preview. This matters for where Stage 2 actually
needs to land to be useful to you day-to-day, not just where it's cheapest for me to extend
existing code.

**Decision needed:** build the Stage 2 test surface in WPF's `SettingsPanel.xaml` (duplicating
the MODEL DEPOT scan UI there too, since it doesn't exist yet), or accept that Stage 2 lives in
Avalonia only for now (you'd need to run the Avalonia build to test) with WPF parity deferred.
Given WPF is what you actually run day-to-day, I'd lean toward building it in WPF directly even
though Avalonia already has the scan UI — but this is your call, not mine to default.

**Design (once the project is decided):** mirrors the existing `BtnTestConn_Click` pattern
(Ollama "Test Connection" button — that generic click-test-show-result shape exists in both
`SettingsPanel`s independently). The thing it would attach to — the Model Depot scan results
list — exists only in Avalonia's `SettingsPanel`, so if this lands in WPF it needs that scan UI
built there first too (see the project-location decision above), not just the test button itself.
Add a "▶ Test" action per resolved role binding in the Model Depot scan results:
- Click: constructs a throwaway `LLamaSharpRuntime`, calls `LoadModelAsync` on that binding's
  base model (no adapter — Stage 1/2 deliberately don't touch AdapterManager), sends one fixed
  test prompt ("Say hello and name one tool you have access to."), streams the response into a
  result text area.
- Fully disposed after the test — no persistent state, no effect on any other panel.
- **WPF convention reminder:** any new interactive control needs `AutomationProperties.AutomationId`
  set (FlaUI targets them) — this applies if Stage 2 lands in WPF, not Avalonia.

**Why this and not a chat window:** a full chat UI for the native runtime is real surface area
(message history, markdown rendering, streaming UI state) that duplicates what `ChatPanel`
already does for Ollama — not worth building before Stage 1 even confirms the runtime works.
A single fixed test prompt with a plain-text result area is enough to manually confirm
generation + tool-call parsing work, which is all this stage needs to prove.

**Pass criteria:** same as Stage 1, but seen with your own eyes in the running app, against
whatever real local GGUF(s) you actually have, not just the one used in Stage 1's harness.

---

## Stage 3 — Decide: which call site, and opt-in or default?

**Not started. Requires your decision, not mine, before any code changes here.**

Corrected candidate call sites, per the fully-verified facts above:

| Call site | Current mechanism | Has an existing `IModelRuntime`-level switch? | Stakes if something's wrong |
|---|---|---|---|
| `HiveWorkerAgent` (via `BuildModelRuntime()`) | Genuine class-swap: `OllamaRuntime` or `LlamaCppServerRuntime` based on `_settings.Backend` | **Yes — the only one that does** | Medium — distributed swarm workers |
| Main chat (`AgentLoop`) | Hardcoded `OllamaRuntime(_ollama)`; Ollama-vs-llama.cpp choice is `OllamaClient`-internal HTTP routing, not class-swap | No | High — used constantly |
| `ChatPanel`/`ChatEngine` | Hardcoded `OllamaRuntime(OllamaClient)`, same shared instance as main chat, same inherited behavior | No | Low — research tab, no swarm/file-write dependents |
| `SwarmSession` | Constructor takes `IModelRuntime` from caller | **Not verified this round** — check before touching | Medium — swarm runs are a core workflow |

**My recommendation, changed from the previous two drafts:** `HiveWorkerAgent` via
`BuildModelRuntime()` first, not `ChatEngine`. It's the only call site with a real, already
-working `IModelRuntime` switch to extend — every other option requires building new selection
logic from nothing, which is real implementation work, not a config change. Extending an
existing three-line switch with a fourth case is the smallest possible Stage 3, and it's also
genuinely useful: it lets a HIVE worker run fully offline on a local GGUF with no Ollama/llama
-server process at all, which is closer to the actual "no Ollama required" goal than adding a
fourth chat-tab option would be.

**Trade-off to weigh:** `HiveWorkerAgent` is lower visibility than the main chat or Research tab
— you won't see it working unless you're actively running a HIVE worker. If the goal is "see
Native Runtime working in something I look at daily," `ChatEngine` is still the better target,
but it requires writing a new selector (e.g. a per-tab or Settings dropdown), not extending
`BuildModelRuntime()`'s existing one. Both are valid; they optimize for different things
(least-new-code vs. most-visible-result).

**My recommendation on opt-in vs. default, unchanged:** extend the existing `InferenceBackend`
enum (`Ollama`, `LlamaCpp` → add `LlamaSharp` or similar). For `HiveWorkerAgent` this is a
one-line addition to `BuildModelRuntime()`'s existing switch. For `ChatEngine`/main chat, it
would mean introducing a Backend-style check at construction time where none exists today —
more work, but still the same mechanism conceptually (an enum value the user picks, defaulting
to Ollama).

**Open questions for you to answer before Stage 3 starts:**
1. Pick `HiveWorkerAgent` (smallest change, lowest visibility) or `ChatEngine`/main chat
   (more code, more visible) as the first real target.
2. If `ChatEngine` or the main chat: confirm extending `InferenceBackend` as the mechanism, and
   accept that new selection logic needs writing (not just a new enum case).
3. If `SwarmSession`: re-verify its actual construction call site first — not done this round.
4. Decide the GGUF path story: does the user pick a folder manually each time, or does Settings
   remember a "Native Runtime models folder" path so this doesn't require re-browsing every
   session?

---

## Stage 4 — Implement the Stage 3 decision

Not started; scope depends entirely on Stage 3's answers. Will be its own follow-up plan once
Stage 3 is decided — not pre-written here, since writing implementation detail against an
undecided design question is how plans rot. (If the chosen path eventually wants the full Phase
3 stack instead of bare `LLamaSharpRuntime`, the `RuntimeOrchestrator`-to-`IModelRuntime` adapter
class noted in Stage 0 becomes part of this stage's scope, not Stage 1-3's.)

---

## Rollback notes

Every stage above is additive and isolated:
- Stage 1 touches no shipped code (throwaway harness only).
- Stage 2 adds a button to Settings; deleting it removes 100% of the surface area.
- Stage 3/4, whenever they happen, extend an existing enum-based switch that already defaults to
  Ollama — if Native turns out to have a problem in real use, picking a different `Backend` value
  is the rollback, not a revert.
