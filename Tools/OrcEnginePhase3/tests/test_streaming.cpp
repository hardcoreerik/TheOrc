// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <cstdio>
#include <filesystem>
#include <limits>
#include <stdexcept>
#include <string>

#include "orcengine/fixture_loader.hpp"
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
}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 3) throw std::runtime_error("usage: test_streaming FIXTURES PHASE1_FIXTURES");
        const std::filesystem::path fixtures = argv[1];
        const LoadedFixture expected = load_fixture(
            (std::filesystem::path(argv[2]) / "fixture_tied.txt").string());

        const ModelArtifactManifest untied_manifest = map_llama_model(index_gguf(fixtures / "model_untied.gguf"));
        const ForwardResult full = forward(materialize_gguf_model(untied_manifest), expected.token_ids);
        StreamingModel streamed(untied_manifest);
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

        StreamingModel reversed(untied_manifest);
        const ForwardResult reverse = reversed.forward(expected.token_ids, {true, -1});
        check(identical(normal, reverse), "in-layer materialization order metamorphic");

        const uint64_t repeated_before = streamed.telemetry().repeated_backing_bytes_read;
        const ForwardResult rematerialized = streamed.forward(expected.token_ids);
        check(identical(normal, rematerialized) &&
              streamed.telemetry().repeated_backing_bytes_read > repeated_before,
              "same backing extents rematerialize identically and count repeated reads");

        const ModelArtifactManifest tied_manifest = map_llama_model(index_gguf(fixtures / "model_tied.gguf"));
        StreamingModel tied(tied_manifest);
        check(identical(full, tied.forward(expected.token_ids)),
              "real tied semantic path equals explicit output path");

        ModelArtifactManifest missing = untied_manifest;
        const auto erase_it = std::find_if(missing.mapped_tensors.begin(), missing.mapped_tensors.end(),
            [](const MappedGgufTensor& tensor) {
                return tensor.semantic.layer == 0 && tensor.semantic.role == SemanticTensorRole::AttentionQuery;
            });
        missing.mapped_tensors.erase(erase_it);
        bool missing_failed = false;
        try { StreamingModel model(missing); (void)model.forward(expected.token_ids); }
        catch (const GgufError&) { missing_failed = true; }
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

        StreamingModel injected(untied_manifest);
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
        StreamingModel truncated_model(truncated_manifest);
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
