# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
The 7th and final required fault type for fault_injection, per
PHASE_0_REFERENCE_ORACLE.md's "Fault-injection proof": tokenizer
special-token error. Needed a real tokenizer to test against -- Profile A
has none, which is why this was deferred until the real-candidate
conversion (oracle/convert_real_candidate.py) existed.

Fault: the token "<|im_start|>" (id=1) is correctly TOKEN_TYPE=CONTROL in
the real conversion, meaning llama.cpp's tokenizer matches it as a single
special token by exact string search. This script writes a FAULTED GGUF
where that same token is mislabeled TOKEN_TYPE=NORMAL, so llama.cpp instead
tries to tokenize the literal substring "<|im_start|>" through the normal
byte-level BPE algorithm -- producing a different token sequence than
either the correct GGUF or the true HF tokenizer.

Detection: tokenize a prompt containing "<|im_start|>" against both GGUFs.
The correct GGUF must match the true HF tokenizer (already proven in
oracle/tokenizer_dual_source_check.py); the faulted GGUF must diverge from
BOTH the correct GGUF and the true HF tokenizer. Divergence-from-truth is
the actual proof, not merely "the two GGUFs differ from each other."
"""
from __future__ import annotations

import json
import os
import subprocess

from gguf import GGUFWriter, TokenType
from tokenizers import Tokenizer

from oracle.convert_real_candidate import SOURCE_DIR, _load_config, _load_tokenizer_arrays
import torch
from safetensors import safe_open

FAULTED_GGUF_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m-faulted-special-token.gguf")
CORRECT_GGUF_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m.gguf")
LLAMA_TOKENIZE_PATH = os.environ.get(
    "ORC_LLAMA_TOKENIZE_PATH",
    r"C:\Users\hardc\AppData\Local\Temp\llamacpp_test\llama-tokenize.exe",
)
TOKENIZER_JSON_PATH = os.path.join(SOURCE_DIR, "tokenizer.json")

FAULT_TOKEN = "<|im_start|>"
PROMPT = "<|im_start|>user"


def _write_faulted_gguf() -> str:
    config = _load_config()
    tokens, merges, token_types = _load_tokenizer_arrays()

    fault_idx = tokens.index(FAULT_TOKEN)
    assert token_types[fault_idx] == int(TokenType.CONTROL), \
        f"expected {FAULT_TOKEN!r} to be CONTROL before faulting"
    token_types = list(token_types)
    token_types[fault_idx] = int(TokenType.NORMAL)  # THE FAULT: control -> normal

    hidden, n_layers = config["hidden_size"], config["num_hidden_layers"]
    intermediate = config["intermediate_size"]
    n_heads, n_kv_heads = config["num_attention_heads"], config["num_key_value_heads"]
    head_dim = hidden // n_heads

    writer = GGUFWriter(FAULTED_GGUF_PATH, arch="llama")
    writer.add_name("SmolLM2-135M-faulted-special-token")
    writer.add_context_length(config["max_position_embeddings"])
    writer.add_embedding_length(hidden)
    writer.add_block_count(n_layers)
    writer.add_feed_forward_length(intermediate)
    writer.add_head_count(n_heads)
    writer.add_head_count_kv(n_kv_heads)
    writer.add_layer_norm_rms_eps(config["rms_norm_eps"])
    writer.add_rope_dimension_count(head_dim)
    writer.add_rope_freq_base(config["rope_theta"])
    writer.add_file_type(0)
    writer.add_tokenizer_model("gpt2")
    writer.add_tokenizer_pre("smollm")
    writer.add_token_list(tokens)
    writer.add_token_merges(merges)
    writer.add_token_types(token_types)  # faulted array
    writer.add_bos_token_id(config["bos_token_id"])
    writer.add_eos_token_id(config["eos_token_id"])
    writer.add_add_bos_token(False)
    writer.add_add_eos_token(False)

    safetensors_path = os.path.join(SOURCE_DIR, "model.safetensors")
    with safe_open(safetensors_path, framework="pt") as f:
        def get(name):
            return f.get_tensor(name).to(dtype=torch.float32).numpy()
        embed = get("model.embed_tokens.weight")
        writer.add_tensor("token_embd.weight", embed)
        writer.add_tensor("output_norm.weight", get("model.norm.weight"))
        writer.add_tensor("output.weight", embed)
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
    return FAULTED_GGUF_PATH


def _llama_cpp_tokenize(gguf_path: str, text: str) -> list[int]:
    proc = subprocess.run(
        [LLAMA_TOKENIZE_PATH, "-m", gguf_path, "-p", text, "--ids"],
        capture_output=True, timeout=30,
    )
    stdout = proc.stdout.decode("utf-8", errors="replace")
    for line in reversed(stdout.splitlines()):
        line = line.strip()
        if line.startswith("[") and line.endswith("]"):
            return json.loads(line)
    raise RuntimeError(f"could not find token ID list in output: {stdout!r}")


def run() -> bool:
    if not os.path.isfile(CORRECT_GGUF_PATH):
        print("FAIL: correct GGUF not found, run oracle.convert_real_candidate first")
        return False

    _write_faulted_gguf()

    true_ids = Tokenizer.from_file(TOKENIZER_JSON_PATH).encode(PROMPT).ids
    correct_gguf_ids = _llama_cpp_tokenize(CORRECT_GGUF_PATH, PROMPT)
    faulted_gguf_ids = _llama_cpp_tokenize(FAULTED_GGUF_PATH, PROMPT)

    print(f"prompt: {PROMPT!r}")
    print(f"  true (HF tokenizer.json):        {true_ids}")
    print(f"  correct GGUF (CONTROL type):     {correct_gguf_ids}")
    print(f"  faulted GGUF (NORMAL type):      {faulted_gguf_ids}")

    correct_matches_truth = correct_gguf_ids == true_ids
    faulted_diverges_from_truth = faulted_gguf_ids != true_ids
    faulted_diverges_from_correct = faulted_gguf_ids != correct_gguf_ids

    print(f"\n  correct GGUF matches true tokenizer: {correct_matches_truth}")
    print(f"  faulted GGUF diverges from truth:    {faulted_diverges_from_truth}")
    print(f"  faulted GGUF diverges from correct:  {faulted_diverges_from_correct}")

    return correct_matches_truth and faulted_diverges_from_truth and faulted_diverges_from_correct


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: tokenizer_special_token_error fault detected")
    raise SystemExit(0 if ok else 1)
