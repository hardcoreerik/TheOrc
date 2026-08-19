// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/cache_trace_loader.hpp"

#include <fstream>
#include <stdexcept>

namespace orcengine {

namespace {

struct Reader {
    std::ifstream in;
    explicit Reader(const std::string& path) : in(path) {
        if (!in) throw std::runtime_error("cache_trace_loader: cannot open " + path);
    }
    std::string tok() {
        std::string t;
        if (!(in >> t)) throw std::runtime_error("cache_trace_loader: unexpected end of file");
        return t;
    }
    int64_t i() { return std::stoll(tok()); }
    float f() { return std::stof(tok()); }
    bool at_eof() { return in.peek() == EOF; }
};

}  // namespace

std::vector<CacheTraceStep> load_cache_trace(const std::string& path) {
    Reader r(path);
    std::string tag = r.tok();
    if (tag != "STEPS") throw std::runtime_error("cache_trace_loader: expected STEPS");
    int64_t n_steps = r.i();
    std::vector<CacheTraceStep> steps(static_cast<size_t>(n_steps));

    for (int64_t s = 0; s < n_steps; ++s) {
        CacheTraceStep& step = steps[static_cast<size_t>(s)];
        tag = r.tok();
        if (tag != "STEP") throw std::runtime_error("cache_trace_loader: expected STEP");
        r.i();  // index, implied by position
        step.kind = r.tok();

        tag = r.tok();
        if (tag != "SEQ_BEFORE") throw std::runtime_error("cache_trace_loader: expected SEQ_BEFORE");
        int64_t seq_len = r.i();
        step.seq_before.resize(static_cast<size_t>(seq_len));
        for (int64_t k = 0; k < seq_len; ++k) step.seq_before[static_cast<size_t>(k)] = r.i();

        tag = r.tok();
        if (tag != "NEW_TOKENS") throw std::runtime_error("cache_trace_loader: expected NEW_TOKENS");
        int64_t new_len = r.i();
        step.new_tokens.resize(static_cast<size_t>(new_len));
        for (int64_t k = 0; k < new_len; ++k) step.new_tokens[static_cast<size_t>(k)] = r.i();

        tag = r.tok();
        if (tag != "START_POSITION") throw std::runtime_error("cache_trace_loader: expected START_POSITION");
        step.start_position = r.i();

        tag = r.tok();
        if (tag != "LOGITS_LAST") throw std::runtime_error("cache_trace_loader: expected LOGITS_LAST");
        int64_t vocab = r.i();
        step.logits_last.resize(static_cast<size_t>(vocab));
        for (int64_t k = 0; k < vocab; ++k) step.logits_last[static_cast<size_t>(k)] = r.f();

        tag = r.tok();
        if (tag != "SELECTED") throw std::runtime_error("cache_trace_loader: expected SELECTED");
        step.selected = r.i();

        // Remaining CACHE_K/CACHE_V pairs belong to this step until the next STEP or EOF.
        while (true) {
            std::streampos before = r.in.tellg();
            std::string marker;
            if (!(r.in >> marker)) break;  // genuine EOF (possibly after trailing whitespace)
            if (marker == "STEP") {
                r.in.seekg(before);
                break;
            }
            if (marker != "CACHE_K") throw std::runtime_error("cache_trace_loader: expected CACHE_K");
            int64_t layer_idx = r.i();
            (void)layer_idx;
            CacheLayerSnapshot snap;
            int64_t k_ndim = r.i();
            snap.k_dims.resize(static_cast<size_t>(k_ndim));
            int64_t k_count = 1;
            for (int64_t d = 0; d < k_ndim; ++d) {
                snap.k_dims[static_cast<size_t>(d)] = r.i();
                k_count *= snap.k_dims[static_cast<size_t>(d)];
            }
            snap.k.resize(static_cast<size_t>(k_count));
            for (int64_t k = 0; k < k_count; ++k) snap.k[static_cast<size_t>(k)] = r.f();

            marker = r.tok();
            if (marker != "CACHE_V") throw std::runtime_error("cache_trace_loader: expected CACHE_V");
            r.i();  // layer_idx again
            int64_t v_ndim = r.i();
            snap.v_dims.resize(static_cast<size_t>(v_ndim));
            int64_t v_count = 1;
            for (int64_t d = 0; d < v_ndim; ++d) {
                snap.v_dims[static_cast<size_t>(d)] = r.i();
                v_count *= snap.v_dims[static_cast<size_t>(d)];
            }
            snap.v.resize(static_cast<size_t>(v_count));
            for (int64_t k = 0; k < v_count; ++k) snap.v[static_cast<size_t>(k)] = r.f();

            step.cache_after.push_back(std::move(snap));
        }
    }
    return steps;
}

}  // namespace orcengine
