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

## Phase 2 — Strict GGUF reader and inspector

**Goal:** parse and validate the pinned artifact without executing it.

**Scope:** supported GGUF version, typed metadata, alignment, tensor descriptors, bounds/overflow validation, file mapping, supported tensor types.

**Deliverables:** `orc-gguf-inspect`, machine-readable manifest, malformed fixtures, tensor-name/dimension validator.

**Definition of done:**

- inspector output matches trusted tools for the pinned model;
- truncation, overflow, overlap, invalid alignment, invalid type, duplicate key, and missing tensor cases fail safely;
- fuzz/sanitizer smoke covers the parser;
- no tensor data is trusted before full descriptor validation.

## Phase 3 — Real-model float32 CPU inference

**Goal:** load the pinned model and match the oracle.

**Scope:** exact tokenizer, fixed graph, batch one, one sequence, prompt evaluation, cached decode, greedy output.

**Definition of done:**

- tokenization matches byte-for-byte fixtures;
- all required weights and dimensions validate;
- intermediate tensors and logits meet approved bounds;
- generated token IDs match for the approved fixture set;
- repeated create/run/destroy is leak-free;
- cancellation and malformed-input checks pass;
- performance is reported honestly without a pass threshold.

**Stop gate:** assess strategic value and maintenance cost before optimization.

## Phase 4 — CPU usability baseline

**Goal:** make the correct CPU engine measurable and usable for experiments.

**Candidate work:** BLAS-backed GEMM, workspace reuse, mapped weights, bounded threading, tiled kernels, then SIMD where profiling justifies it.

**Definition of done:**

- optimized results remain within differential tolerances;
- scalar/reference path is retained;
- benchmark captures hardware, compiler, flags, thread count, memory, prompt/decode rates, and raw artifacts;
- no unexplained regression exceeds the agreed threshold.

## Phase 5 — Initial quantization

**Goal:** support one quantized weight format without sacrificing diagnosis.

**Order:** Q8_0 reference dequantization, direct Q8 dot product if needed, then Q4_0 through the same sequence.

**Definition of done:** format parser and dequantizer match trusted vectors; logits are compared against both float and a pinned external engine; memory reduction is measured; quality impact is reported on a fixed corpus.

## Phase 6 — CUDA correctness baseline

**Goal:** reproduce approved CPU results on one NVIDIA target.

**Scope:** explicit device ownership, long-lived weights/cache, cuBLAS dense operations, minimal elementwise kernels, one stream unless evidence demands more.

**Definition of done:**

- device allocation and errors are deterministic and leak-free;
- CPU/CUDA taps meet per-operator tolerances;
- synchronization is explicit;
- compute capability, driver, CUDA toolkit, library versions, and build flags are recorded;
- prompt and decode paths both execute on the intended backend.

## Phase 7 — Stable native API and managed wrapper

**Goal:** expose the proven standalone engine safely to .NET.

**Scope:** small C ABI, opaque handles, stable errors, cancellation, UTF-8/token buffers, measured telemetry, SafeHandle-based managed ownership.

**Definition of done:** invalid handles and lifetime misuse fail safely; callbacks do not outlive owners; cancellation works; packaging resolves exact native binaries; repeated managed load/generate/dispose is clean.

## Phase 8 — Experimental TheOrc backend

**Goal:** add `OrcEngineRuntime` as an explicitly experimental `ILocalModelRuntime` implementation.

**Constraints:** off by default, no silent fallback, visible actual-runtime telemetry, no default or installer changes, current runtimes preserved.

**Definition of done:** targeted unit tests, native integration tests, one real manual `/verify` flow, exact runtime identity, and documented rollback.

## Phase 9 — Agent-native experiments

Only after Phase 8 may the project test role-owned caches, reusable Context Fabric token blocks, adapter-aware planning, or HIVE execution. Each experiment requires a baseline against the current runtime and an explicit unique-value criterion.

## Permanent verification rule

Every phase leaves behind the smallest runnable check that detects its central failure. Passing compilation is not completion. Plausible text is not completion. A review saying “looks correct” is not completion.
