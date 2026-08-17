// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <cstdint>
#include <filesystem>
#include <map>
#include <optional>
#include <stdexcept>
#include <string>
#include <variant>
#include <vector>

#include "orcengine/backing_extent.hpp"
#include "orcengine/logical_tensor.hpp"
#include "orcengine/model.hpp"

namespace orcengine {

class GgufError : public std::runtime_error {
public:
    explicit GgufError(const std::string& message)
        : std::runtime_error("GGUF validation failed: " + message) {}
};

enum class GgufValueType : uint32_t {
    UInt8 = 0,
    Int8 = 1,
    UInt16 = 2,
    Int16 = 3,
    UInt32 = 4,
    Int32 = 5,
    Float32 = 6,
    Bool = 7,
    String = 8,
    Array = 9,
    UInt64 = 10,
    Int64 = 11,
    Float64 = 12,
};

struct GgufValue {
    GgufValueType type = GgufValueType::UInt8;
    std::variant<uint64_t, int64_t, double, bool, std::string,
                 std::vector<GgufValue>> data = uint64_t{0};
};

enum class GgufTensorEncoding : uint32_t {
    F32 = 0,
    F16 = 1,
    Q4_0 = 2,
    Q4_1 = 3,
    Q5_0 = 6,
    Q5_1 = 7,
    Q8_0 = 8,
    Q2_K = 10,
    Q3_K = 11,
    Q4_K = 12,
    Q5_K = 13,
    Q6_K = 14,
    Q8_K = 15,
};

struct GgufTensorInfo {
    std::string name;
    std::vector<uint64_t> gguf_dimensions;
    TensorShape logical_shape;
    GgufTensorEncoding encoding = GgufTensorEncoding::F32;
    uint64_t relative_offset = 0;
    uint64_t absolute_offset = 0;
    uint64_t encoded_length = 0;
};

struct GgufLimits {
    uint64_t max_file_bytes = 4ULL * 1024 * 1024 * 1024;
    uint64_t max_metadata_entries = 100'000;
    uint64_t max_tensors = 10'000;
    uint64_t max_key_bytes = 65'535;
    uint64_t max_string_bytes = 64ULL * 1024 * 1024;
    uint64_t max_array_elements = 10'000'000;
    uint64_t max_metadata_allocation = 512ULL * 1024 * 1024;
    uint32_t max_array_depth = 8;
    uint32_t max_tensor_rank = 4;
    uint64_t max_dimension = 2'147'483'647;
};

struct GgufTelemetry {
    uint64_t file_size = 0;
    uint64_t metadata_bytes_read = 0;
    uint64_t estimated_metadata_bytes = 0;
    uint64_t estimated_tensor_index_bytes = 0;
    uint64_t estimated_manifest_bytes = 0;
    double metadata_parse_milliseconds = 0.0;
    double tensor_index_milliseconds = 0.0;
    double parse_milliseconds = 0.0;
};

struct GgufArtifact {
    std::filesystem::path path;
    uint32_t version = 0;
    uint32_t alignment = 32;
    uint64_t tensor_data_offset = 0;
    std::map<std::string, GgufValue> metadata;
    std::vector<GgufTensorInfo> tensors;
    GgufTelemetry telemetry;
};

enum class SemanticTensorRole {
    TokenEmbedding,
    FinalNorm,
    OutputHead,
    AttentionNorm,
    AttentionQuery,
    AttentionKey,
    AttentionValue,
    AttentionOutput,
    FfnNorm,
    FfnGate,
    FfnUp,
    FfnDown,
};

struct SemanticTensorId {
    SemanticTensorRole role = SemanticTensorRole::TokenEmbedding;
    int64_t layer = -1;
};

struct MappedGgufTensor {
    SemanticTensorId semantic;
    std::string source_name;
    LogicalTensor logical;
    BackingExtent backing;
    GgufTensorEncoding encoding = GgufTensorEncoding::F32;
};

enum class ModelArtifactKind {
    FullModel,
    UnsupportedArtifact,
};

struct ModelArtifactManifest {
    ModelArtifactKind kind = ModelArtifactKind::UnsupportedArtifact;
    std::string architecture;
    ModelConfig config;
    bool tied_embeddings = false;
    bool materializable = false;
    std::vector<MappedGgufTensor> mapped_tensors;
    std::vector<std::string> unused_tensor_names;
    std::vector<std::string> unsupported_reasons;
};

GgufArtifact index_gguf(const std::filesystem::path& path,
                        const GgufLimits& limits = {});
ModelArtifactManifest map_llama_model(const GgufArtifact& artifact);
ResidentView materialize_gguf_tensor(const MappedGgufTensor& tensor);
ResidentView materialize_gguf_tensor_rows(const MappedGgufTensor& tensor,
                                          uint64_t row_begin,
                                          uint64_t row_count,
                                          uint64_t& backing_bytes_read);
Model materialize_gguf_model(const ModelArtifactManifest& manifest);

const GgufValue& require_metadata(const GgufArtifact& artifact,
                                  const std::string& key);
uint64_t metadata_u64(const GgufArtifact& artifact, const std::string& key);
double metadata_f64(const GgufArtifact& artifact, const std::string& key);
std::string metadata_string(const GgufArtifact& artifact, const std::string& key);

std::string gguf_encoding_name(GgufTensorEncoding encoding);
std::string semantic_tensor_name(const SemanticTensorId& semantic);
bool gguf_encoding_materializable(GgufTensorEncoding encoding);

}  // namespace orcengine
