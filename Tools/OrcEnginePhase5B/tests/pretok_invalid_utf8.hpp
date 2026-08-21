// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// GENERATED FILE -- do not hand-edit. Produced by
// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.
#pragma once

#include <cstddef>
#include <string_view>

namespace orcengine::pretok_fixtures {

struct InvalidUtf8Case { const char* id; std::string_view bytes; };

inline constexpr InvalidUtf8Case kInvalidUtf8Cases[] = {
    {"lone_continuation_byte", std::string_view("\x80", 1)},
    {"truncated_2byte", std::string_view("\xc2", 1)},
    {"truncated_3byte", std::string_view("\xe0\xa0", 2)},
    {"truncated_4byte", std::string_view("\xf0\x90\x80", 3)},
    {"overlong_2byte_null", std::string_view("\xc0\x80", 2)},
    {"overlong_3byte", std::string_view("\xe0\x80\x80", 3)},
    {"surrogate_encoded", std::string_view("\xed\xa0\x80", 3)},
    {"above_10FFFF", std::string_view("\xf4\x90\x80\x80", 4)},
};
inline constexpr std::size_t kInvalidUtf8CaseCount = 8;

}  // namespace orcengine::pretok_fixtures
