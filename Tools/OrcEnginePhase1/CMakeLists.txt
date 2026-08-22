# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 1: a tiny, boring, F32-only, CPU-only C++ reference core,
# provable against the Python oracle in Tools/OrcEnginePhase0/oracle/. No
# CUDA, no quantization, no graph compiler, no production integration -- see
# docs/OrcEngine/PHASE1_IMPLEMENTATION.md.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase1 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE)
    set(CMAKE_BUILD_TYPE Debug)
endif()

function(add_orcengine_variant target_suffix use_f64)
    set(lib_name orcengine_phase1${target_suffix})
    add_library(${lib_name}
        src/ops.cpp
        src/forward.cpp
        src/fixture_loader.cpp
        src/validation.cpp
        src/materialization.cpp
        src/activation_workspace.cpp
    )
    target_include_directories(${lib_name} PUBLIC include)
    target_compile_options(${lib_name} PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    if(use_f64)
        target_compile_definitions(${lib_name} PUBLIC ORCENGINE_ACCUM_F64)
    endif()

    add_executable(test_gates${target_suffix} tests/test_gates.cpp)
    target_link_libraries(test_gates${target_suffix} PRIVATE ${lib_name})

    add_executable(test_decode${target_suffix} tests/test_decode.cpp)
    target_link_libraries(test_decode${target_suffix} PRIVATE ${lib_name})

    add_executable(test_metamorphic${target_suffix} tests/test_metamorphic.cpp)
    target_link_libraries(test_metamorphic${target_suffix} PRIVATE ${lib_name})
endfunction()

# Default (Phase-1 spec) variant: F32 storage/compute/accumulate throughout.
add_orcengine_variant("" FALSE)
# A/B comparison variant only: F64 accumulation, F32 storage/compute otherwise.
# Exists purely to answer the freeze-audit's accumulation question -- the F32
# variant above is the one Phase 1 actually ships.
add_orcengine_variant("_f64accum" TRUE)

add_executable(test_regressions tests/test_regressions.cpp)
target_link_libraries(test_regressions PRIVATE orcengine_phase1)

enable_testing()
add_test(NAME differential_gates
         COMMAND test_gates "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME autoregressive_decode
         COMMAND test_decode "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME metamorphic_residency
         COMMAND test_metamorphic "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME differential_gates_f64
         COMMAND test_gates_f64accum "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME autoregressive_decode_f64
         COMMAND test_decode_f64accum "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME metamorphic_residency_f64
         COMMAND test_metamorphic_f64accum "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME hardening_regressions
         COMMAND test_regressions "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
