// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/ops.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>
#include <string>

namespace orcengine::ops {

std::vector<float> rmsnorm(const std::vector<float>& x, int64_t rows, int64_t cols,
                            const std::vector<float>& weight, float epsilon) {
    std::vector<float> out(static_cast<size_t>(rows * cols));
    for (int64_t r = 0; r < rows; ++r) {
        AccumT sum_sq = AccumT(0);
        for (int64_t c = 0; c < cols; ++c) {
            float v = x[static_cast<size_t>(r * cols + c)];
            sum_sq += static_cast<AccumT>(v) * static_cast<AccumT>(v);
        }
        float mean_sq = static_cast<float>(sum_sq / static_cast<AccumT>(cols));
        float inv_rms = 1.0f / std::sqrt(mean_sq + epsilon);
        for (int64_t c = 0; c < cols; ++c) {
            size_t idx = static_cast<size_t>(r * cols + c);
            out[idx] = x[idx] * inv_rms * weight[static_cast<size_t>(c)];
        }
    }
    return out;
}

std::vector<float> silu(const std::vector<float>& x) {
    std::vector<float> out(x.size());
    for (size_t i = 0; i < x.size(); ++i) {
        float v = x[i];
        out[i] = v * (1.0f / (1.0f + std::exp(-v)));
    }
    return out;
}

std::vector<float> softmax_last_axis(const std::vector<float>& x, int64_t rows, int64_t cols) {
    std::vector<float> out(static_cast<size_t>(rows * cols));
    for (int64_t r = 0; r < rows; ++r) {
        float max_v = -std::numeric_limits<float>::infinity();
        for (int64_t c = 0; c < cols; ++c) {
            max_v = std::max(max_v, x[static_cast<size_t>(r * cols + c)]);
        }
        AccumT sum_exp = AccumT(0);
        std::vector<float> exp_row(static_cast<size_t>(cols));
        for (int64_t c = 0; c < cols; ++c) {
            float e = std::exp(x[static_cast<size_t>(r * cols + c)] - max_v);
            exp_row[static_cast<size_t>(c)] = e;
            sum_exp += static_cast<AccumT>(e);
        }
        for (int64_t c = 0; c < cols; ++c) {
            out[static_cast<size_t>(r * cols + c)] = static_cast<float>(static_cast<AccumT>(exp_row[static_cast<size_t>(c)]) / sum_exp);
        }
    }
    return out;
}

std::vector<float> causal_mask(const std::vector<float>& scores, int64_t q_len, int64_t k_len) {
    std::vector<float> out = scores;
    const float neg_inf = -std::numeric_limits<float>::infinity();
    for (int64_t q = 0; q < q_len; ++q) {
        for (int64_t k = 0; k < k_len; ++k) {
            if (k > q) {
                out[static_cast<size_t>(q * k_len + k)] = neg_inf;
            }
        }
    }
    return out;
}

void rope_cos_sin(int64_t position, int64_t head_dim, double theta,
                   std::vector<float>& cos_out, std::vector<float>& sin_out) {
    int64_t half = head_dim / 2;
    std::vector<float> cos_half(static_cast<size_t>(half));
    std::vector<float> sin_half(static_cast<size_t>(half));
    for (int64_t i = 0; i < half; ++i) {
        double freq = std::pow(theta, -2.0 * static_cast<double>(i) / static_cast<double>(head_dim));
        double angle = static_cast<double>(position) * freq;
        cos_half[static_cast<size_t>(i)] = static_cast<float>(std::cos(angle));
        sin_half[static_cast<size_t>(i)] = static_cast<float>(std::sin(angle));
    }
    cos_out.assign(static_cast<size_t>(head_dim), 0.0f);
    sin_out.assign(static_cast<size_t>(head_dim), 0.0f);
    for (int64_t i = 0; i < half; ++i) {
        cos_out[static_cast<size_t>(i)] = cos_half[static_cast<size_t>(i)];
        cos_out[static_cast<size_t>(half + i)] = cos_half[static_cast<size_t>(i)];
        sin_out[static_cast<size_t>(i)] = sin_half[static_cast<size_t>(i)];
        sin_out[static_cast<size_t>(half + i)] = sin_half[static_cast<size_t>(i)];
    }
}

std::vector<float> rope_rotate_half(const std::vector<float>& x) {
    int64_t head_dim = static_cast<int64_t>(x.size());
    int64_t half = head_dim / 2;
    std::vector<float> out(static_cast<size_t>(head_dim));
    for (int64_t i = 0; i < half; ++i) {
        out[static_cast<size_t>(i)] = -x[static_cast<size_t>(half + i)];
        out[static_cast<size_t>(half + i)] = x[static_cast<size_t>(i)];
    }
    return out;
}

std::vector<float> apply_rope(const std::vector<float>& x, const std::vector<float>& cos,
                               const std::vector<float>& sin) {
    std::vector<float> rotated = rope_rotate_half(x);
    std::vector<float> out(x.size());
    for (size_t i = 0; i < x.size(); ++i) {
        out[i] = x[i] * cos[i] + rotated[i] * sin[i];
    }
    return out;
}

std::vector<float> linear_no_bias(const std::vector<float>& x, int64_t rows, int64_t in_features,
                                   const std::vector<float>& weight_out_in, int64_t out_features) {
    std::vector<float> out(static_cast<size_t>(rows * out_features), 0.0f);
    for (int64_t r = 0; r < rows; ++r) {
        for (int64_t o = 0; o < out_features; ++o) {
            AccumT acc = AccumT(0);
            for (int64_t i = 0; i < in_features; ++i) {
                acc += static_cast<AccumT>(x[static_cast<size_t>(r * in_features + i)]) *
                       static_cast<AccumT>(weight_out_in[static_cast<size_t>(o * in_features + i)]);
            }
            out[static_cast<size_t>(r * out_features + o)] = static_cast<float>(acc);
        }
    }
    return out;
}

std::vector<float> embedding_lookup(const std::vector<float>& table, int64_t hidden,
                                     const std::vector<int64_t>& token_ids) {
    if (hidden <= 0) {
        throw std::invalid_argument("orcengine::ops::embedding_lookup: hidden must be positive");
    }
    if (table.size() % static_cast<size_t>(hidden) != 0) {
        throw std::invalid_argument("orcengine::ops::embedding_lookup: table.size() is not a multiple of hidden");
    }
    // Defensive bounds check: an out-of-range token ID (negative, or >=
    // the vocab this table was sized for) would otherwise index past the
    // end of `table` -- an out-of-bounds heap read, not merely a wrong
    // answer. This is the only place that check can live for every
    // caller of this function; upstream validation (e.g.
    // validate_forward_inputs for the non-cached forward() path) is not
    // guaranteed to run before every call site (the cached-decode path
    // does not currently call it -- see forward_cached.cpp's own added
    // check for that gap specifically).
    const int64_t vocab = static_cast<int64_t>(table.size() / static_cast<size_t>(hidden));
    for (int64_t token : token_ids) {
        if (token < 0 || token >= vocab) {
            throw std::invalid_argument("orcengine::ops::embedding_lookup: token ID " + std::to_string(token) +
                                        " is outside [0, " + std::to_string(vocab) + ")");
        }
    }
    std::vector<float> out(token_ids.size() * static_cast<size_t>(hidden));
    for (size_t s = 0; s < token_ids.size(); ++s) {
        int64_t token = token_ids[s];
        for (int64_t h = 0; h < hidden; ++h) {
            out[s * static_cast<size_t>(hidden) + static_cast<size_t>(h)] =
                table[static_cast<size_t>(token) * static_cast<size_t>(hidden) + static_cast<size_t>(h)];
        }
    }
    return out;
}

int64_t argmax(const std::vector<float>& x) {
    if (x.empty()) throw std::invalid_argument("argmax: empty input");
    size_t best = 0;
    for (size_t i = 1; i < x.size(); ++i) {
        if (x[i] > x[best]) best = i;
    }
    return static_cast<int64_t>(best);
}

}  // namespace orcengine::ops
