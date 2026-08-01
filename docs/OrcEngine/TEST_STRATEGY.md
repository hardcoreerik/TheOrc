# Test Strategy

## Test philosophy

OrcEngine tests prove numerical semantics, parser safety, resource ownership, and integration truth. A chat transcript is a demonstration, not a correctness test.

## Test pyramid

### Level 1: exact structural tests

- checked arithmetic and tensor extents;
- GGUF primitive decoding;
- metadata types and required keys;
- shapes, strides, tensor-name mapping;
- tokenizer bytes and IDs;
- allocation ledger and state transitions;
- error codes.

Expected results are exact.

### Level 2: operator tests

- hand-calculated tiny cases;
- property and randomized differential cases;
- edge cases and non-finite inputs;
- scalar versus optimized CPU;
- CPU versus CUDA later;
- quantized reference versus direct kernel.

### Level 3: layer tests

One decoder layer with fixed weights/input/cache. Compare named taps against Phase 0 artifacts.

### Level 4: model differential tests

Synthetic and pinned real model comparisons for tokenization, prompt evaluation, incremental decode, caches, logits, and greedy tokens.

### Level 5: lifecycle and integration tests

Repeated model/context creation, cancellation, failure injection, managed/native ownership, runtime selection, telemetry, and fallback policy.

### Level 6: benchmarks and long runs

Performance regressions, memory stability, long-context behavior, and repeated generation. These do not replace correctness levels.

## Required operator matrix

| Operator | Exact microcase | Random differential | Edge values | Full-model tap |
|---|---:|---:|---:|---:|
| Embedding lookup | Yes | Yes | invalid IDs | Yes |
| Matmul/matvec | Yes | Yes | zeros/extremes/tails | Yes |
| RMSNorm | Yes | Yes | zero/nearly zero/non-finite | Yes |
| RoPE | Yes | Yes | position 0/boundary/partial dim | Yes |
| Softmax/mask | Yes | Yes | one value/extremes/fully invalid | Yes |
| Attention | Yes | Yes | one token/grouped KV | Yes |
| SiLU/SwiGLU | Yes | Yes | extremes | Yes |
| Cache copy/read | Exact | Yes | first/last/overflow | Yes |
| Argmax | Exact | Yes | tie/non-finite | Yes |

## Differential artifact policy

Small reference artifacts may be versioned in the repository. Large tensor dumps stay in an approved artifact store and are addressed by SHA-256 manifests. Tests fail clearly when optional large artifacts are unavailable; they do not silently skip while reporting success.

## Test categories

- `unit`: no model file or accelerator;
- `oracle-small`: checked-in synthetic artifacts;
- `oracle-real`: opt-in pinned real model;
- `native-cpu`: compiled core on host;
- `native-cuda`: required named GPU/toolkit;
- `integration-dotnet`: native ABI and managed wrapper;
- `theorc-experimental`: opt-in product lane;
- `fuzz-sanitize`: parser/ABI stress;
- `benchmark`: non-gating unless a regression policy says otherwise.

## Determinism

Tests record environment and isolate nondeterminism. Greedy tests use no RNG. Stochastic tests pin algorithm and seed. Thread counts and math modes are explicit. Platform-specific tolerance profiles are named and reviewed.

Phase 0 gate reporting follows [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml): every required check must record `pass` or `fail`; missing, unknown, unavailable, and skipped checks fail the phase. A developer may run a partial local subset, but that run cannot be reported as a Phase 0 pass.

## Parser security tests

- truncation at each field;
- boundary counts and lengths;
- multiplication/addition overflow;
- malformed arrays/strings;
- unsupported version/type;
- descriptor outside file;
- overlap and duplicate identity;
- memory/time limits under generated malformed files;
- fuzz corpus plus crash reproducer retention.

## Lifecycle tests

- unload with active context is rejected or safely deferred;
- double destroy through C ABI is defined and safe at managed layer;
- cancellation before/within/after decode;
- model-load partial failure unwinds all allocations;
- CUDA asynchronous failure poisons context as designed;
- partial prompt-evaluation failure either rolls back every physical/logical cache write or poisons the context;
- repeated load/run/destroy does not grow memory beyond bounded caches;
- reset prevents stale token/cache bleed.

## Fault injection

Inject allocation failures, file-read failures, backend initialization errors, cancellation, invalid tensor descriptors, numerical non-finite values, cache commit failure, callback exceptions, and device loss where tooling permits.

## Test completion report

Every report includes exact command, commit, configuration, platform, skipped categories with reasons, pass/fail counts, duration, artifact hashes/paths, and whether the result was CI- or runtime-observed.

## Narrowest meaningful checks by phase

- Docs: inventory, links, YAML, `git diff --check`.
- Parser: parser unit/malformed/fuzz smoke.
- Operator: named operator differential.
- Model: synthetic oracle before real model.
- CPU optimization: affected operator plus full logits.
- CUDA: affected kernel plus CPU/CUDA model comparison.
- Integration: managed loop plus existing runtime targeted tests.

Full TheOrc test suites are not the first response to a standalone operator edit; run them when integration/shared infrastructure changes.
