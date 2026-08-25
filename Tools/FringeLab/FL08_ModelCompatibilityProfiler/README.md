# FL-08 — Model Compatibility Profiler (research prototype)

Fringe Lab experiment FL-08. Full charter, prior-art review, schema
rationale, recognition-strategy design, extension map, and
operator-facing design are in
`.orc/fringelab/2026-08-25-fl08-model-compat-profiler/experiments/FL-08/`
(`PRIOR_ART.md`, `EXPERIMENT.md`, `DESIGN.md`).

Research-only. Independent of production OrcEngine code and the
`feat/orcengine-phase6-quantization` branch (re-implements the pinned
Q/K permutation formula locally rather than importing cross-worktree).
Never modifies an input artifact.

## Files

- `profiler.py` -- the profiler itself (CLI + library).
- `normalization_plan.py` -- Gate 6 normalization-plan generator
  (reads a profile JSON, proposes but never applies a plan).
- `make_tampered_fixture.py` -- regenerates the committed
  `fixtures/tampered_ambiguous.gguf` synthetic fixture (Case E).
- `test_profiler.py` -- the one focused test target (11 tests, all
  against small synthetic fixtures, no dependency on the real
  ~500MB Phase 6 artifacts).
- `fixtures_results/` -- output of running the 5 required experimental
  cases (A-E) against the real, hash-verified Phase 6 artifacts
  (referenced by absolute path/hash, never copied here).

## Usage

```
python profiler.py ARTIFACT.gguf [--reference REFERENCE.gguf] \
    [--target canonical-llama.cpp|orcengine-current] [--json OUT.json]

python normalization_plan.py PROFILE.json [--out PLAN.json]

python -m unittest test_profiler -v
```
