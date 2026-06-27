#!/usr/bin/env python3
"""Bounded turboSETI campaign entrypoint. Network is disabled by the Warband runner."""
import argparse
import json
from pathlib import Path

from turbo_seti.find_doppler.find_doppler import FindDoppler


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--max-drift", type=float, default=4.0)
    parser.add_argument("--snr", type=float, default=25.0)
    parser.add_argument("--gpu", action="store_true")
    args = parser.parse_args()

    inputs = sorted(p for p in Path("/input").iterdir() if p.is_file())
    if not inputs:
        raise SystemExit("no observation files were mounted")

    out = Path("/output")
    out.mkdir(parents=True, exist_ok=True)
    manifest = {
        "claim": "candidate requiring scientific review",
        "max_drift": args.max_drift,
        "snr": args.snr,
        "gpu": args.gpu,
        "observations": [],
    }

    for index, source in enumerate(inputs):
        observation_dir = out / f"observation-{index:04d}"
        observation_dir.mkdir()
        search = FindDoppler(
            datafile=str(source),
            max_drift=args.max_drift,
            snr=args.snr,
            out_dir=str(observation_dir),
            gpu_backend=args.gpu,
        )
        search.search()
        dat_files = sorted(observation_dir.glob("*.dat"))
        manifest["observations"].append({
            "input_index": index,
            "result_files": [p.name for p in dat_files],
        })

    (out / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps({"status": "completed", "observations": len(inputs)}))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
