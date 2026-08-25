# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Gate 2 follow-up (Codex review finding): the original Gate 2
writeup checked Q/K specifically (at layers 0/11/28) and asserted "only
the GGUF's Q/K tensor layout changed" between the existing-custom and
canonical GGUFs, without exhaustively checking every OTHER shared
tensor. This closes that gap: compares ALL shared tensors between the
two files, permuting Q/K before comparison (the already-proven
transform) and comparing everything else raw.
"""
from gguf import GGUFReader
import numpy as np
import sys

CANON = ".orc/gate2-canonical-diagnostic/smollm2-135m-canonical-f32.gguf"
CUSTOM = "F:/Ai/OrchestratorIDE-phase2-gguf/Tools/OrcEnginePhase0/artifacts/smollm2-135m.gguf"


def get(reader, name):
    for t in reader.tensors:
        if t.name == name:
            return t.data
    return None


def permute(weights, n_head, n_head_kv):
    if n_head_kv is not None and n_head != n_head_kv:
        n_head = n_head_kv
    w = weights.reshape(n_head, 2, weights.shape[0] // n_head // 2, *weights.shape[1:])
    w = w.swapaxes(1, 2)
    return w.reshape(weights.shape)


def main():
    canon = GGUFReader(CANON)
    custom = GGUFReader(CUSTOM)
    n_head, n_head_kv = 9, 3

    canon_names = {t.name for t in canon.tensors}
    custom_names = {t.name for t in custom.tensors}
    only_custom = sorted(custom_names - canon_names)
    only_canon = sorted(canon_names - custom_names)
    print(f"tensors only in custom GGUF: {only_custom}")
    print(f"tensors only in canonical GGUF: {only_canon}")

    mismatches = []
    checked = 0
    for name in sorted(canon_names & custom_names):
        c, u = get(canon, name), get(custom, name)
        if c.shape != u.shape:
            mismatches.append((name, "SHAPE", c.shape, u.shape))
            continue
        if name.endswith("attn_q.weight"):
            ok = np.array_equal(permute(u.copy(), n_head, n_head), c)
        elif name.endswith("attn_k.weight"):
            ok = np.array_equal(permute(u.copy(), n_head, n_head_kv), c)
        else:
            ok = np.array_equal(c, u)
        checked += 1
        if not ok:
            maxdiff = float(np.max(np.abs(c.astype(np.float64) - u.astype(np.float64))))
            mismatches.append((name, "VALUE", maxdiff))

    print(f"checked {checked} shared tensors (Q/K compared post-permutation, everything else raw)")
    print(f"mismatches beyond Q/K: {len(mismatches)}")
    for m in mismatches:
        print(" ", m)

    ok = len(mismatches) == 0 and only_custom == ["output.weight"] and only_canon == []
    print(f"\n{'PASS' if ok else 'FAIL'}: no unexplained tensor differences beyond Q/K permutation "
          f"and the already-accounted-for output.weight/token_embd.weight tie")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
