// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/materialization.hpp"

#include <cstring>
#include <limits>

#include "orcengine/validation.hpp"

namespace orcengine {

BackingExtent BackingExtent::FromF32(const std::vector<float>& values) {
    if (values.size() > static_cast<size_t>(std::numeric_limits<int64_t>::max() /
                                            static_cast<int64_t>(sizeof(float)))) {
        throw ValidationError("F32 backing byte count overflow");
    }
    BackingExtent extent("<memory>", 0,
                         static_cast<int64_t>(values.size() * sizeof(float)),
                         BackingEncoding::F32Raw);
    extent.bytes_.resize(values.size() * sizeof(float));
    if (!values.empty()) {
        std::memcpy(extent.bytes_.data(), values.data(), extent.bytes_.size());
    }
    return extent;
}

ResidentView materialize(const LogicalTensor& tensor, const BackingExtent& backing) {
    if (backing.encoding() != BackingEncoding::F32Raw) {
        throw ValidationError("Phase-1 materialize requires F32Raw backing");
    }
    const int64_t elements = tensor.shape().element_count();
    if (elements > std::numeric_limits<int64_t>::max() /
                       static_cast<int64_t>(sizeof(float))) {
        throw ValidationError("backing byte count overflow for logical tensor '" +
                              tensor.name() + "'");
    }
    const int64_t required_bytes = elements * static_cast<int64_t>(sizeof(float));
    if (backing.byte_offset() != 0 || backing.byte_length() != required_bytes ||
        static_cast<int64_t>(backing.bytes().size()) != required_bytes) {
        throw ValidationError("backing byte extent does not match logical tensor '" +
                              tensor.name() + "'");
    }

    std::vector<float> values(static_cast<size_t>(elements));
    if (!values.empty()) {
        std::memcpy(values.data(), backing.bytes().data(), backing.bytes().size());
    }
    return ResidentView(tensor.shape(), std::move(values));
}

}  // namespace orcengine
