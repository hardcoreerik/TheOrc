# Phase 4 prep — benchmark record schema

Test/spec work only, permitted under the Phase 0 stop gate (see
`../phase2_prep/README.md`) — no OrcEngine executable code exists to
benchmark yet (Phase 1+ hasn't started).

## What's here

`benchmark_schema.py` codifies `docs/OrcEngine/BENCHMARK_STRATEGY.md`'s
"Mandatory metadata", "Metrics", and "Reporting template" sections as
dataclasses: `HardwareSoftwareEnvironment`, `ModelConfigMetadata`,
`ProcedureMetadata`, `Metrics`, and the top-level `BenchmarkRecord`
matching the doc's reporting template field-for-field.

Not just a schema shell: `capture_environment()` queries REAL hardware/
software facts from whatever machine runs it (CPU, OS, GPU via
`nvidia-smi`, RAM via PowerShell) and `__main__` produces one real
populated record from this machine, proving the schema is genuinely
populatable — not a hypothetical format no one has exercised.

## No benchmark numbers, deliberately

This module makes zero performance claims. There is no OrcEngine to
benchmark yet. It only proves the *record format* and *environment-capture
function* work against real hardware, ahead of Phase 4 needing them.

## Honesty over completeness

8 of 16 environment fields are populated with real captured data on this
machine (CPU model, logical core count, RAM total, GPU model/driver/compute
capability, OS version). The rest (CPU instruction path, physical core
count, NUMA topology, GPU toolkit/cuBLAS versions, power settings) are
honestly `None` rather than guessed — `BENCHMARK_STRATEGY.md`'s own
"never select only the best run" discipline extends naturally to "never
fabricate a field you can't actually determine."

One real gap found and fixed while building this: the original RAM-capture
attempt used `wmic`, which is deprecated/removed on this Windows build
(confirmed via a real `FileNotFoundError`, not assumed) — switched to the
modern PowerShell `Get-CimInstance Win32_ComputerSystem` equivalent, which
works.

## Reproducing

```bash
cd Tools/OrcEnginePhase0
python3 -m phase4_prep.benchmark_schema
```

Writes `artifacts/benchmark_schema_example.json` (committed — small,
no large data, just one example environment-capture record).
