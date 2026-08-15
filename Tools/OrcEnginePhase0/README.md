# OrcEngine Phase 0 — Reference Oracle (work in progress)

This is the Phase 0 reference-oracle implementation for
[OrcEngine](../../docs/OrcEngine/README.md), following
[PHASE_0_REFERENCE_ORACLE.md](../../docs/OrcEngine/PHASE_0_REFERENCE_ORACLE.md),
[PHASE_0_ARCHITECTURE_PROFILE.md](../../docs/OrcEngine/PHASE_0_ARCHITECTURE_PROFILE.md),
and [PHASE_0_ACCEPTANCE.yaml](../../docs/OrcEngine/PHASE_0_ACCEPTANCE.yaml).

Per the project's own accepted decision (OE-ADR-001: "documentation and
deterministic oracle precede implementation"), this directory contains
**oracle/test code only** — no OrcEngine tensor-execution engine exists here
or anywhere else in the repository yet.

## Status against PHASE_0_ACCEPTANCE.yaml

| Check | Status |
|---|---|
| `synthetic_operator_microcases` | **Done** — see `oracle/microcases.py`, 10/10 passing |
| `artifact_schema_complete` | Comparison-record schema implemented (`oracle/comparison.py`); full artifact manifest writer not yet built |
| `three_way_oracle_independence` | Partial — hand-derived/scalar-reference leg only; second NumPy/PyTorch semantic oracle and pinned llama.cpp deployment oracle not started |
| `synthetic_layer_taps` | Not started — needs Fixture B (one-layer model forward pass with tap capture) |
| `cache_equivalence` | Not started — needs Fixture C (multi-layer + KV cache) |
| `near_tie_logits` | Not started |
| `deterministic_regeneration` | Not started — needs two independent clean-environment runs compared |
| `fault_injection` | Not started — needs the full Fixture B/C forward pass to inject faults into |
| `provenance_complete` | Not started — applies to the real-model candidate (SmolLM2-135M), not Profile A |
| `tokenizer_dual_source_agreement` | Not started (real-model candidate) |
| `raw_prompt_identity` | Not started (real-model candidate) |
| `real_candidate_conversion` | Not started (real-model candidate) |
| `real_candidate_logits` | Not started (real-model candidate) |
| `independent_reproduction` | Not started — needs an independent reviewer to run this README's commands from clean |

Everything under "not started" is real, scoped work — nothing here is meant
to imply Phase 0 is close to closing. This is the first rung of the fixture
ladder (Fixture A), not the full ladder.

## What's implemented

- `oracle/ops.py` — the exact Profile A (`OE-L0-SYNTH-1`) block-level math
  (RMSNorm, non-interleaved Llama RoPE, causal masking, softmax, SiLU,
  linear/matmul, embedding lookup), float32 throughout, as plain
  unoptimized NumPy so it reads directly against the spec.
- `oracle/microcases.py` — Fixture A: 10 hand-derived operator microcases.
  Expected values come from a **scalar** Python reference path in this same
  file (using the `math` stdlib, never calling `ops.py`), cross-checked by
  hand in the inline comments. This is the "independent ground truth" oracle
  class from `PHASE_0_REFERENCE_ORACLE.md` — it is deliberately not yet the
  primary semantic oracle (a second full NumPy/PyTorch implementation) or
  the secondary deployment oracle (pinned llama.cpp); both remain open.
- `oracle/comparison.py` — the comparison-record schema from
  `PHASE_0_REFERENCE_ORACLE.md` §"Comparison record": name, shape, strides,
  hashes, max abs/rel error + index, mean abs error, NaN/Inf counts, cosine
  similarity, pass/fail under a named tolerance profile. `compare_exact` for
  integer/byte/shape/token-ID comparisons, `compare_tolerant` for float
  comparisons.

## What's deliberately NOT here

- Fixture B (synthetic one-layer model with full tap capture)
- Fixture C (multi-layer + KV-cache equivalence test)
- Fixture D (pinned real model — SmolLM2-135M candidate)
- The artifact manifest writer (YAML schema from `PHASE_0_REFERENCE_ORACLE.md`)
- Fault-injection harness (transposed weights, off-by-one position, wrong
  RoPE pairing, missing mask, swapped K/V cache, changed epsilon, tokenizer
  special-token error)
- Any C++/CUDA engine code — that's explicitly Phase 1+, gated on Phase 0
  passing in full

## Reproducing

```bash
cd Tools/OrcEnginePhase0
python3 -m pip install -r requirements.txt   # numpy==2.5.2
python3 -m oracle.microcases
```

Expected output: `10/10 microcases passed`, exit code 0. Pinned environment:
CPython 3.14.3, numpy 2.5.2 (verified 2026-08-14 on this repository's
development machine). A different Python/NumPy build is expected to
reproduce these exact float32 results for these small values; if it
doesn't, that disagreement is itself Phase 0 evidence and should be
recorded per the tolerance-policy rules in `PHASE_0_REFERENCE_ORACLE.md`,
not silently tolerated.
