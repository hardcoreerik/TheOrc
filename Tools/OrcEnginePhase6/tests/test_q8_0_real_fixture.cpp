// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 6 Stage 1, Checkpoint 2: real Q8_0 fixture and mixed-format
// loading. Exercises the canonical, provenance-recorded
// smollm2-135m-q8_0.gguf (see fixtures/Q8_0_FIXTURE_PROVENANCE.md) --
// confirms it opens/indexes, that its tensor-by-tensor encoding
// inventory matches the independently recorded manifest exactly, that
// representative Q8_0 tensors dequantize to plausible values (cross-
// checked against the F32 ground truth for the SAME tensor), that
// retained F32 tensors are untouched, and that a truncated/extent-
// short GGUF fails closed rather than reading out-of-bounds/garbage
// bytes (NOT a general claim that arbitrary in-range bit corruption of
// Q8_0 payload bytes is detectable -- Q8_0 has no checksum, and
// in-range corruption is not tested here). See
// docs/OrcEngine/PHASE6_QUANTIZATION_SPEC.md.
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <limits>
#include <string>
#include <system_error>
#include <vector>

#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

int g_failures = 0;

void check(bool cond, const std::string& name) {
    std::printf("[%s] %s\n", cond ? "PASS" : "FAIL", name.c_str());
    if (!cond) ++g_failures;
}

float max_abs_diff(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) return std::numeric_limits<float>::infinity();
    float m = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) m = std::max(m, std::fabs(a[i] - b[i]));
    return m;
}

// Reads the exact raw backing bytes for a named tensor directly off disk,
// independent of dequantization/decoding. Used to prove genuine byte-for-byte
// GGUF backing identity for retained-F32 tensors -- decoded-float-vector
// equality (operator== on std::vector<float>) is NOT sufficient proof of that
// on its own: distinct bit patterns can decode to values IEEE754 reports as
// equal (e.g. +0.0f and -0.0f), so it proves materialized-VALUE identity, not
// backing-BYTE identity. This helper closes that gap by comparing the actual
// source bytes.
std::vector<uint8_t> read_tensor_backing_bytes(const std::filesystem::path& path,
                                               const GgufArtifact& artifact,
                                               const std::string& tensor_name) {
    const GgufTensorInfo* found = nullptr;
    for (const auto& t : artifact.tensors) {
        if (t.name == tensor_name) { found = &t; break; }
    }
    if (found == nullptr) throw std::runtime_error("tensor '" + tensor_name + "' not found for byte-identity check");
    std::ifstream stream(path, std::ios::binary);
    if (!stream) throw std::runtime_error("cannot reopen '" + path.string() + "' for byte-identity check");
    stream.seekg(static_cast<std::streamoff>(found->absolute_offset));
    std::vector<uint8_t> bytes(static_cast<size_t>(found->encoded_length));
    stream.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!stream) throw std::runtime_error("short read for tensor '" + tensor_name + "' byte-identity check");
    return bytes;
}

}  // namespace

int main(int argc, char** argv) {
    if (argc != 3) {
        std::fprintf(stderr, "usage: test_q8_0_real_fixture F32.gguf Q8_0.gguf\n");
        return 2;
    }
    try {
        const std::filesystem::path f32_path = argv[1];
        const std::filesystem::path q8_path = argv[2];

        std::printf("=== Checkpoint 2: real Q8_0 fixture and mixed-format loading ===\n");

        // --- 1. GGUF opens and indexes successfully. ---
        GgufArtifact q8_artifact = index_gguf(q8_path);
        check(!q8_artifact.tensors.empty(), "Q8_0 fixture opens and indexes successfully");

        // --- 2. Mixed-format inventory matches the independently recorded
        // manifest (Q8_0_FIXTURE_PROVENANCE.md): 273 total, 212 Q8_0, 61 F32. ---
        int64_t q8_0_count = 0, f32_count = 0, other_count = 0;
        for (const auto& t : q8_artifact.tensors) {
            if (t.encoding == GgufTensorEncoding::Q8_0) ++q8_0_count;
            else if (t.encoding == GgufTensorEncoding::F32) ++f32_count;
            else ++other_count;
        }
        std::printf("  tensor inventory: total=%zu q8_0=%lld f32=%lld other=%lld\n",
                    q8_artifact.tensors.size(), (long long)q8_0_count, (long long)f32_count, (long long)other_count);
        check(q8_artifact.tensors.size() == 273, "total tensor count matches recorded manifest (273)");
        check(q8_0_count == 212, "Q8_0 tensor count matches recorded manifest (212)");
        check(f32_count == 61, "F32 tensor count matches recorded manifest (61)");
        check(other_count == 0, "no unexpected encodings present");
        check(q8_0_count + f32_count + other_count == static_cast<int64_t>(q8_artifact.tensors.size()),
              "accounting reconciles exactly: q8_0 + f32 + other == total");

        // --- 3. No assumption that norm layers are Q8_0: confirm every
        // *_norm.weight tensor is specifically F32 (not just "some F32 exists"). ---
        bool all_norms_f32 = true;
        int64_t norm_tensor_count = 0;
        for (const auto& t : q8_artifact.tensors) {
            if (t.name.find("norm.weight") != std::string::npos) {
                ++norm_tensor_count;
                if (t.encoding != GgufTensorEncoding::F32) all_norms_f32 = false;
            }
        }
        check(norm_tensor_count == 61 && all_norms_f32,
              "every *_norm.weight tensor (61 of them) is specifically F32, not assumed");

        // --- 4. Mixed-format model loading: map_llama_model + materialize_gguf_model
        // routes each tensor through its own actual encoding, tied/untied identity
        // read from actual metadata (not assumed). ---
        const ModelArtifactManifest manifest = map_llama_model(q8_artifact);
        check(manifest.architecture == "llama", "architecture metadata reads 'llama'");
        check(manifest.materializable, "manifest reports materializable (Q8_0 now supported)");
        check(!manifest.tied_embeddings, "output-head identity read from actual metadata: untied "
                                         "(a separate output.weight tensor is present)");
        const Model q8_model = materialize_gguf_model(manifest);
        check(q8_model.lm_head.has_value(), "untied model: lm_head materialized as its own tensor");
        std::printf("  materialized OK: vocab=%lld hidden=%lld n_layers=%lld\n",
                    (long long)q8_model.config().vocab, (long long)q8_model.config().hidden,
                    (long long)q8_model.config().n_layers);

        // --- 5. Load the F32 ground truth for cross-checking (representative
        // Q8_0 tensors dequantize to plausible values; retained F32 tensors
        // follow the unchanged F32 path exactly). ---
        const GgufArtifact f32_artifact = index_gguf(f32_path);
        const ModelArtifactManifest f32_manifest = map_llama_model(f32_artifact);
        const Model f32_model = materialize_gguf_model(f32_manifest);
        check(f32_model.config().vocab == q8_model.config().vocab &&
                  f32_model.config().hidden == q8_model.config().hidden &&
                  f32_model.config().n_layers == q8_model.config().n_layers,
              "F32 and Q8_0 models report identical architecture config");

        // --- 6. Representative Q8_0 tensor: token_embd.weight. Q8_0's own
        // per-block quantization error bound is scale/2 (int8 rounding), where
        // scale = amax_in_block/127 -- for typical small-magnitude embedding
        // weights this is a few thousandths in absolute terms. A generous
        // sanity bound (not the rigorous, empirically-derived Checkpoint 3
        // tolerance) proves these are genuinely close, not garbage. ---
        {
            const float diff = max_abs_diff(f32_model.token_embedding.raw(), q8_model.token_embedding.raw());
            std::printf("  token_embd.weight (Q8_0 vs F32) max_abs_diff=%.6f\n", diff);
            check(std::isfinite(diff) && diff > 0.0f && diff < 0.05f,
                  "representative Q8_0 tensor (token_embd.weight) dequantizes to plausible values "
                  "(nonzero but small max_abs_diff vs F32 ground truth)");
        }
        {
            const float diff = max_abs_diff(f32_model.effective_lm_head().raw(), q8_model.effective_lm_head().raw());
            std::printf("  output.weight (Q8_0 vs F32) max_abs_diff=%.6f\n", diff);
            check(std::isfinite(diff) && diff > 0.0f && diff < 0.05f,
                  "representative Q8_0 tensor (output.weight) dequantizes to plausible values");
        }
        {
            const float diff = max_abs_diff(f32_model.layers[0].w_gate.raw(), q8_model.layers[0].w_gate.raw());
            std::printf("  blk.0.ffn_gate.weight (Q8_0 vs F32) max_abs_diff=%.6f\n", diff);
            check(std::isfinite(diff) && diff > 0.0f && diff < 0.05f,
                  "representative Q8_0 tensor (blk.0.ffn_gate.weight) dequantizes to plausible values");
        }

        // --- 7. Retained F32 tensors are genuinely byte-for-byte unchanged
        // (the unchanged F32 path, not merely "close"). Compares the actual
        // SOURCE BACKING BYTES read directly off both GGUF files -- not
        // decoded-float-vector equality, which proves materialized-value
        // identity but not backing-byte identity (e.g. it cannot distinguish
        // +0.0f from -0.0f, which are == but bit-distinct). ---
        {
            const auto f32_bytes = read_tensor_backing_bytes(f32_path, f32_artifact, "blk.0.attn_norm.weight");
            const auto q8_bytes = read_tensor_backing_bytes(q8_path, q8_artifact, "blk.0.attn_norm.weight");
            check(f32_bytes == q8_bytes, "retained F32 tensor (blk.0.attn_norm.weight): source GGUF backing "
                                         "bytes are byte-for-byte identical between the F32 and Q8_0 files "
                                         "(compared as raw bytes, not decoded float values)");
            check(f32_model.layers[0].attn_norm_weight.raw() == q8_model.layers[0].attn_norm_weight.raw(),
                  "retained F32 tensor (blk.0.attn_norm.weight): materialized float values also identical "
                  "(consistent with the byte-identity result above)");
        }
        {
            const auto f32_bytes = read_tensor_backing_bytes(f32_path, f32_artifact, "output_norm.weight");
            const auto q8_bytes = read_tensor_backing_bytes(q8_path, q8_artifact, "output_norm.weight");
            check(f32_bytes == q8_bytes, "retained F32 tensor (output_norm.weight): source GGUF backing bytes "
                                         "are byte-for-byte identical between the F32 and Q8_0 files "
                                         "(compared as raw bytes, not decoded float values)");
            check(f32_model.final_norm_weight.raw() == q8_model.final_norm_weight.raw(),
                  "retained F32 tensor (output_norm.weight): materialized float values also identical "
                  "(consistent with the byte-identity result above)");
        }

        // --- 8. Truncation/backing-extent failure fails closed: truncate the
        // file mid-tensor-data and confirm materialization rejects it rather
        // than reading past EOF or interpreting a short buffer as complete
        // blocks. This proves fail-closed behavior for a SHORTENED backing
        // extent specifically; it does NOT test (and this claim is
        // deliberately narrow about) whether arbitrary in-range bit
        // corruption of otherwise-correctly-sized Q8_0 payload bytes would be
        // detected -- Q8_0 has no per-block checksum, so in-range corruption
        // of a scale or quantized value is, by design of the format itself,
        // silently dequantized to a wrong-but-plausible float. No speculative
        // checksum/integrity system is added here; this is a scope note, not
        // a gap being closed. ---
        {
            const std::filesystem::path corrupt_path = std::filesystem::temp_directory_path() /
                                                        "orcengine_phase6_q8_0_checkpoint2_truncated.gguf";
            {
                std::ifstream src(q8_path, std::ios::binary);
                std::ofstream dst(corrupt_path, std::ios::binary);
                const uintmax_t full_size = std::filesystem::file_size(q8_path);
                const uintmax_t truncated_size = full_size / 2;  // cut off roughly half the tensor data
                std::vector<char> buf(static_cast<size_t>(truncated_size));
                src.read(buf.data(), static_cast<std::streamsize>(buf.size()));
                dst.write(buf.data(), static_cast<std::streamsize>(buf.size()));
            }
            bool rejected = false;
            std::string failure_reason;
            try {
                const GgufArtifact corrupt_artifact = index_gguf(corrupt_path);
                const ModelArtifactManifest corrupt_manifest = map_llama_model(corrupt_artifact);
                const Model corrupt_model = materialize_gguf_model(corrupt_manifest);
                (void)corrupt_model;
            } catch (const std::exception& ex) {
                rejected = true;
                failure_reason = ex.what();
            }
            check(rejected, "truncated (corrupted) Q8_0 GGUF fails closed rather than reading OOB/garbage: " +
                            (rejected ? failure_reason : std::string("(did not throw)")));
            // Best-effort cleanup only: on some runs (observed under ASan,
            // likely a Windows file-handle-release timing difference from
            // the exception path above) the OS may still consider the file
            // briefly open when this remove() runs. That is a test-hygiene
            // detail, not evidence of anything -- the actual check above
            // already completed and recorded its result; a temp file left
            // in %TEMP% is harmless and not part of this test's pass/fail
            // contract. Use the error_code overload so a removal failure
            // cannot itself throw and mask the real result above.
            std::error_code remove_ec;
            std::filesystem::remove(corrupt_path, remove_ec);
        }

        std::printf("\n=== Summary ===\n");
        if (g_failures == 0) {
            std::printf("ALL CHECKPOINT 2 CHECKS PASSED\n");
            return 0;
        }
        std::printf("%d FAILURES\n", g_failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
