# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import os
import struct
import sys
from pathlib import Path

import numpy as np
from gguf import GGUFWriter


ROOT = Path(__file__).resolve().parents[2]
PHASE0 = ROOT / "OrcEnginePhase0"
sys.path.insert(0, str(PHASE0))

from oracle.weights import build_weights  # noqa: E402
from phase2_prep.raw_gguf_writer import (  # noqa: E402
    GGUF_TYPE_ARRAY,
    GGUF_TYPE_STRING,
    RawGGUFBuilder,
    _gguf_string,
)
import phase2_prep.generate_malformed_fixtures as malformed  # noqa: E402


SEED = 20260815
CONFIG = {
    "vocab": 32,
    "hidden": 16,
    "intermediate": 32,
    "n_layers": 2,
    "n_q_heads": 4,
    "n_kv_heads": 2,
    "head_dim": 4,
    "max_positions": 16,
    "rmsnorm_epsilon": 1e-5,
    "rope_theta": 10000.0,
}


def tensor_items(include_output: bool) -> list[tuple[str, np.ndarray]]:
    weights = build_weights(
        seed=SEED,
        vocab=CONFIG["vocab"],
        hidden=CONFIG["hidden"],
        intermediate=CONFIG["intermediate"],
        n_layers=CONFIG["n_layers"],
        n_q_heads=CONFIG["n_q_heads"],
        n_kv_heads=CONFIG["n_kv_heads"],
        head_dim=CONFIG["head_dim"],
    )
    items: list[tuple[str, np.ndarray]] = [
        ("token_embd.weight", weights.token_embedding),
        ("output_norm.weight", weights.final_norm_weight),
    ]
    if include_output:
        items.append(("output.weight", weights.token_embedding.copy()))
    for i, layer in enumerate(weights.layers):
        prefix = f"blk.{i}."
        items.extend([
            (prefix + "attn_norm.weight", layer.attn_norm_weight),
            (prefix + "attn_q.weight", layer.w_q),
            (prefix + "attn_k.weight", layer.w_k),
            (prefix + "attn_v.weight", layer.w_v),
            (prefix + "attn_output.weight", layer.w_o),
            (prefix + "ffn_norm.weight", layer.ffn_norm_weight),
            (prefix + "ffn_gate.weight", layer.w_gate),
            (prefix + "ffn_up.weight", layer.w_up),
            (prefix + "ffn_down.weight", layer.w_down),
        ])
    return items


def add_metadata(builder: RawGGUFBuilder, *, reversed_order: bool = False) -> None:
    entries = [
        ("string", "general.architecture", "llama"),
        ("string", "general.name", "OrcEngine Phase-2 tiny Llama"),
        ("uint32", "general.file_type", 0),
        ("uint32", "llama.context_length", CONFIG["max_positions"]),
        ("uint32", "llama.embedding_length", CONFIG["hidden"]),
        ("uint32", "llama.block_count", CONFIG["n_layers"]),
        ("uint32", "llama.feed_forward_length", CONFIG["intermediate"]),
        ("uint32", "llama.attention.head_count", CONFIG["n_q_heads"]),
        ("uint32", "llama.attention.head_count_kv", CONFIG["n_kv_heads"]),
        ("float32", "llama.attention.layer_norm_rms_epsilon", CONFIG["rmsnorm_epsilon"]),
        ("uint32", "llama.rope.dimension_count", CONFIG["head_dim"]),
        ("float32", "llama.rope.freq_base", CONFIG["rope_theta"]),
    ]
    if reversed_order:
        entries.reverse()
    for kind, key, value in entries:
        getattr(builder, f"add_{kind}")(key, value)


def write_raw(path: Path, *, include_output: bool, alignment: int = 32,
              reverse_metadata: bool = False, reverse_tensors: bool = False) -> None:
    builder = RawGGUFBuilder(alignment=alignment)
    add_metadata(builder, reversed_order=reverse_metadata)
    if alignment != 32:
        builder.add_uint32("general.alignment", alignment)
    items = tensor_items(include_output)
    if reverse_tensors:
        items.reverse()
    for name, array in items:
        contiguous = np.ascontiguousarray(array, dtype=np.float32)
        builder.add_tensor(name, contiguous.shape, contiguous.tobytes())
    path.write_bytes(builder.build())


def write_f16(path: Path) -> None:
    writer = GGUFWriter(str(path), arch="llama")
    writer.add_name("OrcEngine Phase-2 tiny Llama F16")
    writer.add_context_length(CONFIG["max_positions"])
    writer.add_embedding_length(CONFIG["hidden"])
    writer.add_block_count(CONFIG["n_layers"])
    writer.add_feed_forward_length(CONFIG["intermediate"])
    writer.add_head_count(CONFIG["n_q_heads"])
    writer.add_head_count_kv(CONFIG["n_kv_heads"])
    writer.add_layer_norm_rms_eps(CONFIG["rmsnorm_epsilon"])
    writer.add_rope_dimension_count(CONFIG["head_dim"])
    writer.add_rope_freq_base(CONFIG["rope_theta"])
    writer.add_file_type(1)
    for name, array in tensor_items(include_output=True):
        writer.add_tensor(name, np.ascontiguousarray(array, dtype=np.float16))
    writer.write_header_to_file()
    writer.write_kv_data_to_file()
    writer.write_tensors_to_file()
    writer.close()


def write_extra_fixtures(output: Path) -> None:
    malformed_dir = output / "malformed"
    malformed_dir.mkdir(parents=True, exist_ok=True)
    (malformed_dir / "huge_metadata_count.gguf").write_bytes(
        b"GGUF" + struct.pack("<IQQ", 3, 0, (1 << 64) - 1)
    )
    (malformed_dir / "truncated_string.gguf").write_bytes(
        b"GGUF" + struct.pack("<IQQ", 3, 0, 1) + struct.pack("<Q", 20) + b"abc"
    )
    (malformed_dir / "malformed_array.gguf").write_bytes(
        b"GGUF" + struct.pack("<IQQ", 3, 0, 1)
        + _gguf_string("general.architecture")
        + struct.pack("<IIQ", GGUF_TYPE_ARRAY, GGUF_TYPE_STRING, 1)
        + struct.pack("<Q", 100)
    )

    invalid_alignment = RawGGUFBuilder()
    invalid_alignment.add_string("general.architecture", "llama")
    invalid_alignment.add_uint32("general.alignment", 7)
    (malformed_dir / "invalid_alignment.gguf").write_bytes(invalid_alignment.build())

    invalid_bool = RawGGUFBuilder()
    invalid_bool.add_string("general.architecture", "llama")
    invalid_bool.metadata.append(("test.invalid_bool", 7, b"\x02"))
    (malformed_dir / "invalid_bool.gguf").write_bytes(invalid_bool.build())

    missing_tensor = RawGGUFBuilder()
    add_metadata(missing_tensor)
    name, array = tensor_items(include_output=False)[0]
    missing_tensor.add_tensor(name, array.shape, np.ascontiguousarray(array, dtype=np.float32).tobytes())
    (malformed_dir / "missing_required_tensor.gguf").write_bytes(missing_tensor.build())

    wrong_output = RawGGUFBuilder()
    add_metadata(wrong_output)
    for name, array in tensor_items(include_output=True):
        if name == "output.weight":
            array = array[:-1]
        contiguous = np.ascontiguousarray(array, dtype=np.float32)
        wrong_output.add_tensor(name, contiguous.shape, contiguous.tobytes())
    (malformed_dir / "wrong_output_shape.gguf").write_bytes(wrong_output.build())

    # Structurally valid Q8_0 tensor with the required quantization metadata omitted.
    header = b"GGUF" + struct.pack("<IQQ", 3, 1, 1)
    metadata = _gguf_string("general.architecture") + struct.pack("<I", GGUF_TYPE_STRING) + _gguf_string("llama")
    descriptor = _gguf_string("token_embd.weight") + struct.pack("<IQIQ", 1, 32, 8, 0)
    pre_data = header + metadata + descriptor
    pre_data += b"\x00" * ((-len(pre_data)) % 32)
    (malformed_dir / "quantized_missing_version.gguf").write_bytes(pre_data + b"\x00" * 34)

    for name, key in {
        "metadata_key_uppercase": "General.architecture",
        "metadata_key_hyphen": "general.architecture-name",
        "metadata_key_leading_underscore": "general._architecture",
        "metadata_key_trailing_underscore": "general.architecture_",
        "metadata_key_double_underscore": "general.architecture__name",
        "metadata_key_empty_segment": "general..architecture",
        "metadata_key_non_ascii": "général.architecture",
    }.items():
        invalid_key = RawGGUFBuilder()
        invalid_key.add_string(key, "llama")
        (malformed_dir / f"{name}.gguf").write_bytes(invalid_key.build())

    long_name = RawGGUFBuilder()
    long_name.add_string("general.architecture", "llama")
    long_name.add_tensor("x" * 65, (1,), struct.pack("<f", 1.0))
    (malformed_dir / "tensor_name_over_64_bytes.gguf").write_bytes(long_name.build())

    missing_embedding = RawGGUFBuilder()
    add_metadata(missing_embedding)
    for name, array in tensor_items(include_output=True):
        if name == "token_embd.weight":
            continue
        contiguous = np.ascontiguousarray(array, dtype=np.float32)
        missing_embedding.add_tensor(name, contiguous.shape, contiguous.tobytes())
    (malformed_dir / "missing_token_embedding.gguf").write_bytes(missing_embedding.build())

    unsupported = RawGGUFBuilder()
    unsupported.add_string("general.architecture", "qwen2")
    (output / "unsupported_architecture.gguf").write_bytes(unsupported.build())


def main(output_dir: str) -> None:
    output = Path(output_dir)
    output.mkdir(parents=True, exist_ok=True)
    write_raw(output / "model_tied.gguf", include_output=False)
    write_raw(output / "model_untied.gguf", include_output=True)
    write_raw(output / "model_reordered.gguf", include_output=True,
              reverse_metadata=True, reverse_tensors=True)
    write_raw(output / "model_align64.gguf", include_output=True, alignment=64)
    write_f16(output / "model_f16.gguf")

    malformed_dir = output / "malformed"
    malformed.OUTPUT_DIR = str(malformed_dir)
    malformed.MANIFEST_PATH = str(malformed_dir / "CONFORMANCE_MANIFEST.json")
    malformed.FIXTURES.clear()
    malformed.generate_all()
    write_extra_fixtures(output)
    (output / ".generated").write_text("phase2 fixtures generated\n", encoding="utf-8")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit("usage: generate_fixtures.py OUTPUT_DIR")
    main(sys.argv[1])
