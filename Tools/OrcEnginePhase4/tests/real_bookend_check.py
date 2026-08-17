# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys
import time


def invoke(executable: str, model: str, steps: int, *extra: str,
           check: bool = True) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [executable, model, "1", "5", "--steps", str(steps), *extra],
        text=True, capture_output=True, check=check,
    )


def outputs(document: dict) -> list[tuple]:
    return [
        (step["tokens"], step["selected"], step["logits_last"], step.get("taps"))
        for step in document["steps"]
    ]


def main(frozen_full: str, frozen_streamed: str, current: str, model: str,
         steps: int, budget: int, chunks: list[int]) -> None:
    started = time.perf_counter()
    full = json.loads(invoke(frozen_full, model, steps).stdout)
    streamed = json.loads(invoke(frozen_streamed, model, steps).stdout)
    if outputs(full) != outputs(streamed):
        raise AssertionError("frozen Phase-3 full and streamed references differ")

    measurements = []
    for chunk in chunks:
        run_started = time.perf_counter()
        result = json.loads(invoke(
            current, model, steps, "--virtualize-bookends",
            "--output-chunk-rows", str(chunk),
        ).stdout)
        if outputs(result) != outputs(full):
            raise AssertionError(f"chunk {chunk} differs from frozen Phase-3 reference")
        telemetry = result["telemetry"]
        if telemetry["peak_resident_weight_bytes"] != budget:
            raise AssertionError(
                f"chunk {chunk}: expected peak {budget}, got "
                f"{telemetry['peak_resident_weight_bytes']}"
            )
        measurements.append({
            "chunk": chunk,
            "seconds": round(time.perf_counter() - run_started, 6),
            "peak": telemetry["peak_resident_weight_bytes"],
            "embedding_bytes": telemetry["embedding_backing_bytes_read"],
            "output_bytes": telemetry["output_backing_bytes_read"],
            "materializations": telemetry["materialization_count"],
            "reads": telemetry["read_count"],
            "embedding_ms": telemetry["embedding_milliseconds"],
            "output_ms": telemetry["output_projection_milliseconds"],
        })

    phase3 = invoke(
        frozen_streamed, model, 1, "--budget-bytes", str(budget), check=False
    )
    if phase3.returncode == 0 or "residency budget" not in phase3.stderr:
        raise AssertionError("strong Phase-4 budget did not reject frozen Phase 3")
    below = invoke(
        current, model, 1, "--virtualize-bookends", "--output-chunk-rows", "1024",
        "--budget-bytes", str(budget - 1), check=False,
    )
    if below.returncode == 0 or "residency budget" not in below.stderr:
        raise AssertionError("one byte below Phase-4 peak did not fail closed")
    exact = json.loads(invoke(
        current, model, 1, "--virtualize-bookends", "--output-chunk-rows", "1024",
        "--budget-bytes", str(budget),
    ).stdout)
    if outputs(exact) != outputs(full)[:1]:
        raise AssertionError("exact-budget output differs from frozen reference")

    sequence = [1, 5] + [step["selected"] for step in full["steps"]]
    print(json.dumps({
        "result": "PASS",
        "sequence": sequence,
        "full_resident_bytes": full["materialized_bytes"],
        "phase3_peak_bytes": streamed["telemetry"]["peak_resident_weight_bytes"],
        "phase4_peak_bytes": budget,
        "phase3_rejected_at_phase4_peak": True,
        "phase4_peak_minus_one_rejected": True,
        "measurements": measurements,
        "total_seconds": round(time.perf_counter() - started, 6),
    }, indent=2))


if __name__ == "__main__":
    if len(sys.argv) < 8:
        raise SystemExit(
            "usage: real_bookend_check.py FROZEN_FULL FROZEN_STREAM CURRENT MODEL "
            "STEPS BUDGET CHUNK..."
        )
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4],
         int(sys.argv[5]), int(sys.argv[6]), [int(value) for value in sys.argv[7:]])
