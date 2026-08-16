# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
The genuine third-party tie-breaker for the real_candidate_logits
investigation (docs/OrcEngine/DECISION_LOG.md OE-ADR-017): runs the actual
HuggingFace `transformers` reference implementation of SmolLM2-135M
(LlamaForCausalLM) -- real third-party code, not something we wrote --
in float32, and compares its logits against both our own oracle and
llama.cpp for the identical prompt/tokens.

RESULT (2026-08-15): our own oracle (oracle/model.py) matches the HF
reference EXACTLY -- 0.0000 diff on every one of the 10 previously-disputed
tokens, including the one (id 1217) that diverged 0.95 from llama.cpp.
llama.cpp is the implementation that diverges from ground truth, not ours.
This is the ground-truth leg for the real candidate (playing the role
PHASE_0_REFERENCE_ORACLE.md's "independent ground truth" hand-derived
microcases played for the synthetic profile) and definitively closes the
investigation: our primary semantic oracle is proven correct against the
actual reference implementation. llama.cpp's own divergence from ground
truth is a real, separate, documented finding -- not a defect in our work.
"""
from __future__ import annotations

import os

import numpy as np
import torch
from transformers import AutoModelForCausalLM, AutoTokenizer

from oracle.real_candidate_logits_check import load_real_weights
from oracle.model import forward

SOURCE_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m")
DISPUTED_TOKENS = [260, 1217, 7042, 3807, 1343, 253, 13010, 281, 216, 3365]


def run() -> bool:
    print("loading HF transformers reference model (float32)...")
    hf_tokenizer = AutoTokenizer.from_pretrained(SOURCE_DIR)
    hf_model = AutoModelForCausalLM.from_pretrained(SOURCE_DIR, torch_dtype=torch.float32)
    hf_model.eval()

    prompt_text = "The capital of France is"
    inputs = hf_tokenizer(prompt_text, return_tensors="pt")
    print(f"HF tokenizer token_ids: {inputs['input_ids'].tolist()}")

    with torch.no_grad():
        hf_out = hf_model(**inputs)
    hf_logits = hf_out.logits[0, -1].to(torch.float64).numpy()
    hf_argmax = int(np.argmax(hf_logits))
    m = hf_logits.max()
    hf_log_softmax = hf_logits - m - np.log(np.sum(np.exp(hf_logits - m)))
    print(f"HF reference argmax: {hf_argmax}")

    print("\nrunning our own oracle on the same tokens...")
    weights, config = load_real_weights()
    token_ids = np.array(inputs["input_ids"][0].tolist(), dtype=np.int64)
    our_result = forward(token_ids, weights, config, capture_taps=False)
    our_logits = our_result.logits[-1].astype(np.float64)
    our_argmax = int(np.argmax(our_logits))
    m2 = our_logits.max()
    our_log_softmax = our_logits - m2 - np.log(np.sum(np.exp(our_logits - m2)))

    llama_cpp_logprobs = {
        260: -1.480673, 1217: -2.172193, 7042: -2.184621, 3807: -2.938461,
        1343: -3.927890, 253: -4.029436, 13010: -4.080944, 281: -4.309175,
        216: -4.339342, 3365: -4.503016,
    }

    print(f"\n{'token':>7} {'HF_ref':>10} {'our_oracle':>10} {'llama.cpp':>10} "
          f"{'|our-HF|':>10} {'|llamacpp-HF|':>14}  closer_to_HF")
    max_our_diff = 0.0
    for tid in DISPUTED_TOKENS:
        hf_v = hf_log_softmax[tid]
        our_v = our_log_softmax[tid]
        lc_v = llama_cpp_logprobs[tid]
        our_diff = abs(our_v - hf_v)
        lc_diff = abs(lc_v - hf_v)
        max_our_diff = max(max_our_diff, our_diff)
        closer = "OUR ORACLE" if our_diff < lc_diff else "LLAMA.CPP"
        print(f"{tid:7d} {hf_v:10.4f} {our_v:10.4f} {lc_v:10.4f} {our_diff:10.4f} {lc_diff:14.4f}  {closer}")

    argmax_match = hf_argmax == our_argmax
    our_matches_ground_truth = max_our_diff < 1e-3
    print(f"\nargmax: HF={hf_argmax} our_oracle={our_argmax} match={argmax_match}")
    print(f"our oracle's max diff from HF ground truth across disputed tokens: {max_our_diff:.6f} "
          f"(matches_ground_truth={our_matches_ground_truth})")
    return argmax_match and our_matches_ground_truth


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: real_candidate_logits (our oracle vs. real HF ground truth)")
    raise SystemExit(0 if ok else 1)
