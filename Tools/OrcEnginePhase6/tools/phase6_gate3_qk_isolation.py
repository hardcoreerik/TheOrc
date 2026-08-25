# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Gate 3 (combined Codex/Grok remediation round 7):
genuine single-variable Q/K isolation.

Gate 2 (OE-ADR-053/054/056) established a strongly-supported but NOT
genuinely isolated causal claim: the canonical GGUF used for comparison
differs from the existing-custom GGUF in Q/K layout AND in
`output.weight` presence (canonical omits it). This module performs
Option A from the round-7 instruction: build a diagnostic artifact that
is byte-for-byte identical to the EXISTING CUSTOM F32 GGUF in every
respect -- including `output.weight` and every metadata field -- except
that `blk.N.attn_q.weight`/`blk.N.attn_k.weight` for ALL 30 layers have
the official llama.cpp permute() applied. This makes Q/K the ONLY
variable between "existing-custom" and this diagnostic artifact, by
construction, and the construction is itself programmatically verified
(not merely asserted).

Uses the `gguf` package's GGUFReader/GGUFWriter (existing GGUF tooling,
per instruction -- not a binary patcher) so metadata and non-Q/K tensor
bytes are read and re-written through GGUF's own supported API, not
manual offset arithmetic.

Three phases:
  1. `audit`: full metadata + all-30-layer tensor diff between the
     existing-custom and canonical F32 GGUFs, classifying every
     difference (not assuming only Q/K differs). Supersedes/extends
     phase6_gate2_full_tensor_diff.py's tensor-only check with metadata
     coverage and hash binding.
  2. `build`: generates the Option-A diagnostic F32 GGUF (untracked,
     under .orc/gate3-qk-isolation/) and verifies programmatically that
     every non-Q/K tensor and every metadata field is unchanged from
     the existing-custom baseline.
  3. `compare`: (separate script, phase6_gate3_isolation_llama_cpp_run.py)
     runs the pinned llama.cpp server against existing-custom vs. this
     diagnostic artifact across the full 7-prompt corpus.

Usage:
    python phase6_gate3_qk_isolation.py audit \
        --existing-f32 PATH --canonical-f32 PATH --report PATH.json
    python phase6_gate3_qk_isolation.py build \
        --existing-f32 PATH --out PATH.gguf --report PATH.json
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys

import numpy as np
from gguf import GGUFReader, GGUFValueType, GGUFWriter

N_HEAD = 9
N_HEAD_KV = 3
N_LAYERS = 30

# Internal GGUFReader bookkeeping fields, not real metadata KV pairs --
# GGUFWriter recomputes these itself; re-adding them via add_key_value
# would corrupt the header.
_INTERNAL_HEADER_FIELDS = frozenset({"GGUF.version", "GGUF.tensor_count", "GGUF.kv_count"})


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _official_permute(weights: np.ndarray, n_head: int, n_head_kv: int | None) -> np.ndarray:
    """Verbatim port of the pinned llama.cpp commit's conversion/llama.py
    LlamaModel.permute() (see PHASE6_GATE2_QK_LAYOUT_ROOT_CAUSE.md 2A for
    the quoted source) -- the same transform already independently
    verified at layers 0/11/28 in Gate 2, applied here generically to all
    30 layers."""
    if n_head_kv is not None and n_head != n_head_kv:
        n_head = n_head_kv
    return (weights.reshape(n_head, 2, weights.shape[0] // n_head // 2, *weights.shape[1:])
            .swapaxes(1, 2)
            .reshape(weights.shape))


def _get_tensor(reader: GGUFReader, name: str):
    for t in reader.tensors:
        if t.name == name:
            return t
    return None


def _all_tensor_names(reader: GGUFReader) -> set[str]:
    return {t.name for t in reader.tensors}


def _classify_tensor_diff(custom: GGUFReader, canon: GGUFReader) -> dict:
    custom_names = _all_tensor_names(custom)
    canon_names = _all_tensor_names(canon)
    only_custom = sorted(custom_names - canon_names)
    only_canon = sorted(canon_names - custom_names)

    qk_layers_checked = []
    qk_mismatches = []
    other_mismatches = []
    other_matches = 0

    for i in range(N_LAYERS):
        for kind, tensor_n_head_kv in (("attn_q", N_HEAD), ("attn_k", N_HEAD_KV)):
            name = f"blk.{i}.{kind}.weight"
            c = _get_tensor(custom, name)
            k = _get_tensor(canon, name)
            if c is None or k is None:
                qk_mismatches.append((name, "MISSING", c is None, k is None))
                continue
            permuted = _official_permute(c.data.copy(), N_HEAD, tensor_n_head_kv)
            ok = np.array_equal(permuted, k.data)
            qk_layers_checked.append(name)
            if not ok:
                qk_mismatches.append((name, "PERMUTE-VALUE-MISMATCH"))

    for name in sorted((custom_names & canon_names) - {f"blk.{i}.{kind}.weight"
                                                        for i in range(N_LAYERS)
                                                        for kind in ("attn_q", "attn_k")}):
        c = _get_tensor(custom, name)
        k = _get_tensor(canon, name)
        if c.data.shape != k.data.shape:
            other_mismatches.append((name, "SHAPE", list(c.data.shape), list(k.data.shape)))
            continue
        if np.array_equal(c.data, k.data):
            other_matches += 1
        else:
            maxdiff = float(np.max(np.abs(c.data.astype(np.float64) - k.data.astype(np.float64))))
            other_mismatches.append((name, "VALUE", maxdiff))

    return {
        "only_in_existing_custom": only_custom,
        "only_in_canonical": only_canon,
        "qk_layers_checked": len(qk_layers_checked),
        "qk_expected_layers": N_LAYERS * 2,
        "qk_mismatches": qk_mismatches,
        "non_qk_shared_tensors_checked": other_matches + len(other_mismatches),
        "non_qk_matches": other_matches,
        "non_qk_mismatches": other_mismatches,
    }


def _classify_metadata_diff(custom: GGUFReader, canon: GGUFReader) -> dict:
    custom_keys = set(custom.fields) - _INTERNAL_HEADER_FIELDS
    canon_keys = set(canon.fields) - _INTERNAL_HEADER_FIELDS
    only_custom = sorted(custom_keys - canon_keys)
    only_canon = sorted(canon_keys - custom_keys)
    differing = []
    identical = []
    for key in sorted(custom_keys & canon_keys):
        cv = custom.fields[key].contents()
        kv = canon.fields[key].contents()
        if cv == kv:
            identical.append(key)
        else:
            differing.append((key, repr(cv)[:200], repr(kv)[:200]))
    return {
        "only_in_existing_custom": only_custom,
        "only_in_canonical": only_canon,
        "identical_fields": identical,
        "differing_fields": differing,
    }


def cmd_audit(args: argparse.Namespace) -> int:
    custom = GGUFReader(args.existing_f32)
    canon = GGUFReader(args.canonical_f32)

    tensor_report = _classify_tensor_diff(custom, canon)
    metadata_report = _classify_metadata_diff(custom, canon)

    # Verify custom output.weight == token_embd.weight (byte identity),
    # the already-established tied-weight fact, re-verified here so this
    # audit's report is self-contained rather than trusting an external
    # claim.
    out_w = _get_tensor(custom, "output.weight")
    emb_w = _get_tensor(custom, "token_embd.weight")
    tied_output_verified = (out_w is not None and emb_w is not None
                            and np.array_equal(out_w.data, emb_w.data))

    report = {
        "existing_custom_f32_sha256": _sha256_file(args.existing_f32),
        "canonical_f32_sha256": _sha256_file(args.canonical_f32),
        "n_layers": N_LAYERS,
        "n_head": N_HEAD,
        "n_head_kv": N_HEAD_KV,
        "tensor_diff": tensor_report,
        "metadata_diff": metadata_report,
        "custom_output_weight_tied_to_token_embd_verified": tied_output_verified,
    }

    all_qk_verified = (tensor_report["qk_layers_checked"] == tensor_report["qk_expected_layers"]
                       and not tensor_report["qk_mismatches"])
    all_non_qk_identical = not tensor_report["non_qk_mismatches"]
    only_structural_diff_is_output_weight = (tensor_report["only_in_existing_custom"] == ["output.weight"]
                                             and tensor_report["only_in_canonical"] == [])

    report["classification"] = {
        "all_30_layers_qk_permute_verified": all_qk_verified,
        "all_non_qk_shared_tensors_identical": all_non_qk_identical,
        "only_structural_tensor_diff_is_output_weight": only_structural_diff_is_output_weight,
        "metadata_fields_differing_count": len(metadata_report["differing_fields"]),
        "metadata_fields_only_in_one_side_count": (len(metadata_report["only_in_existing_custom"])
                                                    + len(metadata_report["only_in_canonical"])),
    }

    with open(args.report, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    print(f"Q/K: {tensor_report['qk_layers_checked']}/{tensor_report['qk_expected_layers']} tensors "
          f"verified via official permute(), {len(tensor_report['qk_mismatches'])} mismatches")
    print(f"Non-Q/K shared tensors: {tensor_report['non_qk_shared_tensors_checked']} checked, "
          f"{len(tensor_report['non_qk_mismatches'])} mismatches")
    print(f"Structural: only_in_custom={tensor_report['only_in_existing_custom']} "
          f"only_in_canonical={tensor_report['only_in_canonical']}")
    print(f"Metadata: {len(metadata_report['identical_fields'])} identical, "
          f"{len(metadata_report['differing_fields'])} differing, "
          f"only_in_custom={metadata_report['only_in_existing_custom']} "
          f"only_in_canonical={metadata_report['only_in_canonical']}")
    if metadata_report["differing_fields"]:
        for key, cv, kv in metadata_report["differing_fields"]:
            print(f"  METADATA DIFFERS: {key}: custom={cv} canonical={kv}")
    print(f"Tied output.weight==token_embd.weight (custom): {tied_output_verified}")
    print(f"\nDONE: written to {args.report!r}")
    return 0


def cmd_build(args: argparse.Namespace) -> int:
    custom = GGUFReader(args.existing_f32)
    existing_hash = _sha256_file(args.existing_f32)

    writer = GGUFWriter(args.out, arch=custom.fields["general.architecture"].contents())
    for key, field in custom.fields.items():
        if key in _INTERNAL_HEADER_FIELDS:
            continue
        types = field.types
        value = field.contents()
        if types[0] == GGUFValueType.ARRAY:
            writer.add_key_value(key, value, GGUFValueType.ARRAY, sub_type=types[1])
        else:
            writer.add_key_value(key, value, types[0])

    qk_names = {f"blk.{i}.{kind}.weight" for i in range(N_LAYERS) for kind in ("attn_q", "attn_k")}
    permuted_count = 0
    for t in custom.tensors:
        if t.name in qk_names:
            layer = int(t.name.split(".")[1])
            kind = "attn_q" if "attn_q" in t.name else "attn_k"
            n_head_kv = N_HEAD if kind == "attn_q" else N_HEAD_KV
            data = _official_permute(t.data.copy(), N_HEAD, n_head_kv)
            permuted_count += 1
        else:
            data = t.data
        writer.add_tensor(t.name, data, raw_dtype=t.tensor_type)

    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()

    if permuted_count != N_LAYERS * 2:
        sys.exit(f"ABORT: permuted {permuted_count} Q/K tensors, expected exactly {N_LAYERS * 2}.")

    # Programmatic self-verification: re-read the artifact just written and
    # confirm EVERY non-Q/K tensor and EVERY metadata field is unchanged
    # from the existing-custom baseline, and every Q/K tensor equals the
    # permuted form -- never trust the write path silently.
    rebuilt = GGUFReader(args.out)
    mismatches = []
    for t in custom.tensors:
        rt = _get_tensor(rebuilt, t.name)
        if rt is None:
            mismatches.append((t.name, "MISSING-IN-OUTPUT"))
            continue
        if t.name in qk_names:
            kind = "attn_q" if "attn_q" in t.name else "attn_k"
            n_head_kv = N_HEAD if kind == "attn_q" else N_HEAD_KV
            expected = _official_permute(t.data.copy(), N_HEAD, n_head_kv)
            if not np.array_equal(expected, rt.data):
                mismatches.append((t.name, "QK-PERMUTE-NOT-APPLIED-CORRECTLY"))
        else:
            if not np.array_equal(t.data, rt.data):
                mismatches.append((t.name, "NON-QK-TENSOR-CHANGED"))
    custom_names = _all_tensor_names(custom)
    rebuilt_names = _all_tensor_names(rebuilt)
    if custom_names != rebuilt_names:
        mismatches.append(("TENSOR-SET", f"custom={custom_names - rebuilt_names} "
                                          f"new={rebuilt_names - custom_names}"))
    metadata_mismatches = []
    for key, field in custom.fields.items():
        if key in _INTERNAL_HEADER_FIELDS:
            continue
        rf = rebuilt.fields.get(key)
        if rf is None or rf.contents() != field.contents():
            metadata_mismatches.append(key)

    out_hash = _sha256_file(args.out)
    report = {
        "existing_custom_f32_sha256": existing_hash,
        "output_gguf_path": args.out,
        "output_gguf_sha256": out_hash,
        "qk_tensors_permuted": permuted_count,
        "tensor_mismatches_vs_baseline": mismatches,
        "metadata_mismatches_vs_baseline": metadata_mismatches,
        "isolation_verified": not mismatches and not metadata_mismatches,
    }
    with open(args.report, "w", encoding="utf-8") as f:
        json.dump(report, f, indent=2)

    print(f"Built {args.out!r} (sha256={out_hash}), permuted {permuted_count} Q/K tensors")
    print(f"Tensor mismatches vs baseline (excluding Q/K, which is EXPECTED to differ): "
          f"{len(mismatches)}")
    for m in mismatches:
        print("  ", m)
    print(f"Metadata mismatches vs baseline: {len(metadata_mismatches)} {metadata_mismatches}")
    print(f"ISOLATION VERIFIED (Q/K is the ONLY variable vs existing-custom): "
          f"{report['isolation_verified']}")
    return 0 if report["isolation_verified"] else 1


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="cmd", required=True)

    p_audit = sub.add_parser("audit")
    p_audit.add_argument("--existing-f32", required=True)
    p_audit.add_argument("--canonical-f32", required=True)
    p_audit.add_argument("--report", required=True)
    p_audit.set_defaults(func=cmd_audit)

    p_build = sub.add_parser("build")
    p_build.add_argument("--existing-f32", required=True)
    p_build.add_argument("--out", required=True)
    p_build.add_argument("--report", required=True)
    p_build.set_defaults(func=cmd_build)

    args = parser.parse_args()
    return args.func(args)


if __name__ == "__main__":
    sys.exit(main())
