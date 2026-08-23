# Phase 6: Initial Quantization (Q8_0)

Status: **SPECIFICATION -- decision-ready, not yet implemented.** This
document is committed alone, as a dedicated spec commit, per the
authorizing instruction: "write a decision-ready Phase 6 spec... do not
implement, stop after the spec is committed and consistent."

Branch: `feat/orcengine-phase6-quantization`, worktree
`F:\Ai\OrchestratorIDE-phase6-quantization`, forked from
`orcengine-phase5c-freeze` (peeled target
`a93e6c6e98a4b9b86f161a4d6695401a7980965e`).

Prepared: 2026-08-22, America/Los_Angeles.

## 1. Why now, and why Q8_0 first

`docs/OrcEngine/CURRENT_STATE.yaml` has read
`quantization: indexing_only_no_materialization_or_compute` since Phase
2: GGUF metadata parsing already recognizes every block-quantized
encoding (`GgufTensorEncoding` in `Tools/OrcEnginePhase2/include/
orcengine/gguf.hpp` -- F16, Q4_0, Q4_1, Q5_0, Q5_1, Q8_0, Q2_K through
Q8_K, each with its correct GGML block size and byte-per-block count
already implemented in `gguf.cpp`'s `block_layout_for_encoding`), and
`BackingEncoding` already tags each tensor's backing extent with its
matching quantized-format enumerator. But Phase 1's `materialize()`
(`Tools/OrcEnginePhase1/src/materialization.cpp`) throws
`ValidationError` on any backing encoding other than `F32Raw` -- no
dequantization or quantized-format compute path exists anywhere in the
codebase. Every real-model test run to date (Phase 2 through Phase 5C)
has used an F32 GGUF specifically because no other format can be
materialized at all.

**Q8_0 is the right first quantized format**, not an arbitrary pick:

- It is llama.cpp's / GGUF's simplest per-block scheme: 32 int8 values
  per block plus one F16 scale, no zero-point, no sub-block nesting
  (unlike the K-quants, which nest multiple precision levels per
  super-block). The dequantization arithmetic is `value = qi * scale`,
  nothing more -- the smallest possible surface area for a first
  correctness proof.
- It is widely distributed for the same SmolLM2-135M family already
  used as this project's canonical real-model fixture (same tokenizer,
  same architecture, same known-good token continuations already
  established in Phase 5A/5B/5C's real-model tests), so no new oracle
  infrastructure is needed -- the existing pinned llama.cpp `b10436`
  binary and the existing F32 real-model reference both already apply.
- Per `ENGINEERING_ROADMAP.md`'s Phase 6 renumbering note (OE-ADR-024):
  quantization has no technical dependency on the token/decode-path
  work (Phases 3-5C) -- it operates below the token boundary, purely on
  tensor materialization. The ordering that put decode-path work first
  was a risk-reduction choice, not a requirement, and remains true here:
  Phase 6 does not need anything from Phase 5C beyond a stable fork
  point.

## 2. Scope, exactly as authorized

In scope:

- Strict GGUF metadata/block-layout validation for Q8_0 specifically:
  confirm block size (32 elements), bytes-per-block (34: 2 bytes F16
  scale + 32 bytes int8 data), tensor byte-extent-matches-declared-
  dimensions arithmetic (already partially present in `gguf.cpp` for
  metadata purposes; Phase 6 must independently re-verify it for the
  materialization path, not silently trust Phase 2's own bookkeeping).
- Q8_0 dequantization: a new `materialize()`-reachable path that reads
  Q8_0-encoded backing bytes and produces the same F32
  `std::vector<float>` shape Phase 1's existing F32 path already
  produces, so every downstream operator (`ops::rmsnorm`, `linear_no_
  bias`, etc.) is unchanged and unaware a tensor was ever quantized.
- Correctness proof for dequantization BEFORE any optimized/fused
  compute: a straightforward, un-optimized reference dequantization
  (block-by-block, scalar loop) is the FIRST implementation and the
  thing validated against oracles. Only after that reference is proven
  correct does an optimized path (if any) get built and differentially
  compared against it -- never the other way around.
- Comparison against two independent authorities, both already
  established in this project and reused, not reinvented:
  1. **The existing F32 reference** (the same SmolLM2-135M GGUF already
     pinned, SHA-256
     `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac`,
     used throughout Phase 2-5C) -- run the identical forward/decode
     path Phase 1/5A already validate, once on the F32 weights and once
     on a Q8_0-quantized build of the SAME underlying model, and compare
     logits with a stated, justified tolerance (quantization is lossy
     by construction; the tolerance must be derived from Q8_0's known
     per-block quantization error bound, not picked arbitrarily).
  2. **The pinned external oracle** (`llama-tokenize.exe`'s sibling
     `llama.cpp` build, `b10436`, already version-pinned by SHA-256 in
     Phase 5B's `three_way_tokenizer_comparison.py` -- Phase 6 should
     use the matching `llama-cli`/`llama-eval`-equivalent binary from
     the SAME pinned build, not a different unpinned llama.cpp checkout)
     run directly against the Q8_0 GGUF, to confirm this project's own
     Q8_0 dequantization+forward path agrees with an independent,
     battle-tested implementation -- not just with itself.
- Honest memory-reduction measurement: report actual resident bytes for
  the F32 weights vs. actual resident bytes for the Q8_0 weights (via
  the existing `ResidencyLedger`, extended to account a quantized
  tensor's RESIDENT byte count as its quantized (compressed) size, not
  its dequantized-on-the-fly F32 footprint -- the two are different
  numbers and must not be conflated). No claimed compute-speed
  improvement is in scope this phase; Phase 6 is a correctness and
  memory-accounting milestone, not a performance one. If a dequantize-
  then-compute path happens to be measured, report it the same way
  Phase 5C's benchmark did -- isolated window, warm-up, repetitions,
  median+spread, no promised outcome -- but do not make memory-
  reduction claims depend on a performance number.

Explicitly out of scope, restated from the authorizing instruction:

- **CUDA / any GPU backend.** Q8_0 dequantization and compute this
  phase are CPU-only, matching every prior phase's CPU-only scope.
- **A generalized quantization framework.** No abstract "QuantFormat"
  interface, no plugin/registry system for arbitrary future formats, no
  speculative support for formats not being implemented this phase.
  Q8_0 gets a concrete, named implementation; nothing is built "to make
  the next format easier" beyond what Q8_0 itself needs.
- **Speculative formats.** Q4_0 and every other quantized encoding
  already recognized in `GgufTensorEncoding` (Q4_1, Q5_0, Q5_1, Q2_K
  through Q8_K) are explicitly DEFERRED, not implemented, not
  scaffolded. Q4_0 in particular is called out because it is the next
  most obvious candidate (it predates Q8_0's era and is still common in
  the wild) -- deferring it explicitly, rather than silently, avoids
  Phase 6 quietly growing into "quantization in general."
- **UI, TheOrc product integration, sticky-layer/VRAM-NVMe planning.**
  Same standing exclusions as every prior OrcEngine phase.
- **Any change to Phase 1-5C's frozen behavior.** Phase 6 may make the
  same kind of backward-compatible ADDITION Phase 5C's OE-ADR-039/040
  precedent established (extend `materialize()`'s dispatch to recognize
  a new backing encoding; add new functions; do not modify the meaning
  of any existing frozen signature for the F32 path). `orcengine-
  phase5c-freeze` and every earlier freeze tag remain untouched by this
  work.

## 3. What "correct" means for Q8_0, precisely

Three distinct claims, each requiring its own evidence, matching this
project's established pattern of never collapsing correctness into one
blended pass/fail:

1. **Metadata/layout correctness**: for a given Q8_0 tensor, the
   declared dimensions, the block count derived from those dimensions,
   and the backing byte extent are mutually consistent, checked
   independently of Phase 2's own metadata-parsing bookkeeping (a
   dedicated Phase 6 validation pass, not a re-export of Phase 2's
   already-computed numbers) -- and a deliberately malformed Q8_0 tensor
   (wrong byte extent for its declared dimensions, truncated block data)
   is rejected before any dequantization is attempted, fail-closed,
   matching Phase 2's own malformed-GGUF fixture-driven test discipline
   (`Tools/OrcEnginePhase2/tests/test_gguf_mutations.cpp`'s established
   pattern -- Phase 6 should add Q8_0-specific malformed fixtures
   alongside it, not invent a separate mechanism).
2. **Dequantization correctness**: for a KNOWN input (a small, hand-
   computable or independently-cross-checked set of Q8_0 blocks with
   known scale and quantized values), the dequantized F32 output matches
   the expected value exactly (Q8_0 dequantization is `qi * scale`, a
   deterministic, exactly-reproducible floating-point computation for a
   given input -- "exactly" here means bit-for-bit against a
   hand-computed or Python-reference-computed expectation, not a
   tolerance-bounded approximation, since there is no rounding
   ambiguity in the dequantization formula itself).
3. **End-to-end forward-pass correctness**: running the SAME model's
   Q8_0-quantized weights through the existing (unmodified, frozen)
   Phase 1/5A forward/cached-decode path produces logits within a
   justified tolerance of the F32 reference's logits for the same
   input, AND within a justified tolerance of the pinned llama.cpp
   oracle's own Q8_0 forward pass for the same input. The tolerance
   must be derived, not guessed: Q8_0's per-block quantization error is
   bounded by half the block's scale value (`scale/2`, since int8
   symmetric quantization rounds to the nearest representable level);
   propagate that bound through the same matmul/rmsnorm error-
   accumulation reasoning Phase 1's own differential-gate tolerances
   already use as precedent (`Tools/OrcEnginePhase1/tests/
   test_gates.cpp`'s existing tolerance-justification discipline), and
   state the derived number explicitly in the implementation evidence
   rather than reusing Phase 1/5A's F32-vs-F32 tolerances unmodified
   (those were calibrated for a fundamentally different, near-lossless
   comparison).

## 4. Implementation shape (for the eventual Stage 1 commit -- not built this pass)

Following the same "narrow, backward-compatible addition" pattern
Phase 5C's OE-ADR-039/040 established as reusable precedent:

- **Phase 2** (`gguf.cpp`): no changes expected -- Q8_0's block layout
  and `BackingEncoding::GgufQ8_0` tagging are already correct and
  already tested via existing metadata/mutation fixtures. Phase 6
  should ADD Q8_0-specific malformed-fixture cases (truncated block
  data, wrong byte extent) to the existing mutation-fixture generator
  rather than building a parallel validation mechanism.
- **Phase 1** (`materialization.cpp`): extend the `materialize()`
  dispatch to recognize `BackingEncoding::GgufQ8_0` as a second
  supported input encoding (alongside the existing, unmodified
  `F32Raw` path) and produce the same `std::vector<float>` output
  shape via a new, separately-named dequantization function (e.g.
  `dequantize_q8_0(...)`) -- not a modification to the existing F32
  materialization code path, which remains byte-identical and must be
  proven so by rerunning Phase 1's full existing test suite unchanged,
  matching the single-implementation-requirement discipline Phase 5C
  established (there, "single implementation" meant one arithmetic body
  shared by two call shapes; here, since F32 and Q8_0 materialization
  are genuinely different operations on different input encodings, the
  discipline is "the F32 path is untouched," not "shared arithmetic" --
  the two are not the same claim and Phase 6's evidence must not
  conflate them).
- **A new `Tools/OrcEnginePhase6` directory**, mirroring the structure
  every prior phase has used (`include/orcengine/`, `src/`, `tests/`,
  `tools/`), depending on Phase 1/Phase 2 the same way Phase 5A/5C
  already do, with its own `CMakeLists.txt`.
- **`ResidencyLedger` extension**: a quantized tensor's resident-byte
  accounting must report its actual quantized (compressed) resident
  size, distinct from any dequantized-scratch footprint incurred during
  a forward pass -- these are two different quantities and Phase 6's
  evidence must report both separately, never blended into one number,
  matching every prior phase's "never collapse distinct accounted
  quantities" discipline (weight vs. KV-cache vs., now, Phase 5C's
  activation-workspace bytes, vs. this phase's quantized-vs-dequantized
  distinction).

## 5. Validation matrix (for the eventual Stage 1 commit)

Debug/Release/strict (`/W4 /WX /permissive-`)/ASan, each individually
reported, on: the new Q8_0 malformed-fixture mutation tests, the
dequantization known-value correctness test, the end-to-end forward-
pass comparison test (both against the F32 reference and against the
pinned llama.cpp oracle), and every pre-existing Phase 1/2/5A/5C test
re-run unchanged to prove the F32 path was not disturbed. Independent
review (grok, per this project's standing "use grok for review when we
can" practice) before any freeze. A dedicated freeze commit and
`orcengine-phase6-freeze` tag only after that review's BLOCKER and
FIX-BEFORE-FREEZE findings are resolved -- following the exact
freeze-tag discipline every prior OrcEngine phase in this project has
used (annotated tag, verified peeled target, verified prior freeze tags
unchanged, push branch+tag only, no merge).

## 6. Open questions for the maintainer (not resolved by this spec)

1. Which GGUF source produces the Q8_0 build of SmolLM2-135M to use as
   the canonical fixture -- re-quantize the existing pinned F32 GGUF
   locally with a documented, reproducible tool invocation (preferred,
   keeps full provenance under this project's control), or download a
   pre-quantized artifact from a named, hash-pinned source (faster, but
   inherits someone else's quantization tool's exact rounding
   behavior, which then becomes part of what "correct" means for this
   fixture)? This spec does not decide it -- Stage 1 implementation
   should record whichever choice is made, with the same SHA-256-
   pinning discipline every other fixture in this project already uses.
2. Whether the end-to-end tolerance derivation in Section 3.3 should be
   validated empirically (run many random inputs, observe the actual
   error distribution, confirm it stays under the derived bound) before
   being trusted as a gate, or whether the analytical bound alone is
   sufficient evidence -- Phase 1's own differential-gate precedent used
   both; Stage 1 should state which approach it took and why.
