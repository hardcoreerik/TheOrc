// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// BackingExtent: describes where a LogicalTensor's bytes physically come
// from -- source path, byte offset, byte length, encoding -- independent of
// whether or how it's currently resident anywhere.
//
// Phase 1 exercises an owned in-memory F32Raw backing in its metamorphic
// tests. Phase 2 also uses this contract for validated file ranges without
// changing the Phase-1 execution operators.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

namespace orcengine {

enum class BackingEncoding {
    F32Text,   // Phase 1: whitespace-delimited float32 text in a fixture file.
    F32Raw,
    F16Raw,
    GgufQ4_0,
    GgufQ4_1,
    GgufQ5_0,
    GgufQ5_1,
    GgufQ8_0,
    GgufQ2_K,
    GgufQ3_K,
    GgufQ4_K,
    GgufQ5_K,
    GgufQ6_K,
    GgufQ8_K,
};

class BackingExtent {
public:
    BackingExtent() = default;
    BackingExtent(std::string source_path, int64_t byte_offset, int64_t byte_length,
                  BackingEncoding encoding)
        : source_path_(std::move(source_path)), byte_offset_(byte_offset),
          byte_length_(byte_length), encoding_(encoding) {}

    static BackingExtent FromF32(const std::vector<float>& values);

    const std::string& source_path() const { return source_path_; }
    int64_t byte_offset() const { return byte_offset_; }
    int64_t byte_length() const { return byte_length_; }
    BackingEncoding encoding() const { return encoding_; }
    const std::vector<uint8_t>& bytes() const { return bytes_; }

private:
    std::string source_path_;
    int64_t byte_offset_ = 0;
    int64_t byte_length_ = 0;
    BackingEncoding encoding_ = BackingEncoding::F32Text;
    std::vector<uint8_t> bytes_;
};

}  // namespace orcengine
