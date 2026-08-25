# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Gate 2 (Codex/Grok remediation round 6): controlled
comparison of llama.cpp's completion against the CANONICAL (correctly
Q/K-permuted) F32 and Q8_0 GGUFs, for the three prompts that disagree
with PyTorch/OrcEngine under the existing custom-layout GGUFs plus one
agreeing control. See
`Tools/OrcEnginePhase6/fixtures/PHASE6_GATE2_QK_LAYOUT_ROOT_CAUSE.md`
for the full writeup and how the canonical GGUFs were produced (the
pinned llama.cpp source's own official converter, commit
6fed9f6ff7a603b124cb8c5864fca6ea879f9f99).

This is a bounded, one-off diagnostic script for this specific
investigation -- it does not attempt to become a general reusable
pipeline component. It reuses the same controlled-server-launch
convention (`--cache-type-k f32 --cache-type-v f32 --flash-attn off`)
and neutral-greedy completion payload established elsewhere in this
project's oracle tooling, and fails closed on a token-ID mismatch
(the same discipline `phase6_llama_cpp_q8_0_oracle.py` uses).

Usage:
    python phase6_gate2_canonical_layout_check.py \
        --server PATH_TO_llama-server.exe \
        --canonical-f32-gguf PATH \
        --canonical-q8-gguf PATH \
        --report PATH_TO_output.json
"""
from __future__ import annotations

import argparse
import json
import socket
import subprocess
import sys
import time
import urllib.request

PROMPTS = {
    "dev_capital_of_france": {"text": "The capital of France is", "token_ids": [504, 3575, 282, 4649, 314]},
    "dev_year_weather": {"text": "In 2026, the weather", "token_ids": [788, 216, 34, 32, 34, 38, 28, 260, 3947]},
    "holdout_she_walked": {"text": "She walked into the", "token_ids": [8113, 13197, 618, 260]},
    "holdout_quick_fox": {"text": "The quick brown fox", "token_ids": [504, 2365, 6354, 16438]},
}

CONTROLLED_NUMERICAL_SERVER_ARGS = ("--cache-type-k", "f32", "--cache-type-v", "f32", "--flash-attn", "off")


def _free_local_port() -> int:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def _wait_health(port: int, timeout_s: float = 60.0) -> None:
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1) as r:
                if json.loads(r.read())["status"] == "ok":
                    return
        except Exception:
            pass
        time.sleep(0.3)
    sys.exit(f"ABORT: server did not become healthy on port {port} within {timeout_s}s")


def run_leg(server_path: str, gguf_path: str, label: str) -> dict:
    port = _free_local_port()
    proc = subprocess.Popen([server_path, "-m", gguf_path, "--port", str(port), "--no-warmup",
                             *CONTROLLED_NUMERICAL_SERVER_ARGS],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    results = {}
    try:
        _wait_health(port)
        for pid, p in PROMPTS.items():
            tok_payload = json.dumps({"content": p["text"], "add_special": False}).encode()
            req = urllib.request.Request(f"http://127.0.0.1:{port}/tokenize", data=tok_payload,
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=15) as r:
                toks = json.loads(r.read())["tokens"]
            if toks != p["token_ids"]:
                sys.exit(f"ABORT: [{label}] token-ID mismatch for {pid!r}: expected {p['token_ids']}, got {toks}")

            payload = json.dumps({
                "prompt": p["text"], "n_predict": 1, "temperature": 0, "n_probs": 5, "cache_prompt": False,
                "repeat_penalty": 1.0, "top_k": 0, "top_p": 1.0, "min_p": 0.0,
                "presence_penalty": 0.0, "frequency_penalty": 0.0,
            }).encode()
            req2 = urllib.request.Request(f"http://127.0.0.1:{port}/completion", data=payload,
                                          headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req2, timeout=30) as r:
                resp = json.loads(r.read())
            top = resp["completion_probabilities"][0]["top_logprobs"]
            argmax = top[0]["id"]
            results[pid] = {"token_ids_match": True, "argmax": argmax,
                            "top5": [(t["id"], round(t["logprob"], 4)) for t in top]}
            print(f"  [{label}] {pid:24s} token_ids_match=True argmax={argmax:6d} top5={results[pid]['top5']}")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
    return results


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", required=True)
    parser.add_argument("--canonical-f32-gguf", required=True)
    parser.add_argument("--canonical-q8-gguf", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()

    print("=== llama.cpp on CANONICAL F32 ===")
    canon_f32_results = run_leg(args.server, args.canonical_f32_gguf, "canon-F32")
    print("\n=== llama.cpp on CANONICAL Q8_0 ===")
    canon_q8_results = run_leg(args.server, args.canonical_q8_gguf, "canon-Q8_0")

    with open(args.report, "w", encoding="utf-8") as f:
        json.dump({"canonical_f32": canon_f32_results, "canonical_q8_0": canon_q8_results}, f, indent=2)
    print(f"\nDONE: written to {args.report!r}")
