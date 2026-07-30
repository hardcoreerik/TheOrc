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
| **Ollama** | `production` (fallback path) | No, as of 2026-07-29 | `AppSettings.Backend = Ollama`, or automatic on native fallback | — it *is* the fallback target for the other lanes | Onboarding, model management, current specialist deployment (`theorc-toolcaller`) |
| **Native in-process** | `production` | **Yes**, as of 2026-07-29 (`NATIVE_RUNTIME_V2_SPEC.md` §6 flip) | On by default; Settings → `ExperimentalNativeMainChatEnabled` / `ExperimentalNativeHiveWorkerEnabled` remain as the opt-OUT toggles | Falls back to Ollama automatically on load/infra failure — see [Fallback mechanics](#fallback-mechanics) below | General chat/swarm/HIVE use, Context Fabric, future Foundry specialists |
| **llama.cpp server** | `opt-in` | No | Settings → `AppSettings.Backend = LlamaCpp` (`InferenceBackend` enum) | Configurable; not wrapped by `NativeWithFallbackRuntime` today | General local inference without any Ollama dependency |
| **Remote HIVE runtime** | `opt-in`, per-workload | No | HIVE node targeting in Chat/Swarm; campaign dispatch | Depends entirely on the workload's own retry/requeue policy | Multi-node execution, distributed campaigns |

None of these is inherently "more local" than another — all four run on infrastructure you control. The distinction is architectural (in-process vs. subprocess vs. daemon vs. remote node), not a privacy one.

## Fallback mechanics (what actually happens today)

`OrchestratorIDE/Core/Runtime/NativeWithFallbackRuntime.cs` is the real mechanism behind "native, falling back to Ollama":

- **Fallback is fail-closed, not fail-open.** Only a narrow, explicit exception allowlist (`InvalidOperationException`, `ObjectDisposedException`, `TimeoutException`) triggers fallback. `RuntimeAdmissionDeniedException` — the scheduler deliberately saying "no VRAM room for this role right now" — is explicitly excluded, so a capacity problem surfaces as a real error instead of being silently papered over by rerouting to Ollama.
- **Fallback only happens before the first observable output.** Once a text token, a tool call, or a usage callback has reached the caller, a later native failure propagates as an error instead of splicing the fallback backend's output onto a partially-generated turn.
- **When it does happen, it's logged as a visible warning** — `MainWindow.axaml.cs` wires `onFallback` to `AddActivity(ActivityKind.Warning, "Native Runtime", ...)`, so a fallback is not silent. It IS transient (an Activity Log line, not a persistent status indicator) — see the open gap below.

## What "for benchmarks, never silently substitute" actually looks like today

Context Fabric's benchmark/report contracts (`ContextFabricContracts.cs`, `ContextFabricReportWriter.cs`, `ContextFabricBaselineRunner.cs`) already record `RuntimeName` in every report — this is real, checked-in behavior, not aspirational. A CF-7 gate report tells you which runtime object ran the benchmark.

**Corrected 2026-07-30 — this section previously overstated the gap.** All four CF report-producing runners (`ContextFabricBaselineRunner`, `ContextFabricFeasibilityRunner`, `ContextFabricBenchmarkExpansionRunner`; `ContextFabricReportWriter.cs` renders the same shape) hold `IRoleRuntime`, never `IModelRuntime`/`NativeWithFallbackRuntime` — verified by reading each `_runtime` field's declared type. `NativeWithFallbackRuntime` implements `IModelRuntime`; it wraps an `IRoleRuntime` as a dependency, it does not implement that interface itself. So a CF report's `RuntimeName` can only ever be the concrete `IRoleRuntime`'s own name (e.g. `"NativeRoleRuntime"`) — it is **structurally impossible** for a CF report to say `"NativeWithFallback"`, because none of these runners ever hold one. This is independently confirmed by `HiveWorkerAgent`'s own comment on the HIVE dispatch side: *"CF reader pack requires the native role executor — it has no generic-LLM fallback path"* — CF/`native_agent` execution is fail-closed by design, with no Ollama fallback reachable in this call path at all, whether run through the GUI or over HIVE.

**Consequence**: "aggregate per-call fallback counts into CF report output" is not an open implementation gap — there is no fallback happening in this path for a counter to report. Adding a `FallbackCalls` field to the four CF report record types would always read 0, which is worse than no field at all: it implies a check ran and found nothing, when the real answer is that the check does not apply here. `NativeWithFallbackRuntime.FallbackCount` (Native Runtime v2.0 §5.4, landed 2026-07-30) covers main chat, the one place a native→Ollama fallback *can* actually happen in production today. `HiveWorkerAgent.FallbackCount` was also added with the same shape but — per a 2026-07-30 adversarial review — is currently unreachable dead code: every construction path (`HiveService.cs`, `MainWindow.axaml.cs`) passes `Runtime = null`, so `ExecuteTaskAsync`'s fail-closed check is always true and the fallback branch never runs. It'll activate the moment some future HIVE worker path wires a real fallback `Runtime`, with no code change needed there, but don't count it as a second live surface today.

## What the UI shows today vs. what the review asked for

The review's ask: the UI should always show requested runtime, actual runtime, model + quantization, whether fallback occurred, why, and whether the workload permits fallback.

| Signal | Shown today? | Where |
|---|---|---|
| Requested runtime | Yes | `AppSettings.Backend` / the experimental-toggle state in Settings |
| Actual runtime (per call) | Partially | Activity Log warning fires *only when a fallback happens* — the steady-state "still on native" case has no persistent indicator |
| Model + quantization | Yes, for Ollama/depot models | Models panel, Model Benchmark window |
| Whether fallback occurred | Yes, transiently, now with a running count | Activity Log message includes `NativeWithFallbackRuntime.FallbackCount` ("fallback #N this session"), added 2026-07-30 -- still only visible in the moment the line appears, not a persistent indicator |
| Why fallback occurred | Yes | The exception message is passed into the Activity Log warning |
| Whether the workload permits fallback | Implicit only | `RuntimeAdmissionDeniedException` exclusion means capacity-denial never silently falls back, but this isn't surfaced as a distinct "this workload requires native, no fallback" indicator anywhere in the UI |

A persistent runtime-status indicator (not just a scrollable log line) and workload-level fallback-permission surfacing remain open UI work.

## Onboarding language

Quick Start and the installation guide default to Ollama because it is genuinely the easiest path today (`production` status per `CURRENT_STATE.yaml`) — that default is not being changed by this document. What changes: both docs now link here so a reader learns native/llama.cpp are real, supported first-class lanes rather than discovering `InferenceBackend.LlamaCpp` by reading source code.
