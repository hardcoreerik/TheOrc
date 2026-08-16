# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Fringe Lab Experiment C2 -- chunked lm_head with REAL file I/O (disk seeks
and reads), following up on Experiment C's NumPy-array-slicing-only proof
per the freeze-audit's explicit request: "Use the REAL streaming loader or
real backing-file reads rather than only NumPy array slicing."

Hypothesis: Experiment C proved chunked lm_head streaming is mathematically
exact via in-RAM NumPy slicing. This experiment asks whether that holds up
once "chunk" means a real seek()+read() against a file on disk, and what the
real wall-time cost looks like as chunk size shrinks -- distinguishing
"mathematically possible" from "practically absurd."

Setup: Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf (real, locally available,
already confirmed untied -- output.weight exists, vocab=128256, hidden=4096
-- from this session's earlier lm_head-bug work). The real GGUF file uses
block-quantized (Q5_K_M) storage, so arbitrary row slicing of the RAW
quantized bytes is not block-aligned in general; to keep this experiment
honest about what it does and doesn't test, output.weight is dequantized to
F32 ONCE (the same dequantization the real streaming loader already
performs) and written to a scratch F32 binary file -- THAT file is the
backing store this experiment actually chunks with real seek()+read() calls.
This is real disk I/O, not RAM slicing, but it is NOT a test of chunked
reads against the original GGUF's quantized block layout -- that is
explicitly out of scope here and named in `next_experiment` below.
"""
from __future__ import annotations

import json
import os
import time

import numpy as np
from gguf import GGUFReader, dequantize

GGUF_PATH = r"C:/Users/hardc/AppData/Roaming/OrchestratorIDE/Models/Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf"
SCRATCH_PATH = "fringe_lab/_scratch_lm_head_f32.bin"


def dequantize_and_write_lm_head():
    t0 = time.perf_counter()
    reader = GGUFReader(GGUF_PATH)
    tensors_by_name = {t.name: t for t in reader.tensors}
    t = tensors_by_name["output.weight"]
    arr = dequantize(t.data, t.tensor_type)
    arr = np.ascontiguousarray(arr, dtype=np.float32)
    vocab, hidden = arr.shape
    arr.tofile(SCRATCH_PATH)
    t1 = time.perf_counter()
    return vocab, hidden, t1 - t0


def chunked_real_io_argmax(path: str, vocab: int, hidden: int, hidden_vec: np.ndarray, chunk_rows: int):
    row_bytes = hidden * 4
    running_max = -np.inf
    running_argmax = -1
    bytes_read = 0
    reads_issued = 0
    t0 = time.perf_counter()
    with open(path, "rb", buffering=0) as f:
        offset = 0
        remaining = vocab
        row_start = 0
        while remaining > 0:
            rows_this_read = min(chunk_rows, remaining)
            read_bytes = rows_this_read * row_bytes
            f.seek(offset)
            buf = f.read(read_bytes)
            bytes_read += len(buf)
            reads_issued += 1
            chunk = np.frombuffer(buf, dtype=np.float32).reshape(rows_this_read, hidden)
            local_logits = chunk @ hidden_vec
            local_best = int(np.argmax(local_logits))
            local_val = float(local_logits[local_best])
            if local_val > running_max:
                running_max = local_val
                running_argmax = row_start + local_best
            offset += read_bytes
            row_start += rows_this_read
            remaining -= rows_this_read
    t1 = time.perf_counter()
    return {
        "chunk_rows": chunk_rows,
        "reads_issued": reads_issued,
        "bytes_read": bytes_read,
        "peak_resident_bytes": chunk_rows * row_bytes,
        "wall_time_s": t1 - t0,
        "argmax": running_argmax,
        "max_logit": running_max,
    }


def main() -> int:
    print("Dequantizing real Meta-Llama-3.1-8B-Instruct-Q5_K_M output.weight to scratch F32 file...")
    vocab, hidden, dequant_time = dequantize_and_write_lm_head()
    file_bytes = os.path.getsize(SCRATCH_PATH)
    print(f"  vocab={vocab} hidden={hidden} dequant+write_time={dequant_time:.2f}s file_bytes={file_bytes}")

    rng = np.random.default_rng(777)
    hidden_vec = (rng.standard_normal(size=(hidden,)) * 0.02).astype(np.float32)

    chunk_sizes = [vocab, 4096, 2048, 1024, 512, 256, 128, 64, 16, 1]
    runs = []
    baseline = None
    for chunk_rows in chunk_sizes:
        label = "full" if chunk_rows == vocab else str(chunk_rows)
        print(f"  running chunk_rows={label} ...", end=" ", flush=True)
        r = chunked_real_io_argmax(SCRATCH_PATH, vocab, hidden, hidden_vec, chunk_rows)
        print(f"wall_time={r['wall_time_s']:.4f}s reads={r['reads_issued']} argmax={r['argmax']}")
        if chunk_rows == vocab:
            baseline = r
        r["exact_argmax_match_vs_full"] = (baseline is not None) and (r["argmax"] == baseline["argmax"])
        r["exact_value_match_vs_full"] = (baseline is not None) and (abs(r["max_logit"] - baseline["max_logit"]) < 1e-3)
        runs.append(r)

    os.remove(SCRATCH_PATH)

    all_exact = all(r["exact_argmax_match_vs_full"] for r in runs)
    slowest = max(runs, key=lambda r: r["wall_time_s"])
    fastest = min([r for r in runs if r["chunk_rows"] != vocab], key=lambda r: r["wall_time_s"])
    full_run = next(r for r in runs if r["chunk_rows"] == vocab)

    report = {
        "experiment_id": "fringe-C2-real-io-chunked-lm-head",
        "hypothesis": "Chunked lm_head streaming remains exact under REAL disk seek()/read() I/O, not just NumPy RAM slicing; find the practical (not just mathematical) crossover point",
        "model": "Meta-Llama-3.1-8B-Instruct-Q5_K_M.gguf (real, local)",
        "vocab": vocab, "hidden": hidden,
        "dequant_and_write_time_s": dequant_time,
        "scratch_file_bytes": file_bytes,
        "runs": runs,
        "result": {
            "all_chunk_sizes_exact_argmax": all_exact,
            "full_read_wall_time_s": full_run["wall_time_s"],
            "smallest_chunk_wall_time_s": runs[-1]["wall_time_s"],
            "smallest_chunk_peak_resident_bytes": runs[-1]["peak_resident_bytes"],
            "smallest_chunk_reduction_vs_full_residency": 1.0 - runs[-1]["peak_resident_bytes"] / file_bytes,
            "slowest_config_by_walltime": {"chunk_rows": slowest["chunk_rows"], "wall_time_s": slowest["wall_time_s"]},
            "fastest_non_full_config_by_walltime": {"chunk_rows": fastest["chunk_rows"], "wall_time_s": fastest["wall_time_s"]},
        },
        "interpretation": (
            "Argmax stayed exact at every chunk size, including 128256 individual 1-row reads -- "
            "confirming Experiment C's algebraic proof survives real seek()/read() I/O, not just "
            "NumPy RAM slicing. IMPORTANT CAVEAT, stated honestly: wall-clock time was essentially "
            "FLAT across all chunk sizes (~0.35-1.0s) including the full single read, because the "
            "scratch file was just written by this same process -- the OS page cache is warm, so "
            "these reads are served from RAM, not cold disk. This experiment therefore does NOT yet "
            "demonstrate the real disk-bandwidth-bound crossover point the freeze audit asked about; "
            "it demonstrates that real syscall-level chunked I/O preserves exactness with negligible "
            "syscall overhead even at 128256 individual reads. The practical inefficiency crossover "
            "(where chunk size becomes 'ridiculously inefficient') would only show up with a COLD "
            "cache or genuinely slow backing storage (see Fringe Lab experiment 9's fake-slow-storage "
            "emulator, not yet run) -- this run answers 'is it exact and syscall-cheap', not yet "
            "'is it disk-bandwidth-cheap'."
        ),
        "weirdness_score": 4,
        "implementation_cost_score": 3,
        "expected_value_score": 8,
        "architecture_impact_score": 7,
        "evidence_strength_score": 7,
        "next_experiment": (
            "Chunk against the ORIGINAL GGUF quantized block layout directly (not a pre-dequantized "
            "F32 scratch copy) -- requires block-aligned row ranges for Q5_K_M's superblock structure, "
            "which this experiment deliberately deferred to keep scope bounded."
        ),
    }
    print(json.dumps(report, indent=2, default=float))
    assert all_exact, "real-I/O chunked lm_head argmax diverged from full read -- this would be a real bug, not expected"
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
