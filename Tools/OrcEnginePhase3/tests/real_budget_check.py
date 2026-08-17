# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys


def invoke(executable: str, model: str, budget: int, *extra: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        [executable, model, "1", "5", "--steps", "1", "--budget-bytes", str(budget), *extra],
        text=True,
        capture_output=True,
    )


def main(full_executable: str, executable: str, model: str, budget: int) -> None:
    trusted = subprocess.run(
        [full_executable, model, "1", "5", "--steps", "1"],
        text=True, capture_output=True, check=True,
    )
    full = invoke(executable, model, budget, "--require-full-resident")
    if full.returncode == 0 or "residency budget" not in full.stderr:
        raise AssertionError("full-resident admission did not fail cleanly under the budget")

    streamed = invoke(executable, model, budget)
    if streamed.returncode != 0:
        raise AssertionError(f"streamed execution failed at its measured peak: {streamed.stderr}")
    result = json.loads(streamed.stdout)
    trusted_result = json.loads(trusted.stdout)
    telemetry = result["telemetry"]
    if result["full_resident_admitted"]:
        raise AssertionError("budget unexpectedly admits full residency")
    if telemetry["peak_resident_weight_bytes"] != budget:
        raise AssertionError("streamed peak did not equal the chosen measured budget")
    expected = trusted_result["steps"][0]
    actual = result["steps"][0]
    if (expected["selected"], expected["logits_last"], expected["taps"]) != (
        actual["selected"], actual["logits_last"], actual["taps"]
    ):
        raise AssertionError("budgeted streamed output differs from trusted full-resident output")

    below = invoke(executable, model, budget - 1)
    if below.returncode == 0 or "residency budget" not in below.stderr:
        raise AssertionError("one byte below streamed peak did not fail closed")

    print(
        "REAL BUDGET PASS: "
        f"budget={budget} full_required={result['full_resident_required_bytes']} "
        f"streamed_peak={telemetry['peak_resident_weight_bytes']} selected={result['steps'][0]['selected']} "
        f"below_peak_failure={below.stderr.strip()}"
    )


if __name__ == "__main__":
    if len(sys.argv) != 5:
        raise SystemExit("usage: real_budget_check.py FULL_EXE STREAM_EXE MODEL BUDGET")
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]))
