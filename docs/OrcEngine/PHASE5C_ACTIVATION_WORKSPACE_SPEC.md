# Phase 5C: Bounded Activation Workspace and Prompt/Decode Benchmarking

Status: **AUTHORIZED FOR IMPLEMENTATION (2026-08-22) -- the maintainer selected option (a) from Section 7 below: a narrow, backward-compatible output-buffer/workspace seam may be added to inherited Phase 1/5A files. The seam investigation in Sections 3-5 remains below EXACTLY as originally written, as historical evidence of what was actually checked before this decision -- it is not rewritten as though the gap never existed. See Section 8 for the resulting rule and Section 9 for the Stage 1 implementation record.**

Branch: `feat/orcengine-phase5c-activation-workspace`, worktree
`F:\Ai\OrchestratorIDE-phase5c-activation-workspace`, forked from
`orcengine-phase5b-freeze` (peeled target `8a36f375110f8002804917809e3a773b25891e1f`).

Prepared: 2026-08-22, America/Los_Angeles.

## 1. Scope, exactly as authorized

Phase 5C is limited to:

- Explicit transient activation/scratch-memory accounting.
- Separation of activation workspace, weight residency, and KV-cache
  residency as three distinct, separately-accounted quantities.
- Reuse of bounded CPU F32 scratch buffers across repeated decode steps.
- Proof that reuse does not change numerical results (bit-exact against
  the frozen, unmodified reference).
- Prompt/prefill timing, first-token timing, steady-state cached-decode
  timing.
- Peak-workspace-bytes and reuse telemetry.

Explicitly out of scope: sticky-layer planner promotion (that is
FL-07B's own research track, not this phase), CUDA, quantization,
VRAM/NVMe planning, a stable ABI, UI, TheOrc product integration, and any
generalized allocator framework.

Constraint, restated from the authorizing instruction: prefer existing
public seams; do not modify any frozen Phase 1-5B file; if a truthful
workspace implementation is impossible through available seams, stop
Phase 5C only (not the other tracks) and report the exact missing seam.

## 2. What "activation workspace" means here, precisely

Three categories of memory this project already tracks separately, per
`docs/OrcEngine/ARCHITECTURE.md`'s "Four independent precision concepts"
lineage and Phase 5A's own "Memory model" section:

1. **Weight residency** (`ResidentView`, `ResidencyLedger`) -- the
   materialized model weights. Already tracked, already accounted,
   frozen since Phase 3/4.
2. **KV-cache residency** (`ContiguousAttentionKVStore`) -- persistent
   per-sequence K/V storage. Already tracked, already a genuine example
   of a pre-allocated, reused, in-place-written buffer: constructed once
   with `max_positions` capacity, then `write_k`/`write_v` mutate it in
   place on every step, no reallocation. Already frozen since Phase 5A.
3. **Activation workspace** (the subject of this phase) -- the
   TRANSIENT intermediate tensors a forward/decode step computes and
   discards: RMSNorm outputs, Q/K/V projections, attention context
   vectors, FFN gate/up/down intermediates, the embedding-lookup output,
   the final-norm output, the logits buffer. **Not currently tracked or
   reused anywhere in this codebase.** Every one of these is a
   freshly-allocated `std::vector<float>`, discarded at the end of the
   call that produced it.

Category 2 (KV-cache) is the existing PATTERN Phase 5C's activation
workspace should follow: an explicitly-owned, capacity-validated,
in-place-written buffer, distinct from and never conflated with weight
residency or KV-cache residency accounting.

## 3. Seam investigation (performed before writing a single line of
Stage 1 code, per the authorizing instruction's own requirement)

Every frozen math primitive that produces an activation tensor was
checked directly against its actual declared signature, not assumed:

```
Tools/OrcEnginePhase1/include/orcengine/ops.hpp:
  std::vector<float> rmsnorm(...)            -- returns by value
  std::vector<float> silu(...)               -- returns by value
  std::vector<float> softmax_last_axis(...)  -- returns by value
  std::vector<float> causal_mask(...)        -- returns by value
  std::vector<float> rope_rotate_half(...)   -- returns by value
  std::vector<float> apply_rope(...)         -- returns by value
  std::vector<float> linear_no_bias(...)     -- returns by value
  std::vector<float> embedding_lookup(...)   -- returns by value
```

`rope_cos_sin` is the ONE exception -- it already takes `cos_out`/
`sin_out` by reference and writes into them in place. Every OTHER
activation-producing primitive returns a freshly-constructed
`std::vector<float>` by value, with **no output-buffer overload
anywhere** in the frozen public API.

```
Tools/OrcEnginePhase5A/include/orcengine/forward_cached.hpp:
  std::vector<float> execute_cached_transformer_layer(
      const std::vector<float>& x, ..., const ContiguousAttentionKVStore&)
```

Takes its input activation `x` by const reference and RETURNS a new
`std::vector<float>` by value -- the per-layer activation chain between
layers is inherently a sequence of fresh allocations; there is no
parameter through which a caller could supply a buffer for this
function to write its result into instead.

```
Tools/OrcEnginePhase5A/include/orcengine/forward_cached.hpp:
  CachedStepResult forward_cached_step(const Model&, ContiguousAttentionKVStore&, ...)
```

Allocates its own embedding-lookup output, final-norm output, and
logits buffers internally and returns them (inside `CachedStepResult`)
by value. No caller-supplied buffer parameter exists here either.

**Conclusion, stated precisely:** there is no existing seam anywhere in
frozen Phase 1-5A through which a caller can supply a reusable output
buffer to ANY activation-producing computation -- not at the per-op
level (`ops::*`), not at the per-layer level
(`execute_cached_transformer_layer`), not at the per-step level
(`forward_cached_step`). Every one of these functions owns its own
output allocation and returns it by value.

## 4. Why this genuinely blocks a truthful Stage 1, not merely an
inconvenient one

Three ways to work around this were considered and rejected, each for a
reason already stated as an explicit Phase 5C non-goal or an existing
project-wide constraint:

1. **Modify the frozen functions to accept an output-buffer parameter.**
   Rejected: explicitly prohibited ("do not modify frozen Phase 1-5B
   files"). These signatures are frozen production API, not this
   phase's to change.
2. **Write a second, parallel implementation of the same math that DOES
   accept output buffers.** Rejected: this is exactly the "second
   copied transformer-layer implementation" this project has
   consistently prohibited since OE-ADR-026 (Phase 5A), for the same
   reason -- two independently-maintained copies of the same math
   diverge silently over time, and "prove they stay identical" becomes
   its own permanent maintenance burden. Phase 5C has no exemption from
   that rule, and extending it to cover the smaller `ops::*` primitives
   too (not just the whole transformer layer) is the same principle at
   a finer grain, not a different one.
3. **Intercept allocations via a custom allocator so the SAME
   `std::vector<float>`-returning calls happen to reuse memory
   underneath.** Rejected: this is precisely a "generalized allocator
   framework," an explicit non-goal, and would not even produce a
   TRUTHFUL workspace-accounting story -- the byte accounting this
   phase is supposed to produce would describe allocator-pool behavior,
   not the actual activation buffers a reader could reason about.

No other seam was found. This is reported as the exact missing seam
this phase's authorizing instruction asked for, not a vague "it's hard."

## 5. Disposition

**Phase 5C Stage 1 (bounded activation-workspace reuse with a bit-exact
numerical proof) cannot be implemented truthfully against the CURRENTLY
frozen Phase 1-5A public API.** The missing seam is precisely: an
output-buffer-accepting overload (or equivalent explicit workspace
parameter) for the activation-producing primitives in
`Tools/OrcEnginePhase1/include/orcengine/ops.hpp` and/or
`execute_cached_transformer_layer`
(`Tools/OrcEnginePhase5A/include/orcengine/forward_cached.hpp`). Adding
such a seam is a decision about FROZEN production API surface, which is
outside this phase's authority to make unilaterally -- per the
authorizing instruction, this stops Phase 5C's Stage 1 implementation
here and reports the finding, rather than proceeding via any of the
three rejected workarounds above.

This does **not** block or reopen Phase 5B (formally frozen,
independently reviewed, tagged, and pushed prior to this worktree's
creation) or FL-07B (Commit 3 landed, PR #105 updated, independent of
this track).

## 6. What Phase 5C COULD still deliver without a new seam, distinguished
from Stage 1 above

The timing/benchmarking half of this phase's charter (prompt/prefill
timing, first-token timing, steady-state decode timing) does NOT
require activation-buffer reuse to produce honest numbers -- it can be
measured against the frozen, unmodified per-call-allocation path
exactly as FL-07B's own B4 measurement was (see
`F:\Ai\OrchestratorIDE-fringelab-fl07b\.orc\fringelab\2026-08-21-fl07b-sticky-planner\EXPERIMENT.md`'s
"B4" section for the isolated-window methodology this would reuse). That
would establish a BASELINE this phase's eventual workspace-reuse
benefit (once the seam question is resolved) could be measured against
-- but collecting that baseline is not "Stage 1: bounded reuse with a
numerical-equivalence proof" as charter-defined, and is not
authorized by this specification pass on its own; it would need to be
proposed as its own narrower, explicitly-scoped follow-up if the
maintainer wants it before the seam question resolves.

## 7. Recommendation for the maintainer decision this phase now needs

One of, not decided by this document:

- **(a)** Authorize a NARROW, explicit output-buffer overload added to
  a small, named set of `ops::*` primitives (e.g. `rmsnorm`,
  `linear_no_bias`) as a new frozen-API addition (not a modification of
  existing overloads -- an additional overload existing callers are
  unaffected by), scoped exactly to what Stage 1 needs, with its own
  review and freeze discipline matching every other frozen-API change
  this project has made.
- **(b)** Accept that Phase 5C, as charter-scoped, is not implementable
  without such a change, and either revise the charter (e.g., scope it
  to timing-only, per Section 6 above) or defer it until a later phase
  is willing to open that specific frozen surface.
- **(c)** Something not yet considered -- this document does not
  attempt to be exhaustive about every conceivable resolution, only
  honest about what was actually investigated.

No implementation proceeds until one of these (or an equivalent) is
explicitly decided.

## 8. Maintainer decision (2026-08-22): option (a) selected

**The maintainer selected option (a).** This is explicit authorization
for a narrow, backward-compatible output-buffer/workspace seam, with
the following rule governing it, recorded here verbatim because it
changes how "frozen" is interpreted for this one purpose:

> The Phase 5B tag is immutable, but a later Phase 5C branch may evolve
> inherited source files through backward-compatible additions.
> Existing public APIs and their numerical behavior must remain
> available and tested. A frozen tag preserves historical authority. It
> does not permanently prohibit later branches from extending shared
> implementation files.

Concretely, this means: `orcengine-phase5b-freeze` (and every earlier
freeze tag) remains untouched and immutable -- it still names an exact,
unmovable commit, and nothing about this decision moves, recreates, or
reinterprets any existing tag. What changes is that `feat/orcengine-
phase5c-activation-workspace`, as a LATER branch built on top of that
frozen history, is now authorized to ADD new overloads to Phase 1's
`ops.hpp`/`ops.cpp` and Phase 5A's `forward_cached.hpp`/`forward_
cached.cpp`, under the single-implementation requirement below -- not to
modify, remove, or change the numerical behavior of any existing
signature.

**Single-implementation requirement, restated as the binding
constraint on every conversion:** for each converted primitive, there
is exactly one arithmetic implementation; the new output-buffer
overload writes into caller-provided storage; the existing return-by-
value signature remains available, unchanged in its own observable
behavior, and is now implemented by allocating an exactly-sized result
and delegating to the SAME shared arithmetic the new overload uses.
Every existing test for the existing signatures must still pass,
unmodified, proving this equivalence in practice, not just in
intent -- see Section 9's validation record.

## 9. Stage 1 implementation record

**Converted primitives (Phase 1 `ops.hpp`/`ops.cpp`):** `rmsnorm_into`,
`linear_no_bias_into`, `silu_into` -- output-buffer (`std::span<float>`)
overloads added alongside the existing return-by-value signatures,
which now allocate an exactly-sized result and delegate to the same
arithmetic. Deliberately NOT converted this stage: `apply_rope`,
`softmax_last_axis`, and the manual elementwise/accumulation loops for
RoPE application, attention-context accumulation, and residual adds --
these remain local `std::vector<float>` allocations in both the
reference and workspace-driven paths, per the "only add output-buffer
forms for operations actually needed" instruction, not mechanically
added for every operation. This is a real, honestly-scoped subset, not
a claim of eliminating every per-layer allocation.

**`ActivationWorkspace`** (`Tools/OrcEnginePhase1/include/orcengine/
activation_workspace.hpp` + `src/activation_workspace.cpp`): ten named
`std::vector<float>` buffers (`norm_out` -- shared, sequential-never-
concurrent slot for attn_norm/ffn_norm/final_norm; `q_proj`; `k_proj`;
`v_proj`; `attn_out`; `gate_proj`; `up_proj`; `gate_activated`;
`ffn_out`; `logits`), each allocated exactly once at construction via
`.assign()`, sized for a fixed model configuration and a fixed maximum
tokens-per-step, never resized afterward. `capacity_bytes()` is fixed
forever after construction; `current_bytes()`/`peak_bytes()` reflect
the MOST RECENT single accessor call, not a running total across a
step's several accessor calls (documented explicitly in the header, a
deliberate scoping decision made during implementation); `reuse_count()`/
`total_prepare_calls()` count every accessor call across
`layer_buffers()`/`final_norm_buffer()`/`logits_buffer()` combined.

**`Tools/OrcEnginePhase5A/src/forward_cached.cpp`:** the original
`execute_cached_transformer_layer` body was extracted, unchanged, into
a private `execute_cached_transformer_layer_impl(..., ActivationWorkspace*)`;
the frozen public signature now calls `impl(..., nullptr)` (falling
back to fresh local `std::vector<float>` buffers, mirroring the
pre-refactor sizes exactly); a new overload taking `ActivationWorkspace&`
calls `impl(..., &workspace)`. RoPE cos/sin temporaries, the manual
attention-context accumulation, and the residual adds are NOT
workspace-covered in either mode, per the scope above.

**`Tools/OrcEnginePhase5C/{include,src}/orcengine/forward_cached_workspace.{hpp,cpp}`**
(new): `forward_cached_step_workspace()` / `forward_cached_step_workspace_
unsafe_explicit_position()`, mirroring Phase 5A's own `forward_cached_step`/
`forward_cached_step_unsafe_explicit_position` exactly in structure and
invariants (including the position-must-equal-current_length() guard
and commit-on-success-only discipline), with one added check:
`new_len > workspace.max_tokens_per_step()` is rejected before any
cache mutation.

**Numerical-equivalence proof:** `Tools/OrcEnginePhase5C/tests/
test_activation_workspace.cpp` (synthetic Fixture C -- multi-token
prefill + 8 single-token decode steps, 65/65 checks) and `test_
activation_workspace_real.cpp` (real SmolLM2-135M F32 GGUF, SHA-256
`fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` --
2-token prefill + 6 single-token decode steps) both compare the
workspace-driven path against Phase 5A's frozen return-by-value
reference on two independently constructed KV caches fed the identical
token sequence: logits bit-identical (`max_abs_diff == 0.0f`), selected
tokens identical, committed KV-cache content and length identical,
all workspace-path logits finite, `capacity_bytes()` unchanged
throughout, `peak_bytes() <= capacity_bytes()` always,
`total_prepare_calls()`/`reuse_count()` matching the exact expected
call count (`steps * (n_layers + 2)`, reuse = total - 1). Failure/retry
behavior also proven: a mismatched-position call is rejected before
mutation, cache state is unchanged after rejection, and a correct
retry afterward still matches the reference exactly -- on both the
synthetic fixture and the real model.

**Benchmark** (`Tools/OrcEnginePhase5C/tools/phase5c_workspace_timing.cpp`,
isolated window, real SmolLM2-135M, 1 untimed warm-up + 5 timed
repetitions per path, interleaved reference/workspace ordering, median
+ min/max spread reported, `ActivationWorkspace` construction cost
measured separately from decode timing): workspace-path
`decode_per_step_ms` median 554.94 vs reference-path median 559.23
(~0.8% lower, well within the observed [549.83,565.74] combined
spread) -- a NEUTRAL result, not a claimed speedup. Expected: Stage 1
converts only three primitives covering nine of many per-layer
allocations, and matmul cost dominates wall-clock at this model size;
no improvement was promised or assumed going in.

**Validation matrix:** Debug 19/19 (Phase 5C tree, including the new
`activation_workspace` synthetic test) + real-model test passing
separately; Release 23/24 (the one failure is `gguf_real_f32_forward`,
an inherited Phase 2 test requiring `ORCENGINE_HF_SOURCE_DIR`, which
was not configured this pass -- an environment gap unrelated to Phase
5C, not a regression); strict `/W4 /WX /permissive-` zero warnings
across the full affected-target set (Phase 1, Phase 5A, Phase 5C);
ASan 13/13 affected targets, no memory-safety findings. The cherry-
picked Phase 5B Track A hardening commit was also re-validated in this
worktree: 29/30 (same out-of-scope `gguf_real_f32_forward` failure),
including `frozen_engine_integration` (86.41s, real model) and the
full tokenizer suite, confirming the cherry-pick and Phase 5C's
parallel Phase 1/5A changes did not disturb it.
