# Orcish Tongue v1 — OrcChat Tool-Call Reliability

> **Relationship to the roadmap entry:** `docs/CURRENT_STATE.yaml`'s `orcish_tongue` entry has read
> `status: planned` / `"Universal tool-call adaptation rename/runtime direction. Not started."`
> since it was written — this document is where it actually starts, scoped deliberately narrow
> (see §0.1). It does not attempt "universal" in one pass; it closes the specific, real gap found
> live on 2026-07-30 and lays a foundation the wider vision can build on later.
>
> **Status: design only, zero code written at time of writing.** Written in response to an
> explicit request for a spec before implementation, matching this repo's established pattern
> (`NATIVE_BROWSER_AUTOMATION_SPEC.md`). Scope was set via three explicit decisions from the
> requester (recorded in §0.1) rather than assumed.

---

## 0. Purpose, scope, decisions already made

### 0.1 What triggered this

Live-testing Browser Automation in the real OrcChat GUI (2026-07-30) surfaced a genuine,
pre-existing bug: `qwen2.5-coder:14b` attempted a `browser_navigate` call as bare JSON
(`{"name": "browser_navigate", "arguments": {"url": "https://example.com"}}`) instead of OrcChat's
taught ReAct-XML convention. `ChatEngine` didn't recognize it — not via native `tool_calls` (empty),
not via the ReAct-XML regex (wrong shape) — and rendered the raw JSON as if it were a normal answer,
with no warning that a tool call had been attempted and silently failed. This is not specific to
browser tools; it would happen with any tool this model tries to call in OrcChat today.

### 0.2 Three scope decisions, made explicitly before this spec was written

1. **Width: OrcChat first, narrow.** Not a redesign of Swarm's or the native
   `HeadlessAgentLoop`'s own tool-calling conventions (both already work adequately for their own
   surfaces — see §1.3/§1.4). This spec fixes `ChatEngine` specifically. A genuinely shared
   cross-surface layer is a real later step, not this one.
2. **Retraining: deferred.** `theorc-toolcaller` stays on its current trained vocabulary (6 tools,
   4 Swarm role tokens). This spec does not include a Foundry retraining round. Where the
   specialist's existing limits bite (browser tools, OrcChat's role-less shape), that's named
   honestly as a gap this spec doesn't close (§2.4, §4).
3. **Spec before code.** This document, reviewed before any implementation begins.

### 0.3 Explicitly out of scope

- Redesigning or touching `AgentLoop`'s, `NativeRoleRuntime`'s, or `HeadlessAgentLoop`'s own
  tool-calling paths (`ToolCallTextParser` is only being *reused*, not modified).
- Any Foundry training/dataset work.
- Deciding whether the mechanisms this spec adds ship default-on or opt-in in OrcChat — that's a
  real product decision but not one to make blind, before there's real behavior to evaluate
  (§4 open question 3).
- A rename of anything to "Orcish Tongue" in the UI/branding. This document uses the name because
  it's the existing roadmap entry's name; whether user-facing surfaces ever say it is unrelated to
  the technical work here.

---

## 1. Current state — gap analysis against the real code

### 1.1 `ChatEngine.RunTurnAsync`'s existing three-outcome structure

(`OrchestratorIDE/Research/ChatEngine.cs:180-237`)

```
stream from model, collect native tool_calls
├─ Path 1: toolCallsNative.Count > 0        → RunNativeToolLoop(...)
├─ Path 2: ResearchToolset.ParseReActCalls  → RunReActLoop(...)
└─ else: SanitizeFinalText(fullText, tools) → plain response
```

`SanitizeFinalText` (`ChatEngine.cs:252-262`) is the existing safety net for the "else" branch — it
already exists specifically to avoid presenting a failed tool attempt as a trustworthy plain
answer. Two cases:
- Empty output → a fixed "didn't produce a usable response" warning.
- `LooksLikeUnexecutedToolAttempt(text, tools)` matches → prepends a "⚠️ attempted a tool call in
  an unsupported format" warning before the raw text.

### 1.2 The actual bug: `LooksLikeUnexecutedToolAttempt`'s regex is too narrow

```csharp
private static bool LooksLikeUnexecutedToolAttempt(string text, List<ToolDefinition> tools) =>
    tools.Any(t => System.Text.RegularExpressions.Regex.IsMatch(
        text, $@"\b{Regex.Escape(t.Name)}\s*\(",
        RegexOptions.IgnoreCase));
```

Matches function-call-pseudocode syntax (`browser_navigate(...)`) — the AMD-stock-price incident
this was originally built for. Does NOT match JSON-object syntax
(`{"name": "browser_navigate", ...}`), because the tool name there is a quoted string value, never
immediately followed by `(`. This is the live bug: the exact shape the model produced, and the exact
shape neither this regex nor either parsing path recognizes.

### 1.3 `ToolCallTextParser` already solves the parsing half — for a different surface

(`OrchestratorIDE/Core/ToolCallTextParser.cs`) — the native runtime/`AgentLoop`'s shared JSON-brace
parser. Lenient, string/escape-aware balanced-brace scanner; accepts `name`/`tool`/`function` and
`arguments`/`args`/`parameters`/`inputs` key aliases; tolerant of surrounding prose and
` ```json ` fences. **The exact bare-JSON text the model produced in §0.1 is valid input to this
parser today, unmodified.** `ChatEngine` simply never calls it. This is the highest-leverage,
lowest-risk fix available: reuse proven code, add zero new parsing logic.

### 1.4 `ToolcallerService` — the existing repair lane, and its real constraints

(`OrchestratorIDE/Services/Swarm/ToolcallerService.cs`) — already does exactly what was described
as the vision: when a Swarm worker's turn produces content but no parseable call, the specialist
gets one shot at proposing the correct one from the worker's stated intent. Two structural
constraints, both deliberate and both directly relevant here:

- **`KnownToolNames`** (`read_file, list_files, grep_code, write_file, run_shell, ask_user`) — the
  frozen v0 tool vocabulary the deployed model was actually trained on. `ProposeAsync` filters to
  this set and returns `null` immediately if nothing survives the filter. **`browser_navigate` and
  the other six browser tools are not in this set** — the repair lane structurally cannot help with
  the exact failure that triggered this spec, and won't be able to until a retraining round
  (explicitly deferred, §0.2 item 2).
- **`SwarmWorkerRole`** (`Researcher, Coder, UIDeveloper, Tester`) — `ProposeAsync` requires one,
  and `BuildSystemPrompt`'s `RoleToken` renders it into the exact prompt shape the model was
  trained on. **Checked against `training_pit/foundry/scripts/export_toolcaller_dataset.py`**: role
  tokens come from `cap["role"]` — whatever role tag organic Swarm captures carried — meaning the
  deployed model was trained ONLY on these four Swarm role tokens, never on an "unknown" or
  role-less prompt shape. `ChatEngine`/OrcChat has no native concept of a Swarm worker role.
  **This means wiring the repair lane into OrcChat requires picking an approximate role token the
  specialist has never actually trained on** — a real, disclosed compromise, not a clean reuse
  (§2.4, §4 open question 1).

### 1.5 Why the role-token approximation is an acceptable risk, not a blocker

`ToolcallerService.ToToolCall` (`ToolcallerService.cs:128-140`) already independently re-validates
any proposal before it's ever treated as runnable: `Kind != "call"` → discarded; tool not in
`KnownToolNames` → discarded; tool not in the caller's actual live tool list → discarded. A
poorly-calibrated response from an out-of-distribution role prompt (garbage, a `clarify`/
`unsupported` decision, an invalid tool name) is structurally caught by validation that already
exists for other reasons — it degrades to "no repair happened," the same outcome as `ProposeAsync`
returning `null` for any other reason (model absent, timeout, malformed JSON). The mechanism was
already built to be best-effort and never make things worse than the no-repair baseline; an
imperfect role token doesn't change that guarantee, it just likely lowers the proposal's hit rate.

---

## 2. Target design

### 2.1 `ChatEngine.RunTurnAsync`'s extended structure

```
stream from model, collect native tool_calls
├─ Path 1: toolCallsNative.Count > 0                    → RunNativeToolLoop(...)          [unchanged]
├─ Path 2: ResearchToolset.ParseReActCalls               → RunReActLoop(...)               [unchanged]
├─ Path 3 (NEW): ToolCallTextParser.Parse(fullText)       → RunNativeToolLoop(...)          [reused, §1.3]
├─ Path 4 (NEW): ToolcallerService.ProposeAsync(...)      → RunNativeToolLoop(...) if "call" [§2.4]
└─ else: SanitizeFinalText(fullText, tools)              → plain response                  [fixed, §2.2]
```

Path 3 reuses `RunNativeToolLoop` directly — `ToolCallTextParser.Parse` returns `List<ToolCall>`,
the exact type that method already accepts. No new loop implementation.

Path 4 only runs when tools exist AND `ToolcallerService.IsEnabled` AND Paths 1-3 all found
nothing. On a `"call"` decision that survives `ToToolCall`'s validation (§1.5), re-enter
`RunNativeToolLoop` with that single proposed call. On any other outcome (null, `no_tool`,
`clarify`, `unsupported`, or a proposal that fails validation), fall through to the existing
`SanitizeFinalText` path unchanged.

### 2.2 Fix `LooksLikeUnexecutedToolAttempt`'s regex (defense in depth)

Even with Paths 3 and 4 added, something can still fall through (repair lane disabled, or a
genuinely unrecognizable format). The existing safety net should catch the JSON-object shape too,
not just function-call pseudocode:

```csharp
private static bool LooksLikeUnexecutedToolAttempt(string text, List<ToolDefinition> tools) =>
    tools.Any(t =>
        Regex.IsMatch(text, $@"\b{Regex.Escape(t.Name)}\s*\(", RegexOptions.IgnoreCase) ||
        Regex.IsMatch(text, $@"[""']name[""']\s*:\s*[""']{Regex.Escape(t.Name)}[""']", RegexOptions.IgnoreCase));
```

Second alternation matches `"name": "browser_navigate"` (or single-quoted) regardless of
surrounding JSON structure — deliberately loose (a substring match, not a strict JSON parse) since
this is a warning heuristic, not an execution path; a false positive here means an extra caution
banner on ordinary text that happens to mention a tool name in that shape, not a wrong action taken.

### 2.3 `ProposeAsync` role-token parameter for OrcChat calls

Pass `SwarmWorkerRole.Researcher` — the closest existing token to OrcChat's actual character
(conversational, research-oriented, not code-generation-focused like `Coder`/`UIDeveloper`, not
verification-focused like `Tester`). Documented at the call site as an explicit approximation, not
a validated behavior, per §1.4/§1.5's reasoning.

### 2.4 What does NOT get fixed by this spec (named, not silently dropped)

- `browser_navigate` and the other six browser tools remain unreachable by the repair lane until a
  retraining round covers them (§0.2 item 2, deliberately deferred).
- The role-token approximation (§2.3) is a known compromise, not a clean fix.
- Swarm's and the native runtime's own tool-calling paths are untouched — this spec doesn't
  unify anything cross-surface, just closes OrcChat's specific gap.

---

## 3. Phased implementation plan

### Phase A — `ToolCallTextParser` fallback in `ChatEngine`

**Scope:** Path 3 above. No `ToolcallerService` involvement — purely a parsing-format addition.

**Verify:**
- Unit test: feed `ChatEngine` (via a fake `IModelRuntime` emitting bare JSON in the exact
  `{"name": ..., "arguments": {...}}` shape from §0.1) a turn with `browser_navigate` registered;
  assert the tool actually executes (not just gets recognized) and `RunTurnAsync` reaches
  `OnTurnComplete` with a real result, not the raw JSON text.
- Regression test: existing native-`tool_calls` and ReAct-XML paths still take priority when
  either produces a result — Path 3 only reached when both are empty.
- Real re-run of the exact live GUI scenario from §0.1 (same model, same prompt) once Phase A
  lands, to confirm this specific failure is now fixed for real, not just in a synthetic test.

### Phase B — Fix `LooksLikeUnexecutedToolAttempt`'s regex

**Scope:** §2.2's regex extension.

**Verify:** Unit test asserting the JSON-object shape now triggers the warning banner when Path 3
doesn't apply (e.g. `ToolcallerRepairEnabled` also off, or the JSON is malformed enough that even
`ToolCallTextParser` can't extract it) — i.e. a case that used to silently render raw JSON as a
trustworthy answer now visibly warns instead.

### Phase C — Wire `ToolcallerService` into `ChatEngine` (Path 4)

**Scope:** §2.1 Path 4 + §2.3's role-token choice.

**Verify:**
- Unit test: with `ToolcallerRepairEnabled = true` and only a `KnownToolNames`-covered tool (e.g.
  `write_file`) registered, a model turn that produces unparseable content triggers a repair
  proposal, and a valid `"call"` decision actually executes.
- Unit test: with only `browser_navigate` (not in `KnownToolNames`) registered, confirm
  `ProposeAsync` returns `null` (via `FilterToKnownTools` emptying) and the turn falls through to
  `SanitizeFinalText` exactly as it does today — proving this spec is honest that browser tools
  aren't helped, not silently claiming otherwise.
- Confirm `ToolcallerRepairEnabled = false` (the existing default) leaves `ChatEngine`'s behavior
  from Phases A/B completely unchanged — Path 4 must be a true no-op when the setting is off.

---

## 4. Open questions requiring an explicit decision

1. **Role-token approximation (§2.3).** `SwarmWorkerRole.Researcher` is a judgment call, not a
   validated choice — nothing in the training data confirms it's the best fit. Worth revisiting
   once there's real usage data on repair-lane hit rate from OrcChat specifically.
2. **Should a repair-lane attempt that itself fails be distinguishable from "no repair was even
   tried"?** Today `SanitizeFinalText`'s warning text doesn't know whether Path 4 ran and failed
   vs. never ran at all (disabled, or no known tools). A more informative warning could name which
   case occurred — deferred as a UX polish, not required for Phase A-C to be correct.
3. **Default-on vs. opt-in in OrcChat**, once Phases A-C land and have real usage evidence. Not
   decided here (§0.3) — a decision for after there's something to evaluate, matching how the
   Native Runtime v2.0 default-flip was made against real HIVE validation evidence rather than
   speculatively.
