# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

import numpy as np


PHASE0 = Path(__file__).resolve().parents[2] / "OrcEnginePhase0"
sys.path.insert(0, str(PHASE0))

from oracle.model import forward  # noqa: E402
from oracle.real_candidate_logits_check import load_real_weights  # noqa: E402


ABS_TOL = 1e-3
REL_TOL = 1e-3
TAP_NAMES = {
    "input_embedding": lambda result: result.taps["input_embedding"],
    "layer0.pre_attention_normalized_state": lambda result: result.taps["layer_0"]["pre_attention_normalized_state"],
    "layer0.q_projection": lambda result: result.taps["layer_0"]["q_projection"],
    "layer0.k_projection": lambda result: result.taps["layer_0"]["k_projection"],
    "layer0.v_projection": lambda result: result.taps["layer_0"]["v_projection"],
    "layer0.attention_output_after_projection": lambda result: result.taps["layer_0"]["attention_output_after_projection"],
    "layer0.down_projection": lambda result: result.taps["layer_0"]["down_projection"],
    "final_normalized_state": lambda result: result.taps["final_normalized_state"],
}


def errors(actual: np.ndarray, expected: np.ndarray) -> tuple[float, float]:
    actual = np.asarray(actual, dtype=np.float32)
    expected = np.asarray(expected, dtype=np.float32)
    assert actual.shape == expected.shape, (actual.shape, expected.shape)
    absolute = np.abs(actual - expected)
    relative = absolute / np.maximum(1.0, np.abs(expected))
    return float(absolute.max(initial=0.0)), float(relative.max(initial=0.0))


def check_close(label: str, actual: np.ndarray, expected: np.ndarray,
                failures: list[str]) -> tuple[float, float]:
    actual_array = np.asarray(actual, dtype=np.float32)
    expected_array = np.asarray(expected, dtype=np.float32)
    max_abs, max_rel = errors(actual_array, expected_array)
    absolute = np.abs(actual_array - expected_array)
    relative = absolute / np.maximum(1.0, np.abs(expected_array))
    passed = bool(
        np.isfinite(actual_array).all()
        and np.isfinite(expected_array).all()
        and np.all((absolute <= ABS_TOL) | (relative <= REL_TOL))
    )
    print(f"  {label}: max_abs={max_abs:.6g} max_rel={max_rel:.6g} pass={passed}")
    if not passed:
        failures.append(f"{label}: max_abs={max_abs} max_rel={max_rel}")
    return max_abs, max_rel


def main(executable: str, gguf_path: str, steps: int, hf_source_dir: str | None = None) -> None:
    if steps <= 0:
        raise ValueError("steps must be positive")
    initial = [1, 5]
    command = [executable, gguf_path, *map(str, initial), "--steps", str(steps)]
    cpp = json.loads(subprocess.check_output(command, text=True))
    if len(cpp["steps"]) != steps:
        raise AssertionError(f"C++ returned {len(cpp['steps'])} steps, expected {steps}")
    weights, config = load_real_weights(hf_source_dir)

    tokens = initial.copy()
    generated = initial.copy()
    global_abs = 0.0
    global_rel = 0.0
    python_seconds = 0.0
    failures: list[str] = []
    for index, cpp_step in enumerate(cpp["steps"]):
        assert cpp_step["tokens"] == tokens
        started = time.perf_counter()
        result = forward(np.asarray(tokens, dtype=np.int64), weights, config, capture_taps=index == 0)
        python_seconds += time.perf_counter() - started
        expected_logits = result.logits[-1]
        expected_selected = int(np.argmax(expected_logits))
        if cpp_step["selected"] != expected_selected:
            raise AssertionError(
                f"step {index}: C++ selected {cpp_step['selected']} but Python selected {expected_selected}"
            )
        max_abs, max_rel = check_close(
            f"step{index}.logits", np.asarray(cpp_step["logits_last"]), expected_logits, failures
        )
        global_abs = max(global_abs, max_abs)
        global_rel = max(global_rel, max_rel)
        if index == 0:
            for name, getter in TAP_NAMES.items():
                tap = cpp_step["taps"][name]
                expected = np.asarray(getter(result), dtype=np.float32)
                assert tap["dims"] == list(expected.shape)
                max_abs, max_rel = check_close(
                    name, np.asarray(tap["data"]), expected.reshape(-1), failures
                )
                global_abs = max(global_abs, max_abs)
                global_rel = max(global_rel, max_rel)
        tokens.append(expected_selected)
        generated.append(expected_selected)

    print(
        "REAL FORWARD PASS: "
        f"steps={steps} sequence={generated} max_abs={global_abs:.6g} max_rel={global_rel:.6g} "
        f"cpp_materialize_ms={cpp['materialize_milliseconds']:.3f} "
        f"cpp_forward_ms={[round(step['forward_milliseconds'], 3) for step in cpp['steps']]} "
        f"python_forward_s={python_seconds:.3f}"
    )
    if failures:
        raise AssertionError("real differential failures:\n" + "\n".join(failures))


if __name__ == "__main__":
    if len(sys.argv) not in (4, 5):
        raise SystemExit(
            "usage: real_forward_check.py FORWARD_EXE MODEL.gguf STEPS [HF_SOURCE_DIR]"
        )
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]),
         sys.argv[4] if len(sys.argv) == 5 else None)
