# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import re
import shutil
import struct
import subprocess
import sys
import tempfile
from pathlib import Path


def run(executable: str, model: Path) -> dict:
    return json.loads(subprocess.check_output([
        executable, str(model), "1", "5", "--steps", "1",
        "--virtualize-bookends", "--output-chunk-rows", "1000",
    ], text=True))


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("usage: corrupt_f32_bookend_check.py INSPECT_EXE FORWARD_EXE MODEL.gguf")
    source = Path(sys.argv[3])
    inspection = subprocess.check_output([sys.argv[1], str(source)], text=True)
    match = re.search(r"output\.weight dims=\[(\d+),(\d+)\].* offset=(\d+)", inspection)
    if not match:
        raise AssertionError("explicit F32 output.weight was not found")
    hidden, _, offset = map(int, match.groups())
    baseline = run(sys.argv[2], source)
    with tempfile.TemporaryDirectory(prefix="orcengine-p4-corrupt-") as directory:
        mutated = Path(directory) / source.name
        shutil.copyfile(source, mutated)
        selected_row = baseline["steps"][0]["selected"]
        with mutated.open("r+b") as stream:
            stream.seek(offset + selected_row * hidden * 4)
            stream.write(struct.pack("<f", 1000.0))
        changed = run(sys.argv[2], mutated)
    before = baseline["steps"][0]
    after = changed["steps"][0]
    if (before["selected"], before["logits_last"]) == (
        after["selected"], after["logits_last"]
    ):
        raise AssertionError("corrupted output row escaped exact differential detection")
    print(
        "CORRUPTED F32 BOOKEND DETECTED: "
        f"row={selected_row} baseline_selected={before['selected']} "
        f"mutated_selected={after['selected']}"
    )
