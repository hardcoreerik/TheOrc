// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// GENERATED FILE -- do not hand-edit. Produced by
// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.
//
// Oracle pretokenization boundary fixtures: byte-offset [begin,end) spans
// computed from tokenizers==0.22.2's real Sequence(Digits(individual_digits
// =true), ByteLevel(add_prefix_space=false, trim_offsets=true, use_regex=
// true)) pretokenizer, loaded from the pinned smollm2-135m/tokenizer.json.
// Codepoint offsets from pre_tokenize_str() were converted to UTF-8 byte
// offsets by summing each preceding codepoint's UTF-8 encoded length
// (deterministic, no oracle dependency for that conversion). Every entry
// was computed twice and asserted identical before being written here.
//   corpus size           : 63
#pragma once

#include <cstddef>
#include <string_view>

namespace orcengine::pretok_fixtures {

struct ByteSpan { std::size_t begin; std::size_t end; };

struct OracleFixture {
    const char* id;
    std::string_view utf8;
    const ByteSpan* spans;
    std::size_t span_count;
};

inline constexpr ByteSpan* kSpans_0 = nullptr;
inline constexpr ByteSpan kSpans_1[] = {{0,5}, {5,6}, {6,12}, {12,13}, {13,18}, {18,21}, {21,23}, {23,28}, {28,29}, {29,30}, {30,31}, {31,32}, {32,33}, {33,34}};
inline constexpr ByteSpan kSpans_2[] = {{0,29}};
inline constexpr ByteSpan kSpans_3[] = {{0,2}, {2,10}, {10,17}};
inline constexpr ByteSpan kSpans_4[] = {{0,8}, {8,15}, {15,18}};
inline constexpr ByteSpan kSpans_5[] = {{0,1}, {1,4}, {4,6}, {6,10}, {10,12}};
inline constexpr ByteSpan kSpans_6[] = {{0,1}, {1,2}, {2,3}, {3,4}, {4,5}};
inline constexpr ByteSpan kSpans_7[] = {{0,4}, {4,8}, {8,9}, {9,13}, {13,17}};
inline constexpr ByteSpan kSpans_8[] = {{0,4}, {4,8}, {8,9}, {9,10}, {10,14}, {14,18}};
inline constexpr ByteSpan kSpans_9[] = {{0,7}};
inline constexpr ByteSpan kSpans_10[] = {{0,3}, {3,5}, {5,9}, {9,11}, {11,13}, {13,16}, {16,18}, {18,21}, {21,23}, {23,25}, {25,27}, {27,29}, {29,32}, {32,35}, {35,39}, {39,41}};
inline constexpr ByteSpan kSpans_11[] = {{0,1}, {1,2}, {2,3}};
inline constexpr ByteSpan kSpans_12[] = {{0,1}, {1,2}, {2,3}, {3,4}};
inline constexpr ByteSpan kSpans_13[] = {{0,3}, {3,8}, {8,9}, {9,10}, {10,11}, {11,12}, {12,13}, {13,14}, {14,15}, {15,16}, {16,17}, {17,21}, {21,26}, {26,27}, {27,28}, {28,29}, {29,30}, {30,31}, {31,32}, {32,33}, {33,34}, {34,35}, {35,36}, {36,37}, {37,38}, {38,39}, {39,40}};
inline constexpr ByteSpan kSpans_14[] = {{0,3}, {3,4}, {4,5}, {5,6}, {6,9}, {9,10}, {10,11}, {11,12}};
inline constexpr ByteSpan kSpans_15[] = {{0,1}, {1,2}, {2,3}, {3,6}};
inline constexpr ByteSpan kSpans_16[] = {{0,3}, {3,4}, {4,5}, {5,6}};
inline constexpr ByteSpan kSpans_17[] = {{0,3}, {3,6}, {6,9}};
inline constexpr ByteSpan kSpans_18[] = {{0,3}, {3,9}};
inline constexpr ByteSpan kSpans_19[] = {{0,2}, {2,6}};
inline constexpr ByteSpan kSpans_20[] = {{0,3}, {3,6}};
inline constexpr ByteSpan kSpans_21[] = {{0,2}, {2,4}, {4,6}};
inline constexpr ByteSpan kSpans_22[] = {{0,3}, {3,6}, {6,9}};
inline constexpr ByteSpan kSpans_23[] = {{0,2}, {2,4}};
inline constexpr ByteSpan kSpans_24[] = {{0,3}, {3,6}, {6,9}};
inline constexpr ByteSpan kSpans_25[] = {{0,5}, {5,14}, {14,21}, {21,29}};
inline constexpr ByteSpan kSpans_26[] = {{0,12}, {12,28}, {28,38}};
inline constexpr ByteSpan kSpans_27[] = {{0,5}, {5,10}, {10,16}, {16,25}};
inline constexpr ByteSpan kSpans_28[] = {{0,1}, {1,3}, {3,5}, {5,7}, {7,9}, {9,11}};
inline constexpr ByteSpan kSpans_29[] = {{0,6}, {6,7}, {7,12}};
inline constexpr ByteSpan kSpans_30[] = {{0,4}, {4,9}, {9,12}, {12,21}, {21,23}, {23,30}, {30,34}, {34,37}, {37,39}, {39,40}, {40,45}, {45,47}, {47,51}};
inline constexpr ByteSpan kSpans_31[] = {{0,5}, {5,6}, {6,14}, {14,15}, {15,21}, {21,22}};
inline constexpr ByteSpan kSpans_32[] = {{0,1}, {1,2}, {2,3}};
inline constexpr ByteSpan kSpans_33[] = {{0,1}, {1,3}, {3,6}};
inline constexpr ByteSpan kSpans_34[] = {{0,3}, {3,5}, {5,9}, {9,12}, {12,13}};
inline constexpr ByteSpan kSpans_35[] = {{0,3}, {3,4}, {4,5}};
inline constexpr ByteSpan kSpans_36[] = {{0,12}, {12,22}, {22,37}};
inline constexpr ByteSpan kSpans_37[] = {{0,10}, {10,21}};
inline constexpr ByteSpan* kSpans_38 = nullptr;
inline constexpr ByteSpan kSpans_39[] = {{0,5}, {5,6}, {6,12}, {12,13}, {13,18}, {18,21}, {21,23}, {23,28}, {28,29}, {29,30}, {30,31}, {31,32}, {32,33}, {33,34}};
inline constexpr ByteSpan kSpans_40[] = {{0,29}};
inline constexpr ByteSpan kSpans_41[] = {{0,2}, {2,10}, {10,17}};
inline constexpr ByteSpan kSpans_42[] = {{0,8}, {8,15}, {15,18}};
inline constexpr ByteSpan kSpans_43[] = {{0,1}, {1,4}, {4,6}, {6,10}, {10,12}};
inline constexpr ByteSpan kSpans_44[] = {{0,4}, {4,8}, {8,9}, {9,13}, {13,17}, {17,18}, {18,23}, {23,27}};
inline constexpr ByteSpan kSpans_45[] = {{0,7}};
inline constexpr ByteSpan kSpans_46[] = {{0,5}, {5,14}, {14,21}, {21,29}};
inline constexpr ByteSpan kSpans_47[] = {{0,12}, {12,34}, {34,50}};
inline constexpr ByteSpan kSpans_48[] = {{0,5}, {5,10}, {10,16}, {16,21}, {21,27}, {27,32}};
inline constexpr ByteSpan kSpans_49[] = {{0,1}, {1,3}, {3,5}, {5,7}, {7,9}, {9,11}, {11,21}, {21,32}};
inline constexpr ByteSpan kSpans_50[] = {{0,4}, {4,7}, {7,16}, {16,18}, {18,24}, {24,29}, {29,31}, {31,39}, {39,45}};
inline constexpr ByteSpan kSpans_51[] = {{0,2}, {2,4}, {4,5}, {5,10}, {10,12}, {12,15}, {15,22}, {22,24}, {24,29}, {29,34}, {34,36}, {36,38}, {38,39}, {39,42}, {42,44}};
inline constexpr ByteSpan kSpans_52[] = {{0,9}, {9,15}, {15,23}, {23,27}, {27,31}, {31,32}, {32,36}, {36,41}};
inline constexpr ByteSpan kSpans_53[] = {{0,12}, {12,24}, {24,34}, {34,40}};
inline constexpr ByteSpan kSpans_54[] = {{0,7}, {7,14}, {14,24}, {24,34}, {34,49}};
inline constexpr ByteSpan kSpans_55[] = {{0,6}, {6,7}, {7,12}};
inline constexpr ByteSpan kSpans_56[] = {{0,1}, {1,10}, {10,12}, {12,19}, {19,20}, {20,23}, {23,24}, {24,28}, {28,30}};
inline constexpr ByteSpan kSpans_57[] = {{0,1}, {1,2}, {2,4}, {4,5}, {5,7}, {7,8}, {8,10}, {10,11}, {11,13}, {13,14}, {14,15}};
inline constexpr ByteSpan kSpans_58[] = {{0,3}, {3,11}, {11,14}, {14,21}, {21,24}};
inline constexpr ByteSpan kSpans_59[] = {{0,5}, {5,6}, {6,12}, {12,13}};
inline constexpr ByteSpan kSpans_60[] = {{0,1}, {1,2}, {2,3}, {3,4}, {4,5}, {5,10}};
inline constexpr ByteSpan kSpans_61[] = {{0,1}, {1,9}, {9,16}};
inline constexpr ByteSpan kSpans_62[] = {{0,7}, {7,8}, {8,14}, {14,23}};

inline constexpr OracleFixture kFixtures[] = {
    {"empty", std::string_view("", 0), kSpans_0, 0},
    {"ascii_words", std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21\x20\x54\x68\x69\x73\x20\x69\x73\x20\x61\x20\x74\x65\x73\x74\x3a\x20\x31\x32\x33\x2e", 34), kSpans_1, 14},
    {"ascii_punct_only", std::string_view("\x21\x40\x23\x24\x25\x5e\x26\x2a\x28\x29\x5f\x2b\x2d\x3d\x5b\x5d\x7b\x7d\x7c\x3b\x27\x3a\x22\x2c\x2e\x2f\x3c\x3e\x3f", 29), kSpans_2, 1},
    {"leading_ws", std::string_view("\x20\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 17), kSpans_3, 3},
    {"trailing_ws", std::string_view("\x74\x72\x61\x69\x6c\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73\x20\x20\x20", 18), kSpans_4, 3},
    {"repeated_ws", std::string_view("\x61\x20\x20\x20\x20\x62\x20\x20\x20\x20\x20\x63", 12), kSpans_5, 5},
    {"tabs", std::string_view("\x61\x09\x62\x09\x63", 5), kSpans_6, 5},
    {"lf", std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f", 17), kSpans_7, 5},
    {"crlf", std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0d\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f", 18), kSpans_8, 6},
    {"only_ws", std::string_view("\x20\x20\x20\x0a\x09\x20\x20", 7), kSpans_9, 1},
    {"contractions", std::string_view("\x64\x6f\x6e\x27\x74\x20\x69\x73\x6e\x27\x74\x20\x49\x27\x76\x65\x20\x49\x27\x6c\x6c\x20\x49\x27\x6d\x20\x49\x27\x64\x20\x77\x65\x27\x72\x65\x20\x63\x61\x6e\x27\x74", 41), kSpans_10, 16},
    {"digit_one", std::string_view("\x61\x31\x61", 3), kSpans_11, 3},
    {"digit_two", std::string_view("\x61\x31\x32\x61", 4), kSpans_12, 4},
    {"digit_long_run", std::string_view("\x54\x68\x65\x20\x79\x65\x61\x72\x20\x32\x30\x32\x33\x31\x32\x33\x31\x20\x77\x61\x73\x20\x6c\x6f\x6e\x67\x3a\x20\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39", 40), kSpans_13, 27},
    {"mixed_letter_number", std::string_view("\x61\x62\x63\x31\x32\x33\x64\x65\x66\x34\x35\x36", 12), kSpans_14, 8},
    {"number_letter_boundary", std::string_view("\x31\x32\x33\x61\x62\x63", 6), kSpans_15, 4},
    {"letter_number_boundary", std::string_view("\x61\x62\x63\x31\x32\x33", 6), kSpans_16, 4},
    {"letter_punct_boundary", std::string_view("\x61\x62\x63\x21\x21\x21\x64\x65\x66", 9), kSpans_17, 3},
    {"punct_letter_boundary", std::string_view("\x21\x21\x21\x61\x62\x63\x64\x65\x66", 9), kSpans_18, 2},
    {"ws_letter_boundary", std::string_view("\x20\x20\x20\x61\x62\x63", 6), kSpans_19, 2},
    {"letter_ws_boundary", std::string_view("\x61\x62\x63\x20\x20\x20", 6), kSpans_20, 2},
    {"arabic_indic_digits", std::string_view("\xd9\xa1\xd9\xa2\xd9\xa3", 6), kSpans_21, 3},
    {"fullwidth_digits", std::string_view("\xef\xbc\x91\xef\xbc\x92\xef\xbc\x93", 9), kSpans_22, 3},
    {"superscript_digits", std::string_view("\xc2\xb2\xc2\xb3", 4), kSpans_23, 2},
    {"roman_numerals", std::string_view("\xe2\x85\xa0\xe2\x85\xa1\xe2\x85\xa2", 9), kSpans_24, 3},
    {"non_ascii_latin", std::string_view("\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9\x20\x6e\x61\xc3\xaf\x76\x65\x20\x5a\xc3\xbc\x72\x69\x63\x68", 29), kSpans_25, 4},
    {"non_ascii_cjk", std::string_view("\xe4\xbd\xa0\xe5\xa5\xbd\xe4\xb8\x96\xe7\x95\x8c\x20\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf\x20\xed\x95\x9c\xea\xb5\xad\xec\x96\xb4", 38), kSpans_26, 3},
    {"emoji", std::string_view("\x48\x65\x6c\x6c\x6f\x20\xf0\x9f\x98\x80\x20\x57\x6f\x72\x6c\x64\x20\xf0\x9f\x98\x81\xf0\x9f\x98\x82", 25), kSpans_27, 4},
    {"combining_marks", std::string_view("\x65\xcc\x81\x20\x61\xcc\x80\x20\x6e\xcc\x83", 11), kSpans_28, 6},
    {"embedded_nul", std::string_view("\x62\x65\x66\x6f\x72\x65\x00\x61\x66\x74\x65\x72", 12), kSpans_29, 3},
    {"special_token_lookalike", std::string_view("\x74\x65\x78\x74\x20\x77\x69\x74\x68\x20\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x20\x69\x6e\x73\x69\x64\x65\x20\x61\x6e\x64\x20\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x20\x74\x6f\x6f", 51), kSpans_30, 13},
    {"punct_adjacent_unicode", std::string_view("\x63\x61\x66\xc3\xa9\x21\x72\xc3\xa9\x73\x75\x6d\xc3\xa9\x2c\x6e\x61\xc3\xaf\x76\x65\x2e", 22), kSpans_31, 6},
    {"numeric_isolated_between_letters", std::string_view("\x61\x35\x62", 3), kSpans_32, 3},
    {"consecutive_numeric_diff_blocks", std::string_view("\x35\xd9\xa1\xef\xbc\x91", 6), kSpans_33, 3},
    {"literal_apostrophe_vs_rsquo", std::string_view("\x64\x6f\x6e\x27\x74\x20\x64\x6f\x6e\xe2\x80\x99\x74", 13), kSpans_34, 5},
    {"uppercase_apostrophe", std::string_view("\x44\x4f\x4e\x27\x54", 5), kSpans_35, 3},
    {"multi_merge_bpe_case", std::string_view("\x75\x6e\x62\x65\x6c\x69\x65\x76\x61\x62\x6c\x79\x20\x77\x6f\x6e\x64\x65\x72\x66\x75\x6c\x20\x74\x72\x61\x6e\x73\x66\x6f\x72\x6d\x61\x74\x69\x6f\x6e", 37), kSpans_36, 3},
    {"repeated_adjacent_pairs", std::string_view("\x61\x61\x61\x61\x61\x61\x61\x61\x61\x61\x20\x62\x62\x62\x62\x62\x62\x62\x62\x62\x62", 21), kSpans_37, 2},
    {"golden__empty_input", std::string_view("", 0), kSpans_38, 0},
    {"golden__ascii_words", std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21\x20\x54\x68\x69\x73\x20\x69\x73\x20\x61\x20\x74\x65\x73\x74\x3a\x20\x31\x32\x33\x2e", 34), kSpans_39, 14},
    {"golden__ascii_punctuation_only", std::string_view("\x21\x40\x23\x24\x25\x5e\x26\x2a\x28\x29\x5f\x2b\x2d\x3d\x5b\x5d\x7b\x7d\x7c\x3b\x27\x3a\x22\x2c\x2e\x2f\x3c\x3e\x3f", 29), kSpans_40, 1},
    {"golden__leading_whitespace", std::string_view("\x20\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 17), kSpans_41, 3},
    {"golden__trailing_whitespace", std::string_view("\x74\x72\x61\x69\x6c\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73\x20\x20\x20", 18), kSpans_42, 3},
    {"golden__repeated_whitespace", std::string_view("\x61\x20\x20\x20\x20\x62\x20\x20\x20\x20\x20\x63", 12), kSpans_43, 5},
    {"golden__newline_and_tab", std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f\x09\x61\x66\x74\x65\x72\x20\x74\x61\x62", 27), kSpans_44, 8},
    {"golden__only_whitespace", std::string_view("\x20\x20\x20\x0a\x09\x20\x20", 7), kSpans_45, 1},
    {"golden__non_ascii_latin", std::string_view("\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9\x20\x6e\x61\xc3\xaf\x76\x65\x20\x5a\xc3\xbc\x72\x69\x63\x68", 29), kSpans_46, 4},
    {"golden__non_ascii_cjk", std::string_view("\xe4\xbd\xa0\xe5\xa5\xbd\xe4\xb8\x96\xe7\x95\x8c\x20\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf\xe4\xb8\x96\xe7\x95\x8c\x20\xec\x95\x88\xeb\x85\x95\xed\x95\x98\xec\x84\xb8\xec\x9a\x94", 50), kSpans_47, 3},
    {"golden__non_ascii_emoji", std::string_view("\x68\x65\x6c\x6c\x6f\x20\xf0\x9f\x98\x80\x20\x77\x6f\x72\x6c\x64\x20\xf0\x9f\x8c\x8d\x20\x65\x6d\x6f\x6a\x69\x20\xf0\x9f\x9a\x80", 32), kSpans_48, 6},
    {"golden__non_ascii_combining_marks", std::string_view("\x65\xcc\x81\x20\x61\xcc\x80\x20\x6f\xcc\x82\x20\x63\x6f\x6d\x62\x69\x6e\x69\x6e\x67\x20\x64\x69\x61\x63\x72\x69\x74\x69\x63\x73", 32), kSpans_49, 8},
    {"golden__text_resembling_special_tokens", std::string_view("\x74\x68\x69\x73\x20\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x20\x6c\x6f\x6f\x6b\x73\x20\x6c\x69\x6b\x65\x20\x61\x20\x73\x70\x65\x63\x69\x61\x6c\x20\x74\x6f\x6b\x65\x6e", 45), kSpans_50, 9},
    {"golden__text_resembling_special_tokens_2", std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x6e\x6f\x74\x20\x72\x65\x61\x6c\x6c\x79\x20\x61\x20\x63\x68\x61\x74\x20\x74\x75\x72\x6e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 44), kSpans_51, 15},
    {"golden__unusual_unicode_ranges", std::string_view("\xe2\x98\x83\xe2\x9d\xa4\xef\xbb\xbf\x20\x6d\x69\x78\x65\x64\x20\x73\x79\x6d\x62\x6f\x6c\x73\x20\x61\x6e\x64\x20\x42\x4f\x4d\x2d\x6c\x69\x6b\x65\x20\x63\x68\x61\x72", 41), kSpans_52, 8},
    {"golden__multibyte_utf8_boundary", std::string_view("\xf0\x9f\x98\x80\xf0\x9f\x98\x81\xf0\x9f\x98\x82\x20\x63\x6f\x6e\x73\x65\x63\x75\x74\x69\x76\x65\x20\x6d\x75\x6c\x74\x69\x62\x79\x74\x65\x20\x65\x6d\x6f\x6a\x69", 40), kSpans_53, 4},
    {"golden__mixed_script", std::string_view("\x45\x6e\x67\x6c\x69\x73\x68\x20\xe4\xb8\xad\xe6\x96\x87\x20\xe6\x97\xa5\xe6\x9c\xac\xe8\xaa\x9e\x20\xed\x95\x9c\xea\xb5\xad\xec\x96\xb4\x20\xd8\xa7\xd9\x84\xd8\xb9\xd8\xb1\xd8\xa8\xd9\x8a\xd8\xa9", 49), kSpans_54, 5},
    {"golden__embedded_nul_char", std::string_view("\x62\x65\x66\x6f\x72\x65\x00\x61\x66\x74\x65\x72", 12), kSpans_55, 3},
    {"golden__encode_decode_encode_caveat", std::string_view("\x20\x20\x4d\x75\x6c\x74\x69\x70\x6c\x65\x20\x20\x20\x73\x70\x61\x63\x65\x73\x09\x61\x6e\x64\x09\x74\x61\x62\x73\x20\x20", 30), kSpans_56, 9},
    {"rawprompt__OE-L0-SYNTH-1-control-tokens", std::string_view("\x3c\x31\x3e\x3c\x35\x3e\x3c\x39\x3e\x3c\x33\x3e\x3c\x37\x3e", 15), kSpans_57, 11},
    {"rawprompt__smollm2-135m:'The capital of France is'", std::string_view("\x54\x68\x65\x20\x63\x61\x70\x69\x74\x61\x6c\x20\x6f\x66\x20\x46\x72\x61\x6e\x63\x65\x20\x69\x73", 24), kSpans_58, 5},
    {"rawprompt__smollm2-135m:'Hello, world!'", std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21", 13), kSpans_59, 4},
    {"rawprompt__smollm2-135m:'12345 test'", std::string_view("\x31\x32\x33\x34\x35\x20\x74\x65\x73\x74", 10), kSpans_60, 6},
    {"rawprompt__smollm2-135m:'  leading spaces'", std::string_view("\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 16), kSpans_61, 3},
    {"rawprompt__smollm2-135m:'unicode: café résumé'", std::string_view("\x75\x6e\x69\x63\x6f\x64\x65\x3a\x20\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9", 23), kSpans_62, 4},
};
inline constexpr std::size_t kFixtureCount = 63;

}  // namespace orcengine::pretok_fixtures
