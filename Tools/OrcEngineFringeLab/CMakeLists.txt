# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Fringe Lab -- isolated research code. Does NOT modify any
# frozen Phase 1/2/3/4 file; reuses their public headers/contracts
# (forward_with_layer_runner, ModelSource, TensorMaterializer,
# ResidencyLedger) exactly as later production phases (Phase 5A) do.
# See docs/OrcEngine/... is NOT updated by this project -- Fringe Lab
# results are recorded under .orc/fringelab/, not the OrcEngine doc set,
# per this session's own charter (Section 18: do not convert a Fringe Lab
# observation into an accepted architectural decision).
cmake_minimum_required(VERSION 3.20)
project(OrcEngineFringeLab CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()
# Pulls in Phase 3 (which pulls in Phase 2, which pulls in Phase 1)
# transitively -- do NOT also add_subdirectory Phase 3 directly, that would
# double-define its targets. FL-07B Commit 2 needs Phase 5A's
# execute_cached_transformer_layer / forward_cached_step seam
# (orcengine_phase5a), which is why this now adds Phase 5A instead of
# Phase 3 directly.
add_subdirectory(../OrcEnginePhase5A phase5a)

if(NOT DEFINED ORCENGINE_REAL_F32_GGUF AND DEFINED ENV{ORCENGINE_REAL_F32_GGUF})
    set(ORCENGINE_REAL_F32_GGUF "$ENV{ORCENGINE_REAL_F32_GGUF}")
endif()

add_library(fringelab_residency_budget src/residency_budget.cpp)
target_include_directories(fringelab_residency_budget PUBLIC include)
target_link_libraries(fringelab_residency_budget PUBLIC orcengine_phase1 orcengine_phase3)
target_compile_options(fringelab_residency_budget PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_fl07_residency_budget tests/test_fl07_residency_budget.cpp)
target_link_libraries(test_fl07_residency_budget PRIVATE fringelab_residency_budget)
target_compile_options(test_fl07_residency_budget PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME fringelab_fl07_residency_budget COMMAND test_fl07_residency_budget
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")

add_executable(fringelab_fl07_residency_sweep tools/fl07_residency_sweep.cpp)
target_link_libraries(fringelab_fl07_residency_sweep PRIVATE fringelab_residency_budget orcengine_phase3_gguf
    $<$<PLATFORM_ID:Windows>:Psapi>)
target_compile_options(fringelab_fl07_residency_sweep PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Fringe Lab FL-07 real GGUF: REGISTERED")
    add_test(NAME fringelab_fl07_real_sweep_smoke
        COMMAND fringelab_fl07_residency_sweep "${ORCENGINE_REAL_F32_GGUF}" 1 5 --n-resident 0 --n-resident 30)
    set_tests_properties(fringelab_fl07_real_sweep_smoke PROPERTIES TIMEOUT 900)
else()
    message(STATUS "Fringe Lab FL-07 real GGUF: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()

# FL-07B -- Multi-Step Sticky-Layer Planning (research(fringelab) commit 1:
# charter + minimal planner/residency implementation + synthetic tests).
# sticky_layer_plan.cpp has NO model/materializer dependency (pure planning
# logic over LayerCostInfo descriptors) -- kept as its own tiny library so
# the planner unit tests never need a real or synthetic model source.
add_library(fringelab_sticky_layer_plan src/sticky_layer_plan.cpp)
target_include_directories(fringelab_sticky_layer_plan PUBLIC include)
target_compile_options(fringelab_sticky_layer_plan PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_library(fringelab_sticky_layer_model src/sticky_layer_model.cpp)
target_include_directories(fringelab_sticky_layer_model PUBLIC include)
target_link_libraries(fringelab_sticky_layer_model PUBLIC orcengine_phase1 orcengine_phase3
    fringelab_sticky_layer_plan)
target_compile_options(fringelab_sticky_layer_model PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_fl07b_sticky_layer tests/test_fl07b_sticky_layer.cpp)
target_link_libraries(test_fl07b_sticky_layer PRIVATE fringelab_sticky_layer_model fringelab_sticky_layer_plan)
target_compile_options(test_fl07b_sticky_layer PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME fringelab_fl07b_sticky_layer COMMAND test_fl07b_sticky_layer
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")

# FL-07B Commit 2 -- Multi-Step Cached-Decode Integration. Threads
# Phase 5A's KV-cache seam (execute_cached_transformer_layer /
# forward_cached_step, orcengine_phase5a) through StickyLayerModel's
# sticky/cold layer split across multiple step() calls against one
# persistent ContiguousAttentionKVStore.
add_library(fringelab_sticky_layer_cached_model src/sticky_layer_cached_model.cpp)
target_include_directories(fringelab_sticky_layer_cached_model PUBLIC include)
target_link_libraries(fringelab_sticky_layer_cached_model PUBLIC orcengine_phase1 orcengine_phase3 orcengine_phase5a
    fringelab_sticky_layer_plan fringelab_sticky_layer_model)
target_compile_options(fringelab_sticky_layer_cached_model PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_fl07b_cached_decode tests/test_fl07b_cached_decode.cpp)
target_link_libraries(test_fl07b_cached_decode PRIVATE fringelab_sticky_layer_cached_model
    fringelab_sticky_layer_model fringelab_sticky_layer_plan orcengine_phase5a)
target_compile_options(test_fl07b_cached_decode PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME fringelab_fl07b_cached_decode COMMAND test_fl07b_cached_decode
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
