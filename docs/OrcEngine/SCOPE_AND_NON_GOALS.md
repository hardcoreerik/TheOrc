# Scope and Non-Goals

## Scope rule

OrcEngine advances through evidence-gated increments. A later capability is not “partially done” merely because a type, interface, or placeholder exists.

## Initial committed scope

The first executable engine milestone is restricted to:

- the exact text-only decoder profile `OE-L0-SYNTH-1`, followed by one separately pinned real-model candidate;
- one small, license-reviewed model artifact;
- CPU execution only;
- float32 weights and activations, or a documented conversion to float32 at load time;
- batch size one;
- one sequence;
- fixed context length;
- causal attention;
- RMSNorm, RoPE, attention, SwiGLU, residuals, embeddings, and output projection;
- deterministic tokenization and detokenization for the pinned model;
- greedy next-token selection;
- deterministic logits and intermediate-tensor comparison;
- a standalone CLI or test harness before TheOrc integration.

## Phase 0 scope

Phase 0 does not implement the engine. It establishes:

- a pinned oracle implementation and commit/version;
- immutable model and tokenizer hashes;
- exact input token fixtures;
- tensor-dump and logits capture points;
- comparison tolerances;
- an artifact schema;
- a repeatable command line;
- provenance and licensing notes;
- failure triage rules.
- the machine-readable [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml), where missing and skipped required checks fail.

## Explicit non-goals for the first engine

- Replacing LLamaSharpRuntime, llama.cpp, or Ollama.
- Competitive tokens per second.
- CUDA, Vulkan, Metal, HIP, SYCL, DirectML, or WebGPU.
- Quantized execution.
- Mixture-of-experts, hybrid recurrent, state-space, multimodal, encoder-only, or encoder-decoder architectures.
- Continuous batching or parallel sequences.
- LoRA loading or adapter hot-swap.
- Prefix sharing across roles.
- Grammar-constrained decoding or tool-call grammars.
- Speculative decoding.
- Multi-GPU or distributed inference.
- HIVE layer/expert partitioning.
- Model training or conversion from training checkpoints.
- A server protocol, OpenAI-compatible API, or public SDK.
- General-purpose autograd.
- A dynamic training graph.
- A GUI.

## Later research scope, not commitment

After float32 CPU correctness, the roadmap may consider:

1. GGUF-backed float16 storage with float32 accumulation.
2. Q8_0 reference dequantization.
3. Q4_0 reference dequantization.
4. Tiled and threaded CPU matrix multiplication.
5. AVX2 kernels behind a scalar oracle.
6. CUDA memory and cuBLAS baseline.
7. Quantized CPU/GPU dot products.
8. Multiple sequences and continuous batching.
9. Role-aware caches and adapter placement.
10. Experimental TheOrc integration.

Each item requires a separate decision and evidence gate.

## Compatibility policy

“Supports GGUF” is too broad. OrcEngine will report compatibility at the tuple level:

```text
(GGUF version, architecture, tokenizer model, tensor types, metadata requirements,
 context features, backend, operating system, hardware)
```

Unknown combinations fail with a precise unsupported-feature error. They do not fall through to guessed tensor names, default dimensions, or fabricated special-token IDs.

## Ownership boundary

Allowed foundational dependencies may include:

- C++ standard library;
- CMake and platform build tools;
- operating-system file mapping and threading primitives;
- BLAS for an early dense reference path;
- CUDA runtime and cuBLAS in the CUDA phase;
- a test-only reference framework such as PyTorch;
- scripts that inspect or convert test artifacts.

Not allowed as OrcEngine execution dependencies:

- llama.cpp or GGML execution APIs;
- LLamaSharp;
- Ollama;
- another complete transformer inference runtime.

Those systems may remain test oracles in isolated tooling.

## Product boundary

TheOrc integration is deliberately late. The standalone engine must prove load, forward pass, cache update, greedy decoding, cancellation, and cleanup before an `IModelRuntime` adapter is proposed. The integration must remain opt-in and must not change current default/fallback behavior.

OrcEngine does not need to discover an absolute capability prohibition before it may continue. Before Phase 1, the maintainer must approve at least one bounded value thesis: either the current LLamaSharp/llama.cpp path prevents a required capability, or OrcEngine has a plausible route to a material measured improvement such as lower time to first token, higher prompt/decode throughput, lower RAM/VRAM, stronger load reliability, better observability/admission, simpler deployment, or better structured-output validity. Claims of “better answers” require controlled evidence identifying what changed—weights, quantization, context, prompt, cache, or decoding—because identical model math is expected to produce equivalent logits within tolerance.

## Scope change process

A scope expansion requires:

1. a motivating measured limitation;
2. a proposed minimal change;
3. effect on correctness and security;
4. test and benchmark plan;
5. maintenance-cost estimate;
6. an entry in [Decision Log](DECISION_LOG.md);
7. maintainer approval.

See [Engineering Roadmap](ENGINEERING_ROADMAP.md) and [Risk Register](RISK_REGISTER.md).
