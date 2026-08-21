// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// LogicalTensor: semantic identity of a tensor -- name, shape, architecture
// role -- independent of where or how its bytes are currently stored. A
// LogicalTensor must survive eviction/reload without changing identity
// (docs/OrcEngine/ARCHITECTURE.md, "Memory model" section, OE-ADR-019).
//
// Phase 1 never evicts or reloads anything (single F32 CPU fixture, whole
// model resident for the process lifetime), so this identity/storage split
// is not yet load-bearing -- but the type boundary exists now on purpose,
// so Phase 6B doesn't have to retrofit it onto code that assumed
// LogicalTensor == a malloc'd pointer.
#pragma once

#include <string>

#include "orcengine/tensor.hpp"

namespace orcengine {

class LogicalTensor {
public:
    LogicalTensor() = default;
    LogicalTensor(std::string name, TensorShape shape)
        : name_(std::move(name)), shape_(std::move(shape)) {}

    const std::string& name() const { return name_; }
    const TensorShape& shape() const { return shape_; }

private:
    std::string name_;
    TensorShape shape_;
};

}  // namespace orcengine
