// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5A: KV-cached incremental decode. Mirrors
// Tools/OrcEnginePhase0/oracle/model.py's forward_cached() step-for-step --
// same operator sequence, same RoPE-at-absolute-position semantics, same
// rectangular causal mask (query position p may attend to key positions
// 0..p) -- extending Phase 1's forward_impl rather than duplicating its
// transformer math. See docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md.
#pragma once

#include <cstdint>
#include <vector>

#include "orcengine/activation_workspace.hpp"
#include "orcengine/context.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

struct CachedStepResult {
    std::vector<float> logits;            // [new_len, vocab]
    std::vector<int64_t> selected_token;  // [new_len]
};

// Shared per-layer cached-decode math: RMSNorm, Q/K/V projection, RoPE,
// GQA-mapped rectangular-causal attention against `cache` (writing this
// step's new K/V at [start_position, start_position+new_len) and reading
// prior committed positions [0, start_position) plus this step's own new
// positions), output projection, residual, FFN. Returns the layer's output
// activation. Used IDENTICALLY by the fully-resident reference
// (forward_cached_step, Reference Path B) and the virtualized composed
// target (forward_cached_step_virtualized, Reference Path C, see
// forward_cached_virtualized.hpp) -- weight SOURCING differs between the
// two (resident array indexing vs on-demand per-layer materialize/release),
// this math does not, per OE-ADR-026's explicit prohibition on a second
// copied transformer-layer implementation.
std::vector<float> execute_cached_transformer_layer(
    const std::vector<float>& x, int64_t new_len, int64_t start_position,
    const LayerWeights& lw, const ModelConfig& cfg, int64_t layer,
    const std::vector<std::vector<float>>& cos_by_pos,
    const std::vector<std::vector<float>>& sin_by_pos,
    ContiguousAttentionKVStore& cache);

// Phase 5C addition (backward-compatible): IDENTICAL math to the
// overload above -- both now route through one shared internal
// implementation (see forward_cached.cpp) -- except the nine largest,
// most-repeated per-layer intermediates (the two RMSNorm outputs, Q/K/V
// projections, attention output projection, FFN gate/up projections,
// the SiLU-activated gate, and the FFN down-projection output) are
// written into `workspace`'s own reusable buffers instead of being
// freshly allocated on every call. `workspace.layer_buffers(new_len)`
// is called internally exactly once; `new_len` must not exceed
// `workspace.max_tokens_per_step()` (throws ActivationWorkspaceError
// otherwise, before any numerical work). The overload above remains
// available, unchanged in its own observable behavior, and is NOT
// implemented in terms of this one (nor vice versa) -- both call the
// same shared per-op arithmetic (ops::rmsnorm_into/linear_no_bias_into/
// silu_into), never a second copy of it. RoPE's per-head-dim
// temporaries, the manual attention-context accumulation, and the
// residual/final-output additions are NOT covered by `workspace` in
// this Stage 1 -- see docs/OrcEngine/PHASE5C_ACTIVATION_WORKSPACE_SPEC.md
// for the exact, honestly-scoped buffer inventory.
std::vector<float> execute_cached_transformer_layer(
    const std::vector<float>& x, int64_t new_len, int64_t start_position,
    const LayerWeights& lw, const ModelConfig& cfg, int64_t layer,
    const std::vector<std::vector<float>>& cos_by_pos,
    const std::vector<std::vector<float>>& sin_by_pos,
    ContiguousAttentionKVStore& cache, ActivationWorkspace& workspace);

// Processes new_token_ids (length new_len) as new positions
// [start_position, start_position + new_len), reading cache.k_row/v_row for
// prior positions [0, start_position) and writing this step's new K/V into
// the cache at [start_position, start_position + new_len).
//
// SAFE, NORMAL PRODUCTION ENTRY POINT (narrowed 2026-08-18 per the P5A-RVW-002
// commit-API safety review finding). This function REQUIRES
// start_position == cache.current_length() and throws KVCacheError BEFORE
// any cache mutation if it does not -- a caller can no longer skip unwritten
// positions, rewind into committed history, or auto-commit a bogus logical
// length by passing the wrong position. current_length() is committed to
// start_position+new_len on the success return path ONLY, never on any
// exception path (including the position-mismatch rejection itself, which
// mutates nothing). Callers must NOT call cache.set_current_length()
// themselves after calling this function; doing so is redundant on success
// and was the exact "failed step + set_current_length()" pattern that could
// make poisoned KV appear committed if called after catching an exception
// (see test_transactional_semantics.cpp for the mid-layer-NaN-fault proof).
//
// Deliberate position-mismatch fault injection (wrong position, rewind,
// reset-to-zero, RoPE-position attacks) is NOT possible through this
// function anymore -- use forward_cached_step_unsafe_explicit_position
// below, which is the same math with the position check removed, reserved
// for tests that need to attack the invariant this function now enforces.
CachedStepResult forward_cached_step(const Model& model, ContiguousAttentionKVStore& cache,
                                      const std::vector<int64_t>& new_token_ids,
                                      int64_t start_position);

// UNSAFE LOW-LEVEL / TEST-ONLY SEAM. Identical to forward_cached_step except
// it does NOT require start_position == cache.current_length() -- the
// caller may pass any position that satisfies the underlying capacity
// bounds, including gaps, rewinds, and resets. Still auto-commits
// current_length() = start_position+new_len on success (never on failure).
// This exists ONLY so fault-injection tests can deliberately violate the
// position invariant forward_cached_step now enforces, to prove the
// resulting divergence is detectable. Production/reference driver code
// must use forward_cached_step, not this function.
CachedStepResult forward_cached_step_unsafe_explicit_position(
    const Model& model, ContiguousAttentionKVStore& cache,
    const std::vector<int64_t>& new_token_ids, int64_t start_position);

}  // namespace orcengine
