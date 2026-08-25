# FL-08 — Model Compatibility Profiler (research charter)

- **Worktree:** `F:\Ai\OrchestratorIDE-fringelab-fl08`
- **Branch:** `research/orcengine-fl08-model-compat-profiler`
- **Base:** `orcengine-phase5c-freeze` (`a93e6c6e98a4b9b86f161a4d6695401a7980965e`) -- the same
  frozen Phase 1-5C foundation Phase 6 itself builds on, but this
  branch does NOT depend on, branch from, or modify the
  `feat/orcengine-phase6-quantization` branch or any of its commits.
- **Independent of production paths and frozen Phase 0-5C source**:
  this experiment adds new files only, under `Tools/FringeLab/
  FL08_ModelCompatibilityProfiler/` and this `.orc/fringelab/` evidence
  directory. No frozen file is modified.

## Hypothesis

> OrcEngine can distinguish at least two artifacts with identical
> high-level architecture and tensor names but different tensor-layout
> semantics, explain the mismatch, and fail closed when evidence is
> insufficient.

## Artifacts used (referenced by hash/provenance, NOT committed)

All four are the exact, already-hash-verified artifacts from Phase 6
round 7 (Gates 2/3), referenced here by absolute path (outside this
worktree, on the same machine) and SHA-256 -- never copied into this
worktree, never committed:

| Role | Path (external to this worktree) | SHA-256 |
|---|---|---|
| existing-custom raw-HF F32 | `F:/Ai/OrchestratorIDE-phase2-gguf/Tools/OrcEnginePhase0/artifacts/smollm2-135m.gguf` | `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` |
| existing-custom raw-HF Q8_0 | `F:/Ai/OrchestratorIDE-phase6-quantization/Tools/OrcEnginePhase6/fixtures/smollm2-135m-q8_0.gguf` | `3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398` |
| canonical llama.cpp F32 | `F:/Ai/OrchestratorIDE-phase6-quantization/.orc/gate2-canonical-diagnostic/smollm2-135m-canonical-f32.gguf` | `aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7` |
| canonical llama.cpp Q8_0 | `F:/Ai/OrchestratorIDE-phase6-quantization/.orc/gate2-canonical-diagnostic/smollm2-135m-canonical-q8_0.gguf` | `dbf0d1f31d3afd0864bb02a916b7e3762728fd616eebd34bd6021c7497def219` |

Re-verified by this charter's own hashing pass immediately before
writing this table (not copied from the Phase 6 doc unverified).

**5th artifact -- deliberately ambiguous/tampered synthetic fixture**:
a small, purpose-built, COMMITTED synthetic GGUF
(`fixtures/tampered_ambiguous.gguf`, a few KB, NOT a real model) with
an internally CONTRADICTORY declaration -- claims `llama` architecture
and the correct tensor names/shapes for Q/K, but has a corrupted/
missing required-tensor set (see Gate 5 for exact construction) so no
confident classification is possible. Small enough to commit safely
(unlike the ~500MB-650MB real fixtures).

## Scope boundary

- Read-only against all 5 artifacts. Never writes, patches, or
  normalizes any input file.
- Does not call into, import, or link against `Tools/OrcEnginePhase6/*`
  or the `feat/orcengine-phase6-quantization` branch in any way --
  the Q/K permutation formula is re-implemented here from the same
  pinned llama.cpp source citation (independently re-derivable, not a
  cross-worktree dependency), consistent with "independent of
  production paths."
- No production OrcEngine loader code is touched.
- No Avalonia/UI work.
- No other model-family adapters (Gemma, Mistral, etc.) -- Llama/Q8_0
  only, per Gate 4's explicit narrow scope.

## Acceptance gate for this charter

Proceeds to Gate 5 (prototype) only because Gates 1-4 (prior art,
schema, recognition strategy) below establish a bounded, achievable,
non-duplicative scope. If Gates 1-4 had found the concept fully
prior-arted with no defensible combination, Gate 5 would not have been
attempted (see `PRIOR_ART.md` Gate 1 question 7 for the specific
defensible combination this charter targets).
