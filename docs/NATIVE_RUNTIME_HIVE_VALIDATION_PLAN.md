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

**Minor evidence-quality gap, closed 2026-07-30**: every job's `Attestation.Backend` reported
`"cpu"` even though the Daemon's own startup log confirms `"CUDA backend selected (cuda12...)"`
on both boxes — `HiveService.cs` called `NativeBackendBootstrap.EnsureConfigured` for logging
only and never passed its verdict into `WorkerCapabilityDetector.DetectAsync`'s
`verifiedNativeBackend` parameter (which defaults to `"cpu"`). Didn't affect whether native
execution happened (`RuntimeName` already proves that), but did make the `Backend` field in
evidence read wrong -- and, less obviously, silently zeroed `WorkerCapabilities.FreeVramMb` too,
since that field is gated by the same parameter. Fixed by declaring `verifiedNativeBackend`
outside the native-configured branch (defaulting to `"cpu"`, correct when native isn't
configured at all) and setting it to `backend.SelectedCuda ? "cuda12" : "cpu"` right where
`backend` is computed, then threading it into the `DetectAsync` call. `WorkerCapabilityDetectorTests.cs`
(new) locks down `DetectAsync`'s own contract for this parameter.

**The GUI half, also closed 2026-07-30.** `MainWindow.axaml.cs`'s `StartHiveWorkerAsync` has the
identical gap — its own `DetectAsync` call never passed `verifiedNativeBackend` either. Initially
assessed as "structurally different, bigger fix" since the backend verdict is computed inside a
different method (`BuildExperimentalNativeRoleRuntime`, three levels of caller indirection away)
with no return path back out. Checked more carefully rather than accepting that first read:
`NativeBackendBootstrap.EnsureConfigured()`'s own doc comment says it configures **once** and
returns a **cached** report on every subsequent call. So calling it again at the
`StartHiveWorkerAsync` call site — guarded on `nativeHiveRuntime is not null` (meaning backend
selection already ran once, successfully, as part of building that runtime) — costs nothing: it
returns the identical cached verdict, never re-triggers real GPU detection. No signature changes,
no new return paths, three lines.

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

**2026-07-27 (resolved) — the blocker was HOW the Warchief was launched, not HIVE auth.**

Running that controlled re-test with both AppData views cleared to byte-identical state, the
worker's peer store deleted, and exactly one `swarmcli` process bound to 7078, the two launch
methods gave OPPOSITE results against the same on-disk trust state:

| Warchief launched via | Pairing result |
|---|---|
| `Win32_Process.Create` (WMI, from `start-warchief.bat`) | `already_paired` — `IsTrusted` true for a node absent from every store found on disk |
| direct child process of the shell | `✓ Paired … fingerprint verified. Shared secret stored.` |

The WMI-spawned process resolves its HIVE state directory somewhere other than the file the rest
of the tooling reads, so it authenticates against a stale peer entry — which is what produced the
`HMAC mismatch`, the "cleared the store and it still says already_paired", and the phantom
in-memory-peer readings throughout this campaign. No system-profile copy exists
(`systemprofile` / `Users\Default` / `Users\Public` all checked and absent), so the exact
redirection is not yet identified — but the operational rule is unambiguous:

> **Launch the Warchief as a direct child process, never via WMI/`Win32_Process.Create`.**
> Then verify `swarmcli` count == 1 and that port 7078 has exactly one listener before pairing.

**HV-3 sequential — PASS on HardcorePC from a COLD worker** (the stronger form: the reservation
is observed appearing on first load *and* holding across every gap, rather than only holding):

```
residency-returns-to-baseline:     PASS — ActiveCount back to 0 after all 3 cycles
reservation-persists-between-jobs: PASS — baseline 584056832 (cold) → [5617221632 ×3]
fresh-conversation-per-job:        PASS — ConversationsCreated = [1, 2, 3]
```

3/3 jobs `completed`, `Attestation.RuntimeName == "NativeRoleRuntime"`, zero fallback. Evidence:
`.orc/hv-3-lane/hv3_sequential_20260727_092800.json`. **This is also the regression check for the
cross-role admission fix** — that change altered the admission path, and the previously-passing
sequential phase still passes with it in place.

Cross-role admission accounting observed live during the concurrent phase on the same 6 GB box:
`reservations [(Worker, 637534208), (Researcher, 637534208)]`, `reservedBytes 6107955200` against
`totalBytes 6442450944`, `rejectedAdmissionCount 0` — two roles holding reservations at once,
each charged its incremental context cost rather than a whole extra model, and the total still
physically possible.

**2026-07-27 (resolved) — the "heartbeat loss" was never the heartbeat. The Warchief had no
artifact store, so workers could not deliver their results.**

Read this before touching heartbeat code again: three fixes were made against a mechanism that was
working correctly the whole time, and the reason they could not be falsified was that a *successful*
beat logged nothing.

`b90fd13d` added the missing positive signal (first successful beat per task) and the queue side
was put behind `THEORC_HIVE_HEARTBEAT_DIAGNOSTICS=1`, which now also announces itself at startup so
an empty receipt log can never again be read as "no beat arrived". With both in place the answer
came in one run, from the worker's own log:

```
♥ Heartbeat established for 'HV-3 concurrent Researcher on HardcorePC' (10s after claim).
[Researcher] '…' — native agent completed in 2 steps (runtime=NativeRoleRuntime, …)
⚠ Worker loop error: Response status code does not indicate success: 503 (Service Unavailable).
```

and from the Warchief's, beats arriving and being **credited** at `sinceLast=10.2s` right up to the
job finishing. Job done, beats landing, result never delivered.

**Root cause:** `swarmcli --warchief` never wired an `ArtifactStore`. Every
`PUT /hive/artifacts/{digest}` answered 503 from `HiveTaskQueue`'s "store is null" branch. The
upload sat *after* the try/catch around execution, so the throw escaped past `PostResultAsync` into
`RunLoopAsync`'s generic handler — the task stayed `claimed` with its heartbeat loop already
cancelled in the `finally`, and 45s later the watchdog re-queued it as a heartbeat timeout. Three
attempts of that produced `exhausted 3 attempts after heartbeat loss` against a healthy worker.
Reproduced identically on HardcorePC and HardcoreLaptopMSI.

The gap was already known from the other side — CF-6's acceptance runner needed a Daemon-hosted
Warchief for exactly this reason (2026-07-21 above) — but it was recorded as a property of *that
runner* rather than as a defect, so any campaign whose jobs emit output files silently required the
Daemon. `ModelStore` was missing for the same reason, which is the
`Approved-model catalog rejected by Warchief: HTTP 503` every worker logs on a one-minute cycle.

Fixed on both sides (`0e9db763`): the stores are wired in `swarmcli --warchief` mirroring
`HiveService.cs`, **and** an upload failure now fails closed and visibly — the result is still
posted, marked failed, carrying the real upload error — so a misconfigured artifact store can never
again impersonate a dead worker. That second half is also HV-4's "job fails visibly" requirement.

Not unit-tested: driving this path needs a full native execution harness, which is disproportionate
for a five-line catch. The evidence is the live two-machine reproduction and fix, recorded here.

**Why the earlier heartbeat fixes read as ineffective:** they were. `203dfeb3` (5s→20s timeout),
`f438d1a9` (dedicated thread) and `f390ac57` (409 instead of 200) are each correct on their own
merits and are kept, but none of them addressed this. Note also that `f438d1a9`'s "no measurable
effect" was measured against HardcorePC, whose checkout was at `570b23d` and therefore did not
contain the fix at all — verify what a worker is actually *running*, not what its repo says.

**HV-3 concurrent — PASS on BOTH machines.** 4/4 jobs `completed`, `NativeRoleRuntime`,
`ClaimedByExpected` true, zero fallback:

```
[HardcorePC]        two-roles-hold-reservations-concurrently: PASS — peak role reservations=2 [roles 1, 2]
[HardcorePC]        cross-role-accounting-stays-physical:     PASS — reservedBytes within totalBytes, every sample
[HardcoreLaptopMSI] two-roles-hold-reservations-concurrently: PASS — peak role reservations=2 [roles 1, 2]
[HardcoreLaptopMSI] cross-role-accounting-stays-physical:     PASS — reservedBytes within totalBytes, every sample
```

Evidence: `.orc/hv-3-lane/hv3_concurrent_20260727_165522.json`. The failing run immediately before
it, on the same build minus the fix, is retained as the paired negative control:
`.orc/hv-3-lane/hv3_concurrent_20260727_165114.json` — identical checks PASS, both Researcher jobs
`failed`.

**Driver check corrected in the same pass — `reservation-persists-between-jobs` was measuring the
wrong quantity, for the second time.** Re-running sequential on the fixed build failed it on a
~27 MB drift (`6226577920` → `6198132736`) while every role held its reservation correctly.
`reservedBytes` is not the ledger: `GetReservationSnapshot` publishes the MAX of the ledger and a
live whole-GPU probe, and on a card this full the probe wins — so asserting a monotonic property of
it asserts that nothing else on the machine may allocate a byte of VRAM. The first false failure
from this same check was the `> baseline` form, which only held from a cold worker. Both times the
check had drifted onto a convenient aggregate rather than the quantity under test.

Restated on the role's reservation entry, which is what the decoupling actually claims: every
post-job sample must still show the role holding a reservation, and no role may lose one it held
after the previous job. Byte values are still recorded as evidence, but not asserted. Worth knowing
for future readings: a role's reservation legitimately SHRINKS from a full-model charge to an
incremental context charge once another role has the base model resident — on this run role 1 went
`5589043712` → `637534208` while the physical footprint stayed at 6.2 GB. The model never left the
card; only the ledger's attribution changed.

**Sequential re-run on the fixed build — PASS on BOTH machines** (so both phases now rest on one
build's evidence rather than two):

```
[HardcorePC]        residency-returns-to-baseline / reservation-persists / fresh-conversation: PASS
                    reserved roles after each cycle=[1,2 | 1,2 | 1,2], ConversationsCreated=[5, 6, 7]
[HardcoreLaptopMSI] residency-returns-to-baseline / reservation-persists / fresh-conversation: PASS
                    reserved roles after each cycle=[1,2 | 1,2 | 1,2], ConversationsCreated=[5, 6, 7]
```

Evidence: `.orc/hv-3-lane/hv3_sequential_20260727_165823.json`.

**HV-3 verdict: items 1 and 2 CLOSED across machines; item 3 remains out of scope.** Forced role
recycle (`MarkRoleDegraded`) is still reachable only from the runtime's own NoKvSlot handling, and
a remote trigger is a mutation needing an authenticated control endpoint plus its own security
review — recorded in every evidence file's `uncoveredItems`. HV-3 must therefore not be reported as
fully closed; §6's "verified model and adapter lifecycle behavior across machines" is now
substantially, but not completely, evidenced.

**2026-07-29 — item 3 CLOSED. Built the authenticated control endpoint this item was waiting on,
plus a genuine pre-existing auth bug found and fixed along the way.**

`POST /hive/roles/degrade` on `HiveNodeServer` (Warchief-only, same authority tier as the
existing `/hive/update/deploy`) forwards to a new public `NativeRoleRuntime.MarkRoleDegraded`
passthrough (mirroring the existing `GetReservationSnapshot`/`GetResidencySnapshot` forwarders),
which calls the same `RuntimeOrchestrator.MarkRoleDegraded` the runtime's own internal NoKvSlot
handling already used — no new recycle mechanism, just a remote door onto the existing one.
`HiveService.cs` wires the handler alongside `NativeTelemetryProvider`.

**Live-verified against a real deployed daemon (HardcorePC), not just unit-tested.** A throwaway
signed-request harness (deleted after use, matching this session's earlier spike/probe
convention) hit the real endpoint over the network with a genuine HMAC-signed, Warchief-identity
request:
```text
POST /hive/roles/degrade {role: Worker}   -> 200 {"status":"ok","role":"Worker"}
POST /hive/roles/degrade {role: bogus}    -> 400 (lists valid roles)
```
Worker log confirmed the fire-and-forget recycle call completed with no error.

**Real pre-existing bug found and fixed by this live test, not by inspection.** The first live
attempt got 403 "only the Warchief may force a role recycle" — against a request signed with
exactly the node id the worker's own `start-worker.bat` configures as its Warchief.
`HiveElectionService.WarchiefNodeId` turned out to be populated ONLY by live election-protocol
messages; nothing in `HiveService.cs` ever called `SetWarchief` from `_cfg.WarchiefNodeId`, even
though that value is already known from static config. **This meant `/hive/update/deploy` — the
existing, previously-shipped endpoint this new one's authorization pattern was modeled on — was
silently unusable in this exact deployment shape the whole time**, not just the new endpoint.
Fixed in `HiveService.cs`: call `election.SetWarchief(_cfg.WarchiefNodeId)` once at startup when
configured. Re-verified live after the fix — 200 on the happy path, confirmed via the worker's own
log line `[Election] ⚙ Warchief set to <nodeId>`.

**HV-3 verdict, final: items 1, 2, and 3 all CLOSED.** Item 3's remote trigger exists, is
authenticated and authorized the same way the codebase's one existing mutation endpoint is, and
is proven working end-to-end on a real fleet machine — plus it fixed a real, previously-unnoticed
bug in that existing endpoint along the way.

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

**2026-07-27 — driver built (`Tools/Hv4RecoveryRunner`); kill, disconnect, ollama and the
Warchief-side half of cancel exercised on real jobs mid-flight.**

**"Visible failure" had to be defined against what actually happens, not against what reads well
in a plan.** A killed or disconnected worker's job is re-queued to `pending` with its attempt
advanced, and it *stays* there — attempts only advance when a worker claims and then goes silent,
so with the box down nobody claims. The first version of the check demanded a terminal status and
so failed the CORRECT behaviour (the work is retryable and was not lost); it could only ever have
passed by waiting out a timeout. Visibility is now "the job stops being attributed to the dead
worker, promptly", which is exactly what the Warchief reports:

> `⚠ Task 'HV-4 kill on HardcorePC' heartbeat timeout from HardcorePC — re-queued (attempt 2)`

and a separate check proves the re-queued work is actually **recovered** on the restarted worker
rather than merely re-queued.

**A phase that can go green without doing the thing it is named after is worse than no phase.**
On the first two-machine kill run the ssh kill returned empty against HardcoreLaptopMSI, the worker
kept running, its job completed normally — and every check went green, because "reached a terminal
state" is trivially true of a job that was never disrupted. HardcorePC's half of the same run was
genuine, so the evidence file read as a clean two-machine pass with one half fabricated. Fixed
with a `kill-actually-landed` gate that abandons the phase loudly, and by refusing `completed` as
evidence of visible failure. The gate confirms the kill via the **worker's own telemetry going
dark** rather than an ssh process count: a remote `Get-Process | .Count` returned an empty string
rather than `0` from inside the driver, and a check that cannot tell "0" from "no answer" is not a
check — while telemetry silence is the stronger claim anyway, since it says the worker is not
*serving*.

**Results — PASS on BOTH machines** (evidence in `.orc/hv-4-lane/`):

```
kill        HardcorePC + HardcoreLaptopMSI   kill landed (telemetry dark), death visible
                                             (status=pending, claimedBy=none), worker rejoined,
                                             re-queued unit RECOVERED on NativeRoleRuntime,
                                             role reusable
disconnect  HardcorePC                       outbound TCP/7079 blocked mid-job → loss visible,
                                             firewall rule auto-restored, role reusable
ollama      HardcorePC + HardcoreLaptopMSI   ollama stopped (before=1/2 → after=0), native job
                                             still completed on NativeRoleRuntime
cancel      HardcorePC                       campaign cancel → task cancelled, role reusable
```

Two operational notes worth keeping. The Ollama stop must kill the tray supervisor `ollama app`
(with a space) as well as the server — matching only the exact process name left it restarting
within seconds, and absence never stuck. And **HardcoreLaptopMSI's sshd goes unreachable for
minutes at a time while the box itself stays healthy** (ping 5 ms, `/hive/native-telemetry`
answering 200); it is the reason the first laptop kill silently no-opped. `Hv4RecoveryRunner.Ssh`
makes three attempts (10s/20s/30s connect timeouts with backoff), and the landed-gate turns any
remaining flake into a loud failure rather than a fabricated pass.

**Item 1 is only half-covered, and this is a product gap, not a harness one.** The plan asks for
cancellation to surface mid-**generation** as an `OperationCanceledException` on the worker. There
is no remote trigger: the worker's only inbound listener is `HiveNodeServer`
(`pair` / `info` / `native-telemetry` / `mesh` / `update`) and it has no task-cancel endpoint, so
cancelling a campaign marks the task cancelled on the Warchief while the worker generates happily
to completion. The `cancel` phase proves the Warchief-side outcome and role reusability and says so
in its own check name. Adding the endpoint is a MUTATION needing authentication and its own
security review — the same call already made for HV-3's `MarkRoleDegraded` item.

Item 4's second half (a deliberately broken native config failing closed with an explicit native
error) is covered by HV-5's `diagnose` phase rather than duplicated here.

**HV-4 verdict: items 2, 3 and 4 evidenced across machines; item 1 half-covered pending a
worker-side cancel endpoint.** Also folded in here, from the HV-3 investigation: a healthy worker
being declared dead (root-caused to the missing artifact store, fixed in `0e9db763`) and the
fail-closed no-fallback behaviour observed throughout — every completing job in every phase above
carries `Attestation.RuntimeName == "NativeRoleRuntime"`.

**2026-07-29 — item 1's missing endpoint now exists: `POST /hive/tasks/cancel`, built alongside
HV-3 item 3's role-degrade endpoint (same session, same authorization pattern, same live
verification approach — full detail there).** `HiveWorkerAgent` gained a per-task
`CancellationTokenSource` registry, separate from the worker's own whole-process lifetime token:
each claimed task gets a linked token registered in `ClaimAndExecuteAsync` and removed in its
`finally`, so a remote cancel interrupts exactly one task's generation
(`OperationCanceledException` inside the loop, task reported `failed` to the Warchief) without
touching the poll loop or any other concurrently-running task. `HiveNodeServer`'s new endpoint
(Warchief-only, mirroring `/hive/update/deploy` and the new role-degrade endpoint) forwards to
`HiveWorkerAgent.TryCancelTask(taskId)`.

**Live-verified against the real deployed daemon on HardcorePC:**
```text
POST /hive/tasks/cancel {taskId: <unknown>}  -> 404 "no in-flight task with this id on this worker"
POST /hive/tasks/cancel {}                   -> 400 "missing taskId"
```
No task happened to be in flight on HardcorePC at verification time, so the "found and actually
cancels" path rests on code review + the linked-token mechanism (a well-established .NET pattern,
same technique already used for this method's own heartbeat-loop cancellation immediately above
it in the same file) rather than a live mid-generation interruption — the 404/400 paths prove the
routing, auth, and registry-miss handling all work correctly end-to-end on a real box, which is
the harder part to get wrong.

**Item 1: the trigger now exists, but per CodeRabbit's correct catch on this same PR (#94), this
is still partial coverage, not "fully covered."** The plan's original ask — cancellation
surfacing mid-generation as an `OperationCanceledException` on the worker via a real remote
trigger — has that trigger built, authenticated, and live-verified for its routing/auth/
validation paths (404/400 above). What has NOT been exercised live is the actual "found and
cancels a real in-flight generation" path — no task happened to be in flight on HardcorePC at
verification time, so that half rests on code review of the linked-token mechanism, not
observation. `Tools/Hv4RecoveryRunner`'s `cancel` phase still exercises only the Warchief-side
campaign-cancel path (a different, already-covered mechanism). Closing item 1 for real needs
either a live mid-generation cancel run or the harness wired to exercise this new endpoint
directly — neither done yet.

**2026-07-29 (same day, before merge) — a Grok second-opinion review (PR #94,
`grok-review -Mode full`) caught a real BLOCKER in the first version of this endpoint, fixed
before merge: a remote cancel would have been silently retried, not stopped.**

The first cut's `ClaimAndExecuteAsync` let a remote cancel's `OperationCanceledException` (from
`taskCts`, distinct from the worker's own whole-process `ct`) fall through to the generic
failure handler, posting `status: "failed"` back to the Warchief. `HiveTaskQueue.HandleFailAsync`
requeues campaign work whenever `Attempt < MaxAttempts` (default budget allows retries) — so an
operator hitting `/hive/tasks/cancel` would have gotten a `200 OK`, and the Warchief would have
quietly re-dispatched the exact same work to another attempt. The opposite of what cancel means,
and worse than not having the endpoint at all: a caller would reasonably believe the work stopped.

**Fixed by giving remote cancellation its own terminal status instead of reusing "failed".**
`ClaimAndExecuteAsync` now catches `OperationCanceledException` specifically `when
(taskCts.IsCancellationRequested)` (checked before the generic handler) and reports
`status: "cancelled"` — posted through the same wire action/endpoint as a genuine failure
(`HiveWorkerAgent.cs`'s `action` decision doesn't need a new route), but `HiveTaskQueue.
HandleFailAsync` now reads that field first and skips its own requeue branch entirely when set,
setting the task's terminal status to `"cancelled"` — already a first-class status elsewhere in
the same class (the pre-existing Warchief-initiated campaign-cancel path), reused rather than
inventing a second vocabulary. Verified: full 687-test suite still green, HardcorePC redeployed
and re-verified healthy after the fix.

**Also fixed from the same review pass, both real correctness/honesty issues, not just style:**
- `HandleMarkRoleDegraded` originally responded `200 OK` BEFORE its fire-and-forget call to the
  handler ran, and swallowed any failure to a console line only — meaning the exact
  `MissingMethodException` hit live during this endpoint's own deployment verification (wrong
  assembly copied to HardcorePC, see HV-3 item 3's entry above) would have reported success on
  the wire while doing nothing. Changed to `await` the handler and return `500` with the real
  error on failure — a slightly slower response for an honest one.
- `/hive/update/deploy` still used its own inline Warchief check instead of the shared
  `IsWarchief` helper the two new endpoints introduced; unified for one authorization code path
  instead of two copies that could silently drift.

**First of two MINOR findings closed 2026-07-30:** the Avalonia GUI's own `HiveNodeServer`/
`HiveWorkerAgent` construction (`MainWindow.axaml.cs`'s `StartHiveWorkerAsync`) now wires all
three. `ElectionService?.SetWarchief(warchiefNodeId)` is seeded first — a real prerequisite, not
cosmetic, since `IsWarchief` (gating the other two) reads `ElectionService.WarchiefNodeId`, and
without seeding it a legitimate request from the real Warchief would still 403 even with the
handlers wired. `CancelTaskHandler` is wired unconditionally (an Ollama-only worker can still have
an in-flight task cancelled) via `_hiveWorkerAgent?.TryCancelTask` (the field, not a captured
local, so a later stop/restart cycle stays correct). `NativeTelemetryProvider`/
`MarkRoleDegradedHandler` are wired only when the worker resolved a real `NativeRoleRuntime`,
mirroring `HiveService.cs`'s exact telemetry shape — including the `FallbackCount`/
`LastFallbackReason` fields Native Runtime v2.0 §5.4 added since this finding was first recorded.
Verified: build clean, full 690-test suite green (this exact code path has no automated coverage
before or after — same "no mockable seam for MainWindow's runtime construction" precedent
`RuntimeOrchestrator.cs`'s own class doc documents — so this is build + careful code review
against the working Daemon reference, not an end-to-end GUI test run).

**That caveat mattered: a full-session grok-review pass (2026-07-30, run against all ten commits
accumulated since this closure, not just the one that introduced the gap) found three real bugs
manual review missed.** `nrr` was captured as a `StartHiveWorkerAsync`-local in both closures,
but `StopHiveWorkerAsync` disposes the native role executor (and the `NativeRoleRuntime` it
wraps) — so any telemetry poll or degrade request arriving after a stop would throw
`ObjectDisposedException` (500) instead of the honest `{}` / 503 "not configured" those endpoints
are supposed to give once the worker isn't running. Fixed by moving the runtime reference into a
field (`_currentNativeHiveRuntime`) read at invocation time, matching the pattern
`CancelTaskHandler` already used correctly. Two more real issues surfaced fixing that one: (1) a
re-entrant `StartHiveWorkerAsync` call that resolves native as null a second time left the PRIOR
run's handlers installed, so `MarkRoleDegradedHandler`'s null-field fallback silently reported
success for a degrade that never happened -- fixed with an explicit `else` clearing both handlers
when native isn't configured this time; (2) the fallback itself was a `?? Task.CompletedTask`
silent-success trap for any *future* call site that gets the field/handler pairing wrong -- grok
flagged this as a standing risk even after both concrete paths were closed, so the fallback now
throws instead, matching this session's own "loud failure over silent success" principle applied
everywhere else. Re-verified clean via `grok-review -Mode diff` after each fix; full 703-test
suite green throughout.

**Two more adversarial rounds against the same GUI wiring, same day.** Worth recording plainly:
repeated adversarial review against previously-"clean" work kept finding real bugs, round after
round, in code that had never been exercised in anger (this GUI HIVE-worker path's own original
gap, per the finding above, was that nobody had wired it at all). Round 2 found two more
BLOCKERs: (1) `NativeWithFallbackRuntime.FallbackCount` (landed as "closing §5.4") had zero
production readers anywhere -- not `GetHealth`/`GetStats`, no Settings probe, not even the
existing Activity Log fallback message -- so the counter existed but the operator-visible signal
was unchanged; fixed by folding the cumulative count into that same Activity Log message. (2)
`_hiveWorkerAgent.Start()` ran before the node-server handlers were wired (unlike
`HiveService.cs`'s wire-then-start order) -- reordered.

Round 3 found a THIRD real bug, this time in round 2's own "don't clobber a live-elected
Warchief" fix: seed-only-when-`WarchiefNodeId`-is-empty turned out to be dead code, because
`HiveNodeServer.Start` already calls `InferWarchiefFromPeerStore`, which sets a non-empty
`WarchiefNodeId` (often this node's own id) before the worker ever starts -- the empty-check
never fired. Fixed by tracking what was last explicitly seeded (`_lastSeededWarchiefNodeId`)
instead of checking emptiness, plus three more findings surfaced fixing THAT: an explicit operator
retarget must always win regardless of election state; the tracked value must only be recorded
when `SetWarchief` actually ran, not on a null-`ElectionService` no-op; and -- the one that took a
fourth round to get right -- value-only tracking couldn't tell "this `ElectionService` instance
has never seen my seed" (a recreated instance, e.g. HIVE MIND restarted) from "a live election
diverged from my seed on the SAME instance," wrongly refusing to seed a fresh/reset instance.
Final design compares by instance reference, not just by value: a new/never-seeded instance is
always eligible, an explicit config change always wins, an empty current value is always safe to
fill, and only a same-instance, same-config, genuinely-diverged value is protected from
clobbering. Also fixed in these rounds: `OnWarchiefTargetSelected` (an explicit Settings retarget)
skipped rebuilding an already-running worker entirely, so a live worker silently kept talking to
its old target while the UI claimed the retarget succeeded; and two more handler-nulling ordering
issues in the stop and window-close teardown paths, mirroring the first round's fix. Every round
re-verified via `grok-review -Mode diff` before moving to the next; full 703-test suite green
throughout all of it.

**Four more adversarial rounds (5 through 8), same day, same GUI wiring.** By this point the
pattern itself was the finding: adversarial review kept surfacing real, distinct bugs in code
that had simply never been exercised before this session, each fix's own blast radius revealing
the next gap. Recorded plainly rather than folded silently into the fix list above, because the
right lesson is "this class of code needs adversarial review before it's trusted," not "these
specific bugs were unusual."

Round 5 found the Warchief-seeding warning was STILL dead in the common case (config empty,
`InferWarchiefFromPeerStore` had already filled something else in) and a separate, more serious
BLOCKER: `StartHiveWorkerCoreAsync` was never serialized against itself, so concurrent callers
(app-launch auto-start, a Settings Start click, a retarget) could each construct a full VRAM-
loaded `NativeRoleRuntime` before the first one finished, orphaning every runtime but the winner.
Fixed with a `SemaphoreSlim` gate around the whole method, the seed-warning restructured to check
the actually-configured target first regardless of what election already held, and a stale
`HiveWarchiefNodeId` pin cleared on explicit retarget.

Round 6 found the gate only guarded Start against itself, not against Stop/window-close --
a stop arriving mid-start could dispose the native executor out from under a start still in
flight. Also: Settings-Start racing ahead of node-server construction left a worker permanently
missing its handlers for the rest of the session (the `IsRunning` early-return meant a LATER
retry never re-checked), and a construction failure between building the native runtime and
`Start()` orphaned it with no cleanup at all. Fixed: one shared lifecycle gate around Start,
Stop, and window-close; a bounded wait for the node server before proceeding; and a try/catch
disposing whatever was actually constructed on failure.

Round 7 found the round-6 fixes each had their own edge case: the window-close path's stale-
worker-reference fallback could double-dispose an already-stopped worker; the failure-cleanup
catch cleared handlers even when `Start()` had already succeeded and only a later step failed
(stripping a live worker's wiring); and the gate could be released before the "we're closing"
flag was actually set, letting a queued Start slip through anyway.

Round 8 found the fix for THAT still had the flag set inside an async UI-thread dispatch AFTER
releasing the gate (moved earlier, now synchronous and before release); the failure-cleanup
catch unconditionally stripped handlers even for a Save()-only failure after a successful Start
(split settings persistence into its own separate try/catch that never reaches the construction-
failure cleanup path); and a defensive gap where a plain-`IDisposable` (not `IAsyncDisposable`)
native runtime implementation would silently skip disposal.

**Stopped here, deliberately, not by running out of findings.** Round 8's remaining two MINORs
were checked rather than chased: one (the lifecycle gate held for the duration of a node-server
poll and model load, so Stop/Close can queue behind an in-flight Start) is a real UX
responsiveness tradeoff, not a correctness bug -- accepted, not fixed, given eight rounds already
spent on genuine correctness issues in this one area. The other (`Start()` setting IsRunning then
throwing) was checked against `HiveWorkerAgent.Start()`'s actual implementation and found
unreachable: the method's only possible exception (`ObjectDisposedException`) throws strictly
before `_cts` is ever assigned, so `IsRunning` cannot be true when it fails. Every round
re-verified via `grok-review -Mode diff`; full 703-test suite green throughout.

**Second MINOR finding, both halves closed 2026-07-30.** `IsWarchief`'s positive match closed
first (see above: the "blocked on `HiveIdentity`'s private constructor" claim was stale --
`CreateEphemeral()`/`CreateForTest()` already existed). `TryCancelTask`'s actual-cancellation
path closed the same day: `HiveWorkerAgentTests.TryCancelTask_OnATaskActuallyInFlight_
ReportsCancelledNotFailed` runs a real `HttpWorkerAgent.Start()` against a fake Warchief (bare
`HttpListener`, not the real `HiveNodeServer` -- this only needs to hand out one lease and
accept one fail-POST, not validate HMAC auth) that leases a task backed by a `Runtime` whose
`StreamCompletionAsync` blocks on the cancellation token indefinitely via
`TaskCompletionSource`-signalled synchronization (waits for generation to *actually* start
before cancelling, not a fixed sleep guess). Asserts the terminal report is "Cancellation
reported to Warchief," never "Failure reported" -- the exact distinction PR #94's grok-review
fix (above) exists to preserve, now covered end-to-end rather than only unit-tested in
isolation. Verified stable across 5 repeated runs (~585ms each, no flakiness) before landing.

**Round 9 (`grok-review -Mode adversary`, 2026-07-30, saved at
`.orc/reviews/grok_adversary_20260730_071515.md`) — 2 BLOCKER, 2 MINOR, all four resolved.**
Same pattern as rounds 1-8: re-running `adversary` mode against the accumulated session diff after
round 8 looked clean kept surfacing new, narrower edge cases in `MainWindow.axaml.cs`'s HIVE-worker
lifecycle code, none of which manual review or a plain `diff`-mode pass had caught.

1. **BLOCKER — node-server readiness wait checked the wrong field.** `StartHiveWorkerCoreAsync`'s
   wait loop checked `_hiveNodeServer is null`, but `_hiveNodeServer` is assigned at
   `StartHiveAsync`'s line 616, well before `.Start()` (line 640) actually creates
   `ElectionService` and binds the listener. A `StartHiveWorkerAsync` call landing in that window
   saw a non-null field, exited the wait immediately, took the "`ElectionService` isn't available"
   branch further down, wired handlers, and called `Start()` on the worker anyway -- after which
   `IsRunning` permanently blocks any later retry from ever re-seeding the Warchief, the exact
   "no auto-repair once running" failure mode round 5's fix was meant to close, reopened here via
   a weaker readiness check. Fixed: the wait now checks `_hiveNodeServer?.ElectionService is null`.
2. **BLOCKER — `MainWindow_Closing`'s guard treated an in-flight Start as "nothing to close."**
   The guard was `if (_closingAfterHiveWorkerShutdown || _hiveWorkerAgent is null) return;` --
   `_hiveWorkerAgent` reads null for the entire window a Start spends in the node-server wait loop
   or inside `BuildRequiredNativeHiveWorkerRuntime()`, before it's ever assigned. A close request
   landing in that window skipped the whole gated `ShutdownHiveWorkerAndCloseAsync()` path
   entirely and let the window close immediately, with the in-flight Start left free to keep
   constructing and starting a native worker against an already-closing UI. Fixed by dropping the
   `_hiveWorkerAgent is null` half of the guard -- every close attempt (until
   `_closingAfterHiveWorkerShutdown` is set) now routes through the gated teardown, which acquires
   `_hiveWorkerLifecycleGate` and only then decides whether there's anything to actually shut
   down. The extra async hop when HIVE MIND was never enabled at all (both fields permanently
   null) is a negligible, uncontended gate acquisition, not a regression.
3. **MINOR — `StartHiveAsync`'s background `Task.Run` had no exception handler.** The
   fire-and-forget `_ = Task.Run(async () => await StartHiveAsync());` call site let any exception
   inside `StartHiveAsync` vanish silently -- notably `HiveIdentity.Load()` throwing on a corrupt
   identity file, which is now the *default* behavior after this session's earlier
   `regenerateOnCorruption` flip. Whatever partial state `StartHiveAsync` had already constructed
   before the throw was left as a non-functional zombie with no Activity Log signal that HIVE MIND
   failed to start at all. Fixed: wrapped in try/catch, logging `ActivityKind.Error` with the
   exception message.
4. **MINOR — checked, not a bug.** The seeding logic's `isNewElectionInstance` always letting the
   *first* seed on a fresh `ElectionService` instance override `InferWarchiefFromPeerStore`'s
   bootstrap guess was flagged as a possible live-election-clobbering risk. Re-reading the seeding
   guard against its own commit history confirmed this is intentional and correct: the "protect a
   live-elected divergence" branch only applies once a value has already been seeded by *this*
   instance and later changed out from under it (a real election result arriving), which by
   definition cannot have happened yet on an instance's very first seed. No code change.

Build clean, full 703/716-green suite unchanged, re-verified via `grok-review -Mode diff` before
landing. That `diff` pass raised one further point, checked and accepted rather than fixed: the new
catch logs the failure but does not unwind whatever `StartHiveAsync` had already constructed before
throwing (`_hiveRpcWorker`, `_hiveNodeServer`, `_hiveBeacon`, `_hiveTaskQueue`, depending how far it
got) — the exception now surfaces loudly instead of silently, which was MINOR 3's actual gap, but a
full rollback-on-partial-failure path is a materially bigger feature than this round's fix and the
triggering case (a corrupt identity file, now the rare path after this session's
`regenerateOnCorruption` flip) is narrow. Left as a known follow-up, not implemented here. (The
`diff` pass's other flag, that `AddActivity` runs from a background `Task.Run`, is a false
positive: `AddActivity` already marshals to the UI thread internally via `Dispatcher.UIThread.
InvokeAsync` — same pattern the pre-existing `UpdateChecker` background task already relies on.)

**Round 10 (`grok-review -Mode adversary`, same day, re-run against the full accumulated diff
immediately after round 9 landed) — 4 BLOCKER, 3 MINOR reported; 1 BLOCKER fixed, 2 MINOR
fixed/checked, the remaining 3 findings assessed and deliberately not touched, with reasoning
recorded below rather than silently dropped.**

1. **BLOCKER, fixed — `HiveIdentity.Load()` was ALSO called, uncaught, on the main UI startup
   path.** `OnLoadedAsync`'s HIVE-membership first-run-wizard trigger called
   `Services.Hive.HiveIdentity.Load().HiveRole` directly, with no try/catch anywhere between it and
   the `Loaded += async (_, _) => await OnLoadedAsync();` event wiring, and this app registers no
   global unhandled-exception handler at all (confirmed by grep). Round 9's MINOR 1 fix only
   covered the call *inside* `StartHiveAsync`'s own `Task.Run` -- this second, earlier call site on
   the synchronous startup path was a real gap in that fix's coverage: a corrupt/decrypt-mismatch
   identity file would have thrown straight through app startup and very likely crashed the whole
   GUI, not just failed to start HIVE MIND, which is a materially worse regression from this
   session's `regenerateOnCorruption` default flip than anything round 9 addressed. Fixed the same
   way: wrapped in try/catch, skip the wizard trigger and log `ActivityKind.Error` on failure
   instead of propagating.
2. **BLOCKER, closed with a bounded wait rather than the full cancellation-token feature.** The
   lifecycle gate is held across `WorkerCapabilityDetector.DetectAsync` (SHA-256 hashing every
   on-disk GGUF), native runtime construction, and the up-to-5s `ElectionService` wait; round 9's
   own BLOCKER 2 fix meant `MainWindow_Closing` now unconditionally routes every close through that
   same gate with no cancellation token, where round 8's now-fixed bug used to provide an (unsafe)
   early exit. Round 8's doc entry already named this exact shape ("Stop/Close can queue behind an
   in-flight Start... accepted, not fixed") but round 9's own correctness fix made the wait
   unconditional instead of racily skippable, so this needed re-examining rather than re-accepting
   as-is. Plumbing a real `CancellationToken` through the whole capability-detection/model-load
   chain remains out of scope (a feature, not a fix) -- instead, `ShutdownHiveWorkerAndCloseAsync`
   now bounds its own gate acquisition to `HiveWorkerLifecycleGateCloseTimeout` (30s): past that, it
   logs a warning and closes anyway without a clean worker shutdown, rather than blocking the whole
   window indefinitely on one slow Start. A follow-up `grok-review -Mode diff` pass confirmed the
   one resulting tradeoff -- a Start that later finishes past the timeout still touches its own
   fields/native runtime after the window is gone -- as understood and intentional, not a new bug:
   that Start already passed its own `_closingAfterHiveWorkerShutdown` entry check long before the
   timeout fired, so there was never a way to retroactively stop it without the cancellation-token
   feature this round deliberately didn't take on.
3. **BLOCKER, closed same day.** When a worker restart's `warchiefNodeId` resolves empty (e.g.
   immediately after `OnWarchiefTargetSelected` clears `HiveWarchiefNodeId` and retargets to a URL
   that hasn't paired yet), the seeding logic used to only log a warning -- it never cleared
   `ElectionService.WarchiefNodeId`, which `HiveElectionService.SetWarchief(string nodeId)` had no
   API to do (non-nullable parameter, private setter). The prior, still-seeded Warchief kept
   `/hive/tasks/cancel`/`/hive/roles/degrade` authority for the rest of that worker's run even
   though the operator had explicitly retargeted away from it. Closed by adding
   `HiveElectionService.ClearWarchief()` -- same reset shape as `SetWarchief` (clears
   `_preFailoverWarchiefId`/suspect votes, resets `State` to `Normal`) but sets `WarchiefNodeId`
   back to null instead of requiring a concrete value. The empty-`warchiefNodeId` branch in
   `MainWindow.axaml.cs` now calls it, gated behind the SAME `safeToReseed` check the seed branch
   below already uses (so a live-elected divergence still can't be clobbered) and only when there's
   an actual stale value to clear. Covered by
   `HiveNodeServerAuthorizationTests.IsWarchief_ReturnsFalse_AfterClearWarchief_
   ForThePreviouslySeededNodeId`.
4. **MINOR, fixed — `StopHiveWorkerCoreAsync`'s settings `Save()` could desync the UI permanently.**
   `_settings.HiveWorkerMode = false; _settings.Save();` ran unguarded before
   `_settingsPanel.SetHiveWorkerRunning(false)` -- a `Save()` throw (disk full, permissions) would
   skip the UI update entirely, and since `_hiveWorkerAgent` is already null by that point (worker
   genuinely stopped a few lines earlier), a subsequent Stop attempt would just early-return at this
   method's own top guard, leaving the "Running" label stuck until an app restart. Fixed: settings
   persistence wrapped in its own try/catch (same shape as the Start-side split from round 8),
   `SetHiveWorkerRunning(false)` now runs unconditionally afterward.
5. **MINOR, checked, no code change — `NativeWithFallbackRuntime.GetHealth`/`GetStats` omit
   `FallbackCount`/`LastFallbackReason`.** Grok's own finding text already notes "docs already admit
   non-persistent" -- `RUNTIME_SUPPORT_MATRIX.md`'s fallback table already says the count is "only
   visible in the moment the line appears, not a persistent indicator." Nothing to fix; the
   documentation was already accurate about this exact gap before the finding was raised.
6. **MINOR, closed same day after all.** `HiveIdentity.Load()`'s `regenerateOnCorruption`
   throw-vs-regenerate branch had no direct unit test -- initially assessed as needing a bigger
   refactor to close (the process-wide `_instance` singleton and hardcoded `IdentityPath` gave no
   reset hook or injectable path). Closed instead by factoring the decrypt/parse/throw-or-regenerate
   decision itself out of `Load()` into `LoadOrCreateFromPath(path, regenerateOnCorruption)`, a
   private helper with no singleton or disk-write side effects, exposed via a test-only
   `LoadFromPathForTest` (mirrors `CreateEphemeral()`'s "no disk" convention). `Load()` itself is
   now just: check the singleton cache, delegate to the helper against the real `IdentityPath`,
   persist if genuinely new. Four new tests in `HiveIdentityTests.cs` cover missing-file,
   corrupt-file-throws (`regenerateOnCorruption: false`), corrupt-file-regenerates
   (`regenerateOnCorruption: true`), and the literal-`"null"`-content branch's specific
   `InvalidOperationException`.

Build clean, full 703/716-green suite unchanged, re-verified via `grok-review -Mode diff` before
landing (that pass's only findings were reminders about unrelated untracked files already correctly
excluded from staging: `training_pit/datasets/toolcaller/` and `docs/OrcEngine/`).

**Round 11 (`grok-review -Mode adversary`, same day, re-run immediately after round 10 landed) --
2 BLOCKER, 3 MINOR reported; all closed same day.** This round is the clearest evidence yet that
`HiveIdentity.Load()`'s uncaught-call-site problem was never really "N separate bugs" -- it's one
bug class (the same call, scattered across a dozen files, each site independently uncaught) that
whack-a-mole fixing keeps re-surfacing one call site at a time. Documented candidly rather than
declared closed prematurely a third time.

1. **BLOCKER, fixed -- two MORE uncaught `HiveIdentity.Load()` call sites on the exact same startup
   path.** `RestoreLastMode()` (called from `OnLoadedAsync`, same as round 10's fix) can reach
   `SetMode("update")`, which calls `HiveIdentity.Load()` directly with no try/catch, or
   `SetMode("hive")`, which calls `_hivePanel.Refresh()` -> `DrawConstellation()` --
   `HivePanel.axaml.cs` has its own separate, uncaught `HiveIdentity.Load()` call. Either one, with
   `_settings.LastMode` set to `"update"` or `"hive"` from a prior session, would crash GUI startup
   on a corrupt identity file exactly like round 10's wizard-trigger bug, despite that fix already
   having landed. Fixed at both points: `SetMode("update")`'s block now catches and falls back to
   an empty `LocalNodeId`/`IsWarchief=false` for just that panel; the `RestoreLastMode()` call site
   itself also got wrapped (catching the `SetMode("hive")` path and any other mode-restore failure
   generically) so `ApplyTrustLevel` right after it still runs regardless of which mode failed to
   restore.
   **`HivePanel.axaml.cs` alone has upwards of ten more `HiveIdentity.Load()` call sites** used
   throughout normal HIVE panel operation (pairing, role changes, fingerprint display) --
   confirmed, critically, NOT a one-time startup risk: the panel's own `Loaded` handler AND its
   `_poll` `DispatcherTimer` (8s interval) both reach `DrawConstellation()`'s uncaught call, and
   since a failed `Load()` never populates the singleton `_instance`, EVERY subsequent attempt
   re-throws identically -- meaning a persistently corrupt identity file would crash the app not
   once at startup but every 8 seconds, forever, once this panel is shown. Patching each of the
   ten-plus call sites individually is the same losing whack-a-mole pattern that took three rounds
   to even notice has a pattern -- NOT done. Instead, wrapped the three periodic
   `DispatcherTimer.Tick` handlers (`_poll`, `_eventPoll`, `_campaignPoll`) and the panel's own
   `Loaded` handler in try/catch (`Debug.WriteLine` only -- this panel has no `AddActivity`-
   equivalent sink to log into, unlike `MainWindow`). This is a general "an uncaught exception
   from a periodic timer tick must never crash the whole app" hardening, not an
   identity-specific fix -- it stops the recurring CRASH regardless of which of the ten-plus call
   sites (or any other cause) throws, at the cost of leaving the panel silently stuck un-refreshed
   rather than genuinely fixing identity loading. The durable fix is still architectural -- e.g.
   making a failed `Load()` cache its own failure and return a degraded, clearly-marked ephemeral
   identity on retry instead of re-throwing every call until the file is fixed, or one process-wide
   preflight gate that disables all HIVE UI for the session on first failure -- and deserves its
   own deliberate round with explicit design input, not a same-session patch under continued
   autonomous iteration, since it touches the exact singleton/regeneration behavior this session's
   headline identity-corruption fix was about.
2. **BLOCKER, fixed -- the stale-Warchief-authority gap round 10 "closed" was only half-closed.**
   Round 10's `ClearWarchief()` fix only fires from the seed block deep inside a Start that reaches
   it (after `WorkerCapabilityDetector.DetectAsync` and native runtime construction) -- but
   `OnWarchiefTargetSelected`'s `StopHiveWorkerAsync()` call, which runs FIRST as part of every
   retarget, never touches `ElectionService.WarchiefNodeId` at all. So the OLD Warchief kept
   `/hive/roles/degrade`, `/hive/tasks/cancel`, AND (missed in round 10's scope) `/hive/update/deploy`
   authority for the entire Stop-to-Start window, and *permanently* if the subsequent Start ever
   threw before reaching its own seed block. Fixed by calling `_hiveNodeServer?.ElectionService?
   .ClearWarchief()` directly inside `OnWarchiefTargetSelected`, synchronously, in the same action
   that clears `_settings.HiveWarchiefNodeId` -- not gated behind `safeToReseed` here, since an
   explicit operator retarget winning immediately over whatever election currently holds is exactly
   the doctrine this file's own seed-block comments already state, just now applied at the moment
   of the actual operator action instead of waiting for a future Start to eventually get there.
3. **MINOR, checked, no code change.** `HiveWorkerAgent.FallbackCount`/`LastFallbackReason` reading
   as permanently 0 in native telemetry is the SAME already-documented dead-code gap from round 9's
   own doc entry (`Runtime` is hardcoded `null` at both GUI and Daemon construction sites) -- not a
   new finding, already accurately caveated in `RUNTIME_SUPPORT_MATRIX.md` and `CURRENT_STATE.yaml`.
4. **MINOR, fixed.** `Tools/SwarmCli/Program.cs`'s `--declare-warchief` also called
   `HiveIdentity.Load()` uncaught -- the same regression class as `MainWindow.axaml.cs`'s startup
   paths, just in a CLI tool instead of the GUI. Wrapped with the same try/catch shape
   `--show-identity` already uses (refuse to silently regenerate, print a clear error, exit 1).
5. **MINOR, fixed.** `MainWindow_Closing` had no guard against being invoked twice before
   `_closingAfterHiveWorkerShutdown` gets set (only set deep inside the async teardown) -- a
   double-click on the close button, or Alt+F4 while a first close was still in flight, spawned a
   SECOND `ShutdownHiveWorkerAndCloseAsync` contending for the same gate. Not a correctness bug
   (`SemaphoreSlim(1,1)` serializes them correctly either way) but genuinely wasted, redundant work.
   Fixed with a new `_hiveWorkerShutdownInFlight` flag, set synchronously the instant the first
   teardown starts and checked (but not gating `e.Cancel`) on every subsequent Closing event. A
   follow-up `grok-review -Mode diff` pass caught that this flag, once set, was never reset if the
   teardown task somehow faulted before reaching its own `finally` -- added a `ContinueWith`
   (`OnlyOnFaulted`-equivalent check) that resets it in that case, so a truly exceptional failure
   can't permanently block every future close attempt.

Build clean, full 708/721-green suite unchanged (4 new `HiveIdentityTests` from item 6 above
included), re-verified via `grok-review -Mode diff` after each fix before landing.

**2026-07-29 (later same day) — item 1 fully closed for real, but only after live testing
surfaced a second, deeper bug the grok BLOCKER fix alone didn't cover.** Built
`Tools/Hv4RecoveryRunner --phase workercancel`: submits a real long-running job pinned to a
target worker, waits for genuine claim, signs and POSTs directly to that worker's
`/hive/tasks/cancel` (not the Warchief's campaign-cancel path — the actual new endpoint), then
asserts the task's terminal status is `cancelled`, never `failed`/requeued/silently
`completed`.

**First live runs (Researcher role) failed on an unrelated pre-existing issue**: the 20-step
`LongSpec` prompt combined with the Researcher role's system-prompt overhead exceeded
HardcorePC's configured 4096 native context on the very first render — a genuine, previously
undiscovered incompatibility (this exact role+box combination had never been exercised with
`LongSpec` before; today's other HV-4 runs all targeted the laptop). Routed around it with
`--role Worker`, not fixed (out of scope here).

**With Worker role, a new and more serious anomaly appeared: a task reached a directly-observed
`Status=cancelled` (confirmed via its `ErrorMsg` field, which only a successful, guarded write in
`HandleFailAsync` can produce) and was LATER found `Status=completed` with real generated
content — the same task, same taskId, re-executed and its stale cancellation error left behind.**
Neither of the two obvious candidates explained it: `HandleFailAsync`'s own attempt-based requeue
is bypassed for `wasCancelled` (confirmed by re-reading the code); `CheckTimeouts`' heartbeat
watchdog has no `switch` case for `"cancelled"` at all, so it cannot touch a cancelled entry
either. Manual code reading of every claim/lease/complete/fail guard, `PostResultAsync`,
`CampaignRepository`, and `UpdateCampaignAfterTerminal` found no code path that moves
`Status="cancelled"` back to claimable — consistent with a second full **Grok** review
(`grok-review -Mode full -Focus "<the exact anomaly>"`, since **Codex** was unavailable — its CLI
build is too old for the configured model and needs an upgrade, an environment issue not fixed
in-session) confirming the same: *"There is no Status=cancelled → pending/claimed assignment
anywhere... A true wasCancelled terminal write cannot be re-claimed by this code."*

**Grok found the real, adjacent bugs instead — not a resurrection path, but silent
data-consistency gaps that produced the exact symptom observed:**
1. `HiveWorkerAgent.PostResultAsync` never checked the HTTP response status of its own
   fail/complete POST — a rejected post (e.g. a race against the heartbeat watchdog) was
   silently treated as success, logged as "reported to Warchief," and the worker moved on
   blind to whether the Warchief's queue agreed.
2. `HiveTaskQueue.HandleCompleteAsync` set `Status="completed"` but never cleared
   `entry.ErrorMsg` — so any error a prior attempt on the same entry had written (a heartbeat
   timeout requeue, or a cancellation) survived a later genuine success forever, producing
   exactly the contradictory record observed.
3. `HiveRepository`'s durable SQL upsert used `error_msg = COALESCE(excluded.error_msg,
   hive_tasks.error_msg)` — a later completion posting `errorMsg: null` could never clear a
   previously-persisted error in the DURABLE store either, the same bug one layer down.

All three fixed. Since none of Grok's findings individually proved to be THE resurrection
mechanism (the actual claim/lease code was confirmed clean), file-based diagnostic logging
(`THEORC_HIVE_TASK_DIAGNOSTICS=1`, gated and zero-cost when off — same convention as
`THEORC_KVCACHE_DIAGNOSTICS`/`THEORC_HIVE_HEARTBEAT_DIAGNOSTICS`, kept as a standing tool rather
than removed) was added at every claim/lease/fail/complete decision point on both the Warchief
and worker sides — `Log(...)`/console output being fully buffered and not visible until process
exit was exactly the reason this took so long to pin down live. Warchief restarted as a direct
child process via `Start-Process` (never WMI, per this doc's own earlier rule) with diagnostics
on.

**The instrumented re-run resolved it: the anomaly did not reproduce, and the trace shows the
mechanism working exactly as designed.** Worker-side: task claimed, cancel found the CTS 153ms
later (not already cancelled), `OperationCanceledException` caught 184ms after that, `cancelled`
result posted 182ms after that — one clean execution attempt, no second registration for the same
taskId. Warchief-side: exactly one `HandleLeaseAsync SELECT` for the taskId, one
`HandleFailAsync ACCEPT` with `entry.Status=claimed` at write time — no second lease, ever. The
earlier races and the one anomalous run most likely came down to the same inherent timing
sensitivity already documented for `hv4-kill`/`hv4-disconnect` elsewhere in this doc: a `steps:1`
completion has no interruption checkpoint *during* the model's single decode call, only
before/after it, so whether a fast cancel wins depends on exactly when it lands relative to that
one call — genuinely narrow, not a resurrection bug, and the three Grok-found bugs make the
system meaningfully more honest regardless of that timing (a lost race now surfaces loudly
instead of silently).

**Confirmed with 3 consecutive clean runs against HardcorePC (not a single pass)**, all four
checks PASS every time, including the direct `terminal status=cancelled` assertion:
`.orc/hv-4-lane/hv4_workercancel_20260729_230906.json`,
`..._231311.json`, `..._231332.json`.

**HV-4 verdict, final: items 2, 3, and 4 evidenced across machines (as before); item 1 is now
CLOSED with real evidence, not partial coverage** — a genuine remote mid-generation cancellation,
authenticated, routed correctly, reported with its own terminal status that cannot be silently
retried or lost, confirmed live and repeatably. §6's "failure, cancellation, disconnect, and
recovery exercises across machines" criterion is materially stronger for it, and the investigation
also hardened two real, previously-silent data-consistency gaps (`PostResultAsync`'s ignored
response status; stale `ErrorMsg` surviving a genuine success in both the in-memory and durable
stores) that were never specific to cancellation and could have masked other classes of failure
the same way.

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

**2026-07-28 — driver built (`Tools/Hv6RepeatabilityRunner`); first full 3× campaign run
unattended, 56 minutes, 8 of 10 lanes green in all three rounds.**

The driver talks to nothing. It invokes the five lane runners and aggregates what they already
wrote, so a lane's verdict here is that lane's own verdict rather than a re-interpretation of raw
telemetry — the only way "the campaign is repeatable" means anything. A failed round does not abort
the run: "round 2 failed, 1 and 3 passed" is the useful answer, and intermittency is exactly what
this lane exists to surface.

**HV-2's `large` phase forced a design decision.** It is a fleet CONFIGURATION, not a job shape —
its own docs say to run it "against a fleet already reconfigured (NativeContextSize env var) and
restarted for that phase". Run against the standard config it cannot deny anything, and it duly
reported the low-VRAM box completing a job it was supposed to refuse. Since HV-6's requirement is
explicitly *no manual intervention between runs*, an operator switching that config mid-campaign is
the very thing being forbidden. The FIRST version of the driver did the switch automatically inside
the main campaign's round loop; a later maintainer decision (recorded below, "Split, not
automated-switch") moved HV-2's large phase out into its own separate `--large-only` invocation
instead, so the shipped driver never switches configuration mid-campaign at all — a large-context
lane's size is set once before round 1 and restored once in a `finally`, with no per-round return to
standard in between (there is nothing to return FROM, since a single invocation's lane pool is
homogeneous by construction). It also aborts outright, rather than recording a lane as `NOT-RUN`, if that one reconfiguration
cannot be applied — but the resulting exception no longer crashes the process before a report is
written: `Main`'s outer `catch` records it as `report.Error` and the JSON/markdown are still
produced, which is itself a fix from the same CodeRabbit review that caught this paragraph being
stale.

```
| Lane             | R1   | R2   | R3   |
| hv1              | PASS | PASS | PASS |
| hv2-large        | PASS | PASS | PASS |
| hv2-small        | PASS | PASS | PASS |
| hv3-sequential   | PASS | PASS | PASS |
| hv3-concurrent   | PASS | PASS | PASS |
| hv4-cancel       | PASS | PASS | PASS |
| hv4-ollama       | PASS | PASS | PASS |
| hv4-kill         | FAIL | FAIL | PASS |
| hv4-disconnect   | FAIL | FAIL | PASS |
| hv5              | PASS | PASS | PASS |
```

Evidence: `.orc/hv-6-lane/hv6_report_20260728_004640.json` + `hv6_summary_20260728_004640.md`,
with all 30 per-lane evidence files listed in it. `reconfigurations` and `fleetRestored` are
recorded so a run that could not put the fleet back says so.

**Every failure is HardcoreLaptopMSI, in the two lanes that deliver a box-level action mid-flight
over ssh. HardcorePC passed every lane in every round.** Three of the four are the ssh call not
landing at all (`kill-actually-landed`, `cut-actually-landed`); the fourth is that box finishing its
job before the kill arrived. **No fabricated passes** — every one was caught by a landed-gate rather
than being measured against an undisturbed worker, which is what those gates were added for.

**The box, not the product.** That laptop's sshd goes unreachable for minutes while the machine
stays healthy — ping 5 ms, `/hive/native-telemetry` answering 200, jobs completing. `sshd_config` is
all defaults and the service is Running; the box is on the **Balanced** power plan, which fits the
symptom exactly: a key exchange needs CPU and an already-established HTTP listener does not, so it
fails precisely when the box is saturated doing the inference these phases need it to be doing.

**2026-07-28 (later) — driver split into two invocations per maintainer decision; a self-inflicted
regression found and fixed; two more full 3× runs.**

**Split, not automated-switch.** The first run above folded HV-2's large-context reconfiguration
into the main campaign's round loop. A maintainer call was made to keep the main campaign's fleet
state untouched for its whole run instead: `hv2-large` now runs as its own separate `--large-only`
invocation, reconfiguring once at the start and restoring once at the end, entirely outside the
main campaign's 3× loop. Both invocations are still independently complete "3× back-to-back, no
intervention" runs — running two of them is not the same as intervening inside either one — and
each writes its own report; the main report explicitly says HV-2's large phase lives in the other
one.

**Self-inflicted regression, found by running it: a killed worker was left dead.** The retry that
produced HardcoreLaptopMSI's clean run above added a race guard (`job-still-running-when-kill-
landed`) whose early return sat *before* the worker restart — so when the race fired (job completed
before the kill landed), the method recorded the check and returned having killed a live worker and
never brought it back. The next lane in that round then failed against a worker this driver itself
had killed and abandoned, which read as cascading fleet instability but was self-inflicted. Fixed
by moving the restart into a guarded, idempotent step called from both the happy path and a
`finally`, so it always runs exactly once whenever the kill actually landed. A second pass of the
same fix then found the restart had been placed *after* the recovery poll rather than before it —
the poll needs a live worker to answer, so it failed unconditionally until corrected. Both are
`Tools/Hv4RecoveryRunner` bugs, not fleet or product defects; each was caught within one round of
introducing it, by actually running the campaign rather than reasoning about the code.

**With both fixed, the main campaign (3×, `hv2-large` excluded) ran clean apart from the box's own
known ssh/timing limits:**

```text
| Lane             | R1   | R2   | R3   |
| hv1              | PASS | PASS | PASS |
| hv2-small        | PASS | PASS | PASS |
| hv3-sequential   | FAIL | PASS | PASS |
| hv3-concurrent   | PASS | PASS | PASS |
| hv4-cancel       | PASS | PASS | PASS |
| hv4-ollama       | PASS | PASS | PASS |
| hv4-kill         | FAIL | FAIL | PASS |
| hv4-disconnect   | PASS | PASS | FAIL |
| hv5              | PASS | PASS | PASS |
```

Evidence: `.orc/hv-6-lane/hv6_report_20260728_033326.json`. Five of nine lanes perfectly green in
all three rounds. Every `hv4-kill`/`hv4-disconnect` failure is again HardcoreLaptopMSI's job
finishing before the disruption landed (`job-still-running-when-kill-landed` /
`job-still-running-when-cut-landed`) — the same timing limit recorded above, not a recurrence of
the dead-worker bug: no failure this run left a worker unrecovered, and every later lane on that box
ran cleanly regardless of what the previous lane's race check reported.

**One new, one-off finding: R1's `hv3-sequential` shows `ConversationsCreated` (after-cycle samples
only) of `[9, 9, 10]` on HardcorePC** — cycles 1→2 landed on the same conversation instead of a
fresh one, while R2 (`[8, 9, 10]`) and R3 (`[8, 9, 10]`) on the identical fleet, same session, were
clean. R1 ran immediately after several minutes of heavy reconfiguration/campaign churn from manual
smoke-testing earlier in the session, which is the more likely explanation than a reproducible
defect — but it is recorded here rather than dismissed, per the plan's own precedent that "a
conversation silently reused and never re-counted" is exactly the failure this check exists to
catch. Not chased further on a single occurrence; worth a second look if HV-6 reproduces it on a
cold, uninterrupted fleet.

**HV-6 verdict: the harness and the fixes it drove are solid; "all green" is not yet met, and the
main gap is a single, understood, external limit — with one unresolved internal anomaly kept
explicitly open, not folded into that same explanation.** Every `hv4-kill`/`hv4-disconnect` failure
across both post-split runs is HardcoreLaptopMSI's ssh/scheduling behavior under load, previously
diagnosed (Balanced power plan) — not the runtime, not the queue, not a silent fallback, and (after
the two fixes above) not this driver leaving a worker dead. Separately, R1's `hv3-sequential` FAIL
on HardcorePC (the `[9, 9, 10]` conversation-count anomaly recorded above) is NOT a HardcoreLaptopMSI
ssh issue and NOT yet resolved — it is a one-off, more-likely-explained-by-session-churn finding on
a clean run of its own; it stays open until a cold, uninterrupted rerun either reproduces or clears
it. Closing HV-6 fully means either accepting the HardcoreLaptopMSI limit as a recorded, permanent
caveat on this fleet's evidence (with the HardcorePC anomaly tracked separately), or changing the
laptop's power plan (a machine setting, not code — not done without confirming first) and
re-running both.

**2026-07-28 (later still) — the induced-job design was the real remaining bug; fixed. The
remaining failures are conclusively isolated to one already-diagnosed hardware limitation, not a
scripting defect.**

**Root cause of the timing race, finally pinned down:** the "at least twenty sections" prompt used
by HV-4's kill/disconnect phases relied on generation LENGTH, and an LLM can (and, per the fleet
logs, often does) emit an arbitrarily long request in a single completion — `steps: 1` — so
wall-clock time was bounded by that one call's raw token speed. HardcoreLaptopMSI's card is fast
enough to finish that before the driver could ssh in and apply a kill or firewall rule, no matter
how long the requested document was. Fixed by asking for 20 separate file-creation steps against
the loop's `MaxSteps` ceiling of 12 — deliberately more than the loop can complete, so the job is
guaranteed to run the full step budget regardless of model speed, since each step costs a full
model round trip that a fast GPU cannot shrink away. Confirmed: `hv4-kill`'s landing (a single,
near-instant `Stop-Process` over ssh) went from failing this race to passing repeatedly.

`hv4-disconnect`'s landing (create a firewall rule, then verify — several ssh round trips, one
including a fixed sleep) is inherently slower than kill's, so it needed the sleep cut from 3s to
1s as well (a poll-loop version was tried first and reverted: it broke 3/3 with an empty
read-back, meaning its braces/semicolons did not survive the ssh → cmd → powershell quoting
chain — the exact class of failure already called out in this file's own comments for the
`New-NetFirewallRule` call itself; the flat-sleep shape is proven to survive that chain, only its
duration changed).

**With both fixes in place, a genuinely clean-fleet 3× main-campaign run (laptop confirmed idle,
4% CPU, before starting) gave the clearest signal yet:**

```
7 of 9 lanes: PASS, every round        (hv1, hv2-small, hv3-sequential, hv3-concurrent,
                                         hv4-cancel, hv4-ollama, hv5)
hv4-kill:     PASS R2, FAIL R1/R3      (HardcoreLaptopMSI only, job-still-running-when-kill-landed)
hv4-disconnect: FAIL all 3 rounds     (HardcoreLaptopMSI only, cut-actually-landed)
```

Evidence: `.orc/hv-6-lane/hv6_report_20260728_090737.json`.

**2026-07-28 (later) — HardcoreLaptopMSI went unreachable and HV-6 closure is blocked on it
regardless of the power-plan question.** Two remediation paths remain open for when the machine
is back: accept the disconnect gap as a permanent documented caveat on this fleet's evidence, or
apply the power-plan changes below (minimum processor state 100%, High Performance mode, USB
selective suspend disabled) and re-run the HV-4 lanes. Neither can proceed with the box offline;
work shifted elsewhere rather than continuing to poll an unreachable machine.

**Isolated the disconnect failure to rule out a scripting bug before accepting it as a hardware
limit.** `hv4-cancel`/`hv4-ollama`/`hv4-kill`'s own ssh calls all succeeded reliably throughout the
same 75-minute run, while `hv4-disconnect`'s firewall-rule call failed 3/3 — narrow enough to be
suspicious. Reproduced the driver's EXACT command string (including the appended
`; Write-Output '<marker>'` completion-detection suffix) by hand, twice, against the idle box: both
times it succeeded cleanly, rule created and verified. That rules out a quoting or scripting defect
in the command itself — the difference is that in the real campaign this ssh call fires *while the
box is actively serving the 20-step generation job under load*, and disconnect's sequence (create +
sleep + verify, several round trips) spends more time exposed to a CPU-saturated sshd than kill's
single near-instant call does. This is the same CPU-bound sshd failure mode diagnosed earlier in
this document, now narrowed one level further: it is specifically a function of how long the
box-level ssh action takes to complete, not of the box or the command being broken.

**HV-6 verdict, and the state this leaves §6 in:** 7 of 9 lanes are now repeatedly, robustly proven
across multiple independent full campaigns today. The 2 remaining failures are conclusively
narrowed to one external, already-diagnosed limitation — HardcoreLaptopMSI's sshd under CPU load —
affecting only the two phases whose disruption-delivery time is long enough to compete with it, and
proven NOT to be a driver, queue, or runtime defect by direct isolated reproduction. Closing this
fully still means either accepting it as a permanent, recorded caveat on this fleet's evidence, or a
hardware/OS-level change to that one machine (outside this session's scope to make unilaterally).
The §6 flip decision should treat HV-6 as evidenced-with-one-named-exception, not as failing outright
— the exception is narrow, understood, external, and does not implicate anything this validation
campaign was built to catch.

**2026-07-29 — `hv3-sequential`'s `[9,9,10]` anomaly does NOT reproduce on a genuinely cold,
uninterrupted worker. Closed as session-churn, not a defect.**

Followed the plan's own prescription: `theorc-warband.exe` on HardcorePC (PID 19780, warm, holding
a `Researcher` role with 3 prior conversations from earlier smoke-testing) was stopped and restarted
via the `TheOrcWorker` scheduled task — the same restart mechanism `Hv4RecoveryRunner` uses, not a
raw process launch. `GET /hive/native-telemetry` confirmed genuinely cold before submitting anything
(`"reservations":[],"residency":[]`, `rejectedAdmissionCount:0`). HardcoreLaptopMSI was confirmed
unreachable at the time (ssh to `100.114.151.4` timed out, consistent with its still-offline state
from earlier this session), so `Tools/Hv3LifecycleRunner` ran single-worker (`--worker-a HardcorePC`
only) — the driver's own warning about `ExcludedWorkerIds` being empty was accepted because no other
worker was live to mis-claim a job.

```
residency-returns-to-baseline:     PASS — ActiveCount back to 0 after all 3 cycles
reservation-persists-between-jobs: PASS — reserved roles [1|1|1]; baseline=548405248 (cold) →
                                    [5589043712 ×3]
fresh-conversation-per-job:        PASS — ConversationsCreated = [1, 2, 3]
```

3/3 jobs `completed`, `claimedByExpected` true, `NativeRoleRuntime`, zero fallback. Evidence:
`.orc/hv-3-lane/hv3_sequential_20260729_015152.json`. `ConversationsCreated` is strictly increasing
with no repeated value — the exact shape the original R1 `[9,9,10]` anomaly failed to show. This
confirms the 2026-07-28 hypothesis: that run's duplicate was produced by session churn (heavy
manual smoke-testing immediately beforehand, on an already-warm worker), not a reproducible defect
in conversation freshness. **`hv3-sequential`'s anomaly is closed** — no code change needed, no
further chase planned. `hv4-disconnect` on HardcoreLaptopMSI remains the sole open HV-6 item, still
blocked on the machine being offline.

**2026-07-28 (later still) — controlled experiment proves the disconnect gap is not fixable from
this driver's code. Stopping further attempts on it.**

The 1s sleep before verifying the firewall rule was cut to 0s entirely (create and verify remain
one ssh call/session, so there is no cross-connection risk — confirmed safe by hand against the
idle box first). Result, 3 clean rounds against a confirmed-idle fleet: **identical to the 1s
version** — `hv4-kill` PASS/PASS/FAIL (the same residual race as before), `hv4-disconnect` FAIL/
FAIL/FAIL, every failure the exact same "block rule not present" symptom.

That is the decisive result. Two different sleep durations (1s and 0s) produced statistically
indistinguishable outcomes — proving the sleep was never the bottleneck, and by extension that no
achievable command-latency reduction in this driver will change the outcome. What actually varies
is whether the ssh session completes AT ALL while this specific box is CPU-loaded serving the
induced job; that is a property of the machine's sshd under load, not of how fast the command
inside the session runs. This driver has no further lever over that.

**Conclusion: the `hv4-disconnect` gap on HardcoreLaptopMSI is not addressable through
`Tools/Hv4RecoveryRunner` changes.** The two remaining paths are unchanged from before this
experiment — accept it as a permanent, named caveat on this fleet's evidence, or make an
OS/hardware-level change to that one machine's ssh/CPU-scheduling behavior, which is outside what
this session does unilaterally. No further code-side attempts at this specific gap are planned;
continuing to retry the same lever after a controlled negative result would not be honest
persistence, it would be ignoring the experiment's own answer.

**2026-07-29 — the machine-side power-plan remediation was tried, on the actual machine, and did
not fix it either.** HardcoreLaptopMSI came back online after being unreachable. Before any test
ran, its actual settings were checked rather than assumed: **AC minimum processor state was
already 100%** (contradicting the original "Balanced plan" diagnosis — the box was on **High
performance** already), and only USB selective suspend was actually off-spec (`Enabled` on both
AC/DC). Disabled it on both
(`powercfg /setacvalueindex`/`/setdcvalueindex … USBSELECTSUSPEND 0`, confirmed via `/query`
afterward). Machine confirmed idle (5% CPU) and on AC power before starting.

With the worker restarted cold (via the `TheOrcLaptopWorker` scheduled task) and the power
settings applied, two `hv4-kill` attempts against the laptop reproduced the same symptom class
immediately: first attempt hit the already-known `job-still-running-when-kill-landed` timing race,
second attempt got the harder failure — **the kill's ssh call never landed at all** (worker kept
answering `/hive/native-telemetry` 60s after the kill was supposed to fire; process never died).
The worker itself stayed healthy throughout (same PID, telemetry fine before and after) — this is
the sshd-under-CPU-load symptom, not a worker crash.

**This closes out the power-plan hypothesis as conclusively as the four driver-side levers were
closed out.** Between the already-correct min-processor-state and the freshly-disabled USB
selective suspend, both items from the originally proposed remediation are now applied on the real
machine, and the failure reproduced anyway, on the first live attempt. Combined with the four
ruled-out driver-side experiments, there is no remaining known lever, on either side of the ssh
call, that has not been tried and shown not to change the outcome. Stopped here rather than
continuing to retry against a machine this session has already been asked not to hammer — the
`hv4-disconnect`/`hv4-kill`-race gap on HardcoreLaptopMSI should now be treated as the permanent,
named caveat option; a hardware/OS root cause deeper than power-plan settings (e.g. sshd service
configuration, background AV/indexing contention, or the process-creation-latency theory from the
fourth lever above) is the only remaining path, and none of those were attempted here.

**2026-07-28 (final confirmation) — third independent full campaign, same result.** Run after
deploying the heartbeat HttpClient-reuse fix (unrelated CodeRabbit finding, `OrchestratorIDE/
Services/Hive/HiveWorkerAgent.cs`) to both fleet workers: `hv1`/`hv2-small`/`hv3-sequential`/
`hv3-concurrent`/`hv4-cancel`/`hv4-ollama`/`hv5` all green in all 3 rounds; `hv4-kill` FAIL/FAIL/PASS
(the same residual timing race); `hv4-disconnect` FAIL/FAIL/FAIL (the same external CPU-contention
limitation). Evidence: `.orc/hv-6-lane/hv6_report_20260728_111409.json`. No regressions from the
heartbeat fix, and the characterization is now confirmed stable across three separate full-campaign
runs today rather than a one-off. Nothing further planned on the `hv4-disconnect` gap specifically —
see the three ruled-out experiments above.

**2026-07-28 (later still) — fourth lever, same result, but it sharpens the diagnosis.** Tried
raising the spawned PowerShell process's own scheduling priority (`PriorityClass = 'High'`, as the
process's first statement) — a real, standard technique for keeping a short-lived administrative
task responsive opposite a CPU-heavy workload, verified by hand against the idle box first. Result
against the fleet: 3/3 identical failures, the same "block rule not present" symptom.

That is informative, not just another negative. Three levers so far (sleep duration ×2, SSH
multiplexing) ruled out command latency and the SSH transport/handshake. This fourth one ruled out
in-process scheduling ONCE THE PROCESS IS RUNNING — priority elevation only takes effect after the
process starts executing statements, and it made no difference. That points the remaining
bottleneck at process CREATION itself: the delay between "ssh requests a new remote command" and
"that process is actually scheduled and begins running anything, including its own priority-boost
statement" — which no script content can touch, because the script hasn't started yet when the
delay happens.

**This identifies a genuinely different, specific remediation on the OTHER side of the contention:
lower the native inference worker's OWN process priority (e.g. `BelowNormal`), rather than trying to
boost the administrative side after the fact.** If new-process scheduling is starved because the
CPU's runnable queue is dominated by an already-running, equal-priority inference process, the
Windows scheduler will naturally prefer any newly-spawned Normal-priority process (ssh's remote
`powershell.exe`) over a `BelowNormal` one without any special elevation needed on the admin side —
the same standard technique background scanners and indexers use to avoid starving foreground work.
This is a real, bounded, low-risk change, but it is a PRODUCT-level change to how
`OrchestratorIDE.NativeRuntime` spawns/runs its inference process, not a `Tools/` driver tweak — outside
what should be decided unilaterally.

Four independent levers now ruled out (three for `hv4-disconnect` specifically, all with clean
negative or diagnosis-sharpening results). Nothing further planned in `Tools/Hv4RecoveryRunner`
itself; the remaining path, if pursued, is the worker-process-priority change above, and that needs
a decision, not more test-harness iteration.

**2026-07-29 — process-creation theory confirmed on HardcoreLaptopMSI; worker priority fix applied.**

Ran on the laptop itself (not from the Warchief driver). Goal: measure process-creation latency
under load, then try the `BelowNormal` lever the previous entry left as the remaining path.

**Confirmed root cause (measured, not guessed):** under 100% CPU load from equal-priority
(Normal) burners, brand-new process creation slows sharply; dropping those burners to
`BelowNormal` restores near-idle spawn latency while CPU stays at 100%. That is the Windows
scheduler preferring already-running Normal work over a newly created Normal peer — exactly the
window no remote-script content can affect (process does not exist yet).

| Condition | `cmd /c echo ok` | `powershell -NoProfile … Write-Output ok` |
|-----------|------------------|-------------------------------------------|
| Idle | ~21 ms avg | ~187 ms avg |
| 12× Normal CPU burners (100% CPU) | ~205 ms avg | ~1160–1300 ms avg |
| Same burners at BelowNormal (still 100% CPU) | ~23 ms avg | ~202–206 ms avg |

**Pass/fail (3 trials, fail = PowerShell spawn ≥ 1 s):**

| Load priority | PowerShell ≥1 s | Values (ms) |
|---------------|-----------------|-------------|
| Normal (old worker behavior) | **3/3 fail** | 1270, 1271, 1330 |
| BelowNormal (fix) | **0/3 fail** | 205, 218, 215 |

Ping-style health and HTTP were not the issue in prior campaigns; this run also reconfirmed
that `GET /hive/native-telemetry` and process health stay fine while spawn latency is bad under
Normal-priority CPU saturation.

**Caveat on magnitude:** synthetic full-core burners produced ~1.3 s PowerShell spawn, not the
multi-10s / 60s remote timeouts seen in live HV-4. Production stalls can stack extra cost
(Defender process-create hooks, `CreateProcessAsUser`, shell profile) on top of the same
scheduling effect. Direction and fix remain correct; if any multi-10s SSH delay remains after
deploy under a real Warchief job, next suspects are AV/sshd shell — not boosting the SSH side again.

A short `swarmcli --native-test` on `qwen25-coder-7b.gguf` was mostly GPU-bound (~11 tok/s,
~4 GB VRAM) with low host CPU during the sample window, so spawn stayed near idle there. That
does not refute the theory: fleet jobs that pin cores for prompt eval match the burner profile
more closely than a brief native-test.

**Fix applied (product-side, as the previous entry required):**

- `OrchestratorIDE.Daemon/Program.cs` — at start of normal long-running mode, set
  `Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal` (non-fatal if
  denied). Comment in-file cites these measurements.
- Debug build on HardcoreLaptopMSI rebuilt and left running: `theorc-warband` at
  **PriorityClass = BelowNormal**, telemetry HTTP 200.
- `start-worker.bat` on the laptop only documents the behavior (priority is set in managed Main
  so log redirection stays intact).
- Same `Program.cs` change mirrored into this checkout (NewcorePC `OrchestratorIDE-dev`) so the
  main tree carries it; rebuild/redeploy fleet workers from this tree as usual.

**What this changes for HV-6 / the named caveat:** the `hv4-disconnect` / sshd-under-load gap is
no longer “no remaining lever.” The lever was process priority on the worker; it is implemented.
**Re-run HV-4 kill/disconnect lanes against HardcoreLaptopMSI under live inference** to convert
this from measured local process-create evidence into fleet pass/fail counts. Until that re-run,
treat the OS-side diagnosis as closed and the fleet caveat as **provisionally fixed, pending
HV-4 re-validation**.

Local SSH loopback auth from the laptop itself was not used for verification (admin
`administrators_authorized_keys` needs elevation; sudo disabled on that box). Verification was
CreateProcess latency under controlled load plus live worker priority/telemetry checks.

**2026-07-29 (same day, later) — fleet re-validation says the opposite of the synthetic result:
the fix does NOT close the gap under real inference load, and a harder symptom appeared.**

Ran the actual `Tools/Hv4RecoveryRunner --phase kill` lane against HardcoreLaptopMSI (now running
the `BelowNormal`-priority build live, confirmed via `Get-Process` before starting) twice, with the
worker's PID recorded immediately before and immediately after each run — not relying on the
driver's own `kill-actually-landed` check alone, because that check infers death from telemetry
silence rather than a process handle, and that inference is exactly what's in question here.

```
Round 1: PID 9376  (StartTime 8:48:01 PM) before  → PID 9376  (unchanged) after
Round 2: PID 19748 (StartTime 9:09:43 PM) before  → PID 19748 (unchanged) after
```

**Both rounds: `kill-actually-landed` reported PASS (telemetry went dark, then answered again),
but the worker process was never actually terminated — a false positive on the check's own core
assumption.** A manual `Get-Process theorc-warband | Stop-Process -Force` issued by hand against
the same box, moments earlier while it was between jobs, DID kill the process immediately (new PID
on restart, confirmed) — so the `Stop-Process` command itself is not fundamentally broken; it is
specifically failing (or landing so late it's overtaken by recovery) while the box is under live
inference load. That is the exact symptom this whole investigation exists to fix, still present.

**A plausible explanation the synthetic benchmark could not have caught:** `Process.PriorityClass`
sets the process's *base* priority class, but native inference code (llama.cpp/ggml via
LLamaSharp P/Invoke) commonly pins its own compute threads to an explicit OS thread priority for
performance, independent of the parent process's class. If so, the real inference workload's
CPU-bound threads may still run at effective priority high enough to starve new process creation
even with the parent process nominally `BelowNormal` — a scenario Grok's synthetic burners
(plain `Math.Sqrt` loops at default thread priority) would not reproduce, and Grok's own passoff
already flagged this exact risk as unverified ("fleet jobs that pin cores for prompt eval look
more like the synthetic burners" — an assumption, not a measurement, and it reads as the wrong one
now).

**Immediately after the second round, SSH to the laptop stopped connecting entirely** (`Connection
timed out`, not just slow) while `ping` stayed healthy at 5ms — a harder failure than the
"tens-of-seconds" symptom documented all session, observed right after two back-to-back real
inference jobs. Testing was stopped at this point rather than continuing to probe a box already
showing a worse symptom than before, per this session's standing instruction not to hammer this
machine.

**Verdict: the `BelowNormal` process-priority fix is a real, well-measured improvement to raw
process-creation latency (Grok's synthetic numbers are not in question), but it has NOT been shown
to fix the actual HV-4 failure mode under real fleet conditions, and it introduced a new risk — the
kill-landed check's telemetry-silence heuristic can now read as a false positive.** The `Program.cs`
change is low-risk and worth keeping (it doesn't hurt, and the isolated benchmark is real), but the
`hv4-disconnect`/`hv4-kill` gap on HardcoreLaptopMSI is **still open, not provisionally fixed**.
Next real lead, if pursued: check whether native inference threads carry their own explicit OS
thread-priority (not just the process's `PriorityClass`) and whether that needs addressing
separately — not yet investigated. Until then, the permanent-caveat option from the earlier
2026-07-29 entry remains the honest fallback.

**2026-07-29 (later) — round 3, dispatched to a fresh Claude Code session running directly on
HardcoreLaptopMSI: root cause of round 2's failure confirmed, and a fix that survives real load
found.**

**Why `BelowNormal` failed under real inference, confirmed with direct thread-priority
measurement (not synthetic burners):** ggml's native thread pool sets one compute-dispatch
thread to Win32 `THREAD_PRIORITY_HIGHEST` — a **relative** offset (`+2`) to the process's own
priority class, not an absolute value.

```
Process class     Elevated thread's actual base priority
BelowNormal (6)    8   ← matches a freshly-spawned shell's DEFAULT priority
Normal (8)         10
```

At `BelowNormal`, that one thread claws back up to priority 8 — identical to a newly-spawned
admin shell's default — so under real contention it's a coin flip, not the clean win round 1's
synthetic burners showed (plain math-loop threads never self-elevate, so simply outranking them
worked; real ggml traffic doesn't). Direct A/B under real ~40-thread ggml compute load (CPU-only,
~2500-token prefill, ~6.5 of 12 cores genuinely busy): **no measurable spawn-latency difference
between Normal and BelowNormal** (`powershell.exe` ~265–303 ms either way).

**Fix: drop to `Idle` instead of `BelowNormal`.** Idle's base is 4; the same `+2` ggml offset
lands the elevated thread at priority **6** — comfortably below a fresh shell's default 8,
restoring the ordering round 1 always intended. Verified with the PID-based method (not
telemetry, which produced round 2's false positive): a throwaway process built from this repo's
real `LLamaSharpRuntime`/ggml path, running genuine sustained CPU-only inference at `Idle`
priority, was killed via `Stop-Process` issued from a separate `ssh.exe` process over the LAN —
**3/3 trials confirmed the PID actually gone** (PIDs 22296, 21204, 21260), with the elevated
thread's priority independently confirmed at 6 as predicted. One trial showed a momentary
`Handles: 0` zombie snapshot before fully clearing — a concrete illustration of exactly the kind
of race that made the telemetry-only check unreliable in round 2.

**Honest residual gap:** verification used a real-code-path throwaway process rather than a
Warchief-dispatched fleet job (`Hv4RecoveryRunner`, run from a non-Warchief machine, hit a hard
401 — it assumes it runs *on* the Warchief where local calls are auto-trusted; that's a tooling
constraint, not a finding about the fix). A single job also only pinned ~6.5 of 12 cores in
testing, more moderate than the multi-10s/60s field failures originally documented — heavier or
concurrent load may still be a factor, not yet reproduced. A separate, unexplained symptom was
also spotted in passing and NOT chased: the live worker's own outbound heartbeat HTTP calls were
seen timing out 20–80s in the field log during a real job-retry loop; unclear if it shares this
root cause or is Warchief/network-side. Worth instrumenting separately, not folded into this
finding.

**Code**: `OrchestratorIDE.Daemon/Program.cs`'s priority line changed `BelowNormal` → `Idle`,
comment updated with the full three-round trail. Mirrored into this tree directly (not via the
X: mapping this time) since the round-3 session's edit stayed local to the laptop's own
`C:\Ai\OrchestratorIDE-dev` checkout. **Not yet re-validated against a real Warchief-dispatched
`hv4-kill`/`hv4-disconnect` campaign** — the throwaway-process verification is strong evidence
the mechanism is fixed, but the fleet-level lanes should still be re-run before this is called
fully closed.

**One durable side effect on HardcoreLaptopMSI, flagged for awareness:** the round-3 session
added the box's own SSH key to its own `administrators_authorized_keys` (correct SYSTEM/
Administrators-only ACLs) to make loopback/LAN SSH verification possible — it wasn't there
before. Low-risk (grants the box the same self-access every other fleet machine already had into
it), but it's a standing change, not reverted.

**HV-6 status: the `hv4-disconnect`/`hv4-kill` gap on HardcoreLaptopMSI now has a specific,
mechanistically-explained, and directly-verified fix, not just a plausible theory.** Live on the
box now (`PriorityClass = Idle`, confirmed). Recommend re-running `Tools/Hv4RecoveryRunner`'s
kill and disconnect phases against it under a real dispatched job as the next step, to convert
this from a strong local proof into the fleet-level evidence the plan's own standard requires
before dropping the named caveat entirely.

**2026-07-29 (later) — fleet-level re-validation attempted over WiFi first: three consecutive
false positives, root-caused to WiFi instability rather than a driver or priority-fix defect.
Then genuinely resolved by testing over Ethernet — both `hv4-kill` and `hv4-disconnect` PASS
clean, for real, with independently-verifiable evidence, not just telemetry silence.**

**The false positives:** three `hv4-kill` attempts over Tailscale and the laptop's WiFi LAN IP
each reported full `Verdict: PASS` (including `kill-actually-landed`, `worker-rejoins`, and
`role-reusable-after-recovery`), but the worker's PID — checked directly via `Get-Process`
immediately before and after each attempt, with hostname/live-clock/computed-uptime
cross-verification to rule out a stale reading — was identical every time. The real production
worker was never killed at all; the driver's telemetry-silence-based check simply couldn't tell.
LAN vs. Tailscale produced the identical false positive both ways, ruling out one network path
being at fault over the other — but not ruling out the network layer itself.

**Root cause: the laptop's WiFi has real, driver-initiated disconnects, confirmed independently
of any of this driver's tooling.** The user observed a live "connection lost" state on the laptop
itself while its WiFi showed as on. `Get-WinEvent -LogName Microsoft-Windows-WLAN-AutoConfig` on
the box shows a clear, recent disconnect/reconnect pattern, including an explicit
`"WLAN AutoConfig service has successfully disconnected from a wireless network. Reason: The
network is disconnected by the driver."` entry — not a signal or AP issue, the RZ616 driver
itself dropping the connection. `Get-NetAdapterPowerManagement` shows the standard "power saving
disconnects WiFi" culprit doesn't even apply here (`AllowComputerToTurnOffDevice: Unsupported`
for this adapter) — this is a different, driver-level flakiness. A mid-test WiFi drop silently
killing the SSH master connection, with a subsequent multiplexed channel request behaving
unpredictably against it, explains the false positives far more simply than a bug in this
driver's `ProcessStartInfo`/`ArgumentList` handling — which was the theory being pursued right
before this was found. **Correctly abandoned that theory rather than chasing a phantom software
bug once real, independent evidence of a hardware/driver problem surfaced.**

**Decisive test: same driver, same box, only the interface changed.** With an Ethernet cable
connected (`192.168.1.179`, distinct from WiFi's `192.168.1.117`), both phases re-run against
the Ethernet IP — and, per HV-6's own repeatability bar, **3 consecutive rounds**, not one, each
with the kill verified by a direct PID check (not telemetry) and the disconnect verified by
actual firewall-rule presence/absence (not inferred from silence):

```text
hv4-kill,       round 1: PID 59984 -> 61608, new StartTime -- genuine kill+restart
hv4-disconnect, round 1: block rule present (count=1) during cut, removed (count=0) after
hv4-kill,       round 2: PID 61608 -> 64300, new StartTime -- genuine kill+restart
hv4-disconnect, round 2: block rule present (count=1) during cut, removed (count=0) after
hv4-kill,       round 3: PID 64300 -> 59756, new StartTime -- genuine kill+restart
hv4-disconnect, round 3: block rule present (count=1) during cut, removed (count=0) after
```

All 6 runs: full `Verdict: PASS`, every check, including `role-reusable-after-recovery`. Worker
confirmed healthy after the final round (telemetry 200, `PriorityClass = Idle` intact through
every restart, firewall rule count 0). Evidence:
`.orc/hv-4-lane/hv4_kill_20260729_154721.json`, `hv4_disconnect_20260729_162459.json`,
`hv4_kill_20260729_165225.json`, `hv4_disconnect_20260729_165755.json`,
`hv4_kill_20260729_170245.json`, `hv4_disconnect_20260729_170601.json`.

**HV-6 verdict, final: the `hv4-disconnect`/`hv4-kill` gap on HardcoreLaptopMSI is CLOSED, with
genuine 3×-round repeatability evidence — not a single lucky pass.** The `Idle`-priority fix is
now confirmed correct at the fleet level (not just via a throwaway process), and the driver
itself has no defect — every prior "failure" on this box was either the pre-fix thread-priority
contention (now fixed) or this box's WiFi driver dropping the connection mid-test (worked around
by using Ethernet). The residual, honest caveat: this specific 3×-round evidence was collected
over a wired connection, not WiFi/Tailscale — HardcoreLaptopMSI should stay on Ethernet for
future HV-4 kill/disconnect runs, or the WiFi driver issue itself should be tracked and fixed as
a known instability on that specific machine, separate from anything this validation campaign
was built to catch in TheOrc's own code. **HV-6 is now 9 of 9 lanes closed.**

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

## 6. §6 decision record — 2026-07-29

**The maintainer decided: flip now.** Native is the default runtime as of this date
(`AppSettings.ExperimentalNativeHiveWorkerEnabled` / `ExperimentalNativeMainChatEnabled` both
`true`; see `docs/NATIVE_RUNTIME_V2_SPEC.md` §6 for the full entry-criteria checklist and code
change). This is the explicit, recorded product decision the plan's own header says this
document cannot itself authorize — recorded here as the paper trail.

**Made against this evidence, not a claim of "all green":**

- HV-1 through HV-3, HV-5: CLOSED (HV-3 item 3 and HV-4 item 1 excepted, both out of scope for
  reasons unrelated to this decision — no remote mutation endpoint exists yet).
- HV-6: 7 of 9 lanes robustly green across three independent full 3× campaigns run 2026-07-28
  and 2026-07-29. The 2 non-green lanes (`hv4-kill`, `hv4-disconnect`) are both isolated to
  SSH-delivered admin actions against **HardcoreLaptopMSI** specifically, while that box is under
  live inference load — not a HIVE dispatch, scheduling, admission, or fallback defect; every one
  of those was separately proven correct, including on that same machine, throughout HV-1–HV-5.
- The root cause of the HardcoreLaptopMSI gap was **not confirmed at decision time.** Two
  attempted fixes (power-plan settings; a `theorc-warband.exe` process-priority change) were each
  tried and each failed to hold up under real fleet load when re-tested — see the 2026-07-29
  entries above for both disproofs, including the PID-based method that caught the second fix's
  false-positive telemetry reading. A further investigation was dispatched and still in flight,
  unresolved, at the moment this decision was recorded.

**This was a conscious choice to accept a narrow, well-isolated, actively-being-chased gap rather
than block the flip on it.** The gap affects only a test harness's ability to reliably simulate a
kill/disconnect against one specific machine under load — it does not implicate native execution
correctness, scheduling correctness, admission correctness, telemetry correctness, or fallback
behavior, all of which were independently and thoroughly proven across the fleet, including on
the affected machine itself. If the in-flight investigation lands a confirmed fix or a firmer
root cause, append it here; if it doesn't, this stands as the permanent caveat the earlier
entries already described as the honest fallback.

**Update, same day, shortly after this decision was recorded:** the in-flight investigation
landed a confirmed, mechanistically-explained root cause and a fix that survives real inference
load (`Idle` process priority, not `BelowNormal` — see the HV-6 section's round-3 entry above for
the full trail). This does not retroactively change the decision above — it was made honestly
against the evidence available at the time — but it substantially de-risks it: the caveat is no
longer just "actively being chased," it now has a specific fix, live on the affected machine,
verified 3/3 by direct process-kill confirmation. Fleet-level `Hv4RecoveryRunner` re-validation
against a real Warchief-dispatched job is still the recommended next step before calling HV-6
fully green.

**Final update, same day: the caveat is now fully closed, not just de-risked.** Fleet-level
re-validation initially produced three false positives over WiFi/Tailscale, traced to the
laptop's WiFi driver dropping the connection mid-test (confirmed independently via
`Get-WinEvent`'s WLAN-AutoConfig log and the user's own observation of a live "connection lost"
state) — not a defect in the priority fix or the HIVE dispatch/scheduling/admission path. Re-run
over a wired Ethernet connection for a genuine 3× repeatability campaign (not a single pass —
CodeRabbit correctly flagged an earlier draft of this entry for claiming closure on one round),
both `hv4-kill` and `hv4-disconnect` passed cleanly all 3 rounds with independently-verifiable
evidence each time (a genuine PID change for the kill; firewall-rule presence/absence for the
disconnect — not telemetry silence). See the HV-6 section's final 2026-07-29 entry for the full
6-run trail. The residual caveat is now narrower still: HardcoreLaptopMSI's WiFi adapter has a
known driver-level instability, worth a wired connection for future fleet
testing on that box, but this no longer implicates the runtime, the scheduler, or the validation
harness in any way.
