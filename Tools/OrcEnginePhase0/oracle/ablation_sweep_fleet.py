# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Runs ablation_sweep's full-layer-impact sweep against every real GGUF
model TheOrc has access to, from THREE sources -- not just the pinned
SmolLM2-135M Phase 0 candidate:

  1. %APPDATA%/OrchestratorIDE/Models -- TheOrc's default native-runtime
     model root (named *.gguf files).
  2. Any directories in ORC_EXTRA_MODELS_DIRS (semicolon-separated) --
     e.g. F:\\AI\\Models, mirroring settings.json's nativeRuntimeModelRoots
     (a second drive with more GGUFs, per that setting's own comment).
  3. Ollama's model store (OLLAMA_MODELS env var if set, else the default
     ~/.ollama). Ollama's "blobs" are content-addressed files with NO
     .gguf extension, but the model-weight layer IS a raw GGUF file
     (verified: reading the first 4 bytes of an Ollama model blob gives
     b"GGUF") -- gguf.GGUFReader reads by content, not extension, so
     these work directly. Each manifest under models/manifests/**/<tag>
     is parsed to find the "application/vnd.ollama.image.model" layer's
     digest, which maps to blobs/sha256-<digest>.

This is deliberately honest about two hard limits rather than silently
skipping or silently approximating past them:

  1. Architecture support: oracle/model.py only implements "llama" and
     "qwen2" block semantics (see oracle/gguf_model_loader.py's module
     docstring for why "qwen35" -- the Qwen3.8 GGUF family -- is out of
     scope for now). Unsupported-architecture files are skipped with a
     clear reason, not silently misinterpreted as llama.
  2. Memory: this whole suite dequantizes weights to full float32 in
     RAM (no on-the-fly/streamed dequantization). A model's real
     parameter count x 4 bytes must fit comfortably in available system
     memory or the run is skipped with the estimated requirement printed
     -- per this project's "no silent caps" discipline, every skip is
     logged with its reason, never just absent from the output.
"""
from __future__ import annotations

import hashlib
import json
import os
import time
import traceback
from dataclasses import dataclass

import numpy as np
import psutil
import yaml
from gguf import GGUFReader

from oracle.ablation_sweep import _all_specs, _demo_per_position_query, _print_summary, _write_report, run_sweep
from oracle.gguf_model_loader import SUPPORTED_ARCHITECTURES, load_gguf_as_model_weights

MODELS_DIR = os.environ.get(
    "ORC_MODELS_DIR",
    os.path.join(os.environ.get("APPDATA", ""), "OrchestratorIDE", "Models"),
)
EXTRA_MODELS_DIRS = [d for d in os.environ.get("ORC_EXTRA_MODELS_DIRS", "").split(";") if d]
# Ollama's default (when OLLAMA_MODELS isn't set) is ~/.ollama directly -- NOT
# ~/.ollama/models -- containing blobs/ and manifests/ as immediate subdirectories.
# Verified against this fleet's actual OLLAMA_MODELS=F:\.ollama layout.
OLLAMA_MODELS_DIR = os.environ.get(
    "OLLAMA_MODELS",
    os.path.join(os.environ.get("USERPROFILE", ""), ".ollama"),
)
FLEET_REPORT_DIR = os.path.join(os.path.dirname(__file__), "..", "artifacts", "ablation_fleet")


@dataclass(frozen=True)
class DiscoveredModel:
    display_name: str  # human-readable, used as the report filename and model_label
    path: str           # actual file path GGUFReader should open (may lack .gguf extension)
    source: str          # "models_dir" | "extra_dir" | "ollama"

# Fraction of TOTAL physical RAM a model's dequantized-to-float32 weight size may consume
# before we refuse to load it. Conservative -- forward-pass intermediates, the Python
# process itself, and the OS all need headroom too; this is not "would technically fit."
MAX_RAM_FRACTION = 0.5

# Deterministic, tokenizer-free prompts: plain token-ID sequences avoiding IDs 0-9 (where
# BOS/EOS/PAD/UNK/control tokens conventionally live across most tokenizer vocabularies).
# Ablation impact is a property of the WEIGHTS' internal computation, not of prompt
# semantics, so real natural-language text isn't required for this measurement -- and
# skipping per-model tokenizer loading keeps this runnable against any GGUF uniformly.
def _prompts_for_vocab(vocab: int) -> list[np.ndarray]:
    rng = np.random.default_rng(20260814)
    low = 100 if vocab > 200 else 10
    high = vocab - 1
    return [
        rng.integers(low, high, size=n, endpoint=True).astype(np.int64)
        for n in (4, 5, 3)
    ]


def _discover_gguf_files(models_dir: str, source: str) -> list[DiscoveredModel]:
    if not os.path.isdir(models_dir):
        return []
    found = []
    for root, _dirs, files in os.walk(models_dir):
        for name in files:
            if name.lower().endswith(".gguf"):
                path = os.path.join(root, name)
                found.append(DiscoveredModel(display_name=name, path=path, source=source))
    return sorted(found, key=lambda m: m.display_name)


def _discover_ollama_models(ollama_dir: str) -> list[DiscoveredModel]:
    """Walks models/manifests/**/<tag> (tag is the last path component; everything before
    it is the repo/namespace), reads each manifest JSON for the
    "application/vnd.ollama.image.model" layer's digest, and maps that to
    blobs/sha256-<digest> -- the actual GGUF file, just without a .gguf extension."""
    manifests_dir = os.path.join(ollama_dir, "manifests")
    blobs_dir = os.path.join(ollama_dir, "blobs")
    if not os.path.isdir(manifests_dir) or not os.path.isdir(blobs_dir):
        return []

    found = []
    for root, _dirs, files in os.walk(manifests_dir):
        for name in files:
            manifest_path = os.path.join(root, name)
            rel = os.path.relpath(manifest_path, manifests_dir)
            display_name = rel.replace(os.sep, "/")  # e.g. "registry.ollama.ai/library/llama3.1/8b"
            try:
                with open(manifest_path, encoding="utf-8") as f:
                    manifest = json.load(f)
            except Exception as e:
                print(f"[ollama:{display_name}] SKIP: unreadable manifest ({e})")
                continue
            model_layer = next(
                (l for l in manifest.get("layers", []) if "image.model" in l.get("mediaType", "")), None
            )
            if model_layer is None:
                # Not every manifest layer set has a weight blob (e.g. embedding-only or
                # adapter-only manifests) -- skip quietly, this isn't a fault condition.
                continue
            digest = model_layer["digest"].replace(":", "-")  # "sha256:abc" -> "sha256-abc"
            blob_path = os.path.join(blobs_dir, digest)
            if not os.path.isfile(blob_path):
                print(f"[ollama:{display_name}] SKIP: manifest references missing blob {digest}")
                continue
            found.append(DiscoveredModel(display_name=display_name, path=blob_path, source="ollama"))
    return sorted(found, key=lambda m: m.display_name)


def _discover_all_models() -> list[DiscoveredModel]:
    models = []
    print(f"scanning {MODELS_DIR!r} (models_dir) for *.gguf ...")
    models.extend(_discover_gguf_files(MODELS_DIR, "models_dir"))
    for extra in EXTRA_MODELS_DIRS:
        print(f"scanning {extra!r} (extra_dir) for *.gguf ...")
        models.extend(_discover_gguf_files(extra, "extra_dir"))
    print(f"scanning {OLLAMA_MODELS_DIR!r} (ollama) for tagged models ...")
    models.extend(_discover_ollama_models(OLLAMA_MODELS_DIR))

    # De-duplicate by content: the same underlying model can appear under multiple names
    # (an Ollama pull of the same repo TheOrc's Models dir also has a named copy of, or two
    # Ollama tags sharing one blob via content-addressing). Dedupe by (size, path) isn't
    # enough since Ollama blobs and named .gguf copies have different paths for identical
    # content -- dedupe by file size + a cheap partial hash of the first 1MB instead, since
    # a full sha256 over every candidate (some 15GB+) before even knowing if it's runnable
    # would be wasteful. Not cryptographically rigorous, but sufficient to catch the exact
    # scenario this fleet actually has (Ollama blob duplicated as a named .gguf elsewhere).
    seen = {}
    deduped = []
    for m in models:
        try:
            size = os.path.getsize(m.path)
            with open(m.path, "rb") as f:
                head_hash = hashlib.sha256(f.read(1024 * 1024)).hexdigest()
        except OSError:
            deduped.append(m)
            continue
        key = (size, head_hash)
        if key in seen:
            print(f"  [{m.display_name}] SKIP: duplicate content of already-discovered "
                  f"{seen[key]!r} (same size + head hash)")
            continue
        seen[key] = m.display_name
        deduped.append(m)
    return deduped


def _estimate_param_bytes(path: str) -> tuple[int, str | None]:
    """Returns (estimated_float32_bytes, architecture_or_None). Reads only metadata/tensor
    shapes (GGUFReader is a memory-mapped, lazy reader -- this does NOT dequantize)."""
    reader = GGUFReader(path)
    arch_field = reader.fields.get("general.architecture")
    arch = bytes(arch_field.parts[arch_field.data[0]]).decode("utf-8") if arch_field else None
    total_elements = sum(int(np.prod(t.shape)) for t in reader.tensors)
    return total_elements * 4, arch  # float32 = 4 bytes/element


def run() -> bool:
    discovered = _discover_all_models()
    if not discovered:
        print("FAIL: no models found across models_dir, extra_dirs, or the Ollama store")
        return False

    total_ram = psutil.virtual_memory().total
    ram_budget = total_ram * MAX_RAM_FRACTION
    by_source = {}
    for m in discovered:
        by_source[m.source] = by_source.get(m.source, 0) + 1
    print(f"\nfound {len(discovered)} model(s) after de-duplication ({by_source}); "
          f"system RAM={total_ram / 1e9:.1f}GB, per-model budget={ram_budget / 1e9:.1f}GB "
          f"({MAX_RAM_FRACTION:.0%} of total)\n")

    fleet_summary = []
    for model in discovered:
        path = model.path
        name = model.display_name
        try:
            est_bytes, arch = _estimate_param_bytes(path)
        except Exception as e:
            print(f"[{name}] SKIP: could not read GGUF metadata ({e})")
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"unreadable: {e}"})
            continue

        if arch not in SUPPORTED_ARCHITECTURES:
            print(f"[{name}] SKIP: architecture={arch!r} not in {SUPPORTED_ARCHITECTURES} "
                  f"(oracle/model.py doesn't implement this block structure yet)")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"unsupported architecture: {arch}"})
            continue

        if est_bytes > ram_budget:
            print(f"[{name}] SKIP: architecture={arch!r} but estimated float32 size "
                  f"{est_bytes / 1e9:.1f}GB exceeds the {ram_budget / 1e9:.1f}GB budget "
                  f"(this suite dequantizes fully into RAM, no streaming)")
            fleet_summary.append({"file": name, "status": "skipped",
                                   "reason": f"too large: {est_bytes / 1e9:.1f}GB estimated "
                                             f"> {ram_budget / 1e9:.1f}GB budget"})
            continue

        print(f"[{name}] loading (arch={arch}, ~{est_bytes / 1e9:.1f}GB estimated)...")
        t0 = time.time()
        try:
            weights, config, info = load_gguf_as_model_weights(path)
        except Exception as e:
            print(f"[{name}] SKIP: load failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"load failed: {e}"})
            continue
        print(f"  loaded in {time.time() - t0:.1f}s: n_layers={config.n_layers} "
              f"hidden={config.hidden} n_q_heads={config.n_q_heads} n_kv_heads={config.n_kv_heads} "
              f"vocab={config.vocab} quant_types={info['quant_types']} "
              f"tied_embeddings={info['tied_embeddings']} has_qkv_bias={info['has_qkv_bias']}")

        prompts = _prompts_for_vocab(config.vocab)
        specs = _all_specs(config, components="layers_only")
        print(f"  sweeping {len(specs)} layers x {len(prompts)} prompts...")
        try:
            report = run_sweep(weights, config, prompts, specs, model_label=name, log_progress=False)
        except Exception as e:
            print(f"[{name}] SKIP: sweep failed: {e}")
            traceback.print_exc()
            fleet_summary.append({"file": name, "status": "skipped", "reason": f"sweep failed: {e}"})
            continue

        report["gguf_info"] = info
        report["source"] = model.source
        _print_summary(report)
        _demo_per_position_query(report)
        # Strip only a REAL trailing .gguf extension (models_dir/extra_dir sources) --
        # NOT via os.path.splitext, which would mistake the dot in an Ollama tag name like
        # "registry.ollama.ai/library/phi4-mini/latest" for a file extension and truncate
        # everything after it (confirmed: produced "registry.ollama.yaml" instead of the
        # full phi4-mini report name). Ollama-sourced display names have no real extension
        # to strip in the first place.
        base = name[:-5] if name.lower().endswith(".gguf") else name
        safe_name = base.replace("/", "__").replace("\\", "__").replace(":", "_")
        out_path = os.path.join(FLEET_REPORT_DIR, f"{safe_name}.yaml")
        _write_report(report, out_path)

        most_impactful = report["results"][0]
        least_impactful = report["results"][-1]
        fleet_summary.append({
            "file": name, "source": model.source, "status": "completed", "architecture": arch,
            "n_layers": config.n_layers, "hidden": config.hidden,
            "elapsed_seconds": report["elapsed_seconds"],
            "most_impactful_layer": most_impactful["label"],
            "most_impactful_logit_l2": most_impactful["logit_l2"],
            "least_impactful_layer": least_impactful["label"],
            "least_impactful_logit_l2": least_impactful["logit_l2"],
        })
        # Free the dequantized weights before the next model -- these are large arrays
        # and there's no reason to hold more than one model's worth in memory at a time.
        del weights, report

    os.makedirs(FLEET_REPORT_DIR, exist_ok=True)
    summary_path = os.path.join(FLEET_REPORT_DIR, "_fleet_summary.yaml")
    with open(summary_path, "w", encoding="utf-8") as f:
        yaml.safe_dump({
            "models_dir": MODELS_DIR, "extra_models_dirs": EXTRA_MODELS_DIRS,
            "ollama_models_dir": OLLAMA_MODELS_DIR, "results": fleet_summary,
        }, f, sort_keys=False, default_flow_style=False)

    print(f"\n{'=' * 60}\nfleet sweep summary\n{'=' * 60}")
    for r in fleet_summary:
        if r["status"] == "completed":
            print(f"  {r['file']}: most-impactful={r['most_impactful_layer']} "
                  f"({r['most_impactful_logit_l2']:.2f}), least-impactful="
                  f"{r['least_impactful_layer']} ({r['least_impactful_logit_l2']:.2f})")
        else:
            print(f"  {r['file']}: SKIPPED -- {r['reason']}")
    print(f"\nwrote {os.path.abspath(summary_path)}")

    completed = sum(1 for r in fleet_summary if r["status"] == "completed")
    return completed > 0


if __name__ == "__main__":
    ok = run()
    print(f"\n{'PASS' if ok else 'FAIL'}: ablation_sweep_fleet ({'at least one model completed' if ok else 'no models completed'})")
    raise SystemExit(0 if ok else 1)
