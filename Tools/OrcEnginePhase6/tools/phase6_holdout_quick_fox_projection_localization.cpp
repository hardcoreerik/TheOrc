// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Gate 5 dedicated follow-up (Codex authority prompt,
// "Resume OrcEngine Phase 6 Stage 1 with the dedicated Gate 5 internal
// Q8_0 investigation"): projection-level (not whole-layer) localization
// of the internal DEV-derived F32-vs-Q8_0 tolerance failure on
// `holdout_quick_fox` (tolerance 0.973504, observed max_abs_error
// 1.079983). See fixtures/PHASE6_GATE5_STATUS.md's "Methodology for the
// dedicated Gate 5 follow-up" section (written BEFORE this file) for the
// exact definitions this tool implements.
//
// A NEW sibling tool, not an edit to
// phase6_holdout_quick_fox_layer_localization.cpp (per that file's own
// "Recommended next step", to avoid growing one file past reviewable
// size). This is a Phase-6-only diagnostic. It does NOT modify any
// frozen Phase 1-5C file:
//   - Whole-layer traversal reuses execute_cached_transformer_layer()
//     unchanged (same public seam the existing tool uses).
//   - Per-projection intermediates are obtained by calling the SAME
//     public ops:: functions execute_cached_transformer_layer_impl uses
//     internally, in the same order, for the SEVEN projection inputs
//     that function does not itself expose -- disclosed explicitly in
//     the methodology section as the one place this diagnostic
//     necessarily re-sequences (not re-derives) frozen math, since no
//     public seam exposes sub-layer intermediates.
//   - Raw Q8_0 block inspection (Gate 2) reads GgufArtifact tensor
//     offsets/lengths (Phase 2's own indexed metadata, read-only) and
//     the file's raw bytes directly -- it does not call or modify
//     Phase 2's loader/dequantization code path itself, except to
//     CROSS-VALIDATE this tool's independent block parser against the
//     frozen dequantize_q8_0_scalar_reference() in a known-value test.
#include <algorithm>
#include <array>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <functional>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

#include "orcengine/context.hpp"
#include "orcengine/forward_cached.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/ops.hpp"

using namespace orcengine;

namespace {

// ---------------------------------------------------------------------
// Reused verbatim from phase6_holdout_quick_fox_layer_localization.cpp
// (same formulas, same code -- see that file's own comments for the
// round-6 Gate 1B rationale). Copied, not shared via a library target,
// because no shared-diagnostic-library target exists yet for these free
// functions; disclosed in the methodology section, not silently done.
// ---------------------------------------------------------------------

struct DiffStats {
    float max_abs = 0.0f;
    double rmse = 0.0;
};

struct LocatedStats {
    DiffStats stats;
    int64_t max_position = -1;
    int64_t max_channel = -1;
};

LocatedStats effect_vector_stats(const std::vector<float>& a, const std::vector<float>& b,
                                 int64_t new_len, int64_t row_width) {
    if (a.size() != b.size() || a.size() != static_cast<size_t>(new_len * row_width)) {
        throw std::runtime_error("effect_vector_stats: shape mismatch");
    }
    LocatedStats result;
    double sq_sum = 0.0;
    for (int64_t p = 0; p < new_len; ++p) {
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float d = std::fabs(a[idx] - b[idx]);
            if (d > result.stats.max_abs) {
                result.stats.max_abs = d;
                result.max_position = p;
                result.max_channel = c;
            }
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
    }
    result.stats.rmse = std::sqrt(sq_sum / static_cast<double>(a.size()));
    return result;
}

LocatedStats factorial_interaction_stats(const std::vector<float>& out_ff, const std::vector<float>& out_qf,
                                         const std::vector<float>& out_fq, const std::vector<float>& out_qq,
                                         int64_t new_len, int64_t row_width) {
    const size_t n = out_ff.size();
    if (out_qf.size() != n || out_fq.size() != n || out_qq.size() != n ||
        n != static_cast<size_t>(new_len * row_width)) {
        throw std::runtime_error("factorial_interaction_stats: shape mismatch");
    }
    LocatedStats result;
    double sq_sum = 0.0;
    for (int64_t p = 0; p < new_len; ++p) {
        for (int64_t c = 0; c < row_width; ++c) {
            const size_t idx = static_cast<size_t>(p * row_width + c);
            const float interaction = out_qq[idx] - out_qf[idx] - out_fq[idx] + out_ff[idx];
            const float d = std::fabs(interaction);
            if (d > result.stats.max_abs) {
                result.stats.max_abs = d;
                result.max_position = p;
                result.max_channel = c;
            }
            sq_sum += static_cast<double>(d) * static_cast<double>(d);
        }
    }
    result.stats.rmse = std::sqrt(sq_sum / static_cast<double>(n));
    return result;
}

// New: L2 norm of an effect vector, restricted to one ROW (position) --
// the methodology section's "L2 norm of Δy" requirement, which the
// existing whole-layer tool never needed (it only reported max/RMSE).
double row_l2_norm_of_diff(const std::vector<float>& a, const std::vector<float>& b,
                           int64_t row, int64_t row_width) {
    double sq_sum = 0.0;
    for (int64_t c = 0; c < row_width; ++c) {
        const size_t idx = static_cast<size_t>(row * row_width + c);
        const double d = static_cast<double>(a[idx]) - static_cast<double>(b[idx]);
        sq_sum += d * d;
    }
    return std::sqrt(sq_sum);
}

void require_configs_equal(const ModelConfig& f32_cfg, const ModelConfig& q8_cfg) {
    const bool equal = f32_cfg.vocab == q8_cfg.vocab && f32_cfg.hidden == q8_cfg.hidden &&
                       f32_cfg.intermediate == q8_cfg.intermediate && f32_cfg.n_layers == q8_cfg.n_layers &&
                       f32_cfg.n_q_heads == q8_cfg.n_q_heads && f32_cfg.n_kv_heads == q8_cfg.n_kv_heads &&
                       f32_cfg.head_dim == q8_cfg.head_dim && f32_cfg.max_positions == q8_cfg.max_positions &&
                       f32_cfg.rmsnorm_epsilon == q8_cfg.rmsnorm_epsilon && f32_cfg.rope_theta == q8_cfg.rope_theta;
    if (!equal) {
        throw std::runtime_error("F32 and Q8_0 model configs differ -- refusing to reuse one config for both paths");
    }
}

struct PromptCase {
    std::string id;
    std::string role;
    std::vector<int64_t> token_ids;
};

// Same 3 prompts, same token IDs, as the existing whole-layer tool.
const std::vector<PromptCase> kPrompts = {
    {"holdout_quick_fox", "FAILING", {504, 2365, 6354, 16438}},
    {"dev_code_snippet", "DEV_below_tolerance", {1604, 803, 24, 81, 28, 278, 727, 1003}},
    {"holdout_hello_world", "HOLDOUT_agreeing", {19556, 905, 28, 451, 314}},
};

const std::vector<int64_t> kImplicatedLayers = {11, 28};

int g_selftest_failures = 0;
void check_and_report(const std::string& name, bool ok) {
    std::printf("[%s] %s\n", ok ? "PASS" : "FAIL", name.c_str());
    if (!ok) ++g_selftest_failures;
}

// ---------------------------------------------------------------------
// Gate 2: independent Q8_0 raw-block inspection.
//
// Disclosed unavailable seam (see PHASE6_GATE5_STATUS.md's methodology
// section): Phase 2's own half_to_float() is anonymous-namespace-local
// to gguf.cpp (internal linkage), not reachable from this translation
// unit, and gguf.hpp does not export it. This is a standard, fully
// specified IEEE-754 binary16 decode, independently reimplemented here
// (not copied), then cross-validated against the frozen
// dequantize_q8_0_scalar_reference() in the known-value test below --
// the two must agree on identical input bytes or that test fails.
// ---------------------------------------------------------------------

float half_to_float_independent(uint16_t half) {
    const uint32_t sign = static_cast<uint32_t>(half & 0x8000U) << 16;
    uint32_t exponent = (half >> 10) & 0x1FU;
    uint32_t mantissa = half & 0x03FFU;
    uint32_t bits;
    if (exponent == 0) {
        if (mantissa == 0) {
            bits = sign;
        } else {
            // Subnormal half -> normalize into a normal single.
            exponent = 127 - 15 + 1;
            while ((mantissa & 0x0400U) == 0) {
                mantissa <<= 1;
                --exponent;
            }
            mantissa &= 0x03FFU;
            bits = sign | (exponent << 23) | (mantissa << 13);
        }
    } else if (exponent == 0x1FU) {
        // Inf/NaN.
        bits = sign | 0x7F800000U | (mantissa << 13);
    } else {
        bits = sign | ((exponent - 15 + 127) << 23) | (mantissa << 13);
    }
    return std::bit_cast<float>(bits);
}

struct Q8_0Block {
    float scale = 0.0f;
    uint16_t scale_bits = 0;
    std::array<int8_t, 32> qi{};
};

Q8_0Block parse_q8_0_block(const uint8_t* block_bytes) {
    Q8_0Block b;
    b.scale_bits = static_cast<uint16_t>(block_bytes[0]) |
                  static_cast<uint16_t>(static_cast<uint16_t>(block_bytes[1]) << 8);
    b.scale = half_to_float_independent(b.scale_bits);
    for (int i = 0; i < 32; ++i) {
        b.qi[static_cast<size_t>(i)] = std::bit_cast<int8_t>(block_bytes[2 + i]);
    }
    return b;
}

std::vector<uint8_t> read_file_range(const std::string& path, uint64_t offset, uint64_t length) {
    std::ifstream f(path, std::ios::binary);
    if (!f) throw std::runtime_error("cannot open " + path + " for raw block inspection");
    f.seekg(static_cast<std::streamoff>(offset));
    std::vector<uint8_t> buf(static_cast<size_t>(length));
    f.read(reinterpret_cast<char*>(buf.data()), static_cast<std::streamsize>(length));
    if (!f) throw std::runtime_error("short read from " + path + " at offset " + std::to_string(offset));
    return buf;
}

const GgufTensorInfo& find_tensor(const GgufArtifact& artifact, const std::string& name) {
    for (const auto& t : artifact.tensors) {
        if (t.name == name) return t;
    }
    throw std::runtime_error("tensor not found: " + name);
}

struct BlockScaleStats {
    int64_t block_count = 0;
    float scale_min = 0.0f, scale_median = 0.0f, scale_mean = 0.0f, scale_max = 0.0f;
    float scale_p10 = 0.0f, scale_p90 = 0.0f;
    // Round-of-hardening (Codex review of the Gate 5 follow-up): a zero
    // scale can represent a legitimate all-zero block (nothing malformed
    // about it), so it is now tracked SEPARATELY from non-finite scales,
    // which remain fail-closed. Conflating the two under one
    // "invalid_scale_count" previously misclassified a valid block.
    int64_t zero_scale_count = 0;       // noteworthy, NOT malformed
    int64_t nonfinite_scale_count = 0;  // malformed, fail-closed
    float recon_max_abs = 0.0f;
    double recon_rmse = 0.0;
    std::vector<int64_t> worst_block_indices;  // top 3 by max-abs reconstruction error
};

// Inspects every Q8_0 block of one named tensor: decodes each block via
// THIS diagnostic's own independent parser, compares against the
// corresponding F32 reference tensor's values (the ground truth this
// block is quantizing), and separately cross-checks against the frozen
// production dequantizer's output for the SAME bytes (parity, not a
// second ground truth).
BlockScaleStats inspect_q8_0_tensor(const std::string& q8_path, const GgufTensorInfo& q8_tensor,
                                    const std::vector<float>& f32_reference_flat) {
    const int64_t elements = q8_tensor.logical_shape.element_count();
    // Round-of-hardening (Codex review): fail closed on every extent
    // mismatch BEFORE any indexing into f32_reference_flat below --
    // previously this function indexed f32_reference_flat[idx] without
    // first proving its size matched the Q8 tensor's element count, an
    // out-of-bounds read risk if a caller ever passed a mismatched pair.
    if (elements <= 0) {
        throw std::runtime_error("tensor '" + q8_tensor.name + "' has a non-positive element count (" +
                                 std::to_string(elements) + ")");
    }
    if (elements % kQ8_0BlockElements != 0) {
        throw std::runtime_error("tensor '" + q8_tensor.name + "' element count not a multiple of 32");
    }
    if (static_cast<int64_t>(f32_reference_flat.size()) != elements) {
        throw std::runtime_error("tensor '" + q8_tensor.name + "' F32 reference has " +
                                 std::to_string(f32_reference_flat.size()) + " elements, expected exactly " +
                                 std::to_string(elements) + " to match the Q8_0 tensor's own element count "
                                 "-- refusing to index a mismatched reference");
    }
    const int64_t block_count = elements / kQ8_0BlockElements;
    const uint64_t total_bytes = static_cast<uint64_t>(block_count) * static_cast<uint64_t>(kQ8_0BlockBytes);
    // The indexed artifact's own encoded_length is DERIVED from this exact
    // same (element_count / block_elements) * block_bytes formula at index
    // time (Tools/OrcEnginePhase2/src/gguf.cpp:436, index_gguf()) -- so
    // total_bytes == q8_tensor.encoded_length is an invariant already
    // proven by the indexer, not something this diagnostic independently
    // declares. Checked directly anyway (cheap, and self-documents the
    // invariant rather than only asserting it in a comment).
    if (total_bytes != q8_tensor.encoded_length) {
        throw std::runtime_error("tensor '" + q8_tensor.name + "' computed Q8_0 backing-byte requirement (" +
                                 std::to_string(total_bytes) + ") disagrees with the indexed artifact's own "
                                 "encoded_length (" + std::to_string(q8_tensor.encoded_length) +
                                 ") -- this should be impossible per index_gguf()'s own derivation; refusing "
                                 "to read a tensor whose own indexed metadata is internally inconsistent");
    }
    const std::vector<uint8_t> raw = read_file_range(q8_path, q8_tensor.absolute_offset, total_bytes);

    // Cross-check against the frozen production dequantizer on the SAME
    // bytes -- parity check, not a second ground truth (see methodology).
    const std::vector<float> production_dequant = dequantize_q8_0_scalar_reference(raw, elements);

    BlockScaleStats stats;
    stats.block_count = block_count;
    std::vector<float> scales(static_cast<size_t>(block_count));
    std::vector<std::pair<float, int64_t>> block_recon_max_abs;  // (error, block_index)
    double recon_sq_sum = 0.0;
    float recon_max_abs = 0.0f;
    double scale_sum = 0.0;

    for (int64_t b = 0; b < block_count; ++b) {
        const Q8_0Block block = parse_q8_0_block(raw.data() + static_cast<size_t>(b) * kQ8_0BlockBytes);
        scales[static_cast<size_t>(b)] = block.scale;
        scale_sum += static_cast<double>(block.scale);
        // Zero is a legitimate scale for an all-zero block -- tracked
        // separately, never conflated with a genuinely malformed
        // (non-finite) scale, which remains fail-closed-reportable.
        if (block.scale == 0.0f) ++stats.zero_scale_count;
        if (!std::isfinite(block.scale)) ++stats.nonfinite_scale_count;

        float block_max_abs = 0.0f;
        for (int64_t i = 0; i < kQ8_0BlockElements; ++i) {
            const size_t idx = static_cast<size_t>(b * kQ8_0BlockElements + i);
            const float independent_value = static_cast<float>(block.qi[static_cast<size_t>(i)]) * block.scale;
            // Parity: this diagnostic's own decode must match production's.
            const float parity_diff = std::fabs(independent_value - production_dequant[idx]);
            if (parity_diff > 1e-6f) {
                throw std::runtime_error("independent Q8_0 decode DISAGREES with production dequantize_q8_0_"
                                         "scalar_reference() for tensor '" + q8_tensor.name + "' block " +
                                         std::to_string(b) + " element " + std::to_string(i) +
                                         " -- stopping rather than reporting numbers from a diverged parser");
            }
            const float recon_err = std::fabs(independent_value - f32_reference_flat[idx]);
            block_max_abs = std::max(block_max_abs, recon_err);
            recon_max_abs = std::max(recon_max_abs, recon_err);
            recon_sq_sum += static_cast<double>(recon_err) * static_cast<double>(recon_err);
        }
        block_recon_max_abs.emplace_back(block_max_abs, b);
    }

    std::vector<float> sorted_scales = scales;
    std::sort(sorted_scales.begin(), sorted_scales.end());
    auto pct = [&](double p) -> float {
        if (sorted_scales.empty()) return 0.0f;
        const size_t idx = static_cast<size_t>(p * static_cast<double>(sorted_scales.size() - 1));
        return sorted_scales[idx];
    };
    stats.scale_min = sorted_scales.front();
    stats.scale_max = sorted_scales.back();
    stats.scale_median = pct(0.5);
    stats.scale_p10 = pct(0.10);
    stats.scale_p90 = pct(0.90);
    stats.scale_mean = static_cast<float>(scale_sum / static_cast<double>(block_count));
    stats.recon_max_abs = recon_max_abs;
    stats.recon_rmse = std::sqrt(recon_sq_sum / static_cast<double>(elements));

    std::sort(block_recon_max_abs.begin(), block_recon_max_abs.end(),
             [](const auto& a, const auto& b) { return a.first > b.first; });
    for (size_t i = 0; i < 3 && i < block_recon_max_abs.size(); ++i) {
        stats.worst_block_indices.push_back(block_recon_max_abs[i].second);
    }
    return stats;
}

// Reads a named F32 tensor's raw bytes directly and returns it as a flat
// float vector -- read-only, own diagnostic-local parsing (F32 storage
// is just contiguous native floats, no block structure), NOT a call into
// Phase 2's loader.
std::vector<float> read_f32_tensor_flat(const std::string& f32_path, const GgufTensorInfo& f32_tensor) {
    const int64_t elements = f32_tensor.logical_shape.element_count();
    const std::vector<uint8_t> raw = read_file_range(f32_path, f32_tensor.absolute_offset,
                                                      static_cast<uint64_t>(elements) * 4);
    std::vector<float> out(static_cast<size_t>(elements));
    std::memcpy(out.data(), raw.data(), raw.size());
    return out;
}

// Gate 2 known-value test: a hand-constructed 2-block Q8_0 buffer with
// KNOWN scale bit patterns and KNOWN signed int8 values, decoded via
// THIS file's independent parser and via the frozen production
// dequantizer, both checked against hand-computed expected floats.
void run_known_value_block_test() {
    std::printf("=== Gate 2 self-test: known-value Q8_0 block decode ===\n");
    // Block 0: scale = 1.0 (F16 bit pattern 0x3C00), values [1, -1, 2, -2, 0, ...].
    // Block 1: scale = 0.5 (F16 bit pattern 0x3800), values [127, -128, 3, -3, 0, ...].
    // Block 2 (round-of-hardening addition): scale = 0.0 (F16 bit pattern
    // 0x0000) with NONZERO int8 values -- a legitimate all-zero block
    // (proves zero scale is decoded as all-zero output regardless of the
    // stored int8 bytes, and is classified separately from a genuinely
    // malformed non-finite scale; see BlockScaleStats).
    std::vector<uint8_t> raw(3 * kQ8_0BlockBytes, 0);
    // Block 0 scale bits 0x3C00, little-endian.
    raw[0] = 0x00; raw[1] = 0x3C;
    const std::vector<int8_t> block0_qi = {1, -1, 2, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                           0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
    for (int i = 0; i < 32; ++i) raw[2 + static_cast<size_t>(i)] = std::bit_cast<uint8_t>(block0_qi[static_cast<size_t>(i)]);
    // Block 1 scale bits 0x3800, little-endian.
    raw[kQ8_0BlockBytes + 0] = 0x00; raw[kQ8_0BlockBytes + 1] = 0x38;
    const std::vector<int8_t> block1_qi = {127, -128, 3, -3, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                                           0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
    for (int i = 0; i < 32; ++i) {
        raw[kQ8_0BlockBytes + 2 + static_cast<size_t>(i)] = std::bit_cast<uint8_t>(block1_qi[static_cast<size_t>(i)]);
    }
    // Block 2 scale bits 0x0000 (zero), int8 values deliberately NONZERO
    // (e.g. 42) to prove the OUTPUT is zero because scale is zero, not
    // because the int8 payload happens to be zero.
    raw[2 * kQ8_0BlockBytes + 0] = 0x00; raw[2 * kQ8_0BlockBytes + 1] = 0x00;
    for (int i = 0; i < 32; ++i) {
        raw[2 * kQ8_0BlockBytes + 2 + static_cast<size_t>(i)] = std::bit_cast<uint8_t>(static_cast<int8_t>(42));
    }

    const Q8_0Block b0 = parse_q8_0_block(raw.data());
    const Q8_0Block b1 = parse_q8_0_block(raw.data() + kQ8_0BlockBytes);
    const Q8_0Block b2 = parse_q8_0_block(raw.data() + 2 * kQ8_0BlockBytes);
    check_and_report("block 0 scale decodes to exactly 1.0 from F16 bits 0x3C00",
                     std::fabs(b0.scale - 1.0f) < 1e-9f);
    check_and_report("block 1 scale decodes to exactly 0.5 from F16 bits 0x3800",
                     std::fabs(b1.scale - 0.5f) < 1e-9f);
    check_and_report("block 0 signed int8 values decoded correctly (1,-1,2,-2)",
                     b0.qi[0] == 1 && b0.qi[1] == -1 && b0.qi[2] == 2 && b0.qi[3] == -2);
    check_and_report("block 1 signed int8 values decoded correctly (127,-128,3,-3) "
                     "-- proves std::bit_cast sign handling at the int8 extremes",
                     b1.qi[0] == 127 && b1.qi[1] == -128 && b1.qi[2] == 3 && b1.qi[3] == -3);
    check_and_report("block 2 scale decodes to exactly 0.0 from F16 bits 0x0000 (a legitimate value, "
                     "not treated as malformed)", b2.scale == 0.0f && std::isfinite(b2.scale));
    check_and_report("block 2 int8 payload is 42 (nonzero) but scale=0.0", b2.qi[0] == 42);

    const std::vector<float> production = dequantize_q8_0_scalar_reference(raw, 96);
    bool block2_all_zero = true;
    for (int i = 0; i < 32; ++i) {
        if (production[static_cast<size_t>(64 + i)] != 0.0f) block2_all_zero = false;
    }
    check_and_report("frozen dequantize_q8_0_scalar_reference() decodes the zero-scale block to all "
                     "zeros despite a nonzero int8 payload (42 * 0.0 = 0.0)", block2_all_zero);
    const bool block0_matches_production =
        std::fabs(production[0] - 1.0f) < 1e-6f && std::fabs(production[1] - (-1.0f)) < 1e-6f &&
        std::fabs(production[2] - 2.0f) < 1e-6f && std::fabs(production[3] - (-2.0f)) < 1e-6f;
    const bool block1_matches_production =
        std::fabs(production[32] - 63.5f) < 1e-6f && std::fabs(production[33] - (-64.0f)) < 1e-6f &&
        std::fabs(production[34] - 1.5f) < 1e-6f && std::fabs(production[35] - (-1.5f)) < 1e-6f;
    check_and_report("frozen dequantize_q8_0_scalar_reference() agrees with hand-computed block 0 "
                     "values (1*1.0, -1*1.0, 2*1.0, -2*1.0)", block0_matches_production);
    check_and_report("frozen dequantize_q8_0_scalar_reference() agrees with hand-computed block 1 "
                     "values (127*0.5, -128*0.5, 3*0.5, -3*0.5)", block1_matches_production);

    // Reconstruction-error accounting: this diagnostic's own recon-error
    // computation against a KNOWN F32 reference must report exactly 0 when
    // the "F32 reference" IS the exact dequantized values (no error
    // introduced), and a KNOWN nonzero value when perturbed by a known amount.
    std::vector<float> exact_reference = production;
    double sq_sum = 0.0;
    float max_abs = 0.0f;
    for (size_t i = 0; i < production.size(); ++i) {
        const float d = std::fabs(production[i] - exact_reference[i]);
        max_abs = std::max(max_abs, d);
        sq_sum += static_cast<double>(d) * static_cast<double>(d);
    }
    check_and_report("reconstruction error against an EXACT reference is exactly 0 (max_abs and RMSE)",
                     max_abs == 0.0f && sq_sum == 0.0);
    exact_reference[0] += 0.25f;  // perturb one known element by a known amount
    float perturbed_max_abs = 0.0f;
    for (size_t i = 0; i < production.size(); ++i) {
        perturbed_max_abs = std::max(perturbed_max_abs, std::fabs(production[i] - exact_reference[i]));
    }
    check_and_report("reconstruction error against a reference perturbed by exactly 0.25 at one element "
                     "reports max_abs=0.25", std::fabs(perturbed_max_abs - 0.25f) < 1e-6f);
    std::printf("\n");
}

// Round-of-hardening negative regression (Codex review): inspect_q8_0_
// tensor() previously indexed f32_reference_flat[idx] without first
// proving its size matched the Q8 tensor's own element count. Proves a
// mismatched reference is REJECTED with a clear diagnostic, not read
// out of bounds.
void run_extent_mismatch_regression() {
    std::printf("=== Gate 1 hardening self-test: extent-mismatch rejection ===\n");
    // One real, well-formed Q8_0 block (32 elements) written to a temp
    // file, so inspect_q8_0_tensor() has genuine bytes to (refuse to) read.
    std::vector<uint8_t> raw(kQ8_0BlockBytes, 0);
    raw[0] = 0x00; raw[1] = 0x3C;  // scale = 1.0
    for (int i = 0; i < 32; ++i) raw[2 + static_cast<size_t>(i)] = std::bit_cast<uint8_t>(static_cast<int8_t>(1));

    const std::string temp_path = std::filesystem::temp_directory_path().string() +
                                  "/phase6_gate5_extent_regression.bin";
    {
        std::ofstream out(temp_path, std::ios::binary);
        out.write(reinterpret_cast<const char*>(raw.data()), static_cast<std::streamsize>(raw.size()));
    }

    GgufTensorInfo fake_tensor;
    fake_tensor.name = "regression_test_tensor";
    fake_tensor.logical_shape = TensorShape({kQ8_0BlockElements});
    fake_tensor.encoding = GgufTensorEncoding::Q8_0;
    fake_tensor.absolute_offset = 0;
    fake_tensor.encoded_length = static_cast<uint64_t>(kQ8_0BlockBytes);

    // Deliberately WRONG size: 16 elements instead of the required 32.
    const std::vector<float> mismatched_reference(16, 0.0f);
    bool threw_as_expected = false;
    std::string thrown_message;
    try {
        (void)inspect_q8_0_tensor(temp_path, fake_tensor, mismatched_reference);
    } catch (const std::exception& ex) {
        threw_as_expected = true;
        thrown_message = ex.what();
    }
    check_and_report("mismatched F32 reference size (16 vs required 32) is rejected with a clear "
                     "diagnostic, not read out of bounds", threw_as_expected);
    if (threw_as_expected) {
        check_and_report("rejection message names the actual vs expected element counts",
                         thrown_message.find("16") != std::string::npos &&
                         thrown_message.find("32") != std::string::npos);
    }

    // Matching size (32) must NOT throw for this reason -- proves the
    // check is precise, not merely rejecting everything.
    const std::vector<float> matching_reference(32, 0.0f);
    bool threw_unexpectedly = false;
    try {
        (void)inspect_q8_0_tensor(temp_path, fake_tensor, matching_reference);
    } catch (const std::exception&) {
        threw_unexpectedly = true;
    }
    check_and_report("a correctly-sized F32 reference (32 elements) does not throw",
                     !threw_unexpectedly);

    std::filesystem::remove(temp_path);
    std::printf("\n");
}

}  // namespace

int main(int argc, char** argv) {
    run_known_value_block_test();
    run_extent_mismatch_regression();
    if (g_selftest_failures != 0) {
        std::fprintf(stderr, "Gate 2 known-value self-test FAILED -- aborting before any model I/O\n");
        return 1;
    }
    if (argc != 3) {
        std::fprintf(stderr, "usage: phase6_holdout_quick_fox_projection_localization F32.gguf Q8_0.gguf\n");
        return 2;
    }
    const std::string f32_path = argv[1];
    const std::string q8_path = argv[2];
    try {
        const GgufArtifact f32_artifact = index_gguf(f32_path);
        const GgufArtifact q8_artifact = index_gguf(q8_path);
        const Model f32_model = materialize_gguf_model(map_llama_model(f32_artifact));
        const Model q8_model = materialize_gguf_model(map_llama_model(q8_artifact));
        require_configs_equal(f32_model.config(), q8_model.config());
        const ModelConfig& cfg = f32_model.config();
        const int64_t hidden = cfg.hidden;
        const int64_t q_dim = cfg.n_q_heads * cfg.head_dim;
        const int64_t kv_dim = cfg.n_kv_heads * cfg.head_dim;
        const int64_t group_size = cfg.group_size();
        const float attn_scale = 1.0f / std::sqrt(static_cast<float>(cfg.head_dim));

        std::printf("=== Gate 5 dedicated follow-up: projection-level localization ===\n");
        std::printf("model: vocab=%lld hidden=%lld n_layers=%lld (F32/Q8_0 configs verified equal)\n\n",
                    (long long)cfg.vocab, (long long)cfg.hidden, (long long)cfg.n_layers);

        for (const PromptCase& pc : kPrompts) {
            const int64_t new_len = static_cast<int64_t>(pc.token_ids.size());
            std::printf("\n########## prompt: %s (role=%s) ##########\n", pc.id.c_str(), pc.role.c_str());

            std::vector<float> x_f32 = ops::embedding_lookup(f32_model.token_embedding.raw(), hidden, pc.token_ids);
            std::vector<float> x_q8 = ops::embedding_lookup(q8_model.token_embedding.raw(), hidden, pc.token_ids);
            std::vector<std::vector<float>> cos_by_pos(static_cast<size_t>(new_len)), sin_by_pos(static_cast<size_t>(new_len));
            for (int64_t i = 0; i < new_len; ++i) {
                ops::rope_cos_sin(i, cfg.head_dim, static_cast<double>(cfg.rope_theta),
                                  cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
            }
            ContiguousAttentionKVStore f32_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
            ContiguousAttentionKVStore q8_cache(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);

            std::vector<float> x_f32_before_11, x_q8_before_11, x_f32_before_28, x_q8_before_28;

            // --- Reproduce Part A's whole-layer trace (same as the existing
            // tool) to (a) capture the cumulative states at the start of
            // layers 11/28 for both models, using their OWN natural forward
            // path, and (b) get the final-position F32 logit margin. ---
            for (int64_t li = 0; li < cfg.n_layers; ++li) {
                if (li == 11) { x_f32_before_11 = x_f32; x_q8_before_11 = x_q8; }
                if (li == 28) { x_f32_before_28 = x_f32; x_q8_before_28 = x_q8; }
                x_f32 = execute_cached_transformer_layer(x_f32, new_len, 0, f32_model.layers[static_cast<size_t>(li)],
                                                         cfg, li, cos_by_pos, sin_by_pos, f32_cache);
                x_q8 = execute_cached_transformer_layer(x_q8, new_len, 0, q8_model.layers[static_cast<size_t>(li)],
                                                        cfg, li, cos_by_pos, sin_by_pos, q8_cache);
            }
            const std::vector<float> final_f32 = ops::rmsnorm(x_f32, new_len, hidden, f32_model.final_norm_weight.raw(), cfg.rmsnorm_epsilon);
            const std::vector<float> logits_f32 = ops::linear_no_bias(final_f32, new_len, hidden, f32_model.effective_lm_head().raw(), cfg.vocab);
            const std::vector<float> last_f32(logits_f32.end() - cfg.vocab, logits_f32.end());
            std::vector<float> sorted_logits = last_f32;
            std::sort(sorted_logits.begin(), sorted_logits.end(), std::greater<float>());
            const float top1_top2_margin = sorted_logits[0] - sorted_logits[1];
            std::printf("F32 top-1/top-2 final-logit margin: %.6f (selected token=%lld)\n",
                        top1_top2_margin, (long long)ops::argmax(last_f32));

            for (int64_t layer : kImplicatedLayers) {
                const std::vector<float>& x_before_f32 = (layer == 11) ? x_f32_before_11 : x_f32_before_28;
                const std::vector<float>& x_before_q8 = (layer == 11) ? x_q8_before_11 : x_q8_before_28;
                const LayerWeights& f32_lw = f32_model.layers[static_cast<size_t>(layer)];
                const LayerWeights& q8_lw = q8_model.layers[static_cast<size_t>(layer)];

                std::printf("\n--- layer %lld, prompt %s: projection-level four-way decomposition ---\n",
                            (long long)layer, pc.id.c_str());

                // --- Each side's own NATURAL sequencing through this layer,
                // replicating execute_cached_transformer_layer_impl's order
                // via public ops:: calls only (disclosed unavailable seam:
                // no public function exposes these intermediates). ---
                auto natural_inputs = [&](const std::vector<float>& x_before, const LayerWeights& lw,
                                          ContiguousAttentionKVStore& cache) {
                    struct Inputs {
                        std::vector<float> qkv_input, context_flat, ffn_input, gate_activated;
                    } out;
                    out.qkv_input = ops::rmsnorm(x_before, new_len, hidden, lw.attn_norm_weight.raw(), cfg.rmsnorm_epsilon);
                    const std::vector<float> q_proj = ops::linear_no_bias(out.qkv_input, new_len, hidden, lw.w_q.raw(), q_dim);
                    const std::vector<float> k_proj = ops::linear_no_bias(out.qkv_input, new_len, hidden, lw.w_k.raw(), kv_dim);
                    const std::vector<float> v_proj = ops::linear_no_bias(out.qkv_input, new_len, hidden, lw.w_v.raw(), kv_dim);

                    std::vector<float> q_rope(static_cast<size_t>(cfg.n_q_heads * new_len * cfg.head_dim));
                    for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
                        for (int64_t i = 0; i < new_len; ++i) {
                            std::vector<float> vec(static_cast<size_t>(cfg.head_dim));
                            for (int64_t d = 0; d < cfg.head_dim; ++d)
                                vec[static_cast<size_t>(d)] = q_proj[static_cast<size_t>(i * q_dim + h * cfg.head_dim + d)];
                            std::vector<float> rotated = ops::apply_rope(vec, cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
                            for (int64_t d = 0; d < cfg.head_dim; ++d)
                                q_rope[static_cast<size_t>((h * new_len + i) * cfg.head_dim + d)] = rotated[static_cast<size_t>(d)];
                        }
                    }
                    for (int64_t h = 0; h < cfg.n_kv_heads; ++h) {
                        for (int64_t i = 0; i < new_len; ++i) {
                            std::vector<float> k_vec(static_cast<size_t>(cfg.head_dim));
                            for (int64_t d = 0; d < cfg.head_dim; ++d)
                                k_vec[static_cast<size_t>(d)] = k_proj[static_cast<size_t>(i * kv_dim + h * cfg.head_dim + d)];
                            std::vector<float> k_rotated = ops::apply_rope(k_vec, cos_by_pos[static_cast<size_t>(i)], sin_by_pos[static_cast<size_t>(i)]);
                            cache.write_k(layer, h, i, k_rotated.data());
                            std::vector<float> v_vec(static_cast<size_t>(cfg.head_dim));
                            for (int64_t d = 0; d < cfg.head_dim; ++d)
                                v_vec[static_cast<size_t>(d)] = v_proj[static_cast<size_t>(i * kv_dim + h * cfg.head_dim + d)];
                            cache.write_v(layer, h, i, v_vec.data());
                        }
                    }
                    std::vector<float> context_heads(static_cast<size_t>(cfg.n_q_heads * new_len * cfg.head_dim));
                    for (int64_t h = 0; h < cfg.n_q_heads; ++h) {
                        const int64_t kv_h = h / group_size;
                        for (int64_t qi = 0; qi < new_len; ++qi) {
                            const int64_t key_count = qi + 1;
                            std::vector<float> scores(static_cast<size_t>(key_count));
                            for (int64_t ki = 0; ki < key_count; ++ki) {
                                const float* k_row = cache.k_row(layer, kv_h, ki);
                                ops::AccumT acc = ops::AccumT(0);
                                for (int64_t d = 0; d < cfg.head_dim; ++d)
                                    acc += static_cast<ops::AccumT>(q_rope[static_cast<size_t>((h * new_len + qi) * cfg.head_dim + d)]) *
                                          static_cast<ops::AccumT>(k_row[d]);
                                scores[static_cast<size_t>(ki)] = static_cast<float>(acc) * attn_scale;
                            }
                            std::vector<float> probs = ops::softmax_last_axis(scores, 1, key_count);
                            for (int64_t d = 0; d < cfg.head_dim; ++d) {
                                ops::AccumT acc = ops::AccumT(0);
                                for (int64_t ki = 0; ki < key_count; ++ki) {
                                    const float* v_row = cache.v_row(layer, kv_h, ki);
                                    acc += static_cast<ops::AccumT>(probs[static_cast<size_t>(ki)]) * static_cast<ops::AccumT>(v_row[d]);
                                }
                                context_heads[static_cast<size_t>((h * new_len + qi) * cfg.head_dim + d)] = static_cast<float>(acc);
                            }
                        }
                    }
                    out.context_flat.resize(static_cast<size_t>(new_len * q_dim));
                    for (int64_t i = 0; i < new_len; ++i)
                        for (int64_t h = 0; h < cfg.n_q_heads; ++h)
                            for (int64_t d = 0; d < cfg.head_dim; ++d)
                                out.context_flat[static_cast<size_t>(i * q_dim + h * cfg.head_dim + d)] =
                                    context_heads[static_cast<size_t>((h * new_len + i) * cfg.head_dim + d)];

                    const std::vector<float> attn_out = ops::linear_no_bias(out.context_flat, new_len, q_dim, lw.w_o.raw(), hidden);
                    std::vector<float> r(x_before.size());
                    for (size_t i = 0; i < r.size(); ++i) r[i] = x_before[i] + attn_out[i];
                    out.ffn_input = ops::rmsnorm(r, new_len, hidden, lw.ffn_norm_weight.raw(), cfg.rmsnorm_epsilon);
                    const std::vector<float> gate_proj = ops::linear_no_bias(out.ffn_input, new_len, hidden, lw.w_gate.raw(), cfg.intermediate);
                    const std::vector<float> up_proj = ops::linear_no_bias(out.ffn_input, new_len, hidden, lw.w_up.raw(), cfg.intermediate);
                    out.gate_activated = ops::silu(gate_proj);
                    for (size_t i = 0; i < out.gate_activated.size(); ++i) out.gate_activated[i] *= up_proj[i];
                    return out;
                };

                // Separate scratch caches (this layer only) so the natural
                // per-side traversal above does not corrupt Part A's own
                // whole-model caches (already fully consumed by this point).
                ContiguousAttentionKVStore scratch_f32(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                ContiguousAttentionKVStore scratch_q8(cfg.n_layers, cfg.n_kv_heads, cfg.max_positions, cfg.head_dim);
                const auto in_f32 = natural_inputs(x_before_f32, f32_lw, scratch_f32);
                const auto in_q8 = natural_inputs(x_before_q8, q8_lw, scratch_q8);

                struct Projection {
                    const char* name;
                    const std::vector<float>* input_f32;
                    const std::vector<float>* input_q8;
                    const ResidentView* w_f32;
                    const ResidentView* w_q8;
                    int64_t in_features;
                    int64_t out_features;
                    std::string tensor_name;  // for Gate 2 correlation
                };
                const std::vector<Projection> projections = {
                    {"Q", &in_f32.qkv_input, &in_q8.qkv_input, &f32_lw.w_q, &q8_lw.w_q, hidden, q_dim,
                     "blk." + std::to_string(layer) + ".attn_q.weight"},
                    {"K", &in_f32.qkv_input, &in_q8.qkv_input, &f32_lw.w_k, &q8_lw.w_k, hidden, kv_dim,
                     "blk." + std::to_string(layer) + ".attn_k.weight"},
                    {"V", &in_f32.qkv_input, &in_q8.qkv_input, &f32_lw.w_v, &q8_lw.w_v, hidden, kv_dim,
                     "blk." + std::to_string(layer) + ".attn_v.weight"},
                    {"O", &in_f32.context_flat, &in_q8.context_flat, &f32_lw.w_o, &q8_lw.w_o, q_dim, hidden,
                     "blk." + std::to_string(layer) + ".attn_output.weight"},
                    {"FFN_gate", &in_f32.ffn_input, &in_q8.ffn_input, &f32_lw.w_gate, &q8_lw.w_gate, hidden, cfg.intermediate,
                     "blk." + std::to_string(layer) + ".ffn_gate.weight"},
                    {"FFN_up", &in_f32.ffn_input, &in_q8.ffn_input, &f32_lw.w_up, &q8_lw.w_up, hidden, cfg.intermediate,
                     "blk." + std::to_string(layer) + ".ffn_up.weight"},
                    {"FFN_down", &in_f32.gate_activated, &in_q8.gate_activated, &f32_lw.w_down, &q8_lw.w_down, cfg.intermediate, hidden,
                     "blk." + std::to_string(layer) + ".ffn_down.weight"},
                };

                for (const Projection& proj : projections) {
                    const std::vector<float> y_ff = ops::linear_no_bias(*proj.input_f32, new_len, proj.in_features, proj.w_f32->raw(), proj.out_features);
                    const std::vector<float> y_qf = ops::linear_no_bias(*proj.input_f32, new_len, proj.in_features, proj.w_q8->raw(), proj.out_features);
                    const std::vector<float> y_fq = ops::linear_no_bias(*proj.input_q8, new_len, proj.in_features, proj.w_f32->raw(), proj.out_features);
                    const std::vector<float> y_qq = ops::linear_no_bias(*proj.input_q8, new_len, proj.in_features, proj.w_q8->raw(), proj.out_features);

                    const LocatedStats weight_effect = effect_vector_stats(y_qf, y_ff, new_len, proj.out_features);
                    const LocatedStats state_effect = effect_vector_stats(y_fq, y_ff, new_len, proj.out_features);
                    const LocatedStats combined_effect = effect_vector_stats(y_qq, y_ff, new_len, proj.out_features);
                    const LocatedStats interaction = factorial_interaction_stats(y_ff, y_qf, y_fq, y_qq, new_len, proj.out_features);
                    const double l2_pos0 = row_l2_norm_of_diff(y_qf, y_ff, 0, proj.out_features);
                    const double l2_final = row_l2_norm_of_diff(y_qf, y_ff, new_len - 1, proj.out_features);

                    std::printf("  [%-9s] weight(Δy=y_qf-y_ff): max_abs=%.6f rmse=%.6f L2(pos0)=%.6f L2(final)=%.6f "
                                "at [pos=%lld,ch=%lld]\n",
                                proj.name, weight_effect.stats.max_abs, weight_effect.stats.rmse, l2_pos0, l2_final,
                                (long long)weight_effect.max_position, (long long)weight_effect.max_channel);
                    std::printf("  [%-9s] state:                 max_abs=%.6f rmse=%.6f at [pos=%lld,ch=%lld]\n",
                                proj.name, state_effect.stats.max_abs, state_effect.stats.rmse,
                                (long long)state_effect.max_position, (long long)state_effect.max_channel);
                    std::printf("  [%-9s] combined:              max_abs=%.6f rmse=%.6f at [pos=%lld,ch=%lld]\n",
                                proj.name, combined_effect.stats.max_abs, combined_effect.stats.rmse,
                                (long long)combined_effect.max_position, (long long)combined_effect.max_channel);
                    std::printf("  [%-9s] interaction:           max_abs=%.6f rmse=%.6f at [pos=%lld,ch=%lld]\n",
                                proj.name, interaction.stats.max_abs, interaction.stats.rmse,
                                (long long)interaction.max_position, (long long)interaction.max_channel);
                }

                // --- Gate 2: raw Q8_0 block inspection for the same 7
                // projections at this layer, only once per layer (not per
                // prompt -- weights do not depend on the prompt). Guard
                // against repeating this for every prompt iteration. ---
                if (pc.id == kPrompts.front().id) {
                    std::printf("\n  -- Gate 2: raw Q8_0 block inspection, layer %lld --\n", (long long)layer);
                    for (const Projection& proj : projections) {
                        const GgufTensorInfo& q8_tensor = find_tensor(q8_artifact, proj.tensor_name);
                        if (q8_tensor.encoding != GgufTensorEncoding::Q8_0) {
                            std::printf("  [%-9s] tensor '%s' is not Q8_0-encoded (encoding=%s) -- skipped\n",
                                        proj.name, proj.tensor_name.c_str(), gguf_encoding_name(q8_tensor.encoding).c_str());
                            continue;
                        }
                        const GgufTensorInfo& f32_tensor = find_tensor(f32_artifact, proj.tensor_name);
                        const std::vector<float> f32_flat = read_f32_tensor_flat(f32_path, f32_tensor);
                        const BlockScaleStats bs = inspect_q8_0_tensor(q8_path, q8_tensor, f32_flat);
                        std::printf("  [%-9s] blocks=%lld scale[min=%.6g p10=%.6g median=%.6g mean=%.6g "
                                    "p90=%.6g max=%.6g] zero_scales=%lld nonfinite_scales=%lld "
                                    "recon[max_abs=%.6g rmse=%.6g] worst_blocks=[",
                                    proj.name, (long long)bs.block_count, bs.scale_min, bs.scale_p10, bs.scale_median,
                                    bs.scale_mean, bs.scale_p90, bs.scale_max, (long long)bs.zero_scale_count,
                                    (long long)bs.nonfinite_scale_count, bs.recon_max_abs, bs.recon_rmse);
                        for (size_t i = 0; i < bs.worst_block_indices.size(); ++i) {
                            std::printf("%lld%s", (long long)bs.worst_block_indices[i],
                                        i + 1 < bs.worst_block_indices.size() ? "," : "");
                        }
                        std::printf("]\n");
                    }
                }
            }
        }
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
