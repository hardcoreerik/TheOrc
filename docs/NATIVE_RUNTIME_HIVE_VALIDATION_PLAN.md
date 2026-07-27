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

**2026-07-21 — Driver built (`Tools/Hv1NativeCampaignRunner`, PR #87), real run against the
fleet, PARTIAL PASS: HardcoreLaptopMSI closed, HardcorePC blocked by a genuine, newly-found
capacity gap — not a driver or fleet-pairing defect.**

**The driver**: submits a `CampaignDefinition` of `ExecutionKind=NativeAgent` work units directly
to a live Warchief's `/hive/campaigns`, pinned per-worker via `ResourceRequirements.
ExcludedWorkerIds` (no CLI flag existed for N-repeat or worker-targeted dispatch before this),
polls each unit's `GET /hive/tasks/{id}` to a terminal state, and validates the exact evidence
HV-1 asks for. That endpoint didn't expose `HiveTaskResult.Attestation`/`Metrics` at all before
this work — only `OutputArtifacts` was surfaced — so `HiveTaskStatusResponse` gained those two
fields as part of this PR. Optional `--gate-model-hash` makes `NativeModelHash` a live capability
gate instead of just an echoed report value.

**Real gap found: `swarmcli --worker` cannot execute `NativeAgent` work units at all.** It never
calls `WorkerCapabilityDetector.DetectAsync` or constructs an `IHiveNativeRoleExecutor` (zero
references to either in `Tools/SwarmCli/Program.cs`), so `HiveWorkerAgent.NativeRoleExecutor`
stays null and any native-role job falls through to the Ollama/`CoderModel` path — which isn't
configured on these boxes, so every job failed with "no model configured." The one-off ad-hoc
dispatch test earlier in this campaign succeeded via swarmcli, but on inspection that used the
default `LegacyAgent` execution kind (plain boss-decomposed goal dispatch through Ollama), not
`NativeAgent` — it was never actually proof of native execution, only of task-queue dispatch.
The real native worker for this deployment shape is `OrchestratorIDE.Daemon` (already exists,
already cross-platform-proven on a Raspberry Pi), configured via `Hive:*` / `HIVE__*` env vars
(`WorkerMode`, `NativeModelRoot`, `NativeVramMb`, `WarchiefUrl`, `WarchiefNodeId`, `WorkerLanes`)
— it wires a real `NativeRoleRuntime` + `HiveNativeRoleExecutorAdapter` when `NativeVramMb > 0`
and a GGUF is present in `NativeModelRoot`.

**Real incident: switching to the Daemon clobbered HardcorePC's (and pre-emptively,
HardcoreLaptopMSI's) HIVE identity — recovered by re-pairing, no data loss beyond the old
keypairs.** `swarmcli` and `OrchestratorIDE.Daemon` share the same identity file path
(`%AppData%\TheOrc\hive-identity.json`) but different encryption: swarmcli defaults to Windows
DPAPI, the Daemon always forces AES-GCM (`Program.cs`'s own comments document this exact
collision, for the Pi's benefit — read it, registered the risk, tripped it anyway on the first
`--show-identity` call). Decryption failed silently, `HiveIdentity.Load()`'s catch-and-regenerate
path fired, and a brand-new identity was generated and persisted, overwriting the old one.
Confirmed via NewcorePC's own peer-store entries: HardcorePC's nodeId changed from `2bacaaef43fd…`
to `5f366bd33add…`. Recovered by restarting the local Warchief with `--allow-fingerprint` for
each box's new fingerprint and running `theorc-warband.exe --pair --target <ip>
--expect-fingerprint <newcorepc's fingerprint>` from each worker (the Daemon must always
initiate, never approve, pairing — no headless approval path exists). Both re-paired clean,
confirmed via the updated peer-store entries (role=Worker, as before).

**Real result — HardcoreLaptopMSI (RTX 4060 Laptop, 8 GB): CLEAN PASS, 5/5.** All five jobs
completed, claimed by the correct target (`ClaimedBy` matched), `Attestation.RuntimeName ==
"NativeRoleRuntime"` on every job, live model-hash capability match (`--gate-model-hash`) against
the pinned fixture, real stats (`steps`/`prompt_tokens`/`completion_tokens`) on every job, zero
fallback anywhere. Evidence: `.orc/hv-1-lane/hv1_native_campaign_20260721_032423.json`.

**Real result — HardcorePC (RTX 3050, 6 GB): BLOCKED, 1/5 at best, reproducible across a fresh
process restart.** The first native job on a freshly-started Daemon process always succeeds;
every job after it — even in a completely clean process, even after the first job's task
finished and control returned to the worker loop — is denied by a genuine, correctly-functioning
`RuntimeAdmissionDeniedException`: `Requires ~3.4 GB, only 2.4 GB available. Budget total=6.0 GB,
reserved=3.6 GB`. This is fail-closed working exactly as designed (no silent fallback, no
overcommit) — the actual finding is that the first job's VRAM reservation for the `Worker` role
never releases, so a second sequential job for the same role can never be admitted on a card this
tight. HardcoreLaptopMSI's 8 GB apparently has enough headroom to absorb the same non-release
across 5 jobs; HardcorePC's 6 GB does not, cleanly reproducing on the box HV-0 deliberately
included *because* it's the fleet's low-VRAM class. Root cause is inside
`NativeRoleRuntime`/`AdapterManager`'s conversation lifecycle (`HiveNativeRoleExecutorAdapter.
ExecuteAgentAsync` never sees or disposes a conversation handle — that lifecycle is fully
internal), not in the HIVE dispatch layer this campaign has been testing — genuinely out of
scope to root-cause further inside this campaign. **Filed as an open follow-up, not fixed here.**

**Minor evidence-quality gap noted, not fixed**: every job's `Attestation.Backend` reports `"cpu"`
even though the Daemon's own startup log confirms `"CUDA backend selected (cuda12...)"` on both
boxes — `HiveService.cs` calls `NativeBackendBootstrap.EnsureConfigured` for logging only and
never passes its verdict into `WorkerCapabilityDetector.DetectAsync`'s `verifiedNativeBackend`
parameter (which defaults to `"cpu"`). Doesn't affect whether native execution happened
(`RuntimeName` already proves that), just makes the `Backend` field in evidence read wrong.

**2026-07-21 (later same session) — root cause found and fixed; HV-1 CLOSED for real, both
boxes, at the original full context (8192).** The "reservation never releases" framing above was
imprecise — the actual bug: `EnsureAdmitted`'s budget comes from a **live whole-GPU nvidia-smi
read** (`NativeVramProbe`), whose `ReservedBytes` already includes a role's *resident* model once
one is loaded. `TryAdmit` then charged a **full fresh-load `EstimateRequiredBytes`** for that same
model on top — one resident model counted once as used (by the probe) and once as needed (by the
estimate). On a card too tight to hold two phantom copies of the same model, every job after the
first was denied. This is exactly analogous to the same-role exclusion `EnsureAdmitted` already
applied to *other* roles' ledger entries (`_reservedByRole`, excluding `binding.Role` itself,
`RuntimeOrchestrator.cs:286-290`) — it just didn't extend that exclusion to the *live probe's own*
number. HardcoreLaptopMSI's 5/5 pass earlier was headroom, not correctness: 8 GB minus one
double-counted ~3.4 GB model still left enough room for a second phantom copy; HardcorePC's 6 GB
did not. Confirmed empirically before touching code: re-running at `NativeContextSize=2048`
(shrinks the KV-cache-dominated estimate) got HardcorePC to 3/3 — proving the double-count was
KV/context-sized, not a true leak.

**Fix** (`RuntimeOrchestrator.EnsureAdmitted`): credit this role's own already-counted resident
bytes back out of the live baseline before charging the estimate, clamped at zero so a probe that
under-counts can't drive the budget negative. Cross-role accounting is unchanged. Regression test
`EnsureAdmitted_ReadmitsSameRole_WithoutDoubleCountingResidentModel`
(`OrchestratorIDE.UnitTests/RuntimeOrchestratorTests.cs`) reproduces the exact shape with a
stateful stand-in for the live probe (idle → resident) — confirmed red before the fix, green
after; full `RuntimeOrchestrator`/`Hive`/`OrcScheduler`/`AdapterManager` suite 155/155 green with
`THEORC_TEST_GGUF` set.

**Decisive re-run, same config that produced the 1/5 failure (full `NativeContextSize=8192`, 5
jobs/worker, live `--gate-model-hash`): HARDCOREPC 5/5, HARDCORELAPTOPM 5/5, zero fallback.**
Evidence: `.orc/hv-1-lane/hv1_native_campaign_20260721_051133.json`.

**HV-1 verdict: CLOSED.** Real native dispatch, correct placement, zero fallback, live capability
matching, evidence retained per job, N≥5 per worker — proven on both fleet machines, including
the deliberately-included low-VRAM class, at the runtime's normal context size. Cleaned up: both
Daemon processes and the local Warchief stopped, remote scratch workspaces
(`hv1-daemon-workspace`) and logs deleted, all local scratch evidence directories (`-smoke`,
`-diag`, `-ctx2048`) removed — only the real closing evidence file retained.

### HV-2 — Capability/resource-aware scheduling

Exploit the VRAM spread deliberately: submit jobs whose context-aware footprint (PR #76
estimator, large `ContextLength`) fits 16 GB and 8 GB but must be **denied** on 6 GB.

- HardcorePC must deny with a real `RuntimeAdmissionDeniedException` (correct numbers in the
  reason), observable in its telemetry (`RejectedAdmissionCount`, `LastRejectionReason`).
- The Warchief must respect capability/placement — the denied job either lands on a box that
  fits or fails visibly; it must never silently reroute to Ollama.
- Also run the inverse: a small job admitted on all three, proving denial is footprint-driven,
  not box-driven.

**2026-07-21 — Driver built (`Tools/Hv2SchedulingRunner`, PR #88), real run against the fleet.
CLOSED for the two-machine spread (6 GB deny / 8 GB admit); NewcorePC (16 GB) excluded — a
genuine Daemon-architecture constraint, not a scheduling gap.**

`NativeContextSize` is a per-worker-process startup config, not a per-job HIVE parameter, so
the "large footprint" and "small footprint" checks run as two separate fleet configurations of
the same machines rather than two job shapes against one running config (`--phase large|small`).
Also added `GET /hive/native-telemetry` on `HiveNodeServer` (`RejectedAdmissionCount`,
`LastRejectionReason`, VRAM totals) — this existed in-process but had no remote observability
surface on a headless worker before this.

**Calibration, computed before touching real hardware:** `OrcScheduler.EstimateRequiredBytes`
is `legacy(base+adapter file size) + 256 MB (CUDA overhead) + 384 MB (compute buffer) + kvBytes`,
where `kvBytes` scales linearly with `ContextLength`. Back-solving from HV-1's own observed
figures (base ≈1.881 GB, ctx=8192 → ~3.4 GB total ⇒ kv(8192) ≈ 894 MB) gave `ctx=40000` ⇒
≈6.77 GB total — denied on 6 GB, comfortable margin under 8 GB. Confirmed near-exact on real
hardware: the actual denial read **"Requires ~6.8 GB, only 5.6 GB available."**

**Large-context phase (ctx=40000), pinned per-worker via `ExcludedWorkerIds`:**
- **HardcorePC (6 GB): DENIED**, a real `RuntimeAdmissionDeniedException` surfaced as `status:
  "failed"` (this execution kind is structurally fail-closed — no Ollama fallback path is even
  reachable). Confirmed via `/hive/native-telemetry`: `RejectedAdmissionCount` 3→6 (exactly the
  3 retry attempts this run made), `LastRejectionReason: "Requires ~6.8 GB, only 5.6 GB
  available."` — the "correct numbers in the reason" bar, met.
- **HardcoreLaptopMSI (8 GB): ADMITTED**, completed normally, `Attestation.RuntimeName ==
  "NativeRoleRuntime"`.
- Evidence: `.orc/hv-2-lane/hv2_large_20260721_141745.json`.

**Small-context (inverse) phase (ctx=8192, already proven safe from HV-1), same two boxes:**
both completed normally — **the same HardcorePC that just denied at ctx=40000 admitted cleanly
at ctx=8192**, the direct proof that the denial above was footprint-driven, not "HardcorePC
always fails." Evidence: `.orc/hv-2-lane/hv2_small_20260721_141958.json`.

**Driver bug found and fixed mid-campaign:** the task-level `HiveTaskResult.ErrorMsg` the
Warchief actually sees is `HiveWorkerAgent`'s generic wrapper text ("native role runtime failed.
Phase 3B does not fall back.") — the `RuntimeAdmissionDeniedException`'s own detailed message
never reaches it, only the worker's local log does. The driver's first pass tried to classify
denial by matching "admission" in that wrapper text and got it wrong (`matchesExpectation:
false` on a genuinely-correct denial). Fixed: classify denial by task status alone (this
execution kind can't fall back instead of failing), and let the separate
`/hive/native-telemetry` check be the sole authority on whether it was specifically an admission
denial with correct numbers — which is exactly why that endpoint needed to exist in the first
place, not just as a nice-to-have.

**Real infrastructure gap found, not fixed (system-settings change, correctly out of scope for
an agent to make unilaterally): HardcorePC's inbound Windows Firewall doesn't allow port 7078
from NewcorePC's LAN address**, so the driver's own remote telemetry fetch times out — confirmed
this is general (even the pre-existing `/hive/info` times out the same way remotely, works fine
over loopback) and not a bug in the new endpoint. Worked around by fetching telemetry via `ssh
HardcorePC curl http://localhost:7078/hive/native-telemetry` instead and splicing it into the
evidence file with a note. A future HV-2+ run should either open that inbound rule (an explicit,
user-authorized action) or teach the driver an SSH-fetch fallback.

**NewcorePC (16 GB) excluded from this run — a real, separate finding, not a scheduling gap.**
Attempted to run `OrchestratorIDE.Daemon` locally on NewcorePC as Warchief+self-worker (to prove
the "fits 16 GB" case); this **regenerated NewcorePC's own HIVE identity** (the same DPAPI/
AES-GCM protector collision from the HV-1 campaign, this time on the box that had never run the
Daemon binary before — NewcorePC's warchief role had only ever run via `swarmcli`, whose
identity uses a different protector). Confirmed via `--show-identity`: new nodeId `e5333a93...`
vs. the `f083b993...` both remote workers still had on file. Unlike the HV-1 recovery, **this
one has no clean fix**: `OrchestratorIDE.Daemon`'s `HiveService.cs` never subscribes to
`OnPairingRequestReceived` and never calls `HiveNodeServer.EnableDevAutoApprove` — by design
(`Program.cs`'s own comment: "this daemon must always be the INITIATOR, never the responder,
until a headless approval path exists"), so a Daemon-hosted Warchief can **never approve an
incoming pairing request** the way `swarmcli --warchief --allow-fingerprint` can. The Daemon
architecture assumes it is always a remote headless *worker* managed by an interactively-running
GUI/swarmcli elsewhere, not something that can host the Warchief role for peers to pair against
unattended. Reverted: killed the Daemon, restarted NewcorePC's Warchief via
`swarmcli --warchief --no-run --allow-fingerprint` (unaffected — its identity was never
touched), which the workers already trusted from the HV-1 fix, and the two-machine run above
completed cleanly on the first real attempt afterward. **Filed as an open follow-up**: either
give the Daemon a headless pairing-approval mode (env-var-gated auto-approve, mirroring
`EnableDevAutoApprove`) or find another way to get a 16 GB box into the worker fleet without
running the Daemon as its own Warchief.

**HV-2 verdict: CLOSED for the 6 GB / 8 GB spread** (the decisive comparison — denial vs.
admission on genuinely different VRAM classes, with correct real numbers and real telemetry).
**The 16 GB "fits" leg is not yet run**, blocked on the Daemon pairing-approval gap above, not
on any scheduling defect — NewcorePC's own native execution was never in question (proven
extensively across Phase A-D). Cleaned up: both Daemon processes, local Warchief, remote scratch
workspaces and logs all stopped/removed.

### HV-3 — Model/adapter lifecycle across machines

- Sequential load → generate → dispose cycles per worker; residency (`ActiveCount`) returns to
  baseline between jobs; reservation behavior matches the documented decoupling (reservation
  persists with the loaded model; residency does not).
- Concurrent second role on the same worker — folds in the deferred Phase D "second concurrent
  role" increment: cross-role admission accounting proven inside one evidence-bearing run.
- Rebind/recycle: force a role recycle (the PR #79 `MarkRoleDegraded` path) remotely and prove
  the next job on that role gets a fresh, working executor.

**2026-07-21 — Attempted a real Context Fabric (CF-6) run over this same fleet, as a richer
multi-role exercise than synthetic marker-file jobs. Found and fixed a real Daemon gap along the
way; the CF-6 run itself hit a genuine model-capability limit, not a HIVE defect.**

`Tools/Cf6AcceptanceRunner` (the existing CF-6 acceptance harness) needs a Warchief with
`ArtifactStore`/`ModelStore` wired — `swarmcli --warchief` (used for HV-1/HV-2's Warchief role)
never sets these up, only `OrchestratorIDE.Daemon` does. Running the Daemon as NewcorePC's
Warchief hit the exact gap HV-2 already found: `HiveService.cs` never subscribes to
`OnPairingRequestReceived` and never calls `EnableDevAutoApprove`, so a Daemon-hosted Warchief
can never approve an incoming peer. **Fixed properly this time** (PR pending): added
`Hive:DevAutoApproveMinutes` (`HIVE__DEVAUTOAPPROVEMINUTES`), which opens
`HiveNodeServer`'s existing time-boxed dev re-sync auto-approve window at startup — the missing
headless approval path Program.cs's own comment says doesn't exist yet, using the mechanism
already built for headless fleet re-sync rather than inventing a new one. Both workers re-paired
against NewcorePC's Daemon identity cleanly through the open window. Note: NewcorePC's Daemon
identity changed *again* between HV-2 and this session (third time) — `MachineKey.Load()`'s
determinism across process restarts is now a suspected separate issue, not yet investigated.

**CF-6 reader stage: real native dispatch, real per-segment retries, zero fallback — but the
reader's structured JSON output was truncated, deterministically, for at least one segment.**
Claimed and executed by NEWCOREPC (`Attestation.RuntimeName == "NativeRoleRuntime"`, real
tok/s/TTFT), retried 3× per `MaxAttempts`, same truncation each time (consistent with
temperature 0 — a deterministic model output, not flaky infra): `Model response could not be
parsed as FabricEvidenceCard. Extracted: {"schemaVersion":..., "heading": "Sectio` (cut off
mid-string, well under the 4096-token `ReaderMaxTokens` budget — not a token-limit cutoff, the
model itself stopped generating early). **This is a model-capability finding, not a HIVE or
dispatch defect**: the fleet's pinned model (Dolphin3.0-Llama3.2-3B, chosen for HV-1/HV-2's VRAM
testing, not for CF quality) is not reliable at CF's reader JSON-extraction task — CF-7's own
GO gate used a materially larger Qwen3.5-9B, not this box's 3B fixture. No larger model is
currently on the fleet to retry with (checked: NewcorePC's model root has two similarly-sized 3B
GGUFs, no CF-capable size). **Not pursued further in that pass** — stopped after one confirmed
reproduction rather than burn more fleet time chasing a known model-capability mismatch. Cleaned
up: all three Daemon processes stopped, scratch workspaces/logs removed.

**2026-07-21 (same session, later pass) — resolved: downloaded the CF-7-proven model, real
multi-node CF-6 confirmation obtained.** User-approved acquisition of
`Qwen3.5-9B-Q4_K_M.gguf` (5.68 GB, SHA-256 `03b74727a860a56338e042c4420bb3f04b2fec5734175f4cb9fa853daf52b7e8`)
from `huggingface.co/unsloth/Qwen3.5-9B-GGUF` — the same model family CF-7's own GO gate used.
Hash-verified after download, placed at `F:\Ai\GarfChat\checkpoints\cf-test-models\` (NewcorePC)
and `C:\Ai\models-cf\` (HardcoreLaptopMSI, copied and independently re-verified, not assumed).

Single-node sanity check first (NewcorePC only): **15 of 16 segments succeeded cleanly** — a
dramatic jump from 0/16 with the small model, confirming the earlier finding was genuinely model
capability, not a HIVE defect. **Real multi-node run** (`--min-nodes 2`, NewcorePC +
HardcoreLaptopMSI, both running the Daemon with `Hive:DevAutoApproveMinutes` from this same
session's fix): reader claims fanned out across both real machines —
`read-00001/00008/00014` claimed by `HARDCORELAPTOPM`, the rest by `NEWCOREPC` — genuine
distinct-worker-node distribution, not a single box doing all the work.

**One segment (seg-006) still fails, deterministically, regardless of model or which node claims
it.** With the bigger model the truncation point moved further into the JSON (through
`schemaVersion`/`corpusId`/`documentId`/`segmentId`/`promptVersion`, cutting off inside
`"summary":"Section 0` — right as it would emit the ordinal's second digit, "6") but never
completes, on either NEWCOREPC or HARDCORELAPTOPM, across every retry. Consistent across two
materially different models and two different machines strongly suggests this is specific to
segment 6's own content or a narrow native-runtime/tokenizer edge case around that exact digit
sequence — not a capability or distribution problem. Not root-caused this session; flagged
precisely rather than guessed at. `Cf6AcceptanceRunner` treats any single reader failure as
fatal to the whole pipeline (verifiers/stitchers/reducer never ran), so this remains a partial,
not full, CF-6 acceptance pass — but the actual thing this exercise was for (real distributed
native Context Fabric execution across the HIVE, confirmed working) is solidly demonstrated:
15/16 real segments, two real machines, real evidence cards, zero fallback.

Evidence: `.orc/cf6-acceptance-qwen/` (single-node), `.orc/cf6-acceptance-qwen-multinode/`
(multi-node, partial due to seg-006). Cleaned up after: both remote Daemon processes and
NewcorePC's local Daemon stopped, scratch workspaces removed. The downloaded model itself was
kept in place (not deleted) on both machines — a real, reusable, hash-verified asset for future
CF work, not a throwaway.

**2026-07-25 — Driver built (`Tools/Hv3LifecycleRunner`), sequential phase PASSES on HardcorePC.
Still a PARTIAL: one worker only, concurrent-role phase not yet run, item 3 out of scope.**

**The driver** submits one work unit per campaign and awaits it to a terminal state before
submitting the next — submitting all N up front would let the Warchief overlap them and destroy
the very thing the phase measures. A `/hive/native-telemetry` sample is taken before the run and
after each cycle, and three checks are evaluated against those samples rather than asserted in
prose: `residency-returns-to-baseline`, `reservation-persists-between-jobs`, and
`fresh-conversation-per-job` (strictly increasing `ConversationsCreated` — what separates "a
fresh conversation per job" from "one conversation silently reused and never re-counted").

**Prerequisite closed first: residency had no remote observability.** The daemon wired
`NativeTelemetryProvider` to `GetReservationSnapshot()` alone, so `ActiveCount`/
`ConversationsCreated`/`Status` existed in-process and nowhere else — the same shape of gap HV-2
found for admission counters, one level down. `/hive/native-telemetry` now carries a `residency`
array. Kept strictly additive (reservation fields keep their top-level position) because
`Tools/Hv2SchedulingRunner` binds them there and HV-6 re-runs that driver unattended 3×.
`AdapterRoleResidency.Binding` is projected to display names rather than serialized whole: it
carries absolute GGUF paths and this endpoint is unauthenticated.

**Two real fleet defects found before any evidence could be collected**, both worth recording
because they cost more time than the phase itself:

1. **The Warchief was not running at all**, and the `swarmcli` binary in `bin/Release` was dated
   2026-06-22 — a month stale, predating the `req.IsLocal` local-trust exemption added
   2026-06-24. Every submission, including a plain `curl` to `/hive/info`, returned
   `401 missing HIVE auth headers`. Stale binaries on the fleet are a live evidence-validity
   hazard, hit twice in one session (this, and HardcoreLaptopMSI's stale `bin/Debug` output).
2. **`HIVE__WARCHIEFNODEID` was never set in the worker start scripts.** Re-pairing alone did NOT
   restore claiming — jobs submitted fine and sat unclaimed. `swarmcli --help` documents this
   flag as what avoids "IP-vs-hostname shared-secret lookup misses"; without it the worker's
   signed claims never matched. Added to HardcorePC's `start-worker.bat` (`.bak` retained).
   NewcorePC's Warchief identity had also churned again (`fbf93f48…`, vs the `f083b993…` the
   workers had on file) — the recurring DPAPI/AES-GCM collision, now on its fourth observation.

**Real defect found BY the phase, fixed here: the reservation snapshot published impossible
numbers.** The first passing run reported `reservedBytes` 11.04 GB against `totalBytes` 6.44 GB
with `availableBytes` pinned to 0, the excess matching that job's `est_vram=4.6GB` exactly.
`GetReservationSnapshot` computed `baseline.ReservedBytes + ledger.Sum()`, but the daemon's
budget provider is a live whole-GPU probe whose `ReservedBytes` already counts every resident
model — the sibling of the HV-1 double-count, in the reporting path instead of the admission
path. Admission was never affected (which is why jobs kept being admitted while telemetry lied),
making it purely a diagnosability defect — and §6 requires consistent telemetry and
diagnosability across machines, so it had to be right before HV-5 sweeps these values fleet-wide.
Fixed by taking the MAX of the two rather than the sum (live probe wins when present; the static
`ReservedBytes: 0` fallback budget lets the ledger win). Regression test confirmed red then
green; full `RuntimeOrchestrator` suite 13/13.

**Decisive run after the fix — HARDCOREPC, 3 sequential cycles, all three checks PASS:**
- `residency-returns-to-baseline`: ActiveCount back to 0 after all 3 cycles.
- `reservation-persists-between-jobs`: baseline `432013312` → `[5589043712 ×3]`, held across
  every gap and now **below** `totalBytes` — and 5589043712 B = 5.20 GiB matches the 5212 MiB
  `nvidia-smi` reported on the box at that moment, so the telemetry and the hardware now agree.
- `fresh-conversation-per-job`: `ConversationsCreated = [1, 2, 3]` from a clean worker process.

All 3 jobs `completed`, `claimedBy=HardcorePC`, `Attestation.RuntimeName == "NativeRoleRuntime"`,
zero fallback. Evidence: `.orc/hv-3-lane/hv3_sequential_20260725_111936.json`.

**2026-07-25 (later) — both fleet workers restored, sequential phase PASSES on BOTH machines;
concurrent-role phase FAILS on a real product defect.**

`HardcoreLaptopMSI` was brought back into the fleet first. Two problems, both silent: its
`NativeModelRoot` contained a subdirectory `2f` holding a 270 MB onboarding GGUF alongside the
4.68 GB coder, and `ModelDepot.Scan` recurses and binds the **smallest** asset — so that worker
had been running a 270 MB model while its config, its logs and this plan all said
qwen2.5-coder:7b. Any evidence collected from it before this would have been invalid without
looking wrong. The daemon's `GGUF assets: N` startup line is the tell; anything but 1 means the
root is not model-specific. Stray directory moved aside; `HIVE__WARCHIEFNODEID` added to both
workers' start scripts.

**Sequential phase, both machines, 3 cycles each — PASS (6/6 jobs native, zero fallback).**
Evidence: `.orc/hv-3-lane/hv3_sequential_20260725_112450.json`. Read together with the
cold-start runs (`…111936` HardcorePC `432013312` → `5589043712`; `…112316` laptop `45088768` →
`5589043712`), the pair gives the full decoupling picture: the reservation *appears* on first
load and then *holds* across every subsequent gap, while residency returns to zero after each
job.

A driver bug was found and fixed here too: `reservation-persists-between-jobs` asserted
`after > baseline`, which only holds from a cold worker — against a warm one the baseline sample
already includes the resident model. The same correct behavior produced opposite verdicts purely
from starting state (warm HardcorePC FAILED, freshly-started laptop PASSED) until the check was
restated as "non-decreasing and at least baseline, with a loaded model actually observed."

**Concurrent-role phase — FAILS, and the cause is a real admission defect, not the harness.**
The `Coder` job completes on both boxes; the concurrent `Researcher` job is denied on both,
fail-closed with no Ollama substitution (itself a clean HV-5 data point). The worker-local log
carries the detail the Warchief never sees:

> `Runtime admission denied for Researcher (qwen25-coder-7b.gguf, lane Background): Requires`
> `~5.2 GB, only 0.0 GB available. Budget total=6.0 GB, reserved=10.3 GB, available=0.0 GB.`

**`reserved=10.3 GB` on a 6.0 GB card** — the same double-count as the snapshot defect above,
but in `EnsureAdmitted` this time. The HV-1 fix credits back only *this* role's prior
reservation; the live nvidia-smi probe in `baseline.ReservedBytes` already counts **other** roles'
resident models as well, so those are charged twice (`5.7 + 4.6 = 10.3`). Compounding it: both
roles resolve to the SAME GGUF and `SessionManager` keeps a single shared base load
(`CanReuseCurrentSession` short-circuits — no reload, no extra VRAM), yet
`OrcScheduler.EstimateRequiredBytes` charges a full fresh-load 5.2 GB for the second role anyway.
So a second role that would actually cost only its context is billed as an entire extra model,
against a baseline that has already counted the first one twice.

Honest reading: a 6 GB card genuinely cannot hold two full 4.6 GB copies, so *denial* is not
self-evidently wrong on this hardware — but the number is physically impossible, and because of
the shared-base-model reuse the true footprint was never actually evaluated. **Deliberately not
fixed in-session:** admission is the safety gate, and it deserves a reviewed change rather than a
late one. Filed as an open follow-up.

**HV-3 verdict: NOT CLOSED — sequential lifecycle proven on both machines, concurrent-role
blocked on an open admission defect.** Outstanding:
(a) the concurrent second-role phase, which cannot pass until the cross-role admission
double-count above is fixed — this is a genuine product blocker, not a missing test; (c) item 3
(forced role recycle across machines), which is
**deliberately out of scope for this driver** and recorded in every evidence file's
`uncoveredItems` — `MarkRoleDegraded` is reachable only from the runtime's own NoKvSlot handling,
and a remote trigger is a MUTATION needing an authenticated control endpoint plus its own
security review, not something to attach to a read-only campaign driver. Since §6's criterion is
lifecycle behavior *across machines*, a one-box pass cannot be promoted to a closure.

**Throughput note (relevant to the §6 gate, not to HV-3):** the live worker log for these jobs
reads `tok/s=8.3`, `13.1`, `10.2` — the fleet's "~7 tok/s" figure, observed directly in the HIVE
path. Every one of those jobs is `steps: 1` with `completion_tokens: 13` against a 384-token
prompt. See the throughput-gate finding: real decode on this same box is 42.5 tok/s.

**2026-07-27 — campaign halted on a HIVE authentication defect, not a runtime one. No worker
can lease; every phase stalls with jobs `pending`.**

Recorded here because the next person to pick this up will otherwise repeat two sessions of
black-box guessing. The runtime work is sound — the admission fix and the sequential phase both
passed on real hardware before this — but the fleet cannot dispatch at all.

**The symptom chain, and how much of it was misdiagnosis.** A dead Warchief looks exactly like a
HIVE fault from every other vantage point. Three separate causes were mistaken for HIVE bugs
before being pinned down:
1. `swarmcli --warchief --timeout N` is a **self-terminate**, not an idle timeout — the process
   exits mid-campaign printing a tidy `Shutting down...`. A 7200s value ended a session at
   exactly the two-hour mark.
2. The Warchief **dies on Ctrl+C from the launching shell's console**, so any interrupted command
   killed it. `warchief.log` ends in a bare `^C` every time. Fixed by giving it its own console
   (`start-warchief.bat`).
3. `warchief.log` and `worker_*.log` are **fully buffered** — they show only the startup banner
   until the process exits. An apparently-empty log was read as "the worker never leased" and a
   healthy run was killed on the strength of it. Use `GET /hive/native-telemetry` for live state.

**The real blocker: `HMAC mismatch`.** Worker rejection reasons were invisible until they were
surfaced (`HiveAuthMiddleware.ValidateCore` has always distinguished unknown-peer / stale-secret /
clock-skew / replay / HMAC-mismatch, and `HiveTaskQueue` returns the verdict in the 401 body — the
worker logged only the status code and discarded it). With that logged, the answer is unambiguous
and narrow. Ruled out by direct inspection: peer enrolment (both stores hold the other side, role
Worker, not revoked, secrets present, written by the *same* ceremony one second apart), clock skew
(fleet within 2s), `MachineKey.Load()` determinism (env → file → generate-once, so both HardcorePC
processes read the same AES-GCM key), and a DPAPI decrypt failure on the Warchief (that path
returns `(false, null)` and would report "no shared secret for peer" instead).

**So both sides finish a mutually-successful pairing ceremony holding different shared-secret
bytes.** That is a crypto/pairing defect, and the next step is a focused unit test over the two
derivation call sites (`HiveNodeServer`'s approve path from `req.ExchangePublicKeyDer`, and
`HivePairingClient.CompletePairing` from the response) asserting both produce identical bytes
under fixed key material — not further live fleet poking.

**Also found: `--leave-hive` is a footgun as shipped.** It clears `HiveId` correctly, but the
daemon **founds a brand-new hive on its next normal start**, so a worker "recovered" this way ends
up in a *different* hive than the Warchief and is then permanently refused by §4.3 — strictly
worse than before. The ordering that works is `--leave-hive --yes` and `--pair` chained in one
invocation, **before any daemon start**. Filed as an open follow-up.

**2026-07-27 (later) — auth investigation suspended. Read this before resuming; several
"findings" below are environment artifacts, not product bugs, and re-deriving them costs hours.**

**What is solid (test-backed, keep):**
- Secret derivation and salting are correct — `HivePairingSecretDerivationTests`, 4/4. Both call
  sites pass node ids in opposite order; `XorNodeIds` is genuinely commutative, padding branch
  included.
- Sign → validate round-trips for the real request shapes — `HiveAuthSignRoundTripTests`, 4/4,
  covering `GET /hive/models` (empty body), the POST heartbeat (JSON body), and a query string.
  The negative control pins the exact reason string `HMAC mismatch`.
- Worker rejection reasons are now logged (`HiveAuthMiddleware` always distinguished
  unknown-peer / stale-secret / clock-skew / replay / HMAC-mismatch, and `HiveTaskQueue` returns
  it in the 401 body; the worker was discarding it).
- Auth IS validated before endpoint routing (`HiveTaskQueue` ~L345 vs routing ~L373), so a 503
  from `/hive/models` genuinely means the signature was accepted.

**What turned out to be an artifact of the debugging environment, NOT a HIVE defect:**
- **There are two AppData views on NewcorePC.** `%APPDATA%\TheOrc\` and
  `…\AppData\Local\Packages\Claude_*\LocalCache\Roaming\TheOrc\` each hold their own
  `hive-peers.json`, `hive-identity.json` and `machine.key`. Tooling launched under the packaged
  app writes through the redirected view; other tooling writes the real one. Every "I cleared the
  peer store and it still says already_paired" observation in this campaign is suspect for that
  reason, and the previously-unexplained note in `HiveNodeServer` — that the on-disk file "was
  repeatedly NOT sufficient to explain a stuck already_paired" — is very plausibly the same thing
  rather than a phantom in-memory entry. **Check which file a given process actually reads before
  concluding anything about trust state.**
- A **running Warchief rewrites `hive-peers.json` wholesale** from its in-memory list, so the
  store may only be edited while it is stopped.
- The apparent "attached Warchief works, detached does not" split was a **port conflict**: a
  previously-launched Warchief was still holding 7078/7079, so the newly-started one failed to
  bind and the old process answered. Always confirm exactly one `swarmcli` is running.

**Genuinely open, unproven either way:** whether the responder's shared secret survives a
persist/reload correctly. The observation that motivated it (auth works pre-restart, fails
post-restart) was collected while the two-AppData confound was active and has NOT been re-checked
under controlled conditions. Do not treat it as established.

**Recommended way to resume:** pin one AppData view explicitly, assert exactly one Warchief
process, then re-run the pair → auth check. Only if it still fails is there a product bug to chase.

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
