// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <limits>
#include <stdexcept>
#include <string>
#include <unordered_set>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/gguf.hpp"
#include "orcengine/gguf_source.hpp"
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
    TensorRowRegionMaterializer rows = [](const LogicalTensor& logical,
                                          const BackingExtent& backing,
                                          const TensorRowRegion& region) {
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

struct WeirdLayoutFaults {
    int64_t corrupt_padding_row = -1;
    int64_t corrupt_data_row = -1;
    int64_t missing_row = -1;
    int64_t duplicate_row = -1;
};

uint32_t row_checksum(const uint8_t* bytes, size_t count) {
    uint32_t value = 2166136261U;
    for (size_t i = 0; i < count; ++i) value = (value ^ bytes[i]) * 16777619U;
    return value;
}

void put_u32(uint8_t* destination, uint32_t value) {
    destination[0] = static_cast<uint8_t>(value);
    destination[1] = static_cast<uint8_t>(value >> 8);
    destination[2] = static_cast<uint8_t>(value >> 16);
    destination[3] = static_cast<uint8_t>(value >> 24);
}

uint32_t get_u32(const uint8_t* source) {
    return static_cast<uint32_t>(source[0]) |
           (static_cast<uint32_t>(source[1]) << 8) |
           (static_cast<uint32_t>(source[2]) << 16) |
           (static_cast<uint32_t>(source[3]) << 24);
}

ModelSourceBinding bind_weird_layout_model(const Model& model,
                                           const std::filesystem::path& path,
                                           WeirdLayoutFaults faults = {}) {
    ModelSourceBinding binding = bind_memory_model(model);
    const uint64_t rows = static_cast<uint64_t>(model.config().vocab);
    const uint64_t columns = static_cast<uint64_t>(model.config().hidden);
    const uint64_t row_bytes = columns * sizeof(float);
    constexpr uint64_t header_bytes = 19;
    constexpr uint64_t prefix_bytes = 7;
    constexpr uint64_t suffix_bytes = 9;
    constexpr uint64_t footer_bytes = 13;
    const uint64_t stride = prefix_bytes + row_bytes + suffix_bytes;
    std::vector<uint8_t> file(static_cast<size_t>(
        header_bytes + rows * stride + footer_bytes), 0xA5);
    const auto slot_for = [rows](uint64_t logical_row) {
        return (logical_row * 5) % rows;
    };
    for (uint64_t logical_row = 0; logical_row < rows; ++logical_row) {
        const uint64_t base = header_bytes + slot_for(logical_row) * stride;
        put_u32(file.data() + base, static_cast<uint32_t>(logical_row));
        file[static_cast<size_t>(base + 4)] = 0xD1;
        file[static_cast<size_t>(base + 5)] = 0xD2;
        file[static_cast<size_t>(base + 6)] = 0xD3;
        const uint64_t data = base + prefix_bytes;
        std::memcpy(file.data() + data,
                    model.token_embedding.raw().data() + logical_row * columns,
                    static_cast<size_t>(row_bytes));
        put_u32(file.data() + data + row_bytes,
                row_checksum(file.data() + data, static_cast<size_t>(row_bytes)));
        for (uint64_t i = 4; i < suffix_bytes; ++i) {
            file[static_cast<size_t>(data + row_bytes + i)] =
                static_cast<uint8_t>(0xE0 + i);
        }
    }
    const auto row_base = [&](int64_t logical_row) {
        return header_bytes + slot_for(static_cast<uint64_t>(logical_row)) * stride;
    };
    if (faults.corrupt_padding_row >= 0) {
        file[static_cast<size_t>(row_base(faults.corrupt_padding_row) + 4)] ^= 0xFF;
    }
    if (faults.corrupt_data_row >= 0) {
        file[static_cast<size_t>(row_base(faults.corrupt_data_row) + prefix_bytes)] ^= 0x01;
    }
    if (faults.missing_row >= 0) {
        put_u32(file.data() + row_base(faults.missing_row), 0xFFFFFFFFU);
    }
    if (faults.duplicate_row >= 0) {
        put_u32(file.data() + row_base(faults.duplicate_row), 0U);
    }
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(reinterpret_cast<const char*>(file.data()),
                 static_cast<std::streamsize>(file.size()));
    if (!output) throw std::runtime_error("cannot create weird-layout backing");
    output.close();

    auto embedding = std::find_if(
        binding.source.tensors.begin(), binding.source.tensors.end(),
        [](const SourceTensor& tensor) {
            return tensor.identity.role == TensorRole::TokenEmbedding;
        });
    embedding->backing = BackingExtent(
        path.string(), 0, static_cast<int64_t>(file.size()), BackingEncoding::F32Raw);
    embedding->backing_identity = "weird-layout:" + path.string();
    binding.materialize_rows = [path, rows, columns, row_bytes, stride, slot_for](
        const LogicalTensor& logical, const BackingExtent& backing,
        const TensorRowRegion& region) {
        if (logical.shape().dims() !=
                std::vector<int64_t>{static_cast<int64_t>(rows),
                                     static_cast<int64_t>(columns)} ||
            backing.source_path() != path.string() || region.row_count == 0 ||
            region.row_begin >= rows || region.row_count > rows - region.row_begin) {
            throw std::runtime_error("invalid weird-layout logical row request");
        }
        std::ifstream input(path, std::ios::binary);
        if (!input) throw std::runtime_error("cannot open weird-layout backing");
        std::vector<float> values(static_cast<size_t>(region.row_count * columns));
        std::vector<uint8_t> slot(static_cast<size_t>(stride));
        uint64_t bytes_read = 0;
        for (uint64_t i = 0; i < region.row_count; ++i) {
            const uint64_t logical_row = region.row_begin + i;
            const uint64_t offset = header_bytes + slot_for(logical_row) * stride;
            input.seekg(static_cast<std::streamoff>(offset));
            input.read(reinterpret_cast<char*>(slot.data()),
                       static_cast<std::streamsize>(slot.size()));
            if (!input) throw std::runtime_error("short weird-layout physical row");
            if (get_u32(slot.data()) != logical_row || slot[4] != 0xD1 ||
                slot[5] != 0xD2 || slot[6] != 0xD3) {
                throw std::runtime_error("weird-layout row directory or padding is corrupt");
            }
            const uint8_t* data = slot.data() + prefix_bytes;
            if (get_u32(data + row_bytes) !=
                    row_checksum(data, static_cast<size_t>(row_bytes))) {
                throw std::runtime_error("weird-layout row checksum is corrupt");
            }
            for (uint64_t marker = 4; marker < suffix_bytes; ++marker) {
                if (data[row_bytes + marker] != static_cast<uint8_t>(0xE0 + marker)) {
                    throw std::runtime_error("weird-layout suffix padding is corrupt");
                }
            }
            std::memcpy(values.data() + i * columns, data,
                        static_cast<size_t>(row_bytes));
            bytes_read += stride;
        }
        return MaterializedRegion{
            ResidentView(TensorShape({static_cast<int64_t>(region.row_count),
                                      static_cast<int64_t>(columns)}),
                         std::move(values)),
            bytes_read};
    };
    return binding;
}

StreamingModel virtual_model(const Model& model, uint64_t chunk_rows,
                             uint64_t budget = std::numeric_limits<uint64_t>::max(),
                             ExecutionObserver observer = {}) {
    ModelSourceBinding binding = bind_memory_model(model);
    StreamingConfig config;
    config.residency_budget_bytes = budget;
    config.observer = std::move(observer);
    config.virtualize_bookends = true;
    config.row_region_materializer = std::move(binding.materialize_rows);
    config.output_chunk_rows = chunk_rows;
    return StreamingModel(std::move(binding.source), std::move(binding.materialize),
                          std::move(config));
}

bool partition_rejected(uint64_t rows, std::vector<TensorRowRegion> regions) {
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

        const std::filesystem::path weird_path = fixtures / "phase4_weird_layout.bin";
        std::vector<ExecutionEvent> weird_events;
        ModelSourceBinding weird_binding = bind_weird_layout_model(
            fixture.model, weird_path);
        StreamingConfig weird_config;
        weird_config.virtualize_bookends = true;
        weird_config.output_chunk_rows = 7;
        weird_config.observer = [&](const ExecutionEvent& event) {
            weird_events.push_back(event);
        };
        weird_config.row_region_materializer =
            std::move(weird_binding.materialize_rows);
        StreamingModel weird(std::move(weird_binding.source),
                             std::move(weird_binding.materialize),
                             std::move(weird_config));
        const ForwardResult weird_result = weird.forward(fixture.token_ids);
        const uint64_t weird_row_bytes =
            static_cast<uint64_t>(fixture.model.config().hidden) * sizeof(float);
        const uint64_t weird_stride = weird_row_bytes + 16;
        const uint64_t weird_unique_tokens = static_cast<uint64_t>(
            std::unordered_set<int64_t>(fixture.token_ids.begin(),
                                        fixture.token_ids.end()).size());
        check(identical(reference, weird_result),
              "padded and physically permuted backing executes bit-identically");
        check(weird.telemetry().embedding_backing_bytes_read ==
                  weird_unique_tokens * weird_stride &&
              weird.telemetry().output_backing_bytes_read ==
                  static_cast<uint64_t>(fixture.model.config().vocab) * weird_stride,
              "weird layout accounts physical bytes rather than logical resident bytes");
        check(std::any_of(weird_events.begin(), weird_events.end(),
            [](const ExecutionEvent& event) {
                return event.kind == ExecutionEventKind::TensorRowRegionMaterialized &&
                       event.backing_bytes_read > event.tensor_bytes;
            }), "observer distinguishes logical row bytes from physical bytes read");
        std::filesystem::remove(weird_path);

        const auto weird_fault_rejected = [&](const char* filename,
                                              WeirdLayoutFaults faults,
                                              bool truncate) {
            const std::filesystem::path path = fixtures / filename;
            ModelSourceBinding binding = bind_weird_layout_model(
                fixture.model, path, faults);
            if (truncate) {
                std::filesystem::resize_file(path,
                    std::filesystem::file_size(path) - weird_stride);
            }
            StreamingConfig config;
            config.virtualize_bookends = true;
            config.output_chunk_rows = 7;
            config.row_region_materializer = std::move(binding.materialize_rows);
            bool rejected = false;
            try {
                StreamingModel model(std::move(binding.source),
                                     std::move(binding.materialize),
                                     std::move(config));
                (void)model.forward(fixture.token_ids);
            } catch (const std::exception&) {
                rejected = true;
            }
            std::filesystem::remove(path);
            return rejected;
        };
        check(weird_fault_rejected("phase4_weird_padding.bin", {1, -1, -1, -1}, false),
              "physical padding corruption fails closed");
        check(weird_fault_rejected("phase4_weird_data.bin", {-1, 1, -1, -1}, false),
              "corrupted physical row fails checksum closed");
        check(weird_fault_rejected("phase4_weird_missing.bin", {-1, -1, 1, -1}, false),
              "missing logical row fails closed");
        check(weird_fault_rejected("phase4_weird_duplicate.bin", {-1, -1, -1, 1}, false),
              "duplicate logical row fails closed");
        check(weird_fault_rejected("phase4_weird_short.bin", {}, true),
              "short weird-layout source fails closed");

        const ModelArtifactManifest f16_manifest = map_llama_model(
            index_gguf(fixtures / "model_f16.gguf"));
        const auto f16_embedding = std::find_if(
            f16_manifest.mapped_tensors.begin(), f16_manifest.mapped_tensors.end(),
            [](const MappedGgufTensor& tensor) {
                return tensor.semantic.role == SemanticTensorRole::TokenEmbedding;
            });
        const ResidentView f16_full = materialize_gguf_tensor(*f16_embedding);
        bool f16_regions_exact = true;
        for (const TensorRowRegion region : {
                 TensorRowRegion{0, 1}, TensorRowRegion{1, 1},
                 TensorRowRegion{15, 1},
                 TensorRowRegion{3, 5}, TensorRowRegion{9, 4},
                 TensorRowRegion{29, 3}}) {
            uint64_t bytes_read = 0;
            const ResidentView rows = materialize_gguf_tensor_rows(
                *f16_embedding, region.row_begin, region.row_count, bytes_read);
            const size_t columns = static_cast<size_t>(
                f16_embedding->logical.shape().dim(1));
            const auto expected_begin = f16_full.raw().begin() +
                static_cast<size_t>(region.row_begin) * columns;
            f16_regions_exact = f16_regions_exact &&
                rows.shape().dims() == std::vector<int64_t>{
                    static_cast<int64_t>(region.row_count),
                    static_cast<int64_t>(columns)} &&
                rows.raw() == std::vector<float>(
                    expected_begin,
                    expected_begin + static_cast<size_t>(region.row_count) * columns) &&
                bytes_read == region.row_count * columns * 2 &&
                rows.raw().size() * sizeof(float) ==
                    region.row_count * columns * sizeof(float);
        }
        check(f16_regions_exact,
              "F16 first, middle, multi-row, nonzero, and final regions decode exactly");

        const ForwardResult f16_reference = forward(
            materialize_gguf_model(f16_manifest), fixture.token_ids);
        ModelSourceBinding f16_binding = bind_gguf_source(f16_manifest);
        StreamingConfig f16_config;
        f16_config.virtualize_bookends = true;
        f16_config.output_chunk_rows = 7;
        f16_config.row_region_materializer = std::move(f16_binding.materialize_rows);
        StreamingModel f16_model(std::move(f16_binding.source),
                                 std::move(f16_binding.materialize),
                                 std::move(f16_config));
        check(identical(f16_reference, f16_model.forward(fixture.token_ids)),
              "F16 row-region execution equals full F16-to-F32 execution");
        const uint64_t f16_peak = f16_model.telemetry().peak_resident_weight_bytes;
        ModelSourceBinding f16_exact_binding = bind_gguf_source(f16_manifest);
        StreamingConfig f16_exact_config;
        f16_exact_config.residency_budget_bytes = f16_peak;
        f16_exact_config.virtualize_bookends = true;
        f16_exact_config.output_chunk_rows = 7;
        f16_exact_config.row_region_materializer =
            std::move(f16_exact_binding.materialize_rows);
        StreamingModel f16_exact(std::move(f16_exact_binding.source),
                                 std::move(f16_exact_binding.materialize),
                                 std::move(f16_exact_config));
        check(identical(f16_reference, f16_exact.forward(fixture.token_ids)),
              "F16 row-region execution succeeds at its exact resident budget");
        bool f16_below_failed = false;
        try {
            ModelSourceBinding below_binding = bind_gguf_source(f16_manifest);
            StreamingConfig below_config;
            below_config.residency_budget_bytes = f16_peak - 1;
            below_config.virtualize_bookends = true;
            below_config.output_chunk_rows = 7;
            below_config.row_region_materializer =
                std::move(below_binding.materialize_rows);
            StreamingModel below(std::move(below_binding.source),
                                 std::move(below_binding.materialize),
                                 std::move(below_config));
            (void)below.forward(fixture.token_ids);
        } catch (const ResidencyBudgetError&) {
            f16_below_failed = true;
        }
        check(f16_below_failed, "F16 row-region peak minus one fails closed");

        MappedGgufTensor odd_f16 = *f16_embedding;
        odd_f16.backing = BackingExtent(
            odd_f16.backing.source_path(), odd_f16.backing.byte_offset(),
            odd_f16.backing.byte_length() - 1, odd_f16.backing.encoding());
        bool odd_f16_failed = false;
        try {
            uint64_t bytes = 0;
            (void)materialize_gguf_tensor_rows(
                odd_f16, static_cast<uint64_t>(odd_f16.logical.shape().dim(0) - 1),
                1, bytes);
        } catch (const GgufError&) {
            odd_f16_failed = true;
        }
        check(odd_f16_failed, "odd F16 encoded extent fails closed");
        bool f16_bounds_failed = false;
        try {
            uint64_t bytes = 0;
            (void)materialize_gguf_tensor_rows(
                *f16_embedding,
                static_cast<uint64_t>(f16_embedding->logical.shape().dim(0)),
                1, bytes);
        } catch (const GgufError&) {
            f16_bounds_failed = true;
        }
        check(f16_bounds_failed, "out-of-range F16 row request fails closed");

        const std::filesystem::path short_f16_path =
            fixtures / "phase4_short_f16.gguf";
        std::filesystem::copy_file(fixtures / "model_f16.gguf", short_f16_path,
                                   std::filesystem::copy_options::overwrite_existing);
        ModelArtifactManifest short_f16_manifest = map_llama_model(
            index_gguf(short_f16_path));
        const auto short_f16_embedding = std::find_if(
            short_f16_manifest.mapped_tensors.begin(),
            short_f16_manifest.mapped_tensors.end(),
            [](const MappedGgufTensor& tensor) {
                return tensor.semantic.role == SemanticTensorRole::TokenEmbedding;
            });
        std::filesystem::resize_file(
            short_f16_path,
            static_cast<uint64_t>(short_f16_embedding->backing.byte_offset()) +
                static_cast<uint64_t>(short_f16_embedding->logical.shape().dim(1)) * 2 - 1);
        bool short_f16_failed = false;
        try {
            uint64_t bytes = 0;
            (void)materialize_gguf_tensor_rows(*short_f16_embedding, 0, 1, bytes);
        } catch (const GgufError&) {
            short_f16_failed = true;
        }
        check(short_f16_failed, "truncated F16 physical row fails closed");
        std::filesystem::remove(short_f16_path);

        StreamingModel measured = virtual_model(fixture.model, 7);
        const ForwardResult measured_result = measured.forward(fixture.token_ids);
        const StreamingTelemetry telemetry = measured.telemetry();
        const size_t unique_tokens = std::unordered_set<int64_t>(
            fixture.token_ids.begin(), fixture.token_ids.end()).size();
        check(identical(reference, measured_result) &&
              telemetry.embedding_row_region_count == unique_tokens,
              "embedding materializes one row per unique token");
        check(telemetry.output_row_region_count ==
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
                return event.kind == ExecutionEventKind::TensorRowRegionMaterialized &&
                       event.row_count > 0 && event.tensor_bytes > 0 &&
                       event.operation != ExecutionOperation::None &&
                       event.milliseconds >= 0.0;
            });
        check(static_cast<uint64_t>(region_events) ==
                  observed.telemetry().row_region_materialization_count,
              "observer reports measured role, region, bytes, operation, and timing");
        StreamingModel throwing_row_observer = virtual_model(
            fixture.model, 7, std::numeric_limits<uint64_t>::max(),
            [](const ExecutionEvent&) {
                throw std::runtime_error("row observer failure");
            });
        check(identical(reference, throwing_row_observer.forward(fixture.token_ids)) &&
              throwing_row_observer.telemetry().observer_failure_count == 1,
              "throwing row-region observer is isolated from inference");

        // The check above throws on the FIRST emitted event of any kind
        // (ModelExecutionBegin, before any row-region event fires), so it
        // never actually exercises a throw occurring DURING row-region event
        // handling despite its name. This test closes that gap: the observer
        // stays silent until it specifically sees a TensorRowRegionMaterialized
        // event, throws only there, and we verify every claim explicitly --
        // the region event was reached, the observer failed there (not
        // earlier/later), the failure was counted exactly once, the observer
        // was disabled per the existing generic isolation policy, inference
        // still completed, and output remained bit-identical.
        bool reached_row_region_event = false;
        bool threw_on_row_region_event = false;
        StreamingModel throwing_on_row_region = virtual_model(
            fixture.model, 7, std::numeric_limits<uint64_t>::max(),
            [&](const ExecutionEvent& event) {
                if (event.kind == ExecutionEventKind::TensorRowRegionMaterialized) {
                    reached_row_region_event = true;
                    threw_on_row_region_event = true;
                    throw std::runtime_error("row-region-specific observer failure");
                }
            });
        const ForwardResult row_region_throw_result =
            throwing_on_row_region.forward(fixture.token_ids);
        check(reached_row_region_event,
              "row-region-specific observer reached a TensorRowRegionMaterialized event");
        check(threw_on_row_region_event,
              "row-region-specific observer threw specifically during region event handling");
        check(throwing_on_row_region.telemetry().observer_failure_count == 1,
              "row-region-specific observer failure was counted exactly once");
        check(identical(reference, row_region_throw_result),
              "inference remained bit-identical after a row-region observer failure");

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
        wrong_config.row_region_materializer = [](const LogicalTensor&,
                                                  const BackingExtent&,
                                                  const TensorRowRegion&) {
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

        const auto wrong_row_count_rejected = [&](bool too_many) {
            ModelSourceBinding binding = bind_memory_model(fixture.model);
            TensorRowRegionMaterializer good = std::move(binding.materialize_rows);
            StreamingConfig config;
            config.virtualize_bookends = true;
            config.output_chunk_rows = 7;
            config.row_region_materializer =
                [good = std::move(good), too_many](const LogicalTensor& logical,
                                                    const BackingExtent& backing,
                                                    const TensorRowRegion& region) {
                    if (!too_many && region.row_count == 1) {
                        return good(logical, backing, region);
                    }
                    const uint64_t returned_rows = too_many
                        ? region.row_count + 1 : region.row_count - 1;
                    const uint64_t columns =
                        static_cast<uint64_t>(logical.shape().dim(1));
                    return MaterializedRegion{
                        ResidentView(TensorShape({static_cast<int64_t>(returned_rows),
                                                  static_cast<int64_t>(columns)}),
                                     std::vector<float>(static_cast<size_t>(
                                         returned_rows * columns))),
                        returned_rows * columns * sizeof(float)};
                };
            try {
                StreamingModel model(std::move(binding.source),
                                     std::move(binding.materialize),
                                     std::move(config));
                (void)model.forward(fixture.token_ids);
                return false;
            } catch (const StreamingError&) {
                return true;
            }
        };
        check(wrong_row_count_rejected(true),
              "materializer returning too many rows fails closed");
        check(wrong_row_count_rejected(false),
              "materializer returning too few rows fails closed");

        ModelSourceBinding bogus_bytes = bind_memory_model(fixture.model);
        StreamingConfig bogus_bytes_config;
        bogus_bytes_config.virtualize_bookends = true;
        bogus_bytes_config.output_chunk_rows = 7;
        bogus_bytes_config.row_region_materializer = [](
            const LogicalTensor& logical, const BackingExtent& backing,
            const TensorRowRegion& region) {
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
            config.row_region_materializer =
                std::move(malformed_shape.materialize_rows);
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
