# TheOrc Native Runtime — v2.0 Production-Readiness Spec

> **Status:** Design / specification only — this document itself defines requirements and
> phases; it does not implement them. **No implementation lands with this document.** Each
> phase below is implemented and reviewed in its own later PR.
>
> **Implementation status (updated as phases land, not on every edit — check PR history for
> the authoritative record):** as of 2026-07-19, [Phase A](#phase-a--authoritative-fail-closed-admission-boundary)
> (admission boundary, PR #70), [Phase B](#phase-b--real-vram-budget--overhead-aware-estimate)
> (live VRAM budget — the read-side only; the cost-*estimate* side was explicitly deferred, PR
> #72), and [Phase C](#phase-c--real-telemetry-surfacing) (real telemetry, PR #73) are landed.
> [Phase D](#phase-d--real-model-native-path-proof-lane) and Phase B's deferred estimate work
> remain open. This section is the one place in the document allowed to describe *shipped*
> state — the rest of the spec below is design intent for later PRs, per each phase's own
> `[Verified]`/`[Proposed]` markers at time of writing.
>
> **Scope discipline:** this spec does **not** change the product's default runtime.
> Ollama remains the default and the fallback target. See [§0.3 Explicitly out of scope](#03-explicitly-out-of-scope).
>
> **Author basis:** written against the actual code under `OrchestratorIDE/Core/Runtime/`
> (verified, cited by `file:line`), plus the four authoritative documents linked in §0.2.
> Where a claim is verified against code it is marked **[Verified]**; where it is a
> proposal for later work it is marked **[Proposed]**.

---

## 0. Purpose, sources, and scope

### 0.1 Purpose

The [ROADMAP](ROADMAP.md)'s v2.0 direction names the foundation-hardening work still open
for the native runtime, verbatim:

> "keep proving the real-model path, surface SessionManager/AdapterManager-backed
> telemetry, wire OrcScheduler into AdapterManager, and do not make native the main
> chat/swarm default yet."

This spec turns that sentence into a phased, verifiable implementation plan. It covers
exactly four workstreams:

1. **[§1](#1-orcscheduler--adaptermanager-integration-authoritative-admission-boundary)** — Wire `OrcScheduler` admission into `AdapterManager` so VRAM admission is a single authoritative, un-bypassable boundary.
2. **[§2](#2-real-runtime-telemetry)** — Surface production-meaningful telemetry from real `SessionManager`/`AdapterManager` state, not placeholders.
3. **[§3](#3-real-model-native-path-proof)** — Prove the native path with a real model beyond the current opt-in smoke lane.
4. **[§4](#4-phased-implementation-roadmap)** — Break the above into small, independently reviewable phases, each with a Definition of Done.

### 0.2 Authoritative sources (linked, not duplicated)

This spec **references** the following and does not restate their decisions. Where it
must diverge, it says so explicitly (see [§0.4](#04-known-contradictions--stale-references)).

| Source | What it owns |
|---|---|
| [`docs/RUNTIME_PHASE0_SPEC.md`](RUNTIME_PHASE0_SPEC.md) | The runtime contracts (`IModelRuntime`/`ILocalModelRuntime`), the DI decision (§4), the LoRA hot-swap spike verdict (§7), and the `AdapterManager` per-role-context design (§7a). **This spec builds on it; it does not re-decide those.** |
| [`docs/RUNTIME_SUPPORT_MATRIX.md`](RUNTIME_SUPPORT_MATRIX.md) | The four runtime lanes, which is default/opt-in/fallback, and the real `NativeWithFallbackRuntime` fallback mechanics. |
| [`docs/CURRENT_STATE.yaml`](CURRENT_STATE.yaml) | The machine-readable "is X actually shipped?" status vocabulary. `native_runtime: opt-in`. |
| [`docs/ROADMAP.md`](ROADMAP.md) | The v2.0 phase table (Phases 0–5) and ranked function priorities. |
| [`docs/NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md`](NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md) | The adjacent, **separate** track: browser/OCR/workspace/shell/artifact function packs. Related but out of scope here — this spec hardens the runtime *foundation*, not the tool surface on top of it. |

### 0.3 Explicitly out of scope

This spec and its near-term implementation PRs **must not**:

- Make the native runtime the default.
- Make Ollama optional, or remove/weaken the existing Ollama path.
- Add automatic silent fallback from native execution to Ollama.
- Claim multi-machine HIVE readiness without live evidence.
- Redesign unrelated Context Fabric, training, UI, or HIVE systems.
- Implement the runtime changes described here.

The eventual default-runtime transition is documented **only** as a later, separately
gated milestone — see [§6](#6-later-milestone-default-runtime-flip-hive-gated). Completing
the foundation phases below does **not** authorize that flip.

### 0.4 Known contradictions & stale references

These were found during research and are surfaced rather than silently resolved. Where
sources disagree, this spec adopts the **more conservative** framing.

1. **Phase 4 "wired in" overstatement.** [`docs/RUNTIME_PHASE0_SPEC.md`](RUNTIME_PHASE0_SPEC.md)
   §0 and §6 describe `OrcScheduler` (Phase 4) as "IMPLEMENTED" and "real and wired in."
   The [ROADMAP](ROADMAP.md) Phase 4 row is more accurate: *"🔶 Started … Still not wired
   into AdapterManager beyond `RuntimeOrchestrator`'s own gate."* **This spec adopts the
   ROADMAP framing.** The scheduler decision function exists and is wired into
   `RuntimeOrchestrator.EnsureAdmitted`, but it is not the sole authoritative boundary —
   see [§1.2](#12-the-gaps). Anyone reading the Phase 0 spec's completeness claims
   should treat them as optimistic relative to the gaps documented here.
2. **Correction (this spec's own earlier claim was wrong):** an initial draft of this document
   claimed the `THEORC_TEST_GGUF` opt-in role-runtime smoke lane the [ROADMAP](ROADMAP.md) Phase 3
   row references was "not present in the current source tree." That was a search error, found
   and corrected during Phase A implementation. **The lane is real.**
   `OrchestratorIDE.UnitTests/NativeRuntimeTestSupportTests.cs` gates three lanes behind
   `THEORC_TEST_GGUF` — the native smoke lane, the native/Ollama parity lane, and the native
   role-runtime smoke lane (`Assert.Ignore` when unset, `Assert.Fail` if set to a non-GGUF path).
   These, plus `Tools/ContextFabricBench/Program.cs` and `ContextFabricFeasibilityRunner`, are the
   real-model harnesses that exist today. [§3](#3-real-model-native-path-proof)'s standardized
   lane should build on `THEORC_TEST_GGUF`'s existing gating convention rather than reinvent one.
3. **`SessionManager` "adapter pending" wording is stale.** `SessionManager` still describes
   adapters as *"pending AdapterManager support"* (`SessionManager.cs:26-27`, and the load
   message at `SessionManager.cs:201`), and its snapshot exposes `HasPendingAdapter`. The
   `AdapterManager` now ships and applies adapters. [§2](#2-real-runtime-telemetry) corrects
   this as part of the telemetry work.

---

## 1. OrcScheduler ↔ AdapterManager integration (authoritative admission boundary)

**Goal:** a single authoritative VRAM-budget admission boundary that native model and
adapter work cannot begin without passing, cannot be bypassed by an alternate call path,
and fails **closed** when admission state is missing, inconsistent, or unavailable.

### 1.1 Current admission-control path

Admission is enforced today in exactly one place:

- `RuntimeOrchestrator.EnsureAdmitted` (`RuntimeOrchestrator.cs:200`) calls
  `IOrcScheduler.TryAdmit` (`OrcScheduler.cs:84`) inside `GetConversationForBindingAsync`,
  under a single `_admissionGate` `SemaphoreSlim` held across the **entire** check → load →
  commit pipeline (`RuntimeOrchestrator.cs:145-184`).
- Reservations are tracked per role in `_reservedByRole`, each tagged with the
  `WeightsGeneration` observed **after** its load succeeded
  (`RuntimeOrchestrator.cs:83`, `:171-176`). A later admission only counts another role's
  reservation if its tagged generation still matches the runtime's current generation
  (`RuntimeOrchestrator.cs:218-223`) — a stale entry (torn down by an intervening reload)
  is excluded.
- A denial throws `RuntimeAdmissionDeniedException` (`RuntimeOrchestrator.cs:225-227`,
  `:344`), which carries the binding, budget, and decision for diagnostics.
- `OrcScheduler.TryAdmit` is a **pure decision function** — it holds no ledger; the caller
  passes the current `VramBudget` snapshot in on every call (`OrcScheduler.cs:23-33`). The
  reservation ledger lives in `RuntimeOrchestrator`, by design. **This ownership split is
  intentional and is preserved by this spec.**

The only production construction path today is
`MainWindow.axaml.cs → NativeRoleRuntime → RuntimeOrchestrator → AdapterManager`
(`IRoleRuntime.cs:104` constructs the orchestrator; `RuntimeOrchestrator.cs:162-164`
is the only caller of `AdapterManager.CreateConversationAsync`).

### 1.2 The gaps

**Gap 1 — the admission boundary is bypassable by construction.** `AdapterManager` is a
`public` class with a `public` constructor (`AdapterManager.cs:109`) and `public`
conversation-minting methods `CreateConversationAsync`/`RebindRoleAsync`
(`AdapterManager.cs:117`, `:127`). These have **no VRAM or scheduler awareness at all** —
`OrcScheduler`'s own class doc states it plainly (`OrcScheduler.cs:11-13`): AdapterManager
*"will happily try to build a persistent executor for every role requested, regardless of
whether there's actually room."* Nothing structural forces callers through
`RuntimeOrchestrator.EnsureAdmitted`; today only one caller exists, but a future caller
constructing `new AdapterManager(runtime)` directly (or calling `CreateConversationAsync`
on one) mints a native context with zero admission.

**Gap 2 — admission is fail-*open* when budget/scheduler are absent.** `EnsureAdmitted`
early-returns (no-op) when `scheduler` or `budgetProvider` is null
(`RuntimeOrchestrator.cs:202-203`). And the production wiring passes both as null whenever a
VRAM budget cannot be derived: `MainWindow.axaml.cs:2289-2290` sets
`scheduler: budget is null ? null : …`, and `TryBuildNativeHiveBudget`
(`MainWindow.axaml.cs:2302-2310`) returns null when `DetectedVramGb <= 0`. **Consequence:**
on any machine where VRAM detection fails, the native runtime loads models with **no
admission control whatsoever** — the only signal is a one-line Activity Log Warning
(`MainWindow.axaml.cs:2261-2263`). This is the inverse of the required invariant.

**Gap 3 — the cost estimate materially under-counts native VRAM.** The budget is a static
`VramBudget(totalBytes, ReservedBytes: 0)` derived from `DetectedVramGb`
(`MainWindow.axaml.cs:2308-2309`), and `OrcScheduler.EstimateRequiredBytes` uses **GGUF file
size on disk** as the VRAM proxy (`OrcScheduler.cs:107-119`). This ignores KV-cache and
recurrent-state ("rs cache") overhead that `AdapterManager`'s own comments quantify as
large and, on some architectures, dominant: rs-cache is ~50 MB/slot; `SeqMax=240` reserved
~12 GB and OOM-crashed a 16 GB GPU on Qwen3.5-9B-Q8_0; even the current `SeqMax=40` reserves
~2 GB (`AdapterManager.cs:73-87`). A file-size-only estimate can therefore admit a load that
does not actually fit.

### 1.3 Target design

Prefer the **smallest** design that centralizes the invariant. No new abstraction layer, no
DI container (per [`docs/RUNTIME_PHASE0_SPEC.md`](RUNTIME_PHASE0_SPEC.md) §4's
Option A decision), no stateful ledger inside the scheduler.

**Ownership (unchanged where already correct):**

| Component | Responsibility |
|---|---|
| `OrcScheduler` | Pure decision: given a binding and a `VramBudget` snapshot, admit or deny. No state. **Keep as-is.** |
| `RuntimeOrchestrator` | The **single** authoritative admission boundary. Owns the reservation ledger (`_reservedByRole`), the admission gate, reservation/commit/rollback, and the `WeightsGeneration` staleness filter. **All native conversation minting must flow through it.** |
| `AdapterManager` | Native context lifecycle only (per-role persistent executors, `TrackedConversation` refcounting, recycle/rebuild). **Must not be reachable as an admission-bypassing minting entry point.** |
| `SessionManager` | Persistent base-model load; no admission role. |

**Required changes:**

1. **Close the bypass (Gap 1).** Constrain `AdapterManager` conversation-minting so it
   cannot be invoked outside the admission boundary. The minimal options (a later PR picks
   one, smallest-first):
   - **1a (preferred):** make `AdapterManager`'s minting methods `internal` and route the
     single existing caller through `RuntimeOrchestrator` (already the case), so external
     assemblies cannot mint unadmitted. Since `Core/Runtime` compiles into
     `OrchestratorIDE.Avalonia.dll` (per [`RUNTIME_PHASE0_SPEC.md`](RUNTIME_PHASE0_SPEC.md)
     §7a), `internal` is sufficient with no `InternalsVisibleTo` needed for production code;
     tests already in the same assembly retain access.
   - **1b (if 1a is insufficient):** require minting to carry an admission token that only
     `RuntimeOrchestrator.EnsureAdmitted` can issue — a compile-time guarantee that a
     conversation was admitted. Heavier; use only if a legitimate second caller needs
     `AdapterManager` without `RuntimeOrchestrator`.
2. **Make admission fail-closed (Gap 2).** When native execution is *requested* but no
   scheduler/budget is configured, the runtime must **not** silently proceed. Required
   behavior: either (a) deny with an explicit `RuntimeAdmissionDeniedException`-class error
   naming "admission state unavailable," or (b) require an explicit, logged, user-visible
   opt-out (e.g. an `AllowUnbudgetedNativeExecution` setting, default off) to proceed
   without admission. Silent no-op admission is removed. The production wiring
   (`MainWindow`, `HiveService`, daemon) must surface budget-derivation failure as a
   blocking condition for native execution, not a warning that precedes an unadmitted load.
3. **Strengthen the estimate (Gap 3).** Replace file-size-only estimation with a
   KV/rs-cache-aware estimate parameterized by the actual `RuntimeOptions.ContextLength` and
   `SeqMax`, and replace the static `ReservedBytes: 0` total with a **measured-free-VRAM**
   baseline where the platform can report it (falling back conservatively, not optimistically,
   where it cannot). Detail deferred to [Phase B](#phase-b--real-vram-budget--overhead-aware-estimate);
   this section only fixes the boundary, Phase B fixes the numbers.

**Reservation lifecycle (make the existing behavior explicit and testable):**

| Step | Behavior | Source / target |
|---|---|---|
| Reserve | Admission decision taken under `_admissionGate`; **no** ledger write yet (check is pure). | **[Verified]** `RuntimeOrchestrator.cs:195-228` |
| Commit | Ledger entry written **only after** load + conversation build both succeed, tagged with post-load `WeightsGeneration`. | **[Verified]** `RuntimeOrchestrator.cs:166-176` |
| Rollback | Nothing to roll back on failure — no entry is written until success (`RuntimeOrchestrator.cs:79-82`). A failed admission costs nothing. | **[Verified]** |
| Release | On role rebind/reload/dispose, stale entries are excluded by the generation filter and cleared on `DisposeAsync` (`RuntimeOrchestrator.cs:258-259`). **[Proposed]** add an explicit per-role release on `TrackedConversation` teardown so the ledger reflects idle roles, not just generation churn. | **[Verified]** partial / **[Proposed]** completion |
| Cancellation | `ct` observed on the gate wait and re-checked post-wait (`RuntimeOrchestrator.cs:145`, `:153`); a cancelled admission writes no entry. | **[Verified]** |
| Exception | Load/build failure throws before commit; `RuntimeAdmissionDeniedException` on denial. | **[Verified]** |

**Concurrency & race handling [Verified, preserve]:** `_admissionGate` serializes the whole
admission pipeline so two roles cannot both observe "nothing reserved yet" and over-admit
(the TOCTOU fix, `RuntimeOrchestrator.cs:52-77`). The separate `_telemetryGate` guards the
dictionary against concurrent reads from `GetReservationSnapshot`
(`RuntimeOrchestrator.cs:85-94`). Any Phase A change must preserve both.

**Lane-awareness [Verified, preserve]:** Boss/Reviewer are `Interactive`; Worker/Researcher
are `Background` (`OrcScheduler.cs:56-60`, `:89-91`). Lane governs queue priority under
contention, not whether something is admitted. A change treating all lanes uniformly is a
regression.

### 1.4 Verification required for §1 (implemented in [Phase A](#phase-a--authoritative-fail-closed-admission-boundary))

- Unit test: `AdapterManager` minting is not reachable from outside the admission boundary
  (compile-time for 1a; token check for 1b).
- Unit test: native execution requested with null budget/scheduler → explicit denial (or
  the explicit opt-out path), **never** a silent load.
- Unit test: two roles whose combined estimate exceeds the budget → the second is denied
  with a correct-numbers `RuntimeAdmissionDeniedException` (proves the ledger cannot be
  bypassed and cannot over-admit).
- Unit test: a denied admission writes no ledger entry (reserve/rollback correctness).
- End-to-end `/verify` — see [Phase A DoD](#phase-a--authoritative-fail-closed-admission-boundary).

---

## 2. Real runtime telemetry

**Goal:** surface production-meaningful telemetry from **real** `SessionManager` and
`AdapterManager` state — not placeholders, inferred values, or prototype-only counters —
and keep it internally consistent across failures and cleanup. Reuse existing logging,
`RuntimeHealth`/`RuntimeStats`/snapshot patterns; **do not** introduce a broad observability
framework (the codebase does not require one).

### 2.1 Authoritative state today

| State | Where it lives | Authoritative? |
|---|---|---|
| VRAM reservation (per-role bytes, total/reserved/available, generation-filtered) | `RuntimeOrchestrator.GetReservationSnapshot()` → `RuntimeReservationSnapshot` (`RuntimeOrchestrator.cs:283-320`, `:338-342`) | **Yes** — real admission state. **No UI/diagnostics consumer found.** |
| Per-role measured perf (tok/s, TTFT) | `NativeRoleRuntime._lastStatsByRole`, populated after each stream (`IRoleRuntime.cs:315-338`) | **Yes** — real measured values. |
| Per-role health (available, active model, adapter, last failure) | `NativeRoleRuntime._lastHealthByRole` (`IRoleRuntime.cs:324-338`, `:423-443`) | **Yes.** |
| Session (current role/binding, last load, health, stats) | `SessionManager.GetSnapshot()` → `RuntimeSessionSnapshot` (`SessionManager.cs:14-20`, `:92-102`) | Partly — `HasPendingAdapter` wording is stale ([§0.4](#04-known-contradictions--stale-references)). |
| Adapter residency & sequence-slot pressure (`ActiveCount`, `ConversationsCreated`, `ForceRecycle`, recycle/hard-limit thresholds) | `AdapterManager.RoleEntry` — **all internal/private** (`AdapterManager.cs:339-393`, `:67`, `:87`) | **Yes, but invisible** outside the class. |

### 2.2 Gaps

- **Placeholder VRAM number.** `RuntimeStats.EstimatedVramBytes` is set from
  `EstimateBindingBytes` = base + adapter **file sizes** (`IRoleRuntime.cs:322`, `:415-421`)
  — an inferred estimate, not measured VRAM. This is exactly the "inferred value" class the
  goal excludes. (`IModelRuntime.cs:84` documents the field as null on Ollama; the native
  path should report measured bytes or null, not a file-size proxy dressed as VRAM.)
- **Reservation snapshot has no consumer.** `GetReservationSnapshot` exists but nothing in
  the UI/diagnostics surfaces it. The admission state that Phase A makes authoritative is
  currently unobservable.
- **Adapter lifecycle is invisible.** Residency, eviction (recycle), sequence-slot pressure,
  and per-role active-conversation counts are all internal to `AdapterManager`. There is no
  way for diagnostics to detect a role stuck with outstanding conversations near the
  `SequenceHardLimit`, or a degraded (`ForceRecycle`) executor.
- **Stale session wording.** `HasPendingAdapter` and "adapter is pending AdapterManager
  support" misrepresent shipped behavior.

### 2.3 Target

Required metrics / status fields / lifecycle events, sourced from the authoritative state
above (add read-only accessors; do not change lifecycle logic):

- **Session lifecycle:** creation, activation, reuse (`ReusedExistingSession` already exists,
  `SessionManager.cs:149-156`), failure, disposal signals. Correct the stale
  `HasPendingAdapter` to reflect actually-applied adapters.
- **Adapter lifecycle:** loading, activation, residency (which roles have live executors),
  eviction/recycle (surface `ForceRecycle` and recycle events), sequence-slot pressure
  (`ConversationsCreated` vs `SequenceRecycleThreshold`/`SequenceHardLimit`), release,
  failure. Add minimal read-only accessors on `AdapterManager` (e.g. a per-role residency
  snapshot record) — **without** exposing the native handles.
- **VRAM admission:** budget total, reserved, committed (active reservations), and rejected
  admissions (count / last reason). `RuntimeReservationSnapshot` already carries total/
  reserved/available; add a rejected-admission counter and wire the snapshot to diagnostics.
- **Measured VRAM:** replace the file-size `EstimatedVramBytes` with a measured value where
  the platform reports it, else null (never a proxy presented as measurement).
- **Consistency across failure/cleanup:** telemetry must reflect release after disposal and
  must not report a role as resident after its executor is torn down (the generation filter
  already does this for reservations — extend the same discipline to residency).
- **Stale-state detection:** expose enough to detect a role stuck near the hard limit with
  outstanding conversations, and a session whose base load failed.

**Keep internal until there is a demonstrated consumer:** raw per-conversation native
sequence ids, KV-cell-level accounting, and any metric with no diagnostics/UI surface. Do
not build dashboards no one reads.

**Surface to existing consumers:** the Activity Log and the existing Settings/diagnostics
surfaces in `MainWindow.axaml.cs` (the same place the native backend verdict and fallback
warnings already appear) — reuse those patterns, not a new framework.

### 2.4 Verification required for §2 (implemented in [Phase C](#phase-c--real-telemetry-surfacing))

- Unit test: reservation/residency counts return to baseline after `DisposeAsync` (no leaked
  reservation or resident role).
- Unit test: telemetry remains consistent across an induced failure + cleanup (a failed load
  leaves no resident role; a torn-down generation drops stale reservations).
- Unit test: `EstimatedVramBytes` is null (not a file-size proxy) when measurement is
  unavailable.
- End-to-end `/verify` — see [Phase C DoD](#phase-c--real-telemetry-surfacing).

---

## 3. Real-model native-path proof

**Goal:** prove the native path with a real model beyond the current opt-in smoke lane,
exercising the actual runtime lifecycle and verifying native jobs do not silently fall back
to Ollama.

### 3.1 Test taxonomy (distinct lanes, do not conflate)

| Lane | Runs in ordinary CI? | Real model? | Purpose |
|---|---|---|---|
| **Unit / component** | Yes | No | Pure logic with a mockable seam — `OrcScheduler.TryAdmit`, `ModelDepot` resolution, `AdapterManager.BindingMatches`, budget math. **[Verified]** these exist: `OrcSchedulerTests`, `ModelDepotTests`, `AdapterManagerTests`, `SessionManagerTests`, `RuntimeOrchestratorTests`, `NativeWithFallbackRuntimeTests`, `NativeRuntimeTestSupportTests` in `OrchestratorIDE.UnitTests/`. |
| **Deterministic integration** | Yes | No (scripted runtime) | Wire multiple components with a scripted/fake runtime (e.g. `ContextFabricScriptedRuntime`) to prove control flow without native objects. |
| **Opt-in hardware-dependent** | No (opt-in flag) | Yes | Real GGUF load + generation on a machine with a GPU. **[Verified]** the real load+adapter+generation success path is **not** automated — "no mockable seam for the native LLamaSharp objects" (`RuntimeOrchestrator.cs:39-42`). Today proven only via the §7 spike harness + manual Settings smoke. |
| **Real-model E2E native-runtime lane** | No (opt-in, hardware-gated) | Yes | **[Proposed]** the new standardized lane this section defines — exercises the whole runtime lifecycle, retains evidence. |
| **Manual / machine-specific** | No | Yes | Evidence that cannot safely run in ordinary CI (specific VRAM sizes, multi-GPU). Recorded, not automated. |

### 3.2 What the real-model E2E lane must exercise

A single opt-in, hardware-gated lane, gated behind the existing `THEORC_TEST_GGUF` convention
`NativeRuntimeTestSupportTests.cs` already uses (see [§0.4](#04-known-contradictions--stale-references))
rather than a new env var — extend that file's lanes, don't fork a parallel gating mechanism.
The lane drives the actual lifecycle:

1. **Model discovery & resolution** through `ModelDepot.Scan` + `ResolveRole`
   (`ModelDepot.cs:102`, `:163`).
2. **Native runtime selection** — confirm the native path is chosen, not Ollama.
3. **Session creation and disposal** via `SessionManager` / `RuntimeOrchestrator`.
4. **Adapter admission and lifecycle** where a role LoRA is present (`AdapterManager`
   create → use → dispose, with `TrackedConversation` refcounting).
5. **Real inference** (or another meaningful native workload) producing real tokens.
6. **Cancellation and failure cleanup** — cancel mid-stream, assert resources and the
   reservation ledger release exactly once.
7. **Telemetry from actual manager state** — read `RuntimeReservationSnapshot` and per-role
   stats mid-flight and after disposal (ties to [§2](#2-real-runtime-telemetry)).
8. **No silent fallback** — a job explicitly routed native that fails must surface a native
   error, **not** Ollama output. See [§5.4](#54-fail-closed-native-execution).

### 3.3 Prerequisites, fixtures, evidence

- **Model/fixture selection:** a small real GGUF that fits the smallest supported reference
  GPU; criteria (size, quant, architecture) recorded in the lane's README, not hard-coded to
  one machine's model. Do **not** hard-code assumptions unvalidated against available
  hardware — preserve explicit calibration/config points (context length, GPU layers,
  `SeqMax`) where real hardware behavior varies.
- **Hardware assumptions:** documented per reference box (e.g. the machines already in the
  fleet), including the known rs-cache-vs-VRAM interaction (`AdapterManager.cs:73-87`).
- **Evidence to retain:** the run transcript, resolved binding, admission decisions,
  telemetry snapshots (before/mid/after), tok/s + TTFT, and a pass/fail summary — same
  evidence-grade discipline as the CF-7 gate runs and the §7 spike (whose transcripts are
  committed under `.grok/spike-assets/`).
- **Timeouts:** explicit per-stage budgets; a hang fails the stage with a diagnostic, it does
  not block indefinitely.
- **Failure diagnosis:** on failure, retain the `THEORC_KVCACHE_DIAGNOSTICS=1` trace
  (`AdapterManager.cs:89-107`, `IRoleRuntime.cs:394-395`) and the last prompt path
  (`GetLastPromptPath`).

### 3.4 Verification required for §3 (implemented in [Phase D](#phase-d--real-model-native-path-proof-lane))

- The lane runs green on a real reference box and emits a retained evidence artifact.
- A negative test: a native-routed job with native prerequisites unavailable fails closed —
  explicit native error, no Ollama substitution.
- End-to-end `/verify` — see [Phase D DoD](#phase-d--real-model-native-path-proof-lane).

---

## 4. Phased implementation roadmap

Ordered so foundational correctness and observability land **before** broader enablement.
Each phase is one or more independently reviewable PRs. Unrelated runtime changes are **not**
combined into one phase merely to reduce PR count.

### What `/verify` means here

`/verify` is not a checkbox. For each phase it names the **specific real flow to drive and
observe** end-to-end — running the affected native path and confirming the observed behavior,
not merely that tests/typecheck pass. A phase is **not** complete on compile + isolated tests
alone; it ships with passing targeted tests **and** a recorded `/verify` exercise **and**
negative-path coverage for its safety-critical behavior.

---

### Phase A — Authoritative, fail-closed admission boundary

- **Purpose / outcome:** native model & adapter work cannot begin without passing one
  authoritative admission boundary; missing/unavailable admission state fails **closed**.
- **Components:** `RuntimeOrchestrator`, `AdapterManager` (visibility/token), production
  wiring in `MainWindow.axaml.cs`, `HiveService.cs`, and the daemon.
- **Dependencies:** none (first phase).
- **Safety invariants:** [§5.2 VRAM admission](#52-vram-budget-admission-control),
  [§5.4 fail-closed](#54-fail-closed-native-execution). Preserve concurrency/lane behavior
  from [§1.3](#13-target-design).
- **Targeted tests:** the four unit tests in [§1.4](#14-verification-required-for-1-implemented-in-phase-a).
- **`/verify`:** drive a native role with a deliberately tiny VRAM budget → observe
  `RuntimeAdmissionDeniedException` (not a load); then drive it with **no** budget/scheduler
  configured → observe explicit failure or the explicit logged opt-out, **never** a silent
  unadmitted load. Record both Activity Log outcomes.
- **Evidence:** test run output + the two `/verify` transcripts.
- **Failure/rollback:** the change is additive to the gate; if a regression appears, the
  offending caller reverts to the pre-Phase-A construction (admission remains at least as
  strong as today, never weaker).
- **Definition of Done:** minting is un-bypassable (compile-time or token); null-budget path
  is fail-closed; over-budget second role is denied with correct numbers; all targeted tests
  pass; both `/verify` exercises recorded; Ollama default unchanged.
- **Non-goals:** changing the *accuracy* of the estimate (that's Phase B); any UI telemetry
  (Phase C).

---

### Phase B — Real VRAM budget & overhead-aware estimate

- **Purpose / outcome:** admission decisions use measured-free VRAM and a KV/rs-cache-aware
  cost estimate, so an admitted load actually fits.
- **Components:** `OrcScheduler.EstimateRequiredBytes`, the `VramBudget` provider in
  `MainWindow`/`HiveService` (replace static `(detectedTotal, 0)`).
- **Dependencies:** Phase A (the boundary must be authoritative before its numbers matter).
- **Safety invariants:** [§5.2](#52-vram-budget-admission-control) — overcommit prevention.
- **Targeted tests:** estimate includes context/`SeqMax`-scaled overhead, not file size
  alone; two roles summing over a measured budget → second denied; degenerate/unknown sizes
  fall back conservatively (never optimistically).
- **`/verify`:** on a reference box, load a role pair that fits and confirm both admit; then
  request a role that pushes past measured-free VRAM and confirm denial with numbers that
  match observed VRAM — and that no OOM/native crash occurs (contrast the historical
  `SeqMax=240` OOM, `AdapterManager.cs:80-86`).
- **Evidence:** the calibration record (estimate vs observed load) for the reference box.
- **Failure/rollback:** if the measured-free source is unreliable on a platform, fall back to
  the conservative detected-total minus a safety margin (still stricter than today's `0`
  reserved) rather than reverting to file-size-only.
- **Definition of Done:** estimate within a documented margin on the reference box; overcommit
  test passes; `/verify` shows a correct-numbers denial with no native OOM.
- **Non-goals:** live GPU dispatch / pipeline queueing (out of scope for v2.0 foundation);
  multi-base-model concurrent residency (`RuntimeOrchestrator.cs:24-35` scope limitation).

---

### Phase C — Real telemetry surfacing

- **Purpose / outcome:** admission state, adapter residency/pressure, and measured VRAM are
  observable in existing diagnostics; no placeholder numbers.
- **Components:** read-only accessors on `AdapterManager` (residency snapshot) and
  `SessionManager` (corrected snapshot), `RuntimeReservationSnapshot` wiring into the Activity
  Log / Settings diagnostics in `MainWindow.axaml.cs`; replace `EstimatedVramBytes` file-size
  proxy.
- **Dependencies:** Phase A (authoritative reservation state) and Phase B (measured VRAM to
  report).
- **Safety invariants:** [§5.3 context lifecycle](#53-correct-context-lifecycle) — telemetry
  must reflect release exactly once; no read may perturb lifecycle (reuse the non-blocking
  `_telemetryGate` pattern, `RuntimeOrchestrator.cs:268-282`).
- **Targeted tests:** the three unit tests in [§2.4](#24-verification-required-for-2-implemented-in-phase-c);
  plus a test that the stale `HasPendingAdapter` wording/field is corrected.
- **`/verify`:** drive a native role; read the reservation + residency snapshot mid-flight
  (role resident, bytes reserved) and after disposal (baseline, nothing resident); induce a
  failure and confirm telemetry shows no phantom resident role.
- **Evidence:** the mid-flight vs post-disposal snapshots.
- **Failure/rollback:** accessors are read-only and additive; revert the diagnostics wiring
  without touching lifecycle if a regression appears.
- **Definition of Done:** reservation/residency/measured-VRAM visible in diagnostics;
  `EstimatedVramBytes` is measured-or-null; stale session wording fixed; consistency tests
  pass; `/verify` snapshots recorded.
- **Non-goals:** a new observability framework; dashboards without a consumer; exposing native
  handles or per-cell KV accounting.

---

### Phase D — Real-model native-path proof lane

- **Purpose / outcome:** a standardized, opt-in, hardware-gated lane proves the whole native
  lifecycle on a real model and proves no silent fallback.
- **Components:** extends the existing `THEORC_TEST_GGUF`-gated lanes in
  `NativeRuntimeTestSupportTests.cs`, reusing `ModelDepot`, `RuntimeOrchestrator`,
  `NativeRoleRuntime`, `ContextFabricBench`-style real-model harness patterns.
- **Dependencies:** Phases A–C (the lane asserts admission, lifecycle, and telemetry behavior
  those phases define).
- **Safety invariants:** all of [§5](#5-mandatory-requirements-traceability) — the lane is
  where they are proven together on real hardware.
- **Targeted tests:** the lane itself (opt-in), plus the negative fail-closed test from
  [§3.4](#34-verification-required-for-3-implemented-in-phase-d).
- **`/verify`:** run the full lane on a reference box end-to-end (discovery → selection →
  session → admission → adapter → inference → cancellation → telemetry → no-fallback) and
  attach the evidence artifact; separately confirm a native-routed job with native disabled
  fails closed with no Ollama output.
- **Evidence:** the retained run artifact ([§3.3](#33-prerequisites-fixtures-evidence)).
- **Failure/rollback:** the lane is opt-in and CI-gated off by default; a flaky lane is fixed
  or quarantined without affecting the default build.
- **Definition of Done:** lane green on a reference box with retained evidence; negative
  fail-closed test passes.
- **Non-goals:** multi-machine HIVE validation (that is [§6](#6-later-milestone-default-runtime-flip-hive-gated),
  not this lane); making native the default.

---

## 5. Mandatory requirements traceability

Every implementation phase must satisfy all of the following. This table maps each mandatory
rule to where it is addressed.

| Mandatory requirement | Addressed in |
|---|---|
| **Tests + end-to-end `/verify`** per phase (not compile/isolated-tests alone), with recorded evidence and negative-path coverage | [§4 "What `/verify` means"](#what-verify-means-here) + every phase's `/verify` + Evidence + DoD rows |
| **VRAM-budget admission control** — capacity estimation & ownership, reserve/release, concurrent behavior, failure cleanup, overcommit prevention, observable rejection, bypass-proof tests | [§1](#1-orcscheduler--adaptermanager-integration-authoritative-admission-boundary), [Phase A](#phase-a--authoritative-fail-closed-admission-boundary), [Phase B](#phase-b--real-vram-budget--overhead-aware-estimate); detail in [§5.2](#52-vram-budget-admission-control) |
| **Correct context lifecycle** — creation/activation, reuse, cancellation, exceptions, disposal ordering, partial init, replacement/eviction, shutdown, stale-state detection, release-exactly-once | [§5.3](#53-correct-context-lifecycle); enforced by [§1.3 reservation lifecycle](#13-target-design) + [§2](#2-real-runtime-telemetry) residency; verified [Phase C](#phase-c--real-telemetry-surfacing)/[Phase D](#phase-d--real-model-native-path-proof-lane) |
| **Fail-closed native execution** — no silent native→Ollama fallback; explicit native failure with diagnostic context; any future fallback is a separate explicit decision, visible to user + telemetry | [§5.4](#54-fail-closed-native-execution); verified [Phase A](#phase-a--authoritative-fail-closed-admission-boundary) + [Phase D](#phase-d--real-model-native-path-proof-lane) |

### 5.2 VRAM-budget admission control

- **Capacity estimation & ownership:** estimation is `OrcScheduler.EstimateRequiredBytes`
  (pure function, [Phase B](#phase-b--real-vram-budget--overhead-aware-estimate) makes it
  overhead-aware); the reservation ledger is owned by `RuntimeOrchestrator`, never the
  scheduler (`OrcScheduler.cs:26-33`). **[Verified split, preserved.]**
- **Reservation & release semantics:** [§1.3 reservation lifecycle table](#13-target-design).
- **Concurrent-request behavior:** serialized through `_admissionGate`
  (`RuntimeOrchestrator.cs:52-77`). **[Verified.]**
- **Failure cleanup:** commit-after-success ⇒ nothing to roll back on failure
  (`RuntimeOrchestrator.cs:79-82`). **[Verified.]**
- **Overcommit prevention:** the per-role ledger sums other roles' generation-current
  footprints into the budget before each decision (`RuntimeOrchestrator.cs:218-223`);
  [Phase B](#phase-b--real-vram-budget--overhead-aware-estimate) makes the footprint accurate.
- **Observable rejection:** `RuntimeAdmissionDeniedException` carries binding/budget/decision
  (`RuntimeOrchestrator.cs:344-374`); [Phase C](#phase-c--real-telemetry-surfacing) adds a
  rejection counter to telemetry.
- **Bypass-proof tests:** [§1.4](#14-verification-required-for-1-implemented-in-phase-a) —
  minting cannot proceed unadmitted through any call path.

### 5.3 Correct context lifecycle

Native contexts/sessions/models/adapters have explicit ownership and deterministic cleanup:

- **Creation/activation:** `RuntimeOrchestrator` owns `SessionManager` + `AdapterManager`
  from one runtime (`RuntimeOrchestrator.cs:110-111`) — no cross-runtime mismatch. **[Verified.]**
- **Reuse:** `SessionManager.CanReuseCurrentSession` short-circuits a matching base load
  (`SessionManager.cs:234-249`). **[Verified.]**
- **Cancellation / exceptions:** `ct` threaded through admission, load, and streaming;
  failures recorded per role (`IRoleRuntime.cs:147-151`, `:423-443`). **[Verified.]**
- **Disposal ordering:** `AdapterManager` disposed before `SessionManager`, the latter in a
  `finally` so a fault cannot leak the session/runtime
  (`RuntimeOrchestrator.cs:244-254`). **[Verified.]**
- **Partial initialization:** `BuildRoleEntry` disposes/unloads whatever was created if the
  LoRA attach throws (`AdapterManager.cs:415-423`). **[Verified.]**
- **Replacement / eviction:** teardown+rebuild only (no live adapter swap — the §7 hazard);
  refcounted `TrackedConversation` blocks teardown of an in-use executor
  (`AdapterManager.cs:127-212`, `:434-463`). **[Verified.]**
- **Process shutdown / stale-state detection:** `WeightsGeneration` invalidates all entries
  on reload (`AdapterManager.cs:224-237`); sequence-slot recycle/hard-limit guards prevent
  the native slot-exhaustion crash (`AdapterManager.cs:48-87`). **[Verified.]**
- **Release exactly once:** `TrackedConversation.Dispose` decrements in a `finally`
  (`AdapterManager.cs:362-371`, `:448-462`); [Phase C](#phase-c--real-telemetry-surfacing)
  adds tests that reservation + residency return to baseline after disposal.

> **Landmine (carry into every phase):** swapping a LoRA on a live context without
> `MemoryClear()` is silently unsafe ([`RUNTIME_PHASE0_SPEC.md`](RUNTIME_PHASE0_SPEC.md)
> §7). The mitigation — one persistent context per role, adapter bound once at creation — is
> load-bearing. No phase may reintroduce live adapter swapping without restating this hazard
> and showing the `MemoryClear()`.

### 5.4 Fail-closed native execution

- A job explicitly routed to the native runtime **must not** silently fall back to Ollama.
- If native prerequisites, admission, model load, adapter load, or execution fail, the system
  returns an **explicit native-runtime failure** with enough diagnostic context to
  investigate (binding, budget, decision, KV diagnostics, last prompt path).
- **[Verified] existing behavior:** `NativeWithFallbackRuntime.ShouldFallback` excludes
  `RuntimeAdmissionDeniedException` from fallback and only falls back before first observable
  output (`NativeWithFallbackRuntime.cs:178-192`). This is the correct direction and must be
  preserved.
- **[Verified] gap:** fallback (where permitted) is surfaced only as a transient Activity Log
  Warning, not persistent telemetry (per [RUNTIME_SUPPORT_MATRIX](RUNTIME_SUPPORT_MATRIX.md)).
  There are two fallback surfaces — `NativeWithFallbackRuntime` (main chat) and
  `NativeRoleRuntime` via `HiveWorkerAgent` ("logged fallback to configured model runtime").
- **[Proposed]:** any future fallback policy is a **separate, explicit** design decision,
  visible to the user **and** to telemetry (not just a log line). Phase A's fail-closed
  admission and Phase D's negative test enforce that a *native-routed* job does not silently
  become an Ollama job.

---

## 6. Later milestone: default-runtime flip (HIVE-gated)

The transition making native the default runtime is a **separate, later milestone**. It is
**not** authorized by completing Phases A–D. Ollama stays the default and the fallback until
this milestone's criteria are met and an explicit product decision is recorded.

**Measurable entry criteria (all required, with live evidence):**

- Successful native workloads across the intended machine roles.
- Correct capability- and resource-aware scheduling across machines.
- Verified model and adapter lifecycle behavior across machines.
- Failure, cancellation, disconnect, and recovery exercises across machines.
- Consistent telemetry and diagnosability across machines.
- No silent fallback across runtime boundaries.
- Repeatable end-to-end evidence on representative hardware.
- An explicit product decision approving the default-runtime change.

This milestone is gated on **live multi-machine HIVE validation** — it may not be claimed
from single-box results, and completing the foundation phases does not imply it. It aligns
with the ROADMAP's existing position that Ollama stays default *"until the ModelDepot +
installer first-run story is bulletproof and the reviewer/Swarm abstraction leaks are
closed."*

---

## 7. Summary of proposed changes by component

| Component | Verified today | Proposed (later PRs) |
|---|---|---|
| `RuntimeOrchestrator` | Sole admission gate; serialized ledger; generation-tagged reservations; reservation snapshot | Fail-closed on missing budget; explicit per-role release; rejection counter (Phases A, C) |
| `OrcScheduler` | Pure decision fn; file-size estimate; lane assignment | Overhead-aware estimate (Phase B) — stays stateless |
| `AdapterManager` | Per-role persistent contexts; refcounted `TrackedConversation`; recycle/hard-limit guards | Minting made un-bypassable; read-only residency snapshot (Phases A, C) |
| `SessionManager` | Persistent base load; reuse; snapshot | Correct stale "pending adapter" wording (Phase C) |
| `ModelDepot` | Local scan + role resolution | Unchanged (used by the proof lane) |
| `IModelRuntime` / `NativeRoleRuntime` | Contracts; per-role measured stats; text-parsed tool calls | Measured-or-null VRAM instead of file-size proxy (Phase C) |
| Production wiring (`MainWindow`, `HiveService`, daemon) | Constructs native runtime; null scheduler on budget-derivation failure (fail-open) | Fail-closed native execution; real budget provider (Phases A, B) |
| Test/proof lanes | Unit tests per component; real path proven only via spike/manual | Standardized opt-in real-model E2E lane (Phase D) |
