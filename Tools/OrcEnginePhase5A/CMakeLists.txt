# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 5A: KV-cached incremental decode reference. Extends
# frozen Phase-1 operators with a cached forward-pass variant, proven
# against an independently-computed Python trace on synthetic Fixture C.
# See docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase5A CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()
add_subdirectory(../OrcEnginePhase1 phase1)

add_library(orcengine_phase5a
    src/forward_cached.cpp
    src/cache_trace_loader.cpp
)
target_include_directories(orcengine_phase5a PUBLIC include)
target_link_libraries(orcengine_phase5a PUBLIC orcengine_phase1)
target_compile_options(orcengine_phase5a PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_cached_decode tests/test_cached_decode.cpp)
target_link_libraries(test_cached_decode PRIVATE orcengine_phase5a)
target_compile_options(test_cached_decode PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_test(NAME cached_decode COMMAND test_cached_decode
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")
