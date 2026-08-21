// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include "orcengine/gguf.hpp"
#include "orcengine/model_source.hpp"

namespace orcengine {

// GGUF is an adapter into the format-neutral Phase-3 source contract.
ModelSourceBinding bind_gguf_source(ModelArtifactManifest manifest);

}  // namespace orcengine
