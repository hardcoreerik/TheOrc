# Engineering Roadmap

## Roadmap contract

Phases are evidence gates, not calendar promises. A phase is complete only when its commands ran, artifacts are retained, failures are understood, and [Project Truth](PROJECT_TRUTH.md) is updated.

No phase changes TheOrc’s default runtime without a separate product decision.

## Phase D0 — Documentation and research foundation

**Goal:** establish shared vocabulary, boundaries, research questions, risk controls, and AI-review workflow.

**Deliverables:** this document suite, current-state snapshot, source ledger, initial review prompts, and open-question queue.

**Definition of done:**

- all required files exist and cross-links resolve;
- current runtime facts are checked against live code;
- hypotheses and unknowns are visibly labeled;
- external technical claims point to primary sources;
- no files outside `docs/OrcEngine` are modified;
- at least two independent AI reviews can use the prompts without hidden conversation context.

**Stop gate:** satisfied on 2026-07-18; maintainer approved Phase 0 research after adversarial review.

## Phase 0 — Deterministic reference oracle

**Goal:** make correctness measurable before implementing OrcEngine.

**Scope:** select model, tokenizer, oracle, capture points, tolerances, artifact format, and repeatable runner.

**Deliverables:**

- immutable manifest with model/tokenizer hashes and licenses;
- pinned environment lock;
- exact prompts and token IDs;
- reference intermediate tensors, logits, cache slices, and greedy tokens;
- comparison script that intentionally fails on perturbed data;
- artifact-size and retention policy.
- exact `OE-L0-SYNTH-1` semantics and a machine-readable acceptance result.

**Definition of done:** see [Phase 0 Reference Oracle](PHASE_0_REFERENCE_ORACLE.md).

**Stop gate:** no engine code until the oracle can localize every required deliberate error and all required fields in [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml) are `pass`. Missing and skipped checks fail. The maintainer must also approve a bounded product-value thesis: prevented capability or material measurable improvement.

## Phase 1 — Tiny synthetic float32 CPU transformer

**Goal:** independently execute a tiny deterministic Llama-style graph.

**Scope:** hard-coded or simple manifest weights, scalar CPU operators, one prompt, no GGUF dependency, one sequence, greedy token.

**Deliverables:** tensor view, matmul, RMSNorm, RoPE, softmax, attention, SwiGLU, residuals, cache, logits, and one runnable check.

**Definition of done:**

- every operator passes small hand-calculated and differential cases;
- every layer tap stays within approved tolerance;
- cache and non-cache forward paths agree for equivalent input;
- leak/error sanitizers are clean on supported development platform;
- one deliberate tensor transpose breaks the expected comparison.

**Stop gate:** decide whether the architecture remains understandable without a generic graph system.

## Phase 2 — Strict GGUF ingestion and real F32 reference — COMPLETE / FROZEN

**Original goal:** parse and validate the pinned artifact without executing it.

**Actual completed scope:** supported GGUF version, typed metadata, alignment,
tensor descriptors, bounds/overflow validation, file-backed extents, supported
tensor types, dense-Llama semantic mapping, F32/F16-to-F32 materialization, and
real explicit/tied F32 execution through frozen Phase-1 math.

**Deliverables:** `orc-gguf-inspect`, machine-readable manifest, malformed fixtures, tensor-name/dimension validator.

**Definition of done:**

- inspector output matches trusted tools for the pinned model;
- truncation, overflow, overlap, invalid alignment, invalid type, duplicate key, and missing tensor cases fail safely;
- fuzz/sanitizer smoke covers the parser;
- no tensor data is trusted before full descriptor validation.

**Closure:** accepted at
`b8e06a0058a56f2ae9fbd1f92ae0bade40b88ec7` and frozen by the immutable
annotated tag `orcengine-phase2-freeze` on 2026-08-16. The direct Hugging
Face/PyTorch comparison, real tied-output execution, >4 GiB sparse extent test,
and explicit four-lane validation matrix are recorded in
[Phase-2 Freeze Hardening](PHASE2_FREEZE_HARDENING.md).

Phase 2 exceeded its original scope deliberately after the parser was proven:
it established real F32 execution correctness before stopping. That invalidates
the old assumption that real-model loading/execution remained wholly in Phase 3.

## Phase 3 — Real-model streaming / working-set reference — FROZEN

**Question:** what is the minimum practical working set required to execute the
real F32 model correctly?

**Reason for reconciliation:** the original Phase-3 goal—load the pinned real
F32 model and match the oracle—was completed and independently frozen in Phase
2. Tokenization and KV-cached decode remain undone, but combining those semantic
changes with the first nonresident execution path would confound the memory
experiment.

**Implemented scope:** retain Phase-1 math and Phase-2 interpretation; keep the
validated manifest open; retain only embedding/final-norm/distinct-output
bookends; materialize one real GGUF-backed layer at a time; release it before
the next; compare against full materialization and Hugging Face/PyTorch; measure
resident bytes, process peak RAM, materializations, backing reads, repeated
reads, and time.

**Definition of done:**

- frozen Phase-1/2 gates remain unchanged and green;
- full-resident behavior remains bit-identical after any shared layer-block extraction;
- real explicit and tied F32 artifacts execute one layer at a time;
- streamed taps/logits/tokens are bit-identical to full materialization and remain tolerance-clean against Hugging Face/PyTorch;
- no more than one transformer layer's residents coexist;
- engine-owned peak resident weight bytes are below 50% of full materialization for both retained artifacts;
- process peak RAM, bytes read, repeated reads, materialization count, and timing are reported for one and four steps;
- one evidence-based decision records whether layer granularity is sufficient or one narrower experiment is warranted;
- strict/ASan/Debug/Release inclusion is explicit;
- independent freeze review accepts the result.

**Non-goals:** tokenizer, KV cache, CUDA, quantized compute, BLAS/SIMD/threading,
generic planner/cache framework, batching, product integration, and tile paging.

**Authority:** immutable tag `orcengine-phase3-freeze`, commit
`98dbcf1f370a93574da32dc02ebdcfeff8a60b3d`; see
[Phase-3 Working-Set Specification](PHASE3_WORKING_SET_SPEC.md) and
[Phase-3 Freeze Hardening](PHASE3_FREEZE_HARDENING.md).

## Phase 4 — Bookend virtualization / sub-tensor working set — FROZEN

**Question:** does a complete embedding/output matrix need to be resident at
once after transformer layers already stream?

**Implemented scope:** format-neutral logical row regions, one row per unique
input token, vocabulary-row output chunks with complete logits, unchanged
Phase-3 layer streaming, tied backing reuse, measured observer events, exact
budgets, and adversarial partition validation.

**Definition of done:**

- all frozen Phase-1/2/3 behavior remains green;
- multiple partitions including one row and a remainder produce exact complete logits;
- explicit and tied real artifacts match frozen Phase 3 and Hugging Face/PyTorch;
- measured peak is substantially below Phase 3 and peak-minus-one rejects;
- neutral in-memory regions, observer neutrality, safety attacks, and all four build lanes pass;
- independent freeze review accepts the result.

**Evidence:** [Phase 4 Bookend Virtualization](PHASE4_BOOKEND_VIRTUALIZATION.md).
**FROZEN 2026-08-18** at trusted commit `944f07b86428ec53d46ca19dc66c3d0d5b1e207d`,
immutable pushed annotated tag `orcengine-phase4-freeze`. Independent review
(OE-ADR-022) returned ACCEPT WITH FIXES; closure (OE-ADR-023) resolved all
three residual hygiene items with no engine defect found. See OE-ADR-024 for
the post-freeze roadmap reconciliation below — the prior practical CPU/
tokenizer/KV/usability roadmap item is now Phase 5, not silently discarded
and not further deferred.

## Phase 5 — Practical CPU inference semantics

**Status, 2026-08-18 (OE-ADR-024):** the phase number this roadmap previously
withheld pending Phase-4 independent review (see the old Phase 4 text above)
is now assigned. Split into three separately-gated sub-phases rather than one
bundled implementation step, per the explicit instruction that a phase needs
one bounded hypothesis and measurable exit gate — tokenizer correctness, KV
cache correctness, and workspace/benchmark work are three different risk
profiles, not one.

### Phase 5A — KV-cached incremental decode reference

**Question:** does one-token-at-a-time cached decode, reading each transformer
layer's weights the same way frozen Phase 3/4 already stream them, produce
exactly the same logits as full-prefix recompute, at every step, for every
GQA/RoPE/causal-boundary case the fixed profile exercises?

**Why this is the first bounded slice:** of the deferred items, cached decode
is the only one that adds a genuinely new *tensor-execution* path (a second
way to compute attention against evolving state) rather than plumbing an
already-proven algorithm (tokenization) or an optimization concern (workspace
reuse, benchmarking) into the existing one. It is explicitly the highest-risk
correctness boundary among the deferred items — RoPE position handling, GQA
key/value head indexing, and causal-boundary correctness against a cache all
have failure modes full-prefix recompute cannot expose. Establishing it first
means Phase 5B (tokenizer) and 5C (workspace/benchmarking) build on a
memory-model and execution-correctness foundation that has already survived
adversarial fault injection, rather than the reverse.

**Scope, oracle, memory model, fault-injection plan, and definition of
done:** [Phase 5A KV-Cached Decode Specification](PHASE5A_KV_CACHE_SPEC.md).

**Status: formally frozen 2026-08-20.** Freeze authority:
`orcengine-phase5a-freeze` (annotated tag, local/unpushed;
`DECISION_LOG.md` OE-ADR-029). Phase 5B specification accepted for
implementation 2026-08-20 on `feat/orcengine-phase5b-tokenizer` (forked
from the freeze tag; `DECISION_LOG.md` OE-ADR-030); Stage 1 (native
GGUF tokenizer-metadata construction and fail-closed validation) was
implemented the same day. Encode/decode and frozen-engine integration
are not implemented; Phase 5B is not complete or frozen. Phase 5C
remains deferred until Phase 5B closes, per the dependency ordering
below.

### Phase 5B — Tokenizer / text-token boundary (implementation in progress: Stage 1 + Stage 2A complete, encode/decode not started)

Exact tokenizer format/profile, source-vs-GGUF-embedded tokenizer agreement,
BOS/EOS, byte/Unicode/whitespace handling, special-token policy,
encode/decode round-trip, malformed-metadata rejection — using the pinned
real SmolLM2-135M candidate, compared against an independent trusted
implementation. Phase 0's oracle already proved `tokenizer_dual_source_
agreement` and `raw_prompt_identity` at the Python level (see
`PHASE_0_ACCEPTANCE.yaml`); this sub-phase's job is wiring an equivalent,
independently re-proven path into the C++ engine itself, which currently
takes only explicit token IDs. **Specification accepted for
implementation 2026-08-20** (maintainer approved all seven previously-
unresolved policy decisions; `DECISION_LOG.md` OE-ADR-030); **Stage 1
implemented, closure-corrected, and green-lane classified the same
day** (`DECISION_LOG.md` OE-ADR-031): `Tools/OrcEnginePhase5B/`
constructs and validates the pinned tokenizer profile from GGUF
metadata. `smollm2-135m.gguf` is the canonical, tokenizer-bearing Phase
5B artifact; `smollm2-135m-tied.gguf` is a frozen legacy tensor/
output-head-equivalence fixture with no tokenizer metadata, and its
rejection is a required, passing test outcome, not a gap. Three
independent test contracts (synthetic, explicit real-artifact positive,
legacy tied-artifact expected-rejection) are all clean across
Debug/Release/strict/ASan — no registered Phase 5B test intentionally
fails. **Stage 2A (exact native pretokenization) implemented the same
day** (`DECISION_LOG.md` OE-ADR-032): `pretokenize.cpp` reproduces the
pinned `Digits->ByteLevel` sequence exactly, producing byte-range
pretoken boundaries only. Its Unicode classification tables were
generated from Python's `unicodedata` and then validated against the
live `tokenizers==0.22.2` oracle (2,831 boundary probes + 4,974 random
codepoints, 0 mismatches after correcting a discovered gap in a naive
`str.isspace()` candidate). `test_pretokenize` matches a 63-entry
oracle-derived fixture corpus plus 8 invalid-UTF-8 cases byte-for-byte:
209/209 checks, 0 failures, across Debug/Release/strict/ASan. Matches
the pinned oracle across the 63-entry corpus, all generated category
boundaries, and the recorded seeded sample; exhaustive equivalence
over every possible Unicode string is not claimed. The generator was
subsequently hardened (`DECISION_LOG.md` OE-ADR-033): fail-closed on
the installed `tokenizers` version and the supplied `tokenizer.json`'s
SHA-256/declared contract, no hard-coded machine-specific path, a
read-only `--check` mode, and the complete provenance hash record.
Token-ID production (BPE merge execution, byte-to-Unicode mapping),
decoding, and frozen-engine integration remain unimplemented. See
[Phase 5B Tokenizer Specification](PHASE5B_TOKENIZER_SPEC.md) for full
status. The llama.cpp secondary-oracle comparison remains an
outstanding validation dependency, required
before Phase 5B can be
considered
complete or frozen, not before implementation may begin.

### Phase 5C — Bounded activation workspace and prompt/decode benchmarking (deferred, not yet specified)

Explicit scratch-buffer accounting distinct from `ResidentView`/weight
residency and from KV-cache residency; workspace-reuse-does-not-change-
numerics proof; then, only after correctness, prompt/prefill vs first-token
vs steady-state decode measurement across full-recompute and cached paths.
Not started; spec to be written when 5A and 5B close, since workspace reuse
is far more meaningful once decode is actually incremental (5A) and real text
input exists (5B).

## Phase 6 — Initial quantization

**Renumbered from "Phase 5" (2026-08-18, OE-ADR-024) — the heading's content
predates Phase 3/4 and OE-ADR-021's later decision that the deferred
practical-CPU work takes priority; see OE-ADR-024 for the full chronology
reconciliation.** No dependency requires this to follow Phase 5: quantization
operates entirely below the token boundary, and Phase 2's existing F32
full-prefix reference plus a pinned external engine remain a sufficient
quantization oracle without requiring cached decode first (`PHASE0_
ACCEPTANCE.yaml`'s and Phase 2's evidence already establish that baseline).
The ordering is a risk-reduction choice (OE-ADR-021), not a technical
dependency — recorded explicitly so it is not mistaken for one.

**Goal:** support one quantized weight format without sacrificing diagnosis.

**Order:** Q8_0 reference dequantization, direct Q8 dot product if needed, then Q4_0 through the same sequence.

**Definition of done:** format parser and dequantizer match trusted vectors; logits are compared against both float and a pinned external engine; memory reduction is measured; quality impact is reported on a fixed corpus.

**Row-region interaction, flagged in advance (OE-ADR-024):** Phase 4's
`TensorRowRegion` contract proves contiguous logical F32/F16 rows only. If
Q8_0 row materialization can be implemented behind that existing logical
contract without changing its meaning (block-aligned row ranges resolved by
the source adapter, same as Phase 4's F16 decode path), demonstrate that. If
quantization-block geometry instead requires a genuinely new logical
contract, this phase must stop and document that architecture question
rather than assuming Phase 4 already answered it.

## Phase 7A — Resident CUDA correctness baseline

**Goal:** reproduce approved CPU results on one NVIDIA target.

**Scope:** explicit device ownership, long-lived weights/cache, cuBLAS dense operations, minimal elementwise kernels, one stream unless evidence demands more.

**Definition of done:**

- device allocation and errors are deterministic and leak-free;
- CPU/CUDA taps meet per-operator tolerances;
- synchronization is explicit;
- compute capability, driver, CUDA toolkit, library versions, and build flags are recorded;
- prompt and decode paths both execute on the intended backend.

**Renumbered from "Phase 6" (2026-08-15, `Infinite_Model_Runtime_Claude_Handoff.md` steering review, see [Decision Log](DECISION_LOG.md) OE-ADR-019).** Full-residency CUDA is still the correct FIRST CUDA milestone — do not skip it for paging — but it is no longer treated as the *only* supported CUDA execution mode before the stable ABI freezes. See 7B/7C/7D below and the roadmap contract note at the top of this document.

## Phase 7B — ExecutionPlanner and explicit residency model

**Goal:** formalize where tensors live and how that's decided, before paged/streamed execution is attempted — so 7C doesn't retrofit residency semantics onto types that assumed permanent residency.

**Why this phase exists:** Phase 0's own ablation-diagnostic tooling (`Tools/OrcEnginePhase0/oracle/gguf_streaming_loader.py`, built after Phase 0 closed) proved in Python that a model does not need to be materialized all at once to execute — Meta-Llama-3.1-8B, which failed to load under every full-residency approach tried in the same session, completed a full forward-pass sweep using 3.17GB peak VRAM by loading one transformer layer from disk, using it, and discarding it before the next. That is real evidence, not speculation, that OrcEngine's permanent architecture must not bake in "the model lives in VRAM" as a foundational assumption.

**Scope — contracts, not full implementations (Phase 1 stays boring; see this document's Permanent verification rule and `ARCHITECTURE.md`):**

- `LogicalTensor` (semantic identity: shape, layout, architecture role) is distinct from `BackingExtent` (source artifact, byte offset, codec, checksum) is distinct from `ResidentView` (memory tier, resident address, resident/compute dtype, lease/lifetime). A logical tensor must survive eviction/reload without changing identity.
- "Model loaded" means source opened, GGUF validated, tensor index built, architecture manifest built, model addressable through a plan — NOT "all tensors copied into RAM/VRAM." Separate `Model::Open` / `ExecutionPlan::Create` / `Context::Create` (or equivalent) rather than one monolithic load call.
- `ExecutionPlanner` (OrcEngine: where do tensors live, what's resident, what streams, what's the fallback plan) is explicitly NOT `OrcScheduler` (TheOrc: should this workload run, on which role/node, what resource policy) — do not build a second product scheduler.
- `gpu_layers` (the llama.cpp placement mechanism TheOrc's `OrcScheduler`/`RuntimeOrchestrator` already use for admission estimates) is a useful CURRENT signal but is explicitly NOT promoted to a fundamental OrcEngine ABI concept — a single integer cannot describe per-tensor/per-tile placement across VRAM/RAM/NVMe tiers, which OrcEngine may eventually need.
- Separate source format / transport format / resident format / compute format as four independent concepts (do not assume GGUF dtype == VRAM dtype == compute dtype) — motivated directly by a real bug this session found and fixed in the streaming/GPU oracle path: naive fp16 storage with fp16 compute silently overflowed (RMSNorm's `x^2` reduction, raw attention-score accumulation, and an FFN down-projection all independently overflowed fp16's max before this was caught), fixed by keeping storage compressed but computing in float32. The general principle — storage precision and compute precision are independent choices, not the same thing — is exactly what this phase should formalize for the C++ engine.
- Unknown/unsupported resource cost is `UnknownCost(reason)` / `UnsupportedCostModel(reason)`, an explicit state the planner understands, not a numeric placeholder. (The Native Runtime C# patch for the equivalent admission bug used a large sentinel constant as a pragmatic, narrowly-scoped compatibility fix — that sentinel-value pattern is explicitly NOT the design to carry into OrcEngine's own cost model.)
- **Region granularity is a policy variable, not a fixed floor** — evidence added by Phase 4's bookend/row-region virtualization and its independent freeze review (see `PHASE4_BOOKEND_VIRTUALIZATION.md` and `DECISION_LOG.md` OE-ADR-022/023): Phase 4's `TensorRowRegion` execution proved a large logical tensor need not be fully resident when its operation decomposes into logical row regions, but the resulting peak resident weight bytes are a function of the CHOSEN region size (`output_chunk_rows`), not an inherent property of the tensor or the technique — smaller regions trade lower residency for more materializations (more read/decode overhead), larger regions trade the reverse, and the crossover point where one term starts dominating another (e.g. output-chunk residency vs. transformer-layer residency) is itself model/dtype/backend-specific, not a universal constant. `ExecutionPlanner` should treat region/chunk size as a real decision variable with a real cost tradeoff, not assume "smaller is always better" or bake in one phase's measured optimum.

**Definition of done:** the three type distinctions above exist as documented contracts (`ARCHITECTURE.md`) with trivial Phase-1-appropriate implementations (`ResidentView` = a CPU pointer; `ExecutionPlanner` = always chooses `ResidentCPU`; `ContextStateStore` = `ContiguousKVStore`) — sophistication belongs in the contracts, not in Phase 1's code.

**Roadmap reconciliation, 2026-08-16:** Phase 1/2 already implemented the three
storage identities, and proposed Phase 3 now validates temporary CPU residency
before CUDA. If Phase 3 succeeds, Phase 7B narrows to multi-tier/device
placement, transfer, fallback, and explicit cost semantics. It must not repeat
the CPU layer-streaming proof or introduce a planner into Phase 3 prematurely.

## Phase 7C — Paged/streamed CUDA proof

**Goal:** prove the same true-streaming, layer-by-layer execution already demonstrated in the Python research harness works in the actual C++/CUDA engine, not just as a research tool.

**Definition of done:** at least one model whose weights exceed available VRAM executes successfully end-to-end through the real engine (not the Python oracle) using the `ExecutionPlanner`/residency contracts from 7B. Slow is an acceptable outcome; "too big for VRAM" alone is not an acceptable terminal failure once this phase starts.

## Phase 7D — Compressed transport and advanced paging research

**Goal:** explicitly experimental research, allowed to fail, not a Phase 1/7A-7C blocker. Candidate directions (see `Infinite_Model_Runtime_Claude_Handoff.md` for the full list; do not treat any of these as decided): tensor/tile-level paging below whole-layer granularity, separate storage/transport/resident/compute precision per tensor, ablation-sensitivity-informed quantization bit allocation (Phase 0's ablation tooling already produces the sensitivity data this would consume — see `oracle/ablation_sweep*.py` and the retained fleet reports under `Tools/OrcEnginePhase0/artifacts/`, though zero-ablation sensitivity is explicitly NOT the same claim as quantization sensitivity and would need its own direct experiments), speculative decoding as a way to amortize expensive weight-page loads over more useful tokens rather than only as a latency trick, MoE expert paging/prefetching, and a disposable content-addressed derived execution cache (GGUF stays canonical; the cache is rebuildable, never a competing model format).

## Phase 8 — Stable native API and managed wrapper

**Renumbered from "Phase 7" (2026-08-18, OE-ADR-024, insertion of Phase 5 -- practical CPU inference semantics -- bumped every phase from the old Phase 5 quantization slot onward by one).**

**Goal:** expose the proven standalone engine safely to .NET.

**Gated on 7C, not just 7A.** The stable ABI must not be frozen before paged/nonresident execution has exercised the model/context/storage contracts — freezing it right after 7A would bake in a full-residency worldview this project's own Phase-0-adjacent evidence has already disproven (see OE-ADR-019).

**Scope:** small C ABI, opaque handles, stable errors, cancellation, UTF-8/token buffers, measured telemetry, SafeHandle-based managed ownership.

**Definition of done:** invalid handles and lifetime misuse fail safely; callbacks do not outlive owners; cancellation works; packaging resolves exact native binaries; repeated managed load/generate/dispose is clean.

## Phase 9 — Experimental TheOrc backend

**Renumbered from "Phase 8" (2026-08-18, OE-ADR-024).**

**Goal:** add `OrcEngineRuntime` as an explicitly experimental `ILocalModelRuntime` implementation.

**Constraints:** off by default, no silent fallback, visible actual-runtime telemetry, no default or installer changes, current runtimes preserved.

**Definition of done:** targeted unit tests, native integration tests, one real manual `/verify` flow, exact runtime identity, and documented rollback.

## Phase 10 — Agent-native experiments

**Renumbered from "Phase 9" (2026-08-18, OE-ADR-024).**

Only after Phase 9 may the project test role-owned caches, reusable Context Fabric token blocks, adapter-aware planning, or HIVE execution. Each experiment requires a baseline against the current runtime and an explicit unique-value criterion.

## Permanent verification rule

Every phase leaves behind the smallest runnable check that detects its central failure. Passing compilation is not completion. Plausible text is not completion. A review saying “looks correct” is not completion.
