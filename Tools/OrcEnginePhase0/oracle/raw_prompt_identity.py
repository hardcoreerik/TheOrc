# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
raw_prompt_identity acceptance check, per PHASE_0_ACCEPTANCE.yaml:
  "raw bytes, rendered template, token IDs, and their hashes are retained"

Retains a real artifact (not just print statements) recording, for every
fixture already used elsewhere in Phase 0: the raw UTF-8 bytes and their
SHA-256, the rendered prompt (== raw text for these fixtures -- none of
them go through a chat template, which is out of scope for Profile A/B),
and the token IDs (from the real tokenizer for real-candidate fixtures,
from the control-token scheme for the synthetic fixture) with their
SHA-256. "Rendered == raw" is stated explicitly per fixture, not silently
assumed, since a real chat-template fixture would need the two to differ
and be tracked separately.
"""
from __future__ import annotations

import hashlib
import json
import os

import numpy as np
from tokenizers import Tokenizer

OUTPUT_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "raw_prompt_identity_manifest.json")
TOKENIZER_JSON_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m", "tokenizer.json")

REAL_CANDIDATE_FIXTURES = [
    "The capital of France is",
    "Hello, world!",
    "12345 test",
    "  leading spaces",
    "unicode: café résumé",
]


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def _sha256_ids(ids: list[int]) -> str:
    payload = ",".join(str(i) for i in ids).encode("utf-8")
    return _sha256_bytes(payload)


def build_record(fixture_id: str, raw_text: str, token_ids: list[int], *, template_applied: bool) -> dict:
    raw_bytes = raw_text.encode("utf-8")
    rendered_prompt = raw_text  # no chat template applied for any current fixture
    rendered_bytes = rendered_prompt.encode("utf-8")
    return {
        "fixture_id": fixture_id,
        "raw_text": raw_text,
        "raw_bytes_sha256": _sha256_bytes(raw_bytes),
        "raw_bytes_length": len(raw_bytes),
        "template_applied": template_applied,
        "rendered_prompt": rendered_prompt,
        "rendered_prompt_sha256": _sha256_bytes(rendered_bytes),
        "rendered_equals_raw": rendered_prompt == raw_text,
        "token_ids": token_ids,
        "token_ids_sha256": _sha256_ids(token_ids),
    }


def run() -> bool:
    records = []

    # Synthetic Profile A fixture (Fixture C's sequence), via the control-token scheme.
    synth_token_ids = [1, 5, 9, 3, 7]
    synth_raw = "".join(f"<{t}>" for t in synth_token_ids)
    records.append(build_record("OE-L0-SYNTH-1-control-tokens", synth_raw, synth_token_ids, template_applied=False))

    # Real-candidate fixtures, via the pinned HF tokenizer.
    if os.path.isfile(TOKENIZER_JSON_PATH):
        hf_tokenizer = Tokenizer.from_file(TOKENIZER_JSON_PATH)
        for text in REAL_CANDIDATE_FIXTURES:
            ids = hf_tokenizer.encode(text).ids
            records.append(build_record(f"smollm2-135m:{text!r}", text, ids, template_applied=False))
    else:
        print(f"NOTE: {TOKENIZER_JSON_PATH!r} not found -- skipping real-candidate fixtures "
              f"(run oracle.download_candidate first)")

    os.makedirs(os.path.dirname(OUTPUT_PATH), exist_ok=True)
    with open(OUTPUT_PATH, "w", encoding="utf-8") as f:
        json.dump({"records": records}, f, indent=2, sort_keys=True, ensure_ascii=False)

    required_fields = {"raw_text", "raw_bytes_sha256", "rendered_prompt", "rendered_prompt_sha256",
                        "token_ids", "token_ids_sha256"}
    all_complete = all(required_fields.issubset(r.keys()) for r in records)

    print(f"wrote {len(records)} fixture records to {OUTPUT_PATH}")
    for r in records:
        print(f"  {r['fixture_id']}: raw_bytes_sha256={r['raw_bytes_sha256'][:12]}... "
              f"token_ids_sha256={r['token_ids_sha256'][:12]}... rendered_equals_raw={r['rendered_equals_raw']}")

    return all_complete and len(records) >= 2  # at least synthetic + one real fixture


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: raw_prompt_identity")
    raise SystemExit(0 if ok else 1)
