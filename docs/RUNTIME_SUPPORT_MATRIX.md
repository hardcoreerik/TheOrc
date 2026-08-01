# TheOrc — Runtime Support Matrix

> Written in response to the 2026-07-12 external release review's P1 finding:
> Quick Start and the installation guide teach an Ollama-only setup while the
> actual direction treats native inference as a first-class lane. This
> document is the "clear runtime matrix" the review asked for. See
> [docs/CURRENT_STATE.yaml](CURRENT_STATE.yaml) for the fixed status
> vocabulary used below.

---

## The four runtime lanes

| Runtime | Current status | Default? | How you switch to it | Fallback behavior | Recommended use |
|---|---|---|---|---|---|
| **Ollama** | Transitional explicit legacy lane | No, as of 2026-07-29 | Disable native main chat, then select `AppSettings.Backend = Ollama` | No automatic cross-runtime fallback | Compatibility while remaining native gaps and the r3 proof-of-concept specialist deployment are retired |
| **Native in-process** | `production` | **Yes**, as of 2026-07-29 (`NATIVE_RUNTIME_V2_SPEC.md` §6 flip) | On by default; Settings → `ExperimentalNativeMainChatEnabled` / `ExperimentalNativeHiveWorkerEnabled` remain as the opt-OUT toggles | Fails closed on prerequisite, admission, load, or execution failure | General chat/swarm/HIVE use, Context Fabric, future Foundry specialists |
| **llama.cpp server** | `opt-in` | No | Settings → `AppSettings.Backend = LlamaCpp` (`InferenceBackend` enum) | Configurable; not wrapped by `NativeWithFallbackRuntime` today | General local inference without any Ollama dependency |
| **Remote HIVE runtime** | `opt-in`, per-workload | No | HIVE node targeting in Chat/Swarm; campaign dispatch | Depends entirely on the workload's own retry/requeue policy | Multi-node execution, distributed campaigns |

None of these is inherently "more local" than another — all four run on infrastructure you control. The distinction is architectural (in-process vs. subprocess vs. daemon vs. remote node), not a privacy one.

## Runtime boundary behavior (what actually happens today)

Native main chat is fail-closed. `MainWindow.axaml.cs` still uses
`NativeWithFallbackRuntime` as the sequencing wrapper, but supplies
`NoFallbackRuntime` as its secondary runtime. That preserves the wrapper's safeguards against
mixing output from two runtimes while ensuring native initialization, admission, model-load,
and execution failures surface as explicit errors instead of becoming Ollama output.

Ollama remains a supported runtime, but only through an explicit opt-out from native main chat.
There is no automatic native-to-Ollama transition in the production main-chat construction path.

**Direction decided 2026-07-31:** native-only is the target. An Ollama path is
migration debt, not a resilience mechanism. If a native workload fails, pause
and repair that native capability, verify the repair, then remove the matching
Ollama dependency from the loop. Do not add new Ollama fallback call sites.

## What "for benchmarks, never silently substitute" actually looks like today

Context Fabric's benchmark/report contracts (`ContextFabricContracts.cs`, `ContextFabricReportWriter.cs`, `ContextFabricBaselineRunner.cs`) already record `RuntimeName` in every report — this is real, checked-in behavior, not aspirational. A CF-7 gate report tells you which runtime object ran the benchmark.

**Corrected 2026-07-30 — this section previously overstated the gap.** All four CF report-producing runners (`ContextFabricBaselineRunner`, `ContextFabricFeasibilityRunner`, `ContextFabricBenchmarkExpansionRunner`; `ContextFabricReportWriter.cs` renders the same shape) hold `IRoleRuntime`, never `IModelRuntime`/`NativeWithFallbackRuntime` — verified by reading each `_runtime` field's declared type. `NativeWithFallbackRuntime` implements `IModelRuntime`; it wraps an `IRoleRuntime` as a dependency, it does not implement that interface itself. So a CF report's `RuntimeName` can only ever be the concrete `IRoleRuntime`'s own name (e.g. `"NativeRoleRuntime"`) — it is **structurally impossible** for a CF report to say `"NativeWithFallback"`, because none of these runners ever hold one. This is independently confirmed by `HiveWorkerAgent`'s own comment on the HIVE dispatch side: *"CF reader pack requires the native role executor — it has no generic-LLM fallback path"* — CF/`native_agent` execution is fail-closed by design, with no Ollama fallback reachable in this call path at all, whether run through the GUI or over HIVE.

**Consequence**: "aggregate per-call fallback counts into CF report output" is not an open implementation gap — there is no fallback happening in this path for a counter to report. Adding a `FallbackCalls` field to the four CF report record types would always read 0, which is worse than no field at all: it implies a check ran and found nothing, when the real answer is that the check does not apply here. `NativeWithFallbackRuntime.FallbackCount` remains available for an explicitly configured compatibility wrapper, but production native main chat supplies `NoFallbackRuntime`, so it cannot become Ollama output. `HiveWorkerAgent.FallbackCount` was also added with the same shape but — per a 2026-07-30 adversarial review — is currently unreachable dead code: every construction path (`HiveService.cs`, `MainWindow.axaml.cs`) passes `Runtime = null`, so `ExecuteTaskAsync`'s fail-closed check is always true and the fallback branch never runs. It'll activate the moment some future HIVE worker path wires a real fallback `Runtime`, with no code change needed there, but don't count it as an active surface today.

## What the UI shows today vs. what the review asked for

The review's ask: the UI should always show requested runtime, actual runtime, model + quantization, whether fallback occurred, why, and whether the workload permits fallback.

| Signal | Shown today? | Where |
|---|---|---|
| Requested runtime | Yes | `AppSettings.Backend` / the experimental-toggle state in Settings |
| Actual runtime (per call) | Partially | Settings reports native depot/budget readiness or probes the explicitly selected legacy runtime; the steady-state runtime has no persistent per-call indicator |
| Model + quantization | Yes, for Ollama/depot models | Models panel, Model Benchmark window |
| Whether fallback occurred | Not applicable to native main chat | Automatic cross-runtime fallback is prohibited in the production construction path |
| Why native failed | Yes | The native error is returned explicitly and the initialization path records an Activity Log warning |
| Whether the workload permits fallback | Partially | Native main chat and native HIVE execution are fail-closed; the Settings copy describes the native no-fallback boundary |

A persistent per-call runtime-status indicator remains open UI work.

## Onboarding language

Quick Start and the installation guide lead with the installer's native-first local setup. Ollama commands are retained only for users who explicitly select that compatibility lane. Both docs link here so a reader can distinguish native in-process execution, Ollama, the managed llama.cpp server, and remote HIVE.
