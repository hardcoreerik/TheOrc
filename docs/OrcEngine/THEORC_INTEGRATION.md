# TheOrc Integration

## Current boundary

**VERIFIED:** TheOrc’s `IModelRuntime` is a backend-neutral streaming surface carrying message history, tool descriptions, sampling inputs, text deltas, tool callbacks, usage callbacks, health, and stats. `ILocalModelRuntime` adds GGUF model load and adapter operations.

**VERIFIED:** `LLamaSharpRuntime` is the production/default in-process implementation. It uses LLamaSharp 0.27.0 and llama.cpp native backends. `OllamaRuntime` remains an explicitly selected compatibility path; `LlamaCppServerRuntime` is an opt-in server path.

## Integration principle

OrcEngine proves itself standalone first. The product adapter should be thin and reuse current TheOrc orchestration rather than creating a second scheduler, model depot, prompt policy, or UI path.

## Proposed stack

```text
AgentLoop / Chat / Swarm / HIVE / Context Fabric
                  |
            IModelRuntime
                  |
        OrcEngineRuntime (C#)
                  |
       OrcEngine C ABI + native library
                  |
        OrcEngine model/context/backend
```

## Managed responsibilities

- validate product configuration and model path;
- own native handles with `SafeHandle` or equally robust lifetime primitives;
- translate `AgentMessage` and tools through an approved prompt path;
- encode strings as explicit UTF-8;
- map cancellation;
- stream complete decoded text chunks;
- translate native errors without losing codes/details;
- report actual runtime, model, backend, cache, and fallback state;
- call existing tool parser only when constrained decoding is unavailable and policy permits.

## Native responsibilities

- model and context lifecycle;
- tokenizer and raw prompt tokens;
- numerical execution and cache;
- sampling/decoding capability;
- allocation/timing measurements;
- structured capabilities and errors.

## Contract fit questions

`IModelRuntime` currently passes message history rather than a raw prompt and has sampling fields shaped by cross-backend compatibility. Integration research must decide:

- whether `OrcEngineRuntime` formats prompts using current `NativePromptBuilder`;
- how model-specific embedded templates are represented;
- how unsupported sampling options are surfaced;
- how token usage distinguishes prompt and generated tokens;
- whether OrcEngine implements `ILocalModelRuntime` before adapters exist;
- whether a narrower internal capability interface is needed without polluting the shared contract.

Do not add a new abstraction until a concrete mismatch exists.

## Runtime selection

Initial product posture:

- feature flag defaults false;
- explicit experimental label;
- explicit model compatibility check before selection;
- no automatic selection based only on `.gguf` extension;
- current native-default, fail-closed, and explicit-compatibility configuration unchanged;
- user-visible actual backend and error reason.

## Fallback policy

For correctness/evidence workloads, OrcEngine must fail closed. Silent substitution would invalidate comparison and benchmark evidence.

If a future compatibility UI offers fallback:

- it occurs only before observable output;
- admission/capability denials are not silently rerouted;
- fallback is explicit in per-call telemetry and saved artifacts;
- partial OrcEngine state/output is never spliced with another backend;
- the workload declares whether fallback is permitted.

Reuse lessons from `NativeWithFallbackRuntime`; do not assume its policy automatically fits OrcEngine research.

## ModelDepot and RuntimeOrchestrator

Potential integration should reuse model identity, role binding, admission, and lifecycle concepts. However, existing classes depend on `LLamaSharpRuntime`-specific executor seams in places. First map concrete coupling; then make only the minimal change required for a proven OrcEngine capability.

## Telemetry mapping

Expose measured:

- runtime name `OrcEngine`;
- engine/build/API version;
- model hash and declared name;
- CPU/CUDA backend and device;
- context length and active position;
- host/device weight, cache, and workspace bytes;
- prompt/decode timings and token counts;
- prompt-template path;
- fallback allowed/occurred/reason.

Unknown stays null/unknown.

## Packaging

Package exact native binaries by runtime identifier and backend. Verify dependency discovery on clean machines. Do not reuse LLamaSharp native DLL names or search rules. Record compiler, standard library, CUDA dependency, and engine commit in the artifact.

## Integration verification

1. Standalone fixture passes.
2. Managed wrapper load/tokenize/decode/dispose loop passes repeatedly.
3. `IModelRuntime` streaming preserves text and tool callback semantics.
4. Cancellation cleans up.
5. Unsupported model fails before generation.
6. Runtime identity remains accurate.
7. One manual TheOrc `/verify` session records actual backend and no fallback.
8. Existing runtime targeted tests remain green.

## Rollback

Disable the experimental flag and remove selection exposure. Existing runtimes and their model assets remain untouched. No data migration is required for the initial integration.
