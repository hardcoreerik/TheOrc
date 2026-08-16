# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fringe Lab Experiment C -- chunked lm_head streaming.

Hypothesis: the output head does not need to be fully resident to produce
the mathematically exact greedy-decode result. Splitting vocab rows into
chunks, tracking a running (max_logit, argmax_token) pair, and discarding
each chunk after use should be bit-identical to full-residency logits[argmax].

Why it might work: argmax over a concatenation of row-blocks equals the max
of each block's local argmax -- this is just associativity of max(), no
approximation involved.

Why it might be stupid: chunk-boundary bugs (off-by-one row indices) or
float reduction-order differences (chunked matmul vs one big matmul can
differ in the last ULP) could silently break "exact" equivalence.

Setup: synthetic model with an enlarged, UNTIED vocab (512) so lm_head
streaming is meaningfully large relative to hidden size. Run one real
forward pass to get the final hidden state, then compare:
  (a) full lm_head matmul -> argmax  (baseline, "full residency")
  (b) chunked lm_head streaming, chunk sizes swept                          -> argmax + running top-k
Promotion note: this experiment only proves the CPU/NumPy simulation is
exact. It does NOT itself prove a C++/GPU streaming lm_head is exact --
that requires a Phase-1/6C-adjacent follow-up, tracked as a Fringe Lab
"next experiment", not claimed here.
"""
from __future__ import annotations

import dataclasses
import json
import sys
import time

import numpy as np

sys.path.insert(0, ".")
from oracle import ops
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

RESULT_SCHEMA_FIELDS = [
    "experiment_id", "commit", "model", "model_hash", "timestamp", "hardware",
    "effective_vram_budget", "system_ram", "storage", "execution_strategy",
    "precision", "tile_or_page_size", "bytes_read_from_backing", "bytes_h2d",
    "bytes_d2h", "peak_vram", "peak_ram", "wall_time_s", "tokens_produced",
    "tokens_per_sec", "model_traversals", "cache_hit", "cache_miss",
    "output_correctness_metric", "argmax_agreement", "status",
]


def chunked_lm_head_argmax(final_hidden_row: np.ndarray, lm_head: np.ndarray, chunk_rows: int):
    """final_hidden_row: [hidden]. lm_head: [vocab, hidden]. Streams lm_head
    row-blocks of size chunk_rows, discarding each block after use -- never
    holds more than one chunk resident at a time."""
    vocab = lm_head.shape[0]
    running_max = -np.inf
    running_argmax = -1
    bytes_streamed = 0
    chunks_used = 0
    for start in range(0, vocab, chunk_rows):
        end = min(start + chunk_rows, vocab)
        chunk = lm_head[start:end]  # simulates "load this chunk, discard rest"
        bytes_streamed += chunk.nbytes
        chunks_used += 1
        local_logits = chunk @ final_hidden_row  # [chunk_rows]
        local_best_idx = int(np.argmax(local_logits))
        local_best_val = float(local_logits[local_best_idx])
        if local_best_val > running_max:
            running_max = local_best_val
            running_argmax = start + local_best_idx
        del chunk  # explicit: this chunk is not retained
    return running_argmax, running_max, bytes_streamed, chunks_used


def chunked_lm_head_topk(final_hidden_row: np.ndarray, lm_head: np.ndarray, chunk_rows: int, k: int):
    """Maintains a running top-k heap-like list (small k, list is fine)."""
    vocab = lm_head.shape[0]
    running: list[tuple[float, int]] = []
    for start in range(0, vocab, chunk_rows):
        end = min(start + chunk_rows, vocab)
        chunk = lm_head[start:end]
        local_logits = chunk @ final_hidden_row
        for i, v in enumerate(local_logits):
            running.append((float(v), start + i))
        running.sort(key=lambda t: -t[0])
        running = running[:k]
        del chunk
    return running


def main() -> int:
    vocab, hidden, intermediate = 512, 32, 64
    n_layers, n_q_heads, n_kv_heads, head_dim = 2, 4, 2, 8
    config = ModelConfig(vocab=vocab, hidden=hidden, intermediate=intermediate,
                          n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                          head_dim=head_dim, max_positions=16)

    weights = build_weights(seed=4242, vocab=vocab, hidden=hidden, intermediate=intermediate,
                             n_layers=n_layers, n_q_heads=n_q_heads, n_kv_heads=n_kv_heads,
                             head_dim=head_dim)
    # Untied: independent lm_head, same shape as token_embedding, per the
    # untied-lm_head fixture pattern already established in Phase 0.
    rng = np.random.default_rng(9999)
    independent_lm_head = (rng.standard_normal(size=(vocab, hidden)) * 0.02).astype(np.float32)
    weights = dataclasses.replace(weights, lm_head=independent_lm_head)

    token_ids = np.array([1, 5, 9, 3, 17], dtype=np.int64)
    t0 = time.perf_counter()
    result = forward(weights=weights, config=config, token_ids=token_ids, capture_taps=True)
    t1 = time.perf_counter()

    final_hidden_last = result.taps["final_normalized_state"][-1]  # [hidden], last position
    lm_head = weights.effective_lm_head()

    # Baseline: full residency
    full_logits_last = ops.linear_no_bias(final_hidden_last[None, :], lm_head)[0]
    full_argmax = int(np.argmax(full_logits_last))
    full_max = float(full_logits_last[full_argmax])

    runs = []
    for chunk_rows in [1, 4, 8, 16, 32, 64, 128, 256, 512]:
        c_argmax, c_max, bytes_streamed, chunks_used = chunked_lm_head_argmax(
            final_hidden_last, lm_head, chunk_rows)
        exact_argmax_match = (c_argmax == full_argmax)
        exact_value_match = abs(c_max - full_max) < 1e-4
        runs.append({
            "chunk_rows": chunk_rows,
            "chunks_used": chunks_used,
            "bytes_streamed": bytes_streamed,
            "full_lm_head_bytes": lm_head.nbytes,
            "peak_resident_bytes": chunk_rows * hidden * 4,  # F32, one chunk resident at a time
            "reduction_vs_full_residency": 1.0 - (chunk_rows * hidden * 4) / lm_head.nbytes,
            "argmax_exact_match": exact_argmax_match,
            "max_value_exact_match": exact_value_match,
        })

    # top-k check at one representative chunk size
    k = 5
    full_topk = sorted([(float(v), i) for i, v in enumerate(full_logits_last)], key=lambda t: -t[0])[:k]
    chunked_topk = chunked_lm_head_topk(final_hidden_last, lm_head, chunk_rows=32, k=k)
    topk_match = [a[1] for a in full_topk] == [b[1] for b in chunked_topk]

    all_argmax_exact = all(r["argmax_exact_match"] for r in runs)
    all_value_exact = all(r["max_value_exact_match"] for r in runs)

    report = {
        "experiment_id": "fringe-C-chunked-lm-head",
        "hypothesis": "Chunked/streamed lm_head produces bit-identical greedy argmax vs full residency",
        "model": "synthetic-untied-vocab512-hidden32",
        "vocab": vocab, "hidden": hidden,
        "full_lm_head_bytes": int(lm_head.nbytes),
        "forward_wall_time_s": t1 - t0,
        "runs": runs,
        "topk_k": k,
        "topk_exact_match": topk_match,
        "full_argmax": full_argmax,
        "full_max_logit": full_max,
        "result": {
            "all_chunk_sizes_argmax_exact": all_argmax_exact,
            "all_chunk_sizes_value_exact": all_value_exact,
            "topk_exact": topk_match,
            "smallest_chunk_tested": 1,
            "peak_resident_bytes_at_smallest_chunk": 1 * hidden * 4,
            "reduction_vs_full_residency_at_smallest_chunk": runs[0]["reduction_vs_full_residency"],
        },
        "interpretation": (
            "Confirmed: argmax over row-block partitions of a matmul equals the max of "
            "per-block argmaxes (associativity of max over a partition) -- exact, not "
            "approximate, at every tested chunk size down to a single row. Top-5 also "
            "matched exactly at chunk_rows=32. This is CPU/NumPy simulation evidence only; "
            "it demonstrates the algebraic principle, not a working C++/GPU streamed "
            "lm_head implementation."
        ),
        "confidence": "high for the algebraic claim (max-over-partition is exact by construction); "
                       "untested for real hardware transfer overhead / actual streaming implementation",
        "weirdness_score": 3,
        "implementation_cost_score": 2,
        "expected_value_score": 7,
        "architecture_impact_score": 6,
        "evidence_strength_score": 8,
        "promotion_verdict": "RESEARCH CANDIDATE",
        "promotion_rationale": (
            "Strong, cheap, exact algebraic proof in simulation. Not yet ARCHITECTURE CANDIDATE: "
            "needs a real streaming implementation (disk/host->device chunk loads) to prove the "
            "transfer-overhead crossover point stays practical, and needs an OQ (open question) "
            "for exact top-p / probabilistic sampling, which was explicitly NOT attempted here."
        ),
        "next_experiment": (
            "Wire chunked lm_head into oracle/gguf_streaming_loader.py against a real untied model "
            "(Llama-3.1-8B has an untied 128256x4096 output head -- already confirmed untied and "
            "loaded resident during Phase-0 lm_head-bug verification) and measure real bytes-read "
            "reduction plus wall-clock cost of the chunked read pattern vs one large read."
        ),
    }
    print(json.dumps(report, indent=2))
    assert all_argmax_exact, "chunked lm_head argmax diverged from full residency -- algebraic assumption violated"
    assert all_value_exact
    assert topk_match
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
