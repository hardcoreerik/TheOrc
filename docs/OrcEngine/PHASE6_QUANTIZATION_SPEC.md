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
     by construction, so a tolerance is expected -- but it must not be
     picked arbitrarily or derived from the analytical per-block bound
     alone: that bound is a documented SANITY envelope only; the actual
     end-to-end gate is established empirically, on a fixed development
     corpus and confirmed on a separate holdout corpus. See Section 3.3
     for the full corrected derivation and why the analytical-only
     approach is insufficient by itself.
  2. **The pinned external oracle** (`llama-tokenize.exe`'s sibling
     `llama.cpp` build, `b10436`, already version-pinned by SHA-256 in
     Phase 5B's `three_way_tokenizer_comparison.py` -- Phase 6 should
     use the matching `llama-cli`/`llama-eval`-equivalent binary from
     the SAME pinned build, not a different unpinned llama.cpp checkout)
     run directly against the Q8_0 GGUF, to confirm this project's own
     Q8_0 dequantization+forward path agrees with an independent,
     battle-tested implementation -- not just with itself.
- **Honest memory-reduction measurement, corrected 2026-08-22 after
  independent review (Codex) caught a self-contradiction in an earlier
  draft of this section.** The earlier draft proposed BOTH dequantizing
  Q8_0 fully into the existing F32 `ResidentView`/`std::vector<float>`
  (Section 4's implementation shape) AND reporting the Q8_0 tensor's
  RESIDENT byte count as its compressed size via `ResidencyLedger`.
  Those two claims cannot both be true: `ResidentView` owns exactly one
  `std::vector<float>` (`Tools/OrcEnginePhase1/include/orcengine/
  resident_view.hpp`) -- if a Q8_0 tensor is fully dequantized into
  that buffer, its ACTUAL resident bytes are the F32-sized buffer, full
  stop, regardless of what the ORIGINAL backing encoding was. Reporting
  the compressed size as "resident" would be reporting a number nothing
  in the actual running process reflects.
  **Corrected model, three genuinely distinct quantities, none allowed
  to stand in for another:**
  1. **Backing bytes** (the GGUF file's on-disk footprint for that
     tensor, and the bytes actually read from it during
     materialization) -- Q8_0's backing IS smaller than F32's for the
     same tensor (roughly 1/4, per Q8_0's 34-bytes-per-32-elements vs.
     F32's 128-bytes-per-32-elements), and this reduction is REAL and
     independent of what materialization does with those bytes
     afterward. This is the only reduction Stage 1, as scoped below
     (dequantize-to-F32 into the existing `ResidentView`), actually
     achieves -- and it should be reported honestly as exactly that: a
     smaller on-disk/backing footprint and less I/O during
     materialization, NOT a smaller running-process memory footprint.
  2. **Resident bytes** (what `ResidencyLedger` already tracks for the
     F32 path) -- for a dequantize-to-F32 Stage 1, a Q8_0 tensor's
     resident bytes after materialization are IDENTICAL to what an F32
     tensor of the same shape would occupy. `ResidencyLedger` must
     report this truthfully (no special-casing a quantized tensor's
     resident accounting to look smaller than the buffer that actually
     exists), and Stage 1's evidence must state plainly: **this Stage
     does not reduce resident weight memory**, only backing/storage
     footprint and file size. A genuine resident-memory reduction
     requires either (a) a true Q8-resident representation with a
     Q8×F32 (or Q8×Q8) compute path that never fully materializes an
     F32 copy, or (b) tightly-bounded on-demand block/layer
     dequantization with the transient F32 scratch honestly accounted
     as scratch, not as reduced residency. Both are larger-scope than
     Stage 1 and deferred to an explicitly authorized future Stage 2;
     WHICH of the two this project eventually pursues remains a
     genuinely open decision this spec does not make -- see Section 6
     item 3 for the resolved Stage 1/Stage 2 boundary itself.
  3. **Transient dequantization scratch** (if any -- Stage 1's
     block-by-block dequantization may or may not need scratch beyond
     the destination `std::vector<float>` itself; state explicitly
     whether it does, and if so, account it separately from both of the
     above).
  No claimed compute-speed improvement is in scope this phase; Phase 6
  is a correctness and honest-accounting milestone, not a performance
  one. If a dequantize-then-compute path happens to be measured, report
  it the same way Phase 5C's benchmark did -- isolated window, warm-up,
  repetitions, median+spread, no promised outcome.

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
   known STORED scale and quantized values), the dequantized F32 output
   matches the expected value exactly (Q8_0 dequantization is
   `qi * d_f16` -- the F16 scale as actually STORED in the block, not a
   recomputed F32 scale; see Section 3.3's correction for why this
   distinction matters -- a deterministic, exactly-reproducible
   floating-point computation for a given input -- "exactly" here means
   bit-for-bit against a
   hand-computed or Python-reference-computed expectation, not a
   tolerance-bounded approximation, since there is no rounding
   ambiguity in the dequantization formula itself).
3. **End-to-end forward-pass correctness**, tolerance derivation
   corrected 2026-08-22 after independent review (Codex) found the
   original `scale/2` premise incomplete for the pinned llama.cpp
   `b10436` Q8_0 implementation specifically: that build computes an
   F32 scale during quantization, quantizes the int8 values against
   that F32 scale, but then STORES the scale as F16 -- dequantization
   reads back the F16-rounded scale, not the original F32 one. A
   per-weight error envelope that ignores this second rounding step
   understates the true bound. The corrected envelope, for one
   dequantized weight `w` with true block scale `d_f32` and stored
   scale `d_f16`, quantized level `q`:

   ```
   |w - q*d_f16| <= d_f32/2 + |q|*|d_f32 - d_f16|
   ```

   the first term is the original int8-rounding bound, the second is
   the additional error from the scale itself being F16-rounded. For
   ONE matmul with fixed input activations `x`, the output error
   propagates linearly: `|Δy_j| <= Σ_i |x_i| * |Δw_ji|`. That bound is
   usable as a per-operation sanity check, but it does NOT extend
   cleanly to a whole-model logit bound: after the first transformer
   operation, the activations feeding every SUBSEQUENT layer are
   themselves already perturbed by upstream quantization error, and
   RMSNorm/SiLU/softmax/attention/residual paths are all nonlinear or
   accumulate across positions -- a fully analytical end-to-end bound
   through 30 layers of that is either intractable to derive honestly
   or so conservative it stops being a meaningful gate. Given that,
   Stage 1's tolerance strategy is:
   - Use the per-operation analytical envelope above as a documented
     SANITY bound (evidence that the dequantization+first-matmul error
     is in the right ballpark, not a mystery number), NOT as the
     end-to-end gate itself.
   - **Require empirical validation as the actual end-to-end gate**:
     run a fixed development corpus, measure the ACTUAL observed
     max-absolute-error, relative error, RMSE, and selected-token
     agreement between the Q8_0 forward pass and the F32 reference;
     derive the gate's tolerance from that observed distribution (with
     a documented safety margin), not from the analytical bound alone.
     Confirm the gate holds on a SEPARATE holdout corpus not used to
     derive it, so the tolerance isn't just curve-fit to the
     development set.
   - Report top-k logit stability (not just top-1/selected-token
     agreement) where feasible -- a model can select the right token
     while its broader logit distribution has already drifted more
     than top-1 agreement alone would reveal.
   - **Use a separately-justified, tighter tolerance for OrcEngine-Q8_0
     vs. llama.cpp-Q8_0 than for OrcEngine-Q8_0 vs. F32.** The F32
     comparison is inherently lossy (quantization vs. an unquantized
     reference); the llama.cpp comparison is two implementations
     applying the SAME lossy quantization scheme to the SAME weights,
     which should agree far more tightly if both are dequantizing
     correctly -- reusing the F32-vs-Q8 tolerance for this second
     comparison would hide a real divergence between the two
     implementations behind quantization noise's own margin.
   Exact dequantization correctness (claim 2 above, `qi * d_f16` for a
   known input) stays a SEPARATE, bit-exact claim -- it is never
   weakened by, or conflated with, this lossy end-to-end agreement
   claim.

## 4. Implementation shape

**Reconciliation note (added after Stage 1 implementation, per Codex
review):** this section originally proposed extending Phase 1's
`materialize()` (`Tools/OrcEnginePhase1/src/materialization.cpp`) and
claimed Phase 2's `gguf.cpp` would need "no changes." The actual Stage
1 implementation instead extends Phase 2's `gguf.cpp` --
`materialize_gguf_tensor()` and `materialize_gguf_tensor_rows()` --
directly. That is a correction of this section's original assumption,
not an undocumented deviation from it:

- Phase 1's `materialize(const LogicalTensor&, const BackingExtent&)`
  is a synthetic, in-memory-only function: it hard-requires
  `BackingEncoding::F32Raw` and reads from an already-in-RAM
  `BackingExtent::bytes_` buffer (see `BackingExtent::FromF32`). It is
  used by Phase 1's own metamorphic/regression tests to materialize
  hand-built tensors; it has never been the code path that reads a real
  GGUF FILE off disk.
- The actual GGUF-file-backed materialization boundary -- the one
  `materialize_gguf_model()` calls, and the one every real-model test
  since Phase 2 has exercised -- has always been Phase 2's
  `materialize_gguf_tensor()` in `gguf.cpp`. It already dispatches on
  `GgufTensorEncoding` (F32/F16 before Stage 1) and reads directly from
  the backing file via `tensor.backing.source_path()`/`byte_offset()`.
  Adding a Q8_0 branch to an existing encoding-dispatch function, in
  the file that already owns that dispatch, is the natural extension
  point -- not a new location chosen to make old prose true.
- This document's original claim that Phase 2 needed "no changes" was
  simply incorrect: it conflated Phase 1's synthetic materializer with
  Phase 2's real GGUF materializer, which are different functions with
  different contracts (in-memory-only vs. file-backed; F32Raw-only vs.
  multi-encoding). Corrected here rather than silently ignored.

Following the same "narrow, backward-compatible addition" pattern
Phase 5C's OE-ADR-039/040 established as reusable precedent, as
actually implemented:

- **Phase 2** (`gguf.cpp`, a FROZEN file under `orcengine-phase2-
  freeze`): `gguf_encoding_materializable()` extended to also accept
  `Q8_0`; a new `required_backing_bytes_for_encoding()` helper computes
  the correct block-based byte requirement for Q8_0 (F32/F16 unchanged,
  still `elements * bytes_per_element`); a new public
  `dequantize_q8_0_scalar_reference()` function; `materialize_gguf_
  tensor()` gains a Q8_0 branch. `materialize_gguf_tensor_rows()`
  explicitly rejects Q8_0 (see Section 4b). This is classified as a
  **backward-compatible ADDITION to a frozen file**, per the exact
  precedent OE-ADR-039/040 established for Phase 5C: new functions and
  a new branch in an existing dispatch function, with the existing
  F32/F16 call signatures, arguments, and return behavior of every
  pre-existing public function completely unchanged. The correct,
  defensible claim is **behavior/signature compatibility for F32/F16,
  supported by rerunning Phase 2's full pre-existing test suite
  unmodified** -- NOT that the frozen source file is "byte-identical"
  to its frozen state. The source text of `gguf.cpp` has changed (new
  code was added to it); claiming byte-identical source would be false
  on its face. What Phase 2's freeze tag (`orcengine-phase2-freeze`,
  unmoved) guarantees is the historical snapshot; what this addition
  guarantees is that nothing reachable through the pre-existing public
  surface behaves differently than it did at that snapshot.
- **Phase 1** (`materialization.cpp`): unchanged. Phase 1's synthetic
  `materialize()` remains F32Raw-only; Q8_0 support was never added
  there, because Phase 1's function is not the GGUF-file materialization
  boundary this project actually uses for real models (see the
  reconciliation note above).
- **A new `Tools/OrcEnginePhase6` directory**, mirroring the structure
  every prior phase has used (`include/orcengine/`, `src/`, `tests/`,
  `tools/`), depending on Phase 1/Phase 2 the same way Phase 5A/5C
  already do, with its own `CMakeLists.txt`.
- **`ResidencyLedger` extension, corrected to match Section 2's fixed
  memory model**: for Stage 1's dequantize-to-F32-into-`ResidentView`
  approach, `ResidencyLedger` must report a materialized Q8_0 tensor's
  RESIDENT bytes as its actual F32 buffer size -- the same as an F32
  tensor of that shape -- NOT its compressed backing size (the earlier
  draft's proposal to report compressed size as "resident" was the
  self-contradiction Section 2 now documents and corrects). What the
  ledger SHOULD newly report, honestly and separately, is the
  BACKING/materialization-read byte count for that tensor (real,
  smaller for Q8_0 than F32), clearly labeled as backing bytes, never
  presented as or blended with resident bytes. If Stage 1's
  dequantization needs any transient scratch beyond the destination
  buffer, that is a third, separately-reported quantity. This keeps the
  established "never collapse distinct accounted quantities" discipline
  intact (weight residency vs. KV-cache residency vs. Phase 5C's
  activation-workspace bytes vs., now, this phase's backing-vs-resident
  distinction) rather than introducing a new number that looks like
  residency but isn't.

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

## 6. Resolved implementation decisions and remaining Stage 2 deferrals

Originally drafted as open questions; items 1-3 below are now resolved
(hardening-consolidation pass, 2026-08-22) and recorded here for
traceability rather than removed, since Stage 1 implementation must
follow exactly what was decided, not re-derive it.

1. **RESOLVED (hardening-consolidation pass, 2026-08-22): fixture
   provenance.** The canonical Q8_0 build of SmolLM2-135M will be
   produced by locally quantizing the existing pinned F32 GGUF (SHA-256
   `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac`)
   using the exact pinned `llama.cpp` `b10436` build's `llama-quantize`
   binary -- not a downloaded pre-quantized artifact, and not a
   different/unpinned quantizer build. This keeps full provenance under
   this project's control and uses the exact same pinned implementation
   the three-way oracle comparison (Phase 5B) already relies on, rather
   than introducing a second, independently-sourced quantization
   implementation's rounding behavior as an unstated variable. Stage 1
   implementation must record: the input F32 GGUF's SHA-256 (already
   pinned above), the `llama-quantize` executable's SHA-256 and
   `--version` output (confirming build `10436`/commit `6fed9f6ff`,
   matching the pinning discipline `three_way_tokenizer_comparison.py`
   already established), the exact invocation (full command line,
   arguments, working directory), the resulting output GGUF's SHA-256,
   and a **tensor-by-tensor encoding inventory** -- because llama.cpp's
   own quantization tooling typically produces a MIXED-format model
   (commonly leaving embeddings, norms, and sometimes the LM head as F32
   or F16 while the transformer's matmul weights become Q8_0, not a
   uniformly all-Q8_0 file). Stage 1's fixture-provenance record and
   `ResidencyLedger` memory report must describe the tensor-by-tensor
   encoding actually present (a per-tensor manifest, not an assumption
   that every tensor is Q8_0), and `materialize()`'s dispatch must
   correctly route each tensor through its OWN actual encoding's path
   (F32 tensors through the existing unmodified path, Q8_0 tensors
   through the new one) rather than assuming a single encoding for the
   whole model.
2. **RESOLVED by Section 3.3's correction**, recorded here for
   traceability: the end-to-end tolerance is NOT derived from the
   analytical bound alone -- empirical validation (fixed development
   corpus, held-out corpus, observed error distribution) is now the
   required gate, with the analytical per-operation bound retained only
   as a documented sanity check, not the gate itself.
3. **RESOLVED (hardening-consolidation pass, 2026-08-22): resident-memory
   strategy.** Phase 6 Stage 1's scope is fixed as: Q8_0 layout
   validation, scalar (un-optimized, reference-first) dequantization
   into the existing F32 `ResidentView`, mixed-format-model loading
   (routing each tensor through its own actual encoding), exact
   dequantization evidence (claim 2, Section 3), and end-to-end oracle
   comparison against both the F32 reference and the pinned llama.cpp
   oracle (claim 3, Section 3). Stage 1 reduces BACKING/storage/
   materialization-read bytes only -- it does NOT claim, and must not be
   read as claiming, any resident-memory reduction; a Q8_0 tensor's
   actual resident footprint after Stage 1 materialization is identical
   to an F32 tensor of the same shape, and `ResidencyLedger` must report
   that truthfully. True Q8-resident compute (a Q8×F32 or Q8×Q8 compute
   path that never fully materializes an F32 copy) or bounded on-demand
   block/layer dequantization (transient F32 scratch honestly accounted
   as scratch, not as reduced residency) are explicitly DEFERRED to a
   separately, explicitly authorized Phase 6 Stage 2 -- not scaffolded,
   not speculatively designed, not implicitly promised by Stage 1's
   existence. Stage 1's evidence must state this limitation plainly
   rather than let a smaller GGUF file size be read as "the model now
   uses less memory to run."
