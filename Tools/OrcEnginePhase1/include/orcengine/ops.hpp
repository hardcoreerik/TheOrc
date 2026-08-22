// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
//
// Scalar F32 CPU operators. Deliberately unfused, unoptimized, and written
// to mirror Tools/OrcEnginePhase0/oracle/ops.py line-for-line so a reviewer
// can check them against the Python oracle without a mental translation
// step. No SIMD, no BLAS, no fusion -- Phase 1's whole point is a boring,
// auditable reference, not a fast one (see docs/OrcEngine/ENGINEERING_ROADMAP.md
// Phase 4 for where optimization is allowed to start).
//
// All matrices below use the same "weight_out_in" convention as the Python
// oracle: a Linear layer's weight is stored [out_features, in_features],
// and linear_no_bias computes x @ W^T.
#pragma once

#include <cstdint>
#include <span>
#include <vector>

namespace orcengine::ops {

// Reduction accumulator type for rmsnorm/softmax/matmul/attention dot
// products. Phase 1's spec default is F32 storage/compute/accumulate; see
// docs/OrcEngine/PHASE1_IMPLEMENTATION.md's "F32 vs F64 accumulation" section
// for the deliberate A/B decision this switch exists to make reproducible.
// Build with -DORCENGINE_ACCUM_F64 to select the F64 variant for comparison.
#ifdef ORCENGINE_ACCUM_F64
using AccumT = double;
#else
using AccumT = float;
#endif

// x: [rows, cols] flattened row-major. weight: [cols] (broadcast per row).
std::vector<float> rmsnorm(const std::vector<float>& x, int64_t rows, int64_t cols,
                            const std::vector<float>& weight, float epsilon);

std::vector<float> silu(const std::vector<float>& x);

// --- Output-buffer forms (Phase 5C addition, backward-compatible) ---------
// Same arithmetic as the return-by-value forms above (see ops.cpp: the
// return-by-value forms are now implemented BY CALLING these -- there is
// exactly one implementation of each operation's math, never two). Writes
// into caller-supplied `out`; never resizes, reallocates, or takes
// ownership of it. `out.size()` must exactly equal the operation's output
// element count (row*col products are overflow-checked before any write);
// throws std::invalid_argument/std::overflow_error before writing anything
// if it does not. Added only for the operations Phase 5C's cached-decode
// activation workspace actually reuses across steps (rmsnorm,
// linear_no_bias, silu) -- not mechanically added to every operation.
void rmsnorm_into(std::span<const float> x, int64_t rows, int64_t cols,
                  std::span<const float> weight, float epsilon, std::span<float> out);
void silu_into(std::span<const float> x, std::span<float> out);

// x: [rows, cols]. Softmax over the last axis (cols), per row, max-subtracted.
std::vector<float> softmax_last_axis(const std::vector<float>& x, int64_t rows, int64_t cols);

// scores: [q_len, k_len]. Sets scores[q][k] = -inf where k > q (causal).
std::vector<float> causal_mask(const std::vector<float>& scores, int64_t q_len, int64_t k_len);

// Full-rotation (rotary_dim == head_dim) non-interleaved Llama RoPE cos/sin for one position.
void rope_cos_sin(int64_t position, int64_t head_dim, double theta,
                   std::vector<float>& cos_out, std::vector<float>& sin_out);

// x: [head_dim]. Returns [-second_half, first_half].
std::vector<float> rope_rotate_half(const std::vector<float>& x);

// x, cos, sin: all [head_dim] (full rotation only -- Phase 1 has no partial rotary factor).
std::vector<float> apply_rope(const std::vector<float>& x, const std::vector<float>& cos,
                               const std::vector<float>& sin);

// x: [rows, in_features]. weight_out_in: [out_features, in_features]. Returns [rows, out_features].
std::vector<float> linear_no_bias(const std::vector<float>& x, int64_t rows, int64_t in_features,
                                   const std::vector<float>& weight_out_in, int64_t out_features);
void linear_no_bias_into(std::span<const float> x, int64_t rows, int64_t in_features,
                         std::span<const float> weight_out_in, int64_t out_features, std::span<float> out);

// table: [vocab, hidden]. token_ids: [seq]. Returns [seq, hidden].
std::vector<float> embedding_lookup(const std::vector<float>& table, int64_t hidden,
                                     const std::vector<int64_t>& token_ids);

// x: [rows] (a single row's logits). Returns the index of the maximum value.
int64_t argmax(const std::vector<float>& x);

}  // namespace orcengine::ops
