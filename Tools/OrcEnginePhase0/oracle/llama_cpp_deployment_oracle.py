# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Secondary deployment oracle for OE-L0-SYNTH-1, per PHASE_0_REFERENCE_ORACLE.md:
  "secondary deployment oracle: pinned llama.cpp for GGUF interpretation
  and end-to-end token comparison."

Writes Profile A's own weights into a real "llama"-architecture GGUF file
(oracle/export_gguf.py), starts a pinned llama.cpp llama-server.exe against
it, requests a completion with logprobs, and compares against our own
oracle's log_softmax for the same input. This is llama.cpp itself acting as
an independent, external, C++/GGML implementation -- not a Python
reimplementation -- which is what makes it a genuinely separate
independence class from the NumPy/PyTorch legs.

Pinned llama.cpp build: b10436 (2026-08-14), CPU, downloaded from
https://github.com/ggml-org/llama.cpp/releases/tag/b10436 -- path below
must point at a llama-server.exe from that same build for reproducibility.
"""
from __future__ import annotations

import json
import os
import subprocess
import time
import urllib.request

import numpy as np

from oracle.export_gguf import run as export_gguf
from oracle.model import ModelConfig, forward
from oracle.weights import build_weights

# Machine-specific -- override with the ORC_LLAMA_SERVER_PATH env var. Get the binary from
# https://github.com/ggml-org/llama.cpp/releases/tag/b10436 (llama-b10436-bin-win-cpu-x64.zip
# on Windows; adjust for other platforms) and point this at llama-server(.exe) from that build.
LLAMA_SERVER_PATH = os.environ.get("ORC_LLAMA_SERVER_PATH", "")
PORT = 8734
SEED = 20260814
LOGPROB_ATOL = 0.1  # looser than intra-Python tolerances -- cross-language, cross-library comparison


def _oracle_log_softmax(token_ids: np.ndarray) -> np.ndarray:
    config = ModelConfig(vocab=32, hidden=16, intermediate=32, n_layers=2,
                          n_q_heads=4, n_kv_heads=2, head_dim=4, max_positions=16)
    weights = build_weights(seed=SEED, vocab=config.vocab, hidden=config.hidden,
                             intermediate=config.intermediate, n_layers=config.n_layers,
                             n_q_heads=config.n_q_heads, n_kv_heads=config.n_kv_heads,
                             head_dim=config.head_dim)
    result = forward(token_ids, weights, config, capture_taps=False)
    logits = result.logits[-1].astype(np.float64)
    m = logits.max()
    return logits - m - np.log(np.sum(np.exp(logits - m)))


def _wait_for_health(port: int, timeout_s: float = 15.0) -> bool:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1) as resp:
                if json.loads(resp.read())["status"] == "ok":
                    return True
        except Exception:
            pass
        time.sleep(0.3)
    return False


def _request_completion(port: int, prompt: str, n_probs: int) -> dict:
    payload = json.dumps({
        "prompt": prompt, "n_predict": 1, "temperature": 0, "n_probs": n_probs, "cache_prompt": False,
    }).encode()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/completion", data=payload,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=15) as resp:
        return json.loads(resp.read())


def run() -> bool:
    if not os.path.isfile(LLAMA_SERVER_PATH):
        print(f"FAIL: llama-server not found at {LLAMA_SERVER_PATH!r}. "
              f"Set ORC_LLAMA_SERVER_PATH or download b10436 from "
              f"https://github.com/ggml-org/llama.cpp/releases/tag/b10436")
        return False

    gguf_path = export_gguf()

    token_ids = np.array([1, 5, 9, 3, 7], dtype=np.int64)
    prompt = "".join(f"<{t}>" for t in token_ids)

    proc = subprocess.Popen(
        [LLAMA_SERVER_PATH, "-m", gguf_path, "--port", str(PORT), "--no-warmup"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    try:
        if not _wait_for_health(PORT):
            print("FAIL: llama-server did not become healthy in time")
            return False

        response = _request_completion(PORT, prompt, n_probs=5)
        top = response["completion_probabilities"][0]["top_logprobs"]
        llama_cpp_ids = [t["id"] for t in top]
        llama_cpp_logprobs = [t["logprob"] for t in top]

        log_softmax = _oracle_log_softmax(token_ids)
        oracle_argmax = int(np.argmax(log_softmax))
        llama_cpp_argmax = llama_cpp_ids[0]  # server returns top_logprobs sorted descending

        print(f"oracle argmax token: {oracle_argmax}")
        print(f"llama.cpp argmax token: {llama_cpp_argmax}")
        argmax_match = oracle_argmax == llama_cpp_argmax

        all_within_tol = True
        for tid, lcp in zip(llama_cpp_ids, llama_cpp_logprobs):
            diff = abs(log_softmax[tid] - lcp)
            within = diff <= LOGPROB_ATOL
            all_within_tol = all_within_tol and within
            print(f"  token {tid:2d}: oracle_log_softmax={log_softmax[tid]:.6f}  "
                  f"llama.cpp_logprob={lcp:.6f}  diff={diff:.6f}  within_tol={within}")

        return argmax_match and all_within_tol
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: llama.cpp deployment oracle agreement")
    raise SystemExit(0 if ok else 1)
