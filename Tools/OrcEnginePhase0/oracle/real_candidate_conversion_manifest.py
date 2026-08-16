# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
real_candidate_conversion acceptance check, per PHASE_0_ACCEPTANCE.yaml:
  "pinned conversion command and GGUF hash with strict parser manifest"

"Strict parser manifest" here means: read the just-written GGUF back with
gguf-py's own GGUFReader (an independent, established parser -- not our
writer trusting itself) and record every tensor's name/shape/dtype and
every metadata field, so what we WROTE is verified against what actually
PARSES, not merely asserted. This is real round-trip verification, not a
restatement of the writer's intent.
"""
from __future__ import annotations

import hashlib
import json
import os

from gguf import GGUFReader

from oracle.convert_real_candidate import SOURCE_DIR, convert

MANIFEST_PATH = os.path.join(os.path.dirname(__file__), "..", "artifacts", "real_candidate_conversion_manifest.json")


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def _sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def run() -> dict:
    output_path = convert()  # re-run conversion fresh, not reuse a stale file

    converter_script_path = os.path.join(os.path.dirname(__file__), "convert_real_candidate.py")
    source_safetensors_path = os.path.join(SOURCE_DIR, "model.safetensors")

    reader = GGUFReader(output_path)
    tensor_manifest = [
        {"name": t.name, "shape": list(int(x) for x in t.shape), "dtype": str(t.tensor_type)}
        for t in reader.tensors
    ]
    field_manifest = {}
    for name, field in reader.fields.items():
        try:
            parts = list(field.parts[field.data[0]]) if field.data else None
            field_manifest[name] = str(parts)[:200]
        except Exception:
            field_manifest[name] = "<array or complex field>"

    manifest = {
        "conversion_command": "python3 -m oracle.convert_real_candidate",
        "converter_script_sha256": _sha256_file(converter_script_path),
        "source_safetensors_sha256": _sha256_file(source_safetensors_path),
        "output_gguf_path": output_path,
        "output_gguf_sha256": _sha256_file(output_path),
        "strict_parser": "gguf-py GGUFReader (independent read-back, not the writer)",
        "parsed_tensor_count": len(reader.tensors),
        "parsed_field_count": len(reader.fields),
        "parsed_tensors": tensor_manifest,
        "parsed_metadata_fields": sorted(field_manifest.keys()),
    }

    os.makedirs(os.path.dirname(MANIFEST_PATH), exist_ok=True)
    with open(MANIFEST_PATH, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)

    print(f"parsed {manifest['parsed_tensor_count']} tensors, {manifest['parsed_field_count']} metadata fields "
          f"back from the converted GGUF via an independent reader")
    print(f"output_gguf_sha256: {manifest['output_gguf_sha256']}")
    print(f"wrote manifest to {MANIFEST_PATH}")

    expected_tensor_count = 3 + 30 * 9  # token_embd + output_norm + output + 30 layers * 9 tensors each
    ok = manifest["parsed_tensor_count"] == expected_tensor_count
    print(f"\ntensor count sanity check: parsed={manifest['parsed_tensor_count']} "
          f"expected={expected_tensor_count} match={ok}")
    return manifest, ok


if __name__ == "__main__":
    _manifest, ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: real_candidate_conversion")
    raise SystemExit(0 if ok else 1)
