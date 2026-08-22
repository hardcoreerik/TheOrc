// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Phase 5C: bounded, reusable CPU F32 activation-workspace buffers for the
// real cached-decode transformer-layer execution flow. A DISTINCT
// accounted quantity from weight residency (ResidencyLedger) and KV-cache
// residency (ContiguousAttentionKVStore) -- never conflated with either.
//
// Owns exactly the named buffers the real per-layer cached-decode flow
// (Tools/OrcEnginePhase5A/src/forward_cached.cpp's
// execute_cached_transformer_layer) actually allocates fresh on every
// call for its LARGEST, most numerous intermediates: the two RMSNorm
// outputs (attn_norm and ffn_norm -- sequential, never concurrently live,
// so they share one buffer), the Q/K/V projections, the attention output
// projection, the FFN gate/up projections, the SiLU-activated gate, and
// the FFN down-projection output. Deliberately does NOT cover every
// intermediate in that function (RoPE's small per-head-dim temporaries,
// the manual attention-context accumulation, softmax's variable-length
// per-position scratch) -- only the operations Phase 1's new `_into`
// overloads (ops.hpp) actually cover, per the authorizing instruction's
// "only add output-buffer forms for operations actually needed... do not
// mechanically add overloads for every operation." This is a real,
// honestly-scoped subset, not a claim of eliminating every allocation in
// the per-layer path.
//
// EXPERIMENTAL, Phase 5C research scope. Not a general memory-pool
// framework: nine concretely-named `std::vector<float>` buffers, sized
// once at construction for a fixed model configuration and a fixed
// maximum tokens-per-step, never resized afterward. No global singleton
// -- each context (e.g. each decode driver instance) owns its own
// `ActivationWorkspace`, so separate contexts never share mutable state.
#pragma once

#include <cstdint>
#include <span>
#include <stdexcept>
#include <string>
#include <vector>

namespace orcengine {

class ActivationWorkspaceError : public std::runtime_error {
public:
    explicit ActivationWorkspaceError(const std::string& message)
        : std::runtime_error("OrcEngine activation workspace error: " + message) {}
};

// One transformer layer's worth of reusable scratch, sized for exactly
// `tokens` rows against the workspace's fixed per-row dimensions. Spans
// alias the workspace's own permanently-owned storage -- they are valid
// only for the lifetime of (and until the next `layer_buffers()` call on)
// the `ActivationWorkspace` that produced them, exactly like any other
// non-owning view.
struct LayerActivationBuffers {
    std::span<float> norm_out;        // [tokens, hidden] -- shared slot: attn_norm output, then (after being consumed) ffn_norm output
    std::span<float> q_proj;          // [tokens, q_dim]
    std::span<float> k_proj;          // [tokens, kv_dim]
    std::span<float> v_proj;          // [tokens, kv_dim]
    std::span<float> attn_out;        // [tokens, hidden]
    std::span<float> gate_proj;       // [tokens, intermediate]
    std::span<float> up_proj;         // [tokens, intermediate]
    std::span<float> gate_activated;  // [tokens, intermediate] -- silu(gate_proj)
    std::span<float> ffn_out;         // [tokens, hidden]
};

class ActivationWorkspace {
public:
    // Prepares capacity for a fixed model configuration and a fixed
    // maximum tokens-per-step. Every buffer is allocated ONCE here, sized
    // for `max_tokens_per_step` rows each (overflow-checked); no buffer is
    // ever resized or reallocated afterward -- layer_buffers()/
    // final_norm_buffer()/logits_buffer() only ever return sub-spans of
    // this same fixed storage. Throws ActivationWorkspaceError on any
    // non-positive dimension or on overflow.
    ActivationWorkspace(int64_t hidden, int64_t intermediate, int64_t q_dim, int64_t kv_dim, int64_t vocab,
                        int64_t max_tokens_per_step);

    // Returns spans sized for exactly `tokens` rows (0 < tokens <=
    // max_tokens_per_step, checked -- throws before returning anything if
    // violated). Every call reuses the SAME underlying storage; no
    // allocation occurs here. Increments the reuse counter on every call
    // after the first.
    LayerActivationBuffers layer_buffers(int64_t tokens);

    // Step-level (not per-layer) buffers, computed once per step AFTER
    // every layer has run. `final_norm_buffer` reuses the SAME storage as
    // `layer_buffers()`'s own `norm_out` span (the last layer's own
    // attn_norm/ffn_norm usage is finished by the time final-norm runs,
    // so this is a third, still-sequential-never-concurrent use of that
    // one buffer, not a new allocation). `logits_buffer` is a distinct
    // buffer, sized [tokens, vocab]. Both are validated and counted the
    // same way as layer_buffers().
    std::span<float> final_norm_buffer(int64_t tokens);
    std::span<float> logits_buffer(int64_t tokens);

    int64_t hidden() const { return hidden_; }
    int64_t intermediate() const { return intermediate_; }
    int64_t q_dim() const { return q_dim_; }
    int64_t kv_dim() const { return kv_dim_; }
    int64_t vocab() const { return vocab_; }
    int64_t max_tokens_per_step() const { return max_tokens_per_step_; }

    // Fixed forever after construction -- the total bytes this workspace
    // owns (every named buffer's storage, summed), regardless of how many
    // rows any individual accessor call actually requested. All storage
    // is allocated once, at construction, and never freed while the
    // workspace lives -- so this is also, always, the workspace's actual
    // resident footprint; nothing in this class ever returns memory to
    // the allocator early to appear smaller between calls.
    uint64_t capacity_bytes() const { return capacity_bytes_; }
    // Bytes requested by the MOST RECENT single accessor call --
    // layer_buffers(tokens) (summed across its nine buffers),
    // final_norm_buffer(tokens), or logits_buffer(tokens) alone --
    // whichever was called last. This is NOT a running total across
    // multiple buffer kinds requested in sequence during one step (e.g.
    // it does not add layer_buffers()'s footprint to a subsequent
    // logits_buffer() call's footprint) -- it answers "how large was the
    // working set this most recent call touched," not "how much is
    // simultaneously live across the whole step." Always <=
    // capacity_bytes(); reported this way, precisely, rather than left
    // ambiguous.
    uint64_t current_bytes() const { return current_bytes_; }
    // The largest current_bytes() value ever observed across every
    // accessor call this workspace has served -- in practice, always a
    // layer_buffers() call, since that request touches nine buffers at
    // once while final_norm_buffer()/logits_buffer() each touch one.
    uint64_t peak_bytes() const { return peak_bytes_; }
    // Number of accessor calls (layer_buffers/final_norm_buffer/
    // logits_buffer, combined) that reused already-prepared storage --
    // every call after the very first across ALL THREE accessors (the
    // first call, whichever accessor it was, is "preparation," not
    // "reuse").
    uint64_t reuse_count() const { return reuse_count_; }
    // Total accessor calls served (layer_buffers/final_norm_buffer/
    // logits_buffer, combined), including the first.
    uint64_t total_prepare_calls() const { return total_prepare_calls_; }

private:
    void record_usage(uint64_t bytes);

    int64_t hidden_, intermediate_, q_dim_, kv_dim_, vocab_, max_tokens_per_step_;
    std::vector<float> norm_out_, q_proj_, k_proj_, v_proj_, attn_out_, gate_proj_, up_proj_, gate_activated_,
        ffn_out_, logits_;
    uint64_t capacity_bytes_ = 0;
    uint64_t current_bytes_ = 0;
    uint64_t peak_bytes_ = 0;
    uint64_t reuse_count_ = 0;
    uint64_t total_prepare_calls_ = 0;
};

}  // namespace orcengine
