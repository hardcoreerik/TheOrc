# Native Runtime — Multi-Machine HIVE Validation Plan (§6 entry criteria)

> **Status:** Plan only — this document designs the validation campaign for
> [NATIVE_RUNTIME_V2_SPEC.md §6](NATIVE_RUNTIME_V2_SPEC.md)'s default-runtime-flip entry
> criteria. Executing this plan produces the **evidence**; it does not itself authorize the
> flip. Per §6, the flip additionally requires an explicit, recorded product decision — that
> decision belongs to the maintainer, not to this plan or any green run of it.
>
> **Fleet surveyed live 2026-07-20** (SSH, `nvidia-smi`, `git rev-parse` on each box — not
> assumed). All three machines reachable over SSH with key auth; both remotes synced to
> `master` @ `aa07f41` during the survey.

## 1. Fleet under test

| Machine | Role | GPU / VRAM | Driver | Repo path | Notes (verified) |
|---|---|---|---|---|---|
| **NewcorePC** | Warchief + reference worker | RTX 5070 Ti, 16 GB | (reference box) | `F:\Ai\OrchestratorIDE-dev` | All Phase A–D evidence to date produced here. Test GGUF already present. |
| **HardcorePC** | Worker, low-VRAM class | RTX 3050, 6 GB | 560.94 | `F:\Ai\OrchestratorIDE-dev` | ⚠ Known unresolved native-lib regression (CF-7 gate broken since the 2026-07-04 clean rebuild; root cause never found). ~4 TB free. |
| **HardcoreLaptopMSI** | Worker, mobile class | RTX 4060 Laptop, 8 GB | 581.80 | `C:\Ai\OrchestratorIDE-dev` | CUDA-toolkit redist DLLs not present as system libs — the `OrchestratorIDE.NativeRuntime.csproj` bundling path (build-box `CUDA_PATH`) must be verified here or binaries deployed from NewcorePC. Only ~18 GB free on C:. |

The 16 / 8 / 6 GB VRAM spread and the driver spread (560.94 vs 581.80) are deliberate assets:
§6 asks for "representative hardware," and this fleet genuinely represents the desktop-large /
laptop-mid / desktop-small classes TheOrc targets. Record GPU model + driver + VRAM in every
evidence artifact.

**Pinned model fixture (distributed during the survey):**
`Dolphin3.0-Llama3.2-3B-Q4_K_M.gguf`,
SHA-256 `5d6d02eeefa1ab5dbf23f97afdf5c2c95ad3d946dc3b6e9ab72e6c1637d54177`.
Locations: NewcorePC `F:\Ai\GarfChat\checkpoints\android-test-models\`; HardcorePC
`F:\Ai\models\`; HardcoreLaptopMSI `C:\Ai\models\`. Hash must be re-verified on each box
before any phase runs (a corrupted copy invalidates cross-machine comparisons).

## 2. Criteria → phases map

Each §6 bullet maps to exactly one campaign phase below; a phase is DONE only with retained
evidence (same discipline as the Phase D E2E lane's `.orc/native-e2e-lane/` artifacts).

| §6 entry criterion | Phase |
|---|---|
| Successful native workloads across the intended machine roles | HV-1 |
| Correct capability- and resource-aware scheduling across machines | HV-2 |
| Verified model and adapter lifecycle behavior across machines | HV-3 |
| Failure, cancellation, disconnect, and recovery exercises across machines | HV-4 |
| Consistent telemetry and diagnosability across machines | HV-5 |
| No silent fallback across runtime boundaries | HV-4 + HV-5 (asserted in every phase) |
| Repeatable end-to-end evidence on representative hardware | HV-6 |
| Explicit product decision | **Out of scope for this plan** — maintainer's call, recorded separately |

## 3. Phases

### HV-0 — Fleet readiness (preconditions; no §6 credit)

1. **Merge gate:** PR #81 (E2E lane + evidence store) and #82 (docs sync) merged; all three
   boxes on the same `master` commit. (Repos synced already; re-sync after merges.)
2. **Model gate:** pinned GGUF hash verified on all three boxes.
3. **Build gate:** `dotnet build` green on each box, and the CUDA backend actually selected on
   each — `NativeBackendBootstrap`'s backend report per box, not assumed. On HardcoreLaptopMSI
   this specifically verifies the CUDA redist bundling; if the local build lacks `CUDA_PATH`,
   deploy binaries built on NewcorePC instead and record which path was taken.
4. **HardcorePC regression gate (the known blocker):** the unresolved native-lib regression
   must be root-caused and fixed — or explicitly waived with the box excluded — before HV-1.
   Diagnosing it IS the campaign's first real "diagnosability" exercise: use
   `THEORC_KVCACHE_DIAGNOSTICS=1`, the backend report, and the Phase D lane run locally on
   that box. HardcorePC has Claude CLI installed — prefer running diagnosis there directly
   over command-by-command SSH.
5. **Single-box lane gate:** the PR #81 E2E lane green on each box with retained evidence —
   three per-box artifacts recording tok/s, TTFT, measured VRAM. This is the per-box floor
   under every multi-machine phase.
6. **Worker outbound-polling liveness — CLOSED 2026-07-20. Control-plane inbound-listener
   reachability (the original scope of this gate, needed for `--declare-warchief`-style RPCs)
   remains NOT closed and is not applicable to a `--worker`-only fleet.** `swarmcli --worker`
   opens no inbound listener at all — it's a pure outbound poller — so there is nothing at port
   7078 on a worker box for a control-plane RPC to reach, by design, regardless of how long the
   process runs. That is a genuinely different, harder claim than "the worker is alive and
   polling," and this entry does NOT claim the harder one is resolved. What IS proven: commands
   run were `swarmcli --worker --warchief-url http://192.168.1.15:7079 --warchief-nodeid
   f083b993d872cdb2d13fc4c8435764bfd5f2ecc149a9910146e5bad3106c4768 --lanes coder` on HardcorePC
   (LAN) and `swarmcli --worker --warchief-url http://100.112.36.18:7079 --warchief-nodeid
   f083b993d872cdb2d13fc4c8435764bfd5f2ecc149a9910146e5bad3106c4768 --lanes coder` on
   HardcoreLaptopMSI (Tailscale) — both processes started with exit status 0, connected, and
   polled cleanly with no errors for the duration of the test in item 7 below. If a future phase
   needs the control-plane RPC path (e.g. remote role reassignment), that requires actually
   running a listener-bearing mode (`--warchief`, or a future persistent worker-with-listener
   mode) on the target box and re-testing `--declare-warchief` against it — starting a `--worker`
   process for longer does not get there. Evidence caveat: the polling proof above was observed
   as live session tool output, not written to a retained log file on either box — there is no
   persisted artifact path for this gate the way the Phase D lane has `.orc/native-e2e-lane/`. A
   future formal HV-1 run should redirect worker stdout to a retained per-box log file so this
   gate has a durable artifact, not just a transcript claim.
7. **Task-dispatch authorization gate — CLOSED 2026-07-20 (HMAC claim/complete path proven;
   control-plane Controller-authorization remains untested and is not applicable to this
   deployment shape).** Each worker's `hive-peers.json` does record NewcorePC as `role=Observer`
   (confirmed live via `swarmcli --show-identity`'s `SelfRole` field, not a stale snapshot), and
   being the HIVE's `Founder` does not grant automatic `Controller` authority toward peers —
   both true, per `HIVE_MEMBERSHIP_SPEC.md`. `--declare-warchief` (the actual test for whether
   the control-plane role-assignment RPC honors/rejects NewcorePC's authority over a peer) was
   attempted but never reached either worker: `--worker` mode opens no inbound listener for that
   RPC to connect to. **So the Controller-authorization question itself is still unverified for
   this worker-only deployment shape — it was not resolved, only found not applicable to the
   mechanism this phase actually needs.** What WAS proven, on the separate task-queue path: a
   real `swarmcli --warchief --goal "Create a file named hello.txt containing the single line:
   hive dispatch test ok"` one-shot run (workspace `F:\Ai\hive-test-scratch`) had its single
   coder task dispatched to the HIVE queue and claimed/completed by HARDCORELAPTOPMSI over the
   real Tailscale network (`[coder] 💻 Write create_file.py — ✅ completed by HARDCORELAPTOPMSI`),
   including a full retry-on-test-failure loop (tester caught a missing `main.py`, boss
   spawned a targeted fix task, coder wrote it, `python -m py_compile main.py` exited 0, swarm
   completed). **Task-queue claim/complete uses a separate HMAC-based mechanism from the
   control-plane role-assignment RPC, and that HMAC path does not gate on the Observer/Controller
   role snapshot — this is sufficient for HV-1's dispatch requirement even though the
   Controller-authorization RPC itself was never exercised.** Both remote worker processes
   stopped and the disposable test workspace cleaned up afterward.

### HV-1 — Native workloads across machine roles

Warchief (NewcorePC) dispatches real native-role campaign jobs to both workers through the
existing HIVE campaign contracts (leasing, persistence, verification — already in code) using
the fleet's `hive-peers.json` (use the auto-resync tool, not hand edits).

- Each worker executes real native generations for jobs it did not originate.
- Evidence per job: runtime name (`NativeRoleRuntime`), machine identity, binding, output,
  stats — and **no fallback marker anywhere**. Native campaign jobs already fail closed by
  design; this proves it holds when dispatched remotely.
- Pass: N≥5 jobs per worker, all native, zero fallback, evidence retained per job.

### HV-2 — Capability/resource-aware scheduling

Exploit the VRAM spread deliberately: submit jobs whose context-aware footprint (PR #76
estimator, large `ContextLength`) fits 16 GB and 8 GB but must be **denied** on 6 GB.

- HardcorePC must deny with a real `RuntimeAdmissionDeniedException` (correct numbers in the
  reason), observable in its telemetry (`RejectedAdmissionCount`, `LastRejectionReason`).
- The Warchief must respect capability/placement — the denied job either lands on a box that
  fits or fails visibly; it must never silently reroute to Ollama.
- Also run the inverse: a small job admitted on all three, proving denial is footprint-driven,
  not box-driven.

### HV-3 — Model/adapter lifecycle across machines

- Sequential load → generate → dispose cycles per worker; residency (`ActiveCount`) returns to
  baseline between jobs; reservation behavior matches the documented decoupling (reservation
  persists with the loaded model; residency does not).
- Concurrent second role on the same worker — folds in the deferred Phase D "second concurrent
  role" increment: cross-role admission accounting proven inside one evidence-bearing run.
- Rebind/recycle: force a role recycle (the PR #79 `MarkRoleDegraded` path) remotely and prove
  the next job on that role gets a fresh, working executor.

### HV-4 — Failure, cancellation, disconnect, recovery

All on real jobs mid-flight, all asserting fail-closed (no Ollama substitution) and clean
recovery:

1. **Remote cancellation** mid-generation → `OperationCanceledException` surfaces to the
   dispatcher; worker role reusable afterward (PR #79's fix, now proven cross-machine).
2. **Worker process kill** mid-job → Warchief detects the death, job fails **visibly**,
   lease/queue behavior correct; worker restarts and rejoins (auto-resync), then serves a new
   job.
3. **Network disconnect** (temporarily block the HIVE port / drop the link) mid-campaign →
   same visibility + recovery expectations; no half-spliced outputs.
4. **Ollama-absent worker**: with Ollama stopped on a worker, a native-routed job still runs
   natively; a deliberately broken native config on that worker fails closed with an explicit
   native error — CF-6's "Ollama-absence death test" precedent, now fleet-wide.

### HV-5 — Telemetry consistency + no-silent-fallback sweep

- Same evidence JSON schema from all three boxes for one shared campaign; per-box
  reservation/residency/measured-VRAM snapshots collected centrally.
- Log sweep across all three boxes: zero silent-fallback markers, plus the standing
  `NoKvSlot` grep before trusting any numbers.
- Diagnosability drill: for one induced failure per box, the retained diagnostics
  (`error_type`, KV diagnostics, backend report) must be sufficient to identify the cause
  without interactive debugging — measured by actually doing it.

### HV-6 — Repeatability + fleet report

- The full HV-1→HV-5 campaign, run **3× back-to-back**, all green, no manual intervention
  between runs.
- Aggregate fleet report (per-box + campaign-level JSON and a human summary) retained as the
  §6 evidence bundle. The report explicitly does NOT claim the flip — it presents the
  evidence for the maintainer's §6 decision.

## 4. Harness shape (implementation guidance, not code)

- **Driver:** a `Tools/` PowerShell orchestration script on the Warchief (SSH for box-level
  actions: build, kill, restart; HIVE contracts for job dispatch) — multi-machine flows do not
  fit NUnit. Per-box, the existing gated NUnit lanes remain the single-box floor (HV-0.5).
- **Evidence:** extend the `NativeE2ELaneEvidenceStore` schema pattern with
  `machine`, `gpu`, `driver_version` fields (a sibling fleet store, keeping `schema_version`
  discipline); central collection via scp back to the Warchief.
- **Small, reviewed PRs as always:** evidence-store extension, then driver script, then
  per-phase additions — not one mega-PR.

## 5. Known blockers & risks (honest, current as of 2026-07-20)

1. **HardcorePC native-lib regression** — unresolved since 2026-07-04; hard precondition
   (HV-0.4). Until fixed, the fleet is effectively two machines and §6's "across machines"
   evidence is weakened.
2. **PR #81/#82 unmerged** — the harness builds on #81's lane and evidence store.
3. **HardcoreLaptopMSI disk headroom** (~18 GB free): one pinned model is fine; do not stage
   multi-model suites there without cleanup.
4. **CUDA redist on the laptop** — bundling depends on the build box's `CUDA_PATH`; a wrong
   build silently lands on the CPU backend at ~1.7 tok/s, which HV-0.3's backend report check
   exists to catch.
5. **Driver spread** (560.94 vs 581.80) — treated as representative, but if HardcorePC's
   regression turns out driver-related, a driver update becomes part of HV-0.4 and must be
   recorded in the evidence.
