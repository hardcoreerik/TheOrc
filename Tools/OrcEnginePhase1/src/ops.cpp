// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/ops.hpp"

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>
#include <string>

namespace orcengine::ops {

namespace {
// Local overflow-checked helper, matching the convention every other
// phase in this project already uses (a small local checked_mul rather
// than a shared cross-phase utility) -- see e.g. sticky_layer_plan.cpp's
// own checked_add for the identical pattern.
int64_t checked_mul_i64(int64_t a, int64_t b, const char* what) {
    if (a != 0 && b > std::numeric_limits<int64_t>::max() / a) {
        throw std::overflow_error(std::string("orcengine::ops: ") + what + " overflowed int64_t");
    }
    return a * b;
}
}  // namespace

void rmsnorm_into(std::span<const float> x, int64_t rows, int64_t cols,
                  std::span<const float> weight, float epsilon, std::span<float> out) {
    const int64_t expected = checked_mul_i64(rows, cols, "rmsnorm element count");
    if (static_cast<int64_t>(out.size()) != expected) {
        throw std::invalid_argument("orcengine::ops::rmsnorm_into: out.size() does not match rows*cols");
    }
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
}

std::vector<float> rmsnorm(const std::vector<float>& x, int64_t rows, int64_t cols,
                            const std::vector<float>& weight, float epsilon) {
    std::vector<float> out(static_cast<size_t>(checked_mul_i64(rows, cols, "rmsnorm element count")));
    rmsnorm_into(x, rows, cols, weight, epsilon, out);
    return out;
}

void silu_into(std::span<const float> x, std::span<float> out) {
    if (out.size() != x.size()) {
        throw std::invalid_argument("orcengine::ops::silu_into: out.size() does not match x.size()");
    }
    for (size_t i = 0; i < x.size(); ++i) {
        float v = x[i];
        out[i] = v * (1.0f / (1.0f + std::exp(-v)));
    }
}

std::vector<float> silu(const std::vector<float>& x) {
    std::vector<float> out(x.size());
    silu_into(x, out);
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

void linear_no_bias_into(std::span<const float> x, int64_t rows, int64_t in_features,
                         std::span<const float> weight_out_in, int64_t out_features, std::span<float> out) {
    const int64_t expected = checked_mul_i64(rows, out_features, "linear_no_bias element count");
    if (static_cast<int64_t>(out.size()) != expected) {
        throw std::invalid_argument("orcengine::ops::linear_no_bias_into: out.size() does not match rows*out_features");
    }
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
}

std::vector<float> linear_no_bias(const std::vector<float>& x, int64_t rows, int64_t in_features,
                                   const std::vector<float>& weight_out_in, int64_t out_features) {
    std::vector<float> out(static_cast<size_t>(checked_mul_i64(rows, out_features, "linear_no_bias element count")),
                           0.0f);
    linear_no_bias_into(x, rows, in_features, weight_out_in, out_features, out);
    return out;
}

std::vector<float> embedding_lookup(const std::vector<float>& table, int64_t hidden,
                                     const std::vector<int64_t>& token_ids) {
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
