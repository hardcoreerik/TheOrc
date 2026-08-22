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
# A3 (Section 11 frozen-engine integration proof) needs Phase 5A's
# forward_cached_step seam, so this pulls in Phase 5A instead of Phase 2
# directly -- Phase 5A already add_subdirectory's Phase 3, which
# add_subdirectory's Phase 2, which add_subdirectory's Phase 1, so every
# target Phase 5B previously got via Phase 2 is still available
# transitively; adding Phase 2 a second time directly would double-define
# its targets.
add_subdirectory(../OrcEnginePhase5A phase5a)

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

# Stage 2B: native byte mapping, BPE, and text-to-token-ID encoding.
# Requires a real TokenizerProfile (there is no synthetic-metadata path for
# encode -- Stage 1's synthetic fixture's vocabulary is deliberately not
# BPE-consistent with a byte alphabet, only with the merge-result rule it
# was built to test), so this test is registered only when
# ORCENGINE_REAL_F32_GGUF is configured, reusing the same cache variable
# Stage 1's explicit positive suite already uses.
if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5B Stage 2B encode test: REGISTERED")
    add_executable(test_encode tests/test_encode.cpp)
    target_link_libraries(test_encode PRIVATE orcengine_phase5b)
    target_include_directories(test_encode PRIVATE tests)
    target_compile_options(test_encode PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME encode COMMAND test_encode "${ORCENGINE_REAL_F32_GGUF}")
else()
    message(STATUS "Phase 5B Stage 2B encode test: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()

# A1: native token-ID decoding. Reuses the same encode_oracle_fixtures.hpp
# corpus Stage 2B's own test_encode.cpp uses -- decode is validated as
# encode's oracle-anchored inverse, no separate decode-only pinned
# artifact is required. Same real-GGUF requirement as encode (no synthetic
# metadata path for a real TokenizerProfile).
if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5B decode test: REGISTERED")
    add_executable(test_tokenizer_decode tests/test_decode.cpp)
    target_link_libraries(test_tokenizer_decode PRIVATE orcengine_phase5b)
    target_include_directories(test_tokenizer_decode PRIVATE tests)
    target_compile_options(test_tokenizer_decode PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME decode COMMAND test_tokenizer_decode "${ORCENGINE_REAL_F32_GGUF}")
else()
    message(STATUS "Phase 5B decode test: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()

# A2: streaming UTF-8 decode (Utf8StreamDecoder). Same real-GGUF
# requirement as decode/encode.
if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5B streaming decode test: REGISTERED")
    add_executable(test_streaming_decode tests/test_streaming_decode.cpp)
    target_link_libraries(test_streaming_decode PRIVATE orcengine_phase5b)
    target_compile_options(test_streaming_decode PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME streaming_decode COMMAND test_streaming_decode "${ORCENGINE_REAL_F32_GGUF}")
else()
    message(STATUS "Phase 5B streaming decode test: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()

# A3: Section 11 frozen-engine integration proof. Needs orcengine_phase5a
# (forward_cached_step), pulled in transitively via the add_subdirectory
# change above.
if(ORCENGINE_REAL_F32_GGUF)
    message(STATUS "Phase 5B frozen-engine integration test: REGISTERED")
    add_executable(test_frozen_engine_integration tests/test_frozen_engine_integration.cpp)
    target_link_libraries(test_frozen_engine_integration PRIVATE orcengine_phase5b orcengine_phase5a)
    target_compile_options(test_frozen_engine_integration PRIVATE
        $<$<CXX_COMPILER_ID:MSVC>:/W4>
        $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
    )
    add_test(NAME frozen_engine_integration COMMAND test_frozen_engine_integration "${ORCENGINE_REAL_F32_GGUF}")
    set_tests_properties(frozen_engine_integration PROPERTIES TIMEOUT 1800)
else()
    message(STATUS "Phase 5B frozen-engine integration test: SKIPPED -- ORCENGINE_REAL_F32_GGUF not configured")
endif()

# A4: minimal CLI for the native leg of the three-way tokenizer
# comparison (tools/native_tokenize_cli.cpp). Not a test target -- a
# small utility invoked manually/by a comparison script, same category
# as gguf_cached_forward.cpp elsewhere in this project.
add_executable(orcengine_native_tokenize tools/native_tokenize_cli.cpp)
target_link_libraries(orcengine_native_tokenize PRIVATE orcengine_phase5b)
target_compile_options(orcengine_native_tokenize PRIVATE
    $<$<CXX_COMPILER_ID:MSVC>:/W4>
    $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-Wall -Wextra>
)
