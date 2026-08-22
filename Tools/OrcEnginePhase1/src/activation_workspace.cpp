// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
#include "orcengine/activation_workspace.hpp"

#include <limits>

namespace orcengine {

namespace {
int64_t checked_mul(int64_t a, int64_t b, const char* what) {
    if (a < 0 || b < 0) throw ActivationWorkspaceError(std::string(what) + ": negative dimension");
    if (a != 0 && b > std::numeric_limits<int64_t>::max() / a) {
        throw ActivationWorkspaceError(std::string(what) + " overflowed int64_t");
    }
    return a * b;
}
uint64_t checked_add_u64(uint64_t a, uint64_t b, const char* what) {
    if (a > std::numeric_limits<uint64_t>::max() - b) {
        throw ActivationWorkspaceError(std::string(what) + " overflowed uint64_t");
    }
    return a + b;
}
}  // namespace

ActivationWorkspace::ActivationWorkspace(int64_t hidden, int64_t intermediate, int64_t q_dim, int64_t kv_dim,
                                         int64_t vocab, int64_t max_tokens_per_step)
    : hidden_(hidden), intermediate_(intermediate), q_dim_(q_dim), kv_dim_(kv_dim), vocab_(vocab),
      max_tokens_per_step_(max_tokens_per_step) {
    if (hidden_ <= 0 || intermediate_ <= 0 || q_dim_ <= 0 || kv_dim_ <= 0 || vocab_ <= 0 ||
        max_tokens_per_step_ <= 0) {
        throw ActivationWorkspaceError("all dimensions must be positive");
    }

    const int64_t hidden_elems = checked_mul(max_tokens_per_step_, hidden_, "hidden buffer element count");
    const int64_t q_elems = checked_mul(max_tokens_per_step_, q_dim_, "q_proj buffer element count");
    const int64_t kv_elems = checked_mul(max_tokens_per_step_, kv_dim_, "kv_proj buffer element count");
    const int64_t intermediate_elems =
        checked_mul(max_tokens_per_step_, intermediate_, "intermediate buffer element count");
    const int64_t vocab_elems = checked_mul(max_tokens_per_step_, vocab_, "logits buffer element count");

    // Every buffer allocated exactly ONCE, here, at construction. Nothing
    // after this constructor ever calls resize/reserve/push_back on any
    // of these vectors -- accessor methods only ever hand out sub-spans.
    norm_out_.assign(static_cast<size_t>(hidden_elems), 0.0f);
    q_proj_.assign(static_cast<size_t>(q_elems), 0.0f);
    k_proj_.assign(static_cast<size_t>(kv_elems), 0.0f);
    v_proj_.assign(static_cast<size_t>(kv_elems), 0.0f);
    attn_out_.assign(static_cast<size_t>(hidden_elems), 0.0f);
    gate_proj_.assign(static_cast<size_t>(intermediate_elems), 0.0f);
    up_proj_.assign(static_cast<size_t>(intermediate_elems), 0.0f);
    gate_activated_.assign(static_cast<size_t>(intermediate_elems), 0.0f);
    ffn_out_.assign(static_cast<size_t>(hidden_elems), 0.0f);
    logits_.assign(static_cast<size_t>(vocab_elems), 0.0f);

    uint64_t bytes = 0;
    for (size_t n : {norm_out_.size(), q_proj_.size(), k_proj_.size(), v_proj_.size(), attn_out_.size(),
                     gate_proj_.size(), up_proj_.size(), gate_activated_.size(), ffn_out_.size(), logits_.size()}) {
        bytes = checked_add_u64(bytes, static_cast<uint64_t>(n) * sizeof(float), "capacity");
    }
    capacity_bytes_ = bytes;
}

LayerActivationBuffers ActivationWorkspace::layer_buffers(int64_t tokens) {
    if (tokens <= 0 || tokens > max_tokens_per_step_) {
        throw ActivationWorkspaceError("layer_buffers: tokens (" + std::to_string(tokens) +
                                       ") outside (0, max_tokens_per_step_ (" +
                                       std::to_string(max_tokens_per_step_) + ")]");
    }

    const size_t hidden_n = static_cast<size_t>(tokens * hidden_);
    const size_t q_n = static_cast<size_t>(tokens * q_dim_);
    const size_t kv_n = static_cast<size_t>(tokens * kv_dim_);
    const size_t intermediate_n = static_cast<size_t>(tokens * intermediate_);

    LayerActivationBuffers out;
    out.norm_out = std::span<float>(norm_out_.data(), hidden_n);
    out.q_proj = std::span<float>(q_proj_.data(), q_n);
    out.k_proj = std::span<float>(k_proj_.data(), kv_n);
    out.v_proj = std::span<float>(v_proj_.data(), kv_n);
    out.attn_out = std::span<float>(attn_out_.data(), hidden_n);
    out.gate_proj = std::span<float>(gate_proj_.data(), intermediate_n);
    out.up_proj = std::span<float>(up_proj_.data(), intermediate_n);
    out.gate_activated = std::span<float>(gate_activated_.data(), intermediate_n);
    out.ffn_out = std::span<float>(ffn_out_.data(), hidden_n);

    uint64_t used = 0;
    used = checked_add_u64(used, static_cast<uint64_t>(hidden_n) * sizeof(float), "current usage");   // norm_out
    used = checked_add_u64(used, static_cast<uint64_t>(q_n) * sizeof(float), "current usage");
    used = checked_add_u64(used, static_cast<uint64_t>(kv_n) * sizeof(float) * 2, "current usage");    // k+v
    used = checked_add_u64(used, static_cast<uint64_t>(hidden_n) * sizeof(float), "current usage");    // attn_out
    used = checked_add_u64(used, static_cast<uint64_t>(intermediate_n) * sizeof(float) * 3, "current usage");  // gate+up+activated
    used = checked_add_u64(used, static_cast<uint64_t>(hidden_n) * sizeof(float), "current usage");    // ffn_out
    record_usage(used);
    return out;
}

std::span<float> ActivationWorkspace::final_norm_buffer(int64_t tokens) {
    if (tokens <= 0 || tokens > max_tokens_per_step_) {
        throw ActivationWorkspaceError("final_norm_buffer: tokens (" + std::to_string(tokens) +
                                       ") outside (0, max_tokens_per_step_ (" +
                                       std::to_string(max_tokens_per_step_) + ")]");
    }
    const size_t hidden_n = static_cast<size_t>(tokens * hidden_);
    record_usage(static_cast<uint64_t>(hidden_n) * sizeof(float));
    return std::span<float>(norm_out_.data(), hidden_n);
}

std::span<float> ActivationWorkspace::logits_buffer(int64_t tokens) {
    if (tokens <= 0 || tokens > max_tokens_per_step_) {
        throw ActivationWorkspaceError("logits_buffer: tokens (" + std::to_string(tokens) +
                                       ") outside (0, max_tokens_per_step_ (" +
                                       std::to_string(max_tokens_per_step_) + ")]");
    }
    const size_t vocab_n = static_cast<size_t>(tokens * vocab_);
    record_usage(static_cast<uint64_t>(vocab_n) * sizeof(float));
    return std::span<float>(logits_.data(), vocab_n);
}

void ActivationWorkspace::record_usage(uint64_t bytes) {
    current_bytes_ = bytes;
    if (current_bytes_ > peak_bytes_) peak_bytes_ = current_bytes_;
    if (total_prepare_calls_ > 0) ++reuse_count_;
    ++total_prepare_calls_;
}

}  // namespace orcengine
