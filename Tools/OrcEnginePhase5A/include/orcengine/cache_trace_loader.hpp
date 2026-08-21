// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Loads the flat text trace format written by
// Tools/OrcEnginePhase0/oracle/export_cpp_phase5a_cache_fixture.py --
// per-step new tokens, start position, expected logits/selection, and full
// per-layer cache K/V content after the step (not just logits -- see
// docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md's oracle section for why cache
// content itself must be independently checked).
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace orcengine {

struct CacheLayerSnapshot {
    std::vector<int64_t> k_dims;  // [n_kv_heads, cur_len, head_dim]
    std::vector<float> k;
    std::vector<int64_t> v_dims;
    std::vector<float> v;
};

struct CacheTraceStep {
    std::string kind;  // "prefill" | "decode"
    std::vector<int64_t> seq_before;
    std::vector<int64_t> new_tokens;
    int64_t start_position = 0;
    std::vector<float> logits_last;
    int64_t selected = 0;
    std::vector<CacheLayerSnapshot> cache_after;
};

std::vector<CacheTraceStep> load_cache_trace(const std::string& path);

}  // namespace orcengine
