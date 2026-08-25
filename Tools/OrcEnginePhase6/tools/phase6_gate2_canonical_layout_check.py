# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Gate 2 (combined Codex/Grok remediation round 7):
fail-closed, reproducible controlled comparison of llama.cpp's
completion against BOTH the EXISTING custom-layout GGUFs and the
CANONICAL (correctly Q/K-permuted) GGUFs, across the full fixed
7-prompt corpus. See
`Tools/OrcEnginePhase6/fixtures/PHASE6_GATE2_QK_LAYOUT_ROOT_CAUSE.md`
for the full writeup and how the canonical GGUFs were produced.

This is a REWRITE of the round-6 prototype, which accepted arbitrary
server/GGUF paths with no identity verification and wrote only
argmax/top-5 results -- it did not durably prove WHICH server or
artifacts produced the report. Reuses phase6_llama_cpp_q8_0_oracle.py's
established identity-verification, server-launch, and request helpers
(does not duplicate a second identity framework -- see
phase6_paired_four_way_evidence.py for the precedent of importing
`oracle` for exactly this reason).

Verifies BEFORE launching any server:
    - llama-server.exe SHA-256 + --version banner (build 10436 / commit
      6fed9f6ff) + llama-server-impl.dll SHA-256 (oracle._verify_server_identity)
    - existing custom F32 GGUF SHA-256 (oracle.EXPECTED_F32_GGUF_SHA256)
    - existing custom Q8_0 GGUF SHA-256 (oracle.EXPECTED_Q8_GGUF_SHA256)
    - canonical F32 GGUF SHA-256 (this module's own pinned authority)
    - canonical Q8_0 GGUF SHA-256 (this module's own pinned authority)
    - the fixed 7-prompt corpus: exact IDs, exact text, exact token IDs
      (matches phase6_paired_four_way_evidence.EXPECTED_PAIRED_CORPUS_IDS)

Fails closed (sys.exit, before or during the run -- never a silent
partial result) on: missing files, missing/null/empty/non-string
hashes, any hash mismatch, wrong server version, unexpected/missing/
duplicate prompt IDs, a token-ID mismatch against the pinned corpus,
server early exit or nonzero exit, missing/malformed response fields,
or an incomplete result row.

The committed JSON report includes every verified identity, the exact
controlled server argument vector, the exact neutral greedy request
payload, the exact pinned converter source commit and HF source
revision, all 4 legs' results for all 7 prompts, and a schema_version.
Console output corresponds exactly to that JSON (both built from the
same in-memory result structure, not two independently-formatted
views).

Usage:
    python phase6_gate2_canonical_layout_check.py \
        --server PATH_TO_llama-server.exe \
        --existing-f32-gguf PATH \
        --existing-q8-gguf PATH \
        --canonical-f32-gguf PATH \
        --canonical-q8-gguf PATH \
        --report PATH_TO_output.json
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
import phase6_llama_cpp_q8_0_oracle as oracle  # noqa: E402

SCHEMA_VERSION = 1

# Pinned during Gate 2B generation (PHASE6_GATE2_QK_LAYOUT_ROOT_CAUSE.md
# section 2B) -- independently recomputed and confirmed identical before
# writing this script (sha256sum, not copied from the doc unverified).
EXPECTED_CANONICAL_F32_GGUF_SHA256 = "aef7f8d471367c711a7e46365619498e0e51a0fa93dca8aa09005dabc19810e7"
EXPECTED_CANONICAL_Q8_GGUF_SHA256 = "dbf0d1f31d3afd0864bb02a916b7e3762728fd616eebd34bd6021c7497def219"

# Exact pinned llama.cpp converter source and HF source identity this
# comparison's canonical artifacts were generated against -- recorded in
# the committed report so a reviewer never has to trust an undated claim.
PINNED_LLAMA_CPP_COMMIT = "6fed9f6ff7a603b124cb8c5864fca6ea879f9f99"
PINNED_LLAMA_CPP_TAG = "b10436"
PINNED_HF_SOURCE = "HuggingFaceTB/SmolLM2-135M"
PINNED_HF_REVISION = "93efa2f097d58c2a74874c7e644dbc9b0cee75a2"

# The fixed 7-prompt corpus (matches phase6_paired_four_way_evidence.py's
# EXPECTED_PAIRED_CORPUS_IDS and phase6_q8_0_f32_comparison_evidence_v3.jsonl
# exactly -- text and token IDs copied from that evidence file, the same
# authority every other Phase 6 external-oracle leg in this project uses).
PROMPTS = {
    "dev_capital_of_france": {"text": "The capital of France is", "token_ids": [504, 3575, 282, 4649, 314]},
    "dev_once_upon_a_time": {"text": "Once upon a time", "token_ids": [6403, 1980, 253, 655]},
    "dev_code_snippet": {"text": "def add(a, b): return", "token_ids": [1604, 803, 24, 81, 28, 278, 727, 1003]},
    "dev_year_weather": {"text": "In 2026, the weather", "token_ids": [788, 216, 34, 32, 34, 38, 28, 260, 3947]},
    "holdout_hello_world": {"text": "Hello world, this is", "token_ids": [19556, 905, 28, 451, 314]},
    "holdout_she_walked": {"text": "She walked into the", "token_ids": [8113, 13197, 618, 260]},
    "holdout_quick_fox": {"text": "The quick brown fox", "token_ids": [504, 2365, 6354, 16438]},
}
EXPECTED_PROMPT_IDS = frozenset(PROMPTS)


def _verify_canonical_gguf_identity(path: str, expected_hash: str, label: str) -> str:
    """Same fail-closed discipline as oracle._verify_gguf_identity, but for
    the canonical artifacts, which have no evidence-file cross-check to
    perform (they were never fed into phase6_q8_0_comparison.exe)."""
    if not os.path.isfile(path):
        sys.exit(f"ABORT: {label} GGUF not found at {path!r}")
    actual_hash = oracle._sha256_file(path)
    if not actual_hash:
        sys.exit(f"ABORT: {label} GGUF hash computation returned empty/falsy result.")
    if actual_hash != expected_hash:
        sys.exit(f"ABORT: {label} GGUF SHA-256 {actual_hash} does not match the pinned authority "
                 f"{expected_hash} -- wrong or regenerated-and-drifted artifact.")
    print(f"{label} GGUF identity verified: {actual_hash}")
    return actual_hash


def _validate_prompt_ids() -> None:
    """Fail closed before any server launches if this module's own
    hardcoded corpus constant has been miscopied -- defensive against
    editing mistakes, not user input."""
    if set(PROMPTS) != EXPECTED_PROMPT_IDS:
        sys.exit("ABORT: internal inconsistency -- PROMPTS keys do not match EXPECTED_PROMPT_IDS.")
    if len(PROMPTS) != 7:
        sys.exit(f"ABORT: PROMPTS has {len(PROMPTS)} entries, expected exactly 7 (the fixed corpus).")
    for pid, p in PROMPTS.items():
        if not p.get("text"):
            sys.exit(f"ABORT: prompt {pid!r} has an empty/missing 'text' field.")
        if not p.get("token_ids"):
            sys.exit(f"ABORT: prompt {pid!r} has an empty/missing 'token_ids' field.")


def _run_leg(server_path: str, gguf_path: str, label: str) -> dict:
    """Runs all 7 corpus prompts against one GGUF through the pinned,
    identity-verified server under the shared controlled numerical
    settings. Fails closed on any token-ID mismatch, malformed response,
    or incomplete result -- never returns a partial dict silently."""
    port = oracle._free_local_port()
    proc = oracle._launch_controlled_server(server_path, gguf_path, port)
    results: dict[str, dict] = {}
    try:
        oracle._wait_for_health(proc, port)
        print(f"[{label}] llama-server healthy on port {port} (pid {proc.pid})")
        for pid, p in PROMPTS.items():
            llama_token_ids = oracle._request_tokenize(port, p["text"])
            if llama_token_ids != p["token_ids"]:
                sys.exit(f"ABORT: [{label}] token-ID mismatch for {pid!r}: "
                         f"expected {p['token_ids']}, got {llama_token_ids}")

            response = oracle._request_completion(port, p["text"], n_probs=5)
            if "completion_probabilities" not in response or not response["completion_probabilities"]:
                sys.exit(f"ABORT: [{label}] malformed /completion response for {pid!r}: {response!r}")
            top = response["completion_probabilities"][0].get("top_logprobs")
            if not top:
                sys.exit(f"ABORT: [{label}] /completion response for {pid!r} has no top_logprobs: {response!r}")
            for t in top:
                if "id" not in t or "logprob" not in t:
                    sys.exit(f"ABORT: [{label}] malformed top_logprobs entry for {pid!r}: {t!r}")

            argmax = top[0]["id"]
            results[pid] = {
                "token_ids_match": True,
                "argmax": argmax,
                "top5": [(t["id"], round(t["logprob"], 6)) for t in top],
            }
            print(f"  [{label}] {pid:24s} token_ids_match=True argmax={argmax:6d} top5={results[pid]['top5']}")
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

    if set(results) != EXPECTED_PROMPT_IDS:
        sys.exit(f"ABORT: [{label}] incomplete result set: got {sorted(results)}, "
                 f"expected {sorted(EXPECTED_PROMPT_IDS)}.")
    return results


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=os.environ.get("ORC_LLAMA_SERVER_PATH", ""))
    parser.add_argument("--existing-f32-gguf", required=True)
    parser.add_argument("--existing-q8-gguf", required=True)
    parser.add_argument("--canonical-f32-gguf", required=True)
    parser.add_argument("--canonical-q8-gguf", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    if not args.server:
        parser.error("--server is required (or set ORC_LLAMA_SERVER_PATH)")

    _validate_prompt_ids()

    print("=== Identity verification (fail closed before any server launch) ===")
    oracle._verify_server_identity(args.server)
    existing_f32_hash = oracle._verify_gguf_identity(
        args.existing_f32_gguf, oracle.EXPECTED_F32_GGUF_SHA256, "f32_artifact_sha256", [], "existing-custom F32")
    existing_q8_hash = oracle._verify_gguf_identity(
        args.existing_q8_gguf, oracle.EXPECTED_Q8_GGUF_SHA256, "q8_artifact_sha256", [], "existing-custom Q8_0")
    canonical_f32_hash = _verify_canonical_gguf_identity(
        args.canonical_f32_gguf, EXPECTED_CANONICAL_F32_GGUF_SHA256, "canonical F32")
    canonical_q8_hash = _verify_canonical_gguf_identity(
        args.canonical_q8_gguf, EXPECTED_CANONICAL_Q8_GGUF_SHA256, "canonical Q8_0")

    legs = [
        ("existing_custom_f32", args.existing_f32_gguf),
        ("existing_custom_q8_0", args.existing_q8_gguf),
        ("canonical_f32", args.canonical_f32_gguf),
        ("canonical_q8_0", args.canonical_q8_gguf),
    ]
    leg_results = {}
    for label, path in legs:
        print(f"\n=== llama.cpp on {label} ===")
        leg_results[label] = _run_leg(args.server, path, label)

    report = {
        "schema_version": SCHEMA_VERSION,
        "identities": {
            "llama_server_exe_sha256": oracle.EXPECTED_SERVER_EXE_SHA256,
            "llama_server_impl_dll_sha256": oracle.EXPECTED_SERVER_IMPL_DLL_SHA256,
            "llama_server_build_marker": oracle.EXPECTED_BUILD_MARKER,
            "llama_server_commit_marker": oracle.EXPECTED_COMMIT_MARKER,
            "existing_custom_f32_gguf_sha256": existing_f32_hash,
            "existing_custom_q8_0_gguf_sha256": existing_q8_hash,
            "canonical_f32_gguf_sha256": canonical_f32_hash,
            "canonical_q8_0_gguf_sha256": canonical_q8_hash,
            "pinned_llama_cpp_converter_commit": PINNED_LLAMA_CPP_COMMIT,
            "pinned_llama_cpp_tag": PINNED_LLAMA_CPP_TAG,
            "pinned_hf_source": PINNED_HF_SOURCE,
            "pinned_hf_revision": PINNED_HF_REVISION,
        },
        "controlled_server_args": list(oracle.CONTROLLED_NUMERICAL_SERVER_ARGS),
        "neutral_greedy_params": oracle.NEUTRAL_GREEDY_PARAMS,
        "prompt_corpus": PROMPTS,
        "results": leg_results,
    }
    with open(args.report, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)
    print(f"\nDONE: written to {args.report!r}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
