# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 6 Stage 1, Checkpoint 3 (Codex remediation, Stage 2): OrcEngine-
Q8_0-vs-pinned-llama.cpp-Q8_0 oracle leg.

This is a REWRITE of the original driver, which had a mathematically
invalid comparison (see Finding 1 below) and several unverified-
identity gaps a Codex review found. This version:

  1. Uses the FULL-VOCABULARY logsumexp phase6_q8_0_comparison.exe now
     emits per prompt (evidence schema_version >= 2) to compute an
     EXACT orc_logprob = raw_orc_logit - full_vocab_logsumexp, instead
     of the earlier code's invalid approach of renormalizing only
     OrcEngine's own top-5 logits (a different, smaller-domain softmax)
     and comparing that to llama.cpp's full-vocabulary log-probability.
     Still only computable for tokens llama.cpp's server itself reports
     that ALSO appear in OrcEngine's own top-5 slice (the raw logit for
     an arbitrary vocabulary token is not otherwise in the evidence) --
     reported as "N/A" rather than approximated for anything else.
  2. Verifies llama-server.exe's pinned build/commit via --version,
     records and checks its SHA-256 (and llama-server-impl.dll's, where
     the actual request logic lives) against the authority now recorded
     in fixtures/Q8_0_FIXTURE_PROVENANCE.md -- this is the FIRST time
     this specific executable was hash-pinned; earlier proof relied only
     on llama-tokenize.exe's --version and a shared llama.dll hash.
  3. Verifies the Q8_0 GGUF's SHA-256 against the value BOTH this
     project's provenance record AND the evidence file itself carry.
  4. Binds a dynamically-chosen free local port (not a fixed constant),
     confirms the launched subprocess is still alive before treating a
     health response as authoritative, and only ever terminates the
     specific subprocess handle this script started.
  5. Requests tokenization of each prompt from llama-server's own
     /tokenize endpoint and asserts the resulting token ID sequence is
     EXACTLY equal to the token IDs OrcEngine's evidence recorded for
     that same prompt -- a matching text string is not proof of
     identical model input; only matching token IDs are.
  6. Does NOT assert an unjustified external-oracle log-probability
     tolerance. No prior empirically-derived Q8-vs-Q8 numerical floor
     exists anywhere in this project (the only prior cross-engine
     tolerance, LOGPROB_ATOL=0.1 in llama_cpp_deployment_oracle.py, was
     derived for a completely different, synthetic 2-layer/16-hidden
     toy model and an F32-vs-F32 comparison -- not an applicable floor
     for a real 30-layer/576-hidden Q8_0-vs-Q8_0 comparison). Per this
     remediation's explicit instruction not to pick a threshold after
     seeing results, this script reports log-probability agreement as
     DIAGNOSTIC evidence only. The PASS/FAIL gate is restricted to two
     claims that need no numeric tolerance to justify: (a) identical
     input token IDs, and (b) identical greedy (argmax) token. Deriving
     a legitimate log-probability tolerance is explicitly deferred, not
     fabricated here.

Requires (fails closed, does not silently skip):
    - ORC_LLAMA_SERVER_PATH set, pointing at llama-server.exe from the
      pinned b10436/commit 6fed9f6ff build
    - the locally-quantized Q8_0 GGUF (see fixtures/Q8_0_FIXTURE_PROVENANCE.md)
    - phase6_q8_0_comparison.exe's schema_version>=2 JSON-lines evidence
      file, already generated (this script does not regenerate it)

Usage:
    python phase6_llama_cpp_q8_0_oracle.py <q8_0.gguf> <evidence.jsonl> <output_report.jsonl>
"""
from __future__ import annotations

import hashlib
import json
import os
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request

LLAMA_SERVER_PATH = os.environ.get("ORC_LLAMA_SERVER_PATH", "")

EXPECTED_BUILD_MARKER = "build 10436"
EXPECTED_COMMIT_MARKER = "6fed9f6ff"

# First pinned during this remediation pass (see Q8_0_FIXTURE_PROVENANCE.md
# "Independent proof" section) -- no prior authority record existed for
# llama-server.exe/llama-server-impl.dll before this.
EXPECTED_SERVER_EXE_SHA256 = "ae159e001d959e7a773af61e24d8e7d5d4de565865ec12a0dfd9974dc8ab7ca1"
EXPECTED_SERVER_IMPL_DLL_SHA256 = "77f8cf124d0222993f7e98c16cecc855bc7b0d31f8005155d75f141944846793"

# Matches Q8_0_FIXTURE_PROVENANCE.md's "Output" section and the constant
# hardcoded in phase6_q8_0_comparison.cpp (kQ8ExpectedSha256).
EXPECTED_Q8_GGUF_SHA256 = "3aed955db7e8e7e73e12a05964ad9efb79cef77a895120a77475d7743609d398"

# Matches Q8_0_FIXTURE_PROVENANCE.md's "Input" section and the constant
# hardcoded in phase6_q8_0_comparison.cpp (kF32ExpectedSha256).
EXPECTED_F32_GGUF_SHA256 = "fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac"

MIN_SCHEMA_VERSION = 2


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()


def _verify_server_identity(server_path: str) -> None:
    """Fails closed (sys.exit) unless llama-server.exe is the pinned
    b10436/6fed9f6ff build, by hash AND by its own --version banner."""
    if not os.path.isfile(server_path):
        sys.exit(f"ABORT: llama-server not found at {server_path!r}. "
                 f"Set ORC_LLAMA_SERVER_PATH to the pinned b10436 build's llama-server.exe.")

    actual_hash = _sha256_file(server_path)
    if actual_hash != EXPECTED_SERVER_EXE_SHA256:
        sys.exit(f"ABORT: llama-server.exe SHA-256 {actual_hash} does not match the pinned "
                 f"authority {EXPECTED_SERVER_EXE_SHA256} recorded in "
                 f"fixtures/Q8_0_FIXTURE_PROVENANCE.md -- refusing to trust an unverified binary.")

    # Codex remediation Gate 3, item 3: for this pinned build layout, the
    # implementation DLL is documented (Q8_0_FIXTURE_PROVENANCE.md) as
    # itself hash-pinned -- its absence must ABORT, not downgrade to a
    # warning, because a missing impl DLL means the actual request-
    # handling logic was never verified at all (the .exe is a thin stub).
    impl_dll = os.path.join(os.path.dirname(server_path), "llama-server-impl.dll")
    if not os.path.isfile(impl_dll):
        sys.exit(f"ABORT: llama-server-impl.dll not found alongside {server_path!r} -- this pinned "
                 f"build layout requires it (it is where llama-server's actual request-handling logic "
                 f"lives, not the .exe stub, and it is itself hash-pinned in "
                 f"Q8_0_FIXTURE_PROVENANCE.md). Refusing to proceed with only the .exe stub verified.")
    impl_hash = _sha256_file(impl_dll)
    if impl_hash != EXPECTED_SERVER_IMPL_DLL_SHA256:
        sys.exit(f"ABORT: llama-server-impl.dll SHA-256 {impl_hash} does not match the pinned "
                 f"authority {EXPECTED_SERVER_IMPL_DLL_SHA256} -- refusing to trust an unverified "
                 f"implementation library.")

    try:
        result = subprocess.run([server_path, "--version"], capture_output=True, text=True, timeout=15)
    except subprocess.SubprocessError as ex:
        sys.exit(f"ABORT: could not run 'llama-server.exe --version': {ex}")
    # Codex remediation Gate 3, item 4: check the return code BEFORE
    # trusting the banner text -- a nonzero exit could print a partial or
    # misleading banner on stderr/stdout while still not being the real,
    # successfully-running pinned binary.
    if result.returncode != 0:
        sys.exit(f"ABORT: 'llama-server.exe --version' exited with nonzero code {result.returncode} -- "
                 f"stdout={result.stdout!r} stderr={result.stderr!r}")
    banner = (result.stdout or "") + (result.stderr or "")
    if EXPECTED_BUILD_MARKER not in banner or EXPECTED_COMMIT_MARKER not in banner:
        sys.exit(f"ABORT: llama-server.exe --version banner does not confirm the pinned build:\n{banner}\n"
                 f"expected markers {EXPECTED_BUILD_MARKER!r} and {EXPECTED_COMMIT_MARKER!r}")
    print(f"Server identity verified: hash-pinned AND --version confirms {EXPECTED_BUILD_MARKER}/"
          f"{EXPECTED_COMMIT_MARKER}")


def _verify_gguf_identity(gguf_path: str, expected_hash: str, evidence_field: str,
                          evidence_entries: list[dict], label: str) -> str:
    """Shared, precision-independent GGUF identity check: the file's own
    SHA-256 must match the pinned authority, AND every evidence entry that
    records a hash for this artifact (via `evidence_field`) must also
    match -- catching the case where evidence was generated against a
    DIFFERENT file than the one this run is now comparing against. Used
    for both the Q8_0 and F32 legs (Gate 3 requires the F32 leg meet the
    same identity standard the Q8 oracle already did)."""
    if not os.path.isfile(gguf_path):
        sys.exit(f"ABORT: {label} GGUF not found at {gguf_path!r}")
    actual_hash = _sha256_file(gguf_path)
    if actual_hash != expected_hash:
        sys.exit(f"ABORT: {label} GGUF SHA-256 {actual_hash} does not match the pinned authority "
                 f"{expected_hash} -- wrong or corrupted fixture.")
    # Codex remediation round 3, Gate 3: EVERY evidence entry must carry
    # its required artifact hash -- a MISSING field (or a present but
    # null/empty one) was previously silently accepted, which defeats the
    # purpose of cross-checking evidence against a pinned artifact for
    # entries that simply omit the field.
    for entry in evidence_entries:
        if evidence_field not in entry:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} is missing required field "
                     f"{evidence_field!r} -- cannot verify it was generated against the pinned "
                     f"{label} artifact.")
        evidence_hash = entry[evidence_field]
        if not evidence_hash:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} has a null/empty {evidence_field}.")
        if evidence_hash != expected_hash:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} records {evidence_field}="
                     f"{evidence_hash!r}, which does not match the pinned authority {expected_hash} -- "
                     f"evidence was generated against a different {label} file than this run is using.")
    print(f"{label} GGUF identity verified: {actual_hash} (matches pinned authority and evidence records)")
    return actual_hash


def _verify_q8_gguf_identity(q8_path: str, evidence_entries: list[dict]) -> None:
    _verify_gguf_identity(q8_path, EXPECTED_Q8_GGUF_SHA256, "q8_artifact_sha256", evidence_entries, "Q8_0")


def _verify_f32_gguf_identity(f32_path: str, evidence_entries: list[dict]) -> None:
    _verify_gguf_identity(f32_path, EXPECTED_F32_GGUF_SHA256, "f32_artifact_sha256", evidence_entries, "F32")


def _free_local_port() -> int:
    """Binds an ephemeral port and immediately releases it, so the server
    process picks it up cleanly -- avoids a hardcoded fixed port silently
    connecting to a stale/unrelated pre-existing server on that port."""
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


def _wait_for_health(proc: subprocess.Popen, port: int, timeout_s: float = 60.0) -> None:
    """Fails closed (sys.exit) if the launched process exits before
    becoming healthy, or times out -- never silently falls through to
    treating an unrelated already-healthy server as this run's server."""
    deadline = time.time() + timeout_s
    while time.time() < deadline:
        exit_code = proc.poll()
        if exit_code is not None:
            sys.exit(f"ABORT: llama-server process (pid {proc.pid}) exited with code {exit_code} "
                     f"before becoming healthy -- this run's server process specifically, not some "
                     f"other server, is what failed.")
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=1) as resp:
                if json.loads(resp.read())["status"] == "ok":
                    return
        except (urllib.error.URLError, ConnectionError, OSError):
            pass
        time.sleep(0.3)
    sys.exit(f"ABORT: llama-server (pid {proc.pid}) did not become healthy on port {port} within "
             f"{timeout_s}s")


# Codex remediation Gate 2: the SINGLE canonical neutral-greedy completion
# contract, shared by every leg (Q8 oracle, F32 localizer) that requests a
# one-token completion from the pinned llama-server -- previously the F32
# localizer maintained its own separate, ad hoc payload dict that happened
# to be similar but was never proven identical. temperature=0 alone is NOT
# sufficient to guarantee a pure argmax response: llama-server's /completion
# endpoint applies repeat_penalty/top_k/top_p/min_p/presence_penalty/
# frequency_penalty BEFORE sampling, and several of those are non-neutral
# by default (this build's server-observed default `repeat_penalty` is
# 1.0-ish but NOT guaranteed to be exactly 1.0 across builds/config, and
# `top_k` commonly defaults to a small positive value like 40, which
# would truncate the distribution before argmax selection even at
# temperature=0). Every field below is explicitly set to its neutral/off
# value so this is unambiguously a pure argmax-over-full-vocabulary
# response, not merely "temperature=0 and otherwise whatever the server
# defaults to." Empirically verified the pinned b10436 server accepts
# every one of these fields (a real /completion request with this exact
# payload against a real pinned model returned HTTP 200 with a normal
# completion body -- not a speculative/unverified field list).
NEUTRAL_GREEDY_PARAMS = {
    "temperature": 0,
    "cache_prompt": False,
    "repeat_penalty": 1.0,
    "top_k": 0,
    "top_p": 1.0,
    "min_p": 0.0,
    "presence_penalty": 0.0,
    "frequency_penalty": 0.0,
}


def build_completion_payload(prompt: str, n_probs: int) -> dict:
    """The one canonical request body for a neutral, greedy, one-token
    completion. Both the Q8 and F32 legs MUST call this (not maintain
    their own copies) so a test can prove, by construction, that they are
    identical -- not merely visually similar."""
    return {"prompt": prompt, "n_predict": 1, "n_probs": n_probs, **NEUTRAL_GREEDY_PARAMS}


def _request_completion(port: int, prompt: str, n_probs: int) -> dict:
    payload = json.dumps(build_completion_payload(prompt, n_probs)).encode()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/completion", data=payload,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def _request_tokenize(port: int, prompt: str) -> list[int]:
    """Uses llama-server's own /tokenize endpoint so BOTH engines'
    tokenization of the SAME prompt text is proven identical for THIS
    run, not merely assumed from an earlier, separate proof elsewhere in
    this project. add_special=False: OrcEngine's evidence token_ids do
    not include a BOS token for any of this corpus's prompts (verified
    by inspection -- none of the recorded IDs is llama's BOS id), so the
    comparison must use the same (no-special-token) tokenization mode on
    the llama.cpp side or it would be comparing different input by
    construction."""
    payload = json.dumps({"content": prompt, "add_special": False}).encode()
    req = urllib.request.Request(
        f"http://127.0.0.1:{port}/tokenize", data=payload,
        headers={"Content-Type": "application/json"},
    )
    with urllib.request.urlopen(req, timeout=15) as resp:
        body = json.loads(resp.read())
    return body["tokens"]


def _log_softmax_at(full_vocab_logsumexp: float, ids: list[int], logits: list[float],
                    target_id: int) -> float | None:
    """Exact log-probability of `target_id`, using the FULL-VOCABULARY
    logsumexp the C++ tool already computed (evidence schema_version>=2)
    -- NOT a re-derived top-k-only normalization, which would be a
    smaller, different softmax and mathematically invalid to compare
    against llama.cpp's full-vocabulary log-probability. Only computable
    when `target_id`'s raw OrcEngine logit is present in the evidence
    (i.e. it is one of OrcEngine's own top-5); otherwise returns None,
    reported honestly as not comparable, never approximated."""
    if target_id not in ids:
        return None
    idx = ids.index(target_id)
    return logits[idx] - full_vocab_logsumexp


def _validate_evidence_schema(entries: list[dict]) -> None:
    """Fails closed (sys.exit) on malformed/incomplete evidence: missing
    schema_version, a schema_version too old to carry full-vocabulary
    normalization data, or any required field absent from an entry.
    Extracted as its own function so it is independently testable
    without needing a live server or real evidence file."""
    if not entries:
        sys.exit("ABORT: evidence is empty")
    for entry in entries:
        schema_version = entry.get("schema_version")
        if schema_version is None or schema_version < MIN_SCHEMA_VERSION:
            sys.exit(f"ABORT: evidence entry {entry.get('id')!r} has schema_version={schema_version!r}, "
                     f"required >= {MIN_SCHEMA_VERSION} (full-vocabulary logsumexp normalization data). "
                     f"Regenerate evidence with the corrected phase6_q8_0_comparison.exe.")
        for required_field in ("q8_full_vocab_logsumexp", "token_ids", "q8_top5_ids", "q8_top5_logits",
                               "compared_position", "q8_selected"):
            if required_field not in entry:
                sys.exit(f"ABORT: evidence entry {entry.get('id')!r} is missing required field "
                         f"{required_field!r} -- malformed or incomplete evidence, refusing to proceed.")


def run(q8_path: str, evidence_path: str, report_path: str) -> bool:
    if not os.path.isfile(evidence_path):
        sys.exit(f"ABORT: OrcEngine evidence file not found at {evidence_path!r} -- "
                 f"run phase6_q8_0_comparison.exe first")

    with open(evidence_path, encoding="utf-8") as f:
        orcengine_evidence = [json.loads(line) for line in f if line.strip()]
    _validate_evidence_schema(orcengine_evidence)

    _verify_server_identity(LLAMA_SERVER_PATH)
    _verify_q8_gguf_identity(q8_path, orcengine_evidence)

    port = _free_local_port()
    proc = subprocess.Popen(
        [LLAMA_SERVER_PATH, "-m", q8_path, "--port", str(port), "--no-warmup"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    all_ok = True
    results = []
    try:
        _wait_for_health(proc, port)
        print(f"llama-server healthy on dynamically-chosen port {port} (pid {proc.pid})")

        for entry in orcengine_evidence:
            # --- Token-ID identity proof: a matching text string is not
            # sufficient proof of identical model input. ---
            llama_token_ids = _request_tokenize(port, entry["text"])
            orc_token_ids = entry["token_ids"]
            token_ids_match = llama_token_ids == orc_token_ids
            if not token_ids_match:
                all_ok = False
                print(f"{entry['id']:24s} TOKEN-ID MISMATCH: orc={orc_token_ids} llama.cpp={llama_token_ids}")
                results.append({"id": entry["id"], "token_ids_match": False,
                                "orc_token_ids": orc_token_ids, "llama_token_ids": llama_token_ids})
                continue  # comparing logits under a proven input mismatch would be meaningless

            response = _request_completion(port, entry["text"], n_probs=5)
            top = response["completion_probabilities"][0]["top_logprobs"]
            llama_ids = [t["id"] for t in top]
            llama_logprobs = [t["logprob"] for t in top]
            llama_argmax = llama_ids[0]  # server returns sorted descending

            orc_argmax = entry["q8_selected"]
            argmax_agree = orc_argmax == llama_argmax
            if not argmax_agree:
                all_ok = False

            orc_ids = entry["q8_top5_ids"]
            orc_logits = entry["q8_top5_logits"]
            full_vocab_logsumexp = entry["q8_full_vocab_logsumexp"]
            comparisons = []
            for lid, llp in zip(llama_ids, llama_logprobs):
                orc_lp = _log_softmax_at(full_vocab_logsumexp, orc_ids, orc_logits, lid)
                if orc_lp is None:
                    comparisons.append({"token_id": lid, "llama_logprob": llp, "orc_logprob": None,
                                        "diff": None, "comparable": False})
                    continue
                diff = abs(orc_lp - llp)
                # Diagnostic only -- see module docstring point 6: no
                # justified tolerance exists yet, so this does NOT feed
                # into all_ok/PASS-FAIL.
                comparisons.append({"token_id": lid, "llama_logprob": llp, "orc_logprob": orc_lp,
                                    "diff": diff, "comparable": True})

            print(f"{entry['id']:24s} tokens_match=True orc_argmax={orc_argmax:6d} "
                  f"llama_argmax={llama_argmax:6d} argmax_agree={argmax_agree}")
            for c in comparisons:
                if not c["comparable"]:
                    print(f"    token {c['token_id']:6d}: llama_logprob={c['llama_logprob']:.6f}  "
                          f"orc_logprob=N/A (outside OrcEngine's own top-5 slice)")
                else:
                    print(f"    token {c['token_id']:6d}: llama_logprob={c['llama_logprob']:.6f}  "
                          f"orc_logprob={c['orc_logprob']:.6f}  diff={c['diff']:.6f}  (diagnostic only, "
                          f"no justified tolerance -- see module docstring)")

            results.append({"id": entry["id"], "token_ids_match": True, "orc_argmax": orc_argmax,
                            "llama_argmax": llama_argmax, "argmax_agree": argmax_agree,
                            "comparisons": comparisons})
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)

    with open(report_path, "w", encoding="utf-8") as f:
        for r in results:
            f.write(json.dumps(r) + "\n")

    return all_ok


if __name__ == "__main__":
    if len(sys.argv) != 4:
        sys.exit(f"usage: {sys.argv[0]} <q8_0.gguf> <evidence.jsonl> <output_report.jsonl>")
    ok = run(sys.argv[1], sys.argv[2], sys.argv[3])
    print(f"\n{'PASS' if ok else 'FAIL'}: OrcEngine-Q8_0 vs pinned llama.cpp-Q8_0 oracle agreement "
          f"(token-ID identity + argmax match ONLY -- log-probability diffs are reported as diagnostic "
          f"evidence, not gated, because no justified external-oracle log-probability tolerance exists "
          f"yet; deriving one is explicitly deferred, not fabricated after seeing results)")
    raise SystemExit(0 if ok else 1)
