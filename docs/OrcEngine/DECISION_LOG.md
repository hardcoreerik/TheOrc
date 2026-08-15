# Decision Log

## Rules

- Append decisions; do not rewrite old outcomes to look inevitable.
- Use one stable ID per decision.
- Record evidence, alternatives, consequences, owner, and review date.
- A proposed decision is not accepted until status changes to `accepted`.
- Superseding decisions link both directions.

## Template

```text
ID: OE-ADR-NNN
Title:
Status: proposed | accepted | rejected | superseded
Date:
Owner:
Context:
Evidence:
Decision:
Alternatives:
Consequences:
Validation/revisit trigger:
Supersedes / superseded by:
```

## OE-ADR-001 — Begin as documentation and research

- **Status:** accepted
- **Date:** 2026-07-18
- **Context:** A from-scratch engine is a large independent systems project with correctness, hardware, maintenance, and licensing risk.
- **Decision:** establish a reviewable document corpus and deterministic oracle plan before implementation.
- **Alternatives:** create a source scaffold immediately; fork llama.cpp; replace LLamaSharp directly.
- **Consequences:** no executable OrcEngine claim exists; implementation waits on Phase 0 approval.
- **Revisit trigger:** documentation review closes blocking contradictions and selects oracle/model.

## OE-ADR-002 — Coexist with current runtimes

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** OrcEngine starts as an experimental backend alongside `LLamaSharpRuntime`, `LlamaCppServerRuntime`, and `OllamaRuntime`; it does not replace or change defaults.
- **Evidence:** current runtimes are real and TheOrc depends on them; OrcEngine has no implementation.
- **Consequence:** integration is late, opt-in, and rollback is disabling selection.

## OE-ADR-003 — Define “from scratch” by execution ownership

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** OrcEngine owns model parsing, architecture semantics, tensor/cache execution, tokenization, and decoding. General compute/platform libraries may be used; complete inference engines may only serve as test oracles.
- **Consequence:** using BLAS/cuBLAS is permitted; wrapping llama.cpp/LLamaSharp as execution is not OrcEngine.

## OE-ADR-004 — Reference-oracle-first development

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** Phase 0 pins model, tokenizer, oracles, intermediate taps, tolerances, hashes, and fault injection before engine code.
- **Consequence:** plausible generated text cannot satisfy correctness gates.

## OE-ADR-005 — Tiny CPU-only float32 first engine

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** first engine supports one small classic Llama-style text model, float32 CPU, batch one, one sequence, fixed context, greedy decode.
- **Rejected for first phase:** CUDA, quantization, multiple architectures/sequences, adapters, grammar, HIVE.

## OE-ADR-006 — Eager fixed model loop before generic graph

- **Status:** proposed
- **Date:** 2026-07-18
- **Decision:** directly invoke the minimum operators for the pinned architecture.
- **Alternative:** build graph IR/planner first.
- **Revisit trigger:** a measured backend, workspace, fusion, or scheduling requirement cannot be met clearly with eager execution.

## OE-ADR-007 — Strict compatibility tuple

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** never claim universal GGUF support; list exact format/architecture/tokenizer/dtype/backend/platform combinations.
- **Consequence:** unsupported inputs fail before execution with structured reasons.

## OE-ADR-008 — C ABI for eventual .NET integration

- **Status:** proposed
- **Date:** 2026-07-18
- **Decision:** use opaque handles and plain C data structures with a SafeHandle-based managed wrapper.
- **Revisit trigger:** standalone engine proof reveals a simpler safe boundary or ownership mismatch.

## OE-ADR-009 — cuBLAS-first CUDA baseline

- **Status:** proposed
- **Date:** 2026-07-18
- **Decision:** use cuBLAS/cuBLASLt experiment for dense operations before considering custom GEMM.
- **Revisit trigger:** Phase 6 profiling and layout/reproducibility results.

## OE-ADR-010 — OrcEngine is a separately authorized research track

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** current native-runtime documents describing LLamaSharp as the computation layer do not prohibit a separately scoped from-scratch OrcEngine experiment.
- **Consequence:** OrcEngine remains docs/research-only until its own gates pass and does not alter native-runtime production work.

## OE-ADR-011 — Product value may be prevention or measured improvement

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** continued work may be justified by a capability the existing stack prevents or by a material reproducible improvement on an agreed metric.
- **Consequence:** OrcEngine is not limited to novelty, but vague “does better” claims do not pass; baseline, workload, metric, hardware, and artifacts are required.

## OE-ADR-012 — Staged implementation-language stack

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** use Python with NumPy/PyTorch for Phase 0 oracles, C++20 for the standalone engine, CUDA C++/cuBLAS in the GPU phase, and a small C ABI plus C# `SafeHandle` wrapper only after standalone proof.
- **Alternative:** Rust core.
- **Revisit trigger:** measured safety, portability, build, or CUDA-integration evidence favors another core language before Phase 1 begins.

## OE-ADR-013 — Standards-compatible strict GGUF ingestion

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** implement a narrow official-GGUF subset with two-pass validation, bounded resources, typed manifest, precise compatibility verdicts, and fail-closed behavior. Do not create a proprietary model container for the initial path.
- **Consequence:** arrays may nest, tensor offsets are data-section-relative, and absent `general.alignment` means 32; these are parser-contract requirements, not converter folklore.

## OE-ADR-014 — First synthetic profile and real-model candidate

- **Status:** accepted
- **Date:** 2026-07-18
- **Decision:** `OE-L0-SYNTH-1` is the exact synthetic profile. `HuggingFaceTB/SmolLM2-135M` revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` is the first real-model candidate.
- **Consequence:** candidate status does not imply supported execution. Conversion, hashes, tokenizer reconciliation, licensing, and oracle reproduction remain Phase 0 gates.

## OE-ADR-015 — Fixture B/C weight-init scale raised to 0.1

- **Status:** accepted
- **Date:** 2026-08-14
- **Owner:** Claude (Sonnet 5), OrcEngine Phase 0 loop
- **Context:** `oracle/weights.py`'s synthetic weight generator (`numpy.random.default_rng(seed).standard_normal() * WEIGHT_SCALE`) originally used `WEIGHT_SCALE = 0.02`. The Phase 0 exit gate requires the oracle harness to detect seeded faults (transposed projections, wrong RoPE pairing, missing causal mask, etc.); a scale too small relative to the residual path can mask those faults.
- **Evidence:** measured on Fixture B (`n_layers=1`, hidden=16, seed=20260814, token_ids=[1,5,9,3]), injecting a transposed `w_o` fault and comparing logits against the unfaulted baseline:
  - `scale=0.02` → max\|Δlogit\|=0.0696, mean\|Δlogit\|=0.0207, **argmax unchanged** (fault invisible to greedy-decode comparison)
  - `scale=0.10` → max\|Δlogit\|=1.2084, mean\|Δlogit\|=0.3679, argmax changed
  - `scale=0.30` → max\|Δlogit\|=6.0313, argmax changed
  - `scale=0.50` → max\|Δlogit\|=10.0058, argmax changed
  - `scale=1.00` → max\|Δlogit\|=18.9345, argmax changed
- **Decision:** set `WEIGHT_SCALE = 0.1` — the smallest tested value where the reference fault reliably flips argmax, keeping weights as close to the original small-init intent as the fault-detection requirement allows.
- **Alternatives:** leave at 0.02 and rely solely on tight numeric-tolerance comparison of intermediate taps (rejected for now: still fragile for faults with small first-order effect, and the exit gate's own bar is "detects at least" the seven listed faults, not "logits differ by any epsilon"); use a much larger scale (e.g. 1.0) for maximum fault visibility (rejected: unnecessarily far from typical small-init transformer behavior for no added benefit at this fixture size).
- **Consequence:** `Tools/OrcEnginePhase0/oracle/weights.py` regenerates different weight values than any artifact produced under the old scale; any previously retained Fixture B/C artifacts generated at 0.02 are stale and must be regenerated. No such artifacts were committed yet, so no migration is required.
- **Validation/revisit trigger:** if the full 7-fault injection suite (not yet built) finds a fault type that remains undetectable at 0.1, raise the scale again with the same kind of measured evidence, or introduce a fault-type-specific fixture instead of a single global scale.

## OE-ADR-016 — Phase 0 bounded product-value thesis (proposed, awaiting maintainer approval)

- **Status:** proposed
- **Date:** 2026-08-15
- **Owner:** Claude (Sonnet 5), OrcEngine Phase 0 loop — proposing for hardcoreerik's approval, NOT self-accepting
- **Context:** `ENGINEERING_ROADMAP.md`'s Phase 0 stop-gate requires, in addition to all 14 `PHASE_0_ACCEPTANCE.yaml` checks passing: "The maintainer must also approve a bounded product-value thesis: prevented capability or material measurable improvement." `OPEN_QUESTIONS.md` OQ-008 records the gate *type* as decided (prevented capability or measurable improvement, either acceptable) but leaves "thesis selection open" — no specific thesis has been proposed for approval yet. This entry proposes one, using evidence gathered this session rather than a hypothetical.
- **Evidence:** on 2026-08-15, the user asked to run Qwen3.8-27B (released 2026-08-13, `general.architecture = qwen35`) on this machine's RTX 5070 Ti. TheOrc's native runtime (LLamaSharp 0.27.0, published 2026-04-26 — confirmed via NuGet's own registration API to be the newest version SciSharp has ever published, no prerelease or newer version exists) failed to load the model with `LoadWeightsFailedException`, reproducibly, regardless of available VRAM (tested at both ~11GB and ~13GB free) and regardless of GPU-layer count (reproduced identically with `NativeRuntimeGpuLayers=0`, i.e. pure CPU/RAM path) — ruling out a memory/capacity explanation. A controlled experiment swapping fresh llama.cpp `b10436` (2026-08-14) binaries in place of LLamaSharp's bundled ones, while keeping LLamaSharp's C# P/Invoke layer, reproduced the identical failure — indicating native struct/ABI drift that P/Invoke cannot safely bridge, not a fixable binary-refresh problem. A separate `llama-cli.exe` from that same fresh `b10436` build loaded and ran the model successfully (confirmed by the user watching Task Manager memory climb on a genuine weight-load, and independently by the user reporting Ollama runs the model fine — Ollama vendors llama.cpp directly on its own update cadence, unlike LLamaSharp). LLamaSharp's release cadence over its last three versions was 2025-08-16 → 2026-02-15 (6 months) → 2026-04-26 (2.3 months); as of this writing it has been 3.6 months since 0.27.0 with no successor.
- **Decision (proposed):** adopt **prevented capability** as the Phase 0 product-value thesis: *"TheOrc's native runtime cannot load models using architectures introduced after LLamaSharp 0.27.0's vendored llama.cpp commit (April 2026), including at least one real, user-requested model (Qwen3.8-27B, August 2026) verified unloadable by three independent tests (in-process load, GPU-layer-count sweep, binary-swap experiment). This is a recurring, dated, structural gap — not a one-off bug — that will reproduce for every future model release until either LLamaSharp catches up (outside TheOrc's control, 2-6 month cadence) or TheOrc owns the computation plane itself. OrcEngine's success criterion against this thesis: an OrcEngine-loaded model that LLamaSharp's currently-bundled runtime cannot load, producing output that matches Phase 0/1's own oracle within the approved tolerance profile — first demonstrated on the synthetic profile and the Phase 0 real candidate, not yet claimed for Qwen3.8-27B itself (that would require OrcEngine to reach real-model, GPU-capable phases, far beyond Phase 0)."*
- **Alternatives:** (a) a measurable-improvement thesis (e.g., faster load time, lower VRAM overhead, better throughput than LLamaSharp on models it *can* load) — rejected as the *primary* Phase 0 thesis because OrcEngine's earliest concrete, dated evidence is about capability LLamaSharp categorically lacks, not incremental improvement on capability it already has; a measurable-improvement thesis remains a legitimate *secondary* claim once Phase 3+ produces comparable real-model numbers. (b) No thesis until a real model reaches Phase 3 — rejected because the roadmap requires the thesis to gate Phase 0's own closure, not defer it past Phase 0.
- **Consequences:** if accepted, Phase 0's closure requires this thesis statement (or the maintainer's edited version of it) alongside all 14 acceptance checks — not a vaguer or unstated one. It also sets an explicit non-claim: Phase 0 passing does NOT mean OrcEngine can run Qwen3.8-27B; that claim only becomes assessable once real-model + GPU phases (3, 6+) exist. Scope-creep risk (claiming more than Phase 0 proves) is deliberately fenced off in the wording above.
- **Validation/revisit trigger:** if the maintainer rejects "prevented capability" as the framing, or wants the thesis scoped to a different model/architecture gap, or wants a measurable-improvement clause added now rather than later — revise this entry (new status, not a silent edit) rather than treating it as settled.
