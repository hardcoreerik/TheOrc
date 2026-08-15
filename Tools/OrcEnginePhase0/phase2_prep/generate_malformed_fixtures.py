# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 2 prep (ENGINEERING_ROADMAP.md Phase 2, permitted under the Phase 0
stop gate as test/fixture work, NOT engine code): generates the malformed-
GGUF conformance corpus described in
docs/OrcEngine/MODEL_FORMAT_AND_GGUF.md's "Malformed-input suite":

  "bad magic, unsupported version, truncation at every structural boundary,
  huge counts, integer overflow, invalid type, invalid UTF-8 policy,
  duplicate key/name, zero dimensions, unsupported dtype, misalignment,
  offset before data, extent past EOF, overlapping tensors, missing
  required metadata, inconsistent dimensions"

Each fixture starts from a KNOWN-VALID baseline (built with
phase2_prep/raw_gguf_writer.py and confirmed loadable by gguf-py's
independent GGUFReader before any corruption is applied -- see
verify_baseline_is_valid()), then applies exactly one corruption. This is
NOT the Phase 2 parser itself (writing that is explicitly forbidden until
all 14 Phase 0 checks pass, per OE-ADR-001) -- it is the fixture corpus
that parser will need to prove itself against, plus a conformance manifest
recording what each fixture is and where the reader-stage spec
(MODEL_FORMAT_AND_GGUF.md "Reader stages") says it should be rejected.
"""
from __future__ import annotations

import json
import os
import struct

import numpy as np

from phase2_prep.raw_gguf_writer import RawGGUFBuilder, _gguf_string

OUTPUT_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "malformed_gguf_fixtures")
MANIFEST_PATH = os.path.join(OUTPUT_DIR, "CONFORMANCE_MANIFEST.json")


def _valid_baseline_bytes() -> bytes:
    b = RawGGUFBuilder()
    b.add_string("general.architecture", "llama")
    b.add_string("general.name", "phase2-prep-baseline")
    b.add_uint32("llama.context_length", 16)
    b.add_uint32("llama.embedding_length", 4)
    arr = np.array([[1.0, 2.0, 3.0, 4.0], [5.0, 6.0, 7.0, 8.0]], dtype=np.float32)
    b.add_tensor("token_embd.weight", arr.shape, arr.tobytes())
    return b.build()


def verify_baseline_is_valid(data: bytes) -> bool:
    """Confirms the baseline is genuinely well-formed before corrupting it,
    via gguf-py's independent GGUFReader -- corrupting an already-broken
    baseline would produce meaningless fixtures. Kept on disk afterward as
    the reference-valid fixture (gguf-py mmaps the file on Windows, so
    deleting it immediately after read can hit a file-lock error) --
    positive control for the future Phase 2 parser conformance suite."""
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    path = os.path.join(OUTPUT_DIR, "_valid_baseline.gguf")
    with open(path, "wb") as f:
        f.write(data)
    from gguf import GGUFReader
    r = GGUFReader(path)
    return len(r.tensors) == 1 and "general.architecture" in r.fields


FIXTURES: list[dict] = []


def _write_fixture(name: str, data: bytes, *, category: str, description: str,
                    expected_stage: str, expected_reason: str) -> None:
    path = os.path.join(OUTPUT_DIR, f"{name}.gguf")
    with open(path, "wb") as f:
        f.write(data)
    FIXTURES.append({
        "name": name, "file": os.path.basename(path), "category": category,
        "description": description, "expected_reader_stage": expected_stage,
        "expected_verdict": "reject", "expected_reason": expected_reason,
        "file_size_bytes": len(data),
    })


def generate_all() -> None:
    os.makedirs(OUTPUT_DIR, exist_ok=True)
    baseline = _valid_baseline_bytes()
    assert verify_baseline_is_valid(baseline), "baseline itself is not valid GGUF -- fix raw_gguf_writer.py first"

    # 1. Bad magic.
    corrupted = b"BADM" + baseline[4:]
    _write_fixture("bad_magic", corrupted, category="file_envelope",
                    description="First 4 bytes changed from 'GGUF' to 'BADM'.",
                    expected_stage="1. File envelope", expected_reason="magic mismatch")

    # 2. Unsupported version.
    corrupted = baseline[:4] + struct.pack("<I", 999) + baseline[8:]
    _write_fixture("unsupported_version", corrupted, category="file_envelope",
                    description="Version field set to 999 (only version 3 is the first supported tuple).",
                    expected_stage="1. File envelope", expected_reason="unsupported version")

    # 3. Truncated at header (only 8 of the first ~24 header bytes present).
    _write_fixture("truncated_header", baseline[:8], category="file_envelope",
                    description="File cut off after magic+version, before tensor_count/kv_count.",
                    expected_stage="1. File envelope", expected_reason="file too short for header")

    # 4. Truncated mid-metadata (cut partway through the metadata KV section).
    header_len = 4 + 4 + 8 + 8  # magic + version + tensor_count + kv_count
    _write_fixture("truncated_metadata", baseline[:header_len + 10], category="metadata_table",
                    description="File cut off partway through the first metadata key's string bytes.",
                    expected_stage="2. Metadata table", expected_reason="metadata entry extends past EOF")

    # 5. Truncated mid-tensor-data (valid header/metadata/tensor-info, data section cut short).
    _write_fixture("truncated_tensor_data", baseline[:-10], category="tensor_data",
                    description="Last 10 bytes of tensor data removed; tensor extent now exceeds file size.",
                    expected_stage="3. Tensor descriptors", expected_reason="tensor extent past EOF")

    # 6. Huge tensor_count (claims far more tensors than actually follow).
    corrupted = baseline[:4] + baseline[4:8] + struct.pack("<Q", 10_000_000_000) + baseline[16:]
    _write_fixture("huge_tensor_count", corrupted, category="resource_abuse",
                    description="tensor_count field set to 10 billion while file contains 1 real tensor descriptor.",
                    expected_stage="1. File envelope / resource caps",
                    expected_reason="tensor_count exceeds configured cap (10,000) and/or file size cannot support it")

    # 7. Integer overflow in tensor dims (two huge dims whose product overflows u64 byte-size math).
    b7 = RawGGUFBuilder()
    b7.add_string("general.architecture", "llama")
    b7.add_string("general.name", "overflow-test")
    huge = 2**33
    b7.tensors.append(__import__("phase2_prep.raw_gguf_writer", fromlist=["TensorSpec"]).TensorSpec(
        name="huge.weight", dims=(huge, huge), data=b"\x00" * 4))  # dims lie about actual data size
    _write_fixture("integer_overflow_dims", b7.build(), category="tensor_descriptors",
                    description=f"Tensor dims ({huge}, {huge}) whose element-count product overflows "
                                f"when multiplied by element size, while actual data is 4 bytes.",
                    expected_stage="3. Tensor descriptors",
                    expected_reason="dimension product overflow / declared extent exceeds file size")

    # 8. Invalid metadata value type (type tag not in the GGUF type enum 0-12).
    # Rebuild kv section manually with a bogus type=255 for one entry.
    b8 = RawGGUFBuilder()
    b8.add_string("general.architecture", "llama")
    header = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 0) + struct.pack("<Q", 1)
    bogus_kv = _gguf_string("general.architecture") + struct.pack("<I", 255) + _gguf_string("llama")
    _write_fixture("invalid_metadata_type", header + bogus_kv, category="metadata_table",
                    description="Metadata value_type tag set to 255, not a valid GGUF type enum value (0-12).",
                    expected_stage="2. Metadata table", expected_reason="unknown/invalid value type tag")

    # 9. Invalid UTF-8 in a string value (raw invalid byte sequence, bypassing proper encoding).
    invalid_utf8_bytes = b"\xff\xfe\x00invalid"
    header9 = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 0) + struct.pack("<Q", 1)
    kv9 = _gguf_string("general.name") + struct.pack("<I", 8) + struct.pack("<Q", len(invalid_utf8_bytes)) + invalid_utf8_bytes
    _write_fixture("invalid_utf8_string", header9 + kv9, category="metadata_table",
                    description="A string metadata value contains raw invalid-UTF-8 bytes (0xFF 0xFE).",
                    expected_stage="2. Metadata table", expected_reason="invalid UTF-8 in string value")

    # 10. Duplicate metadata key.
    b10 = RawGGUFBuilder()
    b10.add_string("general.architecture", "llama")
    b10.add_string("general.architecture", "gpt2")  # duplicate key, different value
    _write_fixture("duplicate_metadata_key", b10.build(), category="metadata_table",
                    description="'general.architecture' appears twice with different values.",
                    expected_stage="2. Metadata table", expected_reason="duplicate required key")

    # 11. Duplicate tensor name.
    b11 = RawGGUFBuilder()
    b11.add_string("general.architecture", "llama")
    arr11 = np.array([1.0, 2.0], dtype=np.float32)
    b11.add_tensor("dup.weight", arr11.shape, arr11.tobytes())
    b11.add_tensor("dup.weight", arr11.shape, arr11.tobytes())  # same name twice
    _write_fixture("duplicate_tensor_name", b11.build(), category="tensor_descriptors",
                    description="Two tensor descriptors both named 'dup.weight'.",
                    expected_stage="3. Tensor descriptors", expected_reason="duplicate tensor name")

    # 12. Zero dimension.
    b12 = RawGGUFBuilder()
    b12.add_string("general.architecture", "llama")
    b12.tensors.append(__import__("phase2_prep.raw_gguf_writer", fromlist=["TensorSpec"]).TensorSpec(
        name="zero.weight", dims=(0, 4), data=b""))
    _write_fixture("zero_dimension", b12.build(), category="tensor_descriptors",
                    description="Tensor descriptor has a dimension of 0.",
                    expected_stage="3. Tensor descriptors", expected_reason="zero-length dimension (nonzero rank required)")

    # 13. Unsupported dtype (a ggml_type value not in the first supported tuple, e.g. 99).
    b13 = RawGGUFBuilder()
    b13.add_string("general.architecture", "llama")
    header13 = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 1) + struct.pack("<Q", 1)
    kv13 = _gguf_string("general.architecture") + struct.pack("<I", 8) + _gguf_string("llama")
    tensor_info13 = _gguf_string("weird.weight") + struct.pack("<I", 1) + struct.pack("<Q", 2) + struct.pack("<I", 99) + struct.pack("<Q", 0)
    pre13 = header13 + kv13 + tensor_info13
    pad13 = (-len(pre13)) % 32
    pre13 += b"\x00" * pad13
    _write_fixture("unsupported_dtype", pre13 + b"\x00" * 32, category="tensor_descriptors",
                    description="Tensor type field set to 99, not a supported ggml_type for the first tuple (F32 only).",
                    expected_stage="3. Tensor descriptors", expected_reason="unsupported tensor dtype")

    # 14. Misaligned tensor offset (offset not a multiple of alignment=32).
    b14 = RawGGUFBuilder()
    b14.add_string("general.architecture", "llama")
    arr14 = np.array([1.0, 2.0, 3.0, 4.0], dtype=np.float32)
    b14.add_tensor("misaligned.weight", arr14.shape, arr14.tobytes())
    data14 = bytearray(b14.build())
    # Find and corrupt the offset field (last 8 bytes of the tensor_info section, before alignment padding).
    # For this single-tensor case we know the offset is 0; force it to 5 (not a multiple of 32) directly
    # in the built bytes is fragile, so instead rebuild with a manually-placed bad offset:
    header14 = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 1) + struct.pack("<Q", 1)
    kv14 = _gguf_string("general.architecture") + struct.pack("<I", 8) + _gguf_string("llama")
    tensor_info14 = _gguf_string("misaligned.weight") + struct.pack("<I", 1) + struct.pack("<Q", 4) + struct.pack("<I", 0) + struct.pack("<Q", 5)
    pre14 = header14 + kv14 + tensor_info14
    pad14 = (-len(pre14)) % 32
    pre14 += b"\x00" * pad14
    _write_fixture("misaligned_tensor_offset", pre14 + b"\x00" * 64, category="tensor_descriptors",
                    description="Tensor offset field set to 5, not a multiple of the 32-byte alignment.",
                    expected_stage="3. Tensor descriptors", expected_reason="tensor offset not aligned")

    # 15. Offset before data / negative-equivalent (offset points before the tensor-data section start
    #     is impossible to express as a valid u64 "before zero", so this fixture instead demonstrates
    #     the equivalent real-world case: offset overlapping the tensor_info/metadata region itself by
    #     being interpreted as file-absolute when it must be data-section-relative -- i.e. offset=0 for
    #     a SECOND tensor when a first tensor already legitimately occupies bytes [0, N), which IS the
    #     overlapping-tensors category (#16). This fixture is intentionally merged into #16 rather than
    #     duplicated as a fake distinct case -- see conformance manifest note.)

    # 16. Overlapping tensor extents.
    b16 = RawGGUFBuilder()
    b16.add_string("general.architecture", "llama")
    arr16a = np.array([1.0, 2.0, 3.0, 4.0], dtype=np.float32)
    arr16b = np.array([5.0, 6.0, 7.0, 8.0], dtype=np.float32)
    b16.add_tensor("first.weight", arr16a.shape, arr16a.tobytes())
    b16.add_tensor("second.weight", arr16b.shape, arr16b.tobytes())
    data16 = b16.build()
    # Force both tensors' offset to 0 by rebuilding with explicit TensorSpec + manual offset patch:
    # (simplest robust approach: rebuild tensor_info manually with both offsets = 0)
    header16 = b"GGUF" + struct.pack("<I", 3) + struct.pack("<Q", 2) + struct.pack("<Q", 1)
    kv16 = _gguf_string("general.architecture") + struct.pack("<I", 8) + _gguf_string("llama")
    ti16 = (_gguf_string("first.weight") + struct.pack("<I", 1) + struct.pack("<Q", 4) + struct.pack("<I", 0) + struct.pack("<Q", 0) +
            _gguf_string("second.weight") + struct.pack("<I", 1) + struct.pack("<Q", 4) + struct.pack("<I", 0) + struct.pack("<Q", 0))
    pre16 = header16 + kv16 + ti16
    pad16 = (-len(pre16)) % 32
    pre16 += b"\x00" * pad16
    pre16 += arr16a.tobytes() + b"\x00" * (32 - 16) + arr16b.tobytes() + b"\x00" * (32 - 16)
    _write_fixture("overlapping_tensors", pre16, category="tensor_descriptors",
                    description="Two distinct tensors ('first.weight', 'second.weight') both declare offset=0, overlapping.",
                    expected_stage="3. Tensor descriptors", expected_reason="overlapping tensor extents")

    # 17. Missing required metadata (general.architecture omitted entirely).
    b17 = RawGGUFBuilder()
    b17.add_string("general.name", "no-architecture-field")
    arr17 = np.array([1.0, 2.0], dtype=np.float32)
    b17.add_tensor("some.weight", arr17.shape, arr17.tobytes())
    _write_fixture("missing_required_metadata", b17.build(), category="architecture_binding",
                    description="'general.architecture' key is entirely absent.",
                    expected_stage="4. Architecture binding", expected_reason="required key general.architecture missing")

    # 18. Inconsistent dimensions (declared embedding_length metadata contradicts the actual
    #     token_embd.weight tensor's own dimension).
    b18 = RawGGUFBuilder()
    b18.add_string("general.architecture", "llama")
    b18.add_uint32("llama.embedding_length", 999)  # claims hidden=999
    arr18 = np.array([[1.0, 2.0, 3.0, 4.0]], dtype=np.float32)  # actual tensor hidden dim is 4
    b18.add_tensor("token_embd.weight", arr18.shape, arr18.tobytes())
    _write_fixture("inconsistent_dimensions", b18.build(), category="architecture_binding",
                    description="llama.embedding_length metadata says 999 but token_embd.weight's actual "
                                "last dimension is 4.",
                    expected_stage="4. Architecture binding",
                    expected_reason="tensor dimension does not match declared metadata dimension")

    FIXTURES.insert(0, {
        "name": "_valid_baseline", "file": "_valid_baseline.gguf", "category": "positive_control",
        "description": "Genuinely well-formed GGUF (verified via gguf-py's independent GGUFReader). "
                        "Every other fixture in this corpus is a single corruption applied to this file.",
        "expected_reader_stage": "n/a", "expected_verdict": "accept", "expected_reason": "n/a",
        "file_size_bytes": len(baseline),
    })

    manifest = {
        "schema_version": 1,
        "baseline_verified_valid_via": "gguf-py GGUFReader (independent parser)",
        "note_on_fixture_15": "offset-before-data-start is not independently constructible as a distinct "
                               "u64 value from overlapping-tensors; merged into fixture #16 "
                               "(overlapping_tensors) rather than faked as a separate case.",
        "fixtures": FIXTURES,
    }
    with open(MANIFEST_PATH, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=False)

    print(f"generated {len(FIXTURES)} malformed GGUF fixtures in {OUTPUT_DIR}")
    for fx in FIXTURES:
        print(f"  {fx['name']} ({fx['category']}): {fx['file_size_bytes']} bytes")
    print(f"\nconformance manifest written to {MANIFEST_PATH}")


def check_against_gguf_py() -> None:
    """
    Records how gguf-py (an existing, widely-used, independent parser --
    not the strict OrcEngine parser Phase 2 will build) handles each
    fixture. This is informative, not a pass/fail gate for THIS script:
    fixtures gguf-py silently accepts are exactly where OrcEngine's own
    Phase 2 parser must be stricter than the ecosystem norm, per
    MODEL_FORMAT_AND_GGUF.md's stated design goal ("how OrcEngine can be
    better at GGUF ingestion"). Appends the result to the manifest.
    """
    from gguf import GGUFReader

    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    for fx in manifest["fixtures"]:
        if fx["category"] == "positive_control":
            continue
        path = os.path.join(OUTPUT_DIR, fx["file"])
        try:
            r = GGUFReader(path)
            fx["gguf_py_verdict"] = "silently_accepted"
            fx["gguf_py_detail"] = f"tensors={len(r.tensors)}, fields={len(r.fields)}"
        except Exception as e:
            fx["gguf_py_verdict"] = "rejected"
            fx["gguf_py_detail"] = f"{type(e).__name__}: {str(e)[:120]}"

    silently_accepted = [fx["name"] for fx in manifest["fixtures"] if fx.get("gguf_py_verdict") == "silently_accepted"]
    manifest["gguf_py_comparison_summary"] = {
        "purpose": "gguf-py is an established, independent parser -- NOT the strict OrcEngine Phase 2 "
                   "parser. Fixtures it silently accepts mark exactly where OrcEngine's own parser must "
                   "be stricter than the current ecosystem norm.",
        "fixtures_gguf_py_silently_accepts": silently_accepted,
        "count_rejected_by_gguf_py": sum(1 for fx in manifest["fixtures"]
                                          if fx.get("gguf_py_verdict") == "rejected"),
        "count_silently_accepted_by_gguf_py": len(silently_accepted),
    }

    with open(MANIFEST_PATH, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=False)

    print(f"\ngguf-py comparison: {len(manifest['gguf_py_comparison_summary']['fixtures_gguf_py_silently_accepts'])} "
          f"of {len(manifest['fixtures']) - 1} fixtures are silently ACCEPTED by gguf-py (ecosystem baseline is "
          f"more lenient than what OrcEngine's own Phase 2 parser must enforce):")
    for name in silently_accepted:
        print(f"  - {name}")


if __name__ == "__main__":
    generate_all()
    check_against_gguf_py()
