# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Generates fixtures/tampered_ambiguous.gguf: a small, deterministic,
internally-contradictory synthetic GGUF for FL-08 Case E.

Contradiction: declares `llama.block_count = 2` (two transformer
layers) but only writes tensors for layer 0 -- Layer 2 (architecture
validation)'s layer-count-consistency check must catch this and
classify the artifact INVALID, not silently proceed with 1 layer.

Deterministic: every tensor's contents are a fixed, seeded array (not
random-each-run), and no timestamp metadata is written, so re-running
this script produces a byte-identical file every time (verified by the
test suite).

Run once to (re)generate the committed fixture:
    python make_tampered_fixture.py
"""
from __future__ import annotations

import os

import numpy as np
from gguf import GGUFWriter

OUT_PATH = os.path.join(os.path.dirname(__file__), "fixtures", "tampered_ambiguous.gguf")

HIDDEN = 8
N_HEAD = 2
N_HEAD_KV = 2
HEAD_DIM = HIDDEN // N_HEAD
INTERMEDIATE = 16
VOCAB = 32


def main() -> None:
    writer = GGUFWriter(OUT_PATH, arch="llama")
    writer.add_name("fl08-tampered-ambiguous-fixture")
    writer.add_context_length(128)
    writer.add_embedding_length(HIDDEN)
    writer.add_block_count(2)  # DECLARES 2 layers ...
    writer.add_feed_forward_length(INTERMEDIATE)
    writer.add_head_count(N_HEAD)
    writer.add_head_count_kv(N_HEAD_KV)
    writer.add_layer_norm_rms_eps(1e-5)
    writer.add_rope_dimension_count(HEAD_DIM)
    writer.add_rope_freq_base(10000.0)
    writer.add_file_type(0)

    rng = np.random.default_rng(seed=12345)  # fixed seed -- deterministic content, not random-each-run

    def const_tensor(*shape) -> np.ndarray:
        return rng.standard_normal(shape).astype(np.float32)

    writer.add_tensor("token_embd.weight", const_tensor(VOCAB, HIDDEN))
    writer.add_tensor("output_norm.weight", const_tensor(HIDDEN))

    # ... but only layer 0's tensors are actually written (layer 1 is
    # MISSING -- the deliberate contradiction).
    i = 0
    writer.add_tensor(f"blk.{i}.attn_norm.weight", const_tensor(HIDDEN))
    writer.add_tensor(f"blk.{i}.attn_q.weight", const_tensor(N_HEAD * HEAD_DIM, HIDDEN))
    writer.add_tensor(f"blk.{i}.attn_k.weight", const_tensor(N_HEAD_KV * HEAD_DIM, HIDDEN))
    writer.add_tensor(f"blk.{i}.attn_v.weight", const_tensor(N_HEAD_KV * HEAD_DIM, HIDDEN))
    writer.add_tensor(f"blk.{i}.attn_output.weight", const_tensor(HIDDEN, N_HEAD * HEAD_DIM))
    writer.add_tensor(f"blk.{i}.ffn_norm.weight", const_tensor(HIDDEN))
    writer.add_tensor(f"blk.{i}.ffn_gate.weight", const_tensor(INTERMEDIATE, HIDDEN))
    writer.add_tensor(f"blk.{i}.ffn_up.weight", const_tensor(INTERMEDIATE, HIDDEN))
    writer.add_tensor(f"blk.{i}.ffn_down.weight", const_tensor(HIDDEN, INTERMEDIATE))

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()
    print(f"wrote {OUT_PATH}")


if __name__ == "__main__":
    main()
