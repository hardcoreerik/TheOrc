// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ContextStateStore / Context: per-sequence state (currently just K/V,
// contiguous, no paging). See docs/OrcEngine/ARCHITECTURE.md, "Context
// state is abstract, and pageable KV is not speculative" -- the Native
// Runtime's own production incidents (fixed KV slots, NoKvSlot, cancellation
// poisoning) are why this stays a named, swappable type instead of a bare
// std::vector threaded through the operators.
//
// Phase 1 implements only full-prefix (non-incremental) forward passes, so
// ContextStateStore here is unused by the differential harness but exists
// as the documented contract point for Phase 3's cached-decode work.
#pragma once

#include <vector>

#include "orcengine/execution_plan.hpp"
#include "orcengine/model.hpp"
#include "orcengine/tensor.hpp"

namespace orcengine {

// Contiguous, non-paged K/V storage for one sequence. [n_layers][n_kv_heads][max_positions][head_dim].
class ContiguousAttentionKVStore {
public:
    ContiguousAttentionKVStore() = default;
    ContiguousAttentionKVStore(int64_t n_layers, int64_t n_kv_heads, int64_t max_positions, int64_t head_dim)
        : n_layers_(n_layers), n_kv_heads_(n_kv_heads), max_positions_(max_positions), head_dim_(head_dim),
          k_(static_cast<size_t>(n_layers * n_kv_heads * max_positions * head_dim), 0.0f),
          v_(static_cast<size_t>(n_layers * n_kv_heads * max_positions * head_dim), 0.0f) {}

    int64_t n_layers() const { return n_layers_; }
    int64_t n_kv_heads() const { return n_kv_heads_; }
    int64_t max_positions() const { return max_positions_; }
    int64_t head_dim() const { return head_dim_; }

private:
    int64_t n_layers_ = 0, n_kv_heads_ = 0, max_positions_ = 0, head_dim_ = 0;
    std::vector<float> k_;
    std::vector<float> v_;
};

class Context {
public:
    static Context Create(const Model& model, const ExecutionPlan& plan) {
        (void)plan;
        Context ctx;
        ctx.kv_store_ = ContiguousAttentionKVStore(
            model.config().n_layers, model.config().n_kv_heads,
            model.config().max_positions, model.config().head_dim);
        return ctx;
    }

    ContiguousAttentionKVStore& kv_store() { return kv_store_; }

private:
    ContiguousAttentionKVStore kv_store_;
};

}  // namespace orcengine
