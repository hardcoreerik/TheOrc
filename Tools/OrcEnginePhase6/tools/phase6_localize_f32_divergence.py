# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 3 stop-condition localization: the corrected pinned
llama.cpp Q8-vs-Q8 oracle (phase6_llama_cpp_q8_0_oracle.py) disagreed
with OrcEngine's own greedy Q8_0 selection on 3 of 7 corpus prompts
(dev_year_weather, holdout_she_walked, holdout_quick_fox). Per this
remediation's mandatory stop condition ("the corrected pinned Q8-vs-Q8
oracle fails"), this script performs one bounded localization step
BEFORE stopping: does the SAME disagreement reproduce at full F32
precision (no Q8_0 quantization involved at all)? If so, the divergence
is not a Phase 6/Q8_0 defect -- it is a pre-existing OrcEngine-vs-
llama.cpp F32 forward-path divergence this corpus happens to expose.

Requests each disagreeing prompt's F32 completion directly from the
pinned llama-server against the pinned F32 GGUF, with all optional
sampling biases neutralized explicitly (repeat_penalty=1.0, top_k=0,
top_p=1.0, min_p=0.0, presence/frequency_penalty=0.0) so a hidden
non-greedy default cannot be mistaken for a genuine model disagreement,
and compares against the OrcEngine F32 selection/top-5 already recorded
in the Checkpoint 3 v2 evidence file.

Usage:
    python phase6_localize_f32_divergence.py
"""
from __future__ import annotations

import json
import os
import subprocess
import sys
import urllib.request

sys.path.insert(0, os.path.dirname(__file__))
import phase6_llama_cpp_q8_0_oracle as oracle  # noqa: E402

SERVER = os.environ.get(
    "ORC_LLAMA_SERVER_PATH", r"C:\Users\hardc\AppData\Local\Temp\llamacpp_test\llama-server.exe")
F32_GGUF = r"F:\Ai\OrchestratorIDE-phase2-gguf\Tools\OrcEnginePhase0\artifacts\smollm2-135m.gguf"
EVIDENCE = os.path.join(os.path.dirname(__file__), "..", "fixtures",
                        "phase6_q8_0_f32_comparison_evidence_v2.jsonl")

DISAGREEING_PROMPT_IDS = {"dev_year_weather", "holdout_she_walked", "holdout_quick_fox"}


def main() -> None:
    with open(EVIDENCE, encoding="utf-8") as f:
        entries = [json.loads(line) for line in f if line.strip()]
    targets = [e for e in entries if e["id"] in DISAGREEING_PROMPT_IDS]

    port = oracle._free_local_port()
    proc = subprocess.Popen([SERVER, "-m", F32_GGUF, "--port", str(port), "--no-warmup"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        oracle._wait_for_health(proc, port)
        for e in targets:
            llama_tok = oracle._request_tokenize(port, e["text"])
            tok_match = llama_tok == e["token_ids"]

            payload = json.dumps({
                "prompt": e["text"], "n_predict": 1, "temperature": 0, "n_probs": 5, "cache_prompt": False,
                "repeat_penalty": 1.0, "top_k": 0, "top_p": 1.0, "min_p": 0.0,
                "presence_penalty": 0.0, "frequency_penalty": 0.0,
            }).encode()
            req = urllib.request.Request(f"http://127.0.0.1:{port}/completion", data=payload,
                                         headers={"Content-Type": "application/json"})
            with urllib.request.urlopen(req, timeout=30) as r:
                resp = json.loads(r.read())
            top = resp["completion_probabilities"][0]["top_logprobs"]
            llama_argmax = top[0]["id"]
            orc_f32_selected = e["f32_selected"]

            print(f"{e['id']:24s} tok_match={tok_match} orc_F32_selected={orc_f32_selected:6d} "
                  f"llama_F32_argmax={llama_argmax:6d} agree={orc_f32_selected == llama_argmax}")
            print(f"    llama F32 top5 (id, logprob): "
                  f"{[(t['id'], round(t['logprob'], 4)) for t in top]}")
            print(f"    orc   F32 top5 (id, raw logit): "
                  f"{list(zip(e['f32_top5_ids'], [round(x, 4) for x in e['f32_top5_logits']]))}")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)


if __name__ == "__main__":
    main()
