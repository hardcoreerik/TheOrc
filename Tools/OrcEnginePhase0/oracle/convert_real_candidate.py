# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Converts the downloaded SmolLM2-135M (revision
93efa2f097d58c2a74874c7e644dbc9b0cee75a2, oracle/download_candidate.py) into
a real "llama"-architecture GGUF file. Real conversion of a real model --
not the synthetic Profile A trick in oracle/export_gguf.py, though it
reuses the same official `gguf` PyPI writer for the same reason: avoid
hand-rolling the binary format.

Tensor mapping (verified against the actual safetensors keys, not assumed):
  model.embed_tokens.weight              -> token_embd.weight (also tied -> output.weight)
  model.norm.weight                      -> output_norm.weight
  model.layers.N.input_layernorm.weight  -> blk.N.attn_norm.weight
  model.layers.N.self_attn.q_proj.weight -> blk.N.attn_q.weight
  model.layers.N.self_attn.k_proj.weight -> blk.N.attn_k.weight
  model.layers.N.self_attn.v_proj.weight -> blk.N.attn_v.weight
  model.layers.N.self_attn.o_proj.weight -> blk.N.attn_output.weight
  model.layers.N.post_attention_layernorm.weight -> blk.N.ffn_norm.weight
  model.layers.N.mlp.gate_proj.weight    -> blk.N.ffn_gate.weight
  model.layers.N.mlp.up_proj.weight      -> blk.N.ffn_up.weight
  model.layers.N.mlp.down_proj.weight    -> blk.N.ffn_down.weight
Source weights are bfloat16 (config.json: torch_dtype=bfloat16); upcast to
float32 on load (numpy has no native bfloat16 type; PyTorch does, so
safetensors is loaded via the "pt" framework then converted). This is a
widening upcast, not a precision-losing downcast -- consistent with
PHASE_0_REFERENCE_ORACLE.md's "explicit float32 execution" determinism
control, and matches Profile A's own float32-only numeric policy.

All HF Linear weights are already stored [out_features, in_features] --
verified against the actual tensor shapes (e.g. self_attn.k_proj.weight is
[192, 576] = [num_kv_heads(3)*head_dim(64), hidden(576)]) -- same
convention oracle/model.py already uses, so no transpose is needed.

Tokenizer: real GPT2-style byte-level BPE (tokenizer.ggml.model="gpt2"),
real vocab.json + merges.txt transcribed directly into GGUF's tokenizer.*
arrays -- llama.cpp's own GPT2 tokenizer implementation (already proven,
used by many real models) does the actual algorithm; this step is data
transcription, not a from-scratch tokenizer implementation.
"""
from __future__ import annotations

import hashlib
import json
import os

import numpy as np
import torch
from gguf import GGUFWriter, TokenType
from safetensors import safe_open

SOURCE_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m")
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m.gguf")


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _load_config() -> dict:
    with open(os.path.join(SOURCE_DIR, "config.json"), encoding="utf-8") as f:
        return json.load(f)


def _load_tokenizer_arrays() -> tuple[list[str], list[str], list[int]]:
    with open(os.path.join(SOURCE_DIR, "vocab.json"), encoding="utf-8") as f:
        vocab = json.load(f)  # token string -> id
    with open(os.path.join(SOURCE_DIR, "merges.txt"), encoding="utf-8") as f:
        lines = f.read().splitlines()
    merges = [ln for ln in lines[1:] if ln]  # skip "#version: 0.2" header line

    with open(os.path.join(SOURCE_DIR, "special_tokens_map.json"), encoding="utf-8") as f:
        special_map = json.load(f)
    special_strings = set(special_map.get("additional_special_tokens", []))
    for key in ("bos_token", "eos_token", "unk_token"):
        val = special_map.get(key)
        if isinstance(val, dict):
            special_strings.add(val["content"])
        elif isinstance(val, str):
            special_strings.add(val)

    tokens_by_id = [None] * len(vocab)
    for tok, idx in vocab.items():
        tokens_by_id[idx] = tok
    assert all(t is not None for t in tokens_by_id), "vocab.json has a gap in token IDs"

    token_types = [
        int(TokenType.CONTROL) if tok in special_strings else int(TokenType.NORMAL)
        for tok in tokens_by_id
    ]
    return tokens_by_id, merges, token_types


def write_gguf(output_path: str, name: str, config: dict,
                tokens: list[str], merges: list[str], token_types: list[int]) -> str:
    """
    Single source of truth for the real-candidate GGUF layout. Both the correct
    conversion (convert(), below) and the deliberately-faulted variant
    (oracle/tokenizer_special_token_fault.py) call this with everything identical
    except token_types -- extracted per a CodeRabbit finding (PR #102): the fault
    script originally copy-pasted this whole function, which meant a future
    metadata change to convert() alone would make the faulted GGUF differ from the
    correct one in a SECOND dimension, no longer isolating the injected fault.
    """
    hidden = config["hidden_size"]
    n_layers = config["num_hidden_layers"]
    intermediate = config["intermediate_size"]
    n_heads = config["num_attention_heads"]
    n_kv_heads = config["num_key_value_heads"]
    head_dim = hidden // n_heads
    rms_eps = config["rms_norm_eps"]
    rope_theta = config["rope_theta"]
    max_pos = config["max_position_embeddings"]
    bos_id = config["bos_token_id"]
    eos_id = config["eos_token_id"]

    assert config.get("tie_word_embeddings", False), \
        "tie_word_embeddings is false in source config; output.weight must come from " \
        "lm_head.weight, not token_embd.weight -- this converter only implements the tied case"

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    writer = GGUFWriter(output_path, arch="llama")

    writer.add_name(name)
    writer.add_context_length(max_pos)
    writer.add_embedding_length(hidden)
    writer.add_block_count(n_layers)
    writer.add_feed_forward_length(intermediate)
    writer.add_head_count(n_heads)
    writer.add_head_count_kv(n_kv_heads)
    writer.add_layer_norm_rms_eps(rms_eps)
    writer.add_rope_dimension_count(head_dim)
    writer.add_rope_freq_base(rope_theta)
    writer.add_file_type(0)  # ALL_F32

    writer.add_tokenizer_model("gpt2")
    # Pre-tokenizer regex-scheme identifier. Confirmed against llama.cpp's own
    # convert_hf_to_gguf_update.py, which maps the SmolLM tokenizer family to "smollm"
    # (entry: {"name": "smollm", ..., "repo": ".../HuggingFaceTB/SmolLM-135M"}). Without
    # this key, llama.cpp falls back to a generic pre-tokenizer and prints
    # "GENERATION QUALITY WILL BE DEGRADED" -- confirmed by omitting it first.
    writer.add_tokenizer_pre("smollm")
    writer.add_token_list(tokens)
    writer.add_token_merges(merges)
    writer.add_token_types(token_types)
    writer.add_bos_token_id(bos_id)
    writer.add_eos_token_id(eos_id)
    writer.add_add_bos_token(False)
    writer.add_add_eos_token(False)

    safetensors_path = os.path.join(SOURCE_DIR, "model.safetensors")
    # Loaded as bfloat16 (config.json: torch_dtype=bfloat16) -- numpy has no native bfloat16,
    # so load via the PyTorch framework (which does) and upcast to float32 explicitly.
    with safe_open(safetensors_path, framework="pt") as f:
        def get(tensor_name: str) -> np.ndarray:
            return f.get_tensor(tensor_name).to(dtype=torch.float32).numpy()

        embed = get("model.embed_tokens.weight")
        writer.add_tensor("token_embd.weight", embed)
        writer.add_tensor("output_norm.weight", get("model.norm.weight"))
        writer.add_tensor("output.weight", embed)  # tied, asserted above

        for i in range(n_layers):
            p = f"model.layers.{i}."
            writer.add_tensor(f"blk.{i}.attn_norm.weight", get(p + "input_layernorm.weight"))
            writer.add_tensor(f"blk.{i}.attn_q.weight", get(p + "self_attn.q_proj.weight"))
            writer.add_tensor(f"blk.{i}.attn_k.weight", get(p + "self_attn.k_proj.weight"))
            writer.add_tensor(f"blk.{i}.attn_v.weight", get(p + "self_attn.v_proj.weight"))
            writer.add_tensor(f"blk.{i}.attn_output.weight", get(p + "self_attn.o_proj.weight"))
            writer.add_tensor(f"blk.{i}.ffn_norm.weight", get(p + "post_attention_layernorm.weight"))
            writer.add_tensor(f"blk.{i}.ffn_gate.weight", get(p + "mlp.gate_proj.weight"))
            writer.add_tensor(f"blk.{i}.ffn_up.weight", get(p + "mlp.up_proj.weight"))
            writer.add_tensor(f"blk.{i}.ffn_down.weight", get(p + "mlp.down_proj.weight"))

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()
    return output_path


def convert() -> str:
    config = _load_config()
    tokens, merges, token_types = _load_tokenizer_arrays()

    safetensors_path = os.path.join(SOURCE_DIR, "model.safetensors")
    source_sha256 = _sha256_file(safetensors_path)

    write_gguf(OUTPUT_PATH, "SmolLM2-135M", config, tokens, merges, token_types)

    output_sha256 = _sha256_file(OUTPUT_PATH)
    size = os.path.getsize(OUTPUT_PATH)
    print(f"wrote {OUTPUT_PATH} ({size} bytes)")
    print(f"source model.safetensors sha256: {source_sha256}")
    print(f"converted GGUF sha256: {output_sha256}")

    return OUTPUT_PATH


if __name__ == "__main__":
    convert()
