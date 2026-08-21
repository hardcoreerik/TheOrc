# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys


def run(executable: str, model: str, steps: int, *extra: str) -> dict:
    return json.loads(subprocess.check_output(
        [executable, model, "1", "5", "--steps", str(steps), *extra], text=True
    ))


def main(full_exe: str, streamed_exe: str, model: str, steps: int, predicted_peak: int) -> None:
    full = run(full_exe, model, steps)
    streamed = run(streamed_exe, model, steps)
    reverse = run(streamed_exe, model, 1, "--reverse-layer-materialization") if steps > 1 else None
    def outputs(document: dict) -> list[tuple]:
        return [
            (step["tokens"], step["selected"], step["logits_last"], step.get("taps"))
            for step in document["steps"]
        ]

    if outputs(full) != outputs(streamed) or (
        reverse is not None and outputs(streamed)[:1] != outputs(reverse)
    ):
        raise AssertionError("full, streamed, and reverse-order outputs are not bit-identical")
    telemetry = streamed["telemetry"]
    if telemetry["peak_active_layers"] != 1 or telemetry["current_layer"] != -1:
        raise AssertionError("streaming violated the one-layer lifetime invariant")
    if telemetry["peak_resident_weight_bytes"] != predicted_peak:
        raise AssertionError(
            f"predicted peak {predicted_peak}, measured {telemetry['peak_resident_weight_bytes']}"
        )
    if steps > 1 and telemetry["repeated_backing_bytes_read"] <= 0:
        raise AssertionError("multi-step execution did not report repeated backing reads")
    ratio = telemetry["peak_resident_weight_bytes"] / full["materialized_bytes"]
    print(
        "REAL STREAMING PASS: "
        f"steps={steps} sequence={[1, 5] + [step['selected'] for step in streamed['steps']]} "
        f"full_bytes={full['materialized_bytes']} peak_bytes={telemetry['peak_resident_weight_bytes']} "
        f"peak_ratio={ratio:.6%} cumulative_bytes={telemetry['cumulative_materialized_bytes']} "
        f"backing_bytes={telemetry['backing_bytes_read']} repeated_bytes={telemetry['repeated_backing_bytes_read']} "
        f"reads={telemetry['read_count']} materializations={telemetry['materialization_count']} "
        f"releases={telemetry['release_count']} process_ws={telemetry['peak_process_working_set_bytes']}"
    )


if __name__ == "__main__":
    if len(sys.argv) != 6:
        raise SystemExit("usage: real_streaming_check.py FULL_EXE STREAM_EXE MODEL STEPS PREDICTED_PEAK")
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]))
