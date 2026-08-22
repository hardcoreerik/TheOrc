# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 5C Stage 1: bounded, reusable CPU F32 activation
# workspace for the cached-decode transformer-layer execution flow. See
# docs/OrcEngine/PHASE5C_ACTIVATION_WORKSPACE_SPEC.md.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase5C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

# Phase 5A already pulls in Phase 3 -> Phase 2 -> Phase 1 transitively --
# do not also add_subdirectory those directly, that would double-define
# their targets (same pattern every later phase in this project uses).
add_subdirectory(../OrcEnginePhase5A phase5a)

if(NOT DEFINED ORCENGINE_REAL_F32_GGUF AND DEFINED ENV{ORCENGINE_REAL_F32_GGUF})
    set(ORCENGINE_REAL_F32_GGUF "$ENV{ORCENGINE_REAL_F32_GGUF}")
endif()

add_library(orcengine_phase5c src/forward_cached_workspace.cpp)
target_include_directories(orcengine_phase5c PUBLIC include)
target_link_libraries(orcengine_phase5c PUBLIC orcengine_phase1 orcengine_phase5a)
target_compile_options(orcengine_phase5c PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_activation_workspace tests/test_activation_workspace.cpp)
target_link_libraries(test_activation_workspace PRIVATE orcengine_phase5c)
target_compile_options(test_activation_workspace PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME activation_workspace COMMAND test_activation_workspace
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5C real-GGUF workspace evidence: REGISTERED")
    add_executable(test_activation_workspace_real tests/test_activation_workspace_real.cpp)
    target_link_libraries(test_activation_workspace_real PRIVATE orcengine_phase5c orcengine_phase2)
    target_compile_options(test_activation_workspace_real PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME activation_workspace_real COMMAND test_activation_workspace_real "${ORCENGINE_REAL_F32_GGUF}")
    set_tests_properties(activation_workspace_real PROPERTIES TIMEOUT 900)

    add_executable(phase5c_workspace_timing tools/phase5c_workspace_timing.cpp)
    target_link_libraries(phase5c_workspace_timing PRIVATE orcengine_phase5c orcengine_phase2)
    target_compile_options(phase5c_workspace_timing PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
else()
    message(STATUS "Phase 5C real-GGUF workspace evidence: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()
