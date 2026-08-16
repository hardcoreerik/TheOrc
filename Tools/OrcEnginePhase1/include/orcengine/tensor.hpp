// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// TensorShape: pure semantic shape (dimension sizes, row-major/C order).
// No storage, no residency, no backing -- just "what shape is this
// mathematically." Every other Phase-1 contract type (LogicalTensor,
// BackingExtent, ResidentView) references a TensorShape but is not one.
#pragma once

#include <cstdint>
#include <numeric>
#include <stdexcept>
#include <string>
#include <vector>

namespace orcengine {

class TensorShape {
public:
    TensorShape() = default;
    explicit TensorShape(std::vector<int64_t> dims) : dims_(std::move(dims)) {}

    int64_t ndim() const { return static_cast<int64_t>(dims_.size()); }
    int64_t dim(int64_t i) const { return dims_.at(static_cast<size_t>(i)); }
    const std::vector<int64_t>& dims() const { return dims_; }

    int64_t element_count() const {
        return std::accumulate(dims_.begin(), dims_.end(), int64_t{1}, std::multiplies<int64_t>());
    }

    bool operator==(const TensorShape& other) const { return dims_ == other.dims_; }
    bool operator!=(const TensorShape& other) const { return !(*this == other); }

    std::string to_string() const {
        std::string s = "[";
        for (size_t i = 0; i < dims_.size(); ++i) {
            if (i) s += ", ";
            s += std::to_string(dims_[i]);
        }
        return s + "]";
    }

private:
    std::vector<int64_t> dims_;
};

}  // namespace orcengine
