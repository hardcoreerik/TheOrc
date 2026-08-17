# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase3 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Python3 REQUIRED COMPONENTS Interpreter)
enable_testing()
add_subdirectory(../OrcEnginePhase2 phase2)

add_library(orcengine_phase3 src/streaming.cpp)
target_include_directories(orcengine_phase3 PUBLIC include)
target_link_libraries(orcengine_phase3 PUBLIC orcengine_phase1)
target_compile_options(orcengine_phase3 PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_library(orcengine_phase3_gguf src/gguf_source.cpp)
target_include_directories(orcengine_phase3_gguf PUBLIC include)
target_link_libraries(orcengine_phase3_gguf PUBLIC orcengine_phase3 orcengine_phase2)
target_compile_options(orcengine_phase3_gguf PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(orcengine_gguf_streaming_forward tools/streaming_forward.cpp)
target_link_libraries(orcengine_gguf_streaming_forward PRIVATE orcengine_phase3_gguf)

add_executable(test_streaming tests/test_streaming.cpp)
target_link_libraries(test_streaming PRIVATE orcengine_phase3_gguf)
add_dependencies(test_streaming generate_phase2_fixtures)
add_test(NAME streaming_working_set COMMAND test_streaming
         "${CMAKE_CURRENT_BINARY_DIR}/phase2/fixtures"
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")

add_executable(orcengine_phase1_snapshot_current tools/phase1_snapshot.cpp)
target_link_libraries(orcengine_phase1_snapshot_current PRIVATE orcengine_phase1)

if(ORCENGINE_FROZEN_PHASE1_SNAPSHOT)
    add_test(NAME phase1_frozen_cross_differential
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/cross_freeze_check.py"
                "${ORCENGINE_FROZEN_PHASE1_SNAPSHOT}"
                "$<TARGET_FILE:orcengine_phase1_snapshot_current>"
                "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
endif()

if(ORCENGINE_REAL_F32_GGUF)
    add_test(NAME streaming_real_explicit
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_streaming_check.py"
                "$<TARGET_FILE:orcengine_gguf_forward>"
                "$<TARGET_FILE:orcengine_gguf_streaming_forward>"
                "${ORCENGINE_REAL_F32_GGUF}" "$<IF:$<CONFIG:Release>,4,1>" 240655104)
    set_tests_properties(streaming_real_explicit PROPERTIES TIMEOUT 900)
    add_test(NAME streaming_real_explicit_budget
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_budget_check.py"
                "$<TARGET_FILE:orcengine_gguf_forward>"
                "$<TARGET_FILE:orcengine_gguf_streaming_forward>"
                "${ORCENGINE_REAL_F32_GGUF}" 240655104)
    set_tests_properties(streaming_real_explicit_budget PROPERTIES TIMEOUT 900)
endif()

if(ORCENGINE_REAL_TIED_F32_GGUF)
    add_test(NAME streaming_real_tied
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_streaming_check.py"
                "$<TARGET_FILE:orcengine_gguf_forward>"
                "$<TARGET_FILE:orcengine_gguf_streaming_forward>"
                "${ORCENGINE_REAL_TIED_F32_GGUF}" "$<IF:$<CONFIG:Release>,4,1>" 127408896)
    set_tests_properties(streaming_real_tied PROPERTIES TIMEOUT 900)
    add_test(NAME streaming_real_tied_budget
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_budget_check.py"
                "$<TARGET_FILE:orcengine_gguf_forward>"
                "$<TARGET_FILE:orcengine_gguf_streaming_forward>"
                "${ORCENGINE_REAL_TIED_F32_GGUF}" 127408896)
    set_tests_properties(streaming_real_tied_budget PROPERTIES TIMEOUT 900)
endif()

if(ORCENGINE_REAL_F32_GGUF AND ORCENGINE_HF_SOURCE_DIR)
    add_test(NAME streaming_real_hf_pytorch
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase2/tests/hf_pytorch_forward_check.py"
                "$<TARGET_FILE:orcengine_gguf_streaming_forward>" "${ORCENGINE_REAL_F32_GGUF}"
                "${ORCENGINE_HF_SOURCE_DIR}" "$<IF:$<CONFIG:Release>,4,1>")
    set_tests_properties(streaming_real_hf_pytorch PROPERTIES TIMEOUT 900)
endif()
