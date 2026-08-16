# Phase 2 prep — malformed-GGUF conformance corpus

Test/fixture work only, per `ENGINEERING_ROADMAP.md`'s Phase 0 stop gate
("What you may NOT do: write the C++/CUDA engine itself before Phase 0's
14 checks all read `pass`") — no Phase 2 parser exists here or anywhere
else in the repository yet. This is the fixture corpus that parser will
need to prove itself against, built ahead of time so Phase 2 doesn't start
from zero.

## What's here

- `raw_gguf_writer.py` — a minimal GGUF binary-format writer built directly
  from the spec (not via the `gguf` PyPI package's `GGUFWriter`), because
  Phase 2 prep needs byte-level control to construct fixtures that a
  well-behaved writer won't let you produce. Verified against `gguf-py`'s
  independent `GGUFReader` before any corruption is applied.
- `generate_malformed_fixtures.py` — generates the "Malformed-input suite"
  from `docs/OrcEngine/MODEL_FORMAT_AND_GGUF.md`: bad magic, unsupported
  version, truncation at multiple structural boundaries, huge counts,
  integer overflow, invalid type, invalid UTF-8, duplicate key/name, zero
  dimension, unsupported dtype, misalignment, overlapping tensors, missing
  required metadata, inconsistent dimensions. Each fixture starts from one
  verified-valid baseline with exactly one corruption applied.

## Real finding, not just fixtures

Running the fixtures through `gguf-py` (an existing, independent, widely-used
parser — not the strict parser OrcEngine's own Phase 2 will build) shows it
silently **accepts** 6 of the 17 malformed fixtures: invalid UTF-8 in a
string value, a zero-length tensor dimension, a misaligned tensor offset,
overlapping tensor extents, missing required architecture metadata, and a
tensor dimension inconsistent with declared metadata. These 6 are exactly
where OrcEngine's own parser needs to be *stricter* than the current
ecosystem norm — this is the concrete, evidence-based version of
`MODEL_FORMAT_AND_GGUF.md`'s stated goal ("how OrcEngine can be better at
GGUF ingestion... not by inventing `OrcGGUF`").

## Reproducing

```bash
cd Tools/OrcEnginePhase0
python3 -m phase2_prep.generate_malformed_fixtures
```

Writes 18 `.gguf` files (1 valid baseline + 17 malformed) to
`artifacts/malformed_gguf_fixtures/` (gitignored — repo-wide `*.gguf` rule,
regenerate on demand, deterministic) and a
`CONFORMANCE_MANIFEST.json` (committed — small, human-readable, the actual
conformance test list Phase 2 needs) recording each fixture's category,
description, expected rejection stage/reason per
`MODEL_FORMAT_AND_GGUF.md`'s "Reader stages", and the real `gguf-py`
comparison verdict.
