# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Commit B: bounded three-authority F32 diagnostic.

Answers: on the three F32-divergent prompts (dev_year_weather,
holdout_she_walked, holdout_quick_fox), does the pinned PyTorch
source-model authority (real HuggingFace `transformers`
LlamaForCausalLM, the actual upstream implementation, not anything this
project wrote) agree with OrcEngine or with llama.cpp? Also runs the 4
control prompts where OrcEngine and llama.cpp already agree, through
the exact same code path, so the divergent-vs-control comparison is
apples to apples.

This is a READ-ONLY diagnostic. It does not change transformer math,
does not touch OrcEngine's or the Phase 0 NumPy oracle's forward-pass
code, and does not widen any tolerance. Numerical differences are
reported as diagnostic data; only exact selected-token (argmax)
agreement is treated as a categorical fact.

Established pinned-authority provenance (see PHASE6_THREE_AUTHORITY_STATUS.md
for the full chain; summarized here):
  - Source: HuggingFaceTB/SmolLM2-135M, revision
    93efa2f097d58c2a74874c7e644dbc9b0cee75a2 (oracle/download_candidate.py).
  - Local artifact: Tools/OrcEnginePhase0/artifacts/smollm2-135m/
    (config.json, model.safetensors, tokenizer files).
  - model.safetensors SHA-256: 80521b40281d6ce74e35c9282c22539e75aa0ac
    8578892b2a59955ef78d55da1 -- matches "source_safetensors_sha256" in
    the already-committed Tools/OrcEnginePhase0/artifacts/
    real_candidate_conversion_manifest.json.
  - That SAME manifest's "output_gguf_sha256" is
    fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac --
    the EXACT pinned F32 GGUF hash used throughout Phase 6, proving this
    HF directory is genuinely the source of the F32 GGUF being compared,
    not merely a same-named model from elsewhere.
  - This directory was ALREADY used as a real (not synthetic) PyTorch
    ground-truth authority once before, in oracle/hf_reference_check.py
    (OE-ADR-017, 2026-08-15): our own oracle matched it exactly on 10
    disputed tokens; llama.cpp was the outlier there. This script reuses
    the same "load real HF weights, eval(), no_grad(), float32" pattern
    but (a) targets OrcEngine instead of the Phase 0 NumPy oracle, (b)
    passes the exact committed token IDs directly instead of invoking
    the tokenizer (Gate 10 item 9 -- a matching prompt STRING is not
    proof of identical model input), and (c) covers all 7 Phase 6
    prompts, not just one.

No BOS/EOS or other special token is inserted (this script does not
call the tokenizer at all); position IDs are HF's default 0..N-1 for a
single non-cached forward pass over the full prompt, matching
OrcEngine's own forward_cached_step(..., start_position=0) convention
for these prompts.

Usage:
    python phase6_three_authority_f32_diagnostic.py \
        --hf-model-dir PATH_TO_smollm2-135m_HF_DIR \
        --manifest PATH_TO_real_candidate_conversion_manifest.json \
        --f32-gguf PATH_TO_smollm2-135m.gguf \
        --evidence PATH_TO_evidence_v3.jsonl \
        --paired-report PATH_TO_phase6_paired_four_way_report.jsonl \
        --report PATH_TO_output_report.jsonl

Codex remediation round 4, Gate 3: --manifest and --f32-gguf are new,
required arguments -- the original version of this script hashed
model.safetensors against a hardcoded constant only, never opened the
already-committed provenance manifest at runtime, and never hashed the
actual F32 GGUF used for the comparison, which overclaimed what the
program's success message actually verified. Both are now genuinely
opened/hashed and cross-checked (manifest <-> actual files <->
independently pinned constants <-> evidence rows) before any model is
loaded.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import math
import os
import sys

EXPECTED_SOURCE_SAFETENSORS_SHA256 = "80521b40281d6ce74e35c9282c22539e75aa0ac8578892b2a59955ef78d55da1"
EXPECTED_F32_GGUF_SHA256 = "fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac"

DIVERGENT_PROMPT_IDS = ("dev_year_weather", "holdout_she_walked", "holdout_quick_fox")
CONTROL_PROMPT_IDS = ("dev_capital_of_france", "dev_once_upon_a_time", "dev_code_snippet", "holdout_hello_world")
ALL_SEVEN_PROMPT_IDS = CONTROL_PROMPT_IDS[:3] + (DIVERGENT_PROMPT_IDS[0],) + \
    (CONTROL_PROMPT_IDS[3],) + DIVERGENT_PROMPT_IDS[1:]  # canonical corpus order, unused beyond documentation


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--hf-model-dir", required=True,
                        help="Path to the pinned local HF SmolLM2-135M directory (config.json/model.safetensors)")
    parser.add_argument("--manifest", required=True,
                        help="Path to Tools/OrcEnginePhase0/artifacts/real_candidate_conversion_manifest.json")
    parser.add_argument("--f32-gguf", required=True,
                        help="Path to the actual F32 GGUF used by OrcEngine/llama.cpp for this comparison")
    parser.add_argument("--evidence", required=True)
    parser.add_argument("--paired-report", required=True)
    parser.add_argument("--report", required=True)
    return parser.parse_args()


def _load_and_validate_manifest(manifest_path: str) -> dict:
    """Codex remediation round 4, Gate 3: the ORIGINAL _verify_pytorch_
    authority() never actually opened real_candidate_conversion_
    manifest.json at runtime -- it only checked model.safetensors
    against a hardcoded constant and cited the manifest in comments/
    messages, which overclaimed what the program verified. This
    function genuinely opens and parses it, requiring the expected hash
    fields to be present, non-null STRINGS (not merely "truthy")."""
    if not os.path.isfile(manifest_path):
        sys.exit(f"ABORT: provenance manifest not found at {manifest_path!r} -- cannot establish the "
                 f"PyTorch source-model authority (Outcome P4).")
    try:
        with open(manifest_path, encoding="utf-8") as f:
            manifest = json.load(f)
    except json.JSONDecodeError as ex:
        sys.exit(f"ABORT: provenance manifest {manifest_path!r} is malformed JSON: {ex} (Outcome P4).")
    for field in ("source_safetensors_sha256", "output_gguf_sha256"):
        value = manifest.get(field)
        if not isinstance(value, str) or not value:
            sys.exit(f"ABORT: provenance manifest {manifest_path!r} field {field!r} is missing, null, "
                     f"empty, or not a string (got {value!r}) -- refusing to trust an incomplete "
                     f"manifest (Outcome P4).")
    return manifest


def _verify_pytorch_authority(hf_model_dir: str, manifest_path: str, f32_gguf_path: str,
                              evidence_entries: list[dict]) -> dict:
    """Fails closed (sys.exit), BEFORE any model is loaded/executed,
    unless: the manifest is genuinely opened and complete; the actual
    local model.safetensors hash matches BOTH the manifest's own
    source_safetensors_sha256 AND the independently pinned expected
    constant; and the actual F32 GGUF passed for this comparison run
    hashes to the manifest's own output_gguf_sha256, the independently
    pinned expected constant, AND every applicable evidence row's own
    recorded f32_artifact_sha256. Returns the loaded manifest (used to
    record identity in the durable report)."""
    manifest = _load_and_validate_manifest(manifest_path)

    safetensors_path = os.path.join(hf_model_dir, "model.safetensors")
    if not os.path.isfile(safetensors_path):
        sys.exit(f"ABORT: model.safetensors not found at {safetensors_path!r} (Outcome P4).")
    actual_safetensors_hash = _sha256_file(safetensors_path)
    if actual_safetensors_hash != manifest["source_safetensors_sha256"]:
        sys.exit(f"ABORT: model.safetensors SHA-256 {actual_safetensors_hash} does not match the "
                 f"manifest's own source_safetensors_sha256 {manifest['source_safetensors_sha256']!r} "
                 f"-- refusing an unverified weight file (Outcome P4).")
    if actual_safetensors_hash != EXPECTED_SOURCE_SAFETENSORS_SHA256:
        sys.exit(f"ABORT: model.safetensors SHA-256 {actual_safetensors_hash} does not match the "
                 f"independently pinned expected constant {EXPECTED_SOURCE_SAFETENSORS_SHA256} -- the "
                 f"manifest and this script's own pin have diverged (Outcome P4).")

    if not os.path.isfile(f32_gguf_path):
        sys.exit(f"ABORT: F32 GGUF not found at {f32_gguf_path!r} (Outcome P4).")
    actual_gguf_hash = _sha256_file(f32_gguf_path)
    if actual_gguf_hash != manifest["output_gguf_sha256"]:
        sys.exit(f"ABORT: F32 GGUF SHA-256 {actual_gguf_hash} does not match the manifest's own "
                 f"output_gguf_sha256 {manifest['output_gguf_sha256']!r} -- this diagnostic would be "
                 f"comparing PyTorch against a DIFFERENT F32 GGUF than the one the manifest attests was "
                 f"produced from the verified source weights (Outcome P4).")
    if actual_gguf_hash != EXPECTED_F32_GGUF_SHA256:
        sys.exit(f"ABORT: F32 GGUF SHA-256 {actual_gguf_hash} does not match the independently pinned "
                 f"expected constant {EXPECTED_F32_GGUF_SHA256} (Outcome P4).")
    for entry in evidence_entries:
        evidence_hash = entry.get("f32_artifact_sha256")
        if evidence_hash is not None and evidence_hash != actual_gguf_hash:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} records f32_artifact_sha256="
                     f"{evidence_hash!r}, which does not match the verified F32 GGUF hash "
                     f"{actual_gguf_hash} -- evidence was generated against a different file "
                     f"(Outcome P4).")

    config_path = os.path.join(hf_model_dir, "config.json")
    if not os.path.isfile(config_path):
        sys.exit(f"ABORT: config.json not found at {config_path!r} (Outcome P4).")
    with open(config_path, encoding="utf-8") as f:
        config = json.load(f)
    if config.get("architectures") != ["LlamaForCausalLM"] or config.get("model_type") != "llama":
        sys.exit(f"ABORT: config.json does not describe a LlamaForCausalLM model as expected: "
                 f"{config.get('architectures')!r}/{config.get('model_type')!r} (Outcome P4).")

    print(f"PyTorch authority verified: manifest {manifest_path!r} opened and parsed; "
          f"model.safetensors SHA-256 {actual_safetensors_hash} matches BOTH the manifest's "
          f"source_safetensors_sha256 AND the independently pinned constant; F32 GGUF SHA-256 "
          f"{actual_gguf_hash} matches the manifest's output_gguf_sha256, the independently pinned "
          f"constant, AND every applicable evidence row.")
    return manifest


def _load_pytorch_model(hf_model_dir: str):
    import torch
    from transformers import AutoModelForCausalLM

    torch.use_deterministic_algorithms(True)
    model = AutoModelForCausalLM.from_pretrained(hf_model_dir, dtype=torch.float32)
    model.eval()  # Gate 10 item 6
    return model, torch


def _pytorch_logits_for_prompt(model, torch_module, token_ids: list[int]):
    """No tokenizer invoked (Gate 10 item 9): token_ids are passed
    directly as input_ids. No BOS/EOS inserted (item 10): this function
    does not add any token to the caller's list. Position IDs are HF's
    default 0..N-1 for a fresh (non-cached) forward pass, matching
    OrcEngine's own start_position=0 convention for these prompts (item
    11). Returns the FINAL prompt position's full-vocabulary logits as a
    float64 list (item 12)."""
    input_ids = torch_module.tensor([token_ids], dtype=torch_module.long)
    with torch_module.no_grad():  # Gate 10 item 7
        out = model(input_ids=input_ids)  # item 8: no sampling, raw logits only
    final_logits = out.logits[0, -1].to(torch_module.float64)
    return final_logits.tolist()


def _full_vocab_logsumexp(logits: list[float]) -> float:
    m = max(logits)
    return math.log(sum(math.exp(v - m) for v in logits)) + m


def _top_k(logits: list[float], k: int) -> list[tuple[int, float]]:
    indexed = sorted(range(len(logits)), key=lambda i: logits[i], reverse=True)[:k]
    return [(i, logits[i]) for i in indexed]


def _load_exact_corpus(path: str, label: str) -> dict[str, dict]:
    """Codex remediation round 4, Gate 3: fails closed on duplicate,
    missing, empty, or unexpected prompt IDs BEFORE building the
    {id: row} dict -- the ORIGINAL code built the dict via a
    comprehension that would silently let a later duplicate entry
    overwrite an earlier one. Reuses the same validation shape as
    phase6_paired_four_way_evidence.py's _validate_paired_corpus()."""
    with open(path, encoding="utf-8") as f:
        rows = [json.loads(line) for line in f if line.strip()]

    ids = [r.get("id") for r in rows]
    empty = [i for i, pid in enumerate(ids) if not pid]
    if empty:
        sys.exit(f"ABORT: {label} contains {len(empty)} row(s) with a missing/empty 'id' at index/indices "
                 f"{empty}.")

    expected = set(CONTROL_PROMPT_IDS) | set(DIVERGENT_PROMPT_IDS)
    id_set = set(ids)
    missing = expected - id_set
    if missing:
        sys.exit(f"ABORT: {label} is missing expected prompt ID(s): {sorted(missing)}")
    if len(ids) != len(id_set):
        seen, duplicated = set(), set()
        for pid in ids:
            (duplicated if pid in seen else seen).add(pid)
        duplicated_relevant = duplicated & expected
        if duplicated_relevant:
            sys.exit(f"ABORT: {label} contains duplicate entries for expected prompt ID(s): "
                     f"{sorted(duplicated_relevant)}")

    return {pid: r for pid, r in zip(ids, rows) if pid in expected}


def run(hf_model_dir: str, manifest_path: str, f32_gguf_path: str, evidence_path: str,
        paired_report_path: str, report_path: str) -> list[dict]:
    if not os.path.isfile(evidence_path):
        sys.exit(f"ABORT: evidence file not found at {evidence_path!r}")
    if not os.path.isfile(paired_report_path):
        sys.exit(f"ABORT: paired four-way report not found at {paired_report_path!r} -- run "
                 f"phase6_paired_four_way_evidence.py first.")

    evidence_by_id = _load_exact_corpus(evidence_path, "evidence")
    paired_by_id = _load_exact_corpus(paired_report_path, "paired report")

    manifest = _verify_pytorch_authority(hf_model_dir, manifest_path, f32_gguf_path,
                                         list(evidence_by_id.values()))
    model, torch_module = _load_pytorch_model(hf_model_dir)

    import transformers as transformers_module
    run_identity = {
        "pytorch_version": torch_module.__version__,
        "transformers_version": transformers_module.__version__,
        "hf_model_dir": os.path.abspath(hf_model_dir),
        "manifest_path": os.path.abspath(manifest_path),
        "manifest_source_safetensors_sha256": manifest["source_safetensors_sha256"],
        "manifest_output_gguf_sha256": manifest["output_gguf_sha256"],
        "verified_f32_gguf_sha256": EXPECTED_F32_GGUF_SHA256,
    }

    rows = []
    print(f"\n{'Prompt':24s} {'group':10s} {'PT':>7s} {'Orc':>7s} {'llama':>7s} "
          f"{'PT==Orc':>8s} {'PT==llama':>10s}")
    for prompt_id in list(CONTROL_PROMPT_IDS) + list(DIVERGENT_PROMPT_IDS):
        evidence = evidence_by_id[prompt_id]
        paired = paired_by_id[prompt_id]
        token_ids = evidence["token_ids"]

        pt_logits = _pytorch_logits_for_prompt(model, torch_module, token_ids)
        pt_logsumexp = _full_vocab_logsumexp(pt_logits)
        pt_top10 = _top_k(pt_logits, 10)
        pt_argmax = pt_top10[0][0]

        orc_f32_selected = evidence["f32_selected"]
        llama_f32_argmax = paired["llama_f32_argmax"]
        group = "divergent" if prompt_id in DIVERGENT_PROMPT_IDS else "control"

        pt_agrees_with_orc = pt_argmax == orc_f32_selected
        pt_agrees_with_llama = pt_argmax == llama_f32_argmax

        print(f"{prompt_id:24s} {group:10s} {pt_argmax:7d} {orc_f32_selected:7d} {llama_f32_argmax:7d} "
              f"{str(pt_agrees_with_orc):>8s} {str(pt_agrees_with_llama):>10s}")

        # Diagnostic comparisons where directly comparable: for every
        # token ID present in PyTorch's own top-10, ALSO look it up in
        # OrcEngine's top-5 (raw logit -> log-prob via its own recorded
        # f32_full_vocab_logsumexp) and llama.cpp's top-5
        # (already-normalized log-probs) -- exact wherever comparable,
        # "N/A" (never approximated) otherwise.
        orc_top5_ids = evidence["f32_top5_ids"]
        orc_top5_logits = evidence["f32_top5_logits"]
        orc_logsumexp = evidence["f32_full_vocab_logsumexp"]
        llama_top5 = {t["id"]: t["logprob"] for t in paired["llama_f32_top5"]}

        comparisons = []
        abs_diffs_vs_orc, abs_diffs_vs_llama = [], []
        for tid, pt_logit in pt_top10:
            pt_logprob = pt_logit - pt_logsumexp
            orc_logprob = None
            if tid in orc_top5_ids:
                orc_raw_logit = orc_top5_logits[orc_top5_ids.index(tid)]
                orc_logprob = orc_raw_logit - orc_logsumexp
                abs_diffs_vs_orc.append(abs(pt_logprob - orc_logprob))
            llama_logprob = llama_top5.get(tid)
            if llama_logprob is not None:
                abs_diffs_vs_llama.append(abs(pt_logprob - llama_logprob))
            comparisons.append({
                "token_id": tid, "pt_logprob": pt_logprob,
                "orc_logprob": orc_logprob, "llama_logprob": llama_logprob,
            })

        max_abs_error_vs_orc = max(abs_diffs_vs_orc) if abs_diffs_vs_orc else None
        rmse_vs_orc = math.sqrt(sum(d * d for d in abs_diffs_vs_orc) / len(abs_diffs_vs_orc)) if abs_diffs_vs_orc else None
        max_abs_error_vs_llama = max(abs_diffs_vs_llama) if abs_diffs_vs_llama else None
        rmse_vs_llama = math.sqrt(sum(d * d for d in abs_diffs_vs_llama) / len(abs_diffs_vs_llama)) if abs_diffs_vs_llama else None

        rows.append({
            "id": prompt_id, "group": group, "token_ids": token_ids,
            "pytorch_selected": pt_argmax, "orc_f32_selected": orc_f32_selected,
            "llama_f32_argmax": llama_f32_argmax,
            "pytorch_agrees_with_orc": pt_agrees_with_orc, "pytorch_agrees_with_llama": pt_agrees_with_llama,
            "pytorch_full_vocab_logsumexp": pt_logsumexp,
            "pytorch_top10": [{"id": i, "logit": v, "logprob": v - pt_logsumexp} for i, v in pt_top10],
            "comparisons_where_directly_comparable": comparisons,
            "numerical_error_scope": "overlapping top-k tokens only (PyTorch top-10 intersected with "
                                     "OrcEngine top-5 / llama.cpp top-5) -- NOT a full-vocabulary "
                                     "equality proof",
            "max_abs_error_vs_orc": max_abs_error_vs_orc, "rmse_vs_orc": rmse_vs_orc,
            "max_abs_error_vs_llama": max_abs_error_vs_llama, "rmse_vs_llama": rmse_vs_llama,
            "llama_controlled_server_args": paired.get("controlled_server_args"),
            "run_identity": run_identity,
        })

    try:
        with open(report_path, "w", encoding="utf-8") as f:
            for r in rows:
                f.write(json.dumps(r) + "\n")
            f.flush()
            os.fsync(f.fileno())
    except OSError as ex:
        sys.exit(f"ABORT: failed to write report to {report_path!r}: {ex}")

    return rows


if __name__ == "__main__":
    args = _parse_args()
    result_rows = run(args.hf_model_dir, args.manifest, args.f32_gguf, args.evidence,
                      args.paired_report, args.report)
    print(f"\nDONE: three-authority F32 diagnostic collected for {len(result_rows)} prompts, "
          f"written to {args.report!r}")
    raise SystemExit(0)
