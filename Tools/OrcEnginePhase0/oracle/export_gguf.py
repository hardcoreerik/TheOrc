# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Writes OE-L0-SYNTH-1's own weights into a real "llama"-architecture GGUF
file, so llama.cpp itself can be used as the secondary deployment oracle
(PHASE_0_REFERENCE_ORACLE.md's third independence class) without waiting
for Fixture D's real-model conversion. Profile A's block semantics
(RMSNorm, GQA, non-interleaved RoPE, SwiGLU) ARE the standard llama.cpp
"llama" architecture at any size -- this isn't a proprietary format, it's
the real one, just tiny.

Uses the official `gguf` PyPI package (the same writer llama.cpp's own
conversion scripts use) rather than hand-rolling the binary format --
avoids reintroducing exactly the kind of "wrong hand-derived format" risk
this whole project exists to guard against.

Tokenizer design: every one of the 32 vocab entries is a CONTROL token
(TOKEN_TYPE=3) with a unique unambiguous string "<0>".."<31>". Control
tokens are matched by exact string search in llama.cpp's tokenizer
pipeline BEFORE the base BPE/SPM algorithm runs, so a prompt string built
from these markers maps deterministically to exact token IDs without
depending on merge rules or byte-level encoding schemes -- avoiding a
second, unrelated format-guessing risk on top of the GGUF layout itself.
"""
from __future__ import annotations

import os

import numpy as np
from gguf import GGUFWriter, TokenType

from oracle.model import ModelConfig
from oracle.weights import ModelWeights, build_weights

SEED = 20260814
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "oe_l0_synth_1.gguf")


def export(weights: ModelWeights, config: ModelConfig, output_path: str) -> None:
    writer = GGUFWriter(output_path, arch="llama")

    writer.add_name("OE-L0-SYNTH-1")
    writer.add_context_length(config.max_positions)
    writer.add_embedding_length(config.hidden)
    writer.add_block_count(config.n_layers)
    writer.add_feed_forward_length(config.intermediate)
    writer.add_head_count(config.n_q_heads)
    writer.add_head_count_kv(config.n_kv_heads)
    writer.add_layer_norm_rms_eps(config.rmsnorm_epsilon)
    writer.add_rope_dimension_count(config.head_dim)
    writer.add_rope_freq_base(config.rope_theta)
    writer.add_file_type(0)  # ALL_F32

    tokens = [f"<{i}>" for i in range(config.vocab)]
    scores = [0.0] * config.vocab
    token_types = [TokenType.CONTROL] * config.vocab
    writer.add_tokenizer_model("llama")
    writer.add_token_list(tokens)
    writer.add_token_scores(scores)
    writer.add_token_types(token_types)
    writer.add_bos_token_id(0)
    writer.add_eos_token_id(0)
    writer.add_add_bos_token(False)
    writer.add_add_eos_token(False)

    writer.add_tensor("token_embd.weight", weights.token_embedding)
    writer.add_tensor("output_norm.weight", weights.final_norm_weight)
    writer.add_tensor("output.weight", weights.token_embedding)  # tied, written explicitly

    for i, lw in enumerate(weights.layers):
        writer.add_tensor(f"blk.{i}.attn_norm.weight", lw.attn_norm_weight)
        writer.add_tensor(f"blk.{i}.attn_q.weight", lw.w_q)
        writer.add_tensor(f"blk.{i}.attn_k.weight", lw.w_k)
        writer.add_tensor(f"blk.{i}.attn_v.weight", lw.w_v)
        writer.add_tensor(f"blk.{i}.attn_output.weight", lw.w_o)
        writer.add_tensor(f"blk.{i}.ffn_norm.weight", lw.ffn_norm_weight)
        writer.add_tensor(f"blk.{i}.ffn_gate.weight", lw.w_gate)
        writer.add_tensor(f"blk.{i}.ffn_down.weight", lw.w_down)
        writer.add_tensor(f"blk.{i}.ffn_up.weight", lw.w_up)

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()


def run() -> str:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    export(weights, config, OUTPUT_PATH)
    size = os.path.getsize(OUTPUT_PATH)
    print(f"wrote {OUTPUT_PATH} ({size} bytes)")
    return OUTPUT_PATH


if __name__ == "__main__":
    run()
