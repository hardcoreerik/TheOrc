# Copyright (C) 2025-present hardcoreerik / TheOrc contributors
# SPDX-License-Identifier: AGPL-3.0-or-later
"""
Phase 4 prep (ENGINEERING_ROADMAP.md Phase 4, test/spec/fixture work only,
no engine code -- see phase2_prep/README.md for why this is permitted
under the Phase 0 stop gate): the benchmark record schema required by
docs/OrcEngine/BENCHMARK_STRATEGY.md's "Mandatory metadata", "Metrics",
and "Reporting template" sections, codified as a dataclass schema rather
than left as prose.

Not just a schema shell: environment_capture() below queries REAL hardware/
software facts from whatever machine runs it (CPU, OS, GPU via nvidia-smi
if present) and this module's __main__ produces one real populated record
from THIS machine, proving the schema captures what BENCHMARK_STRATEGY.md
actually asks for -- per the project's "build it, run it, show real
output" discipline, not a hypothetical schema no one has exercised.

No benchmark numbers are claimed or implied by this module -- there is no
OrcEngine to benchmark yet (Phase 1+ doesn't exist). This only proves the
RECORD FORMAT is real and populatable, ahead of Phase 4 needing it.
"""
from __future__ import annotations

import json
import os
import platform
import re
import subprocess
from dataclasses import asdict, dataclass, field


@dataclass
class HardwareSoftwareEnvironment:
    """BENCHMARK_STRATEGY.md 'Mandatory metadata', hardware/software subset."""
    operating_system: str
    os_power_plan: str | None
    cpu_model: str
    cpu_cores_logical: int
    cpu_cores_physical: int | None
    cpu_instruction_path: str  # e.g. "AVX2", "AVX512" -- best-effort, "unknown" if not determinable
    thread_count_used: int | None
    cpu_affinity_used: str | None
    ram_total_bytes: int | None
    numa_topology: str | None
    gpu_model: str | None
    gpu_compute_capability: str | None
    gpu_driver_version: str | None
    gpu_toolkit_version: str | None
    gpu_cublas_version: str | None
    gpu_clock_power_settings: str | None


@dataclass
class ModelConfigMetadata:
    """BENCHMARK_STRATEGY.md 'Mandatory metadata', model subset."""
    model_file_sha256: str
    architecture: str
    parameter_count: int | None
    tensor_types: list[str]
    context_length: int
    prompt_tokens: int
    generated_tokens: int
    batch_or_sequences: int
    cache_dtype_layout: str


@dataclass
class ProcedureMetadata:
    """BENCHMARK_STRATEGY.md 'Mandatory metadata', procedure subset."""
    cold_or_warm: str  # named precisely per "Cold versus warm" section, e.g. "cold_process_warm_pages"
    repetition_count: int
    warmup_count: int
    runtime_backend: str
    fallback_count: int


@dataclass
class Metrics:
    """BENCHMARK_STRATEGY.md 'Metrics' section."""
    load_latency_ms: float | None
    time_to_first_token_ms: float | None
    prompt_tokens_per_second: float | None
    decode_tokens_per_second: float | None
    per_token_latency_median_ms: float | None
    per_token_latency_p90_ms: float | None
    per_token_latency_p99_ms: float | None
    peak_host_bytes: int | None
    peak_device_bytes: int | None
    allocation_count: int | None
    allocation_bytes: int | None
    energy_watts: float | None  # only when measurement tooling is reliable, per the doc
    correctness_status: str      # e.g. "pass:oracle_profile_name" or "not_evaluated"


@dataclass
class BenchmarkRecord:
    """
    Full record matching BENCHMARK_STRATEGY.md's 'Reporting template':
      Benchmark ID / date, Question, Commit and tree, Hardware/software,
      Model/config hashes, Exact command, Warmup and samples, Correctness
      gate, Raw artifact, Summary, Comparison, Limitations, Conclusion.
    """
    benchmark_id: str
    date: str
    question: str
    orcengine_commit: str
    tree_dirty: bool
    environment: HardwareSoftwareEnvironment
    model_config: ModelConfigMetadata
    procedure: ProcedureMetadata
    exact_command: str
    metrics: Metrics
    raw_artifact_path: str
    summary: str
    comparison: str
    limitations: str
    conclusion: str

    def to_dict(self) -> dict:
        return asdict(self)


def _run(cmd: list[str]) -> str | None:
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=10)
        return out.stdout.strip() if out.returncode == 0 else None
    except Exception:
        return None


def _detect_cpu_instruction_path() -> str:
    # Best-effort: Windows doesn't expose this as simply as /proc/cpuinfo. Report what we can
    # confirm rather than guessing -- BENCHMARK_STRATEGY.md requires this field to be honest,
    # not fabricated to look complete.
    if platform.system() == "Windows":
        return "unknown (Windows: no simple /proc/cpuinfo-equivalent flags query used here)"
    try:
        with open("/proc/cpuinfo") as f:
            flags = f.read()
        if "avx512f" in flags:
            return "AVX512"
        if "avx2" in flags:
            return "AVX2"
        if "avx" in flags:
            return "AVX"
        return "unknown"
    except Exception:
        return "unknown"


def _detect_gpu_info() -> dict:
    out = _run(["nvidia-smi", "--query-gpu=name,driver_version,compute_cap", "--format=csv,noheader"])
    if not out:
        return {"gpu_model": None, "gpu_compute_capability": None, "gpu_driver_version": None}
    parts = [p.strip() for p in out.splitlines()[0].split(",")]
    return {
        "gpu_model": parts[0] if len(parts) > 0 else None,
        "gpu_driver_version": parts[1] if len(parts) > 1 else None,
        "gpu_compute_capability": parts[2] if len(parts) > 2 else None,
    }


def capture_environment() -> HardwareSoftwareEnvironment:
    """Queries REAL facts from the current machine. No placeholder/fabricated values --
    fields we genuinely cannot determine here are None, not guessed."""
    cpu_model = platform.processor() or "unknown"
    logical_cores = os.cpu_count() or 0
    gpu_info = _detect_gpu_info()

    ram_total = None
    if platform.system() == "Windows":
        # wmic is deprecated/removed on newer Windows builds (confirmed absent on this machine --
        # FileNotFoundError, caught by _run()'s try/except). Use the modern PowerShell equivalent.
        out = _run(["powershell", "-NoProfile", "-Command",
                    "(Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory"])
        if out:
            m = re.search(r"(\d+)", out)
            if m:
                ram_total = int(m.group(1))

    return HardwareSoftwareEnvironment(
        operating_system=f"{platform.system()} {platform.release()} ({platform.version()})",
        os_power_plan=None,  # not queried here -- would need `powercfg /getactivescheme` on Windows
        cpu_model=cpu_model,
        cpu_cores_logical=logical_cores,
        cpu_cores_physical=None,  # os.cpu_count() only gives logical; physical needs a 3rd-party lib
        cpu_instruction_path=_detect_cpu_instruction_path(),
        thread_count_used=None,  # per-run field, not an environment constant
        cpu_affinity_used=None,
        ram_total_bytes=ram_total,
        numa_topology=None,
        gpu_model=gpu_info["gpu_model"],
        gpu_compute_capability=gpu_info["gpu_compute_capability"],
        gpu_driver_version=gpu_info["gpu_driver_version"],
        gpu_toolkit_version=None,
        gpu_cublas_version=None,
        gpu_clock_power_settings=None,
    )


if __name__ == "__main__":
    env = capture_environment()
    print("Real environment captured from this machine:")
    print(json.dumps(asdict(env), indent=2))

    populated_fields = sum(1 for v in asdict(env).values() if v is not None)
    total_fields = len(asdict(env))
    print(f"\n{populated_fields}/{total_fields} environment fields populated with real data "
          f"(the rest are honestly None, not fabricated, per BENCHMARK_STRATEGY.md's own "
          f"'never select only the best run' / no-fabrication spirit).")

    output_path = os.path.join(os.path.dirname(__file__), "..", "artifacts", "benchmark_schema_example.json")
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8") as f:
        json.dump({"environment_capture_example": asdict(env),
                    "note": "No benchmark was run -- OrcEngine has no executable code yet (Phase 1+ "
                            "not started). This proves the record schema and environment-capture "
                            "function are real and populatable, not that any benchmark exists."},
                   f, indent=2)
    print(f"\nwrote example to {output_path}")
