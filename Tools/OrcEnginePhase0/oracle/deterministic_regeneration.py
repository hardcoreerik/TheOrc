# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
deterministic_regeneration acceptance check, per PHASE_0_ACCEPTANCE.yaml:
  "two clean regenerations produce identical non-floating identities and
  approved floating comparisons."

Runs oracle/regen_artifact.py as TWO separate `python3 -m` subprocesses --
each a genuinely fresh interpreter/process, not a second in-process call
(same-process determinism was already checked, more weakly, in
fixture_b.py). Compares non-floating identities for exact equality and
floating arrays for both bit-exact hash equality and a numeric tolerance
comparison, since the two are different claims.
"""
from __future__ import annotations

import json
import subprocess
import sys

ATOL = 1e-6
RTOL = 1e-5


def _run_once() -> dict:
    proc = subprocess.run(
        [sys.executable, "-m", "oracle.regen_artifact"],
        capture_output=True, text=True, check=True,
    )
    return json.loads(proc.stdout)


def _max_abs_diff(a: list, b: list) -> float:
    import numpy as np
    return float(np.abs(np.array(a, dtype=np.float64) - np.array(b, dtype=np.float64)).max())


def run() -> bool:
    run1 = _run_once()
    run2 = _run_once()

    nf1, nf2 = run1["non_floating_identities"], run2["non_floating_identities"]
    non_floating_match = nf1 == nf2
    print(f"non-floating identities identical across two clean processes: {non_floating_match}")
    if not non_floating_match:
        for key in nf1:
            if nf1[key] != nf2[key]:
                print(f"  MISMATCH at '{key}': run1={nf1[key]!r} run2={nf2[key]!r}")

    fl1, fl2 = run1["floating"], run2["floating"]
    hash_match = fl1["logits_sha256"] == fl2["logits_sha256"]
    logits_diff = _max_abs_diff(fl1["logits_values"], fl2["logits_values"])
    tolerance_match = logits_diff <= ATOL
    print(f"floating logits bit-exact hash match: {hash_match} "
          f"(sha256: {fl1['logits_sha256'][:12]}... vs {fl2['logits_sha256'][:12]}...)")
    print(f"floating logits numeric max_abs_diff={logits_diff:.3e} "
          f"(within approved atol={ATOL}: {tolerance_match})")

    ffn_hash_match = fl1["layer0_post_ffn_residual_sha256"] == fl2["layer0_post_ffn_residual_sha256"]
    print(f"layer-0 post-FFN-residual hash match: {ffn_hash_match}")

    return non_floating_match and hash_match and tolerance_match and ffn_hash_match


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: deterministic_regeneration")
    raise SystemExit(0 if ok else 1)
