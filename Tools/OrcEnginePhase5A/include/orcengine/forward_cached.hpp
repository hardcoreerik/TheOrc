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

// Processes new_token_ids (length new_len) as new positions
// [start_position, start_position + new_len), reading cache.k_row/v_row for
// prior positions [0, start_position) and writing this step's new K/V into
// the cache at [start_position, start_position + new_len). Does NOT advance
// cache.current_length() -- the caller does that explicitly after
// validating the step succeeded, matching the spec's "explicit ownership"
// requirement (no accidental cache mutation on a failed/partial step).
CachedStepResult forward_cached_step(const Model& model, ContiguousAttentionKVStore& cache,
                                      const std::vector<int64_t>& new_token_ids,
                                      int64_t start_position);

}  // namespace orcengine
