# TheOrc Native Runtime — Phase 0 Spec

> **Status:** PLANNING ONLY. Do not implement until ORC ACADEMY v3 lands (A/B eval + adapter registration complete).
> **Author:** Claude (code-grounded). ChatGPT and Grok contributed the strategic framing and phasing; this doc is the authoritative spec because it is written against the actual codebase, not a generic mental model.
> **Canonical location:** `.grok/RUNTIME_PHASE0_SPEC.md`. Referenced from `docs/ROADMAP.md` and `.grok/PROJECT_TRUTH.md` §4.

---

## 0. Why this doc exists (and supersedes the AI sketches)

Three good-faith sketches preceded this (ChatGPT ×1, Grok ×2). They agree on strategy and converge on phasing, and both are adopted below. But all three were written **without reading the code**, and each repeated the same three errors. This spec keeps their good parts and overrides the wrong ones with code reality.

| What the sketches said | Code reality | This spec |
|---|---|---|
| `GenerateAsync(string prompt)` → `IAsyncEnumerable<InferenceToken>` | Real contract takes `IEnumerable<AgentMessage> history` + `IReadOnlyList<object> tools` and emits tool calls via an `onToolCall` callback ([AgentLoop.cs:248](../OrchestratorIDE/Core/AgentLoop.cs#L248)) | Interface carries **history + tools + tool-call callback**. A single-prompt/no-tools interface would silently kill tool calling — the core of the product. |
| "Update dependency injection" | **There is no DI container.** Every client is `new OllamaClient(...)` in code-behind | DI is a **fork in the road, decided explicitly** (§4), not assumed. |
| "Phase 1: build LlamaCppServerRuntime" | [`LlamaServerManager`](../OrchestratorIDE/Core/LlamaServerManager.cs) already manages `llama-server.exe` lifecycle, health, GPU layers, and HIVE RPC fan-out; [`OllamaClient`](../OrchestratorIDE/Core/OllamaClient.cs) already switches backends via `InferenceBackend` | Phase 1 = **wrap the existing manager**, not build new. |
| "LLamaSharp does zero-merge LoRA hot-swap" (stated as fact) | Unverified; a KV cache built with an adapter active is not valid for base inference | Hot-swap is **spike-before-roadmap** (§7), not a promised feature. |

**Adopted from the sketches:** the P0→P5 phasing, the KV-cache "research not feature" caveat (ChatGPT), the moat articulation and the 3-role AdapterManager idea (Grok), the cross-platform / OS-expansion tie-in (Grok).

---

## 1. Scope of Phase 0

Phase 0 is the **safe abstraction layer only**. One deliverable: an `IModelRuntime` interface that the existing Ollama path implements with **zero behavior change**, proven by routing at least one real generation call site through it.

**In scope**
- `IModelRuntime` interface extracted from the *real* `StreamCompletionAsync` signature.
- `OllamaRuntime` — thin adapter over the existing `OllamaClient` (delegates, does not rewrite).
- Supporting `RuntimeHealth` / `RuntimeStats` records that report **only what is actually known** (null fields, never invented numbers).
- TODO stubs (comments only) for `LlamaCppServerRuntime` and `LLamaSharpRuntime`.
- One migrated call site + a test using the existing `FakeOllamaClient` seam.
- Doc updates (ROADMAP, PROJECT_TRUTH) marking this as v2.0 direction.

**Explicitly NOT in Phase 0**
- LLamaSharpRuntime implementation, GGUF downloader (ModelDepot), GPU/backend detector, LoRA hot-swap, prefix KV cache, OrcScheduler, HIVE peer runtime dispatch. All deferred to later phases (§6).

---

## 2. The real generation contract (ground truth)

Every generation call site uses this method on `OllamaClient` ([OllamaClient.cs:193](../OrchestratorIDE/Core/OllamaClient.cs#L193)):

```csharp
public virtual async IAsyncEnumerable<string> StreamCompletionAsync(
    string model,
    IEnumerable<AgentMessage> history,        // full multi-turn conversation
    IReadOnlyList<object>? tools = null,       // tool schemas (ToolDefinition.ToOllamaSchema())
    double temperature = 0.1,
    int maxTokens = 4096,
    Action<ToolCall>? onToolCall = null,       // structured tool calls, via callback
    Action<int, int>? onUsage = null,          // (promptTokens, completionTokens), via callback
    CancellationToken ct = default)
```

- Yields raw **text deltas** as `string`. Tool calls and usage come back through **callbacks**, not the yielded stream. This split is load-bearing: the UI streams text live ([AgentLoop.cs:257](../OrchestratorIDE/Core/AgentLoop.cs#L257)) while tool calls accumulate separately.
- The model-list / connectivity / VRAM-eviction methods (`GetInstalledModelsAsync`, `IsReachableAsync`, `GetLoadedModelsAsync`, `EvictAndVerifyAsync`) are also part of how the app drives the backend.
- Backend already abstracted: `InferenceBackend { Ollama, LlamaCpp }` ([AppSettings.cs:14](../OrchestratorIDE/Core/AppSettings.cs#L14)); the streaming endpoint (`/v1/chat/completions`) is identical for both — only model discovery differs.

**The test seam already exists:** `FakeOllamaClient : OllamaClient` overrides the `virtual` methods ([FakeOllamaClient.cs:32](../OrchestratorIDE/Tests/FakeOllamaClient.cs#L32)). The interface must be satisfiable by this fake with no rework, or it is the wrong interface.

---

## 3. The interface (copy-paste target)

Extract from the working signature. Do **not** invent a parallel record hierarchy that the default backend can't satisfy.

```csharp
// OrchestratorIDE/Core/Runtime/IModelRuntime.cs
namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// Backend-neutral inference surface. The orchestration layer (AgentLoop,
/// SwarmSession, HiveWorkerAgent, ChatEngine) depends on this instead of a
/// concrete client, so the backend can change without touching the swarm.
///
/// Phase 0 implementation: OllamaRuntime (wraps the existing OllamaClient).
/// Default backend stays Ollama until ModelDepot + installer story is solid.
/// </summary>
public interface IModelRuntime
{
    /// <summary>Human-readable backend name for logs/telemetry, e.g. "Ollama".</summary>
    string RuntimeName { get; }

    /// <summary>Quick connectivity check (≤3s). Maps to OllamaClient.IsReachableAsync.</summary>
    Task<bool> IsReachableAsync(CancellationToken ct = default);

    /// <summary>List model IDs the backend can serve. Maps to GetInstalledModelsAsync.</summary>
    Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default);

    /// <summary>
    /// Stream a completion. Text deltas are yielded; tool calls and token usage
    /// are delivered via callbacks (matches the existing contract exactly).
    /// </summary>
    IAsyncEnumerable<string> StreamCompletionAsync(
        string model,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        CancellationToken ct = default);

    /// <summary>What the runtime knows about itself right now. Unknown fields = null.</summary>
    RuntimeHealth GetHealth();

    /// <summary>Perf telemetry. Every field nullable — return null, never a fake number.</summary>
    RuntimeStats GetStats();
}

public sealed record RuntimeHealth(
    bool IsAvailable,
    string RuntimeName,
    string? ActiveModel = null,
    string? Message = null);

public sealed record RuntimeStats(
    string RuntimeName,
    string? ActiveModel = null,
    double? TokensPerSecond = null,        // null until measured
    TimeSpan? LastTimeToFirstToken = null, // null until measured
    long? EstimatedVramBytes = null);      // null on Ollama (not exposed per-process)
```

### Capability interface (keep load/hot-swap OUT of the base)

`LoadModelAsync` / `UnloadLoRAAsync` / GGUF-path loading are **LLamaSharp capabilities Ollama cannot honor**. Putting them in the base interface forces `OllamaRuntime` to throw `NotSupportedException` — a leaky abstraction. Instead, native-only capabilities live in a separate interface that only the native runtime implements:

```csharp
// Implemented by LLamaSharpRuntime ONLY (Phase 2+). OllamaRuntime does not implement this.
public interface ILocalModelRuntime : IModelRuntime, IAsyncDisposable
{
    Task<ModelLoadResult> LoadModelAsync(
        string baseGgufPath, string? adapterPath = null,
        RuntimeOptions? options = null, CancellationToken ct = default);

    Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default); // spike first (§7)
}

public sealed record RuntimeOptions(
    int ContextLength = 8192, int GpuLayers = -1, bool PreferGpu = true);

public sealed record ModelLoadResult(
    bool Success, string RuntimeName, string ModelRef, string? Message = null);
```

Callers that only generate depend on `IModelRuntime`. Only the native-runtime owner (`SessionManager`, landed Phase 3) depends on `ILocalModelRuntime` — note this scopes `SessionManager` to in-process GGUF runtimes only; `OllamaRuntime` is a thin `IModelRuntime` passthrough and does not implement `ILocalModelRuntime`, so it is never managed by `SessionManager`.

---

## 4. The DI decision (must be made, not assumed)

There is **no DI container today** — clients are constructed with `new OllamaClient(...)` at ~9 sites (§5). Two honest options:

- **Option A — No DI (passthrough).** Construct `IModelRuntime` exactly where `OllamaClient` is constructed today (e.g. `new OllamaRuntime(new OllamaClient(host, backend))`), pass the interface instead of the concrete type. Smallest possible change, truest to "zero behavior change." **Recommended for Phase 0.**
- **Option B — Introduce DI.** Add `Microsoft.Extensions.DependencyInjection`, register `IModelRuntime → OllamaRuntime` as a singleton, resolve at composition root. This is a real architectural change and should be a **separate, deliberate task** — ideally aligned with the Avalonia migration if that work is introducing a service container anyway. Do **not** smuggle DI in under "Phase 0, zero behavior change."

**Decision:** Default to **Option A** unless the Avalonia host is already standing up a DI container, in which case align with it. This choice is a prerequisite — resolve it before writing code.

---

## 5. Call-site inventory (the real surface)

Generation call sites using `StreamCompletionAsync` (these consume `IModelRuntime`):

| Site | File | Note |
|---|---|---|
| Boss plan + execute loop | [AgentLoop.cs:97](../OrchestratorIDE/Core/AgentLoop.cs#L97), [:248](../OrchestratorIDE/Core/AgentLoop.cs#L248) | **Migrate this one in Phase 0** — highest-value proof |
| Swarm workers | [SwarmSession.cs](../OrchestratorIDE/Agents/SwarmSession.cs) | spins up per-node clients dynamically ([:162](../OrchestratorIDE/Agents/SwarmSession.cs#L162)) |
| HIVE remote worker | [HiveWorkerAgent.cs](../OrchestratorIDE/Services/Hive/HiveWorkerAgent.cs) | trust-boundary sensitive — migrate carefully |
| Agent Builder | [AgentBuilderDialog.xaml.cs](../OrchestratorIDE/UI/Dialogs/AgentBuilderDialog.xaml.cs) | v1.9 Avalonia dialog work in flight |
| Just-Chat | [ChatEngine.cs](../OrchestratorIDE/Research/ChatEngine.cs) | |
| Test fake | [FakeOllamaClient.cs](../OrchestratorIDE/Tests/FakeOllamaClient.cs) | already overrides the signature |

Construction sites: MainWindow ×2, SwarmSession, SwarmBoardPanel, ModelWikiWindow, AutoTestRunner, plus the `OllamaClient` ctor. **Phase 0 migrates one (AgentLoop); the rest follow incrementally** — do not big-bang all six.

---

## 6. Phasing (authoritative)

| Phase | Scope | Est. (solo) | Risk |
|---|---|---|---|
| **0 — Abstraction** | `IModelRuntime` + `OllamaRuntime` + records; migrate AgentLoop; one test; DI decision per §4. Zero behavior change. | 2–3 days | Low |
| **1 — Server bridge** | `LlamaCppServerRuntime` that **wraps existing `LlamaServerManager`** + `OllamaClient(Backend=LlamaCpp)`. Mostly wiring; proves the abstraction across two backends. | 2–4 days | Low |
| **2 — Native in-process** | `LLamaSharpRuntime : ILocalModelRuntime`. Direct GGUF + LoRA load, streaming, stats. Backend package detection (Cuda12/Cpu) at first-run/install. The real "no Ollama dependency" win. | 1–2 weeks | Medium |
| **3 — Orchestration** | `ModelDepot` (registry + first-run downloader), `SessionManager` (persistent base model in VRAM), `AdapterManager` (boss/worker/reviewer LoRAs), telemetry (TTFT, tok/s, VRAM). Installer first-run UX is the long pole. | 3–5 weeks | Medium-High |
| **4 — Swarm scheduling** | `OrcScheduler` — capability + VRAM + lane-aware dispatch; pipeline boss→workers (prefill workers while user reviews plan). | 2–3 weeks | High |
| **5 — Optimization (research)** | Prefix KV cache for the shared warband/system prompt; multi-LoRA cache experiments. **Not blocking. Treat as research.** | open | Research |

**KV caveat (verbatim, keep forever):** Full shared KV cache across *different* LoRA-specialized agents is not guaranteed safe or simple — adapters change activations, and a cache built with one adapter active is invalid for another. Start with **simple prefix caching of the common warband/system prompt only**. Deeper sharing is a research track, never a promised Phase deliverable.

---

## 7. Required spike before Phase 2/3 roadmapping

Before LoRA hot-swap appears on any committed roadmap, run a **half-day spike**:

- Load Gemma 4 12B base in LLamaSharp with the v3 adapter applied; generate; detach adapter; generate base; re-attach. Measure: does detach/attach require a context rebuild? What is the real cost? Does the KV cache survive an adapter change (expected: no)? **[Closed 2026-06-18 against a substitute pair — see finding below for why, and for the result.]**
- Confirm LLamaSharp's actual LoRA API surface (runtime apply/scale vs. load-time only) on the pinned NuGet version.
- **Output:** a one-paragraph finding in this doc. If hot-swap needs a context rebuild, `AdapterManager` keeps **separate contexts per role** rather than swapping in place — adjust Phase 3 accordingly.

**Finding (2026-06-18, empirical spike run, broadened across 2 LoRA samples — harness at `.grok/spike-assets/HotSwapSpike/`, paths overridable via args/env vars; raw run transcripts committed at `results-sample1-eltorio-appreciation.log` and `results-sample2-bunnycore-r1.log` in that same directory, addressing the Grok finding that the prior version of this paragraph made claims with no diff-backed evidence):**
Used a locally-available Llama-3.2-3B-Instruct fork (not the exact Gemma 4 12B/v3 pair — v3's adapter only exists in PEFT/safetensors form, no GGUF conversion done; the fork substitution is a lineage caveat on output *quality*, not on the *mechanism* result below, since tensor shapes match) plus two real downloaded GGUF LoRAs trained for genuinely different tasks — `eltorio/Llama-3.2-3B-appreciation` (essay feedback) and `bunnycore/Llama-3.2-3B-R1-lora` (reasoning-style) — via `BatchedExecutor`/`SafeLLamaContextHandle` directly. **Both samples produced identical qualitative results on every measured dimension** (no crash on dirty swap, plausible-not-garbled dirty output, exact byte-for-byte detach match), confirming the finding below is a property of the API/KV-cache mechanism, not an artifact of one specific adapter. Sample 2's clean-adapter output was notably more coherent/on-task than sample 1's, which strengthens confidence that sample 1's slightly confused clean-adapter response was that specific adapter's narrow training scope, not a mechanism or base-fork-lineage problem. **LLamaSharp 0.27.0's actual API:** `SafeLlamaModelHandle.LoadLoraFromFile(path)` (38 ms, one-time per adapter file) returns a `LoraAdapter` struct; `SafeLLamaContextHandle.SetLoraAdapters(Span<(LoraAdapter Adapter, float Scale)>)` (~1 ms) attaches it to a **live context** — this is runtime-apply with a per-call adjustable scale, not load-time-only, contradicting the "unverified" framing in §0's table. **The dangerous case is confirmed real and silent:** calling `SetLoraAdapters` on a context with KV cache already populated *without* clearing memory does **not** throw and produces plausible-looking (not visibly garbled) output — meaning a naive hot-swap would silently mix pre-adapter and post-adapter KV cache entries within one sequence with no error signal at all. The only verified-correct path is `MemoryClear(true)` immediately after `SetLoraAdapters`, followed by a full re-prefill: this is **not free but is cheap** — ~850 ms for a short prompt on an RTX 5070 Ti once CUDA graphs are warm (`BatchedExecutor`/`LLamaContext` objects survive; only KV memory and token reprocessing happen, so this is "context rebuild" in the KV-cache sense, not in the C#-object-teardown sense). Detach (`SetLoraAdapters([])` + `MemoryClear`) was verified byte-for-byte exact against the original base-model greedy output, confirming clean reversibility (the harness now asserts this explicitly via a `detachMatchesBaseline` boolean and writes all step output to `results.log` for an auditable record, addressing the first Grok review pass on this finding, which correctly flagged that the original console-only narrative and a buggy "compare to baseline" print weren't diff-backed). **Verdict for AdapterManager:** keep **separate persistent contexts per role**, per this section's own fallback — not because shared-context swapping is catastrophically expensive, but because (a) it's silently unsafe without disciplined `MemoryClear` discipline on every swap, and (b) TheOrc's warband already runs roles concurrently via Swarm, and one `LLamaContext` cannot safely serve concurrent independent sequences regardless of the LoRA question. **Today's exposure is zero**: `LLamaSharpRuntime.StreamCompletionAsync` constructs a fresh `StatelessExecutor` (fresh context) per call, so no KV cache persists across calls to corrupt — this risk only becomes live if a future optimization (persistent multi-turn context per role, or Phase 5 prefix caching) reuses a context across an adapter change without explicit `MemoryClear`.

---

## 8. Hard-rule compliance (carry into acceptance criteria)

- **Local-only.** No new cloud dependency. ✅ (runtime is in-process or local server)
- **No secrets in repo / no Anthropic bulk-gen.** Unaffected; runtime touches inference, not data-gen. ✅
- **Pit Boss local-only.** Native runtime *strengthens* this (no external service). ✅
- **HIVE security boundary.** Runtime lives inside the same trust boundary; HIVE worker dispatch through the runtime is Phase 4+, not Phase 0. ✅
- **Training Pit convention unchanged.** Adapters remain GGUF LoRAs following `train_{KEY}/eval_{KEY}`. Native loading removes the `ollama create` merge step in deploy — a simplification, not a contract change. ✅
- **Reviewer gate.** Every phase increment goes through the existing review workflow (`tools/codex-review.ps1` + Grok Mean Coach). ✅

---

## 9. Phase 0 acceptance criteria

- [ ] Project builds.
- [ ] Existing Ollama-backed generation works unchanged (boss plan + execute, swarm).
- [ ] UI streaming, Training Pit, and HIVE behavior unchanged.
- [ ] At least one generation path (AgentLoop) depends on `IModelRuntime`, not `OllamaClient`.
- [ ] Ollama remains the default runtime.
- [ ] `FakeOllamaClient` satisfies the interface (adapter or direct) with no rework; the existing trust-path tests pass.
- [ ] `RuntimeStats`/`RuntimeHealth` return null for anything not actually measured — no invented VRAM/throughput numbers.
- [ ] DI decision (§4) recorded in the PR description.
- [ ] No `LLamaSharpRuntime` presented as working — stubs are clearly TODO.

---

## 10. Open decisions for Erik

1. **DI: Option A (no-DI passthrough) or B (introduce container)?** Spec recommends A unless Avalonia host already has a container.
2. **Canonical PROJECT_TRUTH location** — this spec treats `.grok/PROJECT_TRUTH.md` as canonical and the root copy as a removable duplicate.
3. **Sequencing after v3** — Phase 0 can land *while* v3 trains (it touches no training code), but per project rule nothing starts until you green-light. Phase 2 (native) is the recommended first big push after v3 A/B eval + adapter registration, since it removes the biggest user-facing friction (Ollama install).

---

## 11. ORCISH TONGUE — the universal tool caller (why native is a capability, not just cleanup)

> **ORCISH TONGUE** is the new name for the system formerly called GOBLIN MIND (the tool-call format intelligence under `Services/ToolCalls/`). Rename inventory: [`.grok/RENAME_GOBLIN_MIND.md`](RENAME_GOBLIN_MIND.md). Renamed to end the GOBLIN MIND / HIVE MIND collision and to name what it actually does: speak every local model's tool-call dialect.

### What it is today (prompt layer — probabilistic)

ORCISH TONGUE probes each model's preferred tool-call format (`FormatProbeEngine`: `BareJson`, `OpenAiJson`, `HermesXml`, `PythonStyle`, `YamlBlock`), injects matching instructions (`BuildToolFormatSection`), then parses defensively — including the `TryParseTextToolCalls` fallback for models that ignore the format and emit loose text. This is **"probe, ask nicely, parse defensively, hope."** It adapts to whatever the model does; it cannot guarantee.

### What native unlocks (decoder layer — deterministic)

llama.cpp supports **grammar-constrained decoding (GBNF)**. In-process via LLamaSharp, the decoder can be constrained so the model **physically cannot emit a token** that breaks the tool-call schema — valid by construction, not by cooperation.

| Today (via HTTP) | Native (ORCISH TONGUE + GBNF) |
|---|---|
| Probe format → ask model to comply | Probe format → **compile to a GBNF grammar** |
| Model may emit malformed JSON | Malformed JSON is **unreachable** |
| `TryParseTextToolCalls` fallback | Fallback becomes **dead code** |
| Works *better* on tool-trained models | Works *equally* on any model, even ones never trained for tool use |

That last row is the real prize: a universal tool caller that depends on the model being good at tool calling is a compatibility layer; one built on constrained decoding makes a dumb 3B model emit perfect structured calls anyway. Same `FormatProbeEngine` probe data — deterministic outcome instead of a hopeful one. ORCISH TONGUE keeps its brain; native gives it teeth.

### Cost reality (do not let this scare off native)

Going in-process, the app inherits the "server-side tool-call translation" that Ollama/llama-server do today. That translation is **not a compute cost** — it is two cheap string operations (render messages+tools into the raw prompt via the GGUF's chat template; parse output back into tool calls), microseconds each. The expensive thing is inference, paid identically on any backend. Net, in-process is **leaner** (drops the server process, the HTTP socket, the per-call JSON serialize). The engineering is moderate because ORCISH TONGUE already owns the parsing + schema rendering; the one genuinely new piece is **chat-template application**, which LLamaSharp largely provides by reading the GGUF metadata. GBNF then makes the parsing half deterministic.

**The big bite in the whole runtime endeavor is ModelDepot/installer (Phase 3) and OrcScheduler (Phase 4) — not tool-call translation.**

### Phasing fit

- ORCISH TONGUE (prompt-layer) keeps working unchanged through Phases 0–1.
- GBNF constrained decoding is a **Phase 2 capability** (needs `LLamaSharpRuntime`). Built on existing `FormatProbeEngine` probe data.
- `TryParseTextToolCalls` is deprecated only once GBNF is proven on the native path — keep it as the fallback for the Ollama/server backends.

### Related: grammar-constrained *plans*

The boss plan JSON can be grammar-constrained the same way (Phase 2+), guaranteeing **structural** validity. Note this does **not** address the v2-regression class — that was *content* poison (tester roles assigned write tasks), which is semantic. The suitability gate remains the defense there. Structural grammar is a quality floor, not a substitute for the gate.
