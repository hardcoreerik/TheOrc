// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include "orcengine/backing_extent.hpp"
#include "orcengine/logical_tensor.hpp"
#include "orcengine/resident_view.hpp"

namespace orcengine {

// Phase 1's smallest real backing path: immutable F32 bytes are copied into
// a fresh CPU-resident allocation for the identified logical tensor.
ResidentView materialize(const LogicalTensor& tensor, const BackingExtent& backing);

}  // namespace orcengine
