// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <algorithm>
#include <bit>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

#include "orcengine/fixture_loader.hpp"
#include "orcengine/forward.hpp"

using namespace orcengine;

namespace {
void print_bits(const std::vector<float>& values) {
    for (float value : values) std::printf(" %08x", std::bit_cast<uint32_t>(value));
    std::printf("\n");
}

void snapshot(const char* label, const LoadedFixture& fixture,
              const std::vector<int64_t>& tokens) {
    const ForwardResult result = forward(fixture.model, tokens);
    std::vector<std::string> names;
    names.reserve(result.taps.size());
    for (const auto& [name, _] : result.taps) names.push_back(name);
    std::sort(names.begin(), names.end());
    for (const std::string& name : names) {
        std::printf("%s tap %s", label, name.c_str());
        print_bits(result.taps.at(name).data);
    }
    std::printf("%s logits", label);
    print_bits(result.logits);
    std::printf("%s selected", label);
    for (int64_t token : result.selected_token)
        std::printf(" %lld", static_cast<long long>(token));
    std::printf("\n");
}
}  // namespace

int main(int argc, char** argv) {
    try {
        const std::string root = argc > 1 ? argv[1] : "fixtures_phase1";
        const LoadedFixture tied = load_fixture(root + "/fixture_tied.txt");
        const LoadedFixture untied = load_fixture(root + "/fixture_untied.txt");
        const LoadedFixture decode = load_fixture(root + "/fixture_decode_weights.txt");
        snapshot("tied", tied, tied.token_ids);
        snapshot("untied", untied, untied.token_ids);
        std::vector<int64_t> tokens = decode.token_ids;
        for (int step = 0; step < 8; ++step) {
            const std::string label = "decode" + std::to_string(step);
            const ForwardResult result = forward(decode.model, tokens);
            snapshot(label.c_str(), decode, tokens);
            tokens.push_back(result.selected_token.back());
        }
        std::printf("decode sequence");
        for (int64_t token : tokens) std::printf(" %lld", static_cast<long long>(token));
        std::printf("\n");
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "%s\n", ex.what());
        return 1;
    }
}
