# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Downloads the Phase 0 real-model candidate at its PINNED revision (per
PHASE_0_ARCHITECTURE_PROFILE.md / OE-ADR-014):
  HuggingFaceTB/SmolLM2-135M, revision 93efa2f097d58c2a74874c7e644dbc9b0cee75a2

Pinning the revision (not "main"/latest) is the point: provenance_complete
requires an immutable source identity, not "whatever HF serves today."
Computes and prints SHA-256 for every downloaded file -- this is the raw
material for the provenance manifest, not the manifest itself (that's a
separate step once conversion also has hashes to report).
"""
from __future__ import annotations

import hashlib
import json
import os

from huggingface_hub import snapshot_download

REPO_ID = "HuggingFaceTB/SmolLM2-135M"
REVISION = "93efa2f097d58c2a74874c7e644dbc9b0cee75a2"
LOCAL_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "smollm2-135m")


def _sha256_file(path: str) -> str:
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def run() -> dict:
    os.makedirs(LOCAL_DIR, exist_ok=True)
    local_path = snapshot_download(
        repo_id=REPO_ID, revision=REVISION, local_dir=LOCAL_DIR,
    )
    print(f"downloaded to {local_path}")

    hashes = {}
    for root, _dirs, files in os.walk(local_path):
        for fn in files:
            if fn.startswith(".") or fn == "download_manifest.json":
                continue
            full = os.path.join(root, fn)
            rel = os.path.relpath(full, local_path)
            sha = _sha256_file(full)
            size = os.path.getsize(full)
            hashes[rel] = {"sha256": sha, "size_bytes": size}
            print(f"  {rel}: {size} bytes, sha256={sha}")

    manifest = {
        "repo_id": REPO_ID,
        "pinned_revision": REVISION,
        "local_path": local_path,
        "files": hashes,
    }
    manifest_path = os.path.join(local_path, "download_manifest.json")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, sort_keys=True)
    print(f"\nwrote download manifest to {manifest_path}")
    return manifest


if __name__ == "__main__":
    run()
