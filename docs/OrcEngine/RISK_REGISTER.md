# Risk Register

## Scoring

Likelihood and impact use Low/Medium/High. Priority is a qualitative combination, reviewed at every phase boundary.

| ID | Risk | Likelihood | Impact | Priority | Mitigation / evidence gate |
|---|---|---:|---:|---:|---|
| R-001 | Project duplicates llama.cpp without unique value | High | High | Critical | Require a unique TheOrc capability before post-baseline investment. |
| R-002 | Fluent output hides numerical defects | High | High | Critical | Intermediate taps, logits comparison, cache equivalence, deliberate fault tests. |
| R-003 | GGUF parser accepts malicious offsets/counts | Medium | High | Critical | Checked arithmetic, strict limits, fuzzing, sanitizers, validate before mapping views. |
| R-004 | Scope expands to many architectures/backends | High | High | Critical | Compatibility tuple, evidence gates, separate decisions for every expansion. |
| R-005 | Oracle shares the same bug/assumption | Medium | High | High | Primary semantic and secondary deployment oracles plus synthetic hand cases. |
| R-006 | Model/tokenizer licensing is unclear | Medium | High | High | Provenance manifest and legal review before fixture distribution. |
| R-007 | C++ memory/lifetime defect causes corruption | Medium | High | High | Opaque ownership, sanitizers, failure injection, repeated lifecycle tests. |
| R-008 | CUDA async error corrupts reusable context | Medium | High | High | Explicit sync/error boundaries; poison/reset policy. |
| R-009 | Quantization degradation goes unnoticed | Medium | High | High | Block, layer, logits, token, and corpus-level comparisons. |
| R-010 | Existing runtime behavior regresses during integration | Low | High | High | Late opt-in adapter, narrow diff, targeted existing-runtime tests, rollback flag. |
| R-011 | Silent fallback invalidates evidence | Medium | High | High | Fail-closed research lane and persistent per-call runtime/fallback artifact fields. |
| R-012 | Documentation drifts from live code | High | Medium | High | Commit-pinned Project Truth and periodic link/code-anchor review. |
| R-013 | AI reviewers amplify incorrect claims | High | Medium | High | Require citations, code anchors, confidence, and maintainer verification. |
| R-014 | Benchmark comparisons are not equivalent | High | Medium | High | Mandatory configuration/hardware/hash metadata and raw artifacts. |
| R-015 | Performance work destroys debug reference path | Medium | High | High | Keep scalar/dequantized oracle path and differential tests. |
| R-016 | Thread oversubscription makes TheOrc unusable | Medium | Medium | Medium | Explicit thread controls and coexistence benchmark. |
| R-017 | Hardware support is inferred from compilation | Medium | Medium | Medium | Label build-only separately from runtime-observed. |
| R-018 | C ABI evolves prematurely and freezes mistakes | Medium | Medium | Medium | Design ABI only after standalone core proof; version capabilities/errors. |
| R-019 | Large oracle artifacts bloat repository | High | Medium | High | Commit small fixtures/manifests; external hashed artifact store for large dumps. |
| R-020 | Maintenance diverts resources from TheOrc | High | High | Critical | Phase stop gates and explicit maintainer funding decisions. |
| R-021 | Cache reuse crosses incompatible model/prompt/adapter state | Medium | High | Critical | No cross-role sharing by default; identity-rich cache keys and proof. |
| R-022 | Context overflow is silently shifted/truncated | Medium | High | High | Initial fail-closed capacity error; no implicit shifting. |
| R-023 | Native dependency/package mismatch | Medium | High | High | Exact artifact manifest, clean-machine resolution tests, ABI version check. |
| R-024 | Unsupported model is partially executed | Medium | High | High | Full compatibility validation before allocations/execution. |
| R-025 | Non-finite numerical state propagates silently | Medium | Medium | Medium | Verification-mode finite checks and structured failure tap. |

## Top risks requiring continuous review

### Strategic duplication

The project’s largest risk is not technical failure; it is succeeding at a costly duplicate that adds no product advantage. Phase 3 must include a continue/stop review independent of correctness pride.

### Parser attack surface

Model files are untrusted binary input. Even a local-first product may download community models. Parser limits and memory safety are release requirements, not polish.

### Truth drift

The current repository already contains status documents written at different times. OrcEngine must pin observations to code commits and state when evidence is older.

### Numerical confidence

Tolerance widening, final-token-only tests, and oracle coupling can manufacture confidence. Reviewers should challenge the earliest comparison point and fault-injection sensitivity.

## Risk review cadence

- update at each phase proposal and exit;
- add risks discovered by AI review only after verification;
- close a risk only with evidence and a decision-log entry;
- preserve closed risks for historical context;
- promote any parser, memory-safety, license, or fallback-integrity issue to a stop gate.
