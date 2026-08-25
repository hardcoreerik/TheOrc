# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
FL-08 (Fringe Lab): Model Compatibility Profiler -- research prototype.

Given a GGUF artifact (and, optionally, a paired reference artifact of
the SAME underlying model in a known-different Q/K layout convention),
produces an evidence-backed compatibility profile: what the artifact
declares, what structure proves, what the numerical permutation probe
proves, and what remains ambiguous or unknown -- never a guess.

This is a RESEARCH PROTOTYPE (Fringe Lab FL-08). It is independent of
production OrcEngine code: it does not import, link against, or modify
any file under Tools/OrcEngine*, and it does not touch the production
loader. See .orc/fringelab/2026-08-25-fl08-model-compat-profiler/
experiments/FL-08/ for the full charter, schema rationale, and
recognition-strategy design this implements.

The Q/K permutation formula below is the SAME formula Phase 6 round 7
independently verified against the pinned llama.cpp commit
6fed9f6ff7a603b124cb8c5864fca6ea879f9f99 (tag b10436),
conversion/llama.py's LlamaModel.permute() -- re-implemented here
directly (not imported cross-worktree) so this experiment has no
dependency on the feat/orcengine-phase6-quantization branch.

Usage:
    python profiler.py ARTIFACT.gguf [--reference REFERENCE.gguf]
                        [--target canonical-llama.cpp|orcengine-current]
                        [--json OUT.json]

Exit code: 0 for VERIFIED_COMPATIBLE or VERIFIED_NORMALIZATION_REQUIRED
(a profile was successfully produced), nonzero for AMBIGUOUS, INVALID,
or VERIFIED_UNSUPPORTED (a profile was still produced and printed, but
this artifact must not be treated as safe to execute without further
evidence or an explicit operator decision).
"""
from __future__ import annotations

import argparse
import hashlib
import json
import sys

import numpy as np
from gguf import GGUFReader, GGUFValueType

SCHEMA_VERSION = 1

N_HEAD_DEFAULT = None  # read from metadata, never assumed
KNOWN_ENCODINGS = {"F32", "F16", "Q8_0"}
REQUIRED_LLAMA_PER_LAYER_SUFFIXES = (
    "attn_norm.weight", "attn_q.weight", "attn_k.weight", "attn_v.weight", "attn_output.weight",
    "ffn_norm.weight", "ffn_gate.weight", "ffn_up.weight", "ffn_down.weight",
)
REQUIRED_LLAMA_GLOBAL_TENSORS = ("token_embd.weight", "output_norm.weight")

_INTERNAL_HEADER_FIELDS = frozenset({"GGUF.version", "GGUF.tensor_count", "GGUF.kv_count"})


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def official_permute(weights: np.ndarray, n_head: int, n_head_kv: int | None) -> np.ndarray:
    """Verbatim port of conversion/llama.py's LlamaModel.permute() at the
    pinned commit -- already independently verified against real
    artifacts in Phase 6 round 7 (phase6_gate3_qk_isolation.py), not
    re-derived here, just re-implemented for this independent worktree."""
    if n_head_kv is not None and n_head != n_head_kv:
        n_head = n_head_kv
    return (weights.reshape(n_head, 2, weights.shape[0] // n_head // 2, *weights.shape[1:])
            .swapaxes(1, 2)
            .reshape(weights.shape))


class Profile:
    """Accumulates evidence and classifications for one artifact. Every
    setter records its own evidence string -- nothing is asserted into
    the final JSON without a paired human-readable justification."""

    def __init__(self, path: str):
        self.path = path
        self.evidence: list[str] = []
        self.unresolved: list[str] = []
        self.data: dict = {
            "schema_version": SCHEMA_VERSION,
            "artifact": {"path": path, "sha256": None, "file_size_bytes": None},
            "container": {"type": "GGUF", "version": None, "valid": False, "evidence": []},
            "declared_architecture": None,
            "provenance": {"known": False, "producer": None, "source": "UNKNOWN"},
            "tensor_inventory_fingerprint": None,
            "metadata_fingerprint": None,
            "tokenizer_fingerprint": None,
            "output_weight_semantics": "UNKNOWN",
            "qk_layout": {
                "classification": "UNKNOWN", "confidence": "AMBIGUOUS",
                "layers_checked": 0, "layers_total": 0, "per_layer_consistent": False,
            },
            "quantization_formats": [],
            "known_normalization_requirements": [],
            "runtime_compatibility": {"target": None, "result": "AMBIGUOUS"},
            "confidence_level": "AMBIGUOUS",
            "evidence": [],
            "unresolved_ambiguities": [],
            "execution_authorization": False,
        }

    def add_evidence(self, text: str) -> None:
        self.evidence.append(text)

    def add_ambiguity(self, text: str) -> None:
        self.unresolved.append(text)

    def finalize(self) -> dict:
        self.data["evidence"] = list(self.evidence)
        self.data["unresolved_ambiguities"] = list(self.unresolved)
        return self.data


def _tensor_by_name(reader: GGUFReader, name: str):
    for t in reader.tensors:
        if t.name == name:
            return t
    return None


def _fingerprint_tensor_inventory(reader: GGUFReader) -> str:
    items = sorted((t.name, tuple(int(d) for d in t.shape), t.tensor_type.name) for t in reader.tensors)
    return hashlib.sha256(repr(items).encode()).hexdigest()


def _fingerprint_metadata(reader: GGUFReader) -> str:
    items = []
    for key in sorted(reader.fields):
        if key in _INTERNAL_HEADER_FIELDS:
            continue
        items.append((key, repr(reader.fields[key].contents())))
    return hashlib.sha256(repr(sorted(items)).encode()).hexdigest()


def _fingerprint_tokenizer(reader: GGUFReader) -> str | None:
    tok_keys = sorted(k for k in reader.fields if k.startswith("tokenizer."))
    if not tok_keys:
        return None
    items = [(k, repr(reader.fields[k].contents())) for k in tok_keys]
    return hashlib.sha256(repr(items).encode()).hexdigest()


def layer1_container(profile: Profile, path: str) -> GGUFReader | None:
    """Layer 1: container validation. Returns the reader on success, None
    (profile already marked INVALID) on any structural failure."""
    import os
    if not os.path.isfile(path):
        profile.add_ambiguity(f"artifact not found at {path!r}")
        profile.data["container"]["valid"] = False
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        profile.data["confidence_level"] = "AMBIGUOUS"
        return None

    profile.data["artifact"]["sha256"] = sha256_file(path)
    profile.add_evidence(f"artifact sha256={profile.data['artifact']['sha256']}")
    profile.data["artifact"]["file_size_bytes"] = os.path.getsize(path)

    try:
        reader = GGUFReader(path)
    except Exception as ex:  # noqa: BLE001 -- deliberately broad: any parse failure is INVALID, not a crash
        profile.add_ambiguity(f"container failed to parse: {ex!r}")
        profile.data["container"]["valid"] = False
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        profile.data["confidence_level"] = "AMBIGUOUS"
        return None

    version = int(reader.fields["GGUF.version"].contents()) if "GGUF.version" in reader.fields else None
    profile.data["container"]["version"] = version
    if version not in (2, 3):
        profile.add_ambiguity(f"unrecognized GGUF version {version!r} (this profiler only classifies v2/v3)")
        profile.data["container"]["valid"] = False
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        return None

    file_size = profile.data["artifact"]["file_size_bytes"]
    out_of_bounds = []
    seen_names = set()
    duplicates = set()
    for t in reader.tensors:
        if t.name in seen_names:
            duplicates.add(t.name)
        seen_names.add(t.name)
        end = int(t.data_offset) + t.data.nbytes
        if end > file_size:
            out_of_bounds.append(t.name)
    if duplicates:
        profile.add_ambiguity(f"duplicate tensor names: {sorted(duplicates)}")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        profile.data["container"]["valid"] = False
        return None
    if out_of_bounds:
        profile.add_ambiguity(f"tensor(s) exceed file bounds: {out_of_bounds}")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        profile.data["container"]["valid"] = False
        return None

    unknown_encodings = sorted({t.tensor_type.name for t in reader.tensors} - KNOWN_ENCODINGS)
    if unknown_encodings:
        profile.add_ambiguity(f"tensor encoding(s) not in this profiler's known set: {unknown_encodings}")
        # Not necessarily invalid -- an unrecognized-but-valid encoding is
        # UNSUPPORTED for classification purposes, not a structural defect.

    profile.data["container"]["valid"] = True
    profile.add_evidence(f"container valid: GGUF v{version}, {len(reader.tensors)} tensors, "
                         f"0 out-of-bounds, 0 duplicates")
    profile.data["quantization_formats"] = sorted({t.tensor_type.name for t in reader.tensors})
    return reader


def layer2_architecture(profile: Profile, reader: GGUFReader) -> tuple[str | None, int, int, int, int]:
    """Layer 2: architecture validation. Returns
    (architecture, n_layers, n_head, n_head_kv, hidden) on success, or
    (None, 0, 0, 0, 0) with the profile marked INVALID on contradiction."""
    arch = reader.fields.get("general.architecture")
    architecture = arch.contents() if arch else None
    profile.data["declared_architecture"] = architecture

    if architecture != "llama":
        profile.add_ambiguity(f"declared architecture {architecture!r} is not 'llama' -- "
                              f"this experiment's Layer 3 (Q/K dialect) only supports llama")
        profile.data["runtime_compatibility"]["result"] = "VERIFIED_UNSUPPORTED"
        return None, 0, 0, 0, 0

    def _meta_int(key: str) -> int | None:
        f = reader.fields.get(key)
        return int(f.contents()) if f else None

    n_layers = _meta_int("llama.block_count")
    n_head = _meta_int("llama.attention.head_count")
    n_head_kv = _meta_int("llama.attention.head_count_kv")
    hidden = _meta_int("llama.embedding_length")

    if None in (n_layers, n_head, n_head_kv, hidden):
        profile.add_ambiguity("required llama.* metadata missing "
                              f"(block_count={n_layers} head_count={n_head} "
                              f"head_count_kv={n_head_kv} embedding_length={hidden})")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        return None, 0, 0, 0, 0

    highest_layer_seen = -1
    for t in reader.tensors:
        if t.name.startswith("blk."):
            try:
                idx = int(t.name.split(".")[1])
            except (IndexError, ValueError):
                continue
            highest_layer_seen = max(highest_layer_seen, idx)
    if highest_layer_seen + 1 != n_layers:
        profile.add_ambiguity(f"declared block_count={n_layers} but highest blk.N.* index seen is "
                              f"{highest_layer_seen} ({highest_layer_seen + 1} layers present)")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        return None, 0, 0, 0, 0

    missing_tensors = []
    shape_contradictions = []
    for name in REQUIRED_LLAMA_GLOBAL_TENSORS:
        if _tensor_by_name(reader, name) is None:
            missing_tensors.append(name)
    for i in range(n_layers):
        for suffix in REQUIRED_LLAMA_PER_LAYER_SUFFIXES:
            name = f"blk.{i}.{suffix}"
            t = _tensor_by_name(reader, name)
            if t is None:
                missing_tensors.append(name)
                continue
            if suffix == "attn_q.weight":
                rows = t.shape[0]
                if n_head <= 0 or rows % n_head != 0 or (rows // n_head) % 2 != 0:
                    shape_contradictions.append(
                        f"{name}: {rows} rows not evenly divisible into {n_head} heads (x2 for permute)")
            elif suffix == "attn_k.weight":
                rows = t.shape[0]
                if n_head_kv <= 0 or rows % n_head_kv != 0 or (rows // n_head_kv) % 2 != 0:
                    shape_contradictions.append(
                        f"{name}: {rows} rows not evenly divisible into {n_head_kv} kv-heads (x2 for permute)")

    if missing_tensors:
        profile.add_ambiguity(f"required tensor(s) missing for declared architecture/layer count: "
                              f"{missing_tensors[:10]}{'...' if len(missing_tensors) > 10 else ''}")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        return None, 0, 0, 0, 0
    if shape_contradictions:
        profile.add_ambiguity(f"tensor shape contradicts declared head geometry: {shape_contradictions}")
        profile.data["runtime_compatibility"]["result"] = "INVALID"
        return None, 0, 0, 0, 0

    profile.add_evidence(f"architecture 'llama' validated: {n_layers} layers, all required tensors present, "
                         f"Q/K shapes consistent with head_count={n_head}/head_count_kv={n_head_kv}")
    profile.data["tensor_inventory_fingerprint"] = _fingerprint_tensor_inventory(reader)
    profile.data["metadata_fingerprint"] = _fingerprint_metadata(reader)
    profile.data["tokenizer_fingerprint"] = _fingerprint_tokenizer(reader)
    return architecture, n_layers, n_head, n_head_kv, hidden


def layer3_qk_dialect(profile: Profile, reader: GGUFReader, reference: GGUFReader | None,
                      reference_label: str | None, n_layers: int, n_head: int, n_head_kv: int) -> None:
    """Layer 3: Q/K dialect identification. Only runs the numerical
    permutation check when a paired reference is provided -- otherwise
    reports UNKNOWN/AMBIGUOUS rather than inferring from tensor names."""
    if reference is None:
        profile.add_ambiguity("no paired reference artifact supplied -- Q/K layout cannot be "
                              "classified from a single artifact (tensor names alone are not evidence, "
                              "per this profiler's explicit design)")
        profile.data["qk_layout"] = {
            "classification": "UNKNOWN", "confidence": "AMBIGUOUS",
            "layers_checked": 0, "layers_total": n_layers, "per_layer_consistent": False,
        }
        return

    raw_match_count = 0
    canonical_match_count = 0
    mismatches = []
    for i in range(n_layers):
        for kind, ref_n_head_kv in (("attn_q", n_head), ("attn_k", n_head_kv)):
            name = f"blk.{i}.{kind}.weight"
            a = _tensor_by_name(reader, name)
            b = _tensor_by_name(reference, name)
            if a is None or b is None:
                mismatches.append((name, "MISSING_IN_ONE_SIDE"))
                continue
            if a.data.shape != b.data.shape:
                mismatches.append((name, "SHAPE_MISMATCH"))
                continue
            # The permutation is NOT generally involutory (self-inverse)
            # for real head geometries -- confirmed numerically: applying
            # official_permute() twice to a real 576-row/9-head tensor
            # does NOT recover the original. So "a is RAW relative to b"
            # (permute(a)==b) and "a is CANONICAL relative to b"
            # (permute(b)==a) are two DIFFERENT, independently-checked
            # hypotheses -- never assume the second follows from the
            # first failing.
            direct_match = np.array_equal(a.data, b.data)
            a_is_raw_relative_to_b = np.array_equal(official_permute(a.data.copy(), n_head, ref_n_head_kv), b.data)
            b_is_raw_relative_to_a = np.array_equal(official_permute(b.data.copy(), n_head, ref_n_head_kv), a.data)
            if direct_match and not a_is_raw_relative_to_b and not b_is_raw_relative_to_a:
                canonical_match_count += 1
            elif a_is_raw_relative_to_b and not direct_match and not b_is_raw_relative_to_a:
                raw_match_count += 1
            elif b_is_raw_relative_to_a and not direct_match and not a_is_raw_relative_to_b:
                canonical_match_count += 1
            elif not direct_match and not a_is_raw_relative_to_b and not b_is_raw_relative_to_a:
                mismatches.append((name, "NEITHER_DIRECT_NOR_PERMUTED_MATCH"))
            else:
                # More than one hypothesis matched simultaneously -- only
                # possible for a degenerate shape where the permutation is
                # a no-op. Record as an ambiguity rather than silently
                # picking one label.
                mismatches.append((name, "MULTIPLE_HYPOTHESES_MATCH_SIMULTANEOUSLY"))

    layers_checked = raw_match_count + canonical_match_count
    total_qk_tensors = n_layers * 2
    profile.data["qk_layout"]["layers_checked"] = layers_checked
    profile.data["qk_layout"]["layers_total"] = total_qk_tensors

    if mismatches:
        profile.add_ambiguity(f"Q/K comparison against reference {reference_label!r}: "
                              f"{len(mismatches)} tensor(s) did not cleanly match either direct or "
                              f"permuted form: {mismatches[:5]}{'...' if len(mismatches) > 5 else ''}")
        profile.data["qk_layout"]["classification"] = "AMBIGUOUS"
        profile.data["qk_layout"]["confidence"] = "AMBIGUOUS"
        profile.data["qk_layout"]["per_layer_consistent"] = False
        return

    if raw_match_count == total_qk_tensors:
        profile.data["qk_layout"]["classification"] = "RAW_HF"
        profile.data["qk_layout"]["confidence"] = "NUMERICALLY_VERIFIED"
        profile.data["qk_layout"]["per_layer_consistent"] = True
        profile.add_evidence(f"Q/K fingerprint matches RAW_HF convention: permute(this artifact's Q/K) == "
                             f"reference {reference_label!r}'s Q/K, verified on {total_qk_tensors}/"
                             f"{total_qk_tensors} tensors ({n_layers}/{n_layers} layers)")
    elif canonical_match_count == total_qk_tensors:
        profile.data["qk_layout"]["classification"] = "CANONICAL_LLAMA_CPP"
        profile.data["qk_layout"]["confidence"] = "NUMERICALLY_VERIFIED"
        profile.data["qk_layout"]["per_layer_consistent"] = True
        profile.add_evidence(f"Q/K fingerprint matches CANONICAL_LLAMA_CPP convention: this artifact's Q/K "
                             f"== reference {reference_label!r}'s Q/K directly, verified on "
                             f"{total_qk_tensors}/{total_qk_tensors} tensors ({n_layers}/{n_layers} layers)")
    else:
        profile.add_ambiguity(f"inconsistent per-layer classification: {raw_match_count} layers match "
                              f"RAW_HF pattern, {canonical_match_count} match CANONICAL pattern -- "
                              f"a single artifact should not mix conventions")
        profile.data["qk_layout"]["classification"] = "AMBIGUOUS"
        profile.data["qk_layout"]["confidence"] = "AMBIGUOUS"
        profile.data["qk_layout"]["per_layer_consistent"] = False


def layer_output_weight_semantics(profile: Profile, reader: GGUFReader) -> None:
    out_w = _tensor_by_name(reader, "output.weight")
    emb_w = _tensor_by_name(reader, "token_embd.weight")
    if out_w is None:
        profile.data["output_weight_semantics"] = "ABSENT"
        profile.add_evidence("output.weight tensor absent -- tied head materialized from token_embd.weight "
                             "at runtime (canonical-converter convention)")
        return
    if emb_w is None:
        profile.data["output_weight_semantics"] = "UNKNOWN"
        profile.add_ambiguity("output.weight present but token_embd.weight missing -- cannot classify tie status")
        return
    if out_w.data.shape == emb_w.data.shape and np.array_equal(out_w.data, emb_w.data):
        profile.data["output_weight_semantics"] = "TIED_PHYSICALLY_DUPLICATED"
        profile.add_evidence("output.weight present and byte-identical to token_embd.weight "
                             "(logically tied, physically duplicated)")
    else:
        profile.data["output_weight_semantics"] = "UNTIED"
        profile.add_evidence("output.weight present and NOT identical to token_embd.weight (untied)")


def layer5_decision(profile: Profile, target: str) -> None:
    profile.data["runtime_compatibility"]["target"] = target
    qk = profile.data["qk_layout"]

    if profile.data["runtime_compatibility"]["result"] in ("INVALID", "VERIFIED_UNSUPPORTED"):
        pass  # already decided by an earlier layer -- do not overwrite
    elif qk["classification"] == "AMBIGUOUS" or qk["confidence"] == "AMBIGUOUS":
        profile.data["runtime_compatibility"]["result"] = "AMBIGUOUS"
    elif qk["classification"] == "UNKNOWN":
        profile.data["runtime_compatibility"]["result"] = "AMBIGUOUS"
    elif target == "canonical-llama.cpp":
        if qk["classification"] == "CANONICAL_LLAMA_CPP":
            profile.data["runtime_compatibility"]["result"] = "VERIFIED_COMPATIBLE"
        elif qk["classification"] == "RAW_HF":
            profile.data["runtime_compatibility"]["result"] = "VERIFIED_NORMALIZATION_REQUIRED"
            profile.data["known_normalization_requirements"].append(
                "Q/K RoPE-layout permutation required (raw-HF -> canonical llama.cpp interleaving)")
    elif target == "orcengine-current":
        # Phase 6 round 7 Gate 4 finding: OrcEngine's CURRENT loader
        # silently misinterprets canonically-permuted Q/K as raw --
        # so for THIS target, RAW_HF is what the current loader expects
        # as-is, and CANONICAL_LLAMA_CPP requires (unimplemented)
        # normalization to be safe.
        if qk["classification"] == "RAW_HF":
            profile.data["runtime_compatibility"]["result"] = "VERIFIED_COMPATIBLE"
            profile.add_evidence("OrcEngine's CURRENT loader expects raw-HF Q/K layout "
                                 "(Phase 6 round 7 Gate 4 finding) -- this artifact matches as-is")
        elif qk["classification"] == "CANONICAL_LLAMA_CPP":
            profile.data["runtime_compatibility"]["result"] = "VERIFIED_NORMALIZATION_REQUIRED"
            profile.data["known_normalization_requirements"].append(
                "Q/K RoPE-layout un-permutation required (canonical llama.cpp -> raw-HF, to match "
                "OrcEngine's CURRENT loader's undocumented expectation) -- see Phase 6 OE-ADR-058")
    else:
        profile.add_ambiguity(f"unrecognized runtime_compatibility target {target!r}")
        profile.data["runtime_compatibility"]["result"] = "AMBIGUOUS"

    # Confidence level is the WEAKEST link among what was actually
    # established, never the strongest -- a NUMERICALLY_VERIFIED Q/K
    # layout does not make an AMBIGUOUS container "less ambiguous."
    # Overall confidence reflects the STRONGEST evidence tier actually
    # backing the compatibility DECISION -- Layer 1/2 (container/
    # architecture) already gated INVALID/VERIFIED_UNSUPPORTED cases out
    # above (their own result assignments are left untouched, never
    # overwritten here), so by this point the decision was made on
    # Layer 3's Q/K classification specifically. Capping it down to
    # "STRUCTURALLY_VERIFIED" just because container-validity is itself
    # only a structural check would understate a NUMERICALLY_VERIFIED
    # Q/K finding -- the two checks answer different questions and the
    # weaker one does not poison the stronger one here.
    result = profile.data["runtime_compatibility"]["result"]
    if result == "INVALID":
        profile.data["confidence_level"] = "AMBIGUOUS"
    elif result == "VERIFIED_UNSUPPORTED":
        profile.data["confidence_level"] = "DECLARED"
    else:
        profile.data["confidence_level"] = qk["confidence"]

    profile.data["execution_authorization"] = (result == "VERIFIED_COMPATIBLE")


def profile_artifact(path: str, reference_path: str | None, target: str) -> dict:
    profile = Profile(path)
    reader = layer1_container(profile, path)
    if reader is None:
        return profile.finalize()

    architecture, n_layers, n_head, n_head_kv, hidden = layer2_architecture(profile, reader)
    if architecture is None:
        return profile.finalize()

    layer_output_weight_semantics(profile, reader)

    reference_reader = GGUFReader(reference_path) if reference_path else None
    layer3_qk_dialect(profile, reader, reference_reader, reference_path, n_layers, n_head, n_head_kv)

    layer5_decision(profile, target)
    return profile.finalize()


def human_readable(profile: dict) -> str:
    lines = [
        f"Architecture: {profile['declared_architecture'] or 'UNKNOWN'}",
        f"Container: GGUF v{profile['container']['version']}",
        f"Artifact dialect (Q/K): {profile['qk_layout']['classification']}",
        f"Runtime target: {profile['runtime_compatibility']['target']}",
        f"Compatibility: {profile['runtime_compatibility']['result']}",
    ]
    if profile["known_normalization_requirements"]:
        lines.append("Required transformation:")
        for req in profile["known_normalization_requirements"]:
            lines.append(f"  - {req}")
    lines.append("Evidence:")
    for e in profile["evidence"]:
        lines.append(f"  - {e}")
    if profile["unresolved_ambiguities"]:
        lines.append("Unresolved ambiguities:")
        for a in profile["unresolved_ambiguities"]:
            lines.append(f"  - {a}")
    lines.append(f"Confidence: {profile['confidence_level']}")
    lines.append(f"Execution authorization: {'granted' if profile['execution_authorization'] else 'denied'}")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("artifact")
    parser.add_argument("--reference", default=None)
    parser.add_argument("--target", default="canonical-llama.cpp",
                        choices=["canonical-llama.cpp", "orcengine-current"])
    parser.add_argument("--json", default=None)
    args = parser.parse_args()

    profile = profile_artifact(args.artifact, args.reference, args.target)
    print(human_readable(profile))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(profile, f, indent=2, sort_keys=True)

    result = profile["runtime_compatibility"]["result"]
    return 0 if result in ("VERIFIED_COMPATIBLE", "VERIFIED_NORMALIZATION_REQUIRED") else 1


if __name__ == "__main__":
    sys.exit(main())
