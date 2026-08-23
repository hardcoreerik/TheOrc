# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 6 Stage 1: initial quantization (Q8_0). See
# docs/OrcEngine/PHASE6_QUANTIZATION_SPEC.md. The Q8_0 dequantization and
# materialization-dispatch code itself lives in Phase 2's gguf.hpp/gguf.cpp
# (a backward-compatible addition to that frozen file, matching the
# Phase 5C OE-ADR-039/040 precedent) -- this directory holds Phase 6's own
# tests and, in later checkpoints, its fixture-provenance and real-model
# comparison tooling.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase6 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

add_subdirectory(../OrcEnginePhase2 phase2)

add_executable(test_q8_0_dequant tests/test_q8_0_dequant.cpp)
target_link_libraries(test_q8_0_dequant PRIVATE orcengine_phase2)
target_compile_options(test_q8_0_dequant PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME q8_0_dequant COMMAND test_q8_0_dequant)
