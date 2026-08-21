// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <cstdio>
#include <filesystem>
#include <map>
#include <string>

#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

std::string json_escape(const std::string& value) {
    std::string out;
    for (char c : value) {
        switch (c) {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default: out += c; break;
        }
    }
    return out;
}

std::string dimensions(const std::vector<uint64_t>& dims) {
    std::string out = "[";
    for (size_t i = 0; i < dims.size(); ++i) {
        if (i != 0) out += ",";
        out += std::to_string(dims[i]);
    }
    return out + "]";
}

std::map<std::string, std::string> mappings(const ModelArtifactManifest& manifest) {
    std::map<std::string, std::string> out;
    for (const auto& mapped : manifest.mapped_tensors) {
        out.emplace(mapped.source_name, mapped.logical.name());
    }
    return out;
}

void print_human(const GgufArtifact& artifact, const ModelArtifactManifest& manifest) {
    const auto mapped = mappings(manifest);
    std::printf("path: %s\n", artifact.path.string().c_str());
    std::printf("file_size: %llu\n", static_cast<unsigned long long>(artifact.telemetry.file_size));
    std::printf("gguf_version: %u\n", artifact.version);
    std::printf("metadata_count: %zu\n", artifact.metadata.size());
    std::printf("tensor_count: %zu\n", artifact.tensors.size());
    std::printf("alignment: %u\n", artifact.alignment);
    std::printf("tensor_data_offset: %llu\n", static_cast<unsigned long long>(artifact.tensor_data_offset));
    std::printf("architecture: %s\n", manifest.architecture.c_str());
    std::printf("artifact_kind: %s\n",
                manifest.kind == ModelArtifactKind::FullModel ? "FullModel" : "UnsupportedArtifact");
    if (manifest.kind == ModelArtifactKind::FullModel) {
        std::printf("vocab_size: %lld\n", static_cast<long long>(manifest.config.vocab));
        std::printf("hidden_size: %lld\n", static_cast<long long>(manifest.config.hidden));
        std::printf("intermediate_size: %lld\n", static_cast<long long>(manifest.config.intermediate));
        std::printf("layer_count: %lld\n", static_cast<long long>(manifest.config.n_layers));
        std::printf("head_count: %lld\n", static_cast<long long>(manifest.config.n_q_heads));
        std::printf("kv_head_count: %lld\n", static_cast<long long>(manifest.config.n_kv_heads));
        std::printf("context_length: %lld\n", static_cast<long long>(manifest.config.max_positions));
        std::printf("output_semantics: %s\n", manifest.tied_embeddings ? "tied" : "untied");
        std::printf("mapped_tensor_count: %zu\n", manifest.mapped_tensors.size());
        std::printf("materializable: %s\n", manifest.materializable ? "yes" : "no");
    }
    std::printf("metadata_bytes_read: %llu\n",
                static_cast<unsigned long long>(artifact.telemetry.metadata_bytes_read));
    std::printf("estimated_metadata_bytes: %llu\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_metadata_bytes));
    std::printf("estimated_tensor_index_bytes: %llu\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_tensor_index_bytes));
    std::printf("estimated_manifest_bytes: %llu\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_manifest_bytes));
    std::printf("metadata_parse_milliseconds: %.3f\n", artifact.telemetry.metadata_parse_milliseconds);
    std::printf("tensor_index_milliseconds: %.3f\n", artifact.telemetry.tensor_index_milliseconds);
    std::printf("parse_milliseconds: %.3f\n", artifact.telemetry.parse_milliseconds);
    for (const auto& reason : manifest.unsupported_reasons) {
        std::printf("unsupported: %s\n", reason.c_str());
    }
    std::printf("tensors:\n");
    for (const auto& tensor : artifact.tensors) {
        const auto mapping = mapped.find(tensor.name);
        std::printf("  %s dims=%s encoding=%s offset=%llu bytes=%llu semantic=%s materializable=%s\n",
                    tensor.name.c_str(), dimensions(tensor.gguf_dimensions).c_str(),
                    gguf_encoding_name(tensor.encoding).c_str(),
                    static_cast<unsigned long long>(tensor.absolute_offset),
                    static_cast<unsigned long long>(tensor.encoded_length),
                    mapping == mapped.end() ? "unused" : mapping->second.c_str(),
                    gguf_encoding_materializable(tensor.encoding) ? "yes" : "no");
    }
}

void print_json(const GgufArtifact& artifact, const ModelArtifactManifest& manifest) {
    const auto mapped = mappings(manifest);
    std::printf("{\n");
    std::printf("  \"path\": \"%s\",\n", json_escape(artifact.path.string()).c_str());
    std::printf("  \"file_size\": %llu,\n", static_cast<unsigned long long>(artifact.telemetry.file_size));
    std::printf("  \"gguf_version\": %u,\n", artifact.version);
    std::printf("  \"metadata_count\": %zu,\n", artifact.metadata.size());
    std::printf("  \"tensor_count\": %zu,\n", artifact.tensors.size());
    std::printf("  \"alignment\": %u,\n", artifact.alignment);
    std::printf("  \"architecture\": \"%s\",\n", json_escape(manifest.architecture).c_str());
    std::printf("  \"artifact_kind\": \"%s\",\n",
                manifest.kind == ModelArtifactKind::FullModel ? "FullModel" : "UnsupportedArtifact");
    std::printf("  \"tied_embeddings\": %s,\n", manifest.tied_embeddings ? "true" : "false");
    std::printf("  \"materializable\": %s,\n", manifest.materializable ? "true" : "false");
    std::printf("  \"mapped_tensor_count\": %zu,\n", manifest.mapped_tensors.size());
    std::printf("  \"estimated_metadata_bytes\": %llu,\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_metadata_bytes));
    std::printf("  \"estimated_tensor_index_bytes\": %llu,\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_tensor_index_bytes));
    std::printf("  \"estimated_manifest_bytes\": %llu,\n",
                static_cast<unsigned long long>(artifact.telemetry.estimated_manifest_bytes));
    std::printf("  \"metadata_parse_milliseconds\": %.6f,\n",
                artifact.telemetry.metadata_parse_milliseconds);
    std::printf("  \"tensor_index_milliseconds\": %.6f,\n",
                artifact.telemetry.tensor_index_milliseconds);
    std::printf("  \"parse_milliseconds\": %.6f,\n", artifact.telemetry.parse_milliseconds);
    std::printf("  \"tensors\": [\n");
    for (size_t i = 0; i < artifact.tensors.size(); ++i) {
        const auto& tensor = artifact.tensors[i];
        const auto mapping = mapped.find(tensor.name);
        std::printf("    {\"name\":\"%s\",\"dimensions\":%s,\"encoding\":\"%s\","
                    "\"offset\":%llu,\"encoded_length\":%llu,\"semantic\":\"%s\","
                    "\"materializable\":%s}%s\n",
                    json_escape(tensor.name).c_str(), dimensions(tensor.gguf_dimensions).c_str(),
                    gguf_encoding_name(tensor.encoding).c_str(),
                    static_cast<unsigned long long>(tensor.absolute_offset),
                    static_cast<unsigned long long>(tensor.encoded_length),
                    mapping == mapped.end() ? "unused" : json_escape(mapping->second).c_str(),
                    gguf_encoding_materializable(tensor.encoding) ? "true" : "false",
                    i + 1 == artifact.tensors.size() ? "" : ",");
    }
    std::printf("  ]\n}\n");
}

}  // namespace

int main(int argc, char** argv) {
    try {
        bool json = false;
        std::filesystem::path path;
        for (int i = 1; i < argc; ++i) {
            if (std::string(argv[i]) == "--json") json = true;
            else if (path.empty()) path = argv[i];
            else throw std::runtime_error("usage: orcengine_gguf_inspect [--json] MODEL.gguf");
        }
        if (path.empty()) throw std::runtime_error("usage: orcengine_gguf_inspect [--json] MODEL.gguf");
        const GgufArtifact artifact = index_gguf(path);
        const ModelArtifactManifest manifest = map_llama_model(artifact);
        if (json) print_json(artifact, manifest); else print_human(artifact, manifest);
        return manifest.kind == ModelArtifactKind::FullModel ? 0 : 2;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 1;
    }
}
