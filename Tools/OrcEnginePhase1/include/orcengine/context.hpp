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
// Phase 1 implemented only full-prefix (non-incremental) forward passes, so
// this type existed as a contract point with no real accessors. Phase 5A
// (docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md) is its first real exercise: adds
// explicit, bounds-checked read/write accessors and an explicit current-
// length counter -- KV-cache residency this way stays a distinct, separately
// accounted quantity from weight ResidentView bytes, never conflated (see
// the spec's "Memory model" section).
#pragma once

#include <stdexcept>
#include <vector>

#include "orcengine/execution_plan.hpp"
#include "orcengine/model.hpp"
#include "orcengine/tensor.hpp"
#include "orcengine/validation.hpp"

namespace orcengine {

class KVCacheError : public std::runtime_error {
public:
    explicit KVCacheError(const std::string& message)
        : std::runtime_error("OrcEngine KV cache error: " + message) {}
};

// Contiguous, non-paged K/V storage for one sequence. [n_layers][n_kv_heads][max_positions][head_dim].
class ContiguousAttentionKVStore {
public:
    ContiguousAttentionKVStore() = default;
    ContiguousAttentionKVStore(int64_t n_layers, int64_t n_kv_heads, int64_t max_positions, int64_t head_dim)
        : n_layers_(n_layers), n_kv_heads_(n_kv_heads), max_positions_(max_positions), head_dim_(head_dim),
          k_(checked_size(n_layers, n_kv_heads, max_positions, head_dim), 0.0f),
          v_(checked_size(n_layers, n_kv_heads, max_positions, head_dim), 0.0f) {}

    int64_t n_layers() const { return n_layers_; }
    int64_t n_kv_heads() const { return n_kv_heads_; }
    int64_t max_positions() const { return max_positions_; }
    int64_t head_dim() const { return head_dim_; }

    // Current number of valid, written positions -- distinct from
    // max_positions (capacity). Explicit, caller-advanced (write_row does
    // NOT auto-advance it) so a caller can write and verify before
    // committing the new length -- see KVCacheError below for what happens
    // if that discipline is violated.
    int64_t current_length() const { return current_length_; }
    void set_current_length(int64_t length) {
        if (length < 0 || length > max_positions_) {
            throw KVCacheError("current_length " + std::to_string(length) +
                                " outside [0, " + std::to_string(max_positions_) + "]");
        }
        current_length_ = length;
    }

    // Writes one position's K (or V) vector for one layer/kv_head. Bounds-
    // checked against max_positions_ (capacity), NOT against current_length_
    // -- a write is how a position BECOMES valid; validate against
    // current_length_ separately if "must not overwrite an already-committed
    // position" is a policy this caller wants (Phase 5A's driver does).
    void write_k(int64_t layer, int64_t kv_head, int64_t position, const float* head_dim_values) {
        write_row(k_, layer, kv_head, position, head_dim_values);
    }
    void write_v(int64_t layer, int64_t kv_head, int64_t position, const float* head_dim_values) {
        write_row(v_, layer, kv_head, position, head_dim_values);
    }

    // Read-only access to one position's K (or V) vector, bounds-checked
    // against capacity (max_positions_). Does NOT gate on current_length_ --
    // a single forward_cached_step call legitimately reads positions it is
    // writing in the same call, before current_length_ advances for the
    // step as a whole (see forward_cached.cpp). current_length_ instead
    // exists as explicit committed-length ACCOUNTING the driver maintains;
    // a driver that reads/attends to a position beyond what it has actually
    // written this sequence (a real "stale reuse" or "wrong position" bug)
    // reads genuine zero-initialized memory rather than the correct K/V,
    // which is exactly what Phase 5A's fault-injection tests exploit to
    // prove such bugs are detectable (see PHASE5A_KV_CACHE_SPEC.md).
    const float* k_row(int64_t layer, int64_t kv_head, int64_t position) const {
        return read_row(k_, layer, kv_head, position);
    }
    const float* v_row(int64_t layer, int64_t kv_head, int64_t position) const {
        return read_row(v_, layer, kv_head, position);
    }

private:
    static size_t checked_size(int64_t n_layers, int64_t n_kv_heads,
                               int64_t max_positions, int64_t head_dim) {
        return static_cast<size_t>(
            TensorShape({n_layers, n_kv_heads, max_positions, head_dim}).element_count());
    }

    size_t row_offset(int64_t layer, int64_t kv_head, int64_t position) const {
        return static_cast<size_t>(((layer * n_kv_heads_ + kv_head) * max_positions_ + position) * head_dim_);
    }

    void write_row(std::vector<float>& storage, int64_t layer, int64_t kv_head,
                   int64_t position, const float* head_dim_values) {
        if (layer < 0 || layer >= n_layers_) throw KVCacheError("layer index out of range");
        if (kv_head < 0 || kv_head >= n_kv_heads_) throw KVCacheError("kv_head index out of range");
        if (position < 0 || position >= max_positions_) {
            throw KVCacheError("write position " + std::to_string(position) +
                                " exceeds cache capacity " + std::to_string(max_positions_));
        }
        const size_t offset = row_offset(layer, kv_head, position);
        for (int64_t d = 0; d < head_dim_; ++d) {
            storage[offset + static_cast<size_t>(d)] = head_dim_values[d];
        }
    }

    const float* read_row(const std::vector<float>& storage, int64_t layer, int64_t kv_head,
                          int64_t position) const {
        if (layer < 0 || layer >= n_layers_) throw KVCacheError("layer index out of range");
        if (kv_head < 0 || kv_head >= n_kv_heads_) throw KVCacheError("kv_head index out of range");
        if (position < 0 || position >= max_positions_) {
            throw KVCacheError("read position " + std::to_string(position) +
                                " exceeds cache capacity " + std::to_string(max_positions_));
        }
        return &storage[row_offset(layer, kv_head, position)];
    }

    int64_t n_layers_ = 0, n_kv_heads_ = 0, max_positions_ = 0, head_dim_ = 0;
    int64_t current_length_ = 0;
    std::vector<float> k_;
    std::vector<float> v_;
};

class Context {
public:
    static Context Create(const Model& model, const ExecutionPlan& plan) {
        (void)plan;
        validate_model_config(model.config());
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
