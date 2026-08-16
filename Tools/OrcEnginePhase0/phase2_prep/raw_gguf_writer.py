# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Minimal raw GGUF byte-level writer, built directly from the binary layout
in the GGUF specification (https://github.com/ggml-org/ggml/docs/gguf.md),
NOT via the `gguf` PyPI package's GGUFWriter. This is deliberate: Phase 2
prep needs byte-level control to construct deliberately MALFORMED fixtures
(bad magic, truncation, overflow, misalignment, etc. -- see
docs/OrcEngine/MODEL_FORMAT_AND_GGUF.md "Malformed-input suite"), which a
well-behaved high-level writer won't let you produce.

Layout (little-endian, version 3):
  magic: 4 bytes "GGUF"
  version: uint32
  tensor_count: uint64
  metadata_kv_count: uint64
  metadata_kv[metadata_kv_count]:
    key: gguf_string (uint64 length + utf8 bytes, no NUL terminator)
    value_type: uint32
    value: per type
  tensor_info[tensor_count]:
    name: gguf_string
    n_dimensions: uint32
    dimensions[n_dimensions]: uint64 each
    type: uint32 (ggml_type)
    offset: uint64 (relative to the START of the tensor-data section, which
            begins at the first alignment-boundary after the tensor_info array)
  padding to `general.alignment` (default 32 if absent)
  tensor_data: raw bytes per tensor

This is NOT a full writer -- only string/uint32/uint64/float32/array-of-string
metadata types and F32 tensors are supported, which is all the malformed-
fixture generator needs.
"""
from __future__ import annotations

import struct
from dataclasses import dataclass, field

MAGIC = b"GGUF"
DEFAULT_ALIGNMENT = 32

GGUF_TYPE_UINT32 = 4
GGUF_TYPE_FLOAT32 = 6
GGUF_TYPE_STRING = 8
GGUF_TYPE_ARRAY = 9
GGUF_TYPE_UINT64 = 10

GGML_TYPE_F32 = 0


def _gguf_string(s: str) -> bytes:
    b = s.encode("utf-8")
    return struct.pack("<Q", len(b)) + b


@dataclass
class TensorSpec:
    name: str
    dims: tuple[int, ...]   # numpy-order dims; written reversed (ggml ne convention)
    data: bytes             # raw F32 tensor bytes, already in the right layout


@dataclass
class RawGGUFBuilder:
    version: int = 3
    metadata: list[tuple[str, int, bytes]] = field(default_factory=list)  # (key, type, encoded_value)
    tensors: list[TensorSpec] = field(default_factory=list)
    alignment: int = DEFAULT_ALIGNMENT

    def add_string(self, key: str, value: str) -> None:
        self.metadata.append((key, GGUF_TYPE_STRING, _gguf_string(value)))

    def add_uint32(self, key: str, value: int) -> None:
        self.metadata.append((key, GGUF_TYPE_UINT32, struct.pack("<I", value)))

    def add_uint64(self, key: str, value: int) -> None:
        self.metadata.append((key, GGUF_TYPE_UINT64, struct.pack("<Q", value)))

    def add_float32(self, key: str, value: float) -> None:
        self.metadata.append((key, GGUF_TYPE_FLOAT32, struct.pack("<f", value)))

    def add_string_array(self, key: str, values: list[str]) -> None:
        body = struct.pack("<I", GGUF_TYPE_STRING) + struct.pack("<Q", len(values))
        for v in values:
            body += _gguf_string(v)
        self.metadata.append((key, GGUF_TYPE_ARRAY, body))

    def add_tensor(self, name: str, dims: tuple[int, ...], data: bytes) -> None:
        self.tensors.append(TensorSpec(name=name, dims=dims, data=data))

    def build(self) -> bytes:
        """Returns the complete, well-formed GGUF file bytes."""
        header = MAGIC + struct.pack("<I", self.version)
        header += struct.pack("<Q", len(self.tensors))
        header += struct.pack("<Q", len(self.metadata))

        kv_bytes = b""
        for key, vtype, encoded in self.metadata:
            kv_bytes += _gguf_string(key) + struct.pack("<I", vtype) + encoded

        # First pass: compute tensor_info bytes with placeholder offsets, to know
        # where the tensor-data section starts (needed for real offsets).
        tensor_info_bytes = b""
        running_offset = 0
        offsets = []
        for t in self.tensors:
            offsets.append(running_offset)
            size = len(t.data)
            padded = (size + self.alignment - 1) // self.alignment * self.alignment
            running_offset += padded

        for t, off in zip(self.tensors, offsets):
            tensor_info_bytes += _gguf_string(t.name)
            reversed_dims = tuple(reversed(t.dims))
            tensor_info_bytes += struct.pack("<I", len(reversed_dims))
            for d in reversed_dims:
                tensor_info_bytes += struct.pack("<Q", d)
            tensor_info_bytes += struct.pack("<I", GGML_TYPE_F32)
            tensor_info_bytes += struct.pack("<Q", off)

        pre_data = header + kv_bytes + tensor_info_bytes
        pad_len = (-len(pre_data)) % self.alignment
        pre_data += b"\x00" * pad_len

        data_section = b""
        for t, off in zip(self.tensors, offsets):
            assert len(data_section) == off, "internal offset bookkeeping error"
            data_section += t.data
            padded_len = (len(t.data) + self.alignment - 1) // self.alignment * self.alignment
            data_section += b"\x00" * (padded_len - len(t.data))

        return pre_data + data_section
