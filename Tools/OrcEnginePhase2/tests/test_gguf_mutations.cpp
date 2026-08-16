// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <iterator>
#include <string>
#include <vector>

#include "orcengine/gguf.hpp"

using namespace orcengine;

namespace {

uint64_t next_random(uint64_t& state) {
    state ^= state << 13;
    state ^= state >> 7;
    state ^= state << 17;
    return state;
}

void write_bytes(const std::filesystem::path& path, const std::vector<uint8_t>& bytes) {
    std::ofstream out(path, std::ios::binary | std::ios::trunc);
    if (!out) throw std::runtime_error("cannot write mutation fixture");
    out.write(reinterpret_cast<const char*>(bytes.data()), static_cast<std::streamsize>(bytes.size()));
    if (!out) throw std::runtime_error("short mutation-fixture write");
}

}  // namespace

int main(int argc, char** argv) {
    try {
        if (argc != 2) throw std::runtime_error("usage: test_gguf_mutations VALID_GGUF");
        const std::filesystem::path source = argv[1];
        std::ifstream in(source, std::ios::binary);
        if (!in) throw std::runtime_error("cannot read valid mutation seed");
        const std::vector<uint8_t> original((std::istreambuf_iterator<char>(in)),
                                            std::istreambuf_iterator<char>());
        if (original.empty()) throw std::runtime_error("mutation seed is empty");

        const std::filesystem::path mutation_path =
            std::filesystem::temp_directory_path() / "orcengine_phase2_mutation.gguf";
        uint64_t state = 0x4f7263456e67696eULL;
        size_t accepted = 0;
        size_t rejected = 0;
        constexpr size_t kMutations = 512;
        for (size_t iteration = 0; iteration < kMutations; ++iteration) {
            std::vector<uint8_t> bytes = original;
            const size_t changes = 1 + static_cast<size_t>(next_random(state) % 4);
            for (size_t change = 0; change < changes; ++change) {
                const size_t position = static_cast<size_t>(next_random(state) % bytes.size());
                bytes[position] ^= static_cast<uint8_t>(1U << (next_random(state) % 8));
            }
            write_bytes(mutation_path, bytes);
            try {
                const GgufArtifact artifact = index_gguf(mutation_path);
                (void)map_llama_model(artifact);
                ++accepted;
            } catch (const std::exception&) {
                ++rejected;
            }
        }
        std::error_code ignored;
        std::filesystem::remove(mutation_path, ignored);
        std::printf("GGUF MUTATION PASS: %zu deterministic mutations, %zu rejected cleanly, %zu remained valid\n",
                    kMutations, rejected, accepted);
        return 0;
    } catch (const std::exception& ex) {
        std::fprintf(stderr, "[FAIL] %s\n", ex.what());
        return 1;
    }
}
