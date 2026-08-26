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

Round 2 (Codex authority review + independent Grok Double Check):
this module was found to be able to falsely authorize execution from
an unlabeled direct tensor match, to bind an unvalidated reference
artifact into a trust decision, and to validate Q/K shapes against the
wrong GGUF axis. All three are fixed here -- see the module-level
CHANGELOG comment near the bottom of this docstring... actually see
.orc/fringelab/2026-08-25-fl08-model-compat-profiler/experiments/FL-08/
EXPERIMENT.md's "Round 2 remediation" section for the full account.

Usage:
    python profiler.py ARTIFACT.gguf [--reference REFERENCE.gguf]
                        [--reference-layout raw|canonical]
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
import os
import sys

import numpy as np
from gguf import GGUFReader

SCHEMA_VERSION = 2

KNOWN_ENCODINGS = {"F32", "F16", "Q8_0"}
Q8_0_BLOCK_ELEMENTS = 32
Q8_0_BLOCK_PACKED_BYTES = 34  # 2 bytes f16 scale + 32 bytes int8

REQUIRED_LLAMA_PER_LAYER_SUFFIXES = (
    "attn_norm.weight", "attn_q.weight", "attn_k.weight", "attn_v.weight", "attn_output.weight",
    "ffn_norm.weight", "ffn_gate.weight", "ffn_up.weight", "ffn_down.weight",
)
REQUIRED_LLAMA_GLOBAL_TENSORS = ("token_embd.weight", "output_norm.weight")
# Tensors NOT covered by the Q/K dialect check -- used by pair-identity
# verification (Gate 2) to confirm every OTHER tensor is unchanged.
NON_QK_LAYER_SUFFIXES = tuple(s for s in REQUIRED_LLAMA_PER_LAYER_SUFFIXES
                              if s not in ("attn_q.weight", "attn_k.weight"))

_INTERNAL_HEADER_FIELDS = frozenset({"GGUF.version", "GGUF.tensor_count", "GGUF.kv_count"})

# Confidence ordering, weakest to strongest -- used everywhere an
# "overall = weakest constituent" computation is needed.
_CONFIDENCE_ORDER = ["AMBIGUOUS", "DECLARED", "STRUCTURALLY_VERIFIED", "NUMERICALLY_VERIFIED"]


def _min_confidence(*levels: str) -> str:
    levels = [lv for lv in levels if lv is not None]
    if not levels:
        return "AMBIGUOUS"
    return min(levels, key=_CONFIDENCE_ORDER.index)


def sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def logical_shape(tensor) -> tuple:
    """The authoritative logical numpy shape for a GGUF tensor.

    `ReaderTensor.shape` (a.k.a. `.gguf_dimensions` in some call sites)
    reports dimensions in GGUF's OWN on-disk (`ne[]`) order. gguf-py's
    `.data` materializes the tensor with REVERSED dimensions (row-major
    numpy convention) -- confirmed directly: a real K-projection tensor
    reports `shape=[576, 192]` but `data.shape=(192, 576)`. Every check
    in this module that cares about the LOGICAL output-row axis (the
    axis `official_permute()` reshapes on) must use `tensor.data.shape`,
    never `tensor.shape` -- this single helper is the ONE place that
    decision is made, so it cannot silently drift out of sync between
    call sites again (round-2 remediation: Layer 2's shape-contradiction
    check previously read `tensor.shape[0]` directly and validated the
    wrong axis for every non-square tensor, e.g. K/V/FFN projections)."""
    return tensor.data.shape


def official_permute(weights: np.ndarray, n_head: int, n_head_kv: int | None) -> np.ndarray:
    """Verbatim port of conversion/llama.py's LlamaModel.permute() at the
    pinned commit -- already independently verified against real
    artifacts in Phase 6 round 7 (phase6_gate3_qk_isolation.py), not
    re-derived here, just re-implemented for this independent worktree.
    Operates on `weights.shape[0]` -- callers MUST pass `tensor.data`
    (the logical array), never the raw GGUF dims."""
    if n_head_kv is not None and n_head != n_head_kv:
        n_head = n_head_kv
    return (weights.reshape(n_head, 2, weights.shape[0] // n_head // 2, *weights.shape[1:])
            .swapaxes(1, 2)
            .reshape(weights.shape))


def _tensor_by_name(reader: GGUFReader, name: str):
    for t in reader.tensors:
        if t.name == name:
            return t
    return None


def logical_last_dim(tensor) -> int:
    """The LOGICAL (unpacked) element count of a tensor's last axis.

    For F32/F16 tensors, `logical_shape(tensor)[-1]` already IS the
    logical element count. For Q8_0 tensors, `.data`'s last axis is the
    PACKED BYTE width (round-2 remediation: real Q8_0 artifacts DO
    quantize `token_embd.weight`, per this project's own established
    llama-quantize convention -- the first version of this check
    compared a packed byte count directly against `hidden`, which is
    always wrong by the packing ratio and broke on every real Q8_0
    artifact). Converts packed bytes back to logical elements via the
    same `(bytes/34)*32` relationship `_q8_0_row_validation` checks."""
    shape = logical_shape(tensor)
    if tensor.tensor_type.name == "Q8_0":
        packed_width = shape[-1]
        if packed_width % Q8_0_BLOCK_PACKED_BYTES != 0:
            return -1  # malformed -- caller's equality check will correctly fail
        return (packed_width // Q8_0_BLOCK_PACKED_BYTES) * Q8_0_BLOCK_ELEMENTS
    return shape[-1]


def _encode_field_value(field) -> bytes:
    """Deterministic, FULL-value byte encoding for one GGUF metadata
    field -- deliberately NOT `repr(field.contents())`, which (round-2
    remediation, Grok/Codex finding) can truncate long numpy array
    reprs and silently under-fingerprint large metadata (e.g. token
    lists). Encodes name, GGUF value type tag(s), and every element's
    exact value, with no truncation at any length."""
    parts = [field.name.encode(), repr([t.value for t in field.types]).encode()]
    contents = field.contents()
    if isinstance(contents, list):
        parts.append(str(len(contents)).encode())
        for item in contents:
            parts.append(repr(item).encode())
    else:
        parts.append(repr(contents).encode())
    return b"\x00".join(parts)


def _fingerprint_tensor_inventory(reader: GGUFReader) -> str:
    items = sorted((t.name, tuple(int(d) for d in logical_shape(t)), t.tensor_type.name) for t in reader.tensors)
    return hashlib.sha256(repr(items).encode()).hexdigest()


def _fingerprint_metadata(reader: GGUFReader) -> str:
    h = hashlib.sha256()
    for key in sorted(reader.fields):
        if key in _INTERNAL_HEADER_FIELDS:
            continue
        h.update(_encode_field_value(reader.fields[key]))
    return h.hexdigest()


def _fingerprint_tokenizer(reader: GGUFReader) -> str | None:
    tok_keys = sorted(k for k in reader.fields if k.startswith("tokenizer."))
    if not tok_keys:
        return None
    h = hashlib.sha256()
    for key in tok_keys:
        h.update(_encode_field_value(reader.fields[key]))
    return h.hexdigest()


def _meta_int(reader: GGUFReader, key: str) -> int | None:
    f = reader.fields.get(key)
    return int(f.contents()) if f else None


class ArtifactValidation:
    """Result of running the ONE shared container+architecture
    validation path (Gate 2: "one shared, fail-closed validation path"
    -- used identically for the primary artifact and the paired
    reference, so the reference can no longer bypass Layer 1/2)."""

    def __init__(self, path: str):
        self.path = path
        self.evidence: list[str] = []
        self.ambiguities: list[str] = []
        self.sha256: str | None = None
        self.file_size_bytes: int | None = None
        self.container_version: int | None = None
        self.container_valid = False
        self.declared_architecture: str | None = None
        self.n_layers = 0
        self.n_head = 0
        self.n_head_kv = 0
        self.hidden = 0
        self.reader: GGUFReader | None = None
        self.tensor_inventory_fingerprint: str | None = None
        self.metadata_fingerprint: str | None = None
        self.tokenizer_fingerprint: str | None = None
        self.quantization_formats: list[str] = []
        # Terminal outcome for THIS artifact alone, independent of any
        # comparison against a reference: INVALID, VERIFIED_UNSUPPORTED,
        # or None (structurally sound, architecture-validated, ready
        # for Layer 3).
        self.terminal_result: str | None = None
        self.architecture_confidence = "AMBIGUOUS"

    @property
    def ok(self) -> bool:
        return self.terminal_result is None and self.reader is not None


def validate_artifact(path: str) -> ArtifactValidation:
    """Layers 1+2 (container, architecture) -- the ONE shared path used
    for both the primary artifact and the paired reference. Never
    raises: any parse/structural failure is captured as `terminal_result
    = "INVALID"` with a human-readable ambiguity, never an uncaught
    exception (round-2 remediation, Gate 2: "Missing reference file:
    structured non-authorizing result, not an uncaught exception")."""
    v = ArtifactValidation(path)

    if not os.path.isfile(path):
        v.ambiguities.append(f"artifact not found at {path!r}")
        v.terminal_result = "INVALID"
        return v

    v.sha256 = sha256_file(path)
    v.evidence.append(f"artifact sha256={v.sha256}")
    v.file_size_bytes = os.path.getsize(path)

    try:
        reader = GGUFReader(path)
    except Exception as ex:  # noqa: BLE001 -- any parse failure is INVALID, never a crash
        v.ambiguities.append(f"container failed to parse: {ex!r}")
        v.terminal_result = "INVALID"
        return v

    version = _meta_int(reader, "GGUF.version")
    v.container_version = version
    if version not in (2, 3):
        v.ambiguities.append(f"unrecognized GGUF version {version!r} (this profiler only classifies v2/v3)")
        v.terminal_result = "INVALID"
        return v

    out_of_bounds, seen_names, duplicates = [], set(), set()
    for t in reader.tensors:
        if t.name in seen_names:
            duplicates.add(t.name)
        seen_names.add(t.name)
        end = int(t.data_offset) + t.data.nbytes
        if end > v.file_size_bytes:
            out_of_bounds.append(t.name)
    if duplicates:
        v.ambiguities.append(f"duplicate tensor names: {sorted(duplicates)}")
        v.terminal_result = "INVALID"
        return v
    if out_of_bounds:
        v.ambiguities.append(f"tensor(s) exceed file bounds: {out_of_bounds}")
        v.terminal_result = "INVALID"
        return v

    unknown_encodings = sorted({t.tensor_type.name for t in reader.tensors} - KNOWN_ENCODINGS)
    if unknown_encodings:
        v.ambiguities.append(f"tensor encoding(s) not in this profiler's known set: {unknown_encodings}")

    v.container_valid = True
    v.evidence.append(f"container valid: GGUF v{version}, {len(reader.tensors)} tensors, "
                      f"0 out-of-bounds, 0 duplicates")
    v.quantization_formats = sorted({t.tensor_type.name for t in reader.tensors})
    v.reader = reader

    # --- Layer 2: architecture validation ---
    arch = reader.fields.get("general.architecture")
    v.declared_architecture = arch.contents() if arch else None
    if v.declared_architecture != "llama":
        v.ambiguities.append(f"declared architecture {v.declared_architecture!r} is not 'llama' -- "
                             f"this experiment's Layer 3 (Q/K dialect) only supports llama")
        v.terminal_result = "VERIFIED_UNSUPPORTED"
        return v

    n_layers = _meta_int(reader, "llama.block_count")
    n_head = _meta_int(reader, "llama.attention.head_count")
    n_head_kv = _meta_int(reader, "llama.attention.head_count_kv")
    hidden = _meta_int(reader, "llama.embedding_length")

    if None in (n_layers, n_head, n_head_kv, hidden):
        v.ambiguities.append("required llama.* metadata missing "
                             f"(block_count={n_layers} head_count={n_head} "
                             f"head_count_kv={n_head_kv} embedding_length={hidden})")
        v.terminal_result = "INVALID"
        return v
    if n_layers <= 0 or n_head <= 0 or n_head_kv <= 0 or hidden <= 0:
        v.ambiguities.append(f"non-positive required geometry: block_count={n_layers} head_count={n_head} "
                             f"head_count_kv={n_head_kv} embedding_length={hidden}")
        v.terminal_result = "INVALID"
        return v
    if hidden % n_head != 0:
        v.ambiguities.append(f"embedding_length={hidden} not evenly divisible by head_count={n_head}")
        v.terminal_result = "INVALID"
        return v

    rope_dim = reader.fields.get("llama.rope.dimension_count")
    rope_freq = reader.fields.get("llama.rope.freq_base")
    if rope_dim is None or rope_freq is None:
        v.ambiguities.append("llama.rope.dimension_count / llama.rope.freq_base metadata missing -- "
                             "RoPE application semantics cannot be fully corroborated")
        # Recorded as an ambiguity, not INVALID -- some real artifacts
        # legitimately omit these (defaults apply); per this
        # experiment's own design, missing-but-not-contradictory
        # optional metadata proceeds with the ambiguity recorded, not
        # silently passed.

    highest_layer_seen = -1
    for t in reader.tensors:
        if t.name.startswith("blk."):
            try:
                idx = int(t.name.split(".")[1])
            except (IndexError, ValueError):
                continue
            highest_layer_seen = max(highest_layer_seen, idx)
    if highest_layer_seen + 1 != n_layers:
        v.ambiguities.append(f"declared block_count={n_layers} but highest blk.N.* index seen is "
                             f"{highest_layer_seen} ({highest_layer_seen + 1} layers present)")
        v.terminal_result = "INVALID"
        return v

    missing_tensors, shape_contradictions = [], []
    for name in REQUIRED_LLAMA_GLOBAL_TENSORS:
        if _tensor_by_name(reader, name) is None:
            missing_tensors.append(name)
    q_head_dim_half = (hidden // n_head) // 2
    kv_row_expected_head_dim = hidden // n_head  # head_dim is shared between Q and K/V in this architecture
    for i in range(n_layers):
        for suffix in REQUIRED_LLAMA_PER_LAYER_SUFFIXES:
            name = f"blk.{i}.{suffix}"
            t = _tensor_by_name(reader, name)
            if t is None:
                missing_tensors.append(name)
                continue
            rows = logical_shape(t)[0]
            if suffix == "attn_q.weight":
                if rows != n_head * kv_row_expected_head_dim or q_head_dim_half == 0:
                    shape_contradictions.append(
                        f"{name}: logical rows={rows}, expected {n_head}*{kv_row_expected_head_dim} "
                        f"={n_head * kv_row_expected_head_dim} for head_count={n_head} "
                        f"(and head_dim must be evenly halvable for permute)")
            elif suffix == "attn_k.weight":
                if rows != n_head_kv * kv_row_expected_head_dim:
                    shape_contradictions.append(
                        f"{name}: logical rows={rows}, expected {n_head_kv}*{kv_row_expected_head_dim} "
                        f"={n_head_kv * kv_row_expected_head_dim} for head_count_kv={n_head_kv}")
            elif suffix == "attn_v.weight":
                if rows != n_head_kv * kv_row_expected_head_dim:
                    shape_contradictions.append(
                        f"{name}: logical rows={rows}, expected {n_head_kv * kv_row_expected_head_dim} "
                        f"(V shares head_dim/head_count_kv with K)")
            elif suffix == "attn_output.weight":
                if rows != hidden:
                    shape_contradictions.append(f"{name}: logical rows={rows}, expected hidden={hidden}")
            elif suffix in ("attn_norm.weight", "ffn_norm.weight"):
                if rows != hidden:
                    shape_contradictions.append(f"{name}: logical rows={rows}, expected hidden={hidden}")
            elif suffix == "ffn_down.weight":
                if rows != hidden:
                    shape_contradictions.append(f"{name}: logical rows={rows}, expected hidden={hidden} (output)")
            # ffn_gate.weight / ffn_up.weight: intermediate size is
            # architecture-specific and not independently declared
            # anywhere this profiler reads -- only cross-checked for
            # mutual consistency (gate/up must match), not an absolute
            # expected value.

    ffn_gate_rows, ffn_up_rows = {}, {}
    for i in range(n_layers):
        g = _tensor_by_name(reader, f"blk.{i}.ffn_gate.weight")
        u = _tensor_by_name(reader, f"blk.{i}.ffn_up.weight")
        if g is not None:
            ffn_gate_rows[i] = logical_shape(g)[0]
        if u is not None:
            ffn_up_rows[i] = logical_shape(u)[0]
    for i in range(n_layers):
        if i in ffn_gate_rows and i in ffn_up_rows and ffn_gate_rows[i] != ffn_up_rows[i]:
            shape_contradictions.append(f"blk.{i}: ffn_gate.weight rows={ffn_gate_rows[i]} != "
                                        f"ffn_up.weight rows={ffn_up_rows[i]}")

    emb = _tensor_by_name(reader, "token_embd.weight")
    if emb is not None and logical_last_dim(emb) != hidden:
        shape_contradictions.append(f"token_embd.weight logical last dim={logical_last_dim(emb)} "
                                    f"(raw axis shape={logical_shape(emb)}), expected hidden={hidden}")
    norm = _tensor_by_name(reader, "output_norm.weight")
    if norm is not None and logical_shape(norm)[0] != hidden:
        shape_contradictions.append(f"output_norm.weight rows={logical_shape(norm)[0]}, expected hidden={hidden}")

    if missing_tensors:
        v.ambiguities.append(f"required tensor(s) missing for declared architecture/layer count: "
                             f"{missing_tensors[:10]}{'...' if len(missing_tensors) > 10 else ''}")
        v.terminal_result = "INVALID"
        return v
    if shape_contradictions:
        v.ambiguities.append(f"tensor shape contradicts declared architecture geometry: {shape_contradictions}")
        v.terminal_result = "INVALID"
        return v

    v.evidence.append(f"architecture 'llama' validated: {n_layers} layers, all required tensors present, "
                      f"logical shapes consistent with head_count={n_head}/head_count_kv={n_head_kv}/"
                      f"hidden={hidden} (validated against tensor.data.shape, the logical axis)")
    v.n_layers, v.n_head, v.n_head_kv, v.hidden = n_layers, n_head, n_head_kv, hidden
    v.tensor_inventory_fingerprint = _fingerprint_tensor_inventory(reader)
    v.metadata_fingerprint = _fingerprint_metadata(reader)
    v.tokenizer_fingerprint = _fingerprint_tokenizer(reader)
    v.architecture_confidence = "STRUCTURALLY_VERIFIED"
    return v


def _validation_to_profile_dict(v: ArtifactValidation) -> dict:
    return {
        "path": v.path,
        "sha256": v.sha256,
        "file_size_bytes": v.file_size_bytes,
        "container": {"type": "GGUF", "version": v.container_version, "valid": v.container_valid,
                      "evidence": list(v.evidence)},
        "declared_architecture": v.declared_architecture,
        "tensor_inventory_fingerprint": v.tensor_inventory_fingerprint,
        "metadata_fingerprint": v.metadata_fingerprint,
        "tokenizer_fingerprint": v.tokenizer_fingerprint,
        "quantization_formats": v.quantization_formats,
        "terminal_result": v.terminal_result,
    }


# ---------------------------------------------------------------------
# Layer 3: Q/K dialect identification
# ---------------------------------------------------------------------

def _q8_0_row_validation(a_shape: tuple, hidden: int) -> str | None:
    """Validates the packed-Q8_0 geometry BEFORE any byte comparison is
    trusted (Gate 5). Returns an error string, or None if valid."""
    if hidden % Q8_0_BLOCK_ELEMENTS != 0:
        return f"logical input width {hidden} is not divisible by the Q8_0 block size {Q8_0_BLOCK_ELEMENTS}"
    expected_packed_width = (hidden // Q8_0_BLOCK_ELEMENTS) * Q8_0_BLOCK_PACKED_BYTES
    if len(a_shape) != 2 or a_shape[1] != expected_packed_width:
        return (f"packed row width {a_shape[1] if len(a_shape) == 2 else a_shape} does not equal "
                f"(hidden/{Q8_0_BLOCK_ELEMENTS})*{Q8_0_BLOCK_PACKED_BYTES}={expected_packed_width}")
    return None


def _compare_qk_tensor(a, b, n_head: int, ref_n_head_kv: int, hidden: int) -> tuple[str, str]:
    """Compares one Q or K tensor pair under all 3 hypotheses (direct,
    a-is-raw-relative-to-b, b-is-raw-relative-to-a). Returns
    (outcome, detail) where outcome is one of: "DIRECT", "A_RAW_REL_B",
    "B_RAW_REL_A", "NEITHER", "MULTIPLE", "TYPE_MISMATCH", "SHAPE_MISMATCH",
    "BAD_Q8_0_GEOMETRY"."""
    if a.tensor_type.name != b.tensor_type.name:
        return "TYPE_MISMATCH", f"artifact encoding {a.tensor_type.name} != reference encoding {b.tensor_type.name}"
    a_shape, b_shape = logical_shape(a), logical_shape(b)
    if a_shape != b_shape:
        return "SHAPE_MISMATCH", f"{a_shape} != {b_shape}"

    if a.tensor_type.name == "Q8_0":
        err = _q8_0_row_validation(a_shape, hidden)
        if err:
            return "BAD_Q8_0_GEOMETRY", err
        # Packed Q8_0 rows: row reordering commutes with per-block
        # quantization (each 32-element block is quantized independently
        # WITHIN a row; the permutation only reorders whole rows, never
        # touches within-row byte content) -- so applying the row-only
        # permutation to the packed uint8 array is valid, but ONLY given
        # the geometry check above holds and both sides are confirmed
        # Q8_0 with matching shape.

    direct_match = np.array_equal(a.data, b.data)
    a_is_raw_relative_to_b = np.array_equal(official_permute(a.data.copy(), n_head, ref_n_head_kv), b.data)
    b_is_raw_relative_to_a = np.array_equal(official_permute(b.data.copy(), n_head, ref_n_head_kv), a.data)

    hits = [h for h, flag in (("DIRECT", direct_match), ("A_RAW_REL_B", a_is_raw_relative_to_b),
                              ("B_RAW_REL_A", b_is_raw_relative_to_a)) if flag]
    if len(hits) == 1:
        return hits[0], "unique hypothesis match"
    if len(hits) == 0:
        return "NEITHER", "no hypothesis matched"
    return "MULTIPLE", f"hypotheses {hits} matched simultaneously (degenerate/no-op geometry)"


def layer3_qk_dialect(profile_data: dict, evidence: list, ambiguities: list,
                      artifact_reader: GGUFReader, reference_reader: GGUFReader | None,
                      reference_label: str | None, reference_layout_declared: str | None,
                      n_layers: int, n_head: int, n_head_kv: int, hidden: int) -> None:
    """Layer 3: Q/K dialect identification.

    Round-2 remediation: a DIRECT tensor match (artifact's Q/K byte-
    identical to the reference's) no longer produces an absolute
    RAW_HF/CANONICAL_LLAMA_CPP label -- it proves only that the two
    artifacts use the SAME layout as each other, not which ABSOLUTE
    layout that is. That case is now `SAME_LAYOUT_UNKNOWN` unless an
    operator has explicitly DECLARED the reference's absolute layout
    (`reference_layout_declared`), in which case the label is derived
    from that declaration at DECLARED confidence -- never
    NUMERICALLY_VERIFIED, and per the authorization invariant (Gate 4)
    DECLARED confidence never authorizes execution.

    The two permutation-direction hypotheses are UNCHANGED and remain
    valid absolute-layout evidence on their own: `official_permute()`
    is the EXTERNALLY fixed, independently-verified raw-to-canonical
    transform (not something inferred from which file happens to be
    used as reference), so "permute(X) == Y" proves X is raw-form and Y
    is canonical-form, regardless of reference identity."""
    qk = {
        "classification": "UNKNOWN", "confidence": "AMBIGUOUS",
        "layers_checked": 0, "layers_total": n_layers,
        "qk_tensors_checked": 0, "qk_tensors_total": n_layers * 2,
        "per_layer_consistent": False,
    }
    profile_data["qk_layout"] = qk

    if reference_reader is None:
        ambiguities.append("no paired reference artifact supplied -- Q/K layout cannot be "
                           "classified from a single artifact (tensor names alone are not evidence, "
                           "per this profiler's explicit design)")
        return

    outcomes: dict[str, int] = {}
    details = []
    layers_with_full_qk = 0
    for i in range(n_layers):
        layer_outcomes = []
        for kind, ref_n_head_kv in (("attn_q", n_head), ("attn_k", n_head_kv)):
            name = f"blk.{i}.{kind}.weight"
            a = _tensor_by_name(artifact_reader, name)
            b = _tensor_by_name(reference_reader, name)
            if a is None or b is None:
                outcome, detail = "MISSING_IN_ONE_SIDE", "tensor absent on one side"
            else:
                outcome, detail = _compare_qk_tensor(a, b, n_head, ref_n_head_kv, hidden)
            outcomes[outcome] = outcomes.get(outcome, 0) + 1
            layer_outcomes.append(outcome)
            if outcome not in ("DIRECT", "A_RAW_REL_B", "B_RAW_REL_A"):
                details.append((name, outcome, detail))
        if all(o in ("DIRECT", "A_RAW_REL_B", "B_RAW_REL_A") for o in layer_outcomes) and \
           len(set(layer_outcomes)) == 1:
            layers_with_full_qk += 1

    checked = outcomes.get("DIRECT", 0) + outcomes.get("A_RAW_REL_B", 0) + outcomes.get("B_RAW_REL_A", 0)
    total = n_layers * 2
    qk["qk_tensors_checked"] = checked
    qk["qk_tensors_total"] = total
    qk["layers_checked"] = layers_with_full_qk
    qk["layers_total"] = n_layers

    bad_outcomes = {k: c for k, c in outcomes.items() if k not in ("DIRECT", "A_RAW_REL_B", "B_RAW_REL_A")}
    if bad_outcomes:
        ambiguities.append(f"Q/K comparison against reference {reference_label!r}: "
                           f"{sum(bad_outcomes.values())} tensor(s) did not cleanly resolve one hypothesis: "
                           f"{details[:5]}{'...' if len(details) > 5 else ''}")
        qk["classification"] = "AMBIGUOUS"
        qk["confidence"] = "AMBIGUOUS"
        return

    distinct_hypotheses = {k for k, c in outcomes.items() if c > 0}
    if len(distinct_hypotheses) != 1:
        ambiguities.append(f"inconsistent per-tensor classification across layers: {outcomes} -- "
                           f"a single artifact should not mix Q/K conventions")
        qk["classification"] = "AMBIGUOUS"
        qk["confidence"] = "AMBIGUOUS"
        return

    winning = next(iter(distinct_hypotheses))
    qk["per_layer_consistent"] = True

    if winning == "A_RAW_REL_B":
        qk["classification"] = "RAW_HF"
        qk["confidence"] = "NUMERICALLY_VERIFIED"
        evidence.append(f"Q/K fingerprint matches RAW_HF convention: permute(this artifact's Q/K) == "
                        f"reference {reference_label!r}'s Q/K (this artifact is raw relative to the "
                        f"reference, per the externally-fixed permute() direction), verified on "
                        f"{checked}/{total} tensors ({layers_with_full_qk}/{n_layers} layers)")
        return
    if winning == "B_RAW_REL_A":
        qk["classification"] = "CANONICAL_LLAMA_CPP"
        qk["confidence"] = "NUMERICALLY_VERIFIED"
        evidence.append(f"Q/K fingerprint matches CANONICAL_LLAMA_CPP convention: permute(reference "
                        f"{reference_label!r}'s Q/K) == this artifact's Q/K (the reference is raw "
                        f"relative to this artifact, per the externally-fixed permute() direction), "
                        f"verified on {checked}/{total} tensors ({layers_with_full_qk}/{n_layers} layers)")
        return

    # winning == "DIRECT": same layout as reference, but WHICH absolute
    # layout that is cannot be inferred from equality alone -- equality
    # is symmetric and proves nothing about which side (if either) is
    # canonical. This is the round-2 fix for the false-authorization
    # defect: two byte-identical RAW_HF artifacts must NOT be labeled
    # CANONICAL_LLAMA_CPP just because they match each other.
    if reference_layout_declared in ("raw", "canonical"):
        qk["classification"] = "RAW_HF" if reference_layout_declared == "raw" else "CANONICAL_LLAMA_CPP"
        qk["confidence"] = "DECLARED"
        evidence.append(f"Q/K is byte-identical to reference {reference_label!r} ({checked}/{total} tensors, "
                        f"{layers_with_full_qk}/{n_layers} layers); absolute layout label derived from the "
                        f"OPERATOR-DECLARED reference layout ({reference_layout_declared!r}), NOT from "
                        f"numerical proof -- confidence is DECLARED, not NUMERICALLY_VERIFIED, and this "
                        f"alone must never authorize execution")
        return

    qk["classification"] = "SAME_LAYOUT_UNKNOWN"
    qk["confidence"] = "AMBIGUOUS"
    ambiguities.append(f"Q/K is byte-identical to reference {reference_label!r} on all {checked}/{total} "
                       f"tensors checked, but this proves only that both artifacts use the SAME layout as "
                       f"each other -- NOT which absolute layout (raw-HF or canonical) that is. No "
                       f"operator-declared reference layout was supplied. Classification: "
                       f"SAME_LAYOUT_UNKNOWN, non-authorizing.")


def layer_output_weight_semantics(profile_data: dict, evidence: list, ambiguities: list,
                                  reader: GGUFReader) -> None:
    out_w = _tensor_by_name(reader, "output.weight")
    emb_w = _tensor_by_name(reader, "token_embd.weight")
    if out_w is None:
        profile_data["output_weight_semantics"] = "ABSENT"
        evidence.append("output.weight tensor absent -- tied head materialized from token_embd.weight "
                        "at runtime (canonical-converter convention)")
        return
    if emb_w is None:
        profile_data["output_weight_semantics"] = "UNKNOWN"
        ambiguities.append("output.weight present but token_embd.weight missing -- cannot classify tie status")
        return
    if logical_shape(out_w) == logical_shape(emb_w) and np.array_equal(out_w.data, emb_w.data):
        profile_data["output_weight_semantics"] = "TIED_PHYSICALLY_DUPLICATED"
        evidence.append("output.weight present and byte-identical to token_embd.weight "
                        "(logically tied, physically duplicated)")
    else:
        profile_data["output_weight_semantics"] = "UNTIED"
        evidence.append("output.weight present and NOT identical to token_embd.weight (untied)")


# ---------------------------------------------------------------------
# Pair identity (Gate 2): does the reference actually describe the SAME
# underlying model, independent of the Q/K relationship itself?
# ---------------------------------------------------------------------

def verify_pair_identity(artifact_v: ArtifactValidation, reference_v: ArtifactValidation) -> dict:
    """Compares every NON-Q/K tensor (V, attention-output, norms, FFN,
    embedding, output head) between the artifact and reference. A
    tampered V/norm/FFN/embedding tensor must NOT retain a "verified"
    pair identity even if Q/K still matches -- that is exactly the
    attack this check exists to catch."""
    result = {"status": "UNVERIFIED", "evidence": []}

    a_reader, b_reader = artifact_v.reader, reference_v.reader
    if a_reader is None or b_reader is None:
        result["evidence"].append("one side failed container/architecture validation -- pair identity "
                                  "cannot be established")
        return result

    if artifact_v.declared_architecture != reference_v.declared_architecture:
        result["evidence"].append(f"architecture mismatch: {artifact_v.declared_architecture!r} != "
                                  f"{reference_v.declared_architecture!r}")
        return result
    if (artifact_v.n_layers, artifact_v.n_head, artifact_v.n_head_kv, artifact_v.hidden) != \
       (reference_v.n_layers, reference_v.n_head, reference_v.n_head_kv, reference_v.hidden):
        result["evidence"].append(
            f"geometry mismatch: artifact(layers={artifact_v.n_layers},head={artifact_v.n_head},"
            f"head_kv={artifact_v.n_head_kv},hidden={artifact_v.hidden}) != "
            f"reference(layers={reference_v.n_layers},head={reference_v.n_head},"
            f"head_kv={reference_v.n_head_kv},hidden={reference_v.hidden})")
        return result

    a_types = {t.tensor_type.name for t in a_reader.tensors}
    b_types = {t.tensor_type.name for t in b_reader.tensors}
    if a_types != b_types:
        result["evidence"].append(f"different tensor encodings present ({sorted(a_types)} vs "
                                  f"{sorted(b_types)}) -- byte comparison of non-Q/K tensors cannot "
                                  f"prove identity across encodings, and no provenance/hash chain was "
                                  f"supplied to bind them another way -- pair identity left UNVERIFIED")
        return result

    names_to_check = list(REQUIRED_LLAMA_GLOBAL_TENSORS)
    for i in range(artifact_v.n_layers):
        for suffix in NON_QK_LAYER_SUFFIXES:
            names_to_check.append(f"blk.{i}.{suffix}")
    # output.weight is optional (tied models omit it) -- check it too
    # when present on both sides.
    if _tensor_by_name(a_reader, "output.weight") is not None and \
       _tensor_by_name(b_reader, "output.weight") is not None:
        names_to_check.append("output.weight")

    mismatches, missing = [], []
    for name in names_to_check:
        a = _tensor_by_name(a_reader, name)
        b = _tensor_by_name(b_reader, name)
        if a is None or b is None:
            missing.append(name)
            continue
        if logical_shape(a) != logical_shape(b):
            mismatches.append((name, "SHAPE_MISMATCH"))
            continue
        if not np.array_equal(a.data, b.data):
            mismatches.append((name, "VALUE_MISMATCH"))

    if missing:
        result["evidence"].append(f"non-Q/K tensor(s) missing on one side: {missing[:10]}"
                                  f"{'...' if len(missing) > 10 else ''}")
        return result
    if mismatches:
        result["evidence"].append(f"{len(mismatches)} non-Q/K tensor(s) differ between artifact and "
                                  f"reference (V/attn_output/norm/FFN/embedding) -- these must be "
                                  f"identical for two artifacts of the same underlying model to differ "
                                  f"ONLY in Q/K layout: {mismatches[:5]}{'...' if len(mismatches) > 5 else ''}")
        return result

    result["status"] = "VERIFIED"
    result["evidence"].append(f"all {len(names_to_check)} non-Q/K tensors (V, attention-output, norms, "
                              f"FFN, embedding{', output head' if 'output.weight' in names_to_check else ''}) "
                              f"byte-identical between artifact and reference -- same underlying model, "
                              f"differing (if at all) only in Q/K layout")
    return result


# ---------------------------------------------------------------------
# Layer 5: decision + authorization invariant
# ---------------------------------------------------------------------

def layer5_decision(profile_data: dict, target: str, artifact_terminal_result: str | None,
                    reference_present: bool, reference_terminal_result: str | None,
                    pair_identity_status: str) -> None:
    profile_data["runtime_compatibility"]["target"] = target
    qk = profile_data["qk_layout"]

    if artifact_terminal_result is not None:
        profile_data["runtime_compatibility"]["result"] = artifact_terminal_result
    elif reference_present and reference_terminal_result is not None:
        profile_data["runtime_compatibility"]["result"] = "AMBIGUOUS"
    elif qk["classification"] in ("AMBIGUOUS", "UNKNOWN", "SAME_LAYOUT_UNKNOWN"):
        profile_data["runtime_compatibility"]["result"] = "AMBIGUOUS"
    elif reference_present and pair_identity_status != "VERIFIED":
        profile_data["runtime_compatibility"]["result"] = "AMBIGUOUS"
    elif target == "canonical-llama.cpp":
        if qk["classification"] == "CANONICAL_LLAMA_CPP":
            profile_data["runtime_compatibility"]["result"] = "VERIFIED_COMPATIBLE"
        elif qk["classification"] == "RAW_HF":
            profile_data["runtime_compatibility"]["result"] = "VERIFIED_NORMALIZATION_REQUIRED"
            profile_data["known_normalization_requirements"].append(
                "Q/K RoPE-layout permutation required (raw-HF -> canonical llama.cpp interleaving)")
    elif target == "orcengine-current":
        if qk["classification"] == "RAW_HF":
            profile_data["runtime_compatibility"]["result"] = "VERIFIED_COMPATIBLE"
            profile_data["evidence"].append("OrcEngine's CURRENT loader expects raw-HF Q/K layout "
                                            "(Phase 6 round 7 Gate 4 finding, see the separate Phase 6 "
                                            "worktree/branch evidence -- not present in this tree) -- "
                                            "this artifact matches as-is")
        elif qk["classification"] == "CANONICAL_LLAMA_CPP":
            profile_data["runtime_compatibility"]["result"] = "VERIFIED_NORMALIZATION_REQUIRED"
            profile_data["known_normalization_requirements"].append(
                "Q/K RoPE-layout un-permutation required (canonical llama.cpp -> raw-HF, to match "
                "OrcEngine's CURRENT loader's undocumented expectation -- see the separate Phase 6 "
                "worktree/branch evidence, not present in this tree)")
    else:
        profile_data["unresolved_ambiguities"].append(f"unrecognized runtime_compatibility target {target!r}")
        profile_data["runtime_compatibility"]["result"] = "AMBIGUOUS"

    result = profile_data["runtime_compatibility"]["result"]

    # --- per-axis confidence, Gate 6 ---
    conf = profile_data["confidence"]
    conf["qk_dialect"] = qk["confidence"]
    conf["pair_identity"] = "NUMERICALLY_VERIFIED" if pair_identity_status == "VERIFIED" else "AMBIGUOUS"
    if not reference_present:
        conf["pair_identity"] = "AMBIGUOUS"
        conf["reference"] = "AMBIGUOUS"
    else:
        conf["reference"] = "STRUCTURALLY_VERIFIED" if reference_terminal_result is None else "AMBIGUOUS"

    if result == "INVALID":
        overall = "AMBIGUOUS"
    elif result == "VERIFIED_UNSUPPORTED":
        overall = "DECLARED"
    else:
        overall = _min_confidence(conf["container"], conf["architecture"], conf["reference"],
                                  conf["pair_identity"], conf["qk_dialect"])
    profile_data["confidence_level"] = overall

    # --- authorization invariant, Gate 4: true ONLY when every listed
    # condition holds; false the instant any one fails. ---
    authorization_conditions = {
        "container_valid": profile_data["container"]["valid"],
        "architecture_valid": conf["architecture"] != "AMBIGUOUS",
        "encoding_supported": not any("encoding(s) not in this profiler's known set" in a
                                      for a in profile_data["unresolved_ambiguities"]),
        "reference_valid": reference_present and reference_terminal_result is None,
        "pair_identity_verified": pair_identity_status == "VERIFIED",
        "qk_numerically_verified": qk["confidence"] == "NUMERICALLY_VERIFIED",
        "qk_tensors_nonzero_and_complete": qk["qk_tensors_total"] > 0 and
                                           qk["qk_tensors_checked"] == qk["qk_tensors_total"],
        "per_layer_consistent": qk["per_layer_consistent"],
        "matches_target_as_is": result == "VERIFIED_COMPATIBLE",
        "normalization_not_required": result != "VERIFIED_NORMALIZATION_REQUIRED",
        "no_unresolved_ambiguity": len(profile_data["unresolved_ambiguities"]) == 0,
    }
    profile_data["authorization_conditions"] = authorization_conditions
    profile_data["execution_authorization"] = all(authorization_conditions.values())


def profile_artifact(path: str, reference_path: str | None, target: str,
                     reference_layout_declared: str | None = None) -> dict:
    evidence: list[str] = []
    ambiguities: list[str] = []

    artifact_v = validate_artifact(path)
    evidence.extend(artifact_v.evidence)
    ambiguities.extend(artifact_v.ambiguities)

    profile_data = {
        "schema_version": SCHEMA_VERSION,
        "artifact": {"path": artifact_v.path, "sha256": artifact_v.sha256,
                    "file_size_bytes": artifact_v.file_size_bytes},
        "container": {"type": "GGUF", "version": artifact_v.container_version,
                     "valid": artifact_v.container_valid, "evidence": list(artifact_v.evidence)},
        "declared_architecture": artifact_v.declared_architecture,
        "tensor_inventory_fingerprint": artifact_v.tensor_inventory_fingerprint,
        "metadata_fingerprint": artifact_v.metadata_fingerprint,
        "tokenizer_fingerprint": artifact_v.tokenizer_fingerprint,
        "output_weight_semantics": "UNKNOWN",
        "qk_layout": {
            "classification": "UNKNOWN", "confidence": "AMBIGUOUS",
            "layers_checked": 0, "layers_total": 0,
            "qk_tensors_checked": 0, "qk_tensors_total": 0,
            "per_layer_consistent": False,
        },
        "quantization_formats": artifact_v.quantization_formats,
        "known_normalization_requirements": [],
        "reference": None,
        "pair_identity": {"status": "UNVERIFIED", "evidence": []},
        "runtime_compatibility": {"target": None, "result": "AMBIGUOUS"},
        "confidence": {"container": "AMBIGUOUS", "architecture": "AMBIGUOUS", "reference": "AMBIGUOUS",
                      "pair_identity": "AMBIGUOUS", "qk_dialect": "AMBIGUOUS"},
        "confidence_level": "AMBIGUOUS",
        "evidence": evidence,
        "unresolved_ambiguities": ambiguities,
        "execution_authorization": False,
    }
    profile_data["confidence"]["container"] = "STRUCTURALLY_VERIFIED" if artifact_v.container_valid else "AMBIGUOUS"
    profile_data["confidence"]["architecture"] = artifact_v.architecture_confidence

    if not artifact_v.ok:
        layer5_decision(profile_data, target, artifact_v.terminal_result, False, None, "UNVERIFIED")
        return profile_data

    layer_output_weight_semantics(profile_data, evidence, ambiguities, artifact_v.reader)

    reference_present = reference_path is not None
    reference_terminal_result = None
    pair_identity_status = "UNVERIFIED"

    if reference_present:
        reference_v = validate_artifact(reference_path)
        profile_data["reference"] = _validation_to_profile_dict(reference_v)
        profile_data["reference"]["pair_identity"] = {"status": "UNVERIFIED", "evidence": []}
        reference_terminal_result = reference_v.terminal_result
        for a in reference_v.ambiguities:
            ambiguities.append(f"[reference] {a}")

        if reference_v.ok:
            pair = verify_pair_identity(artifact_v, reference_v)
            profile_data["pair_identity"] = pair
            profile_data["reference"]["pair_identity"] = pair
            pair_identity_status = pair["status"]
            evidence.extend(f"[pair-identity] {e}" for e in pair["evidence"])
            if pair["status"] != "VERIFIED":
                ambiguities.extend(f"[pair-identity] {e}" for e in pair["evidence"])

            layer3_qk_dialect(profile_data, evidence, ambiguities, artifact_v.reader, reference_v.reader,
                              reference_path, reference_layout_declared,
                              artifact_v.n_layers, artifact_v.n_head, artifact_v.n_head_kv, artifact_v.hidden)
        else:
            profile_data["qk_layout"]["layers_total"] = artifact_v.n_layers
            profile_data["qk_layout"]["qk_tensors_total"] = artifact_v.n_layers * 2
            ambiguities.append(f"reference artifact {reference_path!r} failed validation "
                               f"({reference_v.terminal_result}) -- Q/K dialect cannot be classified "
                               f"against an unvalidated reference")
    else:
        layer3_qk_dialect(profile_data, evidence, ambiguities, artifact_v.reader, None, None, None,
                          artifact_v.n_layers, artifact_v.n_head, artifact_v.n_head_kv, artifact_v.hidden)

    layer5_decision(profile_data, target, None, reference_present, reference_terminal_result,
                    pair_identity_status)
    return profile_data


def human_readable(profile: dict) -> str:
    lines = [
        f"Architecture: {profile['declared_architecture'] or 'UNKNOWN'}",
        f"Container: GGUF v{profile['container']['version']}",
        f"Artifact dialect (Q/K): {profile['qk_layout']['classification']}",
        f"Pair identity: {profile['pair_identity']['status']}",
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
    parser.add_argument("--reference-layout", default=None, choices=["raw", "canonical"],
                        help="Operator-declared absolute layout of --reference. Only used when the "
                             "artifact and reference are byte-identical on Q/K (a direct-match case, which "
                             "otherwise cannot be resolved to an absolute dialect). Confidence is DECLARED, "
                             "never NUMERICALLY_VERIFIED, and never alone authorizes execution.")
    parser.add_argument("--target", default="canonical-llama.cpp",
                        choices=["canonical-llama.cpp", "orcengine-current"])
    parser.add_argument("--json", default=None)
    args = parser.parse_args()

    profile = profile_artifact(args.artifact, args.reference, args.target, args.reference_layout)
    print(human_readable(profile))
    if args.json:
        with open(args.json, "w", encoding="utf-8") as f:
            json.dump(profile, f, indent=2, sort_keys=True)

    result = profile["runtime_compatibility"]["result"]
    return 0 if result in ("VERIFIED_COMPATIBLE", "VERIFIED_NORMALIZATION_REQUIRED") else 1


if __name__ == "__main__":
    sys.exit(main())
