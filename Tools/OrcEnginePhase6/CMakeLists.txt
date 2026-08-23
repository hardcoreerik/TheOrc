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

# Real-artifact configuration, populated BEFORE add_subdirectory below --
# matching Phase 5A's own documented P5A-RVW-006 fix (see that file's
# CMakeLists.txt for the full explanation). Phase 2's own CMakeLists.txt
# unconditionally declares ORCENGINE_REAL_F32_GGUF as a CACHE variable
# with an empty default; a CACHE set() without FORCE is a no-op if the
# cache entry already exists, so this block must claim the cache slot
# FIRST (from the environment, as a fallback if no explicit -D was
# given) or Phase 2's empty-default declaration wins instead and the
# environment variable is silently ignored -- exactly the bug P5A-RVW-006
# fixed elsewhere in this project; reproduced identically here for
# ORCENGINE_REAL_Q8_0_GGUF, which is new to Phase 6 and has no such
# pre-existing declaration to race against, but is populated the same
# way for consistency and because Q8 evidence always needs BOTH.
if(NOT DEFINED ORCENGINE_REAL_F32_GGUF AND DEFINED ENV{ORCENGINE_REAL_F32_GGUF})
    set(ORCENGINE_REAL_F32_GGUF "$ENV{ORCENGINE_REAL_F32_GGUF}" CACHE FILEPATH
        "Optional real F32 GGUF for Phase 6 Q8_0 comparison (from environment)")
endif()
if(NOT DEFINED ORCENGINE_REAL_Q8_0_GGUF AND DEFINED ENV{ORCENGINE_REAL_Q8_0_GGUF})
    set(ORCENGINE_REAL_Q8_0_GGUF "$ENV{ORCENGINE_REAL_Q8_0_GGUF}" CACHE FILEPATH
        "Optional locally-quantized Q8_0 GGUF, see fixtures/Q8_0_FIXTURE_PROVENANCE.md (from environment)")
endif()

add_subdirectory(../OrcEnginePhase2 phase2)

add_executable(test_q8_0_dequant tests/test_q8_0_dequant.cpp)
target_link_libraries(test_q8_0_dequant PRIVATE orcengine_phase2)
target_compile_options(test_q8_0_dequant PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME q8_0_dequant COMMAND test_q8_0_dequant)

# Checkpoint 2: real Q8_0 fixture and mixed-format loading. Requires both
# the pinned F32 GGUF and the locally-produced Q8_0 fixture (see
# fixtures/Q8_0_FIXTURE_PROVENANCE.md for exact regeneration steps) --
# neither is committed (*.gguf is gitignored), so this target is only
# registered when both are configured, matching every other real-artifact
# test in this project.
if(ORCENGINE_REAL_F32_GGUF AND ORCENGINE_REAL_Q8_0_GGUF)
    message(STATUS "Phase 6 Checkpoint 2 real-fixture evidence: REGISTERED")
    add_executable(test_q8_0_real_fixture tests/test_q8_0_real_fixture.cpp)
    target_link_libraries(test_q8_0_real_fixture PRIVATE orcengine_phase2)
    target_compile_options(test_q8_0_real_fixture PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME q8_0_real_fixture COMMAND test_q8_0_real_fixture
             "${ORCENGINE_REAL_F32_GGUF}" "${ORCENGINE_REAL_Q8_0_GGUF}")
    set_tests_properties(q8_0_real_fixture PROPERTIES TIMEOUT 300)
else()
    message(STATUS "Phase 6 Checkpoint 2 real-fixture evidence: SKIPPED -- "
                   "ORCENGINE_REAL_F32_GGUF and/or ORCENGINE_REAL_Q8_0_GGUF not configured")
endif()
