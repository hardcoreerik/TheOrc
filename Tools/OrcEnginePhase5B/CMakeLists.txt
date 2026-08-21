# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# OrcEngine Phase 5B Stage 1: native GGUF tokenizer-metadata construction
# and fail-closed validation. See docs/OrcEngine/PHASE5B_TOKENIZER_SPEC.md
# and DECISION_LOG.md OE-ADR-030. Reuses the frozen Phase 2 GGUF reader
# through its existing public target -- does NOT modify Phase 1/2/3/4/5A.
cmake_minimum_required(VERSION 3.20)
project(OrcEnginePhase5B CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

enable_testing()

# Phase 2 already add_subdirectory's Phase 1 itself -- do not also add it
# here, that would double-define its targets (same pattern Phase 3/5A use).
add_subdirectory(../OrcEnginePhase2 phase2)

add_library(orcengine_phase5b
    src/tokenizer.cpp
    src/pretokenize.cpp
)
target_include_directories(orcengine_phase5b PUBLIC include)
target_link_libraries(orcengine_phase5b PUBLIC orcengine_phase2)
target_compile_options(orcengine_phase5b PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)

add_executable(test_tokenizer_metadata tests/test_tokenizer_metadata.cpp)
target_link_libraries(test_tokenizer_metadata PRIVATE orcengine_phase5b)
target_compile_options(test_tokenizer_metadata PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME tokenizer_metadata COMMAND test_tokenizer_metadata)

# Real-artifact tests (OE-ADR-031, green-lane closure pass): two
# INDEPENDENT contracts, each reusing the SAME ORCENGINE_REAL_F32_GGUF /
# ORCENGINE_REAL_TIED_F32_GGUF cache variables Phase 2's own CMakeLists.txt
# already declares (visible here because add_subdirectory(phase2) above has
# already run) -- matching the existing OrcEngine convention of reusing one
# variable name across phases rather than inventing Phase-5B-specific names
# or baking in machine-specific absolute paths. Neither test requires the
# OTHER variable to be configured: the explicit positive suite and the
# legacy tied rejection suite are unrelated contracts (per OE-ADR-031,
# explicit/tied identity is no longer a Phase 5B requirement).
if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5B Stage 1 explicit real-artifact test: REGISTERED")
    add_test(NAME tokenizer_metadata_real_explicit
        COMMAND test_tokenizer_metadata --real-explicit "${ORCENGINE_REAL_F32_GGUF}")
else()
    message(STATUS "Phase 5B Stage 1 explicit real-artifact test: SKIPPED -- "
                    "ORCENGINE_REAL_F32_GGUF not configured")
endif()

if(ORCENGINE_REAL_TIED_F32_GGUF)
    message(STATUS "Phase 5B Stage 1 legacy tied-artifact rejection test: REGISTERED")
    add_test(NAME tokenizer_metadata_legacy_tied_rejection
        COMMAND test_tokenizer_metadata --expect-missing-tokenizer "${ORCENGINE_REAL_TIED_F32_GGUF}")
else()
    message(STATUS "Phase 5B Stage 1 legacy tied-artifact rejection test: SKIPPED -- "
                    "ORCENGINE_REAL_TIED_F32_GGUF not configured")
endif()

# Stage 2A: exact native pretokenization (Digits->ByteLevel boundary proof
# against the tokenizers==0.22.2 oracle). Separate test executable from
# test_tokenizer_metadata -- pretokenization is a distinct concern from
# metadata construction/validation, and mixing the two contracts into one
# executable would make both harder to reason about. No real-GGUF cache
# variable needed: the oracle fixtures are pre-computed, committed data
# (tests/pretok_oracle_fixtures.hpp), not a live artifact load.
add_executable(test_pretokenize tests/test_pretokenize.cpp)
target_link_libraries(test_pretokenize PRIVATE orcengine_phase5b)
target_include_directories(test_pretokenize PRIVATE tests)
target_compile_options(test_pretokenize PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
add_test(NAME pretokenize COMMAND test_pretokenize)
