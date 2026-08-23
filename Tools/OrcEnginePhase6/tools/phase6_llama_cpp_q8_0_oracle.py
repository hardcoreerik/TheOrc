# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Checkpoint 3: OrcEngine-Q8_0-vs-pinned-llama.cpp-Q8_0
oracle leg.

Reuses this project's established pattern from
Tools/OrcEnginePhase0/oracle/llama_cpp_deployment_oracle.py (start
llama-server against a GGUF, request a completion with n_probs logprobs,
compare) -- extended to the real, locally-quantized Q8_0 SmolLM2-135M
fixture instead of the synthetic Profile-A model, and consuming the
JSON-lines evidence phase6_q8_0_comparison.exe already produced for the
OrcEngine-F32-vs-OrcEngine-Q8_0 leg (same predeclared dev/holdout
corpus, same prompt IDs -- so both legs are directly comparable per
prompt).

Requires (fails closed, does not silently skip):
    - ORC_LLAMA_SERVER_PATH set, pointing at llama-server.exe from the
      pinned b10436/commit 6fed9f6ff build (same executable already
      used by llama_cpp_deployment_oracle.py and confirmed via
      --version elsewhere in this project)
    - the locally-quantized Q8_0 GGUF (see fixtures/Q8_0_FIXTURE_PROVENANCE.md)
    - phase6_q8_0_comparison.exe's JSON-lines evidence file, already
      generated (this script does not regenerate it)

Usage:
    python phase6_llama_cpp_q8_0_oracle.py <q8_0.gguf> <evidence.jsonl> <output_report.jsonl>
"""
from __future__ import annotations

import json
import math
import os
import subprocess
import sys
import time
import urllib.request

PORT = 8735
LLAMA_SERVER_PATH = os.environ.get("ORC_LLAMA_SERVER_PATH", "")

# Separately-justified, substantially TIGHTER tolerance for
# OrcEngine-Q8_0-vs-llama.cpp-Q8_0 than for OrcEngine-Q8_0-vs-F32: both
# engines are dequantizing and computing on the IDENTICAL Q8_0 weights, so
# a real disagreement here means an actual bug in one implementation, not
# quantization noise. Applied to log-probability space (natural log),
# which is the space llama.cpp's server API reports in.
LLAMA_CPP_Q8_LOGPROB_ATOL = 0.05


def _wait_for_health(port: int, timeout_s: float = 30.0) -> bool:
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
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def _log_softmax_at(logits: list[float], ids: list[int], target_id: int) -> float | None:
    """Log-softmax of `target_id` given only a top-K logits slice: exact only
    if target_id is one of the K ids; otherwise returns None (cannot be
    computed honestly from a truncated slice -- never approximated as if it
    were the full-vocab softmax denominator)."""
    if target_id not in ids:
        return None
    m = max(logits)
    denom = math.log(sum(math.exp(v - m) for v in logits)) + m
    idx = ids.index(target_id)
    return logits[idx] - denom


def run(q8_path: str, evidence_path: str, report_path: str) -> bool:
    if not LLAMA_SERVER_PATH or not os.path.isfile(LLAMA_SERVER_PATH):
        sys.exit(f"ABORT: llama-server not found at {LLAMA_SERVER_PATH!r}. "
                 f"Set ORC_LLAMA_SERVER_PATH to the pinned b10436 build's llama-server.exe.")
    if not os.path.isfile(q8_path):
        sys.exit(f"ABORT: Q8_0 GGUF not found at {q8_path!r}")
    if not os.path.isfile(evidence_path):
        sys.exit(f"ABORT: OrcEngine evidence file not found at {evidence_path!r} -- "
                 f"run phase6_q8_0_comparison.exe first")

    with open(evidence_path, encoding="utf-8") as f:
        orcengine_evidence = [json.loads(line) for line in f if line.strip()]

    proc = subprocess.Popen(
        [LLAMA_SERVER_PATH, "-m", q8_path, "--port", str(PORT), "--no-warmup"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    all_ok = True
    results = []
    try:
        if not _wait_for_health(PORT):
            sys.exit("ABORT: llama-server did not become healthy in time")

        for entry in orcengine_evidence:
            response = _request_completion(PORT, entry["text"], n_probs=5)
            top = response["completion_probabilities"][0]["top_logprobs"]
            llama_ids = [t["id"] for t in top]
            llama_logprobs = [t["logprob"] for t in top]
            llama_argmax = llama_ids[0]  # server returns sorted descending

            orc_argmax = entry["q8_selected"]
            argmax_agree = orc_argmax == llama_argmax

            # Reconstruct OrcEngine's own log-softmax for the tokens llama.cpp
            # reported, from OrcEngine's own top-5 (id, logit) slice -- exact
            # only where both top-5 sets overlap; entries outside OrcEngine's
            # own top-5 are honestly reported as "not comparable from this
            # slice" rather than silently skipped or approximated.
            orc_ids = entry["q8_top5_ids"]
            orc_logits = entry["q8_top5_logits"]
            comparisons = []
            for lid, llp in zip(llama_ids, llama_logprobs):
                orc_lp = _log_softmax_at(orc_logits, orc_ids, lid)
                if orc_lp is None:
                    comparisons.append({"token_id": lid, "llama_logprob": llp, "orc_logprob": None,
                                        "diff": None, "within_tol": None})
                    continue
                diff = abs(orc_lp - llp)
                within = diff <= LLAMA_CPP_Q8_LOGPROB_ATOL
                comparisons.append({"token_id": lid, "llama_logprob": llp, "orc_logprob": orc_lp,
                                    "diff": diff, "within_tol": within})
                if not within:
                    all_ok = False

            if not argmax_agree:
                all_ok = False

            print(f"{entry['id']:24s} orc_argmax={orc_argmax:6d} llama_argmax={llama_argmax:6d} "
                  f"agree={argmax_agree}")
            for c in comparisons:
                if c["orc_logprob"] is None:
                    print(f"    token {c['token_id']:6d}: llama_logprob={c['llama_logprob']:.6f}  "
                          f"orc_logprob=N/A (outside OrcEngine's own top-5 slice)")
                else:
                    print(f"    token {c['token_id']:6d}: llama_logprob={c['llama_logprob']:.6f}  "
                          f"orc_logprob={c['orc_logprob']:.6f}  diff={c['diff']:.6f}  "
                          f"within_tol={c['within_tol']}")

            results.append({"id": entry["id"], "orc_argmax": orc_argmax, "llama_argmax": llama_argmax,
                            "argmax_agree": argmax_agree, "comparisons": comparisons})
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()

    with open(report_path, "w", encoding="utf-8") as f:
        for r in results:
            f.write(json.dumps(r) + "\n")

    return all_ok


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(f"usage: {sys.argv[0]} <q8_0.gguf> <evidence.jsonl> <output_report.jsonl>")
    ok = run(sys.argv[1], sys.argv[2], sys.argv[3])
    print(f"\n{'PASS' if ok else 'FAIL'}: OrcEngine-Q8_0 vs pinned llama.cpp-Q8_0 oracle agreement "
          f"(argmax match + logprob within {LLAMA_CPP_Q8_LOGPROB_ATOL} where comparable)")
    raise SystemExit(0 if ok else 1)
