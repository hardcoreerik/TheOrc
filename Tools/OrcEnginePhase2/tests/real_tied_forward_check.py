# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys


def run_json(command: list[str]) -> dict:
    return json.loads(subprocess.check_output(command, text=True))


def main(inspector: str, forward: str, explicit_path: str, tied_path: str, steps: int) -> None:
    if steps <= 0:
        raise ValueError("steps must be positive")
    explicit_manifest = run_json([inspector, "--json", explicit_path])
    tied_manifest = run_json([inspector, "--json", tied_path])
    if explicit_manifest["tied_embeddings"]:
        raise AssertionError("explicit output.weight artifact was classified tied")
    if not tied_manifest["tied_embeddings"]:
        raise AssertionError("absent output.weight artifact was not classified tied")
    if explicit_manifest["mapped_tensor_count"] != tied_manifest["mapped_tensor_count"] + 1:
        raise AssertionError("tied artifact did not remove exactly one mapped output tensor")

    initial = [1, 5]
    explicit = run_json([forward, explicit_path, *map(str, initial), "--steps", str(steps)])
    tied = run_json([forward, tied_path, *map(str, initial), "--steps", str(steps)])
    if len(explicit["steps"]) != steps or len(tied["steps"]) != steps:
        raise AssertionError(
            f"decode step count mismatch: explicit={len(explicit['steps'])} "
            f"tied={len(tied['steps'])} expected={steps}"
        )
    for index, (explicit_step, tied_step) in enumerate(zip(explicit["steps"], tied["steps"])):
        if explicit_step["tokens"] != tied_step["tokens"]:
            raise AssertionError(f"step {index}: token histories differ")
        if explicit_step["selected"] != tied_step["selected"]:
            raise AssertionError(f"step {index}: selected tokens differ")
        if explicit_step["logits_last"] != tied_step["logits_last"]:
            raise AssertionError(f"step {index}: logits are not bit-identical")
        if index == 0 and explicit_step["taps"] != tied_step["taps"]:
            raise AssertionError("first-step intermediate taps are not bit-identical")

    sequence = initial + [step["selected"] for step in explicit["steps"]]
    print(
        "REAL TIED OUTPUT PASS: "
        f"steps={steps} sequence={sequence} logits=bit-identical taps=bit-identical "
        f"explicit_mapped={explicit_manifest['mapped_tensor_count']} "
        f"tied_mapped={tied_manifest['mapped_tensor_count']}"
    )


if __name__ == "__main__":
    if len(sys.argv) != 6:
        raise SystemExit(
            "usage: real_tied_forward_check.py INSPECTOR FORWARD EXPLICIT.gguf TIED.gguf STEPS"
        )
    main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], int(sys.argv[5]))
