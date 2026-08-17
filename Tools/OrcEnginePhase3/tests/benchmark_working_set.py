# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import hashlib
import json
import subprocess
import sys
import threading
import time
from pathlib import Path

import psutil


def run(executable: str, model: str) -> dict:
    started = time.perf_counter()
    process = subprocess.Popen(
        [executable, model, "1", "5", "--steps", "1"],
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
    )
    peak_rss = [0]

    def sample() -> None:
        while process.poll() is None:
            try:
                peak_rss[0] = max(peak_rss[0], psutil.Process(process.pid).memory_info().rss)
            except psutil.Error:
                pass
            time.sleep(0.05)

    sampler = threading.Thread(target=sample)
    sampler.start()
    stdout, stderr = process.communicate()
    sampler.join()
    if process.returncode:
        raise RuntimeError(stderr)
    document = json.loads(stdout)
    return {
        "wall_ms": (time.perf_counter() - started) * 1000,
        "sampled_peak_rss": peak_rss[0],
        "materialize_ms": document["materialize_milliseconds"],
        "forward_ms": document["steps"][0]["forward_milliseconds"],
        "selected": document["steps"][0]["selected"],
        "telemetry": document.get("telemetry"),
    }


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest().upper()


def main(full_exe: str, stream_exe: str, model_name: str, model_path: str) -> None:
    model = Path(model_path)
    evidence = {
        "artifact": model_name,
        "path": str(model),
        "file_bytes": model.stat().st_size,
        "sha256": sha256(model),
        "cache_state": "warm or unknown; OS filesystem cache was not flushed",
        "poll_interval_ms": 50,
        "cold_process": run(stream_exe, str(model)),
        "warmups": {
            "full": run(full_exe, str(model)),
            "streamed": run(stream_exe, str(model)),
        },
        "repetitions": [],
    }
    orders = [("full", full_exe), ("streamed", stream_exe)], [
        ("streamed", stream_exe), ("full", full_exe)
    ], [("full", full_exe), ("streamed", stream_exe)]
    for index, order in enumerate(orders, 1):
        evidence["repetitions"].append({
            "index": index,
            "order": [name for name, _ in order],
            "runs": {name: run(exe, str(model)) for name, exe in order},
        })
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: benchmark_working_set.py FULL_EXE STREAM_EXE NAME MODEL")
    main(*sys.argv[1:])
