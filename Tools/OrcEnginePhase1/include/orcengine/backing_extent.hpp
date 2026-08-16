// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// BackingExtent: describes where a LogicalTensor's bytes physically come
// from -- source path, byte offset, byte length, encoding -- independent of
// whether or how it's currently resident anywhere.
//
// Phase 1's only source is the flat text fixture format (see
// docs/OrcEngine/PHASE1_IMPLEMENTATION.md); "encoding" is always F32Text
// here. A future GGUF-backed BackingExtent (Phase 2+) points into a mapped
// GGUF file instead -- this type is what makes that swap not require
// touching LogicalTensor or the operators at all.
#pragma once

#include <cstdint>
#include <string>

namespace orcengine {

enum class BackingEncoding {
    F32Text,   // Phase 1: whitespace-delimited float32 text in a fixture file.
    F32Raw,    // reserved for a future raw-binary backing (unused in Phase 1).
};

class BackingExtent {
public:
    BackingExtent() = default;
    BackingExtent(std::string source_path, int64_t byte_offset, int64_t byte_length,
                  BackingEncoding encoding)
        : source_path_(std::move(source_path)), byte_offset_(byte_offset),
          byte_length_(byte_length), encoding_(encoding) {}

    const std::string& source_path() const { return source_path_; }
    int64_t byte_offset() const { return byte_offset_; }
    int64_t byte_length() const { return byte_length_; }
    BackingEncoding encoding() const { return encoding_; }

private:
    std::string source_path_;
    int64_t byte_offset_ = 0;
    int64_t byte_length_ = 0;
    BackingEncoding encoding_ = BackingEncoding::F32Text;
};

}  // namespace orcengine
