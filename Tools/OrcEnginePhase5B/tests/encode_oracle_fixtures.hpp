// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// GENERATED FILE -- do not hand-edit. Produced by
// Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py.
//
// Stage 2B encode() oracle fixtures: exact token-ID sequences from the
// real tokenizers==0.22.2 Tokenizer.encode(), loaded from the pinned
// smollm2-135m/tokenizer.json (sha256- and contract-verified by the
// generator before use -- see docs/OrcEngine/DECISION_LOG.md OE-ADR-034
// for the full hash record). mode 'A' = oracle encode_special_tokens=True
// (native LiteralText); mode 'B' = oracle encode_special_tokens=False,
// the oracle's OWN default (native RecognizeControlTokens). Every entry
// was computed twice and asserted identical before being written here.
//   corpus size           : 386
#pragma once

#include <cstddef>
#include <cstdint>
#include <string_view>

namespace orcengine::pretok_fixtures {

struct EncodeFixture {
    const char* id;
    char mode;  // 'A' = LiteralText, 'B' = RecognizeControlTokens
    std::string_view utf8;
    const std::int64_t* ids;
    std::size_t id_count;
};

inline constexpr std::int64_t* kIds_0 = nullptr;
inline constexpr std::int64_t kIds_1[] = {19556, 28, 905, 17, 669, 314, 253, 1028, 42, 216, 33, 34, 35, 30};
inline constexpr std::int64_t kIds_2[] = {17, 48, 19, 20, 21, 78, 22, 26, 1000, 79, 34592, 30208, 6150, 108, 43, 1539, 1002, 18380, 44, 46, 47};
inline constexpr std::int64_t kIds_3[] = {12420, 982, 3247, 982, 339, 3543, 339, 3060, 339, 5248, 339, 6737, 392, 2316, 416, 982};
inline constexpr std::int64_t kIds_4[] = {256, 2899, 5600};
inline constexpr std::int64_t kIds_5[] = {4861, 4966, 5600, 333};
inline constexpr std::int64_t kIds_6[] = {81, 333, 278, 289, 265};
inline constexpr std::int64_t kIds_7[] = {81, 197, 82, 197, 83};
inline constexpr std::int64_t kIds_8[] = {1311, 582, 198, 1311, 827};
inline constexpr std::int64_t kIds_9[] = {1311, 582, 201, 198, 1311, 827};
inline constexpr std::int64_t kIds_10[] = {504, 713, 216, 34, 32, 34, 35, 33, 34, 35, 33, 436, 986, 42, 216, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41, 41};
inline constexpr std::int64_t kIds_11[] = {164, 111, 164, 112, 164, 113};
inline constexpr std::int64_t kIds_12[] = {8083, 235, 8083, 236, 8083, 237};
inline constexpr std::int64_t kIds_13[] = {83, 1939, 2756, 412, 2756, 5422, 2756, 15486, 46494, 1777, 7170, 5130};
inline constexpr std::int64_t kIds_14[] = {18645, 250, 48392, 138, 7906, 240, 178, 239, 230, 46673, 237, 10391, 237, 41152, 7365, 111, 7365, 124, 216, 33085, 246, 181, 130, 251, 183, 240, 129};
inline constexpr std::int64_t kIds_15[] = {19556, 40303, 218, 2260, 40303, 219, 10813, 242, 220};
inline constexpr std::int64_t kIds_16[] = {2756, 25782, 5549, 126};
inline constexpr std::int64_t kIds_17[] = {17985, 190, 9110};
inline constexpr std::int64_t kIds_18[] = {2692, 351, 2067, 108, 486, 1714, 2692, 108, 46, 2972, 284, 2067, 108, 306, 79, 3738, 108, 46, 1147};
inline constexpr std::int64_t* kIds_19 = nullptr;
inline constexpr std::int64_t kIds_20[] = {19556, 28, 905, 17, 669, 314, 253, 1028, 42, 216, 33, 34, 35, 30};
inline constexpr std::int64_t kIds_21[] = {17, 48, 19, 20, 21, 78, 22, 26, 1000, 79, 34592, 30208, 6150, 108, 43, 1539, 1002, 18380, 44, 46, 47};
inline constexpr std::int64_t kIds_22[] = {256, 2899, 5600};
inline constexpr std::int64_t kIds_23[] = {4861, 4966, 5600, 333};
inline constexpr std::int64_t kIds_24[] = {81, 333, 278, 289, 265};
inline constexpr std::int64_t kIds_25[] = {1311, 582, 198, 1311, 827, 197, 9110, 10147};
inline constexpr std::int64_t kIds_26[] = {333, 3354, 256};
inline constexpr std::int64_t kIds_27[] = {83, 1939, 2756, 412, 2756, 5422, 2756, 15486, 46494, 1777, 7170, 5130};
inline constexpr std::int64_t kIds_28[] = {18645, 250, 48392, 138, 7906, 240, 178, 239, 230, 46673, 237, 10391, 237, 41152, 7365, 111, 7365, 124, 7906, 240, 178, 239, 230, 18601, 239, 226, 182, 223, 239, 33085, 242, 183, 222, 133, 183, 244, 238};
inline constexpr std::int64_t kIds_29[] = {28120, 40303, 218, 905, 15107, 230, 231, 649, 33777, 15107, 244, 218};
inline constexpr std::int64_t kIds_30[] = {85, 151, 219, 253, 151, 218, 263, 151, 220, 9301, 801, 387, 608, 747};
inline constexpr std::int64_t kIds_31[] = {8232, 2067, 108, 486, 1714, 2692, 108, 46, 5117, 702, 253, 1767, 9624};
inline constexpr std::int64_t kIds_32[] = {44, 108, 306, 79, 3738, 108, 46, 1766, 2159, 253, 11743, 1607, 44, 108, 306, 79, 486, 108, 46};
inline constexpr std::int64_t kIds_33[] = {173, 242, 221, 173, 247, 114, 186, 136, 140, 6468, 7349, 284, 389, 9232, 29, 2579, 1336};
inline constexpr std::int64_t kIds_34[] = {10813, 242, 218, 10813, 242, 219, 10813, 242, 220, 19996, 1575, 505, 105, 676, 649, 33777};
inline constexpr std::int64_t kIds_35[] = {14901, 216, 28589, 29184, 17097, 241, 115, 40993, 179, 120, 248, 216, 33085, 246, 181, 130, 251, 183, 240, 129, 27819, 34966, 20602, 31462, 23339, 43463};
inline constexpr std::int64_t kIds_36[] = {17985, 190, 9110};
inline constexpr std::int64_t kIds_37[] = {216, 16560, 256, 5600, 197, 397, 197, 100, 7366, 256};
inline constexpr std::int64_t kIds_38[] = {44, 33, 21198, 37, 21198, 41, 21198, 35, 21198, 39, 46};
inline constexpr std::int64_t kIds_39[] = {504, 3575, 282, 4649, 314};
inline constexpr std::int64_t kIds_40[] = {19556, 28, 905, 17};
inline constexpr std::int64_t kIds_41[] = {33, 34, 35, 36, 37, 1028};
inline constexpr std::int64_t kIds_42[] = {216, 2899, 5600};
inline constexpr std::int64_t kIds_43[] = {35852, 42, 37366, 412, 2756, 5422, 2756};
inline constexpr std::int64_t kIds_44[] = {44, 108, 486, 1714, 2692, 108, 46};
inline constexpr std::int64_t kIds_45[] = {44, 108, 306, 79, 3738, 108, 46};
inline constexpr std::int64_t kIds_46[] = {44, 108, 306, 79, 486, 108, 46};
inline constexpr std::int64_t kIds_47[] = {44, 22139, 79, 1245, 46};
inline constexpr std::int64_t kIds_48[] = {44, 10368, 46};
inline constexpr std::int64_t kIds_49[] = {44, 2153, 79, 29070, 46};
inline constexpr std::int64_t kIds_50[] = {44, 5805, 46};
inline constexpr std::int64_t kIds_51[] = {44, 460, 79, 9888, 46};
inline constexpr std::int64_t kIds_52[] = {44, 2195, 79, 3738, 46};
inline constexpr std::int64_t kIds_53[] = {44, 2195, 79, 3591, 46};
inline constexpr std::int64_t kIds_54[] = {44, 2195, 79, 12739, 46};
inline constexpr std::int64_t kIds_55[] = {44, 90, 1110, 44451, 79, 3738, 46};
inline constexpr std::int64_t kIds_56[] = {44, 90, 1110, 44451, 79, 2692, 46};
inline constexpr std::int64_t kIds_57[] = {44, 90, 1110, 44451, 79, 4635, 46};
inline constexpr std::int64_t kIds_58[] = {44, 90, 1110, 44451, 79, 4952, 46};
inline constexpr std::int64_t kIds_59[] = {44, 90, 1110, 44451, 79, 13164, 46};
inline constexpr std::int64_t kIds_60[] = {44, 16451, 79, 4952, 46};
inline constexpr std::int64_t kIds_61[] = {0};
inline constexpr std::int64_t kIds_62[] = {1};
inline constexpr std::int64_t kIds_63[] = {2};
inline constexpr std::int64_t kIds_64[] = {3};
inline constexpr std::int64_t kIds_65[] = {4};
inline constexpr std::int64_t kIds_66[] = {5};
inline constexpr std::int64_t kIds_67[] = {6};
inline constexpr std::int64_t kIds_68[] = {7};
inline constexpr std::int64_t kIds_69[] = {8};
inline constexpr std::int64_t kIds_70[] = {9};
inline constexpr std::int64_t kIds_71[] = {10};
inline constexpr std::int64_t kIds_72[] = {11};
inline constexpr std::int64_t kIds_73[] = {12};
inline constexpr std::int64_t kIds_74[] = {13};
inline constexpr std::int64_t kIds_75[] = {14};
inline constexpr std::int64_t kIds_76[] = {15};
inline constexpr std::int64_t kIds_77[] = {16};
inline constexpr std::int64_t kIds_78[] = {0, 0};
inline constexpr std::int64_t kIds_79[] = {0, 1};
inline constexpr std::int64_t kIds_80[] = {0, 2};
inline constexpr std::int64_t kIds_81[] = {0, 3};
inline constexpr std::int64_t kIds_82[] = {0, 4};
inline constexpr std::int64_t kIds_83[] = {0, 5};
inline constexpr std::int64_t kIds_84[] = {0, 6};
inline constexpr std::int64_t kIds_85[] = {0, 7};
inline constexpr std::int64_t kIds_86[] = {0, 8};
inline constexpr std::int64_t kIds_87[] = {0, 9};
inline constexpr std::int64_t kIds_88[] = {0, 10};
inline constexpr std::int64_t kIds_89[] = {0, 11};
inline constexpr std::int64_t kIds_90[] = {0, 12};
inline constexpr std::int64_t kIds_91[] = {0, 13};
inline constexpr std::int64_t kIds_92[] = {0, 14};
inline constexpr std::int64_t kIds_93[] = {0, 15};
inline constexpr std::int64_t kIds_94[] = {0, 16};
inline constexpr std::int64_t kIds_95[] = {1, 0};
inline constexpr std::int64_t kIds_96[] = {1, 1};
inline constexpr std::int64_t kIds_97[] = {1, 2};
inline constexpr std::int64_t kIds_98[] = {1, 3};
inline constexpr std::int64_t kIds_99[] = {1, 4};
inline constexpr std::int64_t kIds_100[] = {1, 5};
inline constexpr std::int64_t kIds_101[] = {1, 6};
inline constexpr std::int64_t kIds_102[] = {1, 7};
inline constexpr std::int64_t kIds_103[] = {1, 8};
inline constexpr std::int64_t kIds_104[] = {1, 9};
inline constexpr std::int64_t kIds_105[] = {1, 10};
inline constexpr std::int64_t kIds_106[] = {1, 11};
inline constexpr std::int64_t kIds_107[] = {1, 12};
inline constexpr std::int64_t kIds_108[] = {1, 13};
inline constexpr std::int64_t kIds_109[] = {1, 14};
inline constexpr std::int64_t kIds_110[] = {1, 15};
inline constexpr std::int64_t kIds_111[] = {1, 16};
inline constexpr std::int64_t kIds_112[] = {2, 0};
inline constexpr std::int64_t kIds_113[] = {2, 1};
inline constexpr std::int64_t kIds_114[] = {2, 2};
inline constexpr std::int64_t kIds_115[] = {2, 3};
inline constexpr std::int64_t kIds_116[] = {2, 4};
inline constexpr std::int64_t kIds_117[] = {2, 5};
inline constexpr std::int64_t kIds_118[] = {2, 6};
inline constexpr std::int64_t kIds_119[] = {2, 7};
inline constexpr std::int64_t kIds_120[] = {2, 8};
inline constexpr std::int64_t kIds_121[] = {2, 9};
inline constexpr std::int64_t kIds_122[] = {2, 10};
inline constexpr std::int64_t kIds_123[] = {2, 11};
inline constexpr std::int64_t kIds_124[] = {2, 12};
inline constexpr std::int64_t kIds_125[] = {2, 13};
inline constexpr std::int64_t kIds_126[] = {2, 14};
inline constexpr std::int64_t kIds_127[] = {2, 15};
inline constexpr std::int64_t kIds_128[] = {2, 16};
inline constexpr std::int64_t kIds_129[] = {3, 0};
inline constexpr std::int64_t kIds_130[] = {3, 1};
inline constexpr std::int64_t kIds_131[] = {3, 2};
inline constexpr std::int64_t kIds_132[] = {3, 3};
inline constexpr std::int64_t kIds_133[] = {3, 4};
inline constexpr std::int64_t kIds_134[] = {3, 5};
inline constexpr std::int64_t kIds_135[] = {3, 6};
inline constexpr std::int64_t kIds_136[] = {3, 7};
inline constexpr std::int64_t kIds_137[] = {3, 8};
inline constexpr std::int64_t kIds_138[] = {3, 9};
inline constexpr std::int64_t kIds_139[] = {3, 10};
inline constexpr std::int64_t kIds_140[] = {3, 11};
inline constexpr std::int64_t kIds_141[] = {3, 12};
inline constexpr std::int64_t kIds_142[] = {3, 13};
inline constexpr std::int64_t kIds_143[] = {3, 14};
inline constexpr std::int64_t kIds_144[] = {3, 15};
inline constexpr std::int64_t kIds_145[] = {3, 16};
inline constexpr std::int64_t kIds_146[] = {4, 0};
inline constexpr std::int64_t kIds_147[] = {4, 1};
inline constexpr std::int64_t kIds_148[] = {4, 2};
inline constexpr std::int64_t kIds_149[] = {4, 3};
inline constexpr std::int64_t kIds_150[] = {4, 4};
inline constexpr std::int64_t kIds_151[] = {4, 5};
inline constexpr std::int64_t kIds_152[] = {4, 6};
inline constexpr std::int64_t kIds_153[] = {4, 7};
inline constexpr std::int64_t kIds_154[] = {4, 8};
inline constexpr std::int64_t kIds_155[] = {4, 9};
inline constexpr std::int64_t kIds_156[] = {4, 10};
inline constexpr std::int64_t kIds_157[] = {4, 11};
inline constexpr std::int64_t kIds_158[] = {4, 12};
inline constexpr std::int64_t kIds_159[] = {4, 13};
inline constexpr std::int64_t kIds_160[] = {4, 14};
inline constexpr std::int64_t kIds_161[] = {4, 15};
inline constexpr std::int64_t kIds_162[] = {4, 16};
inline constexpr std::int64_t kIds_163[] = {5, 0};
inline constexpr std::int64_t kIds_164[] = {5, 1};
inline constexpr std::int64_t kIds_165[] = {5, 2};
inline constexpr std::int64_t kIds_166[] = {5, 3};
inline constexpr std::int64_t kIds_167[] = {5, 4};
inline constexpr std::int64_t kIds_168[] = {5, 5};
inline constexpr std::int64_t kIds_169[] = {5, 6};
inline constexpr std::int64_t kIds_170[] = {5, 7};
inline constexpr std::int64_t kIds_171[] = {5, 8};
inline constexpr std::int64_t kIds_172[] = {5, 9};
inline constexpr std::int64_t kIds_173[] = {5, 10};
inline constexpr std::int64_t kIds_174[] = {5, 11};
inline constexpr std::int64_t kIds_175[] = {5, 12};
inline constexpr std::int64_t kIds_176[] = {5, 13};
inline constexpr std::int64_t kIds_177[] = {5, 14};
inline constexpr std::int64_t kIds_178[] = {5, 15};
inline constexpr std::int64_t kIds_179[] = {5, 16};
inline constexpr std::int64_t kIds_180[] = {6, 0};
inline constexpr std::int64_t kIds_181[] = {6, 1};
inline constexpr std::int64_t kIds_182[] = {6, 2};
inline constexpr std::int64_t kIds_183[] = {6, 3};
inline constexpr std::int64_t kIds_184[] = {6, 4};
inline constexpr std::int64_t kIds_185[] = {6, 5};
inline constexpr std::int64_t kIds_186[] = {6, 6};
inline constexpr std::int64_t kIds_187[] = {6, 7};
inline constexpr std::int64_t kIds_188[] = {6, 8};
inline constexpr std::int64_t kIds_189[] = {6, 9};
inline constexpr std::int64_t kIds_190[] = {6, 10};
inline constexpr std::int64_t kIds_191[] = {6, 11};
inline constexpr std::int64_t kIds_192[] = {6, 12};
inline constexpr std::int64_t kIds_193[] = {6, 13};
inline constexpr std::int64_t kIds_194[] = {6, 14};
inline constexpr std::int64_t kIds_195[] = {6, 15};
inline constexpr std::int64_t kIds_196[] = {6, 16};
inline constexpr std::int64_t kIds_197[] = {7, 0};
inline constexpr std::int64_t kIds_198[] = {7, 1};
inline constexpr std::int64_t kIds_199[] = {7, 2};
inline constexpr std::int64_t kIds_200[] = {7, 3};
inline constexpr std::int64_t kIds_201[] = {7, 4};
inline constexpr std::int64_t kIds_202[] = {7, 5};
inline constexpr std::int64_t kIds_203[] = {7, 6};
inline constexpr std::int64_t kIds_204[] = {7, 7};
inline constexpr std::int64_t kIds_205[] = {7, 8};
inline constexpr std::int64_t kIds_206[] = {7, 9};
inline constexpr std::int64_t kIds_207[] = {7, 10};
inline constexpr std::int64_t kIds_208[] = {7, 11};
inline constexpr std::int64_t kIds_209[] = {7, 12};
inline constexpr std::int64_t kIds_210[] = {7, 13};
inline constexpr std::int64_t kIds_211[] = {7, 14};
inline constexpr std::int64_t kIds_212[] = {7, 15};
inline constexpr std::int64_t kIds_213[] = {7, 16};
inline constexpr std::int64_t kIds_214[] = {8, 0};
inline constexpr std::int64_t kIds_215[] = {8, 1};
inline constexpr std::int64_t kIds_216[] = {8, 2};
inline constexpr std::int64_t kIds_217[] = {8, 3};
inline constexpr std::int64_t kIds_218[] = {8, 4};
inline constexpr std::int64_t kIds_219[] = {8, 5};
inline constexpr std::int64_t kIds_220[] = {8, 6};
inline constexpr std::int64_t kIds_221[] = {8, 7};
inline constexpr std::int64_t kIds_222[] = {8, 8};
inline constexpr std::int64_t kIds_223[] = {8, 9};
inline constexpr std::int64_t kIds_224[] = {8, 10};
inline constexpr std::int64_t kIds_225[] = {8, 11};
inline constexpr std::int64_t kIds_226[] = {8, 12};
inline constexpr std::int64_t kIds_227[] = {8, 13};
inline constexpr std::int64_t kIds_228[] = {8, 14};
inline constexpr std::int64_t kIds_229[] = {8, 15};
inline constexpr std::int64_t kIds_230[] = {8, 16};
inline constexpr std::int64_t kIds_231[] = {9, 0};
inline constexpr std::int64_t kIds_232[] = {9, 1};
inline constexpr std::int64_t kIds_233[] = {9, 2};
inline constexpr std::int64_t kIds_234[] = {9, 3};
inline constexpr std::int64_t kIds_235[] = {9, 4};
inline constexpr std::int64_t kIds_236[] = {9, 5};
inline constexpr std::int64_t kIds_237[] = {9, 6};
inline constexpr std::int64_t kIds_238[] = {9, 7};
inline constexpr std::int64_t kIds_239[] = {9, 8};
inline constexpr std::int64_t kIds_240[] = {9, 9};
inline constexpr std::int64_t kIds_241[] = {9, 10};
inline constexpr std::int64_t kIds_242[] = {9, 11};
inline constexpr std::int64_t kIds_243[] = {9, 12};
inline constexpr std::int64_t kIds_244[] = {9, 13};
inline constexpr std::int64_t kIds_245[] = {9, 14};
inline constexpr std::int64_t kIds_246[] = {9, 15};
inline constexpr std::int64_t kIds_247[] = {9, 16};
inline constexpr std::int64_t kIds_248[] = {10, 0};
inline constexpr std::int64_t kIds_249[] = {10, 1};
inline constexpr std::int64_t kIds_250[] = {10, 2};
inline constexpr std::int64_t kIds_251[] = {10, 3};
inline constexpr std::int64_t kIds_252[] = {10, 4};
inline constexpr std::int64_t kIds_253[] = {10, 5};
inline constexpr std::int64_t kIds_254[] = {10, 6};
inline constexpr std::int64_t kIds_255[] = {10, 7};
inline constexpr std::int64_t kIds_256[] = {10, 8};
inline constexpr std::int64_t kIds_257[] = {10, 9};
inline constexpr std::int64_t kIds_258[] = {10, 10};
inline constexpr std::int64_t kIds_259[] = {10, 11};
inline constexpr std::int64_t kIds_260[] = {10, 12};
inline constexpr std::int64_t kIds_261[] = {10, 13};
inline constexpr std::int64_t kIds_262[] = {10, 14};
inline constexpr std::int64_t kIds_263[] = {10, 15};
inline constexpr std::int64_t kIds_264[] = {10, 16};
inline constexpr std::int64_t kIds_265[] = {11, 0};
inline constexpr std::int64_t kIds_266[] = {11, 1};
inline constexpr std::int64_t kIds_267[] = {11, 2};
inline constexpr std::int64_t kIds_268[] = {11, 3};
inline constexpr std::int64_t kIds_269[] = {11, 4};
inline constexpr std::int64_t kIds_270[] = {11, 5};
inline constexpr std::int64_t kIds_271[] = {11, 6};
inline constexpr std::int64_t kIds_272[] = {11, 7};
inline constexpr std::int64_t kIds_273[] = {11, 8};
inline constexpr std::int64_t kIds_274[] = {11, 9};
inline constexpr std::int64_t kIds_275[] = {11, 10};
inline constexpr std::int64_t kIds_276[] = {11, 11};
inline constexpr std::int64_t kIds_277[] = {11, 12};
inline constexpr std::int64_t kIds_278[] = {11, 13};
inline constexpr std::int64_t kIds_279[] = {11, 14};
inline constexpr std::int64_t kIds_280[] = {11, 15};
inline constexpr std::int64_t kIds_281[] = {11, 16};
inline constexpr std::int64_t kIds_282[] = {12, 0};
inline constexpr std::int64_t kIds_283[] = {12, 1};
inline constexpr std::int64_t kIds_284[] = {12, 2};
inline constexpr std::int64_t kIds_285[] = {12, 3};
inline constexpr std::int64_t kIds_286[] = {12, 4};
inline constexpr std::int64_t kIds_287[] = {12, 5};
inline constexpr std::int64_t kIds_288[] = {12, 6};
inline constexpr std::int64_t kIds_289[] = {12, 7};
inline constexpr std::int64_t kIds_290[] = {12, 8};
inline constexpr std::int64_t kIds_291[] = {12, 9};
inline constexpr std::int64_t kIds_292[] = {12, 10};
inline constexpr std::int64_t kIds_293[] = {12, 11};
inline constexpr std::int64_t kIds_294[] = {12, 12};
inline constexpr std::int64_t kIds_295[] = {12, 13};
inline constexpr std::int64_t kIds_296[] = {12, 14};
inline constexpr std::int64_t kIds_297[] = {12, 15};
inline constexpr std::int64_t kIds_298[] = {12, 16};
inline constexpr std::int64_t kIds_299[] = {13, 0};
inline constexpr std::int64_t kIds_300[] = {13, 1};
inline constexpr std::int64_t kIds_301[] = {13, 2};
inline constexpr std::int64_t kIds_302[] = {13, 3};
inline constexpr std::int64_t kIds_303[] = {13, 4};
inline constexpr std::int64_t kIds_304[] = {13, 5};
inline constexpr std::int64_t kIds_305[] = {13, 6};
inline constexpr std::int64_t kIds_306[] = {13, 7};
inline constexpr std::int64_t kIds_307[] = {13, 8};
inline constexpr std::int64_t kIds_308[] = {13, 9};
inline constexpr std::int64_t kIds_309[] = {13, 10};
inline constexpr std::int64_t kIds_310[] = {13, 11};
inline constexpr std::int64_t kIds_311[] = {13, 12};
inline constexpr std::int64_t kIds_312[] = {13, 13};
inline constexpr std::int64_t kIds_313[] = {13, 14};
inline constexpr std::int64_t kIds_314[] = {13, 15};
inline constexpr std::int64_t kIds_315[] = {13, 16};
inline constexpr std::int64_t kIds_316[] = {14, 0};
inline constexpr std::int64_t kIds_317[] = {14, 1};
inline constexpr std::int64_t kIds_318[] = {14, 2};
inline constexpr std::int64_t kIds_319[] = {14, 3};
inline constexpr std::int64_t kIds_320[] = {14, 4};
inline constexpr std::int64_t kIds_321[] = {14, 5};
inline constexpr std::int64_t kIds_322[] = {14, 6};
inline constexpr std::int64_t kIds_323[] = {14, 7};
inline constexpr std::int64_t kIds_324[] = {14, 8};
inline constexpr std::int64_t kIds_325[] = {14, 9};
inline constexpr std::int64_t kIds_326[] = {14, 10};
inline constexpr std::int64_t kIds_327[] = {14, 11};
inline constexpr std::int64_t kIds_328[] = {14, 12};
inline constexpr std::int64_t kIds_329[] = {14, 13};
inline constexpr std::int64_t kIds_330[] = {14, 14};
inline constexpr std::int64_t kIds_331[] = {14, 15};
inline constexpr std::int64_t kIds_332[] = {14, 16};
inline constexpr std::int64_t kIds_333[] = {15, 0};
inline constexpr std::int64_t kIds_334[] = {15, 1};
inline constexpr std::int64_t kIds_335[] = {15, 2};
inline constexpr std::int64_t kIds_336[] = {15, 3};
inline constexpr std::int64_t kIds_337[] = {15, 4};
inline constexpr std::int64_t kIds_338[] = {15, 5};
inline constexpr std::int64_t kIds_339[] = {15, 6};
inline constexpr std::int64_t kIds_340[] = {15, 7};
inline constexpr std::int64_t kIds_341[] = {15, 8};
inline constexpr std::int64_t kIds_342[] = {15, 9};
inline constexpr std::int64_t kIds_343[] = {15, 10};
inline constexpr std::int64_t kIds_344[] = {15, 11};
inline constexpr std::int64_t kIds_345[] = {15, 12};
inline constexpr std::int64_t kIds_346[] = {15, 13};
inline constexpr std::int64_t kIds_347[] = {15, 14};
inline constexpr std::int64_t kIds_348[] = {15, 15};
inline constexpr std::int64_t kIds_349[] = {15, 16};
inline constexpr std::int64_t kIds_350[] = {16, 0};
inline constexpr std::int64_t kIds_351[] = {16, 1};
inline constexpr std::int64_t kIds_352[] = {16, 2};
inline constexpr std::int64_t kIds_353[] = {16, 3};
inline constexpr std::int64_t kIds_354[] = {16, 4};
inline constexpr std::int64_t kIds_355[] = {16, 5};
inline constexpr std::int64_t kIds_356[] = {16, 6};
inline constexpr std::int64_t kIds_357[] = {16, 7};
inline constexpr std::int64_t kIds_358[] = {16, 8};
inline constexpr std::int64_t kIds_359[] = {16, 9};
inline constexpr std::int64_t kIds_360[] = {16, 10};
inline constexpr std::int64_t kIds_361[] = {16, 11};
inline constexpr std::int64_t kIds_362[] = {16, 12};
inline constexpr std::int64_t kIds_363[] = {16, 13};
inline constexpr std::int64_t kIds_364[] = {16, 14};
inline constexpr std::int64_t kIds_365[] = {16, 15};
inline constexpr std::int64_t kIds_366[] = {16, 16};
inline constexpr std::int64_t kIds_367[] = {44, 108, 486, 1714, 2692};
inline constexpr std::int64_t kIds_368[] = {486, 1714, 2692, 108, 46};
inline constexpr std::int64_t kIds_369[] = {44, 108, 486};
inline constexpr std::int64_t kIds_370[] = {44, 108, 306, 79, 16079};
inline constexpr std::int64_t kIds_371[] = {306, 79, 3738, 108, 46};
inline constexpr std::int64_t kIds_372[] = {44, 108, 2680, 14894, 9940, 18309, 108, 46};
inline constexpr std::int64_t kIds_373[] = {44, 108, 13182, 6228, 8060, 108, 46};
inline constexpr std::int64_t kIds_374[] = {44, 3256, 10437, 79, 6167, 46};
inline constexpr std::int64_t kIds_375[] = {44, 9302, 95, 79, 5820, 46};
inline constexpr std::int64_t kIds_376[] = {25276, 216, 5, 753, 216, 7, 310, 6004};
inline constexpr std::int64_t kIds_377[] = {17985, 216, 0, 990};
inline constexpr std::int64_t kIds_378[] = {17};
inline constexpr std::int64_t kIds_379[] = {254};
inline constexpr std::int64_t kIds_380[] = {419, 23282, 102, 1756};
inline constexpr std::int64_t kIds_381[] = {11510, 81};
inline constexpr std::int64_t kIds_382[] = {46880, 46880, 11510};
inline constexpr std::int64_t kIds_383[] = {25276, 25276};
inline constexpr std::int64_t kIds_384[] = {103, 15838, 739, 7918};
inline constexpr std::int64_t kIds_385[] = {504, 2365, 6354, 16438, 27003, 690, 260, 23790, 2767, 30, 378, 2365, 6354, 16438, 27003, 690, 260, 23790, 2767, 30, 378, 2365, 6354, 16438, 27003, 690, 260, 23790, 2767, 30, 378, 2365, 6354, 16438, 27003, 690, 260, 23790, 2767, 30, 216};

inline constexpr EncodeFixture kEncodeFixtures[] = {
    {"empty", 'A', std::string_view("", 0), kIds_0, 0},
    {"ascii_words", 'A', std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21\x20\x54\x68\x69\x73\x20\x69\x73\x20\x61\x20\x74\x65\x73\x74\x3a\x20\x31\x32\x33\x2e", 34), kIds_1, 14},
    {"ascii_punct_only", 'A', std::string_view("\x21\x40\x23\x24\x25\x5e\x26\x2a\x28\x29\x5f\x2b\x2d\x3d\x5b\x5d\x7b\x7d\x7c\x3b\x27\x3a\x22\x2c\x2e\x2f\x3c\x3e\x3f", 29), kIds_2, 21},
    {"contractions", 'A', std::string_view("\x64\x6f\x6e\x27\x74\x20\x69\x73\x6e\x27\x74\x20\x49\x27\x76\x65\x20\x49\x27\x6c\x6c\x20\x49\x27\x6d\x20\x49\x27\x64\x20\x77\x65\x27\x72\x65\x20\x63\x61\x6e\x27\x74", 41), kIds_3, 16},
    {"leading_ws", 'A', std::string_view("\x20\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 17), kIds_4, 3},
    {"trailing_ws", 'A', std::string_view("\x74\x72\x61\x69\x6c\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73\x20\x20\x20", 18), kIds_5, 4},
    {"repeated_ws", 'A', std::string_view("\x61\x20\x20\x20\x20\x62\x20\x20\x20\x20\x20\x63", 12), kIds_6, 5},
    {"tabs", 'A', std::string_view("\x61\x09\x62\x09\x63", 5), kIds_7, 5},
    {"lf", 'A', std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f", 17), kIds_8, 5},
    {"crlf", 'A', std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0d\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f", 18), kIds_9, 6},
    {"digit_long_run", 'A', std::string_view("\x54\x68\x65\x20\x79\x65\x61\x72\x20\x32\x30\x32\x33\x31\x32\x33\x31\x20\x77\x61\x73\x20\x6c\x6f\x6e\x67\x3a\x20\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39\x39", 40), kIds_10, 27},
    {"arabic_indic_digits", 'A', std::string_view("\xd9\xa1\xd9\xa2\xd9\xa3", 6), kIds_11, 6},
    {"fullwidth_digits", 'A', std::string_view("\xef\xbc\x91\xef\xbc\x92\xef\xbc\x93", 9), kIds_12, 6},
    {"non_ascii_latin", 'A', std::string_view("\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9\x20\x6e\x61\xc3\xaf\x76\x65\x20\x5a\xc3\xbc\x72\x69\x63\x68", 29), kIds_13, 12},
    {"non_ascii_cjk", 'A', std::string_view("\xe4\xbd\xa0\xe5\xa5\xbd\xe4\xb8\x96\xe7\x95\x8c\x20\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf\x20\xed\x95\x9c\xea\xb5\xad\xec\x96\xb4", 38), kIds_14, 27},
    {"emoji", 'A', std::string_view("\x48\x65\x6c\x6c\x6f\x20\xf0\x9f\x98\x80\x20\x57\x6f\x72\x6c\x64\x20\xf0\x9f\x98\x81\xf0\x9f\x98\x82", 25), kIds_15, 9},
    {"combining_marks", 'A', std::string_view("\xc3\xa9\x20\xc3\xa0\x20\xc3\xb1", 8), kIds_16, 4},
    {"embedded_nul", 'A', std::string_view("\x62\x65\x66\x6f\x72\x65\x00\x61\x66\x74\x65\x72", 12), kIds_17, 3},
    {"special_token_lookalike", 'A', std::string_view("\x74\x65\x78\x74\x20\x77\x69\x74\x68\x20\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x20\x69\x6e\x73\x69\x64\x65\x20\x61\x6e\x64\x20\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x20\x74\x6f\x6f", 51), kIds_18, 19},
    {"golden__empty_input", 'A', std::string_view("", 0), kIds_19, 0},
    {"golden__ascii_words", 'A', std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21\x20\x54\x68\x69\x73\x20\x69\x73\x20\x61\x20\x74\x65\x73\x74\x3a\x20\x31\x32\x33\x2e", 34), kIds_20, 14},
    {"golden__ascii_punctuation_only", 'A', std::string_view("\x21\x40\x23\x24\x25\x5e\x26\x2a\x28\x29\x5f\x2b\x2d\x3d\x5b\x5d\x7b\x7d\x7c\x3b\x27\x3a\x22\x2c\x2e\x2f\x3c\x3e\x3f", 29), kIds_21, 21},
    {"golden__leading_whitespace", 'A', std::string_view("\x20\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 17), kIds_22, 3},
    {"golden__trailing_whitespace", 'A', std::string_view("\x74\x72\x61\x69\x6c\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73\x20\x20\x20", 18), kIds_23, 4},
    {"golden__repeated_whitespace", 'A', std::string_view("\x61\x20\x20\x20\x20\x62\x20\x20\x20\x20\x20\x63", 12), kIds_24, 5},
    {"golden__newline_and_tab", 'A', std::string_view("\x6c\x69\x6e\x65\x20\x6f\x6e\x65\x0a\x6c\x69\x6e\x65\x20\x74\x77\x6f\x09\x61\x66\x74\x65\x72\x20\x74\x61\x62", 27), kIds_25, 8},
    {"golden__only_whitespace", 'A', std::string_view("\x20\x20\x20\x0a\x09\x20\x20", 7), kIds_26, 3},
    {"golden__non_ascii_latin", 'A', std::string_view("\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9\x20\x6e\x61\xc3\xaf\x76\x65\x20\x5a\xc3\xbc\x72\x69\x63\x68", 29), kIds_27, 12},
    {"golden__non_ascii_cjk", 'A', std::string_view("\xe4\xbd\xa0\xe5\xa5\xbd\xe4\xb8\x96\xe7\x95\x8c\x20\xe3\x81\x93\xe3\x82\x93\xe3\x81\xab\xe3\x81\xa1\xe3\x81\xaf\xe4\xb8\x96\xe7\x95\x8c\x20\xec\x95\x88\xeb\x85\x95\xed\x95\x98\xec\x84\xb8\xec\x9a\x94", 50), kIds_28, 37},
    {"golden__non_ascii_emoji", 'A', std::string_view("\x68\x65\x6c\x6c\x6f\x20\xf0\x9f\x98\x80\x20\x77\x6f\x72\x6c\x64\x20\xf0\x9f\x8c\x8d\x20\x65\x6d\x6f\x6a\x69\x20\xf0\x9f\x9a\x80", 32), kIds_29, 12},
    {"golden__non_ascii_combining_marks", 'A', std::string_view("\x65\xcc\x81\x20\x61\xcc\x80\x20\x6f\xcc\x82\x20\x63\x6f\x6d\x62\x69\x6e\x69\x6e\x67\x20\x64\x69\x61\x63\x72\x69\x74\x69\x63\x73", 32), kIds_30, 14},
    {"golden__text_resembling_special_tokens", 'A', std::string_view("\x74\x68\x69\x73\x20\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x20\x6c\x6f\x6f\x6b\x73\x20\x6c\x69\x6b\x65\x20\x61\x20\x73\x70\x65\x63\x69\x61\x6c\x20\x74\x6f\x6b\x65\x6e", 45), kIds_31, 13},
    {"golden__text_resembling_special_tokens_2", 'A', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x6e\x6f\x74\x20\x72\x65\x61\x6c\x6c\x79\x20\x61\x20\x63\x68\x61\x74\x20\x74\x75\x72\x6e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 44), kIds_32, 19},
    {"golden__unusual_unicode_ranges", 'A', std::string_view("\xe2\x98\x83\xe2\x9d\xa4\xef\xbb\xbf\x20\x6d\x69\x78\x65\x64\x20\x73\x79\x6d\x62\x6f\x6c\x73\x20\x61\x6e\x64\x20\x42\x4f\x4d\x2d\x6c\x69\x6b\x65\x20\x63\x68\x61\x72", 41), kIds_33, 17},
    {"golden__multibyte_utf8_boundary", 'A', std::string_view("\xf0\x9f\x98\x80\xf0\x9f\x98\x81\xf0\x9f\x98\x82\x20\x63\x6f\x6e\x73\x65\x63\x75\x74\x69\x76\x65\x20\x6d\x75\x6c\x74\x69\x62\x79\x74\x65\x20\x65\x6d\x6f\x6a\x69", 40), kIds_34, 16},
    {"golden__mixed_script", 'A', std::string_view("\x45\x6e\x67\x6c\x69\x73\x68\x20\xe4\xb8\xad\xe6\x96\x87\x20\xe6\x97\xa5\xe6\x9c\xac\xe8\xaa\x9e\x20\xed\x95\x9c\xea\xb5\xad\xec\x96\xb4\x20\xd8\xa7\xd9\x84\xd8\xb9\xd8\xb1\xd8\xa8\xd9\x8a\xd8\xa9", 49), kIds_35, 26},
    {"golden__embedded_nul_char", 'A', std::string_view("\x62\x65\x66\x6f\x72\x65\x00\x61\x66\x74\x65\x72", 12), kIds_36, 3},
    {"golden__encode_decode_encode_caveat", 'A', std::string_view("\x20\x20\x4d\x75\x6c\x74\x69\x70\x6c\x65\x20\x20\x20\x73\x70\x61\x63\x65\x73\x09\x61\x6e\x64\x09\x74\x61\x62\x73\x20\x20", 30), kIds_37, 10},
    {"rawprompt__OE-L0-SYNTH-1-control-tokens", 'A', std::string_view("\x3c\x31\x3e\x3c\x35\x3e\x3c\x39\x3e\x3c\x33\x3e\x3c\x37\x3e", 15), kIds_38, 11},
    {"rawprompt__smollm2-135m:'The capital of France is'", 'A', std::string_view("\x54\x68\x65\x20\x63\x61\x70\x69\x74\x61\x6c\x20\x6f\x66\x20\x46\x72\x61\x6e\x63\x65\x20\x69\x73", 24), kIds_39, 5},
    {"rawprompt__smollm2-135m:'Hello, world!'", 'A', std::string_view("\x48\x65\x6c\x6c\x6f\x2c\x20\x77\x6f\x72\x6c\x64\x21", 13), kIds_40, 4},
    {"rawprompt__smollm2-135m:'12345 test'", 'A', std::string_view("\x31\x32\x33\x34\x35\x20\x74\x65\x73\x74", 10), kIds_41, 6},
    {"rawprompt__smollm2-135m:'  leading spaces'", 'A', std::string_view("\x20\x20\x6c\x65\x61\x64\x69\x6e\x67\x20\x73\x70\x61\x63\x65\x73", 16), kIds_42, 3},
    {"rawprompt__smollm2-135m:'unicode: café résumé'", 'A', std::string_view("\x75\x6e\x69\x63\x6f\x64\x65\x3a\x20\x63\x61\x66\xc3\xa9\x20\x72\xc3\xa9\x73\x75\x6d\xc3\xa9", 23), kIds_43, 7},
    {"modeA_control__<|endoftext|>", 'A', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 13), kIds_44, 7},
    {"modeA_control__<|im_start|>", 'A', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 12), kIds_45, 7},
    {"modeA_control__<|im_end|>", 'A', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 10), kIds_46, 7},
    {"modeA_control__<repo_name>", 'A', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 11), kIds_47, 5},
    {"modeA_control__<reponame>", 'A', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 10), kIds_48, 3},
    {"modeA_control__<file_sep>", 'A', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 10), kIds_49, 5},
    {"modeA_control__<filename>", 'A', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 10), kIds_50, 3},
    {"modeA_control__<gh_stars>", 'A', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 10), kIds_51, 5},
    {"modeA_control__<issue_start>", 'A', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 13), kIds_52, 5},
    {"modeA_control__<issue_comment>", 'A', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 15), kIds_53, 5},
    {"modeA_control__<issue_closed>", 'A', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 14), kIds_54, 5},
    {"modeA_control__<jupyter_start>", 'A', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 15), kIds_55, 7},
    {"modeA_control__<jupyter_text>", 'A', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 14), kIds_56, 7},
    {"modeA_control__<jupyter_code>", 'A', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 14), kIds_57, 7},
    {"modeA_control__<jupyter_output>", 'A', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 16), kIds_58, 7},
    {"modeA_control__<jupyter_script>", 'A', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 16), kIds_59, 7},
    {"modeA_control__<empty_output>", 'A', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 14), kIds_60, 5},
    {"modeB_control__<|endoftext|>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 13), kIds_61, 1},
    {"modeB_control__<|im_start|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 12), kIds_62, 1},
    {"modeB_control__<|im_end|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 10), kIds_63, 1},
    {"modeB_control__<repo_name>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 11), kIds_64, 1},
    {"modeB_control__<reponame>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 10), kIds_65, 1},
    {"modeB_control__<file_sep>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 10), kIds_66, 1},
    {"modeB_control__<filename>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 10), kIds_67, 1},
    {"modeB_control__<gh_stars>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 10), kIds_68, 1},
    {"modeB_control__<issue_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 13), kIds_69, 1},
    {"modeB_control__<issue_comment>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 15), kIds_70, 1},
    {"modeB_control__<issue_closed>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 14), kIds_71, 1},
    {"modeB_control__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 15), kIds_72, 1},
    {"modeB_control__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 14), kIds_73, 1},
    {"modeB_control__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 14), kIds_74, 1},
    {"modeB_control__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 16), kIds_75, 1},
    {"modeB_control__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 16), kIds_76, 1},
    {"modeB_control__<empty_output>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 14), kIds_77, 1},
    {"modeB_adjacent__<|endoftext|>__<|endoftext|>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 26), kIds_78, 2},
    {"modeB_adjacent__<|endoftext|>__<|im_start|>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 25), kIds_79, 2},
    {"modeB_adjacent__<|endoftext|>__<|im_end|>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 23), kIds_80, 2},
    {"modeB_adjacent__<|endoftext|>__<repo_name>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 24), kIds_81, 2},
    {"modeB_adjacent__<|endoftext|>__<reponame>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 23), kIds_82, 2},
    {"modeB_adjacent__<|endoftext|>__<file_sep>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 23), kIds_83, 2},
    {"modeB_adjacent__<|endoftext|>__<filename>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 23), kIds_84, 2},
    {"modeB_adjacent__<|endoftext|>__<gh_stars>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 23), kIds_85, 2},
    {"modeB_adjacent__<|endoftext|>__<issue_start>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 26), kIds_86, 2},
    {"modeB_adjacent__<|endoftext|>__<issue_comment>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 28), kIds_87, 2},
    {"modeB_adjacent__<|endoftext|>__<issue_closed>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 27), kIds_88, 2},
    {"modeB_adjacent__<|endoftext|>__<jupyter_start>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 28), kIds_89, 2},
    {"modeB_adjacent__<|endoftext|>__<jupyter_text>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 27), kIds_90, 2},
    {"modeB_adjacent__<|endoftext|>__<jupyter_code>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 27), kIds_91, 2},
    {"modeB_adjacent__<|endoftext|>__<jupyter_output>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 29), kIds_92, 2},
    {"modeB_adjacent__<|endoftext|>__<jupyter_script>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 29), kIds_93, 2},
    {"modeB_adjacent__<|endoftext|>__<empty_output>", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 27), kIds_94, 2},
    {"modeB_adjacent__<|im_start|>__<|endoftext|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 25), kIds_95, 2},
    {"modeB_adjacent__<|im_start|>__<|im_start|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 24), kIds_96, 2},
    {"modeB_adjacent__<|im_start|>__<|im_end|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 22), kIds_97, 2},
    {"modeB_adjacent__<|im_start|>__<repo_name>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 23), kIds_98, 2},
    {"modeB_adjacent__<|im_start|>__<reponame>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 22), kIds_99, 2},
    {"modeB_adjacent__<|im_start|>__<file_sep>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 22), kIds_100, 2},
    {"modeB_adjacent__<|im_start|>__<filename>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 22), kIds_101, 2},
    {"modeB_adjacent__<|im_start|>__<gh_stars>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 22), kIds_102, 2},
    {"modeB_adjacent__<|im_start|>__<issue_start>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_103, 2},
    {"modeB_adjacent__<|im_start|>__<issue_comment>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 27), kIds_104, 2},
    {"modeB_adjacent__<|im_start|>__<issue_closed>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 26), kIds_105, 2},
    {"modeB_adjacent__<|im_start|>__<jupyter_start>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 27), kIds_106, 2},
    {"modeB_adjacent__<|im_start|>__<jupyter_text>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 26), kIds_107, 2},
    {"modeB_adjacent__<|im_start|>__<jupyter_code>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 26), kIds_108, 2},
    {"modeB_adjacent__<|im_start|>__<jupyter_output>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 28), kIds_109, 2},
    {"modeB_adjacent__<|im_start|>__<jupyter_script>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 28), kIds_110, 2},
    {"modeB_adjacent__<|im_start|>__<empty_output>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_111, 2},
    {"modeB_adjacent__<|im_end|>__<|endoftext|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 23), kIds_112, 2},
    {"modeB_adjacent__<|im_end|>__<|im_start|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 22), kIds_113, 2},
    {"modeB_adjacent__<|im_end|>__<|im_end|>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 20), kIds_114, 2},
    {"modeB_adjacent__<|im_end|>__<repo_name>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 21), kIds_115, 2},
    {"modeB_adjacent__<|im_end|>__<reponame>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 20), kIds_116, 2},
    {"modeB_adjacent__<|im_end|>__<file_sep>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 20), kIds_117, 2},
    {"modeB_adjacent__<|im_end|>__<filename>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 20), kIds_118, 2},
    {"modeB_adjacent__<|im_end|>__<gh_stars>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 20), kIds_119, 2},
    {"modeB_adjacent__<|im_end|>__<issue_start>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 23), kIds_120, 2},
    {"modeB_adjacent__<|im_end|>__<issue_comment>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 25), kIds_121, 2},
    {"modeB_adjacent__<|im_end|>__<issue_closed>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 24), kIds_122, 2},
    {"modeB_adjacent__<|im_end|>__<jupyter_start>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_123, 2},
    {"modeB_adjacent__<|im_end|>__<jupyter_text>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 24), kIds_124, 2},
    {"modeB_adjacent__<|im_end|>__<jupyter_code>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 24), kIds_125, 2},
    {"modeB_adjacent__<|im_end|>__<jupyter_output>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_126, 2},
    {"modeB_adjacent__<|im_end|>__<jupyter_script>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 26), kIds_127, 2},
    {"modeB_adjacent__<|im_end|>__<empty_output>", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 24), kIds_128, 2},
    {"modeB_adjacent__<repo_name>__<|endoftext|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 24), kIds_129, 2},
    {"modeB_adjacent__<repo_name>__<|im_start|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 23), kIds_130, 2},
    {"modeB_adjacent__<repo_name>__<|im_end|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 21), kIds_131, 2},
    {"modeB_adjacent__<repo_name>__<repo_name>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 22), kIds_132, 2},
    {"modeB_adjacent__<repo_name>__<reponame>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 21), kIds_133, 2},
    {"modeB_adjacent__<repo_name>__<file_sep>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 21), kIds_134, 2},
    {"modeB_adjacent__<repo_name>__<filename>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 21), kIds_135, 2},
    {"modeB_adjacent__<repo_name>__<gh_stars>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 21), kIds_136, 2},
    {"modeB_adjacent__<repo_name>__<issue_start>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 24), kIds_137, 2},
    {"modeB_adjacent__<repo_name>__<issue_comment>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 26), kIds_138, 2},
    {"modeB_adjacent__<repo_name>__<issue_closed>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 25), kIds_139, 2},
    {"modeB_adjacent__<repo_name>__<jupyter_start>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 26), kIds_140, 2},
    {"modeB_adjacent__<repo_name>__<jupyter_text>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 25), kIds_141, 2},
    {"modeB_adjacent__<repo_name>__<jupyter_code>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 25), kIds_142, 2},
    {"modeB_adjacent__<repo_name>__<jupyter_output>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 27), kIds_143, 2},
    {"modeB_adjacent__<repo_name>__<jupyter_script>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 27), kIds_144, 2},
    {"modeB_adjacent__<repo_name>__<empty_output>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 25), kIds_145, 2},
    {"modeB_adjacent__<reponame>__<|endoftext|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 23), kIds_146, 2},
    {"modeB_adjacent__<reponame>__<|im_start|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 22), kIds_147, 2},
    {"modeB_adjacent__<reponame>__<|im_end|>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 20), kIds_148, 2},
    {"modeB_adjacent__<reponame>__<repo_name>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 21), kIds_149, 2},
    {"modeB_adjacent__<reponame>__<reponame>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 20), kIds_150, 2},
    {"modeB_adjacent__<reponame>__<file_sep>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 20), kIds_151, 2},
    {"modeB_adjacent__<reponame>__<filename>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 20), kIds_152, 2},
    {"modeB_adjacent__<reponame>__<gh_stars>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 20), kIds_153, 2},
    {"modeB_adjacent__<reponame>__<issue_start>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 23), kIds_154, 2},
    {"modeB_adjacent__<reponame>__<issue_comment>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 25), kIds_155, 2},
    {"modeB_adjacent__<reponame>__<issue_closed>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 24), kIds_156, 2},
    {"modeB_adjacent__<reponame>__<jupyter_start>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_157, 2},
    {"modeB_adjacent__<reponame>__<jupyter_text>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 24), kIds_158, 2},
    {"modeB_adjacent__<reponame>__<jupyter_code>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 24), kIds_159, 2},
    {"modeB_adjacent__<reponame>__<jupyter_output>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_160, 2},
    {"modeB_adjacent__<reponame>__<jupyter_script>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 26), kIds_161, 2},
    {"modeB_adjacent__<reponame>__<empty_output>", 'B', std::string_view("\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 24), kIds_162, 2},
    {"modeB_adjacent__<file_sep>__<|endoftext|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 23), kIds_163, 2},
    {"modeB_adjacent__<file_sep>__<|im_start|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 22), kIds_164, 2},
    {"modeB_adjacent__<file_sep>__<|im_end|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 20), kIds_165, 2},
    {"modeB_adjacent__<file_sep>__<repo_name>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 21), kIds_166, 2},
    {"modeB_adjacent__<file_sep>__<reponame>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 20), kIds_167, 2},
    {"modeB_adjacent__<file_sep>__<file_sep>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 20), kIds_168, 2},
    {"modeB_adjacent__<file_sep>__<filename>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 20), kIds_169, 2},
    {"modeB_adjacent__<file_sep>__<gh_stars>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 20), kIds_170, 2},
    {"modeB_adjacent__<file_sep>__<issue_start>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 23), kIds_171, 2},
    {"modeB_adjacent__<file_sep>__<issue_comment>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 25), kIds_172, 2},
    {"modeB_adjacent__<file_sep>__<issue_closed>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 24), kIds_173, 2},
    {"modeB_adjacent__<file_sep>__<jupyter_start>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_174, 2},
    {"modeB_adjacent__<file_sep>__<jupyter_text>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 24), kIds_175, 2},
    {"modeB_adjacent__<file_sep>__<jupyter_code>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 24), kIds_176, 2},
    {"modeB_adjacent__<file_sep>__<jupyter_output>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_177, 2},
    {"modeB_adjacent__<file_sep>__<jupyter_script>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 26), kIds_178, 2},
    {"modeB_adjacent__<file_sep>__<empty_output>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 24), kIds_179, 2},
    {"modeB_adjacent__<filename>__<|endoftext|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 23), kIds_180, 2},
    {"modeB_adjacent__<filename>__<|im_start|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 22), kIds_181, 2},
    {"modeB_adjacent__<filename>__<|im_end|>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 20), kIds_182, 2},
    {"modeB_adjacent__<filename>__<repo_name>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 21), kIds_183, 2},
    {"modeB_adjacent__<filename>__<reponame>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 20), kIds_184, 2},
    {"modeB_adjacent__<filename>__<file_sep>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 20), kIds_185, 2},
    {"modeB_adjacent__<filename>__<filename>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 20), kIds_186, 2},
    {"modeB_adjacent__<filename>__<gh_stars>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 20), kIds_187, 2},
    {"modeB_adjacent__<filename>__<issue_start>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 23), kIds_188, 2},
    {"modeB_adjacent__<filename>__<issue_comment>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 25), kIds_189, 2},
    {"modeB_adjacent__<filename>__<issue_closed>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 24), kIds_190, 2},
    {"modeB_adjacent__<filename>__<jupyter_start>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_191, 2},
    {"modeB_adjacent__<filename>__<jupyter_text>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 24), kIds_192, 2},
    {"modeB_adjacent__<filename>__<jupyter_code>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 24), kIds_193, 2},
    {"modeB_adjacent__<filename>__<jupyter_output>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_194, 2},
    {"modeB_adjacent__<filename>__<jupyter_script>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 26), kIds_195, 2},
    {"modeB_adjacent__<filename>__<empty_output>", 'B', std::string_view("\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 24), kIds_196, 2},
    {"modeB_adjacent__<gh_stars>__<|endoftext|>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 23), kIds_197, 2},
    {"modeB_adjacent__<gh_stars>__<|im_start|>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 22), kIds_198, 2},
    {"modeB_adjacent__<gh_stars>__<|im_end|>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 20), kIds_199, 2},
    {"modeB_adjacent__<gh_stars>__<repo_name>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 21), kIds_200, 2},
    {"modeB_adjacent__<gh_stars>__<reponame>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 20), kIds_201, 2},
    {"modeB_adjacent__<gh_stars>__<file_sep>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 20), kIds_202, 2},
    {"modeB_adjacent__<gh_stars>__<filename>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 20), kIds_203, 2},
    {"modeB_adjacent__<gh_stars>__<gh_stars>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 20), kIds_204, 2},
    {"modeB_adjacent__<gh_stars>__<issue_start>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 23), kIds_205, 2},
    {"modeB_adjacent__<gh_stars>__<issue_comment>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 25), kIds_206, 2},
    {"modeB_adjacent__<gh_stars>__<issue_closed>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 24), kIds_207, 2},
    {"modeB_adjacent__<gh_stars>__<jupyter_start>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 25), kIds_208, 2},
    {"modeB_adjacent__<gh_stars>__<jupyter_text>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 24), kIds_209, 2},
    {"modeB_adjacent__<gh_stars>__<jupyter_code>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 24), kIds_210, 2},
    {"modeB_adjacent__<gh_stars>__<jupyter_output>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 26), kIds_211, 2},
    {"modeB_adjacent__<gh_stars>__<jupyter_script>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 26), kIds_212, 2},
    {"modeB_adjacent__<gh_stars>__<empty_output>", 'B', std::string_view("\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 24), kIds_213, 2},
    {"modeB_adjacent__<issue_start>__<|endoftext|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 26), kIds_214, 2},
    {"modeB_adjacent__<issue_start>__<|im_start|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 25), kIds_215, 2},
    {"modeB_adjacent__<issue_start>__<|im_end|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 23), kIds_216, 2},
    {"modeB_adjacent__<issue_start>__<repo_name>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 24), kIds_217, 2},
    {"modeB_adjacent__<issue_start>__<reponame>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 23), kIds_218, 2},
    {"modeB_adjacent__<issue_start>__<file_sep>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 23), kIds_219, 2},
    {"modeB_adjacent__<issue_start>__<filename>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 23), kIds_220, 2},
    {"modeB_adjacent__<issue_start>__<gh_stars>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 23), kIds_221, 2},
    {"modeB_adjacent__<issue_start>__<issue_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 26), kIds_222, 2},
    {"modeB_adjacent__<issue_start>__<issue_comment>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 28), kIds_223, 2},
    {"modeB_adjacent__<issue_start>__<issue_closed>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 27), kIds_224, 2},
    {"modeB_adjacent__<issue_start>__<jupyter_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 28), kIds_225, 2},
    {"modeB_adjacent__<issue_start>__<jupyter_text>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 27), kIds_226, 2},
    {"modeB_adjacent__<issue_start>__<jupyter_code>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 27), kIds_227, 2},
    {"modeB_adjacent__<issue_start>__<jupyter_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 29), kIds_228, 2},
    {"modeB_adjacent__<issue_start>__<jupyter_script>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 29), kIds_229, 2},
    {"modeB_adjacent__<issue_start>__<empty_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 27), kIds_230, 2},
    {"modeB_adjacent__<issue_comment>__<|endoftext|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 28), kIds_231, 2},
    {"modeB_adjacent__<issue_comment>__<|im_start|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 27), kIds_232, 2},
    {"modeB_adjacent__<issue_comment>__<|im_end|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 25), kIds_233, 2},
    {"modeB_adjacent__<issue_comment>__<repo_name>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 26), kIds_234, 2},
    {"modeB_adjacent__<issue_comment>__<reponame>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 25), kIds_235, 2},
    {"modeB_adjacent__<issue_comment>__<file_sep>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 25), kIds_236, 2},
    {"modeB_adjacent__<issue_comment>__<filename>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 25), kIds_237, 2},
    {"modeB_adjacent__<issue_comment>__<gh_stars>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 25), kIds_238, 2},
    {"modeB_adjacent__<issue_comment>__<issue_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 28), kIds_239, 2},
    {"modeB_adjacent__<issue_comment>__<issue_comment>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 30), kIds_240, 2},
    {"modeB_adjacent__<issue_comment>__<issue_closed>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 29), kIds_241, 2},
    {"modeB_adjacent__<issue_comment>__<jupyter_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 30), kIds_242, 2},
    {"modeB_adjacent__<issue_comment>__<jupyter_text>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 29), kIds_243, 2},
    {"modeB_adjacent__<issue_comment>__<jupyter_code>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 29), kIds_244, 2},
    {"modeB_adjacent__<issue_comment>__<jupyter_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 31), kIds_245, 2},
    {"modeB_adjacent__<issue_comment>__<jupyter_script>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 31), kIds_246, 2},
    {"modeB_adjacent__<issue_comment>__<empty_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 29), kIds_247, 2},
    {"modeB_adjacent__<issue_closed>__<|endoftext|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 27), kIds_248, 2},
    {"modeB_adjacent__<issue_closed>__<|im_start|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 26), kIds_249, 2},
    {"modeB_adjacent__<issue_closed>__<|im_end|>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 24), kIds_250, 2},
    {"modeB_adjacent__<issue_closed>__<repo_name>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 25), kIds_251, 2},
    {"modeB_adjacent__<issue_closed>__<reponame>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 24), kIds_252, 2},
    {"modeB_adjacent__<issue_closed>__<file_sep>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 24), kIds_253, 2},
    {"modeB_adjacent__<issue_closed>__<filename>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 24), kIds_254, 2},
    {"modeB_adjacent__<issue_closed>__<gh_stars>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 24), kIds_255, 2},
    {"modeB_adjacent__<issue_closed>__<issue_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 27), kIds_256, 2},
    {"modeB_adjacent__<issue_closed>__<issue_comment>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 29), kIds_257, 2},
    {"modeB_adjacent__<issue_closed>__<issue_closed>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 28), kIds_258, 2},
    {"modeB_adjacent__<issue_closed>__<jupyter_start>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_259, 2},
    {"modeB_adjacent__<issue_closed>__<jupyter_text>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 28), kIds_260, 2},
    {"modeB_adjacent__<issue_closed>__<jupyter_code>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 28), kIds_261, 2},
    {"modeB_adjacent__<issue_closed>__<jupyter_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_262, 2},
    {"modeB_adjacent__<issue_closed>__<jupyter_script>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 30), kIds_263, 2},
    {"modeB_adjacent__<issue_closed>__<empty_output>", 'B', std::string_view("\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 28), kIds_264, 2},
    {"modeB_adjacent__<jupyter_start>__<|endoftext|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 28), kIds_265, 2},
    {"modeB_adjacent__<jupyter_start>__<|im_start|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 27), kIds_266, 2},
    {"modeB_adjacent__<jupyter_start>__<|im_end|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 25), kIds_267, 2},
    {"modeB_adjacent__<jupyter_start>__<repo_name>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 26), kIds_268, 2},
    {"modeB_adjacent__<jupyter_start>__<reponame>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 25), kIds_269, 2},
    {"modeB_adjacent__<jupyter_start>__<file_sep>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 25), kIds_270, 2},
    {"modeB_adjacent__<jupyter_start>__<filename>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 25), kIds_271, 2},
    {"modeB_adjacent__<jupyter_start>__<gh_stars>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 25), kIds_272, 2},
    {"modeB_adjacent__<jupyter_start>__<issue_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 28), kIds_273, 2},
    {"modeB_adjacent__<jupyter_start>__<issue_comment>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 30), kIds_274, 2},
    {"modeB_adjacent__<jupyter_start>__<issue_closed>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 29), kIds_275, 2},
    {"modeB_adjacent__<jupyter_start>__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 30), kIds_276, 2},
    {"modeB_adjacent__<jupyter_start>__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 29), kIds_277, 2},
    {"modeB_adjacent__<jupyter_start>__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 29), kIds_278, 2},
    {"modeB_adjacent__<jupyter_start>__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 31), kIds_279, 2},
    {"modeB_adjacent__<jupyter_start>__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 31), kIds_280, 2},
    {"modeB_adjacent__<jupyter_start>__<empty_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 29), kIds_281, 2},
    {"modeB_adjacent__<jupyter_text>__<|endoftext|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 27), kIds_282, 2},
    {"modeB_adjacent__<jupyter_text>__<|im_start|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 26), kIds_283, 2},
    {"modeB_adjacent__<jupyter_text>__<|im_end|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 24), kIds_284, 2},
    {"modeB_adjacent__<jupyter_text>__<repo_name>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 25), kIds_285, 2},
    {"modeB_adjacent__<jupyter_text>__<reponame>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 24), kIds_286, 2},
    {"modeB_adjacent__<jupyter_text>__<file_sep>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 24), kIds_287, 2},
    {"modeB_adjacent__<jupyter_text>__<filename>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 24), kIds_288, 2},
    {"modeB_adjacent__<jupyter_text>__<gh_stars>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 24), kIds_289, 2},
    {"modeB_adjacent__<jupyter_text>__<issue_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 27), kIds_290, 2},
    {"modeB_adjacent__<jupyter_text>__<issue_comment>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 29), kIds_291, 2},
    {"modeB_adjacent__<jupyter_text>__<issue_closed>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 28), kIds_292, 2},
    {"modeB_adjacent__<jupyter_text>__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_293, 2},
    {"modeB_adjacent__<jupyter_text>__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 28), kIds_294, 2},
    {"modeB_adjacent__<jupyter_text>__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 28), kIds_295, 2},
    {"modeB_adjacent__<jupyter_text>__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_296, 2},
    {"modeB_adjacent__<jupyter_text>__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 30), kIds_297, 2},
    {"modeB_adjacent__<jupyter_text>__<empty_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 28), kIds_298, 2},
    {"modeB_adjacent__<jupyter_code>__<|endoftext|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 27), kIds_299, 2},
    {"modeB_adjacent__<jupyter_code>__<|im_start|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 26), kIds_300, 2},
    {"modeB_adjacent__<jupyter_code>__<|im_end|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 24), kIds_301, 2},
    {"modeB_adjacent__<jupyter_code>__<repo_name>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 25), kIds_302, 2},
    {"modeB_adjacent__<jupyter_code>__<reponame>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 24), kIds_303, 2},
    {"modeB_adjacent__<jupyter_code>__<file_sep>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 24), kIds_304, 2},
    {"modeB_adjacent__<jupyter_code>__<filename>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 24), kIds_305, 2},
    {"modeB_adjacent__<jupyter_code>__<gh_stars>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 24), kIds_306, 2},
    {"modeB_adjacent__<jupyter_code>__<issue_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 27), kIds_307, 2},
    {"modeB_adjacent__<jupyter_code>__<issue_comment>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 29), kIds_308, 2},
    {"modeB_adjacent__<jupyter_code>__<issue_closed>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 28), kIds_309, 2},
    {"modeB_adjacent__<jupyter_code>__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_310, 2},
    {"modeB_adjacent__<jupyter_code>__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 28), kIds_311, 2},
    {"modeB_adjacent__<jupyter_code>__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 28), kIds_312, 2},
    {"modeB_adjacent__<jupyter_code>__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_313, 2},
    {"modeB_adjacent__<jupyter_code>__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 30), kIds_314, 2},
    {"modeB_adjacent__<jupyter_code>__<empty_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 28), kIds_315, 2},
    {"modeB_adjacent__<jupyter_output>__<|endoftext|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 29), kIds_316, 2},
    {"modeB_adjacent__<jupyter_output>__<|im_start|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 28), kIds_317, 2},
    {"modeB_adjacent__<jupyter_output>__<|im_end|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 26), kIds_318, 2},
    {"modeB_adjacent__<jupyter_output>__<repo_name>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 27), kIds_319, 2},
    {"modeB_adjacent__<jupyter_output>__<reponame>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 26), kIds_320, 2},
    {"modeB_adjacent__<jupyter_output>__<file_sep>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 26), kIds_321, 2},
    {"modeB_adjacent__<jupyter_output>__<filename>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 26), kIds_322, 2},
    {"modeB_adjacent__<jupyter_output>__<gh_stars>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 26), kIds_323, 2},
    {"modeB_adjacent__<jupyter_output>__<issue_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_324, 2},
    {"modeB_adjacent__<jupyter_output>__<issue_comment>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 31), kIds_325, 2},
    {"modeB_adjacent__<jupyter_output>__<issue_closed>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 30), kIds_326, 2},
    {"modeB_adjacent__<jupyter_output>__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 31), kIds_327, 2},
    {"modeB_adjacent__<jupyter_output>__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 30), kIds_328, 2},
    {"modeB_adjacent__<jupyter_output>__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 30), kIds_329, 2},
    {"modeB_adjacent__<jupyter_output>__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 32), kIds_330, 2},
    {"modeB_adjacent__<jupyter_output>__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 32), kIds_331, 2},
    {"modeB_adjacent__<jupyter_output>__<empty_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_332, 2},
    {"modeB_adjacent__<jupyter_script>__<|endoftext|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 29), kIds_333, 2},
    {"modeB_adjacent__<jupyter_script>__<|im_start|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 28), kIds_334, 2},
    {"modeB_adjacent__<jupyter_script>__<|im_end|>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 26), kIds_335, 2},
    {"modeB_adjacent__<jupyter_script>__<repo_name>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 27), kIds_336, 2},
    {"modeB_adjacent__<jupyter_script>__<reponame>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 26), kIds_337, 2},
    {"modeB_adjacent__<jupyter_script>__<file_sep>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 26), kIds_338, 2},
    {"modeB_adjacent__<jupyter_script>__<filename>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 26), kIds_339, 2},
    {"modeB_adjacent__<jupyter_script>__<gh_stars>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 26), kIds_340, 2},
    {"modeB_adjacent__<jupyter_script>__<issue_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_341, 2},
    {"modeB_adjacent__<jupyter_script>__<issue_comment>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 31), kIds_342, 2},
    {"modeB_adjacent__<jupyter_script>__<issue_closed>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 30), kIds_343, 2},
    {"modeB_adjacent__<jupyter_script>__<jupyter_start>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 31), kIds_344, 2},
    {"modeB_adjacent__<jupyter_script>__<jupyter_text>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 30), kIds_345, 2},
    {"modeB_adjacent__<jupyter_script>__<jupyter_code>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 30), kIds_346, 2},
    {"modeB_adjacent__<jupyter_script>__<jupyter_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 32), kIds_347, 2},
    {"modeB_adjacent__<jupyter_script>__<jupyter_script>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 32), kIds_348, 2},
    {"modeB_adjacent__<jupyter_script>__<empty_output>", 'B', std::string_view("\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_349, 2},
    {"modeB_adjacent__<empty_output>__<|endoftext|>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 27), kIds_350, 2},
    {"modeB_adjacent__<empty_output>__<|im_start|>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 26), kIds_351, 2},
    {"modeB_adjacent__<empty_output>__<|im_end|>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x7c\x69\x6d\x5f\x65\x6e\x64\x7c\x3e", 24), kIds_352, 2},
    {"modeB_adjacent__<empty_output>__<repo_name>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x72\x65\x70\x6f\x5f\x6e\x61\x6d\x65\x3e", 25), kIds_353, 2},
    {"modeB_adjacent__<empty_output>__<reponame>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x72\x65\x70\x6f\x6e\x61\x6d\x65\x3e", 24), kIds_354, 2},
    {"modeB_adjacent__<empty_output>__<file_sep>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e", 24), kIds_355, 2},
    {"modeB_adjacent__<empty_output>__<filename>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x66\x69\x6c\x65\x6e\x61\x6d\x65\x3e", 24), kIds_356, 2},
    {"modeB_adjacent__<empty_output>__<gh_stars>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e", 24), kIds_357, 2},
    {"modeB_adjacent__<empty_output>__<issue_start>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x73\x74\x61\x72\x74\x3e", 27), kIds_358, 2},
    {"modeB_adjacent__<empty_output>__<issue_comment>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6f\x6d\x6d\x65\x6e\x74\x3e", 29), kIds_359, 2},
    {"modeB_adjacent__<empty_output>__<issue_closed>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x69\x73\x73\x75\x65\x5f\x63\x6c\x6f\x73\x65\x64\x3e", 28), kIds_360, 2},
    {"modeB_adjacent__<empty_output>__<jupyter_start>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x74\x61\x72\x74\x3e", 29), kIds_361, 2},
    {"modeB_adjacent__<empty_output>__<jupyter_text>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x74\x65\x78\x74\x3e", 28), kIds_362, 2},
    {"modeB_adjacent__<empty_output>__<jupyter_code>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x63\x6f\x64\x65\x3e", 28), kIds_363, 2},
    {"modeB_adjacent__<empty_output>__<jupyter_output>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x6f\x75\x74\x70\x75\x74\x3e", 30), kIds_364, 2},
    {"modeB_adjacent__<empty_output>__<jupyter_script>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x6a\x75\x70\x79\x74\x65\x72\x5f\x73\x63\x72\x69\x70\x74\x3e", 30), kIds_365, 2},
    {"modeB_adjacent__<empty_output>__<empty_output>", 'B', std::string_view("\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e\x3c\x65\x6d\x70\x74\x79\x5f\x6f\x75\x74\x70\x75\x74\x3e", 28), kIds_366, 2},
    {"modeB_nonmatch__<|endoftext", 'B', std::string_view("\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74", 11), kIds_367, 5},
    {"modeB_nonmatch__endoftext|>", 'B', std::string_view("\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e", 11), kIds_368, 5},
    {"modeB_nonmatch__<|end", 'B', std::string_view("\x3c\x7c\x65\x6e\x64", 5), kIds_369, 3},
    {"modeB_nonmatch__<|im_star", 'B', std::string_view("\x3c\x7c\x69\x6d\x5f\x73\x74\x61\x72", 9), kIds_370, 5},
    {"modeB_nonmatch__im_start|>", 'B', std::string_view("\x69\x6d\x5f\x73\x74\x61\x72\x74\x7c\x3e", 10), kIds_371, 5},
    {"modeB_nonmatch__<|ENDOFTEXT|>", 'B', std::string_view("\x3c\x7c\x45\x4e\x44\x4f\x46\x54\x45\x58\x54\x7c\x3e", 13), kIds_372, 8},
    {"modeB_nonmatch__<|EndOfText|>", 'B', std::string_view("\x3c\x7c\x45\x6e\x64\x4f\x66\x54\x65\x78\x74\x7c\x3e", 13), kIds_373, 7},
    {"modeB_nonmatch__<REPO_NAME>", 'B', std::string_view("\x3c\x52\x45\x50\x4f\x5f\x4e\x41\x4d\x45\x3e", 11), kIds_374, 6},
    {"modeB_nonmatch__<Repo_Name>", 'B', std::string_view("\x3c\x52\x65\x70\x6f\x5f\x4e\x61\x6d\x65\x3e", 11), kIds_375, 6},
    {"modeB_mixed_1", 'B', std::string_view("\x61\x62\x63\x20\x3c\x66\x69\x6c\x65\x5f\x73\x65\x70\x3e\x20\x64\x65\x66\x20\x3c\x67\x68\x5f\x73\x74\x61\x72\x73\x3e\x20\x67\x68\x69", 33), kIds_376, 8},
    {"modeB_mixed_2", 'B', std::string_view("\x62\x65\x66\x6f\x72\x65\x20\x3c\x7c\x65\x6e\x64\x6f\x66\x74\x65\x78\x74\x7c\x3e\x20\x61\x66\x74\x65\x72", 26), kIds_377, 4},
    {"bpe_no_merge_pretoken", 'A', std::string_view("\x21", 1), kIds_378, 1},
    {"bpe_one_merge", 'A', std::string_view("\x69\x6e", 2), kIds_379, 1},
    {"bpe_multi_sequential_merge", 'A', std::string_view("\x75\x6e\x62\x65\x6c\x69\x65\x76\x61\x62\x6c\x79", 12), kIds_380, 4},
    {"bpe_repeated_adjacent_pair", 'A', std::string_view("\x61\x61\x61", 3), kIds_381, 2},
    {"bpe_repeated_adjacent_pair_long", 'A', std::string_view("\x61\x61\x61\x61\x61\x61\x61\x61\x61\x61", 10), kIds_382, 3},
    {"bpe_multi_simultaneous_pairs", 'A', std::string_view("\x61\x62\x63\x61\x62\x63", 6), kIds_383, 2},
    {"bpe_competing_ranks", 'A', std::string_view("\x77\x6f\x6e\x64\x65\x72\x66\x75\x6c\x20\x74\x72\x61\x6e\x73\x66\x6f\x72\x6d\x61\x74\x69\x6f\x6e", 24), kIds_384, 4},
    {"bpe_long_bounded", 'A', std::string_view("\x54\x68\x65\x20\x71\x75\x69\x63\x6b\x20\x62\x72\x6f\x77\x6e\x20\x66\x6f\x78\x20\x6a\x75\x6d\x70\x73\x20\x6f\x76\x65\x72\x20\x74\x68\x65\x20\x6c\x61\x7a\x79\x20\x64\x6f\x67\x2e\x20\x54\x68\x65\x20\x71\x75\x69\x63\x6b\x20\x62\x72\x6f\x77\x6e\x20\x66\x6f\x78\x20\x6a\x75\x6d\x70\x73\x20\x6f\x76\x65\x72\x20\x74\x68\x65\x20\x6c\x61\x7a\x79\x20\x64\x6f\x67\x2e\x20\x54\x68\x65\x20\x71\x75\x69\x63\x6b\x20\x62\x72\x6f\x77\x6e\x20\x66\x6f\x78\x20\x6a\x75\x6d\x70\x73\x20\x6f\x76\x65\x72\x20\x74\x68\x65\x20\x6c\x61\x7a\x79\x20\x64\x6f\x67\x2e\x20\x54\x68\x65\x20\x71\x75\x69\x63\x6b\x20\x62\x72\x6f\x77\x6e\x20\x66\x6f\x78\x20\x6a\x75\x6d\x70\x73\x20\x6f\x76\x65\x72\x20\x74\x68\x65\x20\x6c\x61\x7a\x79\x20\x64\x6f\x67\x2e\x20", 180), kIds_385, 41},
};
inline constexpr std::size_t kEncodeFixtureCount = 386;

}  // namespace orcengine::pretok_fixtures
