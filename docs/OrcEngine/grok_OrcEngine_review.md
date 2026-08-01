# Grok Adversarial Design Review — OrcEngine Documentation Foundation

> Historical review snapshot: this report is preserved as evidence from 2026-07-18 and was verified against commit `0a31aa0`. Its runtime-default observations are not current product truth. Use [Project Truth](PROJECT_TRUTH.md), [Current State](CURRENT_STATE.yaml), and the product [Runtime Support Matrix](../RUNTIME_SUPPORT_MATRIX.md) for current status. Later decision-log entries also resolved some findings; the original review text remains unchanged for auditability.

## 1. Title and review metadata

| Field | Value |
|---|---|
| Document | `docs/OrcEngine/grok_OrcEngine_review.md` |
| Reviewer | Grok (xAI), adversarial design review |
| Review date | 2026-07-18 (America/Los_Angeles) |
| Repository | `F:\Ai\OrchestratorIDE-dev` |
| Documentation root | `docs/OrcEngine` |
| Branch | `docs/orcengine-foundation` |
| Verified code commit | `0a31aa0eca4464e94550617c97a2938453ad6874` |
| Review mode | Read-only. No implementation. No edits to source or other docs. |
| Only write permitted | This file |
| Prompt basis | User mission packet + `docs/OrcEngine/GROK_REVIEW_PROMPT.md` + `AI_AGENT_REVIEW_PROTOCOL.md` severity schema |

**Evidence classes used below**

| Class | Meaning |
|---|---|
| **code-verified** | Observed in live repository source or package references at the commit above |
| **externally verified** | Retrieved from a primary external source on 2026-07-18 |
| **doc-verified** | Observed in OrcEngine or product documentation text |
| **inferred** | Logical consequence of verified facts; not directly measured |
| **unknown** | Not established by this review |

No automated test suites or broad builds were run for this review. Commands that ran: `git` state queries, file inventory, targeted reads/greps, and primary-source page fetches.

---

## 2. Executive verdict

**Recommendation: PAUSE engine implementation.**

The OrcEngine foundation is a **high-quality research and process corpus**: truth labels, non-goals, oracle-first sequencing, fail-closed product integration posture, and security awareness are unusually disciplined for a from-scratch inference proposal.

It is **not yet a justified product investment**. Three issues dominate:

1. **Product identity conflict.** TheOrc’s live architecture narrative still says Native Runtime is *not* a from-scratch inference engine. OrcEngine proposes the opposite ownership boundary without a recorded product-level superseding decision.
2. **Strategic value is deferred past most of the cost.** Unique-value assessment is scheduled after real-model float32 inference (Phase 3), after synthetic engine, GGUF parser, tokenizer, and oracle work.
3. **Phase 0 is designed but not instantiated.** No model, oracle pin, tolerance profile, or license-cleared artifact exists. The volume of later-phase design (CUDA, quant, ABI, agent-native) can create false readiness.

**Allow:** maintainer product decision; independent reviews; Phase 0 research *after* identity and unique-value gates are addressed.
**Do not allow:** engine source scaffold, frozen C ABI, CUDA/quant implementation, or TheOrc default/runtime replacement work.

**Overall grade of the documentation suite as process design:** strong.
**Overall grade as executable technical specification for Phase 1:** incomplete (intentionally, but still blocking).

---

## 3. Scout report

### 3.1 Root agent instructions

Read: `.agents.md` (canonical cross-tool briefing).

Relevant points:

- TheOrc is local-first; Avalonia is the only desktop shell.
- Native Runtime is described as `IModelRuntime` over Ollama / llama.cpp-server / LLamaSharp — control-plane migration, not a tensor engine rewrite.
- Hard rules, SQLite conventions, and review workflow govern product work.

### 3.2 Git and working tree

| Item | Observed |
|---|---|
| Branch | `docs/orcengine-foundation` |
| HEAD | `0a31aa0eca4464e94550617c97a2938453ad6874` |
| Tip subject | `feat(runtime): context-aware VRAM cost estimate from GGUF headers (#76)` |
| vs `origin/master` | Identical tip (`git rev-list --left-right --count origin/master...HEAD` → `0 0`) |
| Tracked modifications | None |
| Untracked (user-owned; not modified) | `docs/OrcEngine/` (entire suite), `training_pit/datasets/toolcaller/`, `training_pit/datasets/toolcaller_v0.meta.json` |

**Scout note:** OrcEngine docs are **untracked**. Claims that the foundation was “verified against” commit `0a31aa0…` are valid for **code anchors**, not for “these docs are part of that commit.”

### 3.3 Expected foundation inventory

Expected: **30** Markdown/YAML foundation files.
Observed foundation files (excluding this review): **30**.
This review file makes the directory count 31 when present.

### 3.4 Current system boundary — mission claims vs live code

| Mission claim | Verdict | Evidence class |
|---|---|---|
| `OllamaRuntime` is Ollama adapter / compatibility lane | Confirmed | code-verified: `OrchestratorIDE/Core/Runtime/OllamaRuntime.cs` |
| Ollama is default | Confirmed | code-verified: `AppSettings.Backend` default `InferenceBackend.Ollama` (`AppSettings.cs` L23) |
| Experimental native main chat / HIVE worker off by default | Confirmed | code-verified: `ExperimentalNativeMainChatEnabled` / `ExperimentalNativeHiveWorkerEnabled` default `false` (`AppSettings.cs` L305–314) |
| `LlamaCppServerRuntime` is out-of-process llama.cpp-server | Confirmed | code-verified: `LlamaCppServerRuntime.cs` |
| `LLamaSharpRuntime` is in-process GGUF via LLamaSharp 0.27.0 | Confirmed | code-verified: `LLamaSharpRuntime.cs`; packages in `OrchestratorIDE.NativeRuntime.csproj` L87–92 |
| LLamaSharp based on llama.cpp | Confirmed | externally verified: SciSharp LLamaSharp README; v0.27.0 maps to llama.cpp `3f7c29d3…` |
| TheOrc owns control-plane lifecycle, prompts, tools, streaming, VRAM admission, telemetry, fallback | Confirmed (with nuance) | code-verified: `RuntimeOrchestrator`, `AdapterManager`, `SessionManager`, `OrcScheduler`, `NativePromptBuilder`, `NativeWithFallbackRuntime` |
| Computation plane (full GGUF tensor exec, kernels, native KV, quant matmul) not owned by TheOrc | Confirmed for LLamaSharp path | code-verified + inferred from LLamaSharp dependency |
| `GgufMetadataReader` is header subset for estimates, not full loader | Confirmed | code-verified: `GgufMetadataReader.cs` |
| OrcEngine has no implementation | Confirmed | doc-verified + directory evidence: no engine sources |
| OrcEngine must not replace defaults | Consistent with code defaults; no OrcEngine path exists | code-verified + doc-verified |

**Product-doc staleness (not OrcEngine’s fault):** `.agents.md` Native Runtime Phase 4 text still implies OrcScheduler is “not yet wired into AdapterManager.” Live code and `.grok/PROJECT_TRUTH.md` show `OrcScheduler.TryAdmit` is used from `RuntimeOrchestrator.EnsureAdmitted`. **Live code is authoritative.**

### 3.5 Exact documentation files reviewed

Every foundation file in §4. This review excludes itself from “foundation inventory” but is the sole written output.

---

## 4. Complete reviewed-file inventory

| Filename | Reviewed | Primary purpose | Consistency | Notes |
|---|---|---|---|---|
| `README.md` | yes | Index, truth labels, pipeline | Good | Best short definition of “from scratch” |
| `PROJECT_VISION.md` | yes | Intent and stop criteria | Good | Unique-value list is hypothesis-only |
| `PROJECT_TRUTH.md` | yes | Verified vs proposed | Strong | Correctly pins commit; no implementation claim |
| `SCOPE_AND_NON_GOALS.md` | yes | Scope limits | Strong | Compatibility tuple is sound policy |
| `ARCHITECTURE.md` | yes | Layers and ownership | Mostly good | C API listed as layer 1 while deferred |
| `THEORY_AND_ASSUMPTIONS.md` | yes | Equations and falsifiers | Mixed | Sketch incomplete for GQA/RoPE detail |
| `RESEARCH_QUESTIONS.md` | yes | Research backlog | Strong | P0 questions correctly block progress |
| `ENGINEERING_ROADMAP.md` | yes | Phases and gates | Good w/ caveats | Strategic stop too late (Phase 3) |
| `PHASE_0_REFERENCE_ORACLE.md` | yes | Oracle methodology | Strong design | Not instantiated |
| `MODEL_FORMAT_AND_GGUF.md` | yes | GGUF reader design | Strong | Needs hard resource limits table |
| `TENSOR_ENGINE_DESIGN.md` | yes | Tensor/operators | Good | Eager-first correct |
| `CPU_BACKEND_DESIGN.md` | yes | CPU path | Good | Scalar→BLAS→SIMD order correct |
| `CUDA_BACKEND_DESIGN.md` | yes | Future GPU | Careful, early | Layout/repro needs more teeth |
| `TOKENIZER_AND_PROMPT_PIPELINE.md` | yes | Text boundary | Good | No pinned family yet |
| `KV_CACHE_AND_CONTEXT_DESIGN.md` | yes | Cache/context | Strong | Best systems design doc |
| `SAMPLING_AND_DECODING.md` | yes | Greedy + later | Good | Tie rule explicit |
| `QUANTIZATION_PLAN.md` | yes | Quant sequence | Good | Weight vs KV quant separated |
| `THEORC_INTEGRATION.md` | yes | Product adapter | Good | Thin adapter correct |
| `TEST_STRATEGY.md` | yes | Verification | Strong | Anti false-green prose good |
| `BENCHMARK_STRATEGY.md` | yes | Perf honesty | Strong | Cold/warm and fallback rules good |
| `RISK_REGISTER.md` | yes | Risks | Strong | R-001/R-020 correctly critical |
| `SECURITY_AND_SAFETY.md` | yes | Trust boundary | Strong | Parser threat model serious |
| `LICENSING_AND_ATTRIBUTION.md` | yes | Compliance | Good | No false clean-room claim |
| `AI_AGENT_REVIEW_PROTOCOL.md` | yes | Review governance | Good | Severity schema used here |
| `CLAUDE_REVIEW_PROMPT.md` | yes | Reviewer packet | Good | Complementary lens |
| `GROK_REVIEW_PROMPT.md` | yes | Adversarial packet | Good | Matches this mission |
| `DECISION_LOG.md` | yes | ADRs | Mixed | 006/008/009 proposed only |
| `OPEN_QUESTIONS.md` | yes | Open decisions | Strong | Honest blockers |
| `GLOSSARY.md` | yes | Terms | Good | Backend/runtime disambiguation |
| `CURRENT_STATE.yaml` | yes | Machine status | Strong w/ gaps | Omits proposed ADRs |

**System-level consistency:** High internal coherence. Failures are strategic timing, product-identity tension, Phase-0 instantiation gaps, and a few authority mismatches—not casual self-contradiction among OrcEngine files.

---

## 5. BLOCKER findings

Severity **BLOCKER** means: proceeding as if the project is product-approved / identity-settled, or proceeding in a way that would invalidate project identity or core justification, is unsafe. Stylistic issues are not blockers.

---

### OE-GROK-001 — Product architecture still forbids a from-scratch engine

- **Severity:** BLOCKER
- **Confidence:** high
- **Classification:** verified fact + recommendation
- **Location:**
  - Product: `docs/ARCHITECTURE.md` L432–435
  - Product: `.grok/PROJECT_TRUTH.md` § “TheOrc Native Runtime” (~L313)
  - OrcEngine: `PROJECT_VISION.md` L21–31; `SCOPE_AND_NON_GOALS.md` § Ownership boundary; `DECISION_LOG.md` OE-ADR-003
- **Finding:** TheOrc’s authoritative product architecture states Native Runtime’s point is **not** to become an inference engine from scratch, but to own scheduling/session/adapter/feedback on top of LLamaSharp. OrcEngine proposes a genuine computation-plane engine. The foundation does not record a product-level decision that supersedes the existing Native Runtime identity statement.
- **Evidence:**
  - code-verified control plane on LLamaSharp exists (`LLamaSharpRuntime`, packages 0.27.0).
  - doc-verified product text: “The point is not to become an inference engine from scratch…” (`docs/ARCHITECTURE.md` L432–435).
  - doc-verified product text: “on top of LLamaSharp … NOT a from-scratch inference engine” (`.grok/PROJECT_TRUTH.md` ~L313).
  - doc-verified OrcEngine DECIDED ownership of parsing, graph, kernels, cache, tokenizer, decoding (OE-ADR-003).
- **Why it matters:** Without a superseding product decision, OrcEngine is an unauthorized strategic fork in documentation form. Agents following product truth will correctly reject engine work; agents following OrcEngine docs will start it. That is identity failure, not a wording nit.
- **Smallest correction or experiment:** Record an explicit product decision (in `.grok/PROJECT_TRUTH.md` and/or a product ADR) that OrcEngine is either (a) a separately funded experimental research track that does not alter Native Runtime goals, or (b) rejected as out of product scope.
- **Falsification condition:** Maintainer-accepted product decision text that deliberately authorizes OrcEngine research beside Native Runtime, with budget/stop criteria.
- **Related documents:** `PROJECT_VISION.md`, `DECISION_LOG.md`, `RISK_REGISTER.md` R-001/R-020, `docs/ARCHITECTURE.md`, `.grok/PROJECT_TRUTH.md`

---

### OE-GROK-002 — Unique product value is assessed after most of the engineering cost

- **Severity:** BLOCKER
- **Confidence:** high
- **Classification:** recommendation
- **Location:**
  - `ENGINEERING_ROADMAP.md` Phase 3 L78–94 (stop gate L94)
  - `PROJECT_VISION.md` L21–31, L80–101
  - `RISK_REGISTER.md` R-001, R-020
  - `OPEN_QUESTIONS.md` OQ-008
  - `RESEARCH_QUESTIONS.md` RQ-024–027
- **Finding:** The suite correctly names “duplicate llama.cpp without unique value” as a critical risk, yet the formal strategic continue/stop after correctness sits at Phase 3—after oracle, synthetic transformer, GGUF reader, tokenizer, and real-model float32 work. That is most of a small engine. Success criterion “at least one TheOrc-native capability unavailable cleanly through LLamaSharp” is not required before Phase 1 code.
- **Evidence:**
  - doc-verified Phase 3 stop gate: “assess strategic value… before optimization” only after real-model float32 DoD (`ENGINEERING_ROADMAP.md` L94).
  - doc-verified agent-native advantages listed as hypotheses (`PROJECT_VISION.md` L23–29).
  - code-verified / doc-verified existing Prefix KV research already closed: cross-role sharing blocked by adapter/context scoping (`.grok/PROJECT_TRUTH.md` Phase 5; OrcEngine `KV_CACHE_AND_CONTEXT_DESIGN.md` § Role-aware future).
- **Why it matters:** Correctness pride will keep funding past the strategic failure point. Education is fine only if budgeted as education. R-001 is not mitigated by process theater after sunk cost.
- **Smallest correction or experiment:** Insert a **pre-Phase-1 strategic spike**: pick ≤3 candidate unique capabilities; for each, document the LLamaSharp/llama.cpp API gap, a reproduction, and a value statement. No Phase 1 engine code until one gap survives.
- **Falsification condition:** Written spike artifacts showing a concrete, product-valued capability that cannot be achieved cleanly with current runtimes.
- **Related documents:** `ENGINEERING_ROADMAP.md`, `PROJECT_VISION.md`, `RISK_REGISTER.md`, `OPEN_QUESTIONS.md`, `RESEARCH_QUESTIONS.md`, `.grok/PREFIX_KV_CACHE_RESEARCH.md` (product)

---

## 6. FIX BEFORE PHASE 0 findings

These must be resolved before Phase 0 is declared complete or before engine implementation starts. They are not mere style nits.

---

### OE-GROK-003 — Phase 0 methodology exists; Phase 0 artifacts do not

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** verified fact
- **Location:** `PHASE_0_REFERENCE_ORACLE.md` (full); `CURRENT_STATE.yaml` `phase_0_blockers` L118–125; `OPEN_QUESTIONS.md` OQ-002–004; `RESEARCH_QUESTIONS.md` RQ-001–005
- **Finding:** Phase 0 correctly specifies manifests, capture points, fault injection, and regeneration rules, but no model, tokenizer, oracle version, tolerance profile, or artifact store is selected. Foundation volume can be misread as readiness.
- **Evidence:** doc-verified open blockers in `CURRENT_STATE.yaml` L118–125; no fixture/manifest files under `docs/OrcEngine` other than prose.
- **Why it matters:** “Docs exist” ≠ “correctness is measurable.” Engine code before instantiated oracle recreates the failure mode the suite itself warns about.
- **Smallest correction or experiment:** Freeze a Phase 0 charter: candidate model shortlist (≤3), license matrix, oracle pins, artifact path, and “no Phase 1 until injected faults fail at expected taps.”
- **Falsification condition:** Regenerable oracle bundle with hashes; independent reviewer reproduces synthetic bundle; deliberate faults fail at expected checkpoints.
- **Related documents:** `PHASE_0_REFERENCE_ORACLE.md`, `ENGINEERING_ROADMAP.md` Phase 0, `CURRENT_STATE.yaml`, `OPEN_QUESTIONS.md`

---

### OE-GROK-004 — Dual-oracle topology can share the same wrong assumption

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** inference + recommendation
- **Location:** `PHASE_0_REFERENCE_ORACLE.md` § Oracle topology L11–18; `RESEARCH_QUESTIONS.md` RQ-002; `RISK_REGISTER.md` R-005
- **Finding:** Primary (PyTorch/Transformers) + secondary (llama.cpp GGUF) is better than one oracle, but both often inherit HuggingFace conversion conventions and Llama folklore. Shared wrong RoPE pairing, GQA mapping, or RMSNorm ε can make OrcEngine match “both oracles” while diverging from intended architecture math. Hand-computed synthetic micro-models are mentioned in the fixture ladder but not required as a third independence class for Phase 0 exit.
- **Evidence:** doc-verified dual-oracle design (`PHASE_0_REFERENCE_ORACLE.md` L15–16); suite self-warns R-005; fixture ladder includes synthetic (L55–65) without making hand-math independence mandatory for exit.
- **Why it matters:** False confidence is worse than known error. Final-token agreement amplifies the hazard when margins are large.
- **Smallest correction or experiment:** Phase 0 exit requires three-way independence: (1) hand math / fixed synthetic weights, (2) semantic framework oracle, (3) deployment GGUF oracle when real GGUF is in scope. Synthetic-only path may omit (3) until Phase 2–3.
- **Falsification condition:** Documented HF vs llama.cpp disagreement localized by harness; synthetic hand cases pass independently of both.
- **Related documents:** `PHASE_0_REFERENCE_ORACLE.md`, `TEST_STRATEGY.md`, `THEORY_AND_ASSUMPTIONS.md` A-003

---

### OE-GROK-005 — “Classic Llama-style” is not a bindable architecture contract

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `SCOPE_AND_NON_GOALS.md` L11–12; `THEORY_AND_ASSUMPTIONS.md` L11–26; `OPEN_QUESTIONS.md` OQ-016–019; `MODEL_FORMAT_AND_GGUF.md` § First supported tuple
- **Finding:** First engine scope says one “classic Llama-style” architecture, but Llama / Llama2 / Llama3 / GGUF `llama` variants differ in GQA, RoPE base/scaling, tied embeddings, tokenizer, and required metadata. Open questions correctly list exact semantics as unknown; roadmap still treats the architecture family as decided enough to build toward.
- **Evidence:** doc-verified open OQ-016–019; externally verified GGUF LLaMA required keys and optional `head_count_kv` ([GGUF spec](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md), 2026-07-18).
- **Why it matters:** Underspecified architecture is how silent “almost Llama” bugs survive token-level tests.
- **Smallest correction or experiment:** Replace the phrase with a **pinned architecture profile**: exact `general.architecture`, required KV keys, GQA rule, RoPE profile, tied-output rule—filled from one real header + one HF config or pure synthetic definition.
- **Falsification condition:** Profile validates against a chosen artifact; two implementers produce matching intermediates from the profile alone on Fixture B.
- **Related documents:** `SCOPE_AND_NON_GOALS.md`, `MODEL_FORMAT_AND_GGUF.md`, `OPEN_QUESTIONS.md`, `PHASE_0_REFERENCE_ORACLE.md`

---

### OE-GROK-006 — Theory sketch omits load-bearing execution details

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `THEORY_AND_ASSUMPTIONS.md` L11–26; `TENSOR_ENGINE_DESIGN.md` § Required operators / Numerical contracts; `PHASE_0_REFERENCE_ORACLE.md` § Required capture points; `MODEL_FORMAT_AND_GGUF.md` § Tensor layout rule
- **Finding:** The layer equations are pedagogically useful but not an implementation contract. Missing or only partially specified: tensor orientation/strides (GGML reverse dims noted elsewhere, not in equations); Q/K/V shapes under GQA; whether `head_dim` equals key/value length; RoPE interleaving vs half-split, rotary dim, position origin, scaling; causal mask for prompt vs decode; stable softmax; tied vs untied output; residual vs parallel residual. Capture points are stronger than the equations, but artifact schema must carry layout/strides or transposed dumps can false-pass shape checks.
- **Evidence:** doc-verified simplified equations L14–24; externally verified llama.cpp HOWTO: “dimensions in ggml are typically in the reverse order of the pytorch dimensions” ([HOWTO-add-model](https://github.com/ggml-org/llama.cpp/blob/master/docs/development/HOWTO-add-model.md), 2026-07-18); doc-verified layout warning in `MODEL_FORMAT_AND_GGUF.md` § Tensor layout rule.
- **Why it matters:** Implementers invent conventions; oracles encode theirs; differentials become noise or false green.
- **Smallest correction or experiment:** Add an Architecture Semantics sheet (per-op ranks, dim names, reduction axes, ε placement) and require stride+layout in every tensor artifact.
- **Falsification condition:** Two independent implementers match intermediates from the sheet on Fixture B without reading each other’s code.
- **Related documents:** `THEORY_AND_ASSUMPTIONS.md`, `TENSOR_ENGINE_DESIGN.md`, `PHASE_0_REFERENCE_ORACLE.md`, `MODEL_FORMAT_AND_GGUF.md`

---

### OE-GROK-007 — Token agreement can still dominate acceptance in practice

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** medium-high
- **Classification:** recommendation
- **Location:** `PHASE_0_REFERENCE_ORACLE.md` L105–108, L130–133, L160–169; `SAMPLING_AND_DECODING.md` § Greedy acceptance; `THEORY_AND_ASSUMPTIONS.md` A-008/A-009; `ENGINEERING_ROADMAP.md` Phase 3 DoD L89
- **Finding:** Docs correctly reject “it chats” as proof and require top-token margins in greedy records. Phase 3 DoD still elevates matching generated token IDs. With large top-1 margins, many wrong graphs still match tokens. Near-tie fixtures are not required in the Phase 0 exit gate.
- **Evidence:** doc-verified A-008/A-009; doc-verified greedy acceptance wants logits metrics (`SAMPLING_AND_DECODING.md`); Phase 3 DoD lists token ID match (`ENGINEERING_ROADMAP.md` L89).
- **Why it matters:** Token match is a weak necessary condition, not a sufficient correctness gate.
- **Smallest correction or experiment:** Phase 0 exit requires intermediate taps + logits bounds + ≥N fixtures with small recorded top-1 margin + a synthetic near-tie weight set that fails under wrong ops but can pass tokens if only tokens are checked.
- **Falsification condition:** Harness fails when intermediates/logits are wrong even if tokens match.
- **Related documents:** `PHASE_0_REFERENCE_ORACLE.md`, `TEST_STRATEGY.md`, `SAMPLING_AND_DECODING.md`

---

### OE-GROK-008 — Tokenizer oracle independence is under-specified relative to blast radius

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `TOKENIZER_AND_PROMPT_PIPELINE.md` full; `PHASE_0_REFERENCE_ORACLE.md` fixture/token fields; `RESEARCH_QUESTIONS.md` RQ-008; GGUF tokenizer section (external)
- **Finding:** Tokenization is correctly treated as first-class, but Phase 0 can still collapse to “match oracle.encode().” If the only authority is the same stack used as secondary oracle, OrcEngine clones tokenizer bugs (byte fallback, special tokens, add-BOS, prefix space). Chat-template separation from math is well stated.
- **Evidence:** doc-verified golden fixture intent (`TOKENIZER_AND_PROMPT_PIPELINE.md` § Golden fixtures); externally verified multiple GGUF tokenizer models (`llama`, `gpt2`, `replit`, `rwkv`) with different merge/score semantics ([GGUF spec](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md), 2026-07-18).
- **Why it matters:** Perfect transformer + wrong IDs = wrong system; shared tokenizer code path yields false green logits.
- **Smallest correction or experiment:** Golden byte fixtures from source tokenizer definition (SentencePiece / HF `tokenizer.json`) **and** GGUF-embedded vocab, with explicit policy when they disagree.
- **Falsification condition:** Harness reports GGUF-vocab vs HF disagreement without hiding it.
- **Related documents:** `TOKENIZER_AND_PROMPT_PIPELINE.md`, `PHASE_0_REFERENCE_ORACLE.md`, `THEORC_INTEGRATION.md`

---

### OE-GROK-009 — GGUF resource limits and relative offsets need a hard contract before parser work

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `MODEL_FORMAT_AND_GGUF.md` § Reader stages; `SECURITY_AND_SAFETY.md` § Parser requirements; GGUF specification (external)
- **Finding:** Compatibility-tuple policy is excellent. Remaining gaps before any parser code: structural version pin (spec v3 current structural description; v2 exists); little-endian first is correct but big-endian detection is historically weak in the spec; nested arrays need depth/element budgets; max rank/dims; tensor data offset is **relative to `tensor_data`**, not file start (easy off-by-header bug); default alignment 32 if `general.alignment` missing.
- **Evidence:** externally verified GGUF file structure, endianness note, alignment default, relative offset ([GGUF spec](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md), 2026-07-18); doc-verified generic caps without numeric table (`SECURITY_AND_SAFETY.md`, `MODEL_FORMAT_AND_GGUF.md`).
- **Why it matters:** Parser bugs are simultaneous security and correctness failures.
- **Smallest correction or experiment:** One-page GGUF limits table (max kv_count, string len, array nesting, rank, dim, file size for Phase 2) + explicit relative-offset rule + fail-closed endian policy.
- **Falsification condition:** Fuzz/malformed suite cannot OOM/crash under caps; relative-offset fixtures pass/fail correctly.
- **Related documents:** `MODEL_FORMAT_AND_GGUF.md`, `SECURITY_AND_SAFETY.md`, `TEST_STRATEGY.md` parser section

---

### OE-GROK-010 — Decision authority drift: proposed ADRs look almost decided

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** verified fact
- **Location:** `DECISION_LOG.md` OE-ADR-006 L67–73, OE-ADR-008 L82–87, OE-ADR-009 L89–94; `CURRENT_STATE.yaml` `decisions` L61–79; `ARCHITECTURE.md` § Proposed layers L27–44
- **Finding:** DECISION_LOG correctly marks ADR-006 (eager loop), ADR-008 (C ABI), ADR-009 (cuBLAS-first) as **proposed**. `CURRENT_STATE.yaml` lists only accepted ADRs 001–005 and 007, omitting proposed ones. Architecture still leads with “Public C API” as layer 1, which reads more decided than ADR-008 allows.
- **Evidence:** doc-verified status fields above.
- **Why it matters:** Agents implement proposed packaging seams before a forward pass exists.
- **Smallest correction or experiment:** `CURRENT_STATE.yaml` should list proposed ADRs with `state: proposed`; Architecture should label C API “future integration seam (post correctness).”
- **Falsification condition:** Machine check: every OE-ADR-ID appears in CURRENT_STATE with matching status.
- **Related documents:** `DECISION_LOG.md`, `CURRENT_STATE.yaml`, `ARCHITECTURE.md`, `RISK_REGISTER.md` R-018

---

### OE-GROK-011 — Tests can still pass while the engine is wrong unless gates are machine-checkable

- **Severity:** FIX BEFORE PHASE 0
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `TEST_STRATEGY.md`; `PHASE_0_REFERENCE_ORACLE.md` § Fault-injection proof L145–154; `BENCHMARK_STRATEGY.md` § Anti-patterns
- **Finding:** Prose already lists many anti-false-green rules. Residual false-green paths if gates stay human-only: shared oracle assumptions; last-logits-only with loose ε; skipped large artifacts reported green; text compare instead of token IDs; warm cache sold as load; CUDA path not actually used; ASCII-only tokenizer tests; short-only cache equivalence; shared layout helpers in “independent” differentials.
- **Evidence:** doc-verified fault-injection list is excellent but not encoded as a checklist artifact; test categories allow skip semantics that must fail closed (`TEST_STRATEGY.md` differential artifact policy).
- **Why it matters:** Process docs without executable gates do not prevent self-deception.
- **Smallest correction or experiment:** Phase 0 acceptance checklist as YAML: required fixtures, required failing fault IDs, required margin cases, skip=fail policy.
- **Falsification condition:** CI template fails if any required checklist item is skipped.
- **Related documents:** `TEST_STRATEGY.md`, `PHASE_0_REFERENCE_ORACLE.md`, `BENCHMARK_STRATEGY.md`

---

## 7. IMPORTANT RESEARCH findings

Material unknowns or design risks that need evidence; not all must be prose-fixed before Phase 0 opens, but they gate later phases or strategic honesty.

---

### OE-GROK-012 — Agent-native differentiators remain unfalsified slogans

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** hypothesis challenge
- **Location:** `PROJECT_VISION.md` L23–29; `RESEARCH_QUESTIONS.md` RQ-024–027; `KV_CACHE_AND_CONTEXT_DESIGN.md` § Role-aware future
- **Finding:** Role-owned caches, exact residency telemetry, Context Fabric prefixes, adapter-aware planning, and HIVE partitioning are labeled hypotheses but still used as the strategic “why.” Existing product research already constrains cross-role KV sharing. Some telemetry goals may be approachable via llama.cpp/LLamaSharp instrumentation without a new engine.
- **Evidence:** doc-verified hypothesis labels; product Prefix KV research closed (see OE-GROK-002 evidence).
- **Why it matters:** Without a surviving unique gap, OrcEngine is education or branding (R-001).
- **Smallest correction or experiment:** Same strategic spike as OE-GROK-002; kill claims that LLamaSharp already covers.
- **Falsification condition:** One capability with written “cannot do in LLamaSharp because …” plus maintainer value statement.
- **Related documents:** `PROJECT_VISION.md`, `RESEARCH_QUESTIONS.md`, `RISK_REGISTER.md` R-001

---

### OE-GROK-013 — Real-model float32 Phase 3 may be operationally hard

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** inference
- **Location:** `ENGINEERING_ROADMAP.md` Phase 3; `QUANTIZATION_PLAN.md` F16 conversion; `OPEN_QUESTIONS.md` OQ-024; `SCOPE_AND_NON_GOALS.md` float32 first
- **Finding:** Scope wants float32 for the first engine. Practical GGUF artifacts are often quantized. Phase 3 may require a rare F16/F32 GGUF or a conversion pipeline that becomes its own correctness surface—or collapse to synthetic-only.
- **Evidence:** doc-verified float32-first scope; quant plan allows F16 storage conversion; OQ-024 open. Ecosystem quantization prevalence is **inferred** planning risk, not measured here.
- **Why it matters:** Sunk cost if Phase 3 cannot exit legally/technically.
- **Smallest correction or experiment:** Phase 0 selects redistributable tiny F16/F32 artifact or explicit convert-to-F32 pipeline with oracle comparison; else formally defer Phase 3.
- **Falsification condition:** Pinned legal non-quant (or audited conversion) artifact with matching dumps.
- **Related documents:** `QUANTIZATION_PLAN.md`, `LICENSING_AND_ATTRIBUTION.md`, `OPEN_QUESTIONS.md`

---

### OE-GROK-014 — TheOrc `GgufMetadataReader` soft-fail must not become the engine loader

- **Severity:** IMPORTANT RESEARCH (boundary) — hard gate before Phase 2 code
- **Confidence:** high
- **Classification:** verified fact + recommendation
- **Location:** `OrchestratorIDE/Core/Runtime/GgufMetadataReader.cs` L38–40, L65–68; `MODEL_FORMAT_AND_GGUF.md` § Existing TheOrc reader; `SECURITY_AND_SAFETY.md`
- **Finding:** Existing reader returns `null` on failure so admission never breaks—correct for estimation. OrcEngine loader must be fail-closed with structured errors. Docs warn against silent promotion; convenience reuse remains a risk.
- **Evidence:** code-verified soft-fail behavior; doc-verified warning in `MODEL_FORMAT_AND_GGUF.md`.
- **Why it matters:** Soft-fail estimation semantics are the opposite of untrusted model execution.
- **Smallest correction or experiment:** Explicit decision: new parser codebase (or dual-mode library with distinct public contracts).
- **Falsification condition:** Boundary review shows no soft-fail path into inference execution.
- **Related documents:** `MODEL_FORMAT_AND_GGUF.md`, `SECURITY_AND_SAFETY.md`, `THEORC_INTEGRATION.md`

---

### OE-GROK-015 — Prompt-template path can invalidate cross-runtime comparisons

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** inference
- **Location:** `TOKENIZER_AND_PROMPT_PIPELINE.md` § Chat templates; `THEORC_INTEGRATION.md` § Contract fit; product `NativePromptBuilder` / LLamaSharp template probing
- **Finding:** Engine boundary should accept raw tokens; product adapters have template fallbacks and prompt-path telemetry. Comparative evidence dies if template identity is invisible.
- **Evidence:** doc-verified integration questions; code-verified product runtime surfaces message history not raw tokens (`IModelRuntime.cs`).
- **Why it matters:** “Matches oracle in product” becomes meaningless under template drift.
- **Smallest correction or experiment:** All comparative fixtures record template ID, raw prompt bytes hash, token IDs hash, special-token policy.
- **Falsification condition:** Schema makes post-token-ID template application impossible to hide.
- **Related documents:** `THEORC_INTEGRATION.md`, `TOKENIZER_AND_PROMPT_PIPELINE.md`, `BENCHMARK_STRATEGY.md`

---

### OE-GROK-016 — BLAS/cuBLAS are compatible with ownership definition; determinism is not free

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** verified fact + recommendation
- **Location:** `SCOPE_AND_NON_GOALS.md` § Ownership boundary; `CPU_BACKEND_DESIGN.md`; `CUDA_BACKEND_DESIGN.md` § Dense operations; NVIDIA cuBLAS docs
- **Finding:** OE-ADR-003’s definition (own semantics; call general GEMM libraries) is coherent and does not make OrcEngine a llama.cpp wrapper. Caveats: cuBLAS is column-major; bitwise reproducibility is conditional (architecture, toolkit, streams, workspace, math mode); reduced-precision reductions and emulation modes can change numerics; async errors surface late.
- **Evidence:** doc-verified ownership decision; externally verified cuBLAS layout and reproducibility notes ([cuBLAS docs](https://docs.nvidia.com/cuda/cublas/), 2026-07-18); externally verified FP non-associativity ([PyTorch numerical accuracy](https://docs.pytorch.org/docs/stable/notes/numerical_accuracy.html), 2026-07-18).
- **Why it matters:** GPU bring-up will be dominated by layout and tolerance mistakes if unplanned.
- **Smallest correction or experiment:** Before Phase 6, write layout + determinism contract (who converts row/column, math mode, workspace, stream policy, non-goal of bitwise CPU==GPU).
- **Falsification condition:** CPU scalar vs cuBLAS matmul matches profiled tolerance on fixed fixtures with recorded modes.
- **Related documents:** `CUDA_BACKEND_DESIGN.md`, `CPU_BACKEND_DESIGN.md`, `DECISION_LOG.md` OE-ADR-003/009

---

### OE-GROK-017 — CUDA packaging and “actual backend” honesty are under-stressed given live scars

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** verified fact + recommendation
- **Location:** `CUDA_BACKEND_DESIGN.md`; `OrchestratorIDE.NativeRuntime.csproj` L62–83; `docs/RUNTIME_SUPPORT_MATRIX.md` fallback identity gap; `NativeBackendBootstrap.cs`
- **Finding:** Deferring CUDA until CPU correctness is right. TheOrc already hit silent CUDA load failure → CPU path. OrcEngine CUDA design covers sync/errors but must treat dependency discovery, redistributables, and fail-closed “requested CUDA unavailable” as exit criteria, not polish.
- **Evidence:** code-verified CUDA redist bundling comments and warning target in `OrchestratorIDE.NativeRuntime.csproj`; doc-verified runtime matrix fallback identity gap.
- **Why it matters:** Reporting CUDA success while running CPU is a product lie and invalidates benchmarks.
- **Smallest correction or experiment:** Phase 6 DoD: forced-CPU vs forced-CUDA paths; fail-closed if CUDA requested but unavailable; artifact field `actual_device` always set.
- **Falsification condition:** Misconfigured machine cannot claim CUDA success.
- **Related documents:** `CUDA_BACKEND_DESIGN.md`, `BENCHMARK_STRATEGY.md`, `THEORC_INTEGRATION.md`, `docs/RUNTIME_SUPPORT_MATRIX.md`

---

### OE-GROK-018 — KV multi-layer commit and mid-prompt failure need tighter rules

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** medium-high
- **Classification:** recommendation
- **Location:** `KV_CACHE_AND_CONTEXT_DESIGN.md` § Invariants, § Transactional decode; `OPEN_QUESTIONS.md` OQ-012, OQ-014
- **Finding:** Transactional decode, poison-on-partial-write, fail-closed overflow, and prompt↔incremental equivalence are among the strongest designs in the suite. Still underspecified: all-or-nothing multi-layer prompt commit if layer L succeeds and L+1 fails; default physical zeroing on reset; cancellation cooperativeness inside long GEMM; explicit coupling of position origin to RoPE research.
- **Evidence:** doc-verified invariants and open questions.
- **Why it matters:** Cache bugs cause delayed divergence after plausible early tokens.
- **Smallest correction or experiment:** Specify all-or-nothing prompt evaluation; mid-prompt failure poisons context unless full candidate-region rollback is proven; add fault-injection tests.
- **Falsification condition:** Injected mid-layer failure leaves no readable mixed prefix.
- **Related documents:** `KV_CACHE_AND_CONTEXT_DESIGN.md`, `TEST_STRATEGY.md`, `PHASE_0_REFERENCE_ORACLE.md` cache equivalence

---

### OE-GROK-019 — Quant plan sequence is sound; K-quants/mixed types are the real world

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `QUANTIZATION_PLAN.md`; GGUF type enum (external)
- **Finding:** Q8_0→Q4_0 reference-dequant-first is correct. Community GGUFs often use K-quants and mixed tensor dtypes. A narrow quant surface may require re-quantizing pinned models rather than loading arbitrary files—should be explicit policy.
- **Evidence:** doc-verified quant order and mixed-type section; externally verified extensive ggml type list including K/IQ types ([GGUF spec](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md), 2026-07-18).
- **Why it matters:** Scope explosion via “one more dtype,” or false claim of practical model support.
- **Smallest correction or experiment:** Compatibility tuple lists exact ggml type IDs; unsupported types fail with names at validation.
- **Falsification condition:** Inspector rejects Q4_K_M with explicit reason; accepts pinned Q8_0.
- **Related documents:** `QUANTIZATION_PLAN.md`, `MODEL_FORMAT_AND_GGUF.md`, `SCOPE_AND_NON_GOALS.md`

---

### OE-GROK-020 — Licensing is careful; monorepo AGPL and model rights still gate code

- **Severity:** IMPORTANT RESEARCH
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `LICENSING_AND_ATTRIBUTION.md`; `OPEN_QUESTIONS.md` OQ-001, OQ-004, OQ-040; repository `LICENSE` / `LICENSING.md` (product)
- **Finding:** Suite correctly avoids formal clean-room claims and requires model/converter/CUDA provenance. Residual: subconscious structure copy from llama.cpp; oracle dumps as weight derivatives; AI-generated kernel provenance; monorepo AGPL inheritance vs separate repo; dual commercial posture.
- **Evidence:** doc-verified licensing guidance; LLamaSharp MIT (externally verified README, 2026-07-18)—relevant only as oracle/runtime comparison, not OrcEngine dependency under current boundary.
- **Why it matters:** Legal overclaim or uncleared model redistribution can end the project.
- **Smallest correction or experiment:** Before Phase 1 code: entity decision (mono vs separate); start attribution ledger; clear first model rights.
- **Falsification condition:** Maintainer/legal sign-off on entity + first artifact redistribution.
- **Related documents:** `LICENSING_AND_ATTRIBUTION.md`, `OPEN_QUESTIONS.md`, `PHASE_0_REFERENCE_ORACLE.md` provenance

---

### OE-GROK-021 — Documentation volume exceeds Phase 0 executable specification

- **Severity:** IMPORTANT RESEARCH (process)
- **Confidence:** medium-high
- **Classification:** recommendation
- **Location:** suite-wide (~30 foundation files); compare `PHASE_0_REFERENCE_ORACLE.md` vs CUDA/quant/Phase 9 material
- **Finding:** Quality is high, but CUDA, quant, integration, and agent-native futures are large relative to unpinned Phase 0. Risk: maintaining speculative docs exceeds research output (R-020).
- **Evidence:** inventory count; Phase 0 still blocked on model selection (`CURRENT_STATE.yaml`).
- **Why it matters:** Opportunity cost against TheOrc main roadmap.
- **Smallest correction or experiment:** Freeze further design expansion; update docs only when Phase 0 experiments answer RQs; mark CUDA/quant/integration provisional.
- **Falsification condition:** Phase 0 completes without needing those later docs expanded.
- **Related documents:** `ENGINEERING_ROADMAP.md`, `RISK_REGISTER.md` R-020, `README.md` maintenance rules

---

## 8. OPTIONAL findings

Improvements with no current gate force. Not blockers.

---

### OE-GROK-022 — C ABI is correctly deferred in ADR status but over-centered in architecture

- **Severity:** OPTIONAL
- **Confidence:** medium-high
- **Classification:** recommendation
- **Location:** `ARCHITECTURE.md` § 1 Public C API L27–44; `DECISION_LOG.md` OE-ADR-008; `RISK_REGISTER.md` R-018
- **Finding:** Text says design ABI after core works; architecture still leads with Public C API as layer 1, biasing early scaffolding.
- **Evidence:** ADR-008 proposed; roadmap Phase 7.
- **Why it matters:** Premature ABI freezes handle/cancellation mistakes.
- **Smallest correction or experiment:** Relabel layer 1 as future integration seam; lead with model loop + tensors.
- **Falsification condition:** Phases 1–3 use only a test harness with no frozen C header.
- **Related documents:** `ARCHITECTURE.md`, `ENGINEERING_ROADMAP.md` Phase 7, `THEORC_INTEGRATION.md`

---

### OE-GROK-023 — Eager execution is right; graph IR and agent-native engine layers should stay cut

- **Severity:** OPTIONAL
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `TENSOR_ENGINE_DESIGN.md` § Eager first; `ENGINEERING_ROADMAP.md` Phase 9; `RESEARCH_QUESTIONS.md` RQ-011, RQ-024–027
- **Finding:** Eager fixed loop is the correct default. Continuous batching, graph compilers, HIVE partitions, and multi-stream CUDA should remain non-goals until measured need.
- **Evidence:** ADR-006 proposed in the right direction; Phase 9 correctly late but still detailed.
- **Why it matters:** Cognitive load and scope creep.
- **Smallest correction or experiment:** Collapse Phase 9/P3 into a short future-research stub; keep active design on Phases 0–3.
- **Falsification condition:** Maintainers report no distraction; Phase 0 finishes without those docs.
- **Related documents:** `SCOPE_AND_NON_GOALS.md`, `ENGINEERING_ROADMAP.md`

---

### OE-GROK-024 — Constrained decoding is deferred yet may be the real differentiator

- **Severity:** OPTIONAL (strategic note)
- **Confidence:** medium
- **Classification:** recommendation
- **Location:** `SAMPLING_AND_DECODING.md` § Grammar-constrained decoding; TheOrc toolcalling product needs
- **Finding:** Greedy baseline is correct. Grammar/tool-constrained decoding is out of scope but may be the only product differentiator worth an engine—currently buried.
- **Evidence:** doc-verified deferral; product toolcaller work exists (product docs/code outside OrcEngine).
- **Why it matters:** Strategic narrative should match the real pain (structured outputs) or drop agent-native rhetoric.
- **Smallest correction or experiment:** Promote constrained decoding as a *candidate* post-Phase-3 unique-value experiment, or stop using it in “why OrcEngine” rhetoric.
- **Falsification condition:** Measured tool-call validity gain vs llama.cpp grammars / current parsers.
- **Related documents:** `SAMPLING_AND_DECODING.md`, `PROJECT_VISION.md`, `THEORC_INTEGRATION.md`

---

### OE-GROK-025 — OrcEngine PROJECT_TRUTH vs `.grok/PROJECT_TRUTH` name collision

- **Severity:** OPTIONAL
- **Confidence:** high
- **Classification:** recommendation
- **Location:** `docs/OrcEngine/PROJECT_TRUTH.md`; `.grok/PROJECT_TRUTH.md`
- **Finding:** Same filename pattern, different scopes. Agents will mix product truth with OrcEngine research truth.
- **Evidence:** both files exist with overlapping “runtime” vocabulary.
- **Why it matters:** Status drift and wrong authority.
- **Smallest correction or experiment:** In OrcEngine README, state authority order explicitly (already partly done); consider renaming mental model to “OrcEngine Truth.”
- **Falsification condition:** Reviewer confusion rate drops (process metric).
- **Related documents:** `README.md` maintenance rules, `GLOSSARY.md`

---

### OE-GROK-026 — Positive: coexistence and fail-closed research posture are well designed

- **Severity:** OPTIONAL (commendation)
- **Confidence:** high
- **Classification:** verified fact
- **Location:** `PROJECT_VISION.md` § Product principles; `SCOPE_AND_NON_GOALS.md` § Product boundary; `THEORC_INTEGRATION.md` § Fallback policy; `DECISION_LOG.md` OE-ADR-001–002
- **Finding:** Experimental beside—not instead of—existing runtimes; no default changes; fail-closed evidence workloads; intermediate taps; scalar reference retention; stop criteria. This is better process design than most rewrite proposals.
- **Evidence:** accepted ADRs 001–002, 004–005, 007; integration rollback section.
- **Why it matters:** If the project proceeds after strategic gates, this process is what keeps TheOrc usable.
- **Smallest correction or experiment:** None required; preserve these constraints under pressure.
- **Falsification condition:** N/A
- **Related documents:** `RISK_REGISTER.md` R-010/R-011

---

## 9. STALE/INVALID claims found during review

| ID | Claim | Why stale/invalid | Authority |
|---|---|---|---|
| S1 | `.agents.md` Native Runtime Phase 4: OrcScheduler not yet wired into AdapterManager / no live admission wiring | **Invalid vs code:** `OrcScheduler` is used from `RuntimeOrchestrator.EnsureAdmitted` with reservation accounting (code comments + `.grok/PROJECT_TRUTH.md` correction). Still no live GPU dispatch/pipeline queue—that part remains true. | Live code > `.agents.md` |
| S2 | Any reading that “Native Runtime” already *is* OrcEngine or owns tensor kernels | **Invalid.** Native Runtime = LLamaSharp orchestration. OrcEngine docs generally avoid this; product naming collision remains a risk. | code-verified |
| S3 | “OrcEngine foundation verified against commit X” implying docs are *in* commit X | **Invalid for docs inclusion.** Docs are untracked; commit verifies code anchors only. | git status |
| S4 | Older product claims that VRAM estimate is only file-size-based | **Stale at this commit.** Context-aware GGUF-header estimate landed in `0a31aa0` (`OrcScheduler` / `GgufMetadataReader`). OrcEngine `PROJECT_TRUTH.md` correctly notes this. | code-verified |
| S5 | Treating OE-ADR-006/008/009 as accepted project law | **Invalid.** Status is `proposed` in `DECISION_LOG.md`. | doc-verified |

No OrcEngine foundation file falsely claimed a runnable engine exists. That honesty is correct.

---

## 10. Cross-document contradiction matrix

| ID | Claim A | Claim B | Resolution |
|---|---|---|---|
| C1 | Product `docs/ARCHITECTURE.md` / `.grok/PROJECT_TRUTH.md`: not from-scratch engine | OrcEngine: build from-scratch computation plane | **Strategic conflict** — product decision required (OE-GROK-001) |
| C2 | `DECISION_LOG` ADR-008 proposed | `ARCHITECTURE.md` leads with Public C API | Mark future seam; do not implement early (OE-GROK-010/022) |
| C3 | `CURRENT_STATE.yaml` decisions omit 006/008/009 | `DECISION_LOG` contains them as proposed | Sync machine-readable proposed list |
| C4 | Unique value is success criterion | Unique-value stop after Phase 3 cost | Move spike before Phase 1 (OE-GROK-002) |
| C5 | Float32 real-model Phase 3 | Quant-first ecosystem + OQ-024 open | May need conversion or defer Phase 3 (OE-GROK-013) |
| C6 | Dual independent oracles | Both may share HF/GGUF conversion assumptions | Require synthetic hand-math class (OE-GROK-004) |
| C7 | OrcEngine “verified against” commit | Docs untracked | Code anchors only (S3) |
| C8 | Compatibility tuple “never universal GGUF” | Broad GGUF design coverage in same suite | Policy OK if claims stay tuple-scoped; enforce in prose/marketing |

Internal OrcEngine contradictions are mostly **status/authority** issues, not equation vs equation fights. The hard contradiction is **product identity vs OrcEngine identity**.

---

## 11. Repository-code truth corrections

Facts **code-verified** at `0a31aa0eca4464e94550617c97a2938453ad6874`:

| Topic | Truth |
|---|---|
| Default backend | `InferenceBackend.Ollama` |
| Experimental native toggles | Default false |
| Runtimes | `OllamaRuntime`, `LlamaCppServerRuntime`, `LLamaSharpRuntime` (`ILocalModelRuntime`) |
| LLamaSharp packages | 0.27.0 CPU + CUDA12 Windows/Linux conditionals |
| Fallback wrapper | `NativeWithFallbackRuntime`: pre-first-output only; admission denial not silently rerouted |
| Orchestration | `RuntimeOrchestrator` wires depot/session/adapters; admission via `OrcScheduler` |
| VRAM estimate | Context-aware from GGUF headers when options provided; legacy file-size path preserved without options |
| GGUF reader in product | Header-only, soft-fail `null`, v2/v3, for estimates—not execution |
| OrcEngine code | None |
| CUDA redist hazard | Documented and partially mitigated in NativeRuntime.csproj; silent CPU fallback was a real failure mode |

**Inferred (not re-executed):** full end-to-end generation paths, GPU timings, and fleet behavior were not re-run in this review.

**Product docs that should be updated (out of scope for this write):** `.agents.md` Phase 4 wiring text.

---

## 12. Numerical and oracle critique

### What is strong

- Intermediate taps preferred over final text (`THEORY_AND_ASSUMPTIONS.md` A-003; Phase 0 capture list).
- Fault-injection list targets real failure classes (transpose, RoPE, mask, K/V swap, ε, BOS).
- Prompt vs incremental cache equivalence is a permanent regression requirement.
- Greedy tie rule and non-finite logits as errors are specified.
- PyTorch non-associativity cited; bitwise FP equality not required for floats.
- Scalar reference path retained for optimized CPU/CUDA differentials.

### What can still produce false confidence

1. Dual oracles sharing conversion assumptions (OE-GROK-004).
2. Token match with large margins (OE-GROK-007).
3. Missing architecture profile (OE-GROK-005/006).
4. Tolerance profiles not yet derived—cannot be invented in prose.
5. Artifact schema without strides/layout.
6. Tokenizer not independent of secondary oracle (OE-GROK-008).
7. Skip of large dumps silently greening suites (must remain fail-closed).

### Smallest credible oracle (summary)

See §18. Minimum is synthetic hand-math + pinned env + fault injections that fail + stride-aware dumps. Real GGUF secondary oracle only after a legal artifact exists.

---

## 13. Security and licensing critique

### Security (strengths)

- Models treated as untrusted binary input.
- Checked arithmetic, caps, fuzz, sanitizers called out.
- No tool execution inside engine; tools stay in TheOrc.
- Fail-closed context overflow; poison contexts after partial backend writes.
- Log hygiene (no prompts/weights by default).

### Security (gaps before implementation)

| Gap | Risk | Gate |
|---|---|---|
| Numeric max tables for parser | DoS / overflow | Before Phase 2 |
| Nested array depth | Resource exhaustion | Before Phase 2 |
| Relative tensor offsets | OOB views | Before Phase 2 |
| Tokenizer pathological tables | CPU DoS | Before tokenizer Phase |
| Debug captures of weights | Data/license leakage | Policy before debug mode |
| ABI callback lifetime | UAF / exception across boundary | Phase 7 |
| Oracle Python env supply chain | Poisoned expected tensors | Phase 0 |
| Model identity by `general.name` | Spoofing | Use hash; name is display only |

### Licensing (strengths)

- No false clean-room claim.
- Model redistribution not assumed from hub availability.
- Oracle dumps treated as model-derived.
- AI output not accepted as provenance.

### Licensing (gaps)

- First model rights uncleared (OQ-004).
- Mono vs separate repo / AGPL inheritance (OQ-001).
- CUDA/BLAS redistribution policy for shipping binaries (OQ-040).
- Study-vs-copy discipline when reading llama.cpp.

---

## 14. Scope cuts: what should be removed or deferred

| Cut / defer | Why |
|---|---|
| Engine source scaffold | Phase 0 not instantiated; strategic gates open |
| Frozen C ABI / public headers | Premature (ADR-008 proposed only) |
| CUDA implementation | Correctly later; stop expanding design until Phase 3 exit |
| Quant implementation | After float correctness |
| Multi-architecture GGUF | Violates tuple discipline |
| Continuous batching / multi-sequence | Non-goal; delete from active design focus |
| Graph IR / fusion planner | No measured need |
| HIVE partitioning in engine | Long-horizon; no single-device profile yet |
| Agent-native cache product claims | Unproven; research only after unique-value spike |
| TheOrc default integration | Explicit non-goal; keep off |
| Broad “supports GGUF” language anywhere | Replace with exact tuples only |

**Keep active focus:** Phase 0 oracle, architecture profile, synthetic float32 loop, strict parser for one tuple, security limits.

---

## 15. Missing subjects: what should be added

| Missing subject | Why |
|---|---|
| Product decision linking OrcEngine to/from Native Runtime goals | OE-GROK-001 |
| Pre-Phase-1 unique-value spike template | OE-GROK-002/012 |
| Bindable architecture profile document | OE-GROK-005/006 |
| Phase 0 acceptance checklist YAML | OE-GROK-011 |
| GGUF hard limits + threat-model table | OE-GROK-009 |
| Tensor artifact schema including strides/layout | OE-GROK-006 |
| Near-tie / low-margin fixture policy | OE-GROK-007 |
| Tokenizer dual-source golden policy (HF vs GGUF vocab) | OE-GROK-008 |
| Layout/determinism contract for BLAS/cuBLAS | OE-GROK-016 |
| `actual_device` / no-silent-CPU policy for CUDA | OE-GROK-017 |
| Multi-layer prompt commit/poison rules | OE-GROK-018 |
| CURRENT_STATE proposed-ADR list | OE-GROK-010 |
| First-model license decision record | OE-GROK-013/020 |

---

## 16. Strongest case for building OrcEngine

If TheOrc is **permanently blocked**—by LLamaSharp/llama.cpp API boundaries—from capabilities that are product-critical (for example: honest per-layer/per-role residency telemetry that cannot be obtained without unacceptable hacks; deterministic evidence-prefix reuse with identity-rich invalidation; or fail-closed research decoding semantics for structured tools), then a **narrow**, oracle-tested, opt-in engine can be justified.

The foundation’s correctness-first discipline, coexistence rules, and security posture are the only responsible way to attempt that. BLAS/cuBLAS use does not invalidate the ownership definition if OrcEngine still owns model semantics and state.

---

## 17. Strongest case against building OrcEngine

TheOrc’s real bottlenecks are **product runtime maturity** on the existing stack: admission quality, fallback identity in evidence workloads, CUDA packaging honesty, role lifecycle, and operator UX—not lack of a custom RMSNorm.

Product architecture still says not to become a from-scratch engine. Unique agent-native value is unproven; some “unique” ideas are already constrained or partially approachable via LLamaSharp. Rebuilding a worse llama.cpp would burn the roadmap and produce an educational CPU toy while production stays on Ollama/LLamaSharp.

**Default prior:** do not build unless a measured gap survives a short spike.

---

## 18. Minimum credible Phase 0 plan

1. **Product decision** on OrcEngine as experimental research vs out-of-scope (OE-GROK-001).
2. **Unique-value spike** (≤3 claims) against LLamaSharp APIs; kill or confirm (OE-GROK-002).
3. **Architecture profile** for one synthetic Llama-like graph (dims, GQA, RoPE, ε, tied out).
4. **Synthetic fixtures A/B/C** with hand-checked ops; independent micro-oracle (numpy/torch).
5. **Artifact schema v1:** dtype, shape, strides, layout enum, layer, position, hash.
6. **Fault injections** that must fail at expected taps (Phase 0 list).
7. **Tolerance derivation** from repeated runs; no universal ε; log decision if widened.
8. **Tokenizer policy:** golden bytes; dual-source if real tokenizer involved.
9. **Provenance:** env lock, hashes; large dumps external; licenses recorded.
10. **Exit:** independent reviewer regenerates synthetic bundle; maintainer accepts; **only then** consider Phase 1 code.
11. **Real GGUF secondary oracle:** only after legal tiny artifact selected; else explicitly defer Phase 3.

---

## 19. Top ten experiments in priority order

| # | Experiment | Answers | Gate |
|---|---|---|---|
| 1 | Unique-value spike vs LLamaSharp (telemetry / prefix / structured decode) | Should project exist? | Pre-Phase 1 |
| 2 | Hand-math Fixture A operators (matmul, RMSNorm, softmax, RoPE, SiLU) | Operator semantics | Phase 0 |
| 3 | Synthetic one-layer forward with fixed weights vs micro-oracle | Graph wiring | Phase 0/1 |
| 4 | Prompt vs incremental cache equivalence on multi-token synthetic | Cache correctness | Phase 0/1 |
| 5 | Fault injection suite (transpose, RoPE, mask, K/V, ε, BOS) | Harness sensitivity | Phase 0 exit |
| 6 | Near-tie logits synthetic set | Token-only false green | Phase 0 |
| 7 | Tokenizer golden bytes (ASCII + multibyte + special tokens) | Text boundary | Phase 0 |
| 8 | GGUF malformed/overflow/nested-array limit prototypes (even in Python) | Parser threat model | Pre-Phase 2 |
| 9 | Tiny legal F16/F32 model provenance + load in HF and llama.cpp | Phase 3 feasibility | Phase 0 |
| 10 | BLAS float32 matmul layout trial (row vs column) on known matrices | CPU baseline contract | Pre-Phase 4 |

---

## 20. Continue, pause, or stop recommendation

| Option | Recommendation |
|---|---|
| **Continue implementation** | **No** |
| **Pause** | **Yes** — default recommendation |
| **Stop / archive** | If maintainer cannot authorize identity (OE-GROK-001) or no unique-value gap survives spike within a fixed budget |

**Pause means:**

- Keep the documentation suite.
- Run product decision + Phase 0 research only after identity is settled.
- Do not open engine source trees, CMake engine targets, or integration flags.

**Stop means:**

- Archive `docs/OrcEngine` as research notes.
- Continue investing in LLamaSharp Native Runtime product gaps instead.

---

## 21. Primary sources

Access date: **2026-07-18**.

| Source | URL | Used for | Class |
|---|---|---|---|
| GGUF specification | https://github.com/ggml-org/ggml/blob/master/docs/gguf.md | Structure, endianness, alignment default, types, offsets, tokenizer keys, LLaMA metadata | externally verified |
| llama.cpp HOWTO-add-model | https://github.com/ggml-org/llama.cpp/blob/master/docs/development/HOWTO-add-model.md | GGML dim order vs PyTorch; RoPE conversion notes | externally verified |
| LLamaSharp repository | https://github.com/SciSharp/LLamaSharp | Based on llama.cpp; MIT; v0.27.0 ↔ llama.cpp commit map | externally verified |
| cuBLAS documentation | https://docs.nvidia.com/cuda/cublas/ | Column-major; context; reproducibility caveats | externally verified |
| CUDA Programming Guide | https://docs.nvidia.com/cuda/cuda-programming-guide/ | Heterogeneous memory/sync model (suite-aligned; not full chapter audit) | externally verified (landing/reference) |
| PyTorch numerical accuracy | https://docs.pytorch.org/docs/stable/notes/numerical_accuracy.html | Non-associativity; cross-impl variance | externally verified |
| SentencePiece project | https://github.com/google/sentencepiece | Tokenizer family research reference | externally verified |

**Repository anchors:** listed in §11 and §3.4.

---

## 22. Files not reviewed and why

| Item | Why not reviewed |
|---|---|
| Full text of `docs/RUNTIME_PHASE0_SPEC.md` / `docs/NATIVE_RUNTIME_V2_SPEC.md` | Boundary verification used live code + targeted product docs; full historical specs not re-read end-to-end |
| Entire `.grok/PROJECT_TRUTH.md` beyond Native Runtime section | Only sections needed for identity/boundary |
| All of `docs/ROADMAP.md` | Sampled Native Runtime narrative only |
| LLamaSharpRuntime.cs full body | Class role, interfaces, and package versions verified; full sampling/template implementation not line-audited |
| AdapterManager/SessionManager/RuntimeOrchestrator full bodies | Existence, roles, and admission wiring confirmed via headers/comments/grep; not a full concurrency audit |
| `training_pit/datasets/toolcaller*` | Unrelated user-owned untracked data; out of scope |
| This review file as “foundation design” | Output artifact, not design input |
| Private maintainer plans outside repo | Unavailable |

**Every generated OrcEngine foundation file in the 30-file suite was reviewed.**

---

## 23. Final verification checklist

| Gate | Status |
|---|---|
| Every foundation file under `docs/OrcEngine` reviewed (30) | **Yes** — inventory §4 |
| `grok_OrcEngine_review.md` is the only file written by this review pass | **Yes** — rewrite of this review only; no other paths edited |
| Local file references point at real paths used in review | **Yes** — code under `OrchestratorIDE/Core/Runtime/`, `OrchestratorIDE.NativeRuntime/`, product `docs/`, `.grok/`, `.agents.md` |
| Findings usable without prior chat | **Yes** — full schema and evidence in-file |
| Facts labeled code-verified / externally verified / inferred / unknown | **Yes** — §1 classes; per-finding evidence |
| No false claim that tests/commands passed beyond what ran | **Yes** — git/list/read/fetch only; no `dotnet test` claimed |
| Severity not inflated for style | **Yes** — two blockers only (identity + strategic cost timing) |
| No implementation; no commit/push/PR/branch change | **Yes** |
| Unrelated working-tree files left alone | **Yes** |

---

### Finding index by severity

| Severity | IDs |
|---|---|
| BLOCKER | OE-GROK-001, OE-GROK-002 |
| FIX BEFORE PHASE 0 | OE-GROK-003 … OE-GROK-011 |
| IMPORTANT RESEARCH | OE-GROK-012 … OE-GROK-021 |
| OPTIONAL | OE-GROK-022 … OE-GROK-026 |
| STALE/INVALID | S1–S5 (§9) |

---

*End of review. Maintainer action required before any OrcEngine implementation.*
