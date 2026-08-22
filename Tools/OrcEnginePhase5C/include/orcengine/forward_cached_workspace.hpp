// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5C Stage 1: a workspace-aware cached-decode step driver, mirroring
// Phase 5A's own forward_cached_step / forward_cached_step_unsafe_explicit_
// position exactly in structure and invariants, but threading an
// ActivationWorkspace through every layer call (via Phase 5A's own
// workspace-aware execute_cached_transformer_layer overload, called but
// not copied) and through the step-level final-norm/logits computation
// (via Phase 1's rmsnorm_into/linear_no_bias_into, called but not
// copied). The per-layer and per-op ARITHMETIC is never duplicated here --
// only the orchestration loop (embedding lookup, RoPE cos/sin
// precomputation, the per-layer loop, argmax selection, cache commit) is
// necessarily written once more, exactly as small and exactly as thin as
// Phase 5A's own equivalent driver.
#pragma once

#include <cstdint>
#include <vector>

#include "orcengine/activation_workspace.hpp"
#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

// SAFE, NORMAL ENTRY POINT -- identical position-invariant contract to
// Phase 5A's own forward_cached_step: requires start_position ==
// cache.current_length(), throws KVCacheError before any mutation if not.
// Commits cache.current_length() on the success path only.
CachedStepResult forward_cached_step_workspace(const Model& model, ContiguousAttentionKVStore& cache,
                                               const std::vector<int64_t>& new_token_ids, int64_t start_position,
                                               ActivationWorkspace& workspace);

// UNSAFE LOW-LEVEL / TEST-ONLY SEAM -- identical relaxation to Phase 5A's
// own forward_cached_step_unsafe_explicit_position: does not require
// start_position == cache.current_length(). Reserved for fault-injection
// and retry-after-failure tests.
CachedStepResult forward_cached_step_workspace_unsafe_explicit_position(
    const Model& model, ContiguousAttentionKVStore& cache, const std::vector<int64_t>& new_token_ids,
    int64_t start_position, ActivationWorkspace& workspace);

}  // namespace orcengine
