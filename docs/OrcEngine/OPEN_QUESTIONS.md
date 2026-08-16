# Open Questions

## Maintainer decisions and Phase 0 research state

| ID | Question | Options | Recommendation | Status |
|---|---|---|---|---|
| OQ-001 | Where should implementation eventually live? | Monorepo native project; separate repository; defer | Defer until Phase 0 proves value | Open |
| OQ-002 | What is the first model? | Synthetic plus published tiny model | `OE-L0-SYNTH-1` plus SmolLM2-135M candidate | Decided; artifacts pending |
| OQ-003 | What primary oracle? | Python semantic oracle plus independent checks | Hand math + pinned Python + pinned llama.cpp | Decided; versions pending |
| OQ-004 | Can real-model artifacts be redistributed? | Commit; scripted download; local-only | Scripted download (not committed) | Resolved for SmolLM2-135M 2026-08-15: Apache-2.0 permits redistribution; repo hygiene (not license) rules out committing the 269MB weights file. See LICENSING_AND_ATTRIBUTION.md attribution ledger. Re-review per future candidate. |
| OQ-005 | C++ immediately? | Python oracle then C++20 core | Python oracle then C++20; CUDA/C ABI/C# later | Decided |
| OQ-006 | First CPU matrix dependency? | Scalar only; BLAS; installed library | Scalar tiny path, BLAS only after profile/portability review | Open |
| OQ-007 | Required first OS? | Windows x64; Windows+Linux; cross-platform | Verify Windows x64 first; label others unknown | Proposed |
| OQ-008 | Product-value gate before engine code? | Prevented capability; measurable improvement | Accept either with a bounded evidence thesis | Resolved: OE-ADR-016 accepted by maintainer 2026-08-15, as written. |

## Architecture questions

- OQ-009: Is eager execution sufficient through CUDA, or is a graph IR needed for workspace/device scheduling?
- OQ-010: Should model architecture code own operator sequence directly or build a small immutable execution plan?
- OQ-011: Should the first context allocate maximum KV capacity or grow in bounded pages?
- OQ-012: Which backends can prove rollback after partial writes? Default semantics are decided: otherwise poison the context.
- OQ-013: Is model destruction with active contexts rejected or reference-counted?
- OQ-014: What cancellation frequency is adequate during large prompt evaluation?
- OQ-015: Which telemetry belongs in stable API versus diagnostic extensions?

## Numerical questions

- OQ-016 through OQ-019 are resolved for `OE-L0-SYNTH-1`; verify the converted real candidate reproduces the source values in [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md).
- OQ-020: What operator-specific float32 tolerances catch seeded defects without false failure?
- OQ-021: How large must the top-token margin be for token agreement to be meaningful?
- OQ-022: What deterministic settings are effective in the primary oracle?

## Format/tokenizer questions

- OQ-023: Which GGUF structural version and endian mode first?
- OQ-024: Does the first model require F16 or quantized storage?
- OQ-025: Which unknown metadata keys are preserved/reported versus rejected?
- OQ-026: What exact tokenizer family and normalization rules apply?
- OQ-027: Does the GGUF embedded chat template match TheOrc’s current prompt expectations?
- OQ-028: How are partial UTF-8 token bytes streamed?

## Product questions

- OQ-029: Does future `OrcEngineRuntime` implement `ILocalModelRuntime` before adapter support exists?
- OQ-030: Should evidence workloads prohibit fallback globally or per request?
- OQ-031: How does model identity integrate with `ModelDepot` without coupling to LLamaSharp executors?
- OQ-032: Which current prompt/tool parser is reusable without changing semantics?
- OQ-033: What user-facing warning is required for an experimental engine?
- OQ-034: What is the exact rollback/cleanup behavior for native assets?

## Governance questions

- OQ-035: Who accepts numerical tolerance changes?
- OQ-036: Where are large oracle artifacts retained?
- OQ-037: Which reviewers are mandatory at phase gates?
- OQ-038: What budget of maintainer time triggers a pause review?
- OQ-039: When does a research prototype warrant a separate repository and release policy?
- OQ-040: What legal review is required before distributing CUDA/BLAS/model artifacts?

## Memory-model and paging questions (added 2026-08-15)

Added following the Python-side streaming/ablation evidence (see [Project Truth](PROJECT_TRUTH.md) and [Decision Log](DECISION_LOG.md) OE-ADR-019) and the `Infinite_Model_Runtime_Claude_Handoff.md` steering review. These target Phase 6B ("ExecutionPlanner and explicit residency model" in [Engineering Roadmap](ENGINEERING_ROADMAP.md)), not Phase 1.

- OQ-041: What is the right default granularity for `ResidentView` paging in the real C++ engine -- whole layer (matches the Python proof-of-concept), individual tensor, or tensor tile? The Python research harness only ever proved whole-layer streaming; tensor/tile-level paging is documented as a future capacity floor, not yet attempted even in Python.
- OQ-042: Does `LayerIntervention`'s algebraic-equivalence approach (an ablation/mask/disable is an execution-path change, not a weight mutation) generalize cleanly to real product use cases beyond ablation research -- LoRA adapters, selective precision, MoE expert disabling? The Python evidence only demonstrates this for ablation zeroing on plain (non-MoE, non-hybrid) transformer blocks.
- OQ-043: What is the actual byte-amortization/throughput cost of true layer streaming in a real (non-research-harness) inference loop generating many tokens, not a one-shot ablation sweep? The measured 3.17GB/Llama-3.1-8B result is a single-forward-pass proof, not a decode-loop throughput measurement -- re-reading every layer from disk per generated token would be prohibitively slow without either KV-cache-aware page reuse or speculative decoding to amortize the cost (see Phase 6D).
- OQ-044: Does ablation-sensitivity data (now available for several real models via `Tools/OrcEnginePhase0/artifacts/`) actually predict useful quantization bit-allocation decisions, or does it need a dedicated direct quantization-perturbation experiment before being trusted for that purpose? Explicitly flagged as unproven in OE-ADR-019 and `PROJECT_TRUTH.md` -- zero-ablation sensitivity is not the same claim as quantization sensitivity.
- OQ-045: What should `UnknownCost`/`UnsupportedCostModel` actually look like as OrcEngine types (vs. the narrow sentinel-constant pattern used as a pragmatic Native Runtime C# patch, which is explicitly not meant to set the OrcEngine precedent)?

## Triage rule

Move questions into [Research Questions](RESEARCH_QUESTIONS.md) when they need a structured experiment. Resolve accepted decisions in [Decision Log](DECISION_LOG.md). Keep rejected alternatives with rationale so another agent does not reopen them without new evidence.
