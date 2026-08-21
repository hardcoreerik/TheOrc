# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 5A: KV-cached incremental decode reference. Extends
# frozen Phase-1 operators with a cached forward-pass variant, proven
# against an independently-computed Python trace on synthetic Fixture C.
# See docs/OrcEngine/PHASE5A_KV_CACHE_SPEC.md.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase5A CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

# Real-artifact configuration, normalized (P5A-RVW-006 closure; ordering fix
# 2026-08-19, confirmed by adversary review). This MUST run BEFORE
# add_subdirectory(Phase 3) below: Phase 3 pulls in Phase 2, and Phase 2's
# own CMakeLists.txt unconditionally declares these same three names as
# CACHE variables with an empty default (`set(ORCENGINE_REAL_F32_GGUF ""
# CACHE FILEPATH ...)`, etc.). A CACHE `set()` without FORCE is a no-op if
# the cache entry already exists -- so populating the cache entry ourselves
# FIRST (from an explicit -D, or as a fallback from the environment) makes
# Phase 2's own empty-default declaration a no-op instead of the reverse.
# The previous placement of this block AFTER add_subdirectory meant Phase
# 2's empty-default entry always existed first, so `NOT DEFINED` here was
# always false (DEFINED does not distinguish an empty cache entry from a
# meaningfully configured one) and the environment fallback could never
# fire -- silently dropping `real_cache_attacks` and every inherited
# Phase 2/3 real-artifact test whenever only the environment variable, not
# `-D`, was set.
#
# Precedence: an explicit `-D` on the command line creates its cache entry
# before ANY CMakeLists.txt runs, so it is already DEFINED here and the
# environment fallback below is skipped for it (explicit wins). The
# environment variable is used only when no `-D` was given. Leaving both
# unset preserves the original empty-default behavior via Phase 2's own
# declaration once add_subdirectory runs.
if(NOT DEFINED ORCENGINE_REAL_F32_GGUF AND DEFINED ENV{ORCENGINE_REAL_F32_GGUF})
    set(ORCENGINE_REAL_F32_GGUF "$ENV{ORCENGINE_REAL_F32_GGUF}" CACHE FILEPATH
        "Optional real F32 GGUF for frozen-core differential test (from environment)")
endif()
if(NOT DEFINED ORCENGINE_REAL_TIED_F32_GGUF AND DEFINED ENV{ORCENGINE_REAL_TIED_F32_GGUF})
    set(ORCENGINE_REAL_TIED_F32_GGUF "$ENV{ORCENGINE_REAL_TIED_F32_GGUF}" CACHE FILEPATH
        "Optional real tied F32 GGUF for output-head equivalence (from environment)")
endif()
if(NOT DEFINED ORCENGINE_HF_SOURCE_DIR AND DEFINED ENV{ORCENGINE_HF_SOURCE_DIR})
    set(ORCENGINE_HF_SOURCE_DIR "$ENV{ORCENGINE_HF_SOURCE_DIR}" CACHE PATH
        "Optional original Hugging Face source for direct PyTorch differential (from environment)")
endif()

# Pull in Phase 3 (which itself pulls in Phase 2, which pulls in Phase 1) so
# both the real-model driver (Reference Path B, fully-resident) and the
# virtualized composed path (Reference Path C) can be built from one
# subdirectory add -- do NOT also add_subdirectory Phase 1/2 directly here,
# that would double-define their targets.
add_subdirectory(../OrcEnginePhase3 phase3)

# Phase 3's own find_package(Python3 ...) result (Python3_EXECUTABLE) is a
# normal variable scoped to Phase 3's own CMakeLists.txt, not visible here
# via add_subdirectory alone -- re-resolve it in this directory's own scope
# for the real 5-way differential registration below.
find_package(Python3 REQUIRED COMPONENTS Interpreter)

add_library(orcengine_phase5a
    src/forward_cached.cpp
    src/forward_cached_virtualized.cpp
    src/cache_trace_loader.cpp
)
target_include_directories(orcengine_phase5a PUBLIC include)
target_link_libraries(orcengine_phase5a PUBLIC orcengine_phase1 orcengine_phase3)
target_compile_options(orcengine_phase5a PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_cached_decode tests/test_cached_decode.cpp)
target_link_libraries(test_cached_decode PRIVATE orcengine_phase5a)
target_compile_options(test_cached_decode PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_test(NAME cached_decode COMMAND test_cached_decode
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

if(ORCENGINE_REAL_F32_GGUF)
    add_test(NAME real_cache_attacks COMMAND test_real_cache_attacks "${ORCENGINE_REAL_F32_GGUF}")
endif()

add_executable(test_cache_position_safety tests/test_cache_position_safety.cpp)
target_link_libraries(test_cache_position_safety PRIVATE orcengine_phase5a)
target_compile_options(test_cache_position_safety PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME cache_position_safety COMMAND test_cache_position_safety
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(test_transactional_semantics tests/test_transactional_semantics.cpp)
target_link_libraries(test_transactional_semantics PRIVATE orcengine_phase5a)
target_compile_options(test_transactional_semantics PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME transactional_semantics COMMAND test_transactional_semantics
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(test_prefill_schedule_equivalence tests/test_prefill_schedule_equivalence.cpp)
target_link_libraries(test_prefill_schedule_equivalence PRIVATE orcengine_phase5a)
target_compile_options(test_prefill_schedule_equivalence PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME prefill_schedule_equivalence COMMAND test_prefill_schedule_equivalence
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(test_virtualized_cached_decode tests/test_virtualized_cached_decode.cpp)
target_link_libraries(test_virtualized_cached_decode PRIVATE orcengine_phase5a)
target_compile_options(test_virtualized_cached_decode PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME virtualized_cached_decode COMMAND test_virtualized_cached_decode
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(test_transactional_semantics_virtualized tests/test_transactional_semantics_virtualized.cpp)
target_link_libraries(test_transactional_semantics_virtualized PRIVATE orcengine_phase5a)
target_compile_options(test_transactional_semantics_virtualized PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME transactional_semantics_virtualized COMMAND test_transactional_semantics_virtualized
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(test_virtualized_cache_attacks tests/test_virtualized_cache_attacks.cpp)
target_link_libraries(test_virtualized_cache_attacks PRIVATE orcengine_phase5a)
target_compile_options(test_virtualized_cache_attacks PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME virtualized_cache_attacks COMMAND test_virtualized_cache_attacks
         "${CMAKE_CURRENT_SOURCE_DIR}/../OrcEnginePhase0/fixtures_phase5a")

add_executable(orcengine_gguf_cached_forward tools/gguf_cached_forward.cpp)
target_link_libraries(orcengine_gguf_cached_forward PRIVATE orcengine_phase5a orcengine_phase2
    $<$<PLATFORM_ID:Windows>:Psapi>)
target_compile_options(orcengine_gguf_cached_forward PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(orcengine_gguf_cached_forward_virtualized tools/gguf_cached_forward_virtualized.cpp)
target_link_libraries(orcengine_gguf_cached_forward_virtualized PRIVATE orcengine_phase5a orcengine_phase3_gguf
    $<$<PLATFORM_ID:Windows>:Psapi>)
target_compile_options(orcengine_gguf_cached_forward_virtualized PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_real_cache_attacks tests/test_real_cache_attacks.cpp)
target_link_libraries(test_real_cache_attacks PRIVATE orcengine_phase5a orcengine_phase2)
target_compile_options(test_real_cache_attacks PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

# P5A-RVW-006 closure: the first REAL-GGUF-backed Path-C evidence actually
# registered as a CTest, so it runs under Debug/Release/strict/ASan like
# every other lane instead of only being exercised manually. Registered
# once for the explicit-output artifact and once for the tied artifact
# (Stage 7 closure) -- both share the same binary, only the model path differs.
add_executable(test_real_composed_evidence tests/test_real_composed_evidence.cpp)
target_link_libraries(test_real_composed_evidence PRIVATE orcengine_phase5a orcengine_phase3_gguf)
target_compile_options(test_real_composed_evidence PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
# TIMEOUT 3600, not 900: under MSVC ASan this test's real-model compute
# (three full forward passes -- A/B/C -- across 4 steps, plus fault attacks
# and a prefill-schedule comparison) measured a ~40-50x slowdown relative
# to Release (real_cache_attacks alone: 9s Release vs 430s ASan), and this
# test does materially more real-model work than real_cache_attacks. 900s
# was observed to time out under ASan; 3600s was not exercised to its
# limit in the run that motivated this change, chosen with headroom.
if(ORCENGINE_REAL_F32_GGUF)
    add_test(NAME real_composed_evidence_explicit COMMAND test_real_composed_evidence "${ORCENGINE_REAL_F32_GGUF}")
    set_tests_properties(real_composed_evidence_explicit PROPERTIES TIMEOUT 3600)
endif()
if(ORCENGINE_REAL_TIED_F32_GGUF)
    add_test(NAME real_composed_evidence_tied COMMAND test_real_composed_evidence "${ORCENGINE_REAL_TIED_F32_GGUF}")
    set_tests_properties(real_composed_evidence_tied PROPERTIES TIMEOUT 3600)
endif()

# The real 5-way differential (adds HF/PyTorch legs D and E) -- registered
# only when the real artifact, an HF source directory, and a Python
# interpreter are all configured (Stage 5 closure). Also invokes an
# ASan-instrumented executable as a subprocess under the ASan lane, so
# gets the same generous timeout for the same reason as above.
if(ORCENGINE_REAL_F32_GGUF AND ORCENGINE_HF_SOURCE_DIR AND Python3_EXECUTABLE)
    add_test(NAME real_5way_composed_differential
        COMMAND ${Python3_EXECUTABLE}
                "${CMAKE_CURRENT_SOURCE_DIR}/tests/real_5way_composed_differential.py"
                "$<TARGET_FILE:orcengine_gguf_cached_forward_virtualized>"
                "${ORCENGINE_REAL_F32_GGUF}" "${ORCENGINE_HF_SOURCE_DIR}" 4)
    set_tests_properties(real_5way_composed_differential PROPERTIES TIMEOUT 3600)
endif()
