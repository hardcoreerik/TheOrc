// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Opt-in observability: per-tap min/max/mean/NaN/Inf counts. Disabled by
// default (near-zero cost when off); enabled by setting the environment
// variable ORCENGINE_DEBUG_TAPS=1 before running. Not wired into any hot
// path decision -- purely diagnostic, matching the steering document's
// "aggressively test for NaN/Inf... without optimizing based on it."
#pragma once

#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <limits>
#include <string>
#include <vector>

namespace orcengine::diagnostics {

inline bool debug_taps_enabled() {
    static const bool enabled = [] {
        const char* v = std::getenv("ORCENGINE_DEBUG_TAPS");
        return v != nullptr && std::string(v) == "1";
    }();
    return enabled;
}

// Prints min/max/mean/nan_count/inf_count for `data` under `label` if
// ORCENGINE_DEBUG_TAPS=1 is set. Always returns whether any NaN/Inf was
// found, regardless of whether printing is enabled, so callers can choose
// to fail closed on poisoned data even with tracing off.
inline bool check_tap(const std::string& label, const std::vector<float>& data) {
    float min_v = std::numeric_limits<float>::infinity();
    float max_v = -std::numeric_limits<float>::infinity();
    double sum = 0.0;
    size_t nan_count = 0, inf_count = 0;
    for (float v : data) {
        if (std::isnan(v)) { ++nan_count; continue; }
        if (std::isinf(v)) { ++inf_count; continue; }
        min_v = std::min(min_v, v);
        max_v = std::max(max_v, v);
        sum += v;
    }
    bool poisoned = nan_count > 0 || inf_count > 0;
    if (debug_taps_enabled()) {
        double mean = data.empty() ? 0.0 : sum / static_cast<double>(data.size());
        std::fprintf(stderr, "[orcengine-debug] %s: min=%.6g max=%.6g mean=%.6g nan=%zu inf=%zu n=%zu%s\n",
                     label.c_str(), min_v, max_v, mean, nan_count, inf_count, data.size(),
                     poisoned ? "  <-- POISONED" : "");
    }
    return poisoned;
}

}  // namespace orcengine::diagnostics
