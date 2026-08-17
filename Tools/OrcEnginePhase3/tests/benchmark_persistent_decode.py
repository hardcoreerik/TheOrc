# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import statistics
import subprocess
import sys
import time


def run(executable: str, model: str) -> dict:
    started = time.perf_counter()
    output = subprocess.check_output(
        [executable, model, "1", "5", "--steps", "4"], text=True
    )
    wall_ms = (time.perf_counter() - started) * 1000
    document = json.loads(output)
    forwards = [step["forward_milliseconds"] for step in document["steps"]]
    telemetry = document.get("telemetry")
    return {
        "wall_ms": wall_ms,
        "materialize_ms": document["materialize_milliseconds"],
        "initial_materialized_bytes": document["materialized_bytes"],
        "initial_materialization_count": document["initial_materialization_count"],
        "inference_ms": sum(forwards),
        "per_token_ms": forwards,
        "sequence": [1, 5] + [step["selected"] for step in document["steps"]],
        "backing_bytes_read": telemetry["backing_bytes_read"] if telemetry else document["backing_bytes_read"],
        "repeated_backing_bytes_read": telemetry["repeated_backing_bytes_read"] if telemetry else 0,
        "read_count": telemetry["read_count"] if telemetry else document["read_count"],
        "materialization_count": telemetry["materialization_count"] if telemetry else document["read_count"],
    }


def summarize(runs: list[dict]) -> dict:
    median = runs[len(runs) // 2]
    return {
        "median_wall_ms": statistics.median(run["wall_ms"] for run in runs),
        "median_materialize_ms": statistics.median(run["materialize_ms"] for run in runs),
        "median_inference_ms": statistics.median(run["inference_ms"] for run in runs),
        "median_time_per_token_ms": statistics.median(run["inference_ms"] / 4 for run in runs),
        "backing_bytes_read": median["backing_bytes_read"],
        "initial_backing_bytes_read": median["initial_materialized_bytes"],
        "ongoing_backing_bytes_per_token":
            (median["backing_bytes_read"] - median["initial_materialized_bytes"]) / 4,
        "repeated_backing_bytes_read": median["repeated_backing_bytes_read"],
        "reads": median["read_count"],
        "ongoing_reads_per_token":
            (median["read_count"] - median["initial_materialization_count"]) / 4,
        "materializations": median["materialization_count"],
        "ongoing_materializations_per_token":
            (median["materialization_count"] - median["initial_materialization_count"]) / 4,
        "sequence": median["sequence"],
    }


def main(full_exe: str, stream_exe: str, name: str, model: str) -> None:
    run(full_exe, model)
    run(stream_exe, model)
    full_runs, streamed_runs = [], []
    for order in (("full", "streamed"), ("streamed", "full"), ("full", "streamed")):
        for mode in order:
            (full_runs if mode == "full" else streamed_runs).append(
                run(full_exe if mode == "full" else stream_exe, model)
            )
    full = summarize(full_runs)
    streamed = summarize(streamed_runs)
    if full["sequence"] != streamed["sequence"]:
        raise AssertionError("persistent full and streamed greedy sequences differ")
    evidence = {
        "artifact": name,
        "cache_state": "warm or unknown; OS filesystem cache was not flushed",
        "method": "one warmup each, then three alternating fresh-process repetitions",
        "full_runs": full_runs,
        "streamed_runs": streamed_runs,
        "full_median": full,
        "streamed_median": streamed,
        "steady_state_slowdown": streamed["median_inference_ms"] / full["median_inference_ms"],
    }
    print(json.dumps(evidence, indent=2))


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: benchmark_persistent_decode.py FULL_EXE STREAM_EXE NAME MODEL")
    main(*sys.argv[1:])
