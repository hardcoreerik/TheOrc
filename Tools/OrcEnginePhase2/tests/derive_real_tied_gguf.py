# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import hashlib
import sys
from pathlib import Path

import numpy as np
from gguf import GGUFReader, GGUFWriter


def scalar(reader: GGUFReader, key: str) -> int | float:
    field = reader.fields[key]
    return np.asarray(field.parts[field.data[0]]).item()


def string(reader: GGUFReader, key: str) -> str:
    field = reader.fields[key]
    return bytes(field.parts[field.data[0]]).decode("utf-8")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(8 * 1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().upper()


def main(source_path: str, target_path: str) -> None:
    source = Path(source_path).resolve()
    target = Path(target_path).resolve()
    reader = GGUFReader(str(source))
    tensors = {tensor.name: tensor for tensor in reader.tensors}
    embedding = tensors["token_embd.weight"]
    output = tensors["output.weight"]
    if (
        embedding.data.shape != output.data.shape
        or embedding.data.dtype != output.data.dtype
        or not np.array_equal(embedding.data.view(np.uint8), output.data.view(np.uint8))
    ):
        raise AssertionError("source output.weight is not byte-identical to token_embd.weight")

    target.parent.mkdir(parents=True, exist_ok=True)
    writer = GGUFWriter(str(target), arch="llama")
    writer.add_name(string(reader, "general.name") + " tied-output derivative")
    writer.add_context_length(int(scalar(reader, "llama.context_length")))
    writer.add_embedding_length(int(scalar(reader, "llama.embedding_length")))
    writer.add_block_count(int(scalar(reader, "llama.block_count")))
    writer.add_feed_forward_length(int(scalar(reader, "llama.feed_forward_length")))
    writer.add_head_count(int(scalar(reader, "llama.attention.head_count")))
    writer.add_head_count_kv(int(scalar(reader, "llama.attention.head_count_kv")))
    writer.add_layer_norm_rms_eps(float(scalar(reader, "llama.attention.layer_norm_rms_epsilon")))
    writer.add_rope_dimension_count(int(scalar(reader, "llama.rope.dimension_count")))
    writer.add_rope_freq_base(float(scalar(reader, "llama.rope.freq_base")))
    writer.add_file_type(0)
    for tensor in reader.tensors:
        if tensor.name != "output.weight":
            writer.add_tensor(tensor.name, tensor.data)
    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()

    derived = GGUFReader(str(target))
    derived_names = {tensor.name for tensor in derived.tensors}
    if "output.weight" in derived_names or len(derived_names) != len(reader.tensors) - 1:
        raise AssertionError("derived artifact did not remove exactly output.weight")
    print(
        "REAL TIED GGUF DERIVED: "
        f"source={source} source_sha256={sha256(source)} source_tensors={len(reader.tensors)} "
        f"target={target} target_sha256={sha256(target)} target_tensors={len(derived.tensors)}"
    )


if __name__ == "__main__":
    if len(sys.argv) != 3:
        raise SystemExit("usage: derive_real_tied_gguf.py SOURCE.gguf TARGET.gguf")
    main(sys.argv[1], sys.argv[2])
