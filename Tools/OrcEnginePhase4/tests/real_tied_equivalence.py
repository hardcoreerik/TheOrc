# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys


def run(executable: str, model: str, steps: int, chunk: int) -> dict:
    return json.loads(subprocess.check_output([
        executable, model, "1", "5", "--steps", str(steps),
        "--virtualize-bookends", "--output-chunk-rows", str(chunk),
    ], text=True))


def outputs(document: dict) -> list[tuple]:
    return [
        (step["tokens"], step["selected"], step["logits_last"], step.get("taps"))
        for step in document["steps"]
    ]


if __name__ == "__main__":
    if len(sys.argv) != 6:
        raise SystemExit(
            "usage: real_tied_equivalence.py EXE EXPLICIT.gguf TIED.gguf STEPS CHUNK"
        )
    explicit = run(sys.argv[1], sys.argv[2], int(sys.argv[4]), int(sys.argv[5]))
    tied = run(sys.argv[1], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]))
    if outputs(explicit) != outputs(tied):
        raise AssertionError("real tied and explicit bookend executions differ")
    print(
        "REAL TIED BOOKEND PASS: "
        f"steps={sys.argv[4]} chunk={sys.argv[5]} "
        f"sequence={[1, 5] + [step['selected'] for step in tied['steps']]} "
        f"peak={tied['telemetry']['peak_resident_weight_bytes']} "
        f"embedding_bytes={tied['telemetry']['embedding_backing_bytes_read']} "
        f"output_bytes={tied['telemetry']['output_backing_bytes_read']}"
    )
