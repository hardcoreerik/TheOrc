# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import json
import subprocess
import sys
import time
from pathlib import Path

import numpy as np
import torch
import transformers
from transformers import AutoModelForCausalLM


ABS_TOL = 1e-3
REL_TOL = 1e-3


def main(executable: str, gguf_path: str, source_dir: str, steps: int,
         extra_args: list[str] | None = None) -> None:
    if steps <= 0:
        raise ValueError("steps must be positive")
    initial = [1, 5]
    cpp = json.loads(subprocess.check_output(
        [executable, gguf_path, *map(str, initial), "--steps", str(steps),
         *(extra_args or [])], text=True
    ))
    if len(cpp["steps"]) != steps:
        raise AssertionError(f"C++ returned {len(cpp['steps'])} steps, expected {steps}")

    started = time.perf_counter()
    model = AutoModelForCausalLM.from_pretrained(
        Path(source_dir), dtype=torch.float32, local_files_only=True, attn_implementation="eager"
    )
    model.eval()
    load_seconds = time.perf_counter() - started

    tokens = initial.copy()
    generated = initial.copy()
    max_abs = 0.0
    max_rel = 0.0
    forward_seconds = 0.0
    with torch.inference_mode():
        for index, cpp_step in enumerate(cpp["steps"]):
            if cpp_step["tokens"] != tokens:
                raise AssertionError(f"step {index}: C++ token history is not the requested history")
            started = time.perf_counter()
            output = model(input_ids=torch.tensor([tokens], dtype=torch.long), use_cache=False)
            forward_seconds += time.perf_counter() - started
            expected = output.logits[0, -1].to(dtype=torch.float32).cpu().numpy()
            actual = np.asarray(cpp_step["logits_last"], dtype=np.float32)
            absolute = np.abs(actual - expected)
            relative = absolute / np.maximum(1.0, np.abs(expected))
            if not np.isfinite(actual).all() or not np.all((absolute <= ABS_TOL) | (relative <= REL_TOL)):
                raise AssertionError(
                    f"step {index}: logits exceed gate max_abs={absolute.max()} max_rel={relative.max()}"
                )
            selected = int(np.argmax(expected))
            if cpp_step["selected"] != selected:
                raise AssertionError(
                    f"step {index}: C++ selected {cpp_step['selected']} but Hugging Face selected {selected}"
                )
            max_abs = max(max_abs, float(absolute.max(initial=0.0)))
            max_rel = max(max_rel, float(relative.max(initial=0.0)))
            tokens.append(selected)
            generated.append(selected)

    print(
        "HF/PYTORCH END-TO-END PASS: "
        f"torch={torch.__version__} transformers={transformers.__version__} steps={steps} "
        f"sequence={generated} max_abs={max_abs:.9g} max_rel={max_rel:.9g} "
        f"hf_load_s={load_seconds:.3f} hf_forward_s={forward_seconds:.3f}"
    )


if __name__ == "__main__":
    if len(sys.argv) < 5:
        raise SystemExit("usage: hf_pytorch_forward_check.py FORWARD_EXE MODEL.gguf HF_SOURCE_DIR STEPS [FORWARD_ARGS...]")
    main(sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5:])
