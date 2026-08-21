# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys
from collections import Counter

from gguf import GGUFReader


def metadata_string(reader: GGUFReader, key: str) -> str:
    field = reader.fields[key]
    return bytes(field.parts[field.data[0]]).decode("utf-8")


def main(inspector: str, model: str) -> None:
    parsed = json.loads(subprocess.check_output([inspector, "--json", model], text=True))
    reference = GGUFReader(model)
    expected_fields = len(reference.fields) - 3  # gguf-py exposes three synthetic GGUF.* header fields.
    expected = {
        tensor.name: {
            "dimensions": [int(value) for value in tensor.shape],
            "encoding": tensor.tensor_type.name,
            "offset": int(tensor.data_offset),
            "encoded_length": int(tensor.n_bytes),
        }
        for tensor in reference.tensors
    }
    actual = {
        tensor["name"]: {
            "dimensions": tensor["dimensions"],
            "encoding": tensor["encoding"],
            "offset": tensor["offset"],
            "encoded_length": tensor["encoded_length"],
        }
        for tensor in parsed["tensors"]
    }

    assert parsed["gguf_version"] == int(reference.fields["GGUF.version"].parts[0][0])
    assert parsed["metadata_count"] == expected_fields
    assert parsed["tensor_count"] == len(reference.tensors)
    assert parsed["architecture"] == metadata_string(reference, "general.architecture")
    assert actual == expected
    assert parsed["mapped_tensor_count"] == len(reference.tensors)
    assert parsed["tied_embeddings"] == ("output.weight" not in expected)

    types = Counter(tensor["encoding"] for tensor in parsed["tensors"])
    print(
        "CROSS-READER PASS: "
        f"version={parsed['gguf_version']} metadata={parsed['metadata_count']} "
        f"tensors={parsed['tensor_count']} mapped={parsed['mapped_tensor_count']} "
        f"tied={parsed['tied_embeddings']} encodings={dict(sorted(types.items()))}"
    )


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: cross_reader_check.py INSPECTOR MODEL.gguf")
    main(sys.argv[1], sys.argv[2])
