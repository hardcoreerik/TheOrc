# Native Runtime Switch Plan

Mini-roadmap for actually routing a live call site through the Native Runtime stack
(`LLamaSharpRuntime`, possibly later `ModelDepot`/`SessionManager`/`AdapterManager`/
`RuntimeOrchestrator`) instead of Ollama. Companion to `.grok/RUNTIME_PHASE0_SPEC.md` (the
architecture spec) — this doc is sequencing and decision-making, not design.

**Status as of 2026-06-19: Stage 0 done. Nothing past Stage 0 has started.** Ollama remains
default and fallback everywhere, per `docs/ROADMAP.md`'s standing commitment.

**Revision note:** the first draft of this doc had three factual errors about current call
-site state, caught by Grok review — corrected below. The errors mattered enough to be worth
naming: they would have led Stage 3 to "migrate" call sites that don't need migrating, and
Stage 2 to build UI in the wrong project.

---

## Stage 0 — Plumbing (✅ done, overnight 2026-06-18/19)

`IModelRuntime` → `OllamaRuntime` / `LlamaCppServerRuntime` / `LLamaSharpRuntime` (Phases 0-2),
`ModelDepot` (local asset discovery), `SessionManager` (persistent base-model load),
`AdapterManager` (per-role persistent LoRA contexts, §7-verdict-compliant), `RuntimeOrchestrator`
(wires the last three together), `OrcScheduler` (VRAM-budget admission check, not yet wired in).
All landed, all Grok-CLEAN, all unit-tested where a native object isn't required.

**Corrected: every real local-generation call site is already behind `IModelRuntime`.** Verified
directly in code, not assumed:
- `MainWindow.xaml.cs:2018` `BuildModelRuntime()` already branches on `_settings.Backend`
  (`InferenceBackend.Ollama` / `LlamaCpp`) and returns `new LlamaCppServerRuntime(...)` or
  `new OllamaRuntime(_ollama)` — **this existing switch is the natural extension point**, not
  something to build from scratch. Adding a third `InferenceBackend` value and a third branch
  here is a small, well-precedented change.
- `ChatPanel.xaml.cs:76/108` constructs `ChatEngine` with `new OllamaRuntime(OllamaClient)` —
  already `IModelRuntime`-wrapped, but **unconditionally Ollama, regardless of `_settings.Backend`**.
  This is a real, pre-existing gap unrelated to Native Runtime: the Research tab doesn't even
  follow the *existing* Ollama/LlamaCpp choice today. Worth fixing either way.
- `SwarmSession.cs` — `_ollama` field and constructor parameter are typed `IModelRuntime`
  already. Only `GetOllamaForRemoteNode` (the remote HIVE node path) constructs a raw
  `OllamaClient`, and that's correctly out of scope — remote nodes are a distributed concern,
  not a local in-process one.
- `HiveWorkerAgent` — migrated to `IModelRuntime` this session (Phase 2.5).

**The actual gap is narrower than "migrate call sites to an abstraction" — that part is done.**
It's: (a) nobody has ever constructed an `LLamaSharpRuntime` and handed it to any of these
already-`IModelRuntime`-shaped call sites, and (b) `LLamaSharpRuntime` itself has never run
end-to-end against a real model inside the app. The §7 spike harness
(`.grok/spike-assets/HotSwapSpike/`) exercised raw `BatchedExecutor` directly — it never touched
`LLamaSharpRuntime`'s own logic (embedded-template detection, `StatelessExecutor`-per-call
streaming, text-format tool-call parsing). That's Stage 1.

**Separate architectural note:** `RuntimeOrchestrator` does **not** implement `IModelRuntime` —
its method shape is role-and-conversation-based (`GetConversationForRoleAsync` returning a
`TrackedConversation`), not the `StreamCompletionAsync(model, history, tools, ...)` shape every
existing call site expects. Plugging the full Phase 3 stack (ModelDepot/SessionManager/
AdapterManager/RuntimeOrchestrator) into `BuildModelRuntime()` would need a new adapter class
implementing `IModelRuntime` on top of `RuntimeOrchestrator` — not yet built, and not needed for
Stage 1/2/3 below, which use `LLamaSharpRuntime` directly. Building that adapter is a Stage 4+
concern once the simpler path is proven.

---

## Stage 1 — Manual smoke test of `LLamaSharpRuntime` itself (no UI changes)

**Goal:** prove `LLamaSharpRuntime.StreamCompletionAsync` works end-to-end before trusting it
anywhere a user can see it.

**How:** a throwaway console harness, same shape as `.grok/spike-assets/HotSwapSpike/` —
construct a real `LLamaSharpRuntime`, call `LoadModelAsync` on a local GGUF (the
Llama-3.2-3B-Instruct fork already on disk works fine), then call `StreamCompletionAsync` with:
1. A plain message, no tools — confirm streamed text comes back coherent.
2. A message + a tool definition — confirm the system-prompt tool-injection block renders
   correctly and the model's text-format tool-call output gets parsed by `ParseToolCalls`.
3. Run it twice in the same process — confirm the `_hasEmbeddedTemplate` cache behaves (second
   call should skip the template-probe try/catch if the first call cached `false`).

**Pass criteria:** coherent streamed output, at least one tool call correctly parsed out of
model text, no exceptions, `GetStats()` reports plausible `TokensPerSecond`/`LastTimeToFirstToken`.

**Out of scope for this stage:** AdapterManager, SessionManager, RuntimeOrchestrator — this is
`LLamaSharpRuntime` in isolation, the same way the §7 spike isolated `BatchedExecutor`. Don't
conflate "the orchestration layer is wired correctly" (already verified by review) with "the
runtime class actually generates correct output" (not yet verified at all) — they're different
risks and this stage is only the second one.

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
(Ollama "Test Connection" button) already present in both `SettingsPanel`s. Add a "▶ Test"
action per resolved role binding in the Model Depot scan results:
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

Corrected candidate call sites — all of these are already `IModelRuntime`-shaped; "switching"
one means changing which concrete instance it's constructed with, not migrating an abstraction:

| Call site | Current concrete instance | Respects existing `Backend` setting? | Stakes if something's wrong |
|---|---|---|---|
| `MainWindow.BuildModelRuntime()` (main IDE chat) | `OllamaRuntime` or `LlamaCppServerRuntime`, switched on `_settings.Backend` | Yes — already has the switch | High — used constantly |
| `ChatPanel`/`ChatEngine` (Research tab) | `OllamaRuntime`, unconditional | **No** — pre-existing gap, ignores `_settings.Backend` entirely | Low — research tab, no swarm/file-write dependents |
| `SwarmSession` main path | Whatever `IModelRuntime` its caller constructs and passes in | Depends on caller | Medium — swarm runs are a core workflow |
| `HiveWorkerAgent` | Whatever `IModelRuntime` its caller constructs and passes in | Depends on caller | Medium — distributed swarm workers |

**My recommendation, unchanged in conclusion but now for the right reason:** `ChatEngine`
(Research tab) first — not because it needs migrating (it doesn't), but because it's the
lowest-blast-radius place to add a *third* `InferenceBackend` value and observe it actually
working under real usage, separate from fixing the pre-existing "ignores Backend entirely" gap
(which should probably be fixed regardless, as its own small task, before or after this).

**My recommendation on opt-in vs. default:** extend the existing `InferenceBackend` enum
(`Ollama`, `LlamaCpp`, → add `LlamaSharp` or similar) rather than inventing a new toggle
mechanism — this reuses a pattern you already have, defaults stay `Ollama`, and "opt-in" simply
means the user picks the new enum value in Settings, same as choosing `LlamaCpp` today. No new
UI paradigm needed.

**Open questions for you to answer before Stage 3 starts:**
1. Confirm `ChatEngine`/Research tab as the first call site (or pick a different one — `MainWindow`
   is also low-effort now that I know its switch already exists, but higher stakes).
2. Confirm extending `InferenceBackend` as the mechanism (or you want something else).
3. Decide whether to fix "`ChatPanel` ignores `_settings.Backend` entirely" as part of this work
   or as a separate, unrelated task — it's a real bug either way, just not one this plan created.
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
