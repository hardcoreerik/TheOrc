# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
from __future__ import annotations

import hashlib
import subprocess
import sys


def run(executable: str, fixtures: str) -> bytes:
    return subprocess.check_output([executable, fixtures])


def main(frozen: str, current: str, fixtures: str) -> None:
    frozen_output = run(frozen, fixtures)
    current_output = run(current, fixtures)
    if frozen_output != current_output:
        raise AssertionError("frozen Phase-1 and current refactored full-resident outputs differ")
    digest = hashlib.sha256(current_output).hexdigest().upper()
    print(f"CROSS-FREEZE PASS: bytes={len(current_output)} sha256={digest}")


if __name__ == "__main__":
    if len(sys.argv) != 4:
        raise SystemExit("usage: cross_freeze_check.py FROZEN_EXE CURRENT_EXE FIXTURES")
    main(sys.argv[1], sys.argv[2], sys.argv[3])
