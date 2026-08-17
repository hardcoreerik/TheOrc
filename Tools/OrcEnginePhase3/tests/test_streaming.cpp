// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <limits>
#include <stdexcept>
#include <string>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/gguf_source.hpp"
#include "orcengine/materialization.hpp"
#include "orcengine/streaming.hpp"
#include "orcengine/validation.hpp"

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
        if (it == right.taps.end() || tap.dims != it->second.dims || tap.data != it->second.data)
            return false;
    }
    return true;
}

StreamingModel stream_gguf(ModelArtifactManifest manifest,
                           StreamingConfig config = {}) {
    ModelSourceBinding binding = bind_gguf_source(std::move(manifest));
    return StreamingModel(std::move(binding.source), std::move(binding.materialize),
                          std::move(config));
}

ModelSourceBinding bind_memory_model(const Model& model) {
    ModelSource source;
    source.config = model.config();
    source.tied_embeddings = model.manifest.tied_embeddings;
    auto add = [&](TensorRole role, int64_t layer, const char* name,
                   const ResidentView& view) {
        const uint64_t bytes = static_cast<uint64_t>(view.raw().size()) * sizeof(float);
        source.tensors.push_back({
            {role, layer}, LogicalTensor(name, view.shape()),
            BackingExtent::FromF32(view.raw()), bytes,
            std::string("memory:") + name,
        });
    };
    add(TensorRole::TokenEmbedding, -1, "token_embedding", model.token_embedding);
    add(TensorRole::FinalNorm, -1, "final_norm", model.final_norm_weight);
    if (model.lm_head) add(TensorRole::OutputHead, -1, "output_head", *model.lm_head);
    for (int64_t layer = 0; layer < model.config().n_layers; ++layer) {
        const LayerWeights& weights = model.layers.at(static_cast<size_t>(layer));
        const std::string prefix = "layer" + std::to_string(layer) + ".";
        add(TensorRole::AttentionNorm, layer, (prefix + "attention_norm").c_str(), weights.attn_norm_weight);
        add(TensorRole::AttentionQuery, layer, (prefix + "attention_query").c_str(), weights.w_q);
        add(TensorRole::AttentionKey, layer, (prefix + "attention_key").c_str(), weights.w_k);
        add(TensorRole::AttentionValue, layer, (prefix + "attention_value").c_str(), weights.w_v);
        add(TensorRole::AttentionOutput, layer, (prefix + "attention_output").c_str(), weights.w_o);
        add(TensorRole::FfnNorm, layer, (prefix + "ffn_norm").c_str(), weights.ffn_norm_weight);
        add(TensorRole::FfnGate, layer, (prefix + "ffn_gate").c_str(), weights.w_gate);
        add(TensorRole::FfnUp, layer, (prefix + "ffn_up").c_str(), weights.w_up);
        add(TensorRole::FfnDown, layer, (prefix + "ffn_down").c_str(), weights.w_down);
    }
    return {std::move(source), [](const LogicalTensor& logical, const BackingExtent& backing) {
        return materialize(logical, backing);
    }};
}
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 3) throw std::runtime_error("usage: test_streaming FIXTURES PHASE1_FIXTURES");
        const std::filesystem::path fixtures = argv[1];
        const LoadedFixture expected = load_fixture(
            (std::filesystem::path(argv[2]) / "fixture_tied.txt").string());

        const ModelArtifactManifest untied_manifest = map_llama_model(index_gguf(fixtures / "model_untied.gguf"));
        const ForwardResult full = forward(materialize_gguf_model(untied_manifest), expected.token_ids);
        StreamingModel streamed = stream_gguf(untied_manifest);
        const uint64_t bookends = streamed.telemetry().current_resident_weight_bytes;
        const ForwardResult normal = streamed.forward(expected.token_ids);
        check(identical(full, normal), "full resident vs streamed is bit-identical at every tap");
        check(streamed.telemetry().peak_active_layers == 1 &&
              streamed.telemetry().current_layer == -1 &&
              streamed.telemetry().current_resident_weight_bytes == bookends,
              "one-layer lifetime and release boundary");
        check(streamed.telemetry().peak_resident_weight_bytes <
              streamed.telemetry().cumulative_materialized_bytes,
              "peak resident weights are below cumulative materialization");

        StreamingModel reversed = stream_gguf(untied_manifest);
        const ForwardResult reverse = reversed.forward(expected.token_ids, {true, -1});
        check(identical(normal, reverse), "in-layer materialization order metamorphic");

        const uint64_t repeated_before = streamed.telemetry().repeated_backing_bytes_read;
        const ForwardResult rematerialized = streamed.forward(expected.token_ids);
        check(identical(normal, rematerialized) &&
              streamed.telemetry().repeated_backing_bytes_read > repeated_before,
              "same backing extents rematerialize identically and count repeated reads");

        const ModelArtifactManifest tied_manifest = map_llama_model(index_gguf(fixtures / "model_tied.gguf"));
        StreamingModel tied = stream_gguf(tied_manifest);
        check(identical(full, tied.forward(expected.token_ids)),
              "real tied semantic path equals explicit output path");

        ModelSourceBinding memory = bind_memory_model(expected.model);
        const ModelSource memory_source = memory.source;
        StreamingModel format_neutral(std::move(memory.source), std::move(memory.materialize));
        const ForwardResult neutral_result = format_neutral.forward(expected.token_ids);
        check(identical(forward(expected.model, expected.token_ids), neutral_result),
              "format-neutral in-memory source executes without GGUF adapter");

        const uint64_t neutral_peak = format_neutral.telemetry().peak_resident_weight_bytes;
        const uint64_t neutral_bookends = format_neutral.telemetry().current_resident_weight_bytes;
        bool full_budget_failed = false;
        try { require_full_resident_budget(memory_source, neutral_peak); }
        catch (const ResidencyBudgetError&) { full_budget_failed = true; }
        check(full_budget_failed && full_resident_bytes(memory_source) > neutral_peak,
              "full resident admission fails under streamed peak budget");

        ModelSourceBinding exact_binding = bind_memory_model(expected.model);
        StreamingModel exact_budget(std::move(exact_binding.source), std::move(exact_binding.materialize),
                                    {neutral_peak, {}});
        check(identical(neutral_result, exact_budget.forward(expected.token_ids)) &&
              exact_budget.telemetry().peak_resident_weight_bytes == neutral_peak,
              "streaming succeeds exactly at required peak budget");

        bool below_peak_failed = false;
        ModelSourceBinding below_binding = bind_memory_model(expected.model);
        StreamingModel below_peak(std::move(below_binding.source), std::move(below_binding.materialize),
                                  {neutral_peak - 1, {}});
        try { (void)below_peak.forward(expected.token_ids); }
        catch (const ResidencyBudgetError&) { below_peak_failed = true; }
        check(below_peak_failed &&
              below_peak.telemetry().current_resident_weight_bytes == neutral_bookends,
              "one byte below streamed peak fails closed and releases partial layer");

        ModelSourceBinding above_binding = bind_memory_model(expected.model);
        StreamingModel above_peak(std::move(above_binding.source), std::move(above_binding.materialize),
                                  {neutral_peak + 1, {}});
        check(identical(neutral_result, above_peak.forward(expected.token_ids)),
              "slightly above streamed peak succeeds");

        bool below_bookends_failed = false;
        try {
            ModelSourceBinding binding = bind_memory_model(expected.model);
            StreamingModel model(std::move(binding.source), std::move(binding.materialize),
                                 {neutral_bookends - 1, {}});
        } catch (const ResidencyBudgetError&) { below_bookends_failed = true; }
        check(below_bookends_failed, "budget below permanent bookends fails during admission");

        std::vector<ExecutionEvent> events;
        ModelSourceBinding observed_binding = bind_memory_model(expected.model);
        StreamingModel observed(std::move(observed_binding.source), std::move(observed_binding.materialize),
            {std::numeric_limits<uint64_t>::max(),
             [&](const ExecutionEvent& event) { events.push_back(event); }});
        const ForwardResult observed_result = observed.forward(expected.token_ids);
        const auto has_event = [&](ExecutionEventKind kind) {
            return std::any_of(events.begin(), events.end(),
                [&](const ExecutionEvent& event) { return event.kind == kind; });
        };
        check(identical(neutral_result, observed_result) &&
              has_event(ExecutionEventKind::ModelExecutionBegin) &&
              has_event(ExecutionEventKind::TensorMaterialized) &&
              has_event(ExecutionEventKind::LayerExecutionBegin) &&
              has_event(ExecutionEventKind::TensorReleased) &&
              has_event(ExecutionEventKind::TokenSelected) &&
              has_event(ExecutionEventKind::ModelExecutionEnd),
              "observer enabled is output-identical and receives structured events");
        check(std::all_of(events.begin(), events.end(), [](const ExecutionEvent& event) {
                  return event.semantics == EvidenceSemantics::Measured;
              }), "observer events distinguish measured evidence semantics");

        ModelSourceBinding throwing_binding = bind_memory_model(expected.model);
        StreamingModel throwing_observer(std::move(throwing_binding.source),
            std::move(throwing_binding.materialize),
            {std::numeric_limits<uint64_t>::max(),
             [](const ExecutionEvent&) { throw std::runtime_error("observer failure"); }});
        check(identical(neutral_result, throwing_observer.forward(expected.token_ids)) &&
              throwing_observer.telemetry().observer_failure_count == 1,
              "throwing observer is disabled and cannot change inference");

        bool oversized_materialization_failed = false;
        try {
            ModelSourceBinding lying = bind_memory_model(expected.model);
            lying.source.tensors.front().resident_bytes -= sizeof(float);
            StreamingModel model(std::move(lying.source), std::move(lying.materialize));
        } catch (const StreamingError&) { oversized_materialization_failed = true; }
        check(oversized_materialization_failed,
              "materializer output larger than declared residency fails closed");

        ModelArtifactManifest missing = untied_manifest;
        const auto erase_it = std::find_if(missing.mapped_tensors.begin(), missing.mapped_tensors.end(),
            [](const MappedGgufTensor& tensor) {
                return tensor.semantic.layer == 0 && tensor.semantic.role == SemanticTensorRole::AttentionQuery;
            });
        missing.mapped_tensors.erase(erase_it);
        bool missing_failed = false;
        try { StreamingModel model = stream_gguf(missing); (void)model.forward(expected.token_ids); }
        catch (const StreamingError&) { missing_failed = true; }
        check(missing_failed, "missing layer tensor fails closed");

        bool invalid_resident_failed = false;
        Model validation_bookends = materialize_gguf_model(untied_manifest);
        try {
            (void)forward_with_layer_runner(
                untied_manifest.config, false,
                validation_bookends.token_embedding,
                &validation_bookends.effective_lm_head(),
                validation_bookends.final_norm_weight,
                expected.token_ids,
                [](int64_t, const LayerConsumer& consume) { consume(LayerWeights{}); });
        } catch (const ValidationError&) { invalid_resident_failed = true; }
        check(invalid_resident_failed, "invalid current-layer residents fail before execution");

        StreamingModel injected = stream_gguf(untied_manifest);
        bool injection_failed = false;
        try { (void)injected.forward(expected.token_ids, {false, 0}); }
        catch (const std::runtime_error&) { injection_failed = true; }
        check(injection_failed && injected.telemetry().current_layer == -1 &&
              injected.telemetry().current_resident_weight_bytes ==
                  untied_manifest.config.vocab * untied_manifest.config.hidden * sizeof(float) * 2 +
                  untied_manifest.config.hidden * sizeof(float),
              "simulated post-release failure leaves clean accounting");

        ResidencyLedger ledger;
        ledger.materialized("max", std::numeric_limits<uint64_t>::max(), 0);
        bool overflow_failed = false;
        try { ledger.materialized("overflow", 1, 0); }
        catch (const std::overflow_error&) { overflow_failed = true; }
        check(overflow_failed, "accounting overflow fails closed");
        ledger.released(std::numeric_limits<uint64_t>::max(), 1);
        bool duplicate_failed = false;
        try { ledger.released(0, 1); }
        catch (const std::logic_error&) { duplicate_failed = true; }
        check(duplicate_failed, "duplicate release fails closed");

        const std::filesystem::path truncated = fixtures / "streaming_truncated_copy.gguf";
        std::filesystem::copy_file(fixtures / "model_untied.gguf", truncated,
                                   std::filesystem::copy_options::overwrite_existing);
        ModelArtifactManifest truncated_manifest = map_llama_model(index_gguf(truncated));
        StreamingModel truncated_model = stream_gguf(truncated_manifest);
        uint64_t truncate_at = std::numeric_limits<uint64_t>::max();
        for (const auto& tensor : truncated_manifest.mapped_tensors) {
            if (tensor.semantic.layer == 1)
                truncate_at = std::min(truncate_at, static_cast<uint64_t>(tensor.backing.byte_offset()));
        }
        std::filesystem::resize_file(truncated, truncate_at);
        bool short_read_failed = false;
        try { (void)truncated_model.forward(expected.token_ids); }
        catch (const GgufError&) { short_read_failed = true; }
        check(short_read_failed && truncated_model.telemetry().current_layer == -1,
              "truncated backing after prior success fails mid-model cleanly");
        std::filesystem::remove(truncated);

        return failures == 0 ? 0 : 1;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 2;
    }
}
