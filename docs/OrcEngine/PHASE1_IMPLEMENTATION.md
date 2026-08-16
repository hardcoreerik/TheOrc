# Phase 1 Implementation — Tiny Synthetic F32 CPU Transformer

**Status as of 2026-08-15: implemented and passing.** This document is the
Phase-1-specific companion to [Engineering Roadmap](ENGINEERING_ROADMAP.md)'s
Phase 1 section, [Project Truth](PROJECT_TRUTH.md), and
[Current State](CURRENT_STATE.yaml).

Branch: `feat/orcengine-phase1`, worktree `F:/Ai/OrchestratorIDE-phase1`,
based on `feat/orcengine-phase0`'s tip (`d9045995`) — the frozen Phase-0
evidence branch is untouched. Code lives under `Tools/OrcEnginePhase1/`.

## What this is

A tiny, boring, deliberately unoptimized C++20 reference implementation of
one transformer forward pass, proven correct tap-by-tap against the trusted
Python oracle in `Tools/OrcEnginePhase0/oracle/`. F32 only. CPU only. No
CUDA, no quantization, no graph compiler, no production (TheOrc) integration.
This is the ruler, not the race car.

## What this is NOT

- Not a GGUF parser (Phase 2).
- Not connected to any real model (Phase 3).
- Not optimized in any way (Phase 4+) — scalar triple loops throughout,
  `double` accumulation for numerical safety, no SIMD/BLAS/fusion.
- Not CUDA (Phase 6A+).
- Not wired into TheOrc's runtime in any way.

## Directory layout

```
Tools/OrcEnginePhase1/
  CMakeLists.txt
  include/orcengine/
    tensor.hpp            TensorShape -- pure semantic shape, no storage
    logical_tensor.hpp     LogicalTensor -- identity (name + shape), no bytes
    backing_extent.hpp     BackingExtent -- source description (path/offset/encoding)
    resident_view.hpp      ResidentView -- the one place that owns float bytes
    model.hpp               ModelConfig, LayerWeights, ModelManifest, Model
    execution_plan.hpp      ExecutionPlan (Phase 1: always ResidentCPU)
    context.hpp              Context, ContiguousAttentionKVStore (unused by
                              the differential harness -- full-prefix only)
    ops.hpp                  scalar F32 operator declarations
    forward.hpp               ForwardResult, Tap, forward()
    fixture_loader.hpp        flat-text fixture loader
    diagnostics.hpp           opt-in NaN/Inf/min/max/mean tap tracing
  src/
    ops.cpp forward.cpp fixture_loader.cpp
  tests/
    test_gates.cpp             differential harness + main()
```

## Why these specific contract types, even though Phase 1 barely uses them

`TensorShape` / `LogicalTensor` / `BackingExtent` / `ResidentView` are kept
as four distinct types — not collapsed into one buffer class — even though
Phase 1's implementations are trivial (`ResidentView` is the only one that
owns memory; `BackingExtent` always points at the flat-text fixture;
`ExecutionPlan` always resolves to `ResidentCPU`). This is a direct,
literal application of `docs/OrcEngine/ARCHITECTURE.md`'s "Memory model"
section and the explicit Phase-1 instruction: *"do not make LogicalTensor
itself synonymous with a malloc'd pointer."* The point is that Phase 6B
(paged/streamed residency) becomes additive to these types, not a rewrite
of every caller.

## Why no JSON library

The fixture format is fully self-controlled — this repo writes it
(`Tools/OrcEnginePhase0/oracle/export_cpp_phase1_fixture.py`) and this repo
reads it (`src/fixture_loader.cpp`). Pulling in `nlohmann/json` or any other
external dependency for a format only two files in the same repo ever touch
buys nothing and adds a build-time network/vendoring dependency for no
correctness benefit. The format is whitespace-delimited tokens
(`CONFIG`/`TIED`/`SEQ`/`TOKENS`/`TENSOR`/`LAYER`/`EXPECT`/`EXPECT_INT`
markers, each followed by shape dims then flattened row-major float32
values), parsed with plain `std::ifstream operator>>`. Total external
dependency count for the whole Phase-1 engine: **zero**, beyond the C++
standard library.

## The tiny model

Reuses Phase 0's own **Fixture C** dimensions exactly
(`Tools/OrcEnginePhase0/oracle/fixture_c.py`'s `ModelConfig` defaults) rather
than inventing a new mathematical target:

```
vocab=32  hidden=16  intermediate=32  n_layers=2
n_q_heads=4  n_kv_heads=2  head_dim=4  max_positions=16
rmsnorm_epsilon=1e-5  rope_theta=10000.0
```

Two fixtures are exported (`fixtures_phase1/fixture_tied.txt`,
`fixtures_phase1/fixture_untied.txt`), both required by the Phase-1
completion gate: a tied-embeddings model (no separate `lm_head`) and an
untied model (independent, randomly-seeded `lm_head` matrix, same shape as
`token_embedding`) — proving the untied-output-head bug this session found
and fixed in the Python oracle (`OE-ADR-019`) has an equally-correct C++
counterpart from day one, not bolted on later.

## Operators implemented

All in `ops.cpp`, matching `Tools/OrcEnginePhase0/oracle/ops.py` line-for-line
so a reviewer can check them side by side without a mental translation step:
`rmsnorm`, `silu`, `softmax_last_axis`, `causal_mask`, `rope_cos_sin` /
`rope_rotate_half` / `apply_rope` (non-interleaved, split-half Llama RoPE,
full rotation only — no partial rotary factor in Phase 1), `linear_no_bias`
(`x @ W^T` for `[out, in]`-stored weights), `embedding_lookup`, `argmax`.
Grouped-query attention head mapping (`kv_h = h / (n_q_heads/n_kv_heads)`)
and the tied/untied `effective_lm_head()` resolver live in `forward.cpp`.

Reductions (`rmsnorm`'s mean-of-squares, `linear_no_bias`'s dot products,
attention's score/context accumulation) use `double` internally even though
inputs and outputs are `float` — a deliberate safety margin against
reduction-order divergence from the Python oracle's own accumulation, not a
production performance choice (see Phase 4 for where BLAS-backed GEMM with
its own accumulation semantics gets evaluated against tolerance, not assumed
safe).

## Differential test harness

`tests/test_gates.cpp` loads each fixture, runs `forward()`, and compares
**every intermediate tap** the Python oracle captured — not just final
logits — against the C++ result: `input_embedding`, and per layer
`pre_attention_normalized_state`, `q_projection`, `k_projection`,
`v_projection`, `q_after_rope`, `k_after_rope`, `attention_probabilities`,
`attention_output_before_projection`, `attention_output_after_projection`,
`post_attention_residual`, `pre_ffn_normalized_state`, `gate_projection`,
`up_projection`, `activated_gated_product`, `down_projection`,
`post_ffn_residual`, then `final_normalized_state`, `logits`, and
`selected_token` (exact integer match, no tolerance). On any divergence it
reports `max_abs_error`, `max_rel_error`, and `first_bad_index` with the
actual/expected values at that index — not just pass/fail.

**Verified to actually detect faults, not just pass trivially**: a
deliberate one-value corruption injected into `token_embedding` (used as
the tied `lm_head`) was correctly caught — every tap up through
`final_normalized_state` still passed (correctly, since the corrupted
vocabulary row wasn't among the input tokens), while `logits` and
`selected_token` failed with the exact expected divergence, localized
precisely to the output projection as the algebra predicts.

Acceptance thresholds: `abs_error > 1e-3 AND rel_error > 1e-3` (both must
fail for a tap to be flagged — this tolerates a single-ULP-scale float32
rounding difference without tolerating an order-of-magnitude divergence).

## NaN/Inf and observability

Every captured tap is checked for NaN/Inf inline (`diagnostics.hpp`,
`forward.cpp`'s `put_tap`); if either is found, `forward()` throws rather
than silently propagating poisoned data (fail closed, per the steering
document's explicit requirement). Verbose per-tap min/max/mean/NaN/Inf
tracing is opt-in via the `ORCENGINE_DEBUG_TAPS=1` environment variable —
zero cost when unset beyond one static bool check per tap.

## Build and test

```bash
cd Tools/OrcEnginePhase1
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Debug
./build/Debug/test_gates.exe ../OrcEnginePhase0/fixtures_phase1
```

To regenerate the fixtures from the Python oracle (only needed if
`oracle/export_cpp_phase1_fixture.py` or the oracle itself changes):

```bash
cd Tools/OrcEnginePhase0
python oracle/export_cpp_phase1_fixture.py
```

## Results (2026-08-15, first green run)

Both fixtures, all taps, all pass. Every divergence measured is at float32
machine-epsilon scale (`~1e-7` to `~5e-7`), roughly 4 orders of magnitude
inside the `1e-3` acceptance threshold — consistent with `double`-accumulated
C++ arithmetic being *more* precise than NumPy's own float32 reductions, not
less. Greedy argmax matches exactly (integer equality, no tolerance) on all
4 positions in both the tied and untied fixture.

| Fixture | Taps compared | Result | Max abs error observed | Max rel error observed |
|---|---|---|---|---|
| tied | 34 intermediate taps + logits + selected_token | ALL PASS | 5.36e-7 | 5.36e-7 |
| untied | 34 intermediate taps + logits + selected_token | ALL PASS | 5.36e-7 | 5.21e-8 |

Argmax agreement: 4/4 positions, both fixtures, exact.

## Known limitations / explicitly deferred

- Full-prefix forward pass only — no incremental/cached decode
  (`ContiguousAttentionKVStore` exists as a contract type but is not
  exercised by any test yet; that's Phase 3's job).
- No partial rotary factor (`rotary_dim < head_dim`) support in
  `apply_rope` — Fixture C uses full rotation, so this was never needed for
  Phase-1 gate-passing; the Python oracle already supports it for later reuse.
- No GGUF, no quantization, no CUDA, no batching, no multi-sequence context.
- `BackingExtent` is defined but not yet populated per-tensor by the fixture
  loader (it always reads eagerly from one path) — the type exists for
  Phase 2's GGUF-backed extents, not exercised here.
- Per-operator timing instrumentation was not added — deferred as
  explicitly lower priority than correctness per the steering document
  ("do not optimize a wrong engine").

## Stop gate

Per the steering document's explicit instruction, Phase 1 stops here
pending deliberate maintainer review. No Phase 2 (GGUF parser expansion,
real model loading, quantization, CUDA) work has started.
