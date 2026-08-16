// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// ResidentView: the currently-materialized copy of a LogicalTensor --
// memory tier, resident address, resident dtype, compute dtype. Distinct
// from LogicalTensor (identity survives eviction) and BackingExtent (where
// the bytes originally come from).
//
// Phase 1 has exactly one tier (ResidentCPU) and one dtype (F32 resident ==
// F32 compute, no separate storage/compute precision yet) -- see
// docs/OrcEngine/ARCHITECTURE.md's "Four independent precision concepts"
// section for why that distinction still exists as a field here even though
// Phase 1 only ever has one answer for it.
#pragma once

#include <stdexcept>
#include <vector>

#include "orcengine/tensor.hpp"

namespace orcengine {

enum class ResidencyTier {
    ResidentCPU,   // Phase 1: the only tier that exists.
};

enum class ComputeDType {
    F32,   // Phase 1: the only compute dtype that exists.
};

// Owns the actual float buffer. This is deliberately the ONLY place in
// Phase 1 that owns tensor bytes -- LogicalTensor and BackingExtent never do.
class ResidentView {
public:
    ResidentView() = default;
    explicit ResidentView(TensorShape shape)
        : shape_(std::move(shape)), data_(static_cast<size_t>(shape_.element_count()), 0.0f) {}
    ResidentView(TensorShape shape, std::vector<float> data)
        : shape_(std::move(shape)), data_(std::move(data)) {
        if (static_cast<int64_t>(data_.size()) != shape_.element_count()) {
            throw std::invalid_argument("ResidentView: data size does not match shape " + shape_.to_string());
        }
    }

    const TensorShape& shape() const { return shape_; }
    ResidencyTier tier() const { return tier_; }
    ComputeDType compute_dtype() const { return compute_dtype_; }

    float* data() { return data_.data(); }
    const float* data() const { return data_.data(); }
    std::vector<float>& raw() { return data_; }
    const std::vector<float>& raw() const { return data_; }

private:
    TensorShape shape_;
    std::vector<float> data_;
    ResidencyTier tier_ = ResidencyTier::ResidentCPU;
    ComputeDType compute_dtype_ = ComputeDType::F32;
};

}  // namespace orcengine
