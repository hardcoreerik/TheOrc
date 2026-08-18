# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase2 CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

find_package(Python3 REQUIRED COMPONENTS Interpreter)
enable_testing()
add_subdirectory(../OrcEnginePhase1 phase1)

add_library(orcengine_phase2
    src/gguf.cpp
)
target_include_directories(orcengine_phase2 PUBLIC include)
target_link_libraries(orcengine_phase2 PUBLIC orcengine_phase1)
target_compile_options(orcengine_phase2 PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

set(PHASE2_FIXTURES "${CMAKE_CURRENT_BINARY_DIR}/fixtures")
add_custom_command(
    OUTPUT "${PHASE2_FIXTURES}/.generated"
    COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/generate_fixtures.py" "${PHASE2_FIXTURES}"
    DEPENDS
        tests/generate_fixtures.py
        ../OrcEnginePhase0/phase2_prep/raw_gguf_writer.py
        ../OrcEnginePhase0/phase2_prep/generate_malformed_fixtures.py
        ../OrcEnginePhase0/oracle/weights.py
    VERBATIM
)
add_custom_target(generate_phase2_fixtures DEPENDS "${PHASE2_FIXTURES}/.generated")

add_executable(orcengine_gguf_inspect tools/gguf_inspect.cpp)
target_link_libraries(orcengine_gguf_inspect PRIVATE orcengine_phase2)

add_executable(orcengine_gguf_forward tools/gguf_forward.cpp)
target_link_libraries(orcengine_gguf_forward PRIVATE orcengine_phase2)

add_executable(test_gguf tests/test_gguf.cpp)
target_link_libraries(test_gguf PRIVATE orcengine_phase2)
add_dependencies(test_gguf generate_phase2_fixtures)

add_executable(test_gguf_mutations tests/test_gguf_mutations.cpp)
target_link_libraries(test_gguf_mutations PRIVATE orcengine_phase2)
add_dependencies(test_gguf_mutations generate_phase2_fixtures)

add_executable(test_gguf_large_sparse tests/test_gguf_large_sparse.cpp)
target_link_libraries(test_gguf_large_sparse PRIVATE orcengine_phase2)

add_test(NAME gguf_conformance COMMAND test_gguf "${PHASE2_FIXTURES}"
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase1")
add_test(NAME gguf_mutations COMMAND test_gguf_mutations "${PHASE2_FIXTURES}/model_untied.gguf")
add_test(NAME gguf_large_sparse COMMAND test_gguf_large_sparse)

set(ORCENGINE_REAL_GGUF "" CACHE FILEPATH "Optional real GGUF for cross-reader indexing test")
if(ORCENGINE_REAL_GGUF)
    add_test(NAME gguf_real_cross_reader
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/cross_reader_check.py"
                "$<TARGET_FILE:orcengine_gguf_inspect>" "${ORCENGINE_REAL_GGUF}")
endif()

set(ORCENGINE_REAL_F32_GGUF "" CACHE FILEPATH "Optional real F32 GGUF for frozen-core differential test")
set(ORCENGINE_HF_SOURCE_DIR "" CACHE PATH "Optional original Hugging Face source for direct PyTorch differential")
if(ORCENGINE_REAL_F32_GGUF)
    # ORCENGINE_HF_SOURCE_DIR is optional here too: real_forward_check.py's
    # load_real_weights() falls back to the in-repo SOURCE_DIR default when
    # no explicit HF source directory is passed, matching its historical
    # behavior. Passing it explicitly lets this test load the pinned
    # candidate's weights from ANY worktree/location, not just one whose
    # Tools/OrcEnginePhase0/artifacts/smollm2-135m happens to be populated --
    # see docs/OrcEngine/DECISION_LOG.md OE-ADR-023 for why this was added.
    if(ORCENGINE_HF_SOURCE_DIR)
        add_test(NAME gguf_real_f32_forward
            COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_forward_check.py"
                    "$<TARGET_FILE:orcengine_gguf_forward>" "${ORCENGINE_REAL_F32_GGUF}"
                    "$<IF:$<CONFIG:Release>,4,1>" "${ORCENGINE_HF_SOURCE_DIR}")
    else()
        add_test(NAME gguf_real_f32_forward
            COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_forward_check.py"
                    "$<TARGET_FILE:orcengine_gguf_forward>" "${ORCENGINE_REAL_F32_GGUF}"
                    "$<IF:$<CONFIG:Release>,4,1>")
    endif()
    set_tests_properties(gguf_real_f32_forward PROPERTIES TIMEOUT 300)
endif()

if(ORCENGINE_REAL_F32_GGUF AND ORCENGINE_HF_SOURCE_DIR)
    add_test(NAME gguf_real_hf_pytorch_forward
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/hf_pytorch_forward_check.py"
                "$<TARGET_FILE:orcengine_gguf_forward>" "${ORCENGINE_REAL_F32_GGUF}"
                "${ORCENGINE_HF_SOURCE_DIR}" "$<IF:$<CONFIG:Release>,4,1>")
    set_tests_properties(gguf_real_hf_pytorch_forward PROPERTIES TIMEOUT 600)
endif()

set(ORCENGINE_REAL_TIED_F32_GGUF "" CACHE FILEPATH "Optional real tied F32 GGUF for output-head equivalence")
if(ORCENGINE_REAL_F32_GGUF AND ORCENGINE_REAL_TIED_F32_GGUF)
    add_test(NAME gguf_real_tied_forward
        COMMAND ${Python3_EXECUTABLE} "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_tied_forward_check.py"
                "$<TARGET_FILE:orcengine_gguf_inspect>" "$<TARGET_FILE:orcengine_gguf_forward>"
                "${ORCENGINE_REAL_F32_GGUF}" "${ORCENGINE_REAL_TIED_F32_GGUF}"
                "$<IF:$<CONFIG:Release>,4,1>")
    set_tests_properties(gguf_real_tied_forward PROPERTIES TIMEOUT 600)
endif()
