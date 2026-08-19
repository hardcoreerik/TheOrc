// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5A Reference Path C: KV-cached decode composed with Phase 3/4's
// transient, per-layer-materialized weight architecture. Persistent
// inference state (ContiguousAttentionKVStore) is threaded through calls;
// weights are never fully resident -- exactly one transformer layer's
// weights are materialized at a time, via the SAME public ModelSource /
// TensorMaterializer / TensorRowRegionMaterializer / ResidencyLedger /
// TensorRowRegion contracts Phase 3/4 already expose (Tools/OrcEnginePhase3/
// include/orcengine/{model_source,streaming}.hpp), not new ones. The
// per-layer transformer math is execute_cached_transformer_layer()
// (forward_cached.hpp) -- identical to Reference Path B
// (forward_cached_step), not reimplemented here, per OE-ADR-026's explicit
// prohibition on a second copied transformer-layer implementation.
//
// Embedding stays row-virtualized (one row materialized/released per
// distinct new token) and the output head stays row-chunk-virtualized
// (build_complete_row_partition), matching Phase 4's own bookend
// virtualization exactly -- the final-norm weight is the one exception,
// kept resident permanently, matching Phase 4's own precedent (it is a
// [hidden] vector, negligible next to a [vocab,hidden] or [hidden,hidden]
// tensor, and Phase 4's own virtualize_bookends=true path does the same).
//
// See docs/OrcEngine/DECISION_LOG.md OE-ADR-026 and
// docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md's "Active Phase-5A completion
// gate after real-model audit" section.
#pragma once

#include <cstdint>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/model_source.hpp"
#include "orcengine/streaming.hpp"

namespace orcengine {

struct VirtualizedCachedConfig {
    TensorMaterializer materializer;                      // whole-tensor: per-layer weights, final norm
    TensorRowRegionMaterializer row_region_materializer;   // row-region: embedding lookup, output chunks
    uint64_t output_chunk_rows = 1024;
    uint64_t residency_budget_bytes = std::numeric_limits<uint64_t>::max();
};

class VirtualizedCachedModel {
public:
    VirtualizedCachedModel(ModelSource source, VirtualizedCachedConfig config);

    const ModelConfig& config() const { return source_.config; }
    const StreamingTelemetry& telemetry() const { return ledger_.telemetry(); }

    // Same contract as forward_cached_step (forward_cached.hpp), including
    // its narrowed auto-commit rule: this function itself commits
    // cache.current_length() to start_position+new_len on the success path
    // ONLY, never on an exception path. Callers must NOT call
    // cache.set_current_length() themselves afterward. Only ONE transformer layer's weights are resident at any instant
    // during this call (proven via telemetry().peak_active_layers == 1 and
    // ResidencyLedger::enter_layer's own more-than-one-resident guard).
    CachedStepResult step(ContiguousAttentionKVStore& cache,
                          const std::vector<int64_t>& new_token_ids,
                          int64_t start_position);

private:
    std::vector<float> virtualized_embedding(const std::vector<int64_t>& token_ids);
    std::vector<float> virtualized_output(const std::vector<float>& final_normed, int64_t new_len);
    LayerWeights materialize_layer(int64_t layer, uint64_t& resident_bytes, uint64_t& tensor_count);

    ModelSource source_;
    VirtualizedCachedConfig config_;
    ResidentView final_norm_weight_;
    ResidencyLedger ledger_;
};

}  // namespace orcengine
