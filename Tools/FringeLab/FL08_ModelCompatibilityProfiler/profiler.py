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
import math
import os
import sys

import numpy as np
from gguf import GGUFReader, GGUFValueType

SCHEMA_VERSION = 2

# Round-5 remediation (Gate 1, Codex authority review): the GGUF scalar
# type families this profiler accepts for the metadata fields it reads
# integer/float/string values from. Corroborated against the real
# Phase 6 artifacts (both use UINT32 for every integer-geometry field
# and FLOAT32 for both rms_epsilon and rope.freq_base -- confirmed by
# direct inspection, not assumed) and against the official GGUF
# metadata schema, which defines these fields as "an integer" / "a
# float" / "a string" without mandating one specific bit width -- so
# the FAMILY of GGUF integer/float types is accepted, not only the one
# width this project's own fixtures happen to use, while STRING/ARRAY/
# BOOL are never silently accepted where a number is required (and
# vice versa) even though Python could coerce a numeric-looking string.
_INT_GGUF_TYPES = frozenset({
    GGUFValueType.UINT8, GGUFValueType.UINT16, GGUFValueType.UINT32, GGUFValueType.UINT64,
    GGUFValueType.INT8, GGUFValueType.INT16, GGUFValueType.INT32, GGUFValueType.INT64,
})
_FLOAT_GGUF_TYPES = frozenset({GGUFValueType.FLOAT32, GGUFValueType.FLOAT64})
_STRING_GGUF_TYPES = frozenset({GGUFValueType.STRING})

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

# Round-3 remediation (Gate 1C): the SMALLEST explicit set of metadata
# keys classified EXECUTION-AFFECTING for this experiment -- compared
# by this profiler's own reading of the real Phase 6 artifacts (custom
# vs. canonical), not a general registry. REQUIRED means present and
# equal on both sides is mandatory for VERIFIED pair identity; OPTIONAL
# means "if present on EITHER side, must be present and equal on BOTH"
# (a key entirely absent from both sides is not itself a defect).
_EXECUTION_METADATA_REQUIRED_KEYS = (
    "llama.feed_forward_length",
    "llama.attention.layer_norm_rms_epsilon",
    "llama.rope.dimension_count",
    "llama.rope.freq_base",
    "llama.context_length",
)
_EXECUTION_METADATA_OPTIONAL_KEYS = (
    "llama.rope.scaling.type",
    "llama.rope.scaling.factor",
    "llama.attention.sliding_window",
)
# Classified BENIGN (pure bookkeeping/provenance, never blocks pair
# identity) from direct inspection of the real custom-vs-canonical
# artifact pair (see EXPERIMENT.md's round-3 metadata classification
# table): general.name/basename/languages/license/quantization_version/
# size_label/type, general.file_type, general.alignment.
#
# llama.attention.key_length / llama.attention.value_length /
# llama.vocab_size are execution-RELATED but REDUNDANT with fields this
# profiler already independently verifies byte-for-byte (head_dim via
# head_count/head_count_kv/embedding_length; vocab_size via
# token_embd.weight's own verified shape) -- when present, their VALUE
# is cross-checked against those already-verified facts rather than
# requiring symmetric presence, since the real canonical artifact
# declares them and the real custom artifact does not.
_EXECUTION_METADATA_REDUNDANT_KEYS = (
    "llama.attention.key_length", "llama.attention.value_length", "llama.vocab_size",
)

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


def _read_typed_scalar(reader: GGUFReader, key: str, allowed_types: frozenset, family_label: str):
    """The ONE shared, type-aware metadata boundary every scalar field
    this profiler consumes must go through (round-5 remediation, Gate
    1). Returns `(value, error)`: `error is None` and `value is None`
    means the field is legitimately ABSENT (not a defect -- callers
    decide whether absence itself matters). `error` set means the
    field IS present but either its declared GGUF type is not in
    `allowed_types`, or its content unexpectedly failed to decode --
    in both cases `value` is `None` and the caller must treat this as
    a structured `INVALID` result, never call `int()`/`float()` on the
    raw contents itself. This is the fix for Codex's reproduced
    fail-open: a `layer_norm_rms_epsilon` field stored with GGUF type
    STRING and content `"0.00001"` previously passed straight through
    `float(field.contents())` -- Python's `float("0.00001")` succeeds
    even though the FIELD's declared type is wrong -- and a
    `"not-a-number"` STRING value raised an uncaught `ValueError` from
    deep inside `validate_artifact()`. Neither can happen once every
    caller reads through this function instead of calling
    `field.contents()` and coercing it directly."""
    field = reader.fields.get(key)
    if field is None:
        return None, None
    actual_type = field.types[0]
    if actual_type not in allowed_types:
        return None, (f"{key}: GGUF type is {actual_type.name} ({actual_type.value}), expected one of "
                      f"{sorted(t.name for t in allowed_types)} (a {family_label} scalar) -- refusing to "
                      f"trust a value read from a field of the wrong declared type, even though Python "
                      f"could coerce its contents (e.g. int('8') or float('0.00001') would silently "
                      f"succeed on a malformed STRING-typed field)")
    try:
        value = field.contents()
    except Exception as ex:  # noqa: BLE001 -- any decode failure becomes a structured error, never a crash
        return None, f"{key}: GGUF type {actual_type.name} but content failed to decode: {ex!r}"
    return value, None


def _read_int_field(reader: GGUFReader, key: str) -> tuple[int | None, str | None]:
    """Returns `(value, error)` -- see `_read_typed_scalar`. `value` is
    a native Python `int` (never a `bool`, never a string coerced via
    `int()`) whenever `error` is `None` and the field is present."""
    value, error = _read_typed_scalar(reader, key, _INT_GGUF_TYPES, "integer")
    if error:
        return None, error
    if value is not None and (not isinstance(value, int) or isinstance(value, bool)):
        return None, f"{key}: decoded value {value!r} is not a native Python int despite an integer GGUF type"
    return value, None


def _read_float_field(reader: GGUFReader, key: str) -> tuple[float | None, str | None]:
    value, error = _read_typed_scalar(reader, key, _FLOAT_GGUF_TYPES, "float")
    if error:
        return None, error
    if value is None:
        return None, None
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return None, f"{key}: decoded value {value!r} is not numeric despite a float GGUF type"
    return float(value), None


def _read_string_field(reader: GGUFReader, key: str) -> tuple[str | None, str | None]:
    value, error = _read_typed_scalar(reader, key, _STRING_GGUF_TYPES, "string")
    if error:
        return None, error
    if value is not None and not isinstance(value, str):
        return None, f"{key}: decoded value {value!r} is not a native Python str despite a string GGUF type"
    return value, None




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
        # Round-3 remediation: execution-relevant metadata, captured
        # individually (not only as an opaque fingerprint) so pair
        # identity can classify EXACTLY what differs and why, per key --
        # key -> _encode_field_value() bytes (exact, non-truncating).
        self.execution_metadata: dict[str, bytes] = {}
        # All tokenizer.* fields, same exact-byte encoding -- the WHOLE
        # tokenizer.* namespace is classified tokenizer-affecting for
        # this experiment (vocabulary, model/policy, IDs, special
        # tokens all live under this prefix).
        self.tokenizer_fields: dict[str, bytes] = {}
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

    version, err = _read_int_field(reader, "GGUF.version")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
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
    declared_architecture, err = _read_string_field(reader, "general.architecture")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
    v.declared_architecture = declared_architecture
    if v.declared_architecture != "llama":
        v.ambiguities.append(f"declared architecture {v.declared_architecture!r} is not 'llama' -- "
                             f"this experiment's Layer 3 (Q/K dialect) only supports llama")
        v.terminal_result = "VERIFIED_UNSUPPORTED"
        return v

    n_layers, err_layers = _read_int_field(reader, "llama.block_count")
    n_head, err_head = _read_int_field(reader, "llama.attention.head_count")
    n_head_kv, err_head_kv = _read_int_field(reader, "llama.attention.head_count_kv")
    hidden, err_hidden = _read_int_field(reader, "llama.embedding_length")
    ffn_length, err_ffn = _read_int_field(reader, "llama.feed_forward_length")
    context_length, err_ctx = _read_int_field(reader, "llama.context_length")
    geometry_type_errors = [e for e in (err_layers, err_head, err_head_kv, err_hidden, err_ffn, err_ctx) if e]
    if geometry_type_errors:
        v.ambiguities.append(f"required geometry metadata type validation failed: {geometry_type_errors}")
        v.terminal_result = "INVALID"
        return v

    if None in (n_layers, n_head, n_head_kv, hidden, ffn_length, context_length):
        v.ambiguities.append("required llama.* metadata missing "
                             f"(block_count={n_layers} head_count={n_head} "
                             f"head_count_kv={n_head_kv} embedding_length={hidden} "
                             f"feed_forward_length={ffn_length} context_length={context_length})")
        v.terminal_result = "INVALID"
        return v
    if n_layers <= 0 or n_head <= 0 or n_head_kv <= 0 or hidden <= 0 or ffn_length <= 0 or context_length <= 0:
        v.ambiguities.append(f"non-positive required geometry: block_count={n_layers} head_count={n_head} "
                             f"head_count_kv={n_head_kv} embedding_length={hidden} "
                             f"feed_forward_length={ffn_length} context_length={context_length}")
        v.terminal_result = "INVALID"
        return v
    if hidden % n_head != 0:
        v.ambiguities.append(f"embedding_length={hidden} not evenly divisible by head_count={n_head}")
        v.terminal_result = "INVALID"
        return v
    if n_head % n_head_kv != 0:
        # Round-4 remediation (Gate 2, Codex-reproduced false
        # authorization): the frozen OrcEngine model layout requires
        # every Q head to map onto a whole number of shared KV heads
        # (grouped-query attention's defining invariant) -- a geometry
        # like hidden=24/n_head=3/n_head_kv=2 is not a valid GQA
        # configuration for ANY known attention-head grouping scheme
        # and was previously able to reach VERIFIED_COMPATIBLE.
        v.ambiguities.append(f"head_count={n_head} not evenly divisible by head_count_kv={n_head_kv} -- "
                             f"not a valid grouped-query-attention geometry (every Q head must map onto a "
                             f"whole number of shared KV heads)")
        v.terminal_result = "INVALID"
        return v
    head_dim = hidden // n_head
    if head_dim % 2 != 0:
        # Round-3 remediation (Gate 2, Codex ODD_HEAD_DIM_CRASH probe):
        # official_permute() reshapes on `rows // n_head // 2` -- an ODD
        # head_dim makes that reshape's element count NOT match the
        # tensor's actual size, and numpy raises ValueError from deep
        # inside the permutation call rather than this profiler failing
        # closed with a structured result. Caught HERE, before any
        # tensor is ever handed to official_permute().
        v.ambiguities.append(f"head_dim=hidden/head_count={head_dim} is odd -- official_permute()'s "
                             f"reshape requires an EVEN head_dim (it reshapes into 2 halves); refusing to "
                             f"attempt any permutation against this geometry")
        v.terminal_result = "INVALID"
        return v

    rms_eps_val, err = _read_float_field(reader, "llama.attention.layer_norm_rms_epsilon")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
    if rms_eps_val is not None:
        if not math.isfinite(rms_eps_val) or rms_eps_val <= 0:
            # Round-4 remediation (Gate 1, Codex-reproduced false
            # authorization): TWO artifacts with the SAME invalid
            # rms_epsilon (e.g. -1.0) previously passed pair identity's
            # equality check and could reach VERIFIED_COMPATIBLE --
            # equality alone does not prove the shared value is a
            # value OrcEngine's frozen RMSNorm can actually execute.
            v.ambiguities.append(f"llama.attention.layer_norm_rms_epsilon={rms_eps_val!r} is not finite "
                                 f"and > 0 -- not a value OrcEngine's frozen RMSNorm can execute")
            v.terminal_result = "INVALID"
            return v

    rope_dim_val, err_dim = _read_int_field(reader, "llama.rope.dimension_count")
    rope_freq_val, err_freq = _read_float_field(reader, "llama.rope.freq_base")
    rope_type_errors = [e for e in (err_dim, err_freq) if e]
    if rope_type_errors:
        # Round-5 remediation (Gate 1, Codex-reproduced fail-open):
        # a STRING-typed llama.rope.dimension_count="8" or
        # llama.rope.freq_base="0.00001" previously passed straight
        # through int()/float() coercion (Python accepts numeric-
        # looking strings) or, for a non-numeric string, raised an
        # UNCAUGHT ValueError from inside this function. Both are
        # closed by routing through _read_int_field/_read_float_field,
        # which check field.types[0] BEFORE ever calling .contents()
        # for arithmetic use.
        v.ambiguities.append(f"RoPE metadata type validation failed: {rope_type_errors}")
        v.terminal_result = "INVALID"
        return v
    if rope_dim_val is None or rope_freq_val is None:
        v.ambiguities.append("llama.rope.dimension_count / llama.rope.freq_base metadata missing -- "
                             "RoPE application semantics cannot be fully corroborated")
        # Recorded as an ambiguity, not INVALID -- some real artifacts
        # legitimately omit these (defaults apply); per this
        # experiment's own design, missing-but-not-contradictory
        # optional metadata proceeds with the ambiguity recorded, not
        # silently passed.
    else:
        # Round-4 remediation (Gate 1): semantic validation of PRESENT
        # RoPE metadata, mirroring the frozen runtime's actual, proven
        # contract (Tools/OrcEnginePhase1/include/orcengine/ops.hpp:
        # "Full-rotation (rotary_dim == head_dim) non-interleaved Llama
        # RoPE ... Phase 1 has no partial rotary factor" -- inspected,
        # not modified). Two artifacts sharing the SAME invalid value
        # (e.g. freq_base=-10000.0, or dimension_count=999) previously
        # passed pair identity's equality check alone.
        if not math.isfinite(rope_freq_val) or rope_freq_val <= 0:
            v.ambiguities.append(f"llama.rope.freq_base={rope_freq_val!r} is not finite and > 0 -- not a "
                                 f"value OrcEngine's frozen RoPE application can execute")
            v.terminal_result = "INVALID"
            return v
        if rope_dim_val != head_dim:
            v.ambiguities.append(f"llama.rope.dimension_count={rope_dim_val} != head_dim={head_dim} -- "
                                 f"OrcEngine's frozen RoPE implementation only supports FULL-HEAD rotation "
                                 f"(rotary_dim == head_dim, no partial rotary factor); a narrower or wider "
                                 f"declared RoPE dimension cannot be executed by the current runtime")
            v.terminal_result = "INVALID"
            return v

    scaling_type_val, err = _read_string_field(reader, "llama.rope.scaling.type")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
    if scaling_type_val is not None:
        if scaling_type_val not in ("none", "linear"):
            # OrcEngine's frozen runtime implements no RoPE scaling
            # variant at all (grep-confirmed: zero occurrences of
            # "rope_scaling"/"yarn"/"ntk" anywhere in Tools/OrcEngine*).
            # "linear" with factor=1.0 is the one no-op case worth
            # tolerating rather than rejecting outright; anything else
            # (yarn, dynamic, longrope, ...) is an execution-affecting
            # mode this runtime cannot apply.
            v.ambiguities.append(f"llama.rope.scaling.type={scaling_type_val!r} is not a scaling mode "
                                 f"OrcEngine's frozen runtime implements (no rope-scaling code exists in "
                                 f"Tools/OrcEngine* at all) -- an unsupported execution-affecting mode")
            v.terminal_result = "INVALID"
            return v
    factor_val, err = _read_float_field(reader, "llama.rope.scaling.factor")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
    if factor_val is not None:
        if not math.isfinite(factor_val) or factor_val <= 0:
            v.ambiguities.append(f"llama.rope.scaling.factor={factor_val!r} is not finite and > 0")
            v.terminal_result = "INVALID"
            return v
        if factor_val != 1.0:
            v.ambiguities.append(f"llama.rope.scaling.factor={factor_val!r} != 1.0 -- OrcEngine's frozen "
                                 f"runtime applies no RoPE scaling, so any non-identity factor is an "
                                 f"execution-affecting mode this runtime cannot apply")
            v.terminal_result = "INVALID"
            return v

    sliding_window_val, err = _read_int_field(reader, "llama.attention.sliding_window")
    if err:
        v.ambiguities.append(err)
        v.terminal_result = "INVALID"
        return v
    if sliding_window_val is not None:
        if sliding_window_val != 0:
            # OrcEngine's frozen runtime implements no sliding-window
            # attention at all (grep-confirmed, same scope as above).
            # 0 is the conventional GGUF "no window / full attention"
            # value; any other declared value requests windowed
            # attention this runtime cannot execute.
            v.ambiguities.append(f"llama.attention.sliding_window={sliding_window_val} is a nonzero "
                                 f"windowed-attention request -- OrcEngine's frozen runtime implements no "
                                 f"sliding-window attention at all")
            v.terminal_result = "INVALID"
            return v

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

    # Round-3 remediation (Gate 2, Codex WRONG_QK_INPUT_WIDTH probe):
    # the round-2 checks validated ROWS only (the output/logical-row
    # axis) and never the LAST dimension (the input/column axis) --
    # a Q/K/V/FFN tensor with correct rows but a wrong input width
    # (e.g. hidden+2) previously passed silently. Every required
    # tensor's COMPLETE logical dimensions are now validated, using
    # `logical_last_dim()` so packed Q8_0 width is decoded correctly.
    missing_tensors, shape_contradictions = [], []
    for name in REQUIRED_LLAMA_GLOBAL_TENSORS:
        if _tensor_by_name(reader, name) is None:
            missing_tensors.append(name)

    def _check_2d(name: str, t, expected_rows: int, expected_cols: int) -> None:
        shape = logical_shape(t)
        if len(shape) != 2:
            shape_contradictions.append(f"{name}: expected rank 2, got shape {shape}")
            return
        rows = shape[0]
        cols = logical_last_dim(t)
        if rows != expected_rows or cols != expected_cols:
            shape_contradictions.append(f"{name}: logical shape=({rows},{cols}) "
                                        f"(raw axis shape={shape}), expected ({expected_rows},{expected_cols})")

    def _check_1d(name: str, t, expected_len: int) -> None:
        shape = logical_shape(t)
        if len(shape) != 1 or shape[0] != expected_len:
            shape_contradictions.append(f"{name}: expected rank 1 shape=({expected_len},), got shape={shape}")

    for i in range(n_layers):
        for suffix in REQUIRED_LLAMA_PER_LAYER_SUFFIXES:
            name = f"blk.{i}.{suffix}"
            t = _tensor_by_name(reader, name)
            if t is None:
                missing_tensors.append(name)
                continue
            if suffix == "attn_q.weight":
                _check_2d(name, t, hidden, hidden)
            elif suffix in ("attn_k.weight", "attn_v.weight"):
                _check_2d(name, t, n_head_kv * head_dim, hidden)
            elif suffix == "attn_output.weight":
                _check_2d(name, t, hidden, hidden)
            elif suffix in ("attn_norm.weight", "ffn_norm.weight"):
                _check_1d(name, t, hidden)
            elif suffix == "ffn_gate.weight" or suffix == "ffn_up.weight":
                _check_2d(name, t, ffn_length, hidden)
            elif suffix == "ffn_down.weight":
                _check_2d(name, t, hidden, ffn_length)

    emb = _tensor_by_name(reader, "token_embd.weight")
    if emb is not None:
        emb_shape = logical_shape(emb)
        if len(emb_shape) != 2 or logical_last_dim(emb) != hidden:
            shape_contradictions.append(f"token_embd.weight: expected rank-2 with logical last dim=hidden="
                                        f"{hidden}, got shape={emb_shape} logical_last_dim={logical_last_dim(emb)}")
    norm = _tensor_by_name(reader, "output_norm.weight")
    if norm is not None:
        _check_1d("output_norm.weight", norm, hidden)
    out_head = _tensor_by_name(reader, "output.weight")
    if out_head is not None and emb is not None:
        out_shape = logical_shape(out_head)
        vocab_rows = logical_shape(emb)[0]
        if len(out_shape) != 2 or out_shape[0] != vocab_rows or logical_last_dim(out_head) != hidden:
            shape_contradictions.append(f"output.weight: expected shape compatible with vocabulary="
                                        f"{vocab_rows}/hidden={hidden}, got shape={out_shape} "
                                        f"logical_last_dim={logical_last_dim(out_head)}")

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
    for key in _EXECUTION_METADATA_REQUIRED_KEYS + _EXECUTION_METADATA_OPTIONAL_KEYS + \
            _EXECUTION_METADATA_REDUNDANT_KEYS:
        f = reader.fields.get(key)
        if f is not None:
            v.execution_metadata[key] = _encode_field_value(f)
    for key in reader.fields:
        if key.startswith("tokenizer."):
            v.tokenizer_fields[key] = _encode_field_value(reader.fields[key])
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
    # Defense in depth: validate_artifact() already rejects odd/zero
    # head_dim before Layer 3 ever runs, so this reshape should never
    # fail here -- but a caught, structured AMBIGUOUS result is still
    # strictly better than an uncaught crash if that invariant is ever
    # violated by a future code path.
    try:
        a_is_raw_relative_to_b = np.array_equal(official_permute(a.data.copy(), n_head, ref_n_head_kv), b.data)
        b_is_raw_relative_to_a = np.array_equal(official_permute(b.data.copy(), n_head, ref_n_head_kv), a.data)
    except ValueError as ex:
        return "PERMUTE_RESHAPE_ERROR", f"official_permute() reshape failed: {ex!r}"

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
        # Artifact is RAW relative to the reference -> the reference
        # itself is CANONICAL. If the operator declared the reference
        # as "raw", that directly CONTRADICTS this numerical finding --
        # round-3 remediation (Gate 3): never silently ignore
        # contradictory operator evidence.
        if reference_layout_declared == "raw":
            qk["classification"] = "AMBIGUOUS"
            qk["confidence"] = "AMBIGUOUS"
            ambiguities.append(f"CONTRADICTION: operator declared --reference-layout=raw, but the numerical "
                               f"permutation proof shows the reference is CANONICAL relative to this "
                               f"artifact (permute(artifact) == reference) -- refusing to silently prefer "
                               f"either the declaration or the numerical evidence")
            return
        qk["classification"] = "RAW_HF"
        qk["confidence"] = "NUMERICALLY_VERIFIED"
        evidence.append(f"Q/K fingerprint matches RAW_HF convention: permute(this artifact's Q/K) == "
                        f"reference {reference_label!r}'s Q/K (this artifact is raw relative to the "
                        f"reference, per the externally-fixed permute() direction), verified on "
                        f"{checked}/{total} tensors ({layers_with_full_qk}/{n_layers} layers)")
        return
    if winning == "B_RAW_REL_A":
        # Reference is RAW relative to the artifact -> the artifact
        # itself is CANONICAL. If the operator declared the reference
        # as "canonical", that contradicts this numerical finding.
        if reference_layout_declared == "canonical":
            qk["classification"] = "AMBIGUOUS"
            qk["confidence"] = "AMBIGUOUS"
            ambiguities.append(f"CONTRADICTION: operator declared --reference-layout=canonical, but the "
                               f"numerical permutation proof shows the reference is RAW relative to this "
                               f"artifact (permute(reference) == artifact) -- refusing to silently prefer "
                               f"either the declaration or the numerical evidence")
            return
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

def _tied_output_proof(reader: GGUFReader, label: str) -> tuple[bool, str]:
    """Gate 1B: tests whether ONE side's `output.weight` (when the OTHER
    side lacks it) is provably a physical duplicate of THAT SIDE's own
    `token_embd.weight` -- same type, same logical shape, byte-identical
    contents. Returns (proven, evidence_string). This does NOT itself
    confirm the two ARTIFACTS' embeddings match each other -- the caller
    must separately confirm token_embd.weight already passed pair
    identity before treating an asymmetric output.weight as safe."""
    out_w = _tensor_by_name(reader, "output.weight")
    emb_w = _tensor_by_name(reader, "token_embd.weight")
    if out_w is None or emb_w is None:
        return False, f"{label}: output.weight or token_embd.weight missing, cannot prove tied duplication"
    if out_w.tensor_type.name != emb_w.tensor_type.name:
        return False, (f"{label}: output.weight type {out_w.tensor_type.name} != "
                       f"token_embd.weight type {emb_w.tensor_type.name}")
    if logical_shape(out_w) != logical_shape(emb_w):
        return False, (f"{label}: output.weight shape {logical_shape(out_w)} != "
                       f"token_embd.weight shape {logical_shape(emb_w)}")
    if not np.array_equal(out_w.data, emb_w.data):
        return False, f"{label}: output.weight is NOT byte-identical to token_embd.weight"
    return True, (f"{label}: output.weight proven byte-for-byte physically-duplicated from this side's own "
                  f"token_embd.weight (same type {out_w.tensor_type.name}, same shape {logical_shape(out_w)})")


def _diff_metadata_dict(a: dict[str, bytes], b: dict[str, bytes], required_keys: tuple[str, ...],
                        optional_keys: tuple[str, ...] = ()) -> list[str]:
    """Compares two key->encoded-bytes dicts. Required keys must be
    present and byte-equal on both sides. Optional keys must be
    byte-equal on both sides ONLY if present on at least one side.
    Returns a list of human-readable defect strings (empty = no defect)."""
    defects = []
    for key in required_keys:
        av, bv = a.get(key), b.get(key)
        if av is None or bv is None:
            defects.append(f"{key}: required execution metadata missing on "
                           f"{'artifact' if av is None else 'reference'} side")
        elif av != bv:
            defects.append(f"{key}: differs between artifact and reference")
    for key in optional_keys:
        av, bv = a.get(key), b.get(key)
        if av is None and bv is None:
            continue
        if av is None or bv is None:
            defects.append(f"{key}: present on only one side ({'reference' if av is None else 'artifact'})")
        elif av != bv:
            defects.append(f"{key}: differs between artifact and reference")
    return defects


def verify_pair_identity(artifact_v: ArtifactValidation, reference_v: ArtifactValidation) -> dict:
    """Compares every NON-Q/K tensor (V, attention-output, norms, FFN,
    embedding, output head) AND every execution-affecting/tokenizer
    metadata field between the artifact and reference. A tampered
    V/norm/FFN/embedding tensor, an execution-metadata mismatch, or a
    tokenizer-metadata mismatch must NOT retain a "verified" pair
    identity even if Q/K still matches -- that is exactly the attack
    this check exists to catch. Round-3 remediation: the non-Q/K
    tensor inventory is now built from the UNION of both sides' tensor
    names (not just the primary side's expected-name list), so an
    unexpected extra tensor on either side is caught rather than
    silently ignored; `output.weight` present on exactly one side is
    UNVERIFIED unless narrowly proven tied to that side's own,
    already-matched `token_embd.weight` (Gate 1B)."""
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

    # --- Gate 1C: execution-affecting metadata ---
    exec_defects = _diff_metadata_dict(artifact_v.execution_metadata, reference_v.execution_metadata,
                                       _EXECUTION_METADATA_REQUIRED_KEYS, _EXECUTION_METADATA_OPTIONAL_KEYS)
    if exec_defects:
        result["evidence"].append(f"execution-affecting metadata mismatch: {exec_defects}")
        return result

    # Redundant-but-execution-related keys: when present, cross-check
    # the VALUE against already-independently-verified facts (head_dim,
    # embedding row count) rather than requiring symmetric presence --
    # the real canonical converter declares these, the real custom
    # converter does not, and both are correct restatements of facts
    # this profiler already verifies another way.
    head_dim = artifact_v.hidden // artifact_v.n_head
    emb = _tensor_by_name(a_reader, "token_embd.weight")
    vocab_rows = logical_shape(emb)[0] if emb is not None else None
    for label, reader_v in (("artifact", artifact_v), ("reference", reference_v)):
        # Round-5 remediation (Gate 1 audit): these redundant keys were
        # previously read via a raw `int(...contents())` call with no
        # type check -- the SAME fail-open/fail-closed defects the
        # required/optional keys had. Routed through the shared
        # type-aware boundary like every other scalar field.
        reader_obj = a_reader if label == "artifact" else b_reader
        kl, err_kl = _read_int_field(reader_obj, "llama.attention.key_length")
        vl, err_vl = _read_int_field(reader_obj, "llama.attention.value_length")
        vs, err_vs = _read_int_field(reader_obj, "llama.vocab_size")
        redundant_type_errors = [f"{label}: {e}" for e in (err_kl, err_vl, err_vs) if e]
        if redundant_type_errors:
            result["evidence"].append(f"redundant-key metadata type validation failed: "
                                      f"{redundant_type_errors}")
            return result
        if kl is not None:
            if kl != head_dim:
                result["evidence"].append(f"{label}: declared llama.attention.key_length={kl} "
                                          f"contradicts independently-verified head_dim={head_dim}")
                return result
        if vl is not None:
            if vl != head_dim:
                result["evidence"].append(f"{label}: declared llama.attention.value_length={vl} "
                                          f"contradicts independently-verified head_dim={head_dim}")
                return result
        if vs is not None and vocab_rows is not None:
            if vs != vocab_rows:
                result["evidence"].append(f"{label}: declared llama.vocab_size={vs} contradicts "
                                          f"independently-verified token_embd.weight row count={vocab_rows}")
                return result

    # --- Gate 1C: tokenizer-affecting metadata (whole tokenizer.* namespace) ---
    tok_a_keys, tok_b_keys = set(artifact_v.tokenizer_fields), set(reference_v.tokenizer_fields)
    if tok_a_keys != tok_b_keys:
        only_a = sorted(tok_a_keys - tok_b_keys)
        only_b = sorted(tok_b_keys - tok_a_keys)
        result["evidence"].append(f"tokenizer metadata key sets differ: only on artifact={only_a}, "
                                  f"only on reference={only_b}")
        return result
    tok_defects = [k for k in tok_a_keys if artifact_v.tokenizer_fields[k] != reference_v.tokenizer_fields[k]]
    if tok_defects:
        result["evidence"].append(f"tokenizer metadata value(s) differ: {sorted(tok_defects)}")
        return result

    # --- Gate 1A: complete non-Q/K tensor inventory, built from the
    # UNION of both sides' actual tensor names (not just the expected-
    # name list), excluding Q/K (Layer 3's job) and output.weight
    # (handled separately below, Gate 1B). ---
    qk_names = {f"blk.{i}.{kind}.weight" for i in range(artifact_v.n_layers) for kind in ("attn_q", "attn_k")}
    a_names = {t.name for t in a_reader.tensors} - qk_names - {"output.weight"}
    b_names = {t.name for t in b_reader.tensors} - qk_names - {"output.weight"}

    only_a = sorted(a_names - b_names)
    only_b = sorted(b_names - a_names)
    if only_a or only_b:
        result["evidence"].append(f"non-Q/K tensor inventory differs: only on artifact={only_a[:10]}, "
                                  f"only on reference={only_b[:10]}")
        return result

    mismatches = []
    for name in sorted(a_names):
        a = _tensor_by_name(a_reader, name)
        b = _tensor_by_name(b_reader, name)
        if a.tensor_type.name != b.tensor_type.name:
            mismatches.append((name, "TYPE_MISMATCH", a.tensor_type.name, b.tensor_type.name))
            continue
        if logical_shape(a) != logical_shape(b):
            mismatches.append((name, "SHAPE_MISMATCH", logical_shape(a), logical_shape(b)))
            continue
        if not np.array_equal(a.data, b.data):
            mismatches.append((name, "VALUE_MISMATCH"))
    if mismatches:
        result["evidence"].append(f"{len(mismatches)} non-Q/K tensor(s) differ between artifact and "
                                  f"reference (V/attn_output/norm/FFN/embedding) -- these must be "
                                  f"identical for two artifacts of the same underlying model to differ "
                                  f"ONLY in Q/K layout: {mismatches[:5]}{'...' if len(mismatches) > 5 else ''}")
        return result

    checked_names = sorted(a_names)

    # --- Gate 1B: output.weight, handled explicitly, never silently
    # skipped regardless of which side(s) have it. ---
    a_out = _tensor_by_name(a_reader, "output.weight")
    b_out = _tensor_by_name(b_reader, "output.weight")
    output_evidence = None
    if a_out is None and b_out is None:
        output_evidence = "output.weight absent on both sides -- shared tied representation"
    elif a_out is not None and b_out is not None:
        if a_out.tensor_type.name != b_out.tensor_type.name:
            result["evidence"].append(f"output.weight type mismatch: artifact={a_out.tensor_type.name} "
                                      f"reference={b_out.tensor_type.name}")
            return result
        if logical_shape(a_out) != logical_shape(b_out):
            result["evidence"].append(f"output.weight shape mismatch: artifact={logical_shape(a_out)} "
                                      f"reference={logical_shape(b_out)}")
            return result
        if not np.array_equal(a_out.data, b_out.data):
            result["evidence"].append("output.weight present on both sides but VALUE_MISMATCH")
            return result
        output_evidence = "output.weight present on both sides and byte-identical"
    else:
        # Present on exactly one side -- the dangerous asymmetric case
        # (Grok round-2 finding). Safe ONLY if narrowly proven tied to
        # THAT side's own token_embd.weight, AND token_embd.weight has
        # already passed pair identity above.
        #
        # Round-4 remediation (Grok round-3 review, finding #4):
        # documenting this guard's ACTUAL reachability honestly, not
        # overclaiming what it catches. `token_embd.weight` is a
        # REQUIRED tensor (Layer 2/`validate_artifact()` already made
        # BOTH sides `INVALID` before `verify_pair_identity()` is ever
        # called if either lacks it), and `checked_names` is only
        # reached AFTER the mismatches check above already returned
        # early for ANY differing non-Q/K tensor -- including
        # `token_embd.weight` itself. So a mismatched embedding is
        # caught by the EARLIER `mismatches` return, not by this guard;
        # by the time execution reaches here, `token_embd.weight` is
        # unconditionally present in `checked_names`. This specific
        # `if` is therefore UNREACHABLE as false under the current code
        # structure -- kept as an explicit internal invariant / defense
        # in depth against a future refactor that decouples the
        # embedding check from this one, not because it currently
        # blocks a real attack path. See
        # `test_embedding_mismatch_denied_before_tied_output_proof_is_ever_reached`
        # in test_profiler.py for the actual attack this protects
        # against (via the earlier `mismatches` path, not this guard).
        present_side_reader = a_reader if a_out is not None else b_reader
        present_side_label = "artifact" if a_out is not None else "reference"
        if "token_embd.weight" not in checked_names:
            result["evidence"].append(f"output.weight present only on {present_side_label}, and "
                                      f"token_embd.weight itself did not pass pair identity -- cannot "
                                      f"prove tied duplication, pair identity UNVERIFIED")
            return result
        proven, evidence_str = _tied_output_proof(present_side_reader, present_side_label)
        if not proven:
            result["evidence"].append(f"output.weight present only on {present_side_label} and NOT "
                                      f"provably tied to its own token_embd.weight -- {evidence_str} -- "
                                      f"pair identity UNVERIFIED (asymmetric untied output head is exactly "
                                      f"the attack this check exists to catch)")
            return result
        output_evidence = (f"output.weight present only on {present_side_label}; proven tied-duplicate: "
                           f"{evidence_str}; the OTHER side's tied head is materialized from its own "
                           f"(already-verified-identical) token_embd.weight at runtime instead")

    result["status"] = "VERIFIED"
    result["evidence"].append(f"all {len(checked_names)} non-Q/K tensors (V, attention-output, norms, FFN, "
                              f"embedding) byte-identical between artifact and reference; {output_evidence}; "
                              f"execution-affecting metadata and the complete tokenizer.* namespace agree -- "
                              f"same underlying executable model, differing (if at all) only in Q/K layout")
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
    elif qk["confidence"] == "DECLARED":
        # Round-3 remediation (Gate 3): a direct-match label derived
        # from --reference-layout is DECLARED evidence, not numerical
        # proof. execution_authorization was already correctly false
        # for this case, but VERIFIED_COMPATIBLE/a successful CLI exit
        # overstated declaration-only evidence as if it were a cleared
        # compatibility verdict. Declaration-only labels never resolve
        # to VERIFIED_COMPATIBLE or VERIFIED_NORMALIZATION_REQUIRED --
        # they remain AMBIGUOUS until numerically confirmed.
        profile_data["runtime_compatibility"]["result"] = "AMBIGUOUS"
        profile_data["unresolved_ambiguities"].append(
            f"Q/K classification {qk['classification']!r} is DECLARED (operator-asserted), not "
            f"NUMERICALLY_VERIFIED -- a runtime compatibility verdict requires numerical proof, not a "
            f"declaration alone")
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
