# OrcEngine documentation index

> Status: Phases 1-5C formally frozen (tags `orcengine-phase1-freeze` .. `orcengine-phase5c-freeze`); Phase 6 Stage 1 (Q8_0 quantization) in progress and NOT frozen; FL-08 research prototype frozen (`orcengine-fl08-freeze`). Machine-readable authority: [CURRENT_STATE.yaml](CURRENT_STATE.yaml)
>
> Product posture: experimental backend candidate; not a replacement for the current production/default runtime
>
> Product baseline verified against `origin/master`: `6ecdd66e5b6bd83de2c5aee2f6c7ed86568d40b7` (2026-07-31)
>
> Pending integration also reviewed: PR #96 head `16501dae4568391e8891dc091f8869d43ca6b7b9`; its studio tools are not treated as shipped

OrcEngine is the working name for a from-scratch model inference engine developed for TheOrc. “From scratch” means OrcEngine would own model-format interpretation, tensor execution, model graphs, tokenization, cache state, and decoding. It may initially use established operating-system, BLAS, or CUDA libraries, but it must not disguise LLamaSharp, llama.cpp, Ollama, or another complete inference engine behind a new name.

This directory contains both the original design corpus and verified Phase-1/2
implementation evidence. Documentation alone remains non-evidence: current
implementation claims are limited to the frozen commits, commands, artifacts,
and measurements recorded in Project Truth. No production integration exists.

## Truth labels

Every document should distinguish these states:

| Label | Meaning |
|---|---|
| **VERIFIED** | Directly observed in current repository code, a reproducible experiment, or a cited primary source. |
| **DECIDED** | Accepted project direction. It can change only through the decision log. |
| **PROPOSED** | Concrete design awaiting evidence or maintainer acceptance. |
| **HYPOTHESIS** | Testable technical belief, not yet demonstrated. |
| **UNKNOWN** | Important question without enough evidence. |
| **OUT OF SCOPE** | Deliberately excluded from the current milestone. |

## Recommended reading order

1. [Project Vision](PROJECT_VISION.md)
2. [Project Truth](PROJECT_TRUTH.md)
3. [Scope and Non-Goals](SCOPE_AND_NON_GOALS.md)
4. [Architecture](ARCHITECTURE.md)
5. [Theory and Assumptions](THEORY_AND_ASSUMPTIONS.md)
6. [Research Questions](RESEARCH_QUESTIONS.md)
7. [Engineering Roadmap](ENGINEERING_ROADMAP.md)
8. [Phase 0 Reference Oracle](PHASE_0_REFERENCE_ORACLE.md)
9. [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md)
10. [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml)
11. [Phase 1 Implementation](PHASE1_IMPLEMENTATION.md)
12. [Phase 2 GGUF Implementation](PHASE2_GGUF_IMPLEMENTATION.md)
13. [Phase 2 Freeze Hardening](PHASE2_FREEZE_HARDENING.md)
14. [Phase 3 Working-Set Specification](PHASE3_WORKING_SET_SPEC.md)
15. [Phase 3 Freeze Hardening](PHASE3_FREEZE_HARDENING.md)
16. [Phase 4 Bookend Virtualization](PHASE4_BOOKEND_VIRTUALIZATION.md)
    - [Phase 5A KV-Cache Spec](PHASE5A_KV_CACHE_SPEC.md), [Phase 5B Tokenizer Spec](PHASE5B_TOKENIZER_SPEC.md), [Phase 5C Activation Workspace Spec](PHASE5C_ACTIVATION_WORKSPACE_SPEC.md), [Phase 6 Quantization Spec](PHASE6_QUANTIZATION_SPEC.md)
17. Technical-design documents in pipeline order
18. Verification, risk, security, licensing, and review documents

## Document index

| Document | Authority | Purpose |
|---|---|---|
| [README](README.md) | Navigation | Index, terminology, reading order, and maintenance rules. |
| [Project Vision](PROJECT_VISION.md) | Intent | Why OrcEngine might exist and what strategic value would justify it. |
| [Project Truth](PROJECT_TRUTH.md) | Current state | Repository-verified facts, hypotheses, unknowns, and evidence dates. |
| [Architecture](ARCHITECTURE.md) | System design | Component boundaries and execution flow. |
| [Scope and Non-Goals](SCOPE_AND_NON_GOALS.md) | Scope | Initial limits and explicit exclusions. |
| [Research Questions](RESEARCH_QUESTIONS.md) | Research backlog | Questions that must be answered before design commitments. |
| [Theory and Assumptions](THEORY_AND_ASSUMPTIONS.md) | Scientific basis | Claims, expected evidence, and falsification conditions. |
| [Engineering Roadmap](ENGINEERING_ROADMAP.md) | Delivery plan | Evidence-gated phases and definitions of done. |
| [Phase 0 Reference Oracle](PHASE_0_REFERENCE_ORACLE.md) | Correctness foundation | Deterministic comparison methodology. |
| [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md) | Executable semantics | Exact synthetic model mathematics and pinned real-model candidate. |
| [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml) | Machine-readable gate | Required Phase 0 evidence; missing or skipped checks fail. |
| [Phase 1 Implementation](PHASE1_IMPLEMENTATION.md) | Frozen implementation evidence | Synthetic F32 core, differential, decode, residency, and hardening results. |
| [Phase 2 GGUF Implementation](PHASE2_GGUF_IMPLEMENTATION.md) | Frozen implementation evidence | Real GGUF ingestion, F32 execution, artifacts, and support boundary. |
| [Phase 2 Freeze Hardening](PHASE2_FREEZE_HARDENING.md) | Freeze evidence | Independent closure tests, matrix, re-attack, and limitations. |
| [Phase 3 Working-Set Specification](PHASE3_WORKING_SET_SPEC.md) | Proposed phase gate | Real-model layer-streaming mission, baseline, risks, metrics, and completion gates. |
| [Phase 3 Freeze Hardening](PHASE3_FREEZE_HARDENING.md) | Freeze evidence | Controlled budget, format-neutral source, observer, persistent timing, cross-freeze differential, and final matrix. |
| [Phase 4 Bookend Virtualization](PHASE4_BOOKEND_VIRTUALIZATION.md) | Candidate implementation evidence | Logical row regions, sparse embedding access, chunked output projection, real-model residency, adversarial tests, and reproduction commands. |
| [Phase 5A KV-Cache Spec](PHASE5A_KV_CACHE_SPEC.md) | Frozen implementation evidence | KV-cached incremental decode reference, virtualized cached path, freeze evidence. |
| [Phase 5B Tokenizer Spec](PHASE5B_TOKENIZER_SPEC.md) | Frozen implementation evidence | Tokenizer/text-token boundary, three-way oracle comparison. |
| [Phase 5C Activation Workspace Spec](PHASE5C_ACTIVATION_WORKSPACE_SPEC.md) | Frozen implementation evidence | Bounded activation workspace and workspace-driven cached decode. |
| [Phase 6 Quantization Spec](PHASE6_QUANTIZATION_SPEC.md) | In-progress phase spec | Q8_0 reference dequantization; internal tolerance gate currently FAILED; see DECISION_LOG OE-ADR-041 through OE-ADR-059. |
| [Model Format and GGUF](MODEL_FORMAT_AND_GGUF.md) | Model ingestion | File parsing, validation, tensor mapping, and compatibility posture. |
| [Tensor Engine Design](TENSOR_ENGINE_DESIGN.md) | Compute core | Tensor representation, operators, allocation, and graph execution. |
| [CPU Backend Design](CPU_BACKEND_DESIGN.md) | CPU execution | Reference kernels, threading, SIMD, and optimization gates. |
| [CUDA Backend Design](CUDA_BACKEND_DESIGN.md) | NVIDIA execution | Device memory, cuBLAS-first execution, kernels, and verification. |
| [Tokenizer and Prompt Pipeline](TOKENIZER_AND_PROMPT_PIPELINE.md) | Text boundary | Encoding, decoding, templates, special tokens, and byte fidelity. |
| [KV Cache and Context Design](KV_CACHE_AND_CONTEXT_DESIGN.md) | Inference state | Cache layout, positions, ownership, lifecycle, and role semantics. |
| [Sampling and Decoding](SAMPLING_AND_DECODING.md) | Token selection | Greedy baseline and later stochastic or constrained decoding. |
| [Quantization Plan](QUANTIZATION_PLAN.md) | Compressed weights | Correctness-first format sequence and kernel graduation. |
| [TheOrc Integration](THEORC_INTEGRATION.md) | Product boundary | Experimental backend integration without disrupting current runtimes. |
| [Test Strategy](TEST_STRATEGY.md) | Verification | Unit, differential, integration, numerical, and hardware testing. |
| [Benchmark Strategy](BENCHMARK_STRATEGY.md) | Performance evidence | Honest measurement and regression policy. |
| [Risk Register](RISK_REGISTER.md) | Risk | Ranked technical, product, legal, and maintenance risks. |
| [Security and Safety](SECURITY_AND_SAFETY.md) | Trust boundary | Untrusted model parsing, memory safety, resource controls, and reporting. |
| [Licensing and Attribution](LICENSING_AND_ATTRIBUTION.md) | Compliance | Project license posture and third-party evidence ledger. |
| [AI Agent Review Protocol](AI_AGENT_REVIEW_PROTOCOL.md) | Review governance | Repeatable multi-agent review process and evidence rules. |
| [Claude Review Prompt](CLAUDE_REVIEW_PROMPT.md) | Reviewer packet | Self-contained prompt for deep architecture review. |
| [Grok Review Prompt](GROK_REVIEW_PROMPT.md) | Reviewer packet | Self-contained adversarial research and feasibility review. |
| [Decision Log](DECISION_LOG.md) | Decisions | Append-only architecture decision history. |
| [Open Questions](OPEN_QUESTIONS.md) | Triage | Unresolved decisions requiring evidence or maintainer input. |
| [Glossary](GLOSSARY.md) | Vocabulary | Stable definitions for project terms. |
| [Current State](CURRENT_STATE.yaml) | Machine-readable status | Phase, evidence, blockers, and document inventory. |

## Pipeline map

```text
GGUF bytes
  -> validated metadata and tensor descriptors
  -> tokenizer + prompt template
  -> token IDs
  -> model graph
  -> tensor backend (CPU first; CUDA later)
  -> logits
  -> decoder / sampler
  -> token IDs and bytes
  -> TheOrc IModelRuntime adapter (only after standalone proof)
```

## Documentation maintenance rules

- [Project Truth](PROJECT_TRUTH.md) records what exists now; the roadmap records what may exist later.
- A benchmark claim must link to an artifact, command, hardware description, build identity, and raw output.
- A correctness claim must identify the oracle, inputs, tolerances, and comparison level.
- Architecture decisions are appended to [Decision Log](DECISION_LOG.md); do not silently rewrite history.
- New questions enter [Open Questions](OPEN_QUESTIONS.md) and graduate to [Research Questions](RESEARCH_QUESTIONS.md) when they need an experiment.
- AI reviews are evidence, not authority. Findings must be checked against current code or primary sources.
- External links are research references, not copied code or automatic license permission.
- If a status statement conflicts with live code, live code wins and the discrepancy is recorded.
- **Automated drift checks.** `Tools/check_orcengine_docs_truth.py` (CI: `.github/workflows/orcengine-docs-truth.yml`, on every PR touching `docs/OrcEngine` or `Tools/OrcEngine*`, on push, and weekly) verifies that freeze tags named in `CURRENT_STATE.yaml` exist and peel to the documented commits, referenced spec/link/file targets exist, ADR references are not dangling, the Project Truth snapshot matches `CURRENT_STATE.yaml`, known-obsolete claims have not resurfaced, and the state docs are not stale relative to the newest OrcEngine code commit. Run it locally with `python Tools/check_orcengine_docs_truth.py`; when you change engine state, update `CURRENT_STATE.yaml` in the same change. Append-only history (`DECISION_LOG.md`, dated `PROJECT_TRUTH.md` sections) is exempt from stale-phrase checks by design.

## Primary external references

- [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md)
- [llama.cpp project](https://github.com/ggml-org/llama.cpp)
- [llama.cpp model-development guide](https://github.com/ggml-org/llama.cpp/blob/master/docs/development/HOWTO-add-model.md)
- [LLamaSharp project](https://github.com/SciSharp/LLamaSharp)
- [CUDA Programming Guide](https://docs.nvidia.com/cuda/cuda-programming-guide/)
- [cuBLAS documentation](https://docs.nvidia.com/cuda/cublas/)
- [PyTorch numerical-accuracy note](https://docs.pytorch.org/docs/stable/notes/numerical_accuracy.html)
- [SentencePiece project](https://github.com/google/sentencepiece)
