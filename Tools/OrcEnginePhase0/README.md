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
| `artifact_schema_complete` | **Done** — `oracle/manifest.py` + `oracle/artifact_record.py` + `oracle/generate_manifest.py`. Real manifest generated from a live Fixture C run, written to `artifacts/fixture_c_manifest.yaml`, reloaded from disk, schema-validated: 8/8 top-level sections, 7/7 tensor artifacts each with all 7 required fields. |
| `three_way_oracle_independence` | Partial — hand-derived/scalar-reference leg only; second NumPy/PyTorch semantic oracle and pinned llama.cpp deployment oracle not started |
| `synthetic_layer_taps` | Partial — Fixture B forward pass built (`oracle/model.py`, `oracle/fixture_b.py`), all 19 required tap points captured, same-process deterministic. **Not yet a pass**: taps aren't cross-validated against an independent implementation — fault injection now covers 6/7 fault types (strong evidence, not full). |
| `cache_equivalence` | **Done** — `oracle/fixture_c.py`, full Profile A (n_layers=2). Full-prefix vs prefill+cached-decode last-position logits agree (max diff 2.4e-07), repeated with context reset, cross-run deterministic. |
| `near_tie_logits` | **Done** — `oracle/fixture_near_tie.py`. Near-tie (margin=0.000145) and exact-tie (bit-exact-equal logits) both constructed via tied-embedding-row perturbation; exact-tie decode rule (lowest token ID wins) verified directly. |
| `deterministic_regeneration` | **Done** — `oracle/deterministic_regeneration.py`, two separate `python3` subprocesses (genuinely fresh interpreters). Non-floating identities exactly equal; floating logits bit-exact (identical SHA-256, max_abs_diff=0.0). |
| `fault_injection` | Partial (6/7) — `oracle/fault_injection.py`: transposed projection matrix, off-by-one position, incorrect RoPE pairing, missing causal mask, changed RMSNorm epsilon, and swapped K/V cache write (via `oracle/model.py`'s `forward_cached`, once Fixture C existed) are all seeded and detected at their exact expected checkpoint. **Not a pass**: only `tokenizer_special_token_error` remains, honestly deferred — Profile A has no tokenizer; needs Fixture D's real tokenizer (SmolLM2-135M candidate). |
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

## Resolved: weight-init scale was masking faults (OE-ADR-015)

Fixture B originally used `WEIGHT_SCALE = 0.02`, which kept residual blocks
close to identity — greedy decode trivially recovered each input token as
its own argmax, and a measured test (transposed `w_o`) showed the fault
produced only a 0.07 max logit diff and did **not** flip argmax: invisible
to fault-injection comparison. Raised to `WEIGHT_SCALE = 0.1` (2026-08-14,
see `docs/OrcEngine/DECISION_LOG.md` OE-ADR-015) — the same fault now
produces a 1.21 max logit diff and reliably flips argmax. Fixture A and B
both still pass under the new scale.

## What's deliberately NOT here

- Fixture B (synthetic one-layer model with full tap capture)
- Fixture D (pinned real model — SmolLM2-135M candidate)
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
