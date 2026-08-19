# Fringe Lab Session Baseline — 2026-08-18-state-residency

## Repository identity

- Worktree: `F:\Ai\OrchestratorIDE-fringelab2` (NEW worktree created this session --
  the pre-existing `F:\Ai\OrchestratorIDE-fringelab` worktree was found to predate
  Phase 1-4 entirely, containing only `Tools/OrcEnginePhase0`, and was not a valid
  base for experiments needing Phase 1/3/4's C++ residency/KV infrastructure)
- Branch: `research/orcengine-fringe-lab2`
- Base authority: `orcengine-phase4-freeze` (peeled `944f07b86428ec53d46ca19dc66c3d0d5b1e207d`)
- HEAD at session start: `944f07b86428ec53d46ca19dc66c3d0d5b1e207d` (clean)

## Environment (see environment/ for raw captures)

- CMake 4.3.3
- Python 3.14.3 (`C:\Users\hardc\AppData\Local\Python\pythoncore-3.14-64\python.exe`)
- `cl` not on PATH outside a VS developer shell (not changed; MSVC builds use
  the same generator-driven invocation established elsewhere in this repo)
- CPU: AMD Ryzen 5 7600X 6-Core Processor
- RAM: ~31.1 GiB
- GPU: NVIDIA GeForce RTX 5070 Ti (WMI AdapterRAM under-reports VRAM on cards
  >4GB due to a known 32-bit-field limitation; not used as an authoritative
  VRAM figure)
- Storage (F:): ~1.16 TiB free

## Scope for this session

FL-05 (shared prefix state), FL-06 (context/execution identity), FL-07
(budgeted weight residency). First research wave only, per the session
charter. This is isolated research -- no modification to
`feat/orcengine-phase5a-kv-cache`, frozen tags, or any production code.
