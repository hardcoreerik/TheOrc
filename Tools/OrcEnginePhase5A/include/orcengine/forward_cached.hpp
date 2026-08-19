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

// Processes new_token_ids (length new_len) as new positions
// [start_position, start_position + new_len), reading cache.k_row/v_row for
// prior positions [0, start_position) and writing this step's new K/V into
// the cache at [start_position, start_position + new_len).
//
// Commit contract (narrowed 2026-08-18 per the commit-API misuse audit,
// see DECISION_LOG.md OE-ADR-026's referenced findings): this function
// commits cache.current_length() to start_position+new_len ITSELF, on the
// success return path ONLY -- never on any exception path. Callers must
// NOT call cache.set_current_length() themselves after calling this
// function; doing so is redundant on success and was the exact
// "failed step + set_current_length()" pattern that could make poisoned
// KV appear committed if called after catching an exception. A failed
// call leaves cache.current_length() exactly as it was before the call
// (see test_transactional_semantics.cpp / test_transactional_semantics_virtualized.cpp
// for the proof, including a mid-layer NaN fault and a safe same-position
// retry).
CachedStepResult forward_cached_step(const Model& model, ContiguousAttentionKVStore& cache,
                                      const std::vector<int64_t>& new_token_ids,
                                      int64_t start_position);

}  // namespace orcengine
