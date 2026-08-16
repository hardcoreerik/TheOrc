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

add_library(orcengine_phase1
    src/ops.cpp
    src/forward.cpp
    src/fixture_loader.cpp
)
target_include_directories(orcengine_phase1 PUBLIC include)
target_compile_options(orcengine_phase1 PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_gates tests/test_gates.cpp)
target_link_libraries(test_gates PRIVATE orcengine_phase1)

enable_testing()
add_test(NAME differential_gates
         COMMAND test_gates "${CMAKE_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
