# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 5B Stage 1: native GGUF tokenizer-metadata construction
# and fail-closed validation. See docs/OrcEngine/PHASE5B_TOKENIZER_SPEC.md
# and DECISION_LOG.md OE-ADR-030. Reuses the frozen Phase 2 GGUF reader
# through its existing public target -- does NOT modify Phase 1/2/3/4/5A.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase5B CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

# Phase 2 already add_subdirectory's Phase 1 itself -- do not also add it
# here, that would double-define its targets (same pattern Phase 3/5A use).
add_subdirectory(../OrcEnginePhase2 phase2)

add_library(orcengine_phase5b
    src/tokenizer.cpp
)
target_include_directories(orcengine_phase5b PUBLIC include)
target_link_libraries(orcengine_phase5b PUBLIC orcengine_phase2)
target_compile_options(orcengine_phase5b PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_tokenizer_metadata tests/test_tokenizer_metadata.cpp)
target_link_libraries(test_tokenizer_metadata PRIVATE orcengine_phase5b)
target_compile_options(test_tokenizer_metadata PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME tokenizer_metadata COMMAND test_tokenizer_metadata)
