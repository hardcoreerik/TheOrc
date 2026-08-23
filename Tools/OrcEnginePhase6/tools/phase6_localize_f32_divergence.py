# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 3 localization (Codex remediation round 2, Gate 3): checks
whether the 3 prompts on which the corrected pinned Q8-vs-Q8 oracle
disagreed with OrcEngine (dev_year_weather, holdout_she_walked,
holdout_quick_fox) ALSO disagree at full F32 precision (no Q8_0
quantization involved). This is causal-localization evidence, so it is
now held to the SAME identity/fail-closed standard as the Q8 oracle
itself -- earlier versions of this script trusted a hardcoded server
path with no hash/version verification and downgraded a missing
implementation DLL to a warning; both are fixed here by reusing the
oracle module's identity-verification functions directly rather than
maintaining a second, weaker copy.

IMPORTANT -- what this script's result does and does NOT prove: an F32
disagreement on the same prompt IDs shows a pre-existing OrcEngine-vs-
llama.cpp F32 forward-path divergence exists on those prompts. It does
NOT, by itself, prove Q8_0 contributes nothing -- the llama.cpp-selected
token can differ between the F32 leg and the Q8_0 leg even when both
disagree with OrcEngine (see Gate 4's four-way evidence for that
comparison). Do not over-read this script's output as clearing Q8_0.

Usage:
    python phase6_localize_f32_divergence.py \
        --server PATH_TO_llama-server.exe \
        --f32-gguf PATH_TO_F32.gguf \
        --evidence PATH_TO_evidence.jsonl \
        --report PATH_TO_output_report.jsonl

--server may be omitted if ORC_LLAMA_SERVER_PATH is set (explicit
environment-variable fallback only for the server path, matching the Q8
oracle's own convention); --f32-gguf/--evidence/--report have no
hardcoded or environment-variable fallback -- they must be passed
explicitly.
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
import phase6_llama_cpp_q8_0_oracle as oracle  # noqa: E402

# The exact, predeclared set of prompt IDs this localization run targets --
# Gate 3 item 7: abort on missing, duplicate, or unexpected target entries,
# rather than silently localizing whatever happens to be in the evidence
# file. This is intentionally the fixed set identified by the Stage 3
# oracle run that motivated this script, not a general-purpose "localize
# everything" tool.
EXPECTED_PROMPT_IDS = ("dev_year_weather", "holdout_she_walked", "holdout_quick_fox")


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=os.environ.get("ORC_LLAMA_SERVER_PATH", ""),
                        help="Path to the pinned llama-server.exe (or set ORC_LLAMA_SERVER_PATH)")
    parser.add_argument("--f32-gguf", required=True, help="Path to the pinned F32 GGUF")
    parser.add_argument("--evidence", required=True,
                        help="Path to the OrcEngine F32-vs-Q8 comparison evidence JSONL (schema_version>=2)")
    parser.add_argument("--report", required=True, help="Path to write the structured localization report JSONL")
    args = parser.parse_args()
    if not args.server:
        parser.error("--server is required (or set ORC_LLAMA_SERVER_PATH)")
    return args


def _select_expected_entries(entries: list[dict]) -> dict[str, dict]:
    """Fails closed on missing, duplicate, or unexpected-count target
    entries -- Gate 3 item 7. Returns {id: entry} for exactly
    EXPECTED_PROMPT_IDS, in that order."""
    by_id: dict[str, list[dict]] = {}
    for e in entries:
        by_id.setdefault(e.get("id"), []).append(e)

    missing = [pid for pid in EXPECTED_PROMPT_IDS if pid not in by_id]
    if missing:
        sys.exit(f"ABORT: evidence is missing expected target prompt ID(s): {missing}")

    duplicated = [pid for pid in EXPECTED_PROMPT_IDS if len(by_id[pid]) > 1]
    if duplicated:
        sys.exit(f"ABORT: evidence contains duplicate entries for target prompt ID(s): {duplicated}")

    return {pid: by_id[pid][0] for pid in EXPECTED_PROMPT_IDS}


def _write_report(report_path: str, results: list[dict]) -> None:
    """Gate 3 item 10: report write failures must be detected, not
    silently swallowed."""
    try:
        with open(report_path, "w", encoding="utf-8") as f:
            for r in results:
                f.write(json.dumps(r) + "\n")
            f.flush()
            os.fsync(f.fileno())
    except OSError as ex:
        sys.exit(f"ABORT: failed to write localization report to {report_path!r}: {ex}")


def run(server_path: str, f32_path: str, evidence_path: str, report_path: str) -> bool:
    if not os.path.isfile(evidence_path):
        sys.exit(f"ABORT: evidence file not found at {evidence_path!r}")
    with open(evidence_path, encoding="utf-8") as f:
        entries = [json.loads(line) for line in f if line.strip()]
    oracle._validate_evidence_schema(entries)
    targets = _select_expected_entries(entries)

    # Same identity standard as the Q8 oracle: hash-pinned server + impl
    # DLL (abort, not warn, if the impl DLL is missing), --version banner
    # checked including its return code, and F32 GGUF hash cross-checked
    # against both the pinned authority and every evidence entry's own
    # recorded f32_artifact_sha256.
    oracle._verify_server_identity(server_path)
    oracle._verify_f32_gguf_identity(f32_path, entries)

    port = oracle._free_local_port()
    proc = subprocess.Popen([server_path, "-m", f32_path, "--port", str(port), "--no-warmup"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    results = []
    try:
        oracle._wait_for_health(proc, port)
        print(f"llama-server healthy on dynamically-chosen port {port} (pid {proc.pid}), F32 GGUF loaded")

        for prompt_id in EXPECTED_PROMPT_IDS:
            e = targets[prompt_id]

            # Gate 3 item 8: abort immediately (not merely report) on a
            # token-ID mismatch -- comparing logits/argmax under a proven
            # input mismatch would be meaningless, and silently continuing
            # risks the mismatch being missed in a large run's output.
            llama_token_ids = oracle._request_tokenize(port, e["text"])
            if llama_token_ids != e["token_ids"]:
                sys.exit(f"ABORT: token-ID mismatch for {prompt_id!r}: "
                         f"OrcEngine={e['token_ids']} llama.cpp={llama_token_ids}")

            # The SHARED, single canonical neutral-greedy payload (Gate 2)
            # -- identical construction to the Q8 oracle's own request, not
            # a second hand-maintained copy.
            response = oracle._request_completion(port, e["text"], n_probs=5)
            top = response["completion_probabilities"][0]["top_logprobs"]
            llama_argmax = top[0]["id"]  # server returns sorted descending
            orc_f32_selected = e["f32_selected"]
            agree = orc_f32_selected == llama_argmax

            print(f"{prompt_id:24s} token_ids_match=True orc_F32_selected={orc_f32_selected:6d} "
                  f"llama_F32_argmax={llama_argmax:6d} agree={agree}")
            print(f"    llama F32 top5 (id, logprob): "
                  f"{[(t['id'], round(t['logprob'], 4)) for t in top]}")
            print(f"    orc   F32 top5 (id, raw logit): "
                  f"{list(zip(e['f32_top5_ids'], [round(x, 4) for x in e['f32_top5_logits']]))}")

            results.append({
                "id": prompt_id, "token_ids_match": True, "orc_f32_selected": orc_f32_selected,
                "llama_f32_argmax": llama_argmax, "agree": agree,
                "llama_f32_top5": top, "orc_f32_top5_ids": e["f32_top5_ids"],
                "orc_f32_top5_logits": e["f32_top5_logits"],
            })
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

    _write_report(report_path, results)

    # Gate 3 item 9: a meaningful exit contract. This script's PURPOSE is
    # to gather diagnostic localization data -- whether F32 agrees or
    # disagrees with llama.cpp is the FINDING being reported, not a script
    # defect, so it does not itself gate success/failure here (unlike the
    # Q8 oracle, where argmax agreement IS the acceptance criterion being
    # tested). Success here means: identity verified, tokenization proven
    # identical, all target prompts processed, report written. All of
    # that held (every sys.exit path above would already have aborted
    # otherwise) if this line is reached.
    return True


if __name__ == "__main__":
    args = _parse_args()
    ok = run(args.server, args.f32_gguf, args.evidence, args.report)
    print(f"\n{'DONE' if ok else 'FAILED'}: F32 localization data collection "
          f"(this reports whether F32 agrees with llama.cpp as a FINDING, not a pass/fail verdict -- "
          f"see the printed per-prompt results and the report file for the actual data)")
    raise SystemExit(0 if ok else 1)
