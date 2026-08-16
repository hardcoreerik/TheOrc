// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <limits>
#include <set>
#include <string>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"
#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

int failures = 0;

void pass(const std::string& name) { std::printf("[PASS] %s\n", name.c_str()); }

void fail(const std::string& name, const std::string& reason) {
    std::printf("[FAIL] %s: %s\n", name.c_str(), reason.c_str());
    ++failures;
}

void require(bool condition, const std::string& name, const std::string& reason) {
    if (condition) pass(name); else fail(name, reason);
}

float max_abs_difference(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) return std::numeric_limits<float>::infinity();
    float maximum = 0.0f;
    for (size_t i = 0; i < a.size(); ++i) maximum = std::max(maximum, std::fabs(a[i] - b[i]));
    return maximum;
}

ForwardResult load_and_forward(const std::filesystem::path& path,
                               const std::vector<int64_t>& tokens,
                               ModelArtifactManifest* out_manifest = nullptr) {
    const GgufArtifact artifact = index_gguf(path);
    ModelArtifactManifest manifest = map_llama_model(artifact);
    Model model = materialize_gguf_model(manifest);
    if (out_manifest != nullptr) *out_manifest = manifest;
    return forward(model, tokens);
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 3) throw std::runtime_error("usage: test_gguf FIXTURE_DIR PHASE1_FIXTURE_DIR");
        const std::filesystem::path fixtures = argv[1];
        const std::string phase1_fixtures = argv[2];

        const std::vector<std::string> malformed = {
            "bad_magic", "unsupported_version", "truncated_header", "truncated_metadata",
            "truncated_tensor_data", "huge_tensor_count", "integer_overflow_dims",
            "invalid_metadata_type", "invalid_utf8_string", "duplicate_metadata_key",
            "duplicate_tensor_name", "zero_dimension", "unsupported_dtype",
            "misaligned_tensor_offset", "overlapping_tensors", "missing_required_metadata",
            "inconsistent_dimensions", "huge_metadata_count", "truncated_string",
            "malformed_array", "invalid_alignment", "invalid_bool", "missing_required_tensor",
            "wrong_output_shape", "quantized_missing_version",
            "metadata_key_uppercase", "metadata_key_hyphen", "metadata_key_leading_underscore",
            "metadata_key_trailing_underscore", "metadata_key_double_underscore",
            "metadata_key_empty_segment", "metadata_key_non_ascii", "tensor_name_over_64_bytes",
            "missing_token_embedding",
        };
        const GgufArtifact baseline = index_gguf(fixtures / "malformed" / "_valid_baseline.gguf");
        require(baseline.version == 3 && baseline.tensors.size() == 1,
                "valid structural baseline", "valid fixture did not index");
        require(baseline.telemetry.estimated_metadata_bytes > 0 &&
                    baseline.telemetry.estimated_tensor_index_bytes > 0 &&
                    baseline.telemetry.estimated_manifest_bytes ==
                        baseline.telemetry.estimated_metadata_bytes +
                        baseline.telemetry.estimated_tensor_index_bytes &&
                    baseline.telemetry.metadata_parse_milliseconds > 0.0 &&
                    baseline.telemetry.tensor_index_milliseconds > 0.0,
                "model-open telemetry split", "metadata/index telemetry is incomplete");
        for (const std::string& name : malformed) {
            bool rejected = false;
            try {
                const GgufArtifact artifact = index_gguf(fixtures / "malformed" / (name + ".gguf"));
                (void)map_llama_model(artifact);
            } catch (const GgufError&) {
                rejected = true;
            }
            require(rejected, "reject malformed " + name, "malformed fixture was accepted");
        }

        const GgufArtifact unsupported_artifact = index_gguf(fixtures / "unsupported_architecture.gguf");
        const ModelArtifactManifest unsupported_manifest = map_llama_model(unsupported_artifact);
        require(unsupported_manifest.kind == ModelArtifactKind::UnsupportedArtifact &&
                    !unsupported_manifest.unsupported_reasons.empty(),
                "unsupported architecture classification", "unsupported artifact was presented as executable");

        const LoadedFixture expected = load_fixture(phase1_fixtures + "/fixture_tied.txt");
        const ForwardResult oracle = forward(expected.model, expected.token_ids);

        ModelArtifactManifest untied_manifest;
        const ForwardResult untied = load_and_forward(fixtures / "model_untied.gguf",
                                                      expected.token_ids, &untied_manifest);
        require(untied_manifest.kind == ModelArtifactKind::FullModel &&
                    !untied_manifest.tied_embeddings && untied_manifest.materializable &&
                    untied_manifest.mapped_tensors.size() == 21,
                "untied semantic inventory", "untied mapping is incomplete or misclassified");
        require(max_abs_difference(oracle.logits, untied.logits) == 0.0f,
                "GGUF F32 forward vs frozen Phase-1 fixture", "logits differ");

        ModelArtifactManifest tied_manifest;
        const ForwardResult tied = load_and_forward(fixtures / "model_tied.gguf",
                                                    expected.token_ids, &tied_manifest);
        require(tied_manifest.tied_embeddings && tied_manifest.mapped_tensors.size() == 20,
                "tied semantic inventory", "absent output.weight was not mapped by the Llama profile");
        require(max_abs_difference(untied.logits, tied.logits) == 0.0f,
                "GGUF tied vs untied byte-identical output", "logits differ");

        const ForwardResult reordered = load_and_forward(fixtures / "model_reordered.gguf", expected.token_ids);
        const ForwardResult aligned = load_and_forward(fixtures / "model_align64.gguf", expected.token_ids);
        require(max_abs_difference(untied.logits, reordered.logits) == 0.0f,
                "metadata and tensor descriptor order metamorphic", "logits differ");
        require(max_abs_difference(untied.logits, aligned.logits) == 0.0f,
                "offset/alignment metamorphic", "logits differ");

        const GgufArtifact f16_artifact = index_gguf(fixtures / "model_f16.gguf");
        const ModelArtifactManifest f16_manifest = map_llama_model(f16_artifact);
        const ForwardResult f16 = forward(materialize_gguf_model(f16_manifest), expected.token_ids);
        const float f16_difference = max_abs_difference(untied.logits, f16.logits);
        require(f16_manifest.materializable && std::isfinite(f16_difference) && f16_difference <= 0.02f &&
                    f16.selected_token == untied.selected_token,
                "F16 source to F32 resident materialization", "F16 differential exceeded 0.02 or changed argmax");

        bool unsupported_failed = false;
        try {
            MappedGgufTensor unsupported = untied_manifest.mapped_tensors.front();
            unsupported.encoding = GgufTensorEncoding::Q4_K;
            (void)materialize_gguf_tensor(unsupported);
        } catch (const GgufError&) {
            unsupported_failed = true;
        }
        require(unsupported_failed, "unsupported quantized materialization fails clearly",
                "Q4_K materialization was accepted");

        std::set<std::string> logical_names;
        bool extents_are_file_backed = true;
        for (const auto& mapped : untied_manifest.mapped_tensors) {
            logical_names.insert(mapped.logical.name());
            extents_are_file_backed = extents_are_file_backed && mapped.backing.bytes().empty() &&
                                      mapped.backing.byte_offset() > 0 && mapped.backing.byte_length() > 0;
        }
        require(logical_names.size() == untied_manifest.mapped_tensors.size(),
                "one source tensor per semantic identity", "duplicate semantic assignment");
        require(extents_are_file_backed, "GGUF BackingExtents stay nonresident after index",
                "indexing copied tensor data");

        const std::filesystem::path source_path = fixtures / "model_untied.gguf";
        const GgufArtifact source_artifact = index_gguf(source_path);
        const auto output_tensor = std::find_if(
            source_artifact.tensors.begin(), source_artifact.tensors.end(),
            [](const GgufTensorInfo& tensor) { return tensor.name == "output.weight"; });
        if (output_tensor == source_artifact.tensors.end()) throw std::runtime_error("fixture output tensor missing");
        std::ifstream source_stream(source_path, std::ios::binary);
        std::vector<uint8_t> corrupted_bytes((std::istreambuf_iterator<char>(source_stream)),
                                             std::istreambuf_iterator<char>());
        const size_t output_offset = static_cast<size_t>(output_tensor->absolute_offset);
        const std::filesystem::path corrupted_path =
            std::filesystem::temp_directory_path() / "orcengine_phase2_corrupted_f32.gguf";
        auto write_corruption = [&] {
            std::ofstream output(corrupted_path, std::ios::binary | std::ios::trunc);
            output.write(reinterpret_cast<const char*>(corrupted_bytes.data()),
                         static_cast<std::streamsize>(corrupted_bytes.size()));
            if (!output) throw std::runtime_error("cannot write corrupted F32 fixture");
        };
        corrupted_bytes[output_offset] = 0x00;
        corrupted_bytes[output_offset + 1] = 0x00;
        corrupted_bytes[output_offset + 2] = 0x00;
        corrupted_bytes[output_offset + 3] = 0x4f;  // finite 2^31, not NaN/Inf
        write_corruption();
        const ForwardResult finite_corruption = load_and_forward(corrupted_path, expected.token_ids);
        require(max_abs_difference(untied.logits, finite_corruption.logits) > 1.0f,
                "finite F32 corruption breaks differential", "corrupted output weight escaped comparison");

        corrupted_bytes[output_offset] = 0x00;
        corrupted_bytes[output_offset + 1] = 0x00;
        corrupted_bytes[output_offset + 2] = 0xc0;
        corrupted_bytes[output_offset + 3] = 0x7f;  // quiet NaN
        write_corruption();
        bool nan_rejected = false;
        try {
            (void)load_and_forward(corrupted_path, expected.token_ids);
        } catch (const std::exception&) {
            nan_rejected = true;
        }
        std::error_code ignored;
        std::filesystem::remove(corrupted_path, ignored);
        require(nan_rejected, "non-finite F32 corruption fails closed",
                "NaN output weight reached execution");

        if (failures == 0) {
            std::printf("ALL GGUF CONFORMANCE TESTS PASSED: 34 malformed, 7 valid/indexable artifacts, "
                        "4 forward equivalences, 2 corruption regressions\n");
            return 0;
        }
        std::printf("%d GGUF CONFORMANCE FAILURES\n", failures);
        return 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
