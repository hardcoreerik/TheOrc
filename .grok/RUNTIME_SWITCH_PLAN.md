# Native Runtime Switch Plan

Mini-roadmap for actually routing a live call site through the Native Runtime stack
(`ModelDepot` → `SessionManager` → `AdapterManager` → `RuntimeOrchestrator`) instead of
Ollama. Companion to `.grok/RUNTIME_PHASE0_SPEC.md` (the architecture spec) — this doc is
sequencing and decision-making, not design.

**Status as of 2026-06-19: Stage 0 done (all the plumbing below this doc exists and is
Grok-CLEAN). Nothing past Stage 0 has started.** Ollama remains default and fallback
everywhere, per `docs/ROADMAP.md`'s standing commitment — nothing in this plan changes that
without an explicit decision at Stage 3.

---

## Stage 0 — Plumbing (✅ done, overnight 2026-06-18/19)

`IModelRuntime` → `OllamaRuntime` / `LlamaCppServerRuntime` / `LLamaSharpRuntime` (Phases 0-2),
`ModelDepot` (local asset discovery), `SessionManager` (persistent base-model load),
`AdapterManager` (per-role persistent LoRA contexts, §7-verdict-compliant), `RuntimeOrchestrator`
(wires the last three together), `OrcScheduler` (VRAM-budget admission check, not yet wired in).
All landed, all Grok-CLEAN, all unit-tested where a native object isn't required.

**The gap this plan exists to close:** none of the above has ever run end-to-end against a real
GGUF inside the actual app. The §7 spike harness (`spike-assets/HotSwapSpike/`) exercised raw
`BatchedExecutor` directly — it never touched `LLamaSharpRuntime` itself. So `LLamaSharpRuntime`'s
own logic (embedded-template detection and ChatML fallback, `StatelessExecutor`-per-call
streaming, text-format tool-call parsing) is implemented and unit-tested for its *pure* logic
paths, but has zero verification that it actually produces correct output against a real model.
That's Stage 1.

---

## Stage 1 — Manual smoke test of `LLamaSharpRuntime` itself (no UI changes)

**Goal:** prove `LLamaSharpRuntime.StreamCompletionAsync` works end-to-end before trusting it
anywhere a user can see it.

**How:** a throwaway console harness, same shape as `spike-assets/HotSwapSpike/` — construct a
real `LLamaSharpRuntime`, call `LoadModelAsync` on a local GGUF (the same Llama-3.2-3B-Instruct
fork already on disk works fine for this), then call `StreamCompletionAsync` with:
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

**Design:** mirrors the existing `BtnTestConn_Click` pattern (Ollama "Test Connection" button)
already in `SettingsPanel`. Add to the existing "MODEL DEPOT" section (already shows scan
results) a "Test" action per resolved role binding:
- Button next to each role's resolved binding in the scan results: "▶ Test"
- Click: constructs a throwaway `LLamaSharpRuntime` + `RuntimeOrchestrator`, calls
  `GetConversationForRoleAsync` for that role's binding, sends one fixed test prompt
  ("Say hello and name one tool you have access to."), streams the response into a result
  TextBlock (same style as the existing `TbDepotResults`/`TbRuntimeExplain` pattern).
- Fully disposed after the test — no persistent state, no effect on any other panel.

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

Candidate call sites currently on raw `OllamaClient` or `OllamaRuntime`:

| Call site | Current state | Stakes if something's wrong |
|---|---|---|
| `Research/ChatEngine.cs` | Already behind `IModelRuntime` (migrated dc79041); concrete instance is `OllamaRuntime` | Low — research tab, no swarm/file-write dependents |
| `Agents/SwarmSession.cs` | Already behind `IModelRuntime` for the main path; Ollama-specific eviction escape hatch still raw `OllamaClient` | Medium — swarm runs are a core workflow |
| `MainWindow` primary chat | Still raw `OllamaClient`, never migrated to `IModelRuntime` | High — the main IDE chat, used constantly |
| `Services/Hive/HiveWorkerAgent.cs` | Already behind `IModelRuntime` (migrated this session, Phase 2.5) | Medium — distributed swarm workers |

**My recommendation:** `ChatEngine` (Research tab) first. Already abstracted behind
`IModelRuntime` (no migration work needed, just swap which concrete instance it's constructed
with), lowest blast radius if `LLamaSharpRuntime` has a bug Stage 1/2 didn't catch, and it's a
tab you'd actually use to manually exercise it in normal usage rather than a synthetic test.

**My recommendation on opt-in vs. default:** opt-in, explicitly, per the standing
`docs/ROADMAP.md` commitment ("Ollama stays default and fallback until the ModelDepot +
installer first-run story is bulletproof"). Concretely: a "Backend" choice exposed wherever
`ChatEngine` is constructed (likely a Settings toggle or a per-tab picker) defaulting to Ollama,
with Native as an explicit selection — not a silent global flip. AdapterManager/OrcScheduler
aren't wired into this path yet either, so even after Stage 3 ships, "Native" here means
"`LLamaSharpRuntime` directly, base model only, no adapter, no VRAM admission check" — that's a
real, useful first step, but it's not the full Phase 3 stack yet. Worth being honest about that
scope in whatever UI label this gets.

**Open questions for you to answer before Stage 3 starts:**
1. Confirm `ChatEngine`/Research tab as the first call site (or pick a different one).
2. Confirm opt-in-per-session as the mechanism (or you want something else — e.g. a global
   experimental-features toggle).
3. Decide the GGUF path story: does the user pick a folder manually each time (reusing the
   ModelDepot scan UI from Stage 2), or does Settings remember a "Native Runtime models folder"
   path so this doesn't require re-browsing every session?

---

## Stage 4 — Implement the Stage 3 decision

Not started; scope depends entirely on Stage 3's answers. Will be its own follow-up plan once
Stage 3 is decided — not pre-written here, since writing implementation detail against an
undecided design question is how plans rot.

---

## Rollback notes

Every stage above is additive and isolated:
- Stage 1 touches no shipped code (throwaway harness only).
- Stage 2 adds a button to Settings; deleting it removes 100% of the surface area.
- Stage 3/4, whenever they happen, should be implemented as a toggle defaulting to the current
  (Ollama) behavior — if Native turns out to have a problem in real use, flipping the toggle
  back is the rollback, not a revert.
