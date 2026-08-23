// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Checkpoint 1: Q8_0 layout validation and scalar
// reference dequantization. Exercises orcengine::dequantize_q8_0_scalar_
// reference() directly (known-value blocks, int8 extremes, scale edge
// cases, hostile/malformed inputs) and the materialization dispatch
// (orcengine::gguf_encoding_materializable()/materialize_gguf_tensor())
// to prove the existing F32/F16 paths are unchanged and unsupported
// encodings still reject. See docs/OrcEngine/PHASE6_QUANTIZATION_SPEC.md.
#include <cmath>
#include <cstdio>
#include <cstdint>
#include <filesystem>
#include <fstream>
#include <functional>
#include <limits>
#include <string>
#include <system_error>
#include <vector>

#include "orcengine/gguf.hpp"
#include "orcengine/logical_tensor.hpp"
#include "orcengine/tensor.hpp"

using namespace orcengine;

namespace {

int g_failures = 0;

void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

void expect_gguf_error_containing(const std::string& name, const std::function<void()>& action,
                                  const std::string& expected_substring) {
    try {
        action();
        check(false, name + " (unexpectedly accepted)");
    } catch (const GgufError& ex) {
        const std::string what = ex.what();
        if (what.find(expected_substring) != std::string::npos) {
            check(true, name);
        } else {
            check(false, name + " (message did not contain '" + expected_substring + "': " + what + ")");
        }
    } catch (const std::exception& ex) {
        check(false, name + " (wrong exception type, not GgufError: " + std::string(ex.what()) + ")");
    }
}

// Well-known exact IEEE-754 half-precision bit patterns, used to build
// known-value Q8_0 blocks without needing a float->half encoder in this
// test (avoiding introducing a second, untested encode path just to
// build fixtures for a decode test).
constexpr uint16_t kHalfZero = 0x0000;
constexpr uint16_t kHalfOne = 0x3C00;
constexpr uint16_t kHalfTwo = 0x4000;
constexpr uint16_t kHalfNegOne = 0xBC00;
constexpr uint16_t kHalfSixteenth = 0x2C00;   // 0.0625 = 2^-4, exact in half
constexpr uint16_t kHalfSmallestSubnormal = 0x0001;  // 2^-24 ~= 5.9604645e-8

std::vector<uint8_t> make_block(uint16_t scale_bits, const std::vector<int8_t>& q) {
    if (q.size() != 32) throw std::logic_error("test block must have exactly 32 quantized values");
    std::vector<uint8_t> block(34);
    block[0] = static_cast<uint8_t>(scale_bits & 0xFF);
    block[1] = static_cast<uint8_t>((scale_bits >> 8) & 0xFF);
    for (size_t i = 0; i < 32; ++i) block[2 + i] = static_cast<uint8_t>(q[i]);
    return block;
}

std::vector<uint8_t> concat(const std::vector<std::vector<uint8_t>>& blocks) {
    std::vector<uint8_t> out;
    for (const auto& b : blocks) out.insert(out.end(), b.begin(), b.end());
    return out;
}

}  // namespace

int main() {
    try {
        std::printf("=== Checkpoint 1: Q8_0 layout validation and scalar dequantization ===\n");

        // --- 1. Known-value single block: scale=2.0, qi = i-16 for i in [0,32). ---
        {
            std::vector<int8_t> q(32);
            for (int i = 0; i < 32; ++i) q[static_cast<size_t>(i)] = static_cast<int8_t>(i - 16);
            const auto bytes = make_block(kHalfTwo, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            bool ok = out.size() == 32;
            for (int i = 0; ok && i < 32; ++i) {
                const float expected = 2.0f * static_cast<float>(i - 16);
                ok = out[static_cast<size_t>(i)] == expected;
            }
            check(ok, "known-value block: scale=2.0, qi=i-16 dequantizes exactly");
        }

        // --- 2. int8 extremes: qi=127 and qi=-128, scale=1.0. ---
        {
            std::vector<int8_t> q(32, 0);
            q[0] = 127;
            q[1] = -128;
            const auto bytes = make_block(kHalfOne, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            check(out[0] == 127.0f, "int8 extreme +127 dequantizes exactly at scale=1.0");
            check(out[1] == -128.0f, "int8 extreme -128 dequantizes exactly at scale=1.0");
        }

        // --- 3. Zero scale: every output must be exactly 0.0, never NaN. ---
        {
            std::vector<int8_t> q(32);
            for (int i = 0; i < 32; ++i) q[static_cast<size_t>(i)] = static_cast<int8_t>(i - 16);
            const auto bytes = make_block(kHalfZero, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            bool all_zero_finite = true;
            for (float v : out) {
                if (!(v == 0.0f) || !std::isfinite(v)) all_zero_finite = false;
            }
            check(all_zero_finite, "zero scale: every dequantized value is exactly 0.0, finite (no NaN)");
        }

        // --- 4. Smallest nonzero F16 scale (subnormal min ~5.96e-8). ---
        {
            std::vector<int8_t> q(32, 0);
            q[0] = 1;
            q[1] = -1;
            const auto bytes = make_block(kHalfSmallestSubnormal, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            constexpr float kExpected = 5.9604645e-8f;  // 2^-24
            const bool finite = std::isfinite(out[0]) && std::isfinite(out[1]);
            const bool close = finite && std::fabs(out[0] - kExpected) < 1e-13f &&
                               std::fabs(out[1] + kExpected) < 1e-13f;
            check(close, "smallest nonzero F16 scale (2^-24) dequantizes correctly and finitely");
        }

        // --- 4b. Signed-byte interpretation, all boundary stored-byte patterns.
        // Codex review finding: the previous implementation used
        // static_cast<int8_t>(uint8_t) for values above 127, which is
        // implementation-defined conversion behavior before relying on any
        // particular bit-preserving guarantee. dequantize_q8_0_scalar_reference()
        // now uses std::bit_cast<int8_t> instead. Proves each of the five
        // boundary stored-byte patterns decodes to its correct signed int8
        // value (and the correct dequantized float at scale=1.0), directly
        // exercising the bit_cast path rather than the arithmetic-conversion
        // path it replaced. ---
        {
            struct Case { uint8_t stored_byte; int8_t expected_signed; };
            const Case cases[] = {
                {0x00, 0},     // zero
                {0x01, 1},     // smallest positive
                {0x7f, 127},   // largest positive (int8 max)
                {0x80, -128},  // smallest negative (int8 min) -- the exact byte
                               // pattern where static_cast<int8_t> of a uint8_t
                               // > 127 was implementation-defined before this fix
                {0xff, -1},    // largest stored byte -> -1
            };
            bool ok = true;
            for (const Case& c : cases) {
                std::vector<int8_t> q(32, 0);
                q[0] = c.expected_signed;  // construct the block from the intended signed value...
                auto bytes = make_block(kHalfOne, q);
                bytes[2] = c.stored_byte;  // ...then assert the RAW STORED BYTE decodes identically,
                                           // proving stored-byte -> signed-value is exactly bit_cast,
                                           // not a re-derivation from the signed value we started with.
                const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
                const float expected = static_cast<float>(c.expected_signed);
                if (out[0] != expected) ok = false;
            }
            check(ok, "signed-byte boundary bytes (0x00,0x01,0x7f,0x80,0xff) decode via bit_cast to correct signed int8 and dequantized value");
        }

        // --- 5. Ordinary positive scale (0.0625, exact in half) and a negative-sign-bit
        // scale (-1.0) proving the sign bit is honored end to end. The standard
        // llama.cpp/GGML Q8_0 quantization procedure (scale = amax/127) always
        // produces a non-negative stored scale for that ONE reference quantizer,
        // but nothing in the Q8_0 FORMAT itself forbids a negative stored scale --
        // this is a decode-path plumbing proof of the sign bit, not a claim that
        // no conformant Q8_0 producer can ever emit one. ---
        {
            std::vector<int8_t> q(32, 0);
            q[0] = 50;
            const auto bytes = make_block(kHalfSixteenth, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            check(out[0] == 3.125f, "ordinary positive scale (0.0625) dequantizes exactly");
        }
        {
            std::vector<int8_t> q(32, 0);
            q[0] = 5;
            const auto bytes = make_block(kHalfNegOne, q);
            const auto out = dequantize_q8_0_scalar_reference(bytes, 32);
            check(out[0] == -5.0f, "negative-sign-bit scale (-1.0) is honored (decode-path sign proof)");
        }

        // --- 6. Malformed/truncated block: one byte short of a full block. ---
        {
            std::vector<int8_t> q(32, 1);
            auto bytes = make_block(kHalfOne, q);
            bytes.pop_back();  // 33 bytes instead of 34
            expect_gguf_error_containing("truncated block (33 bytes instead of 34) rejected",
                                         [&] { (void)dequantize_q8_0_scalar_reference(bytes, 32); },
                                         "expected exactly");
        }

        // --- 7. Extent mismatch: one byte too many. ---
        {
            std::vector<int8_t> q(32, 1);
            auto bytes = make_block(kHalfOne, q);
            bytes.push_back(0);  // 35 bytes instead of 34
            expect_gguf_error_containing("oversized block (35 bytes instead of 34) rejected",
                                         [&] { (void)dequantize_q8_0_scalar_reference(bytes, 32); },
                                         "expected exactly");
        }

        // --- 8. Zero element_count. ---
        {
            std::vector<uint8_t> empty_bytes;
            expect_gguf_error_containing("zero element_count rejected", [&] {
                (void)dequantize_q8_0_scalar_reference(empty_bytes, 0);
            }, "positive");
        }

        // --- 9. Negative element_count. ---
        {
            std::vector<uint8_t> empty_bytes;
            expect_gguf_error_containing("negative element_count rejected", [&] {
                (void)dequantize_q8_0_scalar_reference(empty_bytes, -32);
            }, "positive");
        }

        // --- 10. Non-multiple-of-32 element_count (partial block). ---
        {
            std::vector<int8_t> q(32, 1);
            const auto bytes = make_block(kHalfOne, q);
            expect_gguf_error_containing("element_count=33 (not a multiple of 32) rejected", [&] {
                (void)dequantize_q8_0_scalar_reference(bytes, 33);
            }, "multiple of");
        }

        // --- 11. Hostile huge element_count: int64_t's own range means
        // block_count*34 in uint64_t arithmetic cannot actually overflow for
        // ANY valid positive int64_t element_count (INT64_MAX/32*34 <
        // UINT64_MAX -- uint64_t's range is roughly double int64_t's), so the
        // checked_mul() overflow guard is defensive-in-depth, not reachable
        // via this specific signature. What IS directly testable: a huge
        // element_count must still fail closed (via the byte-length mismatch
        // against a small `bytes` buffer) rather than attempt a
        // multi-exabyte allocation. Proven here, not assumed.
        {
            std::vector<uint8_t> tiny_bytes(34, 0);
            const int64_t huge = (std::numeric_limits<int64_t>::max() / kQ8_0BlockElements) * kQ8_0BlockElements;
            expect_gguf_error_containing("huge element_count fails closed (byte-length mismatch, no giant allocation attempted)",
                                         [&] { (void)dequantize_q8_0_scalar_reference(tiny_bytes, huge); },
                                         "expected exactly");
        }

        // --- 12. Multiple blocks: 2 blocks, values continuous across both. ---
        {
            std::vector<int8_t> q0(32, 0), q1(32, 0);
            for (int i = 0; i < 32; ++i) q0[static_cast<size_t>(i)] = static_cast<int8_t>(i);
            for (int i = 0; i < 32; ++i) q1[static_cast<size_t>(i)] = static_cast<int8_t>(-(i + 1));
            const auto bytes = concat({make_block(kHalfOne, q0), make_block(kHalfTwo, q1)});
            const auto out = dequantize_q8_0_scalar_reference(bytes, 64);
            bool ok = out.size() == 64;
            for (int i = 0; ok && i < 32; ++i) ok = out[static_cast<size_t>(i)] == static_cast<float>(i);
            for (int i = 0; ok && i < 32; ++i) {
                ok = out[32 + static_cast<size_t>(i)] == 2.0f * static_cast<float>(-(i + 1));
            }
            check(ok, "multiple blocks (2): each block's own scale applied correctly, correct offsets");
        }

        // --- 13. Dispatch: existing F32/F16 path unchanged, Q8_0 now accepted,
        // other quantized encodings still rejected. ---
        check(gguf_encoding_materializable(GgufTensorEncoding::F32), "dispatch: F32 still materializable");
        check(gguf_encoding_materializable(GgufTensorEncoding::F16), "dispatch: F16 still materializable");
        check(gguf_encoding_materializable(GgufTensorEncoding::Q8_0), "dispatch: Q8_0 now materializable");
        check(!gguf_encoding_materializable(GgufTensorEncoding::Q4_0), "dispatch: Q4_0 still NOT materializable");
        check(!gguf_encoding_materializable(GgufTensorEncoding::Q6_K), "dispatch: Q6_K still NOT materializable");

        // --- 14. materialize_gguf_tensor: unsupported encoding still rejects
        // (before even opening the backing file). ---
        {
            MappedGgufTensor bad;
            bad.semantic = {SemanticTensorRole::TokenEmbedding, -1};
            bad.source_name = "fake.weight";
            bad.logical = LogicalTensor("fake.weight", TensorShape({4, 8}));
            bad.backing = BackingExtent("this-file-does-not-exist.gguf", 0, 32 * 34, BackingEncoding::GgufQ4_0);
            bad.encoding = GgufTensorEncoding::Q4_0;
            expect_gguf_error_containing("materialize_gguf_tensor rejects unsupported encoding (Q4_0) before opening any file",
                                         [&] { (void)materialize_gguf_tensor(bad); },
                                         "unsupported GGUF tensor encoding");
        }

        // --- 15. materialize_gguf_tensor: real Q8_0 tensor through a temp file,
        // proving end-to-end dispatch (not just the standalone dequant function),
        // plus a local accounting-model arithmetic check: Q8_0 backing bytes vs.
        // F32 resident bytes are DIFFERENT numbers, both reported here explicitly,
        // never conflated. This is NOT a orcengine::ResidencyLedger integration
        // proof -- no ResidencyLedger instance participates in this test; see
        // Phase 6 Stage 5 accounting work for whether/how the actual runtime
        // ledger records Q8_0 backing/resident byte counts. ---
        {
            std::vector<int8_t> q(32, 0);
            for (int i = 0; i < 32; ++i) q[static_cast<size_t>(i)] = static_cast<int8_t>(i - 16);
            const auto block = make_block(kHalfOne, q);  // 34 bytes, scale=1.0

            const std::filesystem::path tmp = std::filesystem::temp_directory_path() /
                                              "orcengine_phase6_q8_0_checkpoint1_fixture.bin";
            {
                std::ofstream f(tmp, std::ios::binary);
                f.write(reinterpret_cast<const char*>(block.data()), static_cast<std::streamsize>(block.size()));
            }

            MappedGgufTensor good;
            good.semantic = {SemanticTensorRole::TokenEmbedding, -1};
            good.source_name = "test.q8_0.weight";
            good.logical = LogicalTensor("test.q8_0.weight", TensorShape({1, 32}));
            good.backing = BackingExtent(tmp.string(), 0, static_cast<int64_t>(block.size()), BackingEncoding::GgufQ8_0);
            good.encoding = GgufTensorEncoding::Q8_0;

            const ResidentView view = materialize_gguf_tensor(good);
            bool ok = view.shape().element_count() == 32;
            for (int i = 0; ok && i < 32; ++i) {
                ok = view.raw()[static_cast<size_t>(i)] == static_cast<float>(i - 16);
            }
            check(ok, "materialize_gguf_tensor: real Q8_0 tensor (via temp file) dequantizes correctly end to end");

            const uint64_t q8_0_backing_bytes = block.size();
            const uint64_t f32_resident_bytes = static_cast<uint64_t>(view.shape().element_count()) * sizeof(float);
            std::printf("  accounting-model check: Q8_0 backing bytes = %llu, F32 resident bytes = %llu (distinct, not conflated)\n",
                        static_cast<unsigned long long>(q8_0_backing_bytes),
                        static_cast<unsigned long long>(f32_resident_bytes));
            check(q8_0_backing_bytes == 34 && f32_resident_bytes == 128 && q8_0_backing_bytes != f32_resident_bytes,
                  "accounting-model check: Q8_0 backing bytes (34) and F32 resident bytes (128) computed separately and correctly (not a ResidencyLedger proof)");

            { std::error_code remove_ec; std::filesystem::remove(tmp, remove_ec); }
        }

        // --- 16. materialize_gguf_tensor: byte-extent mismatch for a Q8_0
        // tensor is rejected before any read past the declared backing. ---
        {
            const std::filesystem::path tmp = std::filesystem::temp_directory_path() /
                                              "orcengine_phase6_q8_0_checkpoint1_bad_extent.bin";
            std::vector<int8_t> q(32, 1);
            const auto block = make_block(kHalfOne, q);
            {
                std::ofstream f(tmp, std::ios::binary);
                f.write(reinterpret_cast<const char*>(block.data()), static_cast<std::streamsize>(block.size()));
            }
            MappedGgufTensor bad;
            bad.semantic = {SemanticTensorRole::TokenEmbedding, -1};
            bad.source_name = "bad_extent.q8_0.weight";
            bad.logical = LogicalTensor("bad_extent.q8_0.weight", TensorShape({1, 32}));
            // Declares byte_length=33 (wrong) for a 32-element Q8_0 tensor, which
            // actually requires exactly 34 bytes -- must be rejected before the
            // file is even opened by the required_backing_bytes_for_encoding()
            // check, not discovered later as a short/garbage read.
            bad.backing = BackingExtent(tmp.string(), 0, 33, BackingEncoding::GgufQ8_0);
            bad.encoding = GgufTensorEncoding::Q8_0;
            expect_gguf_error_containing("materialize_gguf_tensor rejects a Q8_0 byte-extent mismatch (33 declared vs 34 required)",
                                         [&] { (void)materialize_gguf_tensor(bad); },
                                         "backing length does not match");
            { std::error_code remove_ec; std::filesystem::remove(tmp, remove_ec); }
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL CHECKPOINT 1 CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
