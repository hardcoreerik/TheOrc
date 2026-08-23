# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1 Codex remediation, Gate 4: paired four-way evidence.

Runs the pinned, hash-verified llama-server against BOTH the pinned F32
GGUF and the pinned Q8_0 GGUF, using the SAME verified server binary,
implementation DLL, prompts, token IDs, and neutral completion contract
(oracle.build_completion_payload) for every one of the 7 corpus prompts
-- not just the 3 that previously disagreed. Produces one durable,
auditable table:

    Prompt | Orc F32 | Orc Q8_0 | llama F32 | llama Q8_0 |
    Orc internal F32/Q8 agree? | External F32 agree? | External Q8 agree?

so the relationship between the pre-existing F32 cross-engine
divergence and any Q8_0-specific contribution can be read directly,
instead of inferred from two separate, differently-scoped runs.

Does NOT invent an external log-probability tolerance and does NOT
describe top-5 evidence as full-distribution equivalence -- log-
probability comparisons (where computable from the OrcEngine top-5
slice using the correct full-vocabulary logsumexp for the matching
precision) are reported as diagnostic data only, with "N/A" wherever
the required raw logit is not present, exactly as the Q8 oracle and F32
localizer already do.

Usage:
    python phase6_paired_four_way_evidence.py \
        --server PATH_TO_llama-server.exe \
        --f32-gguf PATH_TO_F32.gguf \
        --q8-gguf PATH_TO_Q8_0.gguf \
        --evidence PATH_TO_evidence_v3.jsonl \
        --report PATH_TO_output_report.jsonl
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys

sys.path.insert(0, os.path.dirname(__file__))
import phase6_llama_cpp_q8_0_oracle as oracle  # noqa: E402

MIN_SCHEMA_VERSION_FOR_PAIRED_EVIDENCE = 3  # requires f32_full_vocab_logsumexp


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default=os.environ.get("ORC_LLAMA_SERVER_PATH", ""))
    parser.add_argument("--f32-gguf", required=True)
    parser.add_argument("--q8-gguf", required=True)
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--report", required=True)
    args = parser.parse_args()
    if not args.server:
        parser.error("--server is required (or set ORC_LLAMA_SERVER_PATH)")
    return args


def _validate_v3_evidence(entries: list[dict]) -> None:
    oracle._validate_evidence_schema(entries)
    for entry in entries:
        if entry.get("schema_version", 0) < MIN_SCHEMA_VERSION_FOR_PAIRED_EVIDENCE:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} has schema_version="
                     f"{entry.get('schema_version')!r}, required >= "
                     f"{MIN_SCHEMA_VERSION_FOR_PAIRED_EVIDENCE} (f32_full_vocab_logsumexp). "
                     f"Regenerate evidence with the current phase6_q8_0_comparison.exe.")
        if "f32_full_vocab_logsumexp" not in entry:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} is missing "
                     f"f32_full_vocab_logsumexp -- malformed v3 evidence.")


def _run_external_leg(server_path: str, gguf_path: str, entries: list[dict], label: str) -> dict[str, dict]:
    """Runs every prompt in `entries` against the given GGUF through the
    pinned server, using the shared neutral payload, and returns
    {prompt_id: {"argmax": int, "top5": [...], "token_ids_match": bool}}."""
    port = oracle._free_local_port()
    proc = subprocess.Popen([server_path, "-m", gguf_path, "--port", str(port), "--no-warmup"],
                            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    results: dict[str, dict] = {}
    try:
        oracle._wait_for_health(proc, port)
        print(f"[{label}] llama-server healthy on port {port} (pid {proc.pid})")
        for entry in entries:
            llama_token_ids = oracle._request_tokenize(port, entry["text"])
            token_ids_match = llama_token_ids == entry["token_ids"]
            if not token_ids_match:
                sys.exit(f"ABORT: [{label}] token-ID mismatch for {entry['id']!r}: "
                         f"OrcEngine={entry['token_ids']} llama.cpp={llama_token_ids}")
            response = oracle._request_completion(port, entry["text"], n_probs=5)
            top = response["completion_probabilities"][0]["top_logprobs"]
            results[entry["id"]] = {
                "argmax": top[0]["id"], "top5": top, "token_ids_match": token_ids_match,
            }
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)
    return results


def _write_report(report_path: str, rows: list[dict]) -> None:
    try:
        with open(report_path, "w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(r) + "\n")
            f.flush()
            os.fsync(f.fileno())
    except OSError as ex:
        sys.exit(f"ABORT: failed to write paired evidence report to {report_path!r}: {ex}")


def run(server_path: str, f32_gguf: str, q8_gguf: str, evidence_path: str, report_path: str) -> list[dict]:
    if not os.path.isfile(evidence_path):
        sys.exit(f"ABORT: evidence file not found at {evidence_path!r}")
    with open(evidence_path, encoding="utf-8") as f:
        entries = [json.loads(line) for line in f if line.strip()]
    _validate_v3_evidence(entries)

    oracle._verify_server_identity(server_path)
    oracle._verify_f32_gguf_identity(f32_gguf, entries)
    oracle._verify_q8_gguf_identity(q8_gguf, entries)

    f32_external = _run_external_leg(server_path, f32_gguf, entries, "F32")
    q8_external = _run_external_leg(server_path, q8_gguf, entries, "Q8_0")

    rows = []
    print(f"\n{'Prompt':24s} {'Orc F32':>9s} {'Orc Q8':>9s} {'llama F32':>10s} {'llama Q8':>9s} "
          f"{'IntAgree':>9s} {'ExtF32Agree':>12s} {'ExtQ8Agree':>11s}")
    for entry in entries:
        pid = entry["id"]
        orc_f32 = entry["f32_selected"]
        orc_q8 = entry["q8_selected"]
        llama_f32 = f32_external[pid]["argmax"]
        llama_q8 = q8_external[pid]["argmax"]
        internal_agree = orc_f32 == orc_q8
        external_f32_agree = orc_f32 == llama_f32
        external_q8_agree = orc_q8 == llama_q8

        print(f"{pid:24s} {orc_f32:9d} {orc_q8:9d} {llama_f32:10d} {llama_q8:9d} "
              f"{str(internal_agree):>9s} {str(external_f32_agree):>12s} {str(external_q8_agree):>11s}")

        rows.append({
            "id": pid, "orc_f32_selected": orc_f32, "orc_q8_selected": orc_q8,
            "llama_f32_argmax": llama_f32, "llama_q8_argmax": llama_q8,
            "orc_internal_f32_q8_agree": internal_agree,
            "external_f32_agree": external_f32_agree, "external_q8_agree": external_q8_agree,
            "llama_f32_top5": f32_external[pid]["top5"], "llama_q8_top5": q8_external[pid]["top5"],
            "f32_full_vocab_logsumexp": entry["f32_full_vocab_logsumexp"],
            "q8_full_vocab_logsumexp": entry["q8_full_vocab_logsumexp"],
            "orc_f32_top5_ids": entry["f32_top5_ids"], "orc_f32_top5_logits": entry["f32_top5_logits"],
            "orc_q8_top5_ids": entry["q8_top5_ids"], "orc_q8_top5_logits": entry["q8_top5_logits"],
        })

    _write_report(report_path, rows)
    return rows


if __name__ == "__main__":
    args = _parse_args()
    result_rows = run(args.server, args.f32_gguf, args.q8_gguf, args.evidence, args.report)
    print(f"\nDONE: paired four-way evidence collected for {len(result_rows)} prompts, "
          f"written to {args.report!r}")
    raise SystemExit(0)
