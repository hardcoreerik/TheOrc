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
add_subdirectory(../OrcEnginePhase3 phase3)

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
