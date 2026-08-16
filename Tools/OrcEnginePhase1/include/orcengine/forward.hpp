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

// ActivationBuffer: runtime-created, ephemeral execution state produced
// while running the forward pass -- e.g. "the RMSNorm output at layer 0."
// Deliberately NOT a ResidentView. ResidentView means "the currently
// materialized copy of a LogicalTensor backed by durable storage" (model
// weights); an activation has no LogicalTensor, no BackingExtent, and no
// identity that survives past this one forward() call. Collapsing the two
// concepts because both happen to be "a shape plus a float buffer" would
// quietly erase the model-identity/execution-state distinction Phase 6B's
// residency contracts depend on (docs/OrcEngine/ARCHITECTURE.md's "Memory
// model" section) -- so they stay two separate types even though this one
// is intentionally simpler.
struct ActivationBuffer {
    std::vector<int64_t> dims;
    std::vector<float> data;
};

struct ForwardResult {
    std::unordered_map<std::string, ActivationBuffer> taps;
    std::vector<float> logits;   // [seq, vocab]
    std::vector<int64_t> selected_token;  // [seq]
};

// token_ids: [seq]. Captures every tap the Python oracle captures.
ForwardResult forward(const Model& model, const std::vector<int64_t>& token_ids);

}  // namespace orcengine
