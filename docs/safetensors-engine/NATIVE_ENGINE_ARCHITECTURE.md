# TheOrc — Native Engine Architecture (Safetensors Spike)

> **Status: 🔲 Planned.** Component design for the spike engine defined in
> [SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md). Everything here is proposed;
> nothing exists. The only existing code referenced (interfaces under
> `OrchestratorIDE/Core/Runtime/`) is described as it is today and is **not modified** by
> this spike.

---

## 1. Design constraints (inherited from the charter)

| Constraint | Consequence |
|---|---|
| Purity Level 2 target (C#-authored kernels, JIT to GPU) | GPU backend must compile C# kernels; §6 decides which |
| Zero changes to the shipped native lane | Separate project (§2); integration is a design note only (§8) |
| fp16 GPU / fp32 CPU-reference, greedy, single sequence | No batching abstractions, no scheduler, no sampler zoo |
| Determinism (charter differentiator 3) | Every source of nondeterminism must be identified and pinned (§7) |
| Offload-first is a **seam**, not an implementation | One interface (§4), one in-VRAM implementation |

---

## 2. Project placement

**Chosen: `Tools/SafetensorsSpike/`** — two projects in one folder:

| Project | Kind | Contents |
|---|---|---|
| `Tools/SafetensorsSpike/SafetensorsSpike.csproj` | console (`net10.0`) | CLI harness: `parse`, `generate`, `parity`, `bench` verbs; report writers |
| `Tools/SafetensorsSpike/Engine/` (same csproj, folder) | — | Format reader, tensors, kernels, model, decode loop |

Tests: `Tools/SafetensorsSpike.Tests/SafetensorsSpike.Tests.csproj` (xUnit), referenced by
nothing in the shipped solution's release path.

**Justification vs the alternative:**

| Option | For | Against |
|---|---|---|
| **`Tools/SafetensorsSpike/` (chosen)** | Matches the `Tools/ContextFabricBench/` precedent exactly (standalone console harness, own csproj, writes reports to an `.orc/...` output dir, exit codes for scripting). Deletable in one commit on NO-GO. Zero footprint in the shipped app assembly, so the isolation requirement is enforced by project structure, not discipline | Namespace lives outside `OrchestratorIDE.*` until graduation |
| `OrchestratorIDE.SafetensorsEngine/` (class library) | Graduation-ready shape; app could reference it later | Prematurely blesses a spike as product structure; invites "just reference it from the app to try it" — the exact seepage the isolation requirement forbids. A library with no consumer is also harder to *run*, and a spike is above all a thing you run |

Unlike `ContextFabricBench` (which compiles against `OrchestratorIDE.Core.Runtime` because
its job is to benchmark the shipped lane), **the spike engine references no shipped
project**. Path A of the benchmark (the LLamaSharp baseline) is driven through the *existing*
`ContextFabricBench`-style approach in a thin bench verb that does reference the app's
runtime project — that one file is allowed the reference; the engine itself is not. This
keeps "engine is dependency-light" checkable by `dotnet list reference`.

---

## 3. Component map

```
SafetensorsRepo (format layer — SAFETENSORS_FORMAT_SPEC.md)
      │  ReadOnlyMemory<byte> per tensor, LlamaConfig
      ▼
WeightStore ──uses──► ITensorAllocator ──backed by──► IComputeDevice
      │                                                  ▲
      ▼                                                  │
LlamaModel (graph: 16 × LlamaLayer + embed + norm + head)│
      │  builds/executes ops via                         │
      ▼                                                  │
IKernelSet (matmul, rmsnorm, rope, attention, swiglu, argmax)
      │
      ▼
DecodeSession (KV cache, position tracking, greedy loop, provenance record)
```

### Interface signatures (implementation target)

All files carry the standard SPDX header
(`// Copyright (C) 2025-present hardcoreerik / TheOrc contributors` /
`// SPDX-License-Identifier: AGPL-3.0-or-later`).

```csharp
namespace TheOrc.SafetensorsSpike.Engine;

/// <summary>A device-resident tensor. Dtype and shape are fixed at allocation.</summary>
public sealed class DeviceTensor : IDisposable
{
    public TensorDtype Dtype { get; }            // F16 | F32
    public IReadOnlyList<int> Shape { get; }
    public long ElementCount { get; }
    public IComputeDevice Device { get; }
}

/// <summary>Allocation + host↔device transfer. One implementation per backend.</summary>
public interface ITensorAllocator : IDisposable
{
    DeviceTensor Allocate(TensorDtype dtype, params int[] shape);
    DeviceTensor Upload(ReadOnlySpan<byte> hostData, TensorDtype sourceDtype,
                        TensorDtype deviceDtype, params int[] shape); // BF16→F16 conversion here
    void Download(DeviceTensor tensor, Span<byte> destination);
    long AllocatedBytes { get; }                 // honest accounting, reported in benchmark output
}

/// <summary>The compute backend boundary. Exactly two implementations in the spike:
/// CpuDevice (fp32, reference) and the GPU backend chosen in §6.</summary>
public interface IComputeDevice : IDisposable
{
    string Name { get; }                          // "cpu-fp32", "ilgpu-cuda-fp16", ...
    string BackendVersionInfo { get; }            // package version + driver — feeds provenance
    ITensorAllocator Allocator { get; }
    IKernelSet Kernels { get; }
    void Synchronize();                           // barrier before timing reads / downloads
}

/// <summary>The seven ops the Llama forward pass needs — no more. Shapes/dtypes are
/// specified per-op in NATIVE_ENGINE_FORWARD_PASS.md; kernels validate them.</summary>
public interface IKernelSet
{
    void MatMul(DeviceTensor a, DeviceTensor b, DeviceTensor dest, bool transposeB);
    void RmsNorm(DeviceTensor x, DeviceTensor weight, DeviceTensor dest, float eps);
    void Rope(DeviceTensor qOrK, int headDim, long positionOffset, RopeScaling scaling);
    void Attention(DeviceTensor q, DeviceTensor kCache, DeviceTensor vCache,
                   DeviceTensor dest, int positionCount, int nHeads, int nKvHeads);
    void SwiGlu(DeviceTensor gate, DeviceTensor up, DeviceTensor dest);
    void Add(DeviceTensor a, DeviceTensor b, DeviceTensor dest);
    int ArgMax(DeviceTensor logits);              // greedy sampling, fp32 compare on device or after download — §7
}

/// <summary>Weight residency seam — differentiator 2 as an ARCHITECTURAL SEAM ONLY.</summary>
public interface IWeightResidency        // see §4
{
    DeviceTensor GetResident(string tensorName);  // spike impl: everything already in VRAM; returns immediately
}

/// <summary>One loaded model + one generation session (single sequence).</summary>
public sealed class DecodeSession : IDisposable
{
    public static DecodeSession Load(SafetensorsRepo repo, IComputeDevice device,
                                     int contextLength /* spike: ≤ 4096 */);
    public DecodeStepResult Prefill(ReadOnlySpan<int> tokenIds);       // returns last-position logits
    public DecodeStepResult DecodeNext(int tokenId);                   // appends, returns logits
    public float[] LastLogitsF32 { get; }         // downloaded, fp32 — parity harness input
    public ProvenanceRecord Provenance { get; }   // §7
}
```

---

## 4. The weight-residency seam (offload-first hook)

Differentiator 2 says residency (VRAM / host RAM / disk) should be a core abstraction. The
spike **does not implement offload** — it implements the *interface shape* that makes
offload a later drop-in rather than a rewrite:

- Every kernel-facing weight access goes through `IWeightResidency.GetResident(name)` —
  never a direct field holding a `DeviceTensor`.
- Spike implementation `AllResidentWeights`: uploads everything at load, `GetResident` is a
  dictionary lookup. Zero policy, zero eviction.
- Design note for Tier-A (not spike work): a future `TieredResidency` implements the same
  interface with per-layer granularity — the mmap'd safetensors file *is already* the disk
  tier (zero-copy host reads, §4 of the format spec), which is the structural advantage
  safetensors-direct loading has over GGUF-through-LLamaSharp here.
- Honesty check written into the spike report: with only one trivial implementation, the
  seam is **unproven as an abstraction**. The report must not claim "offload-ready", only
  "offload-shaped".

---

## 5. Memory allocator

Spike policy — simplest thing that is honest:

- Arena-style: one device buffer per weight tensor (allocated once at load), one KV-cache
  allocation per layer sized for the full spike context (`2 × ctx × n_kv_heads × d_head`
  elements, fp16, per layer — matching the KV formula in the format spec §4), and a small
  fixed set of reusable activation scratch buffers (sized for the worst-case prefill batch)
  allocated once at session start.
- **No allocation inside the decode loop.** This is both a perf floor and what makes the
  steady-state VRAM metric in the benchmark meaningful (a churning allocator makes
  "steady-state" a lie).
- `ITensorAllocator.AllocatedBytes` is exact bookkeeping of live allocations; the benchmark
  compares it against the driver-reported process VRAM delta and reports both numbers —
  the same measured-vs-predicted discipline `LLamaSharpRuntime`'s
  `NativeLoadAllocationAccumulator` applies to llama.cpp's own log lines.

---

## 6. GPU backend decision matrix

**Open Decision OD-A1 — GPU backend.** Decided at the end of Phase 3 day 1
([SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) ST-301) by a half-day matmul
bake-off, not by this table alone. The table pre-registers the criteria:

| Criterion | Weight | ILGPU | ComputeSharp | cuBLAS P/Invoke |
|---|---|---|---|---|
| Purity ladder level | high | **2** — C# kernels JIT'd to PTX (CUDA) or OpenCL/CPU | **2** — C# kernels transpiled to HLSL/DX12 compute | 3 — vendor lib, managed callers |
| fp16 kernel support | high | fp16 storage supported; verify arithmetic path on the pinned version — this is the bake-off's first question | fp16 via HLSL `half` with device support caveats; same verification need | Full (`cublasGemmEx`/`cublasHgemm`) |
| Cross-platform reach | medium | CUDA + OpenCL + CPU fallback; not macOS-Metal | **Windows-only (DX12)** — conflicts with the Avalonia cross-platform direction | NVIDIA-only, any OS with CUDA |
| Expected matmul throughput vs tuned native | medium | Naive kernels: poor; tiled shared-memory kernels: workable — the honest expectation is a real gap vs cuBLAS | Same class as ILGPU | Best available — it IS the tuned native |
| Determinism control | high | Full — we author every kernel, fixed reduction order is ours to guarantee | Full, same reason | cuBLAS makes **no** cross-version/cross-arch determinism guarantee; per-version-per-GPU determinism only with fixed algo selection — weakens differentiator 3 |
| Supply-chain surface | medium | One NuGet, no native binaries beyond driver | One NuGet + DX12 (OS-provided) | CUDA toolkit redistributables — a native dependency chain the differentiator claims to avoid |
| Debuggability of a wrong number | high | Kernel source is C# in this repo | C# in repo, but HLSL transpilation in the middle | Opaque |

**Recommendation: ILGPU as the primary target; cuBLAS P/Invoke as the pre-authorized
Level-3 fallback for the matmul op only** (all other ops stay C#-authored — matmul is where
Level-2 throughput will hurt, and a hybrid "our kernels + vendor GEMM" result is still an
honest, clearly-labeled Level-3 data point per charter criterion G-5). ComputeSharp is
eliminated on Windows-only unless the bake-off finds ILGPU's fp16 path unusable, which is
the reversal condition.

**Reversal conditions (pre-registered):**

| Observed in bake-off | Switch to |
|---|---|
| ILGPU fp16 arithmetic unusable/emulated-only on the pinned version | ComputeSharp (accepting Windows-only for the spike; portability re-opens at Tier-A) |
| Best C# tiled matmul < 10% of cuBLAS on the same shapes | Keep ILGPU for non-matmul ops, adopt the cuBLAS fallback for GEMM, record the purity downgrade in the report |
| Neither compiles/runs on the reference box in half a day | Escalate — this is kill-criterion K-3 territory, surface it immediately rather than burning the week |

Pinned versions are recorded in the spike csproj and echoed into every benchmark report's
provenance block (§7). Version numbers are deliberately **not** written in this doc — the
csproj is the single source of truth; a doc copy would drift.

---

## 7. Determinism and provenance

Differentiator 3 requires knowing every nondeterminism source. Inventory and policy:

| Source | Policy |
|---|---|
| Sampling | Greedy (argmax) for all parity/determinism runs; ties broken by lowest token ID, explicitly — an unstated tie-break is where "bit-identical" quietly dies |
| Floating-point reduction order | Fixed by construction in our kernels (deterministic tree/sequential reduction — specified per-op in [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md)); no atomics-based accumulation anywhere |
| GPU JIT recompilation | Kernel compilation happens once at session load; compiled-kernel cache keyed by backend version |
| cuBLAS fallback (if adopted) | Fixed algorithm selection; determinism claim then narrows to "per driver+GPU+version" and the report must say so |
| Threading on CPU path | Single-threaded reference by default; optional parallel-for over heads/rows only where partial results are written to disjoint memory (no reduction races) |
| `ArgMax` | Computed on downloaded fp32 logits on the host in the spike (simplest deterministic option); device-side argmax is an optimization with a determinism proof obligation, deferred |

`ProvenanceRecord`, emitted with every generation:

```csharp
public sealed record ProvenanceRecord(
    string ModelPath, string WeightsSha256,      // hash of the safetensors byte buffer(s)
    string ConfigSha256,
    string DeviceName, string BackendVersionInfo, string DriverVersion,
    TensorDtype ComputeDtype,
    string PromptSha256,                          // over the token-ID array, not text
    int Seed,                                     // recorded even though greedy ignores it
    string EngineGitCommit);
```

WeightsSha256 over multi-GB files is minutes of I/O — computed once and memoized beside the
repo keyed by (path, size, mtime), the `GgufMetadataReader` caching pattern again.

---

## 8. `IModelRuntime` / `IRoleRuntime` integration — design note only

> **This section is a design note. None of it happens during the spike.** It exists so a
> GO decision has a wiring plan that provably does not disturb the production construction
> path.

How the engine would eventually seat, given the interfaces as they exist today:

| Existing surface | Eventual fit |
|---|---|
| `IModelRuntime.StreamCompletionAsync(model, history, tools, temperature, topP, maxTokens, onToolCall, onUsage, ct)` | A future `SafetensorsRuntime : ILocalModelRuntime` implements it the way `LLamaSharpRuntime` does: model-instance singleton (the `model` parameter unused, per the documented local-runtime convention), text deltas yielded, tool calls parsed from output text via the existing `ToolCallTextParser`. Chat-template application is the genuinely new work — safetensors repos carry the template in `tokenizer_config.json` (Jinja), not GGUF metadata, so the existing `LLamaTemplate` path does not transfer; this is the largest hidden cost in the integration and is called out in [RISK_REGISTER.md](RISK_REGISTER.md) R-10 |
| `ILocalModelRuntime.LoadModelAsync(baseGgufPath, adapterPath, options, ct)` | The parameter name says `baseGgufPath`; the contract is really "path to a loadable model asset". Eventual change: accept a safetensors repo directory — an additive, admission-gated change, **not** made during the spike |
| `ModelDepot` | Already classifies `PeftAdapterDirectory` by probing directory contents; a future scan rule recognizes a safetensors model repo (`config.json` + `model.safetensors[.index.json]`) as a new `RuntimeAssetKind.BaseModelSafetensors`. Additive enum member |
| `ModelAdmissionGate` | Today rejects everything but `BaseModelGguf` — which means **the new kind is fail-closed by default the moment it exists**. That is the correct order: the depot can *see* safetensors repos long before the gate *admits* them, and nothing loads until the gate's policy is deliberately extended |
| `OrcScheduler.EstimateRequiredBytes` | Needs a header source per asset kind: `GgufMetadataReader` for GGUF, the format spec's `PredictSize` for safetensors. Same formula, different metadata reader — the formula already lives in one place and stays there |
| `IRoleRuntime` / `NativeRoleRuntime` | No new work at this layer: `NativeRoleRuntime` composes over `LLamaSharpRuntime` via `RuntimeOrchestrator`/`AdapterManager`. A safetensors-backed role runtime is far future (it would need the per-role persistent-executor equivalent); the spike-graduation path stops at `IModelRuntime` |
| `NoFallbackRuntime` boundary | The v2 fail-closed rule (native errors surface, never silently become Ollama output — `docs/RUNTIME_SUPPORT_MATRIX.md`) applies unchanged: a safetensors-lane failure is an explicit error, never a silent switch to the GGUF lane or Ollama |

**Construction-path guarantee:** nothing above modifies `MainWindow.axaml.cs`'s runtime
construction, `RuntimeOrchestrator`, or any Settings default. Until a deliberate, reviewed
Tier-A change, the engine is reachable only through the `Tools/SafetensorsSpike` CLI.
