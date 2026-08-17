# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase4 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()
add_subdirectory(../OrcEnginePhase3 phase3)

add_executable(test_bookend_virtualization tests/test_bookend_virtualization.cpp)
target_link_libraries(test_bookend_virtualization PRIVATE orcengine_phase3_gguf)
target_compile_options(test_bookend_virtualization PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_dependencies(test_bookend_virtualization generate_phase2_fixtures)
add_test(NAME bookend_virtualization COMMAND test_bookend_virtualization
         "${CMAKE_CURRENT_BINARY_DIR}/phase3/phase2/fixtures"
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
