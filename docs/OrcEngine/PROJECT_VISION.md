# Project Vision

## Vision statement

OrcEngine explores whether TheOrc should own a narrow, agent-native model inference engine: one that can load a deliberately small set of pretrained model formats, execute a deliberately small set of decoder architectures, expose honest internal state, and eventually cooperate with TheOrc’s roles, Context Fabric, adapters, scheduling, and HIVE execution model.

The project succeeds only if it creates product value beyond ownership for its own sake. That value may be a capability unavailable through the existing stack or a material, reproducible improvement in performance, resource use, reliability, observability, deployment, or workload validity.

## Why investigate this

TheOrc already owns the control plane:

- agent and role orchestration;
- tools and approval boundaries;
- Context Fabric evidence selection;
- model selection and admission policy;
- session, adapter, and role lifecycle;
- product telemetry and user-facing behavior;
- local-first and HIVE execution policy.

The current in-process runtime does not own the numerical execution plane. `LLamaSharpRuntime` calls LLamaSharp, and LLamaSharp is based on llama.cpp. That is a sensible production architecture. OrcEngine investigates whether moving the ownership boundary downward enables material improvements such as:

- first-class role-owned context and cache state;
- exact allocation and residency telemetry;
- deterministic Context Fabric prefix representation;
- adapter-aware memory planning;
- traceable per-operator and per-layer evidence;
- execution semantics designed for multi-agent workloads;
- HIVE-aware partitioning experiments that are difficult to express through current APIs.

These are hypotheses, not promises.

## The intended identity

**DECIDED:** OrcEngine begins as an experimental research backend beside, not instead of, the existing runtimes.

```text
TheOrc orchestration
  |- LLamaSharpRuntime         production/default in-process llama.cpp path
  |- OllamaRuntime             explicit compatibility path
  |- LlamaCppServerRuntime     opt-in out-of-process llama.cpp server path
  `- OrcEngineRuntime          future experimental adapter, absent today
```

OrcEngine is not:

- a renamed LLamaSharp wrapper;
- a private llama.cpp fork presented as independent work;
- a requirement for TheOrc to function;
- permission to weaken fail-closed admission or fallback visibility;
- a commitment to broad architecture or hardware compatibility;
- a reason to stop improving the existing native runtime.

## Product principles

### Correctness before conversation

The first meaningful result is matching reference logits, not producing plausible text. A model can emit convincing text while every internal result is wrong.

### Narrow compatibility before broad claims

One pinned tiny Llama-style model, float32 CPU execution, batch size one, one sequence, and greedy selection are sufficient for the first engine proof.

### Evidence before optimization

Every optimization must preserve a reference path and show measured benefit. “Should be faster” is not evidence.

### Honest boundaries

Using the C++ standard library, platform virtual-memory APIs, BLAS, CUDA runtime, or cuBLAS does not make OrcEngine fake. Using a complete third-party inference engine as the executor would.

### TheOrc remains usable throughout

No OrcEngine phase changes TheOrc's runtime defaults. No OrcEngine failure should block the current LLamaSharp or explicitly selected Ollama lanes. Integration is a late phase after standalone correctness.

### Research can end the project

A valid outcome is that OrcEngine is educationally useful but strategically unjustified. The project must preserve explicit stop gates.

## Strategic success criteria

OrcEngine earns continued investment when all of the following are true:

1. It produces reproducible, explained correctness evidence.
2. The team can maintain the supported surface without slowing TheOrc’s main roadmap unacceptably.
3. At least one TheOrc-native capability cannot be achieved cleanly through LLamaSharp/llama.cpp, or OrcEngine materially outperforms the agreed baseline on a product-relevant metric.
4. Performance is sufficient for the intended experiment, even if not competitive broadly.
5. Security and licensing obligations are understood and testable.
6. Existing runtimes remain available as production paths and reference oracles.

## Failure and stop criteria

Pause or archive OrcEngine if:

- there is no stable deterministic oracle;
- a pinned model cannot match intermediate tensors or logits after the planned reference phases;
- maintenance depends on continuously chasing many GGUF architectures;
- the unique value reduces to branding;
- a small upstream contribution to LLamaSharp or llama.cpp would deliver the same value at materially lower total cost;
- the project consumes resources needed for higher-value TheOrc work without producing evidence;
- licensing provenance for required code or model artifacts cannot be established.

## Long-horizon possibility, not scope

If the narrow engine becomes correct and maintainable, later research may examine agent-oriented execution: role state as a first-class resource, immutable shared prefixes, adapter placement, deterministic evidence-block reuse, and HIVE execution. These ideas remain hypotheses until the reference engine exists.

See [Scope and Non-Goals](SCOPE_AND_NON_GOALS.md), [Engineering Roadmap](ENGINEERING_ROADMAP.md), and [Theory and Assumptions](THEORY_AND_ASSUMPTIONS.md).
