// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#pragma once

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <fstream>
#include <string>
#include <vector>

#include "orcengine/validation.hpp"

namespace decode_test_support {

inline constexpr float kAbsTol = 1e-3f;
inline constexpr float kRelTol = 1e-3f;

struct DecodeStep {
    std::vector<int64_t> seq_before;
    std::vector<float> logits_last;
    int64_t selected = 0;
};

struct Reader {
    std::ifstream in;
    explicit Reader(const std::string& path) : in(path) {
        if (!in) throw orcengine::ValidationError("cannot open decode trace '" + path + "'");
    }
    std::string tok() {
        std::string value;
        if (!(in >> value)) throw orcengine::ValidationError("unexpected end of decode trace");
        return value;
    }
    int64_t i() { return std::stoll(tok()); }
    float f() { return std::stof(tok()); }
};

inline void require(bool condition, const std::string& message) {
    if (!condition) throw orcengine::ValidationError(message);
}

inline std::vector<DecodeStep> load_decode_trace(const std::string& path) {
    Reader r(path);
    require(r.tok() == "STEPS", "decode trace must begin with STEPS");
    const int64_t n_steps = r.i();
    require(n_steps > 0, "decode STEPS must be > 0");
    require(n_steps <= 1'000'000, "decode STEPS is unreasonably large");

    std::vector<DecodeStep> steps(static_cast<size_t>(n_steps));
    for (int64_t s = 0; s < n_steps; ++s) {
        require(r.tok() == "STEP", "expected STEP record");
        require(r.i() == s, "decode STEP index is not sequential");
        require(r.tok() == "SEQ_BEFORE", "expected SEQ_BEFORE record");
        const int64_t seq_len = r.i();
        require(seq_len > 0, "decode sequence length must be > 0");
        steps[static_cast<size_t>(s)].seq_before.resize(static_cast<size_t>(seq_len));
        for (int64_t i = 0; i < seq_len; ++i) {
            steps[static_cast<size_t>(s)].seq_before[static_cast<size_t>(i)] = r.i();
        }
        require(r.tok() == "LOGITS_LAST", "expected LOGITS_LAST record");
        const int64_t vocab = r.i();
        require(vocab > 0, "decode logits count must be > 0");
        steps[static_cast<size_t>(s)].logits_last.resize(static_cast<size_t>(vocab));
        for (int64_t i = 0; i < vocab; ++i) {
            steps[static_cast<size_t>(s)].logits_last[static_cast<size_t>(i)] = r.f();
        }
        require(r.tok() == "SELECTED", "expected SELECTED record");
        steps[static_cast<size_t>(s)].selected = r.i();
    }

    std::string trailing;
    require(!(r.in >> trailing), "decode trace contains data after declared STEPS entries");
    return steps;
}

inline void validate_decode_trace(const std::vector<DecodeStep>& steps,
                                  const std::vector<int64_t>& initial_tokens,
                                  int64_t vocab, int64_t max_positions) {
    require(!steps.empty(), "decode STEPS must be > 0");
    require(!initial_tokens.empty(), "decode initial sequence must not be empty");
    require(static_cast<int64_t>(initial_tokens.size() + steps.size()) <= max_positions,
            "decode trace exceeds max_positions");

    std::vector<int64_t> expected_sequence = initial_tokens;
    for (size_t i = 0; i < steps.size(); ++i) {
        const DecodeStep& step = steps[i];
        require(step.seq_before.size() == initial_tokens.size() + i,
                "decode sequence length does not equal initial_length + step");
        require(step.seq_before == expected_sequence,
                "decode sequence does not contain the prior selected token");
        require(static_cast<int64_t>(step.logits_last.size()) == vocab,
                "decode logits count does not equal vocab");
        for (float value : step.logits_last) {
            require(std::isfinite(value), "decode expected logits contain NaN or Inf");
        }
        require(step.selected >= 0 && step.selected < vocab,
                "decode selected token is outside [0, vocab)");
        expected_sequence.push_back(step.selected);
    }
}

struct DecodeComparison {
    float max_abs = 0.0f;
    float max_rel = 0.0f;
    bool token_matches = false;
    bool logits_match = false;
    bool finite = true;
    bool pass() const { return finite && token_matches && logits_match; }
};

inline DecodeComparison compare_decode_step(const std::vector<float>& actual,
                                             int64_t actual_selected,
                                             const DecodeStep& expected) {
    require(actual.size() == expected.logits_last.size(),
            "decode actual and expected logits have different sizes");
    DecodeComparison result;
    result.token_matches = actual_selected == expected.selected;
    for (size_t i = 0; i < actual.size(); ++i) {
        const float a = actual[i];
        const float e = expected.logits_last[i];
        if (!std::isfinite(a) || !std::isfinite(e)) {
            result.finite = false;
            continue;
        }
        const float abs_error = std::fabs(a - e);
        result.max_abs = std::max(result.max_abs, abs_error);
        result.max_rel = std::max(result.max_rel, abs_error / std::max(1.0f, std::fabs(e)));
    }
    result.logits_match = result.finite && result.max_abs <= kAbsTol && result.max_rel <= kRelTol;
    return result;
}

}  // namespace decode_test_support
