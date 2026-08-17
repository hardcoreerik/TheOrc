// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <limits>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/materialization.hpp"
#include "orcengine/streaming.hpp"

using namespace orcengine;

namespace {
int failures = 0;

void check(bool condition, const char* name) {
    std::printf("[%s] %s\n", condition ? "PASS" : "FAIL", name);
    if (!condition) ++failures;
}

bool identical(const ForwardResult& left, const ForwardResult& right) {
    if (left.logits != right.logits || left.selected_token != right.selected_token ||
        left.taps.size() != right.taps.size()) return false;
    for (const auto& [name, tap] : left.taps) {
        const auto it = right.taps.find(name);
        if (it == right.taps.end() || tap.dims != it->second.dims ||
            tap.data != it->second.data) return false;
    }
    return true;
}

ModelSourceBinding bind_memory_model(const Model& model) {
    ModelSource source;
    source.config = model.config();
    source.tied_embeddings = model.manifest.tied_embeddings;
    auto add = [&](TensorRole role, int64_t layer, const std::string& name,
                   const ResidentView& view, const std::string& identity) {
        source.tensors.push_back({
            {role, layer}, LogicalTensor(name, view.shape()),
            BackingExtent::FromF32(view.raw()),
            static_cast<uint64_t>(view.raw().size()) * sizeof(float), identity});
    };
    add(TensorRole::TokenEmbedding, -1, "token_embedding", model.token_embedding,
        "memory:shared-bookend");
    add(TensorRole::FinalNorm, -1, "final_norm", model.final_norm_weight,
        "memory:final_norm");
    if (model.lm_head) {
        add(TensorRole::OutputHead, -1, "output_head", *model.lm_head,
            "memory:output_head");
    }
    for (int64_t layer = 0; layer < model.config().n_layers; ++layer) {
        const LayerWeights& weights = model.layers.at(static_cast<size_t>(layer));
        const std::string prefix = "layer" + std::to_string(layer) + ".";
        add(TensorRole::AttentionNorm, layer, prefix + "attention_norm",
            weights.attn_norm_weight, prefix + "attention_norm");
        add(TensorRole::AttentionQuery, layer, prefix + "attention_query",
            weights.w_q, prefix + "attention_query");
        add(TensorRole::AttentionKey, layer, prefix + "attention_key",
            weights.w_k, prefix + "attention_key");
        add(TensorRole::AttentionValue, layer, prefix + "attention_value",
            weights.w_v, prefix + "attention_value");
        add(TensorRole::AttentionOutput, layer, prefix + "attention_output",
            weights.w_o, prefix + "attention_output");
        add(TensorRole::FfnNorm, layer, prefix + "ffn_norm",
            weights.ffn_norm_weight, prefix + "ffn_norm");
        add(TensorRole::FfnGate, layer, prefix + "ffn_gate",
            weights.w_gate, prefix + "ffn_gate");
        add(TensorRole::FfnUp, layer, prefix + "ffn_up",
            weights.w_up, prefix + "ffn_up");
        add(TensorRole::FfnDown, layer, prefix + "ffn_down",
            weights.w_down, prefix + "ffn_down");
    }
    TensorMaterializer full = [](const LogicalTensor& logical,
                                 const BackingExtent& backing) {
        return materialize(logical, backing);
    };
    TensorRegionMaterializer rows = [](const LogicalTensor& logical,
                                       const BackingExtent& backing,
                                       const TensorRegion& region) {
        if (logical.shape().ndim() != 2 || region.row_count == 0) {
            throw std::runtime_error("invalid memory row request");
        }
        const uint64_t total_rows = static_cast<uint64_t>(logical.shape().dim(0));
        const uint64_t columns = static_cast<uint64_t>(logical.shape().dim(1));
        if (region.row_begin >= total_rows ||
            region.row_count > total_rows - region.row_begin) {
            throw std::runtime_error("memory row request out of bounds");
        }
        const uint64_t begin = region.row_begin * columns;
        const uint64_t count = region.row_count * columns;
        if (count > std::numeric_limits<size_t>::max() / sizeof(float) ||
            (begin + count) * sizeof(float) > backing.bytes().size()) {
            throw std::runtime_error("memory row backing is short");
        }
        std::vector<float> values(static_cast<size_t>(count));
        std::memcpy(values.data(), backing.bytes().data() + begin * sizeof(float),
                    static_cast<size_t>(count) * sizeof(float));
        return MaterializedRegion{
            ResidentView(TensorShape({static_cast<int64_t>(region.row_count),
                                      static_cast<int64_t>(columns)}),
                         std::move(values)),
            count * sizeof(float)};
    };
    return {std::move(source), std::move(full), std::move(rows)};
}

StreamingModel virtual_model(const Model& model, uint64_t chunk_rows,
                             uint64_t budget = std::numeric_limits<uint64_t>::max(),
                             ExecutionObserver observer = {}) {
    ModelSourceBinding binding = bind_memory_model(model);
    StreamingConfig config;
    config.residency_budget_bytes = budget;
    config.observer = std::move(observer);
    config.virtualize_bookends = true;
    config.region_materializer = std::move(binding.materialize_region);
    config.output_chunk_rows = chunk_rows;
    return StreamingModel(std::move(binding.source), std::move(binding.materialize),
                          std::move(config));
}

bool partition_rejected(uint64_t rows, std::vector<TensorRegion> regions) {
    try {
        validate_complete_row_partition(rows, regions);
        return false;
    } catch (const StreamingError&) {
        return true;
    }
}
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 3) throw std::runtime_error("usage: test_bookend_virtualization FIXTURES PHASE1_FIXTURES");
        const std::filesystem::path fixtures = argv[1];
        const LoadedFixture fixture = load_fixture(
            (std::filesystem::path(argv[2]) / "fixture_tied.txt").string());
        const ForwardResult reference = forward(fixture.model, fixture.token_ids);

        for (uint64_t chunk : {1ULL, 16ULL, 64ULL, 256ULL, 1024ULL, 7ULL}) {
            StreamingModel model = virtual_model(fixture.model, chunk);
            check(identical(reference, model.forward(fixture.token_ids)),
                  ("chunk " + std::to_string(chunk) + " is bit-identical").c_str());
        }

        StreamingModel measured = virtual_model(fixture.model, 7);
        const ForwardResult measured_result = measured.forward(fixture.token_ids);
        const StreamingTelemetry telemetry = measured.telemetry();
        const size_t unique_tokens = std::unordered_set<int64_t>(
            fixture.token_ids.begin(), fixture.token_ids.end()).size();
        check(identical(reference, measured_result) &&
              telemetry.embedding_region_count == unique_tokens,
              "embedding materializes one row per unique token");
        check(telemetry.output_region_count ==
                  (static_cast<uint64_t>(fixture.model.config().vocab) + 6) / 7 &&
              telemetry.current_resident_weight_bytes ==
                  static_cast<uint64_t>(fixture.model.config().hidden) * sizeof(float),
              "output partition covers every vocabulary row and releases every chunk");

        ModelSourceBinding tied_binding = bind_memory_model(fixture.model);
        const size_t output_heads = std::count_if(
            tied_binding.source.tensors.begin(), tied_binding.source.tensors.end(),
            [](const SourceTensor& tensor) {
                return tensor.identity.role == TensorRole::OutputHead;
            });
        check(fixture.model.manifest.tied_embeddings && output_heads == 0,
              "tied execution has one physical bookend backing and no output duplicate");

        const uint64_t peak = telemetry.peak_resident_weight_bytes;
        StreamingModel exact = virtual_model(fixture.model, 7, peak);
        check(identical(reference, exact.forward(fixture.token_ids)) &&
              exact.telemetry().peak_resident_weight_bytes == peak,
              "virtualized execution succeeds exactly at measured peak");
        bool below_failed = false;
        try {
            StreamingModel below = virtual_model(fixture.model, 7, peak - 1);
            (void)below.forward(fixture.token_ids);
        } catch (const ResidencyBudgetError&) {
            below_failed = true;
        }
        check(below_failed, "one byte below measured peak fails closed");
        bool phase3_failed = false;
        try {
            ModelSourceBinding binding = bind_memory_model(fixture.model);
            StreamingModel phase3(std::move(binding.source), std::move(binding.materialize),
                                  {peak, {}});
            (void)phase3.forward(fixture.token_ids);
        } catch (const ResidencyBudgetError&) {
            phase3_failed = true;
        }
        check(phase3_failed, "same strong budget rejects Phase-3 full bookends");

        std::vector<ExecutionEvent> events;
        StreamingModel observed = virtual_model(
            fixture.model, 7, std::numeric_limits<uint64_t>::max(),
            [&](const ExecutionEvent& event) { events.push_back(event); });
        check(identical(reference, observed.forward(fixture.token_ids)),
              "observer enabled cannot change output");
        const auto region_events = std::count_if(events.begin(), events.end(),
            [](const ExecutionEvent& event) {
                return event.kind == ExecutionEventKind::TensorRegionMaterialized &&
                       event.row_count > 0 && event.tensor_bytes > 0 &&
                       event.operation != ExecutionOperation::None &&
                       event.milliseconds >= 0.0;
            });
        check(static_cast<uint64_t>(region_events) ==
                  observed.telemetry().region_materialization_count,
              "observer reports measured role, region, bytes, operation, and timing");

        check(partition_rejected(8, {{0, 3}, {4, 4}}), "skipped vocabulary row rejected");
        check(partition_rejected(8, {{0, 4}, {3, 5}}), "overlapping vocabulary rows rejected");
        check(partition_rejected(8, {{4, 4}, {0, 4}}), "reordered vocabulary rows rejected");
        check(partition_rejected(8, {{0, 4}, {0, 4}}), "duplicate vocabulary rows rejected");
        check(partition_rejected(8, {{0, 9}}), "oversized final chunk rejected");
        check(partition_rejected(8, {{0, 7}}), "incomplete final chunk rejected");
        bool overflow_failed = false;
        try {
            validate_complete_row_partition(std::numeric_limits<uint64_t>::max(),
                {{0, std::numeric_limits<uint64_t>::max()},
                 {std::numeric_limits<uint64_t>::max(), 1}});
        } catch (const std::overflow_error&) {
            overflow_failed = true;
        }
        check(overflow_failed, "partition offset overflow rejected");

        ModelSourceBinding wrong = bind_memory_model(fixture.model);
        StreamingConfig wrong_config;
        wrong_config.virtualize_bookends = true;
        wrong_config.output_chunk_rows = 7;
        wrong_config.region_materializer = [](const LogicalTensor&, const BackingExtent&,
                                              const TensorRegion&) {
            return MaterializedRegion{ResidentView(TensorShape({1, 1}), {0.0F}), 4};
        };
        bool wrong_shape_failed = false;
        try {
            StreamingModel model(std::move(wrong.source), std::move(wrong.materialize),
                                 std::move(wrong_config));
            (void)model.forward(fixture.token_ids);
        } catch (const StreamingError&) {
            wrong_shape_failed = true;
        }
        check(wrong_shape_failed, "wrong region return shape fails closed");

        ModelSourceBinding bogus_bytes = bind_memory_model(fixture.model);
        StreamingConfig bogus_bytes_config;
        bogus_bytes_config.virtualize_bookends = true;
        bogus_bytes_config.output_chunk_rows = 7;
        bogus_bytes_config.region_materializer = [](const LogicalTensor& logical,
                                                    const BackingExtent& backing,
                                                    const TensorRegion& region) {
            const uint64_t columns = static_cast<uint64_t>(logical.shape().dim(1));
            std::vector<float> values(static_cast<size_t>(region.row_count * columns));
            return MaterializedRegion{
                ResidentView(TensorShape({static_cast<int64_t>(region.row_count),
                                          static_cast<int64_t>(columns)}),
                             std::move(values)),
                static_cast<uint64_t>(backing.byte_length()) + 1};
        };
        bool bogus_bytes_failed = false;
        try {
            StreamingModel model(std::move(bogus_bytes.source),
                                 std::move(bogus_bytes.materialize),
                                 std::move(bogus_bytes_config));
            (void)model.forward(fixture.token_ids);
        } catch (const StreamingError&) {
            bogus_bytes_failed = true;
        }
        check(bogus_bytes_failed, "impossible reported backing-byte count fails closed");

        ModelSourceBinding contradiction = bind_memory_model(fixture.model);
        contradiction.source.tied_embeddings = true;
        contradiction.source.tensors.push_back(contradiction.source.tensors.front());
        contradiction.source.tensors.back().identity.role = TensorRole::OutputHead;
        bool contradiction_failed = false;
        try {
            StreamingModel model(std::move(contradiction.source),
                                 std::move(contradiction.materialize));
        } catch (const StreamingError&) {
            contradiction_failed = true;
        }
        check(contradiction_failed, "tied model with output head fails closed");

        Model explicit_model = fixture.model;
        explicit_model.manifest.tied_embeddings = false;
        explicit_model.lm_head = fixture.model.token_embedding;
        ModelSourceBinding missing_output = bind_memory_model(explicit_model);
        missing_output.source.tensors.erase(std::remove_if(
            missing_output.source.tensors.begin(), missing_output.source.tensors.end(),
            [](const SourceTensor& tensor) {
                return tensor.identity.role == TensorRole::OutputHead;
            }), missing_output.source.tensors.end());
        bool missing_output_failed = false;
        try {
            StreamingModel model(std::move(missing_output.source),
                                 std::move(missing_output.materialize));
        } catch (const StreamingError&) {
            missing_output_failed = true;
        }
        check(missing_output_failed, "untied model missing output head fails closed");

        ModelSourceBinding duplicate_embedding = bind_memory_model(fixture.model);
        duplicate_embedding.source.tensors.push_back(
            duplicate_embedding.source.tensors.front());
        bool duplicate_embedding_failed = false;
        try {
            StreamingModel model(std::move(duplicate_embedding.source),
                                 std::move(duplicate_embedding.materialize));
        } catch (const StreamingError&) {
            duplicate_embedding_failed = true;
        }
        check(duplicate_embedding_failed, "duplicate semantic embedding fails closed");

        ModelSourceBinding malformed_shape = bind_memory_model(fixture.model);
        malformed_shape.source.tensors.front().logical = LogicalTensor(
            "malformed_embedding",
            TensorShape({fixture.model.config().vocab,
                         fixture.model.config().hidden, 1}));
        bool malformed_shape_failed = false;
        try {
            StreamingConfig config;
            config.virtualize_bookends = true;
            config.region_materializer = std::move(malformed_shape.materialize_region);
            StreamingModel model(std::move(malformed_shape.source),
                                 std::move(malformed_shape.materialize),
                                 std::move(config));
        } catch (const StreamingError&) {
            malformed_shape_failed = true;
        }
        check(malformed_shape_failed, "malformed region rank/dimensions fail closed");

        const std::filesystem::path short_path = fixtures / "phase4_short_region.gguf";
        std::filesystem::copy_file(fixtures / "model_tied.gguf", short_path,
                                   std::filesystem::copy_options::overwrite_existing);
        ModelArtifactManifest short_manifest = map_llama_model(index_gguf(short_path));
        const auto embedding = std::find_if(
            short_manifest.mapped_tensors.begin(), short_manifest.mapped_tensors.end(),
            [](const MappedGgufTensor& tensor) {
                return tensor.semantic.role == SemanticTensorRole::TokenEmbedding;
            });
        const uint64_t row_bytes = static_cast<uint64_t>(embedding->logical.shape().dim(1)) * 4;
        uint64_t ignored_bytes = 0;
        bool zero_rows_failed = false;
        try { (void)materialize_gguf_tensor_rows(*embedding, 0, 0, ignored_bytes); }
        catch (const GgufError&) { zero_rows_failed = true; }
        check(zero_rows_failed, "zero-row GGUF region rejected");
        bool bounds_failed = false;
        try {
            (void)materialize_gguf_tensor_rows(
                *embedding, static_cast<uint64_t>(embedding->logical.shape().dim(0)),
                1, ignored_bytes);
        } catch (const GgufError&) { bounds_failed = true; }
        check(bounds_failed, "out-of-bounds GGUF region rejected");
        bool end_bounds_failed = false;
        try {
            (void)materialize_gguf_tensor_rows(
                *embedding, 0,
                static_cast<uint64_t>(embedding->logical.shape().dim(0)) + 1,
                ignored_bytes);
        } catch (const GgufError&) { end_bounds_failed = true; }
        check(end_bounds_failed, "GGUF region ending past tensor rejected");
        bool region_overflow_failed = false;
        try {
            (void)materialize_gguf_tensor_rows(
                *embedding, 1, std::numeric_limits<uint64_t>::max(), ignored_bytes);
        } catch (const GgufError&) { region_overflow_failed = true; }
        check(region_overflow_failed, "GGUF region extent overflow rejected");
        std::filesystem::resize_file(short_path,
            static_cast<uint64_t>(embedding->backing.byte_offset()) + row_bytes - 1);
        bool short_failed = false;
        try {
            uint64_t bytes = 0;
            (void)materialize_gguf_tensor_rows(*embedding, 0, 1, bytes);
        } catch (const GgufError&) {
            short_failed = true;
        }
        check(short_failed, "short GGUF region source fails closed");
        std::filesystem::remove(short_path);

        return failures == 0 ? 0 : 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 2;
    }
}
