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
| **Ollama** | `production` (compatibility path) | **Yes**, today | `AppSettings.Backend = Ollama` (default) | — it *is* the fallback target for the other lanes | Onboarding, model management, current specialist deployment (`theorc-toolcaller`) |
| **Native in-process** | `opt-in` | No | Settings → `ExperimentalNativeMainChatEnabled` / `ExperimentalNativeHiveWorkerEnabled`, or a workload constructing `NativeWithFallbackRuntime` directly | Falls back to Ollama automatically on load/infra failure — see [Fallback mechanics](#fallback-mechanics) below | Context Fabric, future Foundry specialists |
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

**The precise remaining gap**: `RuntimeName` is a static per-instance label. For a run using `NativeWithFallbackRuntime`, every report says `"NativeWithFallback"` regardless of whether every single call actually stayed native or some fraction silently fell back to Ollama mid-run. The report cannot currently distinguish "100% native" from "60% native, 40% fell back" — both produce the identical `RuntimeName` string. For ordinary chat/swarm use this is a reasonable simplification (the Activity Log warning covers it live). For evidence-grade benchmark work specifically — where the review's standard is "never silently substitute another runtime" — this is a real, unclosed gap: **per-call fallback counts are not currently aggregated into benchmark report output.**

This is tracked as open work, not fixed by this document. A future pass should have `NativeWithFallbackRuntime` accumulate a fallback counter and have the Context Fabric report writers include it (e.g. `"runtime": "NativeWithFallback", "fallback_calls": 3, "total_calls": 260"`).

## What the UI shows today vs. what the review asked for

The review's ask: the UI should always show requested runtime, actual runtime, model + quantization, whether fallback occurred, why, and whether the workload permits fallback.

| Signal | Shown today? | Where |
|---|---|---|
| Requested runtime | Yes | `AppSettings.Backend` / the experimental-toggle state in Settings |
| Actual runtime (per call) | Partially | Activity Log warning fires *only when a fallback happens* — the steady-state "still on native" case has no persistent indicator |
| Model + quantization | Yes, for Ollama/depot models | Models panel, Model Benchmark window |
| Whether fallback occurred | Yes, transiently | Activity Log (this session) |
| Why fallback occurred | Yes | The exception message is passed into the Activity Log warning |
| Whether the workload permits fallback | Implicit only | `RuntimeAdmissionDeniedException` exclusion means capacity-denial never silently falls back, but this isn't surfaced as a distinct "this workload requires native, no fallback" indicator anywhere in the UI |

A persistent runtime-status indicator (not just a scrollable log line) and workload-level fallback-permission surfacing remain open UI work.

## Onboarding language

Quick Start and the installation guide default to Ollama because it is genuinely the easiest path today (`production` status per `CURRENT_STATE.yaml`) — that default is not being changed by this document. What changes: both docs now link here so a reader learns native/llama.cpp are real, supported first-class lanes rather than discovering `InferenceBackend.LlamaCpp` by reading source code.
