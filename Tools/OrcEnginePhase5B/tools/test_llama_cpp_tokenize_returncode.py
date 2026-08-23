#!/usr/bin/env python3
# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
#
# Regression test for the fail-open bug an independent review (Codex,
# 2026-08-22) found in three_way_tokenizer_comparison.py's
# llama_cpp_tokenize(): it parsed stdout for a bracketed ID list without
# first checking proc.returncode, so a llama-tokenize invocation that
# exited non-zero but happened to print a parseable "[...]" line on
# stdout (e.g. a stray leftover from a partial/crashed run) would be
# silently accepted as real oracle output. Fixed by checking
# proc.returncode != 0 before parsing. This test proves the fix by
# mocking subprocess.run to reproduce exactly that fake-oracle shape
# (non-zero exit, parseable stdout) and confirming llama_cpp_tokenize
# now raises instead of returning the bogus IDs.
#
# Standalone, no pytest dependency (matches this tool's own zero-extra-
# dependency style) -- run directly: python test_llama_cpp_tokenize_returncode.py
import sys
import unittest
from unittest.mock import patch, MagicMock

import three_way_tokenizer_comparison as driver


class LlamaCppTokenizeReturnCodeTest(unittest.TestCase):
    def test_nonzero_exit_with_parseable_stdout_is_rejected(self):
        fake_proc = MagicMock()
        fake_proc.returncode = 1
        fake_proc.stdout = b"garbage before\n[1, 2, 3]\n"
        fake_proc.stderr = b"llama-tokenize: fatal error, model load failed\n"
        with patch.object(driver.subprocess, "run", return_value=fake_proc):
            with self.assertRaises(RuntimeError) as ctx:
                driver.llama_cpp_tokenize("irrelevant text")
        self.assertIn("exited 1", str(ctx.exception))

    def test_zero_exit_with_parseable_stdout_still_succeeds(self):
        fake_proc = MagicMock()
        fake_proc.returncode = 0
        fake_proc.stdout = b"some log line\n[4, 5, 6]\n"
        fake_proc.stderr = b""
        with patch.object(driver.subprocess, "run", return_value=fake_proc):
            ids = driver.llama_cpp_tokenize("irrelevant text")
        self.assertEqual(ids, [4, 5, 6])


if __name__ == "__main__":
    result = unittest.main(exit=False)
    sys.exit(0 if result.result.wasSuccessful() else 1)
