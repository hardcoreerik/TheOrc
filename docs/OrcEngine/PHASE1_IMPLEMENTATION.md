# Phase 1 Implementation — Tiny Synthetic F32 CPU Transformer

**Status as of 2026-08-16: implemented, hardened, and pending final freeze review.** This document is the
Phase-1-specific companion to [Engineering Roadmap](ENGINEERING_ROADMAP.md)'s
Phase 1 section, [Project Truth](PROJECT_TRUTH.md), and
[Current State](CURRENT_STATE.yaml).

Branch: `feat/orcengine-phase1`, worktree `F:/Ai/OrchestratorIDE-phase1`,
based on `feat/orcengine-phase0`'s tip (`d9045995`) — the frozen Phase-0
evidence branch is untouched. Code lives under `Tools/OrcEnginePhase1/`.

## What this is

A tiny, boring, deliberately unoptimized C++20 reference implementation of
one transformer forward pass, proven correct tap-by-tap against the trusted
Python oracle in `Tools/OrcEnginePhase0/oracle/`. F32 is the deliberate
default; an F64-accumulation comparison target is retained. CPU only. No
CUDA, no quantization, no graph compiler, no production (TheOrc) integration.
This is the ruler, not the race car.

## What this is NOT

- Not a GGUF parser (Phase 2).
- Not connected to any real model (Phase 3).
- Not optimized in any way (Phase 4+) — scalar triple loops throughout,
  F32 accumulation by default, no SIMD/BLAS/fusion.
- Not CUDA (Phase 6A+).
- Not wired into TheOrc's runtime in any way.

## Directory layout

```
Tools/OrcEnginePhase1/
  CMakeLists.txt
  include/orcengine/
    tensor.hpp            TensorShape -- pure semantic shape, no storage
    logical_tensor.hpp     LogicalTensor -- identity (name + shape), no bytes
    backing_extent.hpp     BackingExtent -- source description or owned F32 bytes
    materialization.hpp    LogicalTensor + BackingExtent -> fresh ResidentView
    validation.hpp         model/config/tensor/expectation validation
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
    ops.cpp forward.cpp fixture_loader.cpp validation.cpp materialization.cpp
  tests/
    test_gates.cpp             differential harness + fixture completeness
    test_decode.cpp            enforced 8-step autoregressive comparison
    test_metamorphic.cpp       backing/residency/tied metamorphisms
    test_regressions.cpp       malformed/missing-evidence regressions
```

## Why these specific contract types, even though Phase 1 barely uses them

`TensorShape` / `LogicalTensor` / `BackingExtent` / `ResidentView` are kept
as four distinct types — not collapsed into one buffer class. The ordinary
fixture loader remains eager, while the metamorphic harness creates owned
F32Raw `BackingExtent` bytes and calls `materialize(LogicalTensor,
BackingExtent)` to produce fresh `ResidentView` allocations.
`ExecutionPlan` still always resolves to `ResidentCPU`. This is a direct,
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

## F32 vs F64 accumulation — deliberate decision (2026-08-15 freeze audit)

Phase 1's spec is storage=F32, compute=F32, accumulator=F32 unless evidence
justifies otherwise. The first implementation used `double` accumulation by
default without running that comparison — an accidental deviation, not a
deliberate one. Corrected by making the accumulator type configurable
(`ops::AccumT`, `include/orcengine/ops.hpp`) and building two CMake targets:
`orcengine_phase1` (F32 accumulation, the default) and
`orcengine_phase1_f64accum` (F64 accumulation, comparison-only).

Both were run against the same tied/untied fixtures:

| Accumulator | tied `final_normalized_state` max_abs_err | tied `logits` max_abs_err | untied `logits` max_abs_err | argmax agreement |
|---|---|---|---|---|
| F32 | 7.15e-7 | 3.50e-7 | 5.22e-8 | exact, both fixtures |
| F64 | 4.17e-7 | 2.38e-7 | 5.22e-8 | exact, both fixtures |

F64 is marginally more precise, as expected, but the difference (≈2-3x at
this tensor size) is itself roughly five orders of magnitude smaller than
the `1e-3` acceptance threshold in both directions — **F64 is not materially
necessary here**. Per the stated preference (a clean F32 reference is what
later CUDA/quantized comparisons actually need), **F32 accumulation is the
selected, deliberate default** for `orcengine_phase1`. The F64 variant is
kept only as a standing comparison target — `-DORCENGINE_ACCUM_F64` — not as
an alternative shipped configuration, in case a future, larger model
surfaces a reduction-length regime where the gap stops being negligible.

## Activation representation vs ResidentView

`ForwardResult`'s intermediate values use a dedicated `ActivationBuffer`
type (`forward.hpp`), not `ResidentView`. This was already true in the
original implementation — `ResidentView` is used exclusively for durable
model weights loaded via `fixture_loader.cpp` — but the freeze audit's
concern was valid as a naming/documentation gap: nothing previously stated
the distinction explicitly, so `ActivationBuffer` now carries a doc comment
spelling out why it is not a `ResidentView`: an activation has no
`LogicalTensor` identity, no `BackingExtent`, and no lifetime past one
`forward()` call, whereas `ResidentView` specifically means "the currently
materialized copy of a `LogicalTensor` backed by durable storage."

## Metamorphic tests: physical storage vs logical identity

`tests/test_metamorphic.cpp` proves three deliberately narrow Phase-1
properties about in-memory F32 backing and residency. Three properties,
compared bit-for-bit (`std::memcmp`, not a tolerance — these are the exact
same floating-point operations on the exact same values, so anything but
exact equality would itself be a bug):

1. **Backing materialization**: every weight tensor is represented by a
   `LogicalTensor` plus an owned F32Raw `BackingExtent`, materialized into a
   fresh `ResidentView`, and compared against the fixture-loaded resident.
   Every shape/value matches and logits are bit-identical.
2. **Tied-alias vs tied-duplicate**: a true tied model (`effective_lm_head()`
   aliasing `token_embedding`) is compared against a model whose `lm_head`
   is a physically separate (confirmed different address) but byte-identical
   copy of the same values — logits are bit-identical. This directly shows
   tied inference semantics does not require pointer identity.
3. **Rematerialize from retained backing**: all model tensors are materialized
   twice from the same retained `BackingExtent` set while both resident
   generations coexist, forcing distinct addresses. The first generation is
   then destroyed; the second produces bit-identical logits.

All three pass on both F32 and F64-accumulation variants. This proves the
in-memory F32 path above; it does not claim GGUF, mapped-file, paging, disk,
or CUDA backing support.

Reductions (`rmsnorm`'s mean-of-squares, `linear_no_bias`'s dot products,
attention's score/context accumulation, `softmax`'s sum) use the configurable
`ops::AccumT` accumulator (F32 by default, see above) — inputs and outputs
are always `float`, matching the Python oracle's own `dtype=DTYPE`
(float32) reductions in `oracle/ops.py`.

## Differential test harness

`tests/test_gates.cpp` loads both fixtures and, before execution, requires the
structural Phase-1 expectation set: 36 records per fixture, 72 total. Missing,
extra, incorrectly-shaped, or non-finite expectations fail before `forward()`.
It then compares every required intermediate tap — not just final logits —
against the C++ result: `input_embedding`, and per layer
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

## Autoregressive decode proof (2026-08-15 freeze audit)

The original 72-comparison differential harness proved every intermediate
tap and one forward pass's argmax match the oracle — it did NOT prove
multi-step generation, where C++ and Python could in principle diverge on
step 2 even if step 1's logits matched. `tests/test_decode.cpp` closes that
gap directly: both sides run **full-recompute greedy decode** (no KV cache
in either implementation — matching Phase 1's actual scope), each making
its own independent argmax choice at every step, from the same initial
tokens `[1, 5]`. `oracle/export_cpp_phase1_decode_fixture.py`'s Python side
never sees what C++ will choose; it decides its 8 tokens on its own, and the
C++ side is not fed those choices either — it decides its own 8 tokens
independently and the test only checks agreement after the fact.

The trace validator requires `STEPS > 0`, exact entry count, sequential step
indices, vocabulary-sized finite logits, and sequence growth consistent with
each prior selected token. Each step gates both exact token identity and
`max_abs_error <= 1e-3` plus `max_rel_error <= 1e-3`; printed errors are not
informational-only.

Result: **all 8 steps matched token-for-token**, both making the identical
sequence of choices: `[1, 5, 5, 5, 29, 29, 29, 29, 29, 29]`. Per-step max
logit abs error stayed at float32 machine-epsilon scale throughout
(1.19e-7 to 1.79e-7). The generated sequence collapsing into repeats is
expected and unconcerning — this is a randomly-initialized synthetic model
with no learned language structure, so greedy decode has no reason to avoid
repetition; the test's subject is decode-loop correctness, not output
quality.

## NaN/Inf and observability

Every captured tap is checked for NaN/Inf inline (`diagnostics.hpp`,
`forward.cpp`'s `put_tap`); if either is found, `forward()` throws rather
than silently propagating poisoned data (fail closed, per the steering
document's explicit requirement). Verbose per-tap min/max/mean/NaN/Inf
Golden expectations and decode traces are also rejected if any numeric value
is non-finite, before delta calculation. Tracing is opt-in via the
`ORCENGINE_DEBUG_TAPS=1` environment variable —
zero cost when unset beyond one static bool check per tap.

## Build and test

```bash
cd Tools/OrcEnginePhase1
cmake -S . -B build -G "Visual Studio 17 2022" -A x64
cmake --build build --config Debug
ctest --test-dir build -C Debug --output-on-failure
```

CTest registers seven cases: F32 and F64 variants of gate, decode, and
metamorphic tests, plus one accumulation-independent hardening regression
suite. Or run binaries directly:

```bash
./build/Debug/test_gates.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_decode.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_metamorphic.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_gates_f64accum.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_decode_f64accum.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_metamorphic_f64accum.exe ../OrcEnginePhase0/fixtures_phase1
./build/Debug/test_regressions.exe ../OrcEnginePhase0/fixtures_phase1
```

To regenerate the fixtures from the Python oracle (only needed if the
export scripts or the oracle itself changes):

```bash
cd Tools/OrcEnginePhase0
python oracle/export_cpp_phase1_fixture.py
python oracle/export_cpp_phase1_decode_fixture.py
```

## Results (2026-08-16 hardening run)

Both fixtures, all taps, all pass. Every divergence measured is at float32
machine-epsilon scale (`~1e-7` to `~7e-7`), roughly 4 orders of magnitude
inside the `1e-3` acceptance threshold. Greedy argmax matches exactly
(integer equality, no tolerance) on all 4 positions in both the tied and
untied fixture, AND across all 8 autoregressive decode steps (see above).

| Test | Result | Max abs error observed | Notes |
|---|---|---|---|
| `test_gates` (tied, F32 accum) | ALL PASS | 3.50e-7 (logits) | 34 taps + logits + selected_token |
| `test_gates` (untied, F32 accum) | ALL PASS | 5.22e-8 (logits) | 34 taps + logits + selected_token |
| `test_gates` (tied, F64 accum) | ALL PASS | 2.38e-7 (logits) | comparison build only |
| `test_decode` (F32 accum) | ALL PASS | 1.79e-7 (per-step logits) | 8/8 autoregressive steps, token-for-token |
| `test_metamorphic` (F32 accum) | ALL PASS | 0 (bit-identical) | BackingExtent materialization, tied alias-vs-duplicate, repeated rematerialization |
| `test_regressions` | ALL PASS | n/a | 18 malformed/missing-evidence/arithmetic/backing regressions rejected |

Argmax agreement: 4/4 single-pass positions (both fixtures) + 8/8
autoregressive decode steps, all exact.

## Known limitations / explicitly deferred

- Full-prefix recompute every decode step, no KV cache — proven correct
  across 8 autoregressive steps above, but each step re-runs the whole
  forward pass rather than reusing prior K/V (`ContiguousAttentionKVStore`
  exists as a contract type but is not exercised by any test yet; cached
  decode equivalence is Phase 3's job, matching the Python oracle's own
  scope split).
- No partial rotary factor (`rotary_dim < head_dim`) support in
  `apply_rope` — Fixture C uses full rotation, so this was never needed for
  Phase-1 gate-passing; the Python oracle already supports it for later reuse.
- No GGUF, no quantization, no CUDA, no batching, no multi-sequence context.
- The flat-text fixture loader still reads eagerly and does not retain
  per-tensor extents. Phase 1's real `BackingExtent` path is intentionally
  limited to owned in-memory F32Raw bytes used by the metamorphic tests; GGUF
  file ranges and other storage tiers remain Phase 2+ work.
- Per-operator timing instrumentation was not added — deferred as
  explicitly lower priority than correctness per the steering document
  ("do not optimize a wrong engine").
- The autoregressive decode test uses a fixed 8-step budget and a fixed
  2-token seed — sufficient to prove the decode loop is deterministic and
  agrees with the oracle, not an exhaustive search over seeds/lengths.

## Stop gate

Per the steering document's explicit instruction, Phase 1 stops here
pending deliberate maintainer review. No Phase 2 (GGUF parser expansion,
real model loading, quantization, CUDA) work has started.
