// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Full-prefix (non-incremental) forward pass, mirroring
// Tools/OrcEnginePhase0/oracle/model.py's forward() step-for-step, with the
// same tap names, so the differential harness can compare every stage
// against the exported Python fixture rather than only the final logits.
#pragma once

#include <cstdint>
#include <string>
#include <unordered_map>
#include <vector>

#include "orcengine/model.hpp"

namespace orcengine {

// A named intermediate value: flat row-major data plus its logical shape
// (as explicit dims, since taps range from 1D to 3D).
struct Tap {
    std::vector<int64_t> dims;
    std::vector<float> data;
};

struct ForwardResult {
    std::unordered_map<std::string, Tap> taps;
    std::vector<float> logits;   // [seq, vocab]
    std::vector<int64_t> selected_token;  // [seq]
};

// token_ids: [seq]. Captures every tap the Python oracle captures.
ForwardResult forward(const Model& model, const std::vector<int64_t>& token_ids);

}  // namespace orcengine
