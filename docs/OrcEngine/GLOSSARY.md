# Glossary

## A

**ABI** — Application Binary Interface. OrcEngine proposes a stable C ABI between the native engine and .NET.

**Activation** — Intermediate tensor values produced while executing the model; not the immutable trained weights.

**Admission** — Decision that sufficient configured resources exist before model/context work begins.

**Agent-native** — A hypothesis that inference state and scheduling can reflect TheOrc roles/workloads directly. It is not a current implementation claim.

**Attention** — Operation combining queries with cached/current keys and values to select relevant prior-token information.

## B

**Backend** — Operator/memory implementation for a compute target such as scalar CPU or CUDA. In TheOrc, “runtime backend” can also mean Ollama/LLamaSharp; documents should qualify the term.

**Batch** — Multiple tokens or sequences processed together. Initial OrcEngine scope is one sequence; prompt evaluation may still process multiple positions as a matrix later.

**BLAS** — Standard basic linear algebra interfaces/implementations. Using BLAS does not by itself outsource transformer inference semantics.

## C

**Causal mask** — Restriction preventing a token position from attending to future positions.

**Computation plane** — Numerical model execution: weights, graphs/operators, caches, kernels, and logits.

**Context** — Mutable inference state tied to a model, including position, KV cache, workspace, and decoder state.

**Context Fabric** — TheOrc’s evidence/context subsystem. It remains above OrcEngine; future token/cache reuse is research.

**Control plane** — TheOrc-owned orchestration, roles, tools, policy, admission, sessions, telemetry, and product routing.

**cuBLAS** — NVIDIA BLAS implementation on CUDA, proposed for initial GPU dense operations.

## D–F

**Decode** — Inference for new autoregressive token positions using prior state; also sometimes token-ID-to-text conversion, so qualify as model decode or tokenizer decode.

**Dequantization** — Reconstruction of approximate floating values from a quantized block representation.

**Differential test** — Executes the same input through two implementations and compares results.

**Executor** — Current LLamaSharp object that performs inference. OrcEngine design uses engine/context/backend terminology until implementation chooses names.

**Float32 / F32** — IEEE-style 32-bit floating-point storage/computation baseline, subject to platform/order numerical variation.

**Forward pass** — Model computation mapping token input/state to activations/logits.

## G–I

**GGML** — Tensor library/ecosystem underlying llama.cpp; not an OrcEngine execution dependency under the accepted boundary.

**GGUF** — Extensible binary format containing model metadata and tensor descriptors/data for inference.

**Greedy decoding** — Deterministically selecting the maximum-logit token under documented tie behavior.

**Grouped-query attention (GQA)** — Attention configuration with more query heads than key/value heads; requires explicit head mapping.

**HIVE** — TheOrc’s multi-machine execution/control system. Distributed OrcEngine work is long-horizon research.

**Hypothesis** — Testable belief not yet verified.

## K–L

**Kernel** — Low-level operator implementation on CPU/GPU.

**KV cache** — Per-layer stored key/value projections from prior positions used by autoregressive attention.

**LLamaSharpRuntime** — TheOrc’s current in-process `ILocalModelRuntime`, implemented with LLamaSharp and llama.cpp native backends.

**Logits** — Raw model output scores over vocabulary before sampling.

**LoRA** — Low-rank adapter weights modifying model behavior. Out of initial OrcEngine scope.

## M–P

**mmap / memory mapping** — OS facility mapping file bytes into virtual memory; useful for GGUF but subject to strict lifetime/bounds rules.

**Model graph** — Ordered tensor operations implementing an architecture.

**Oracle** — Pinned reference implementation/artifact used to judge expected results; not assumed infallible.

**OrcEngine** — Proposed from-scratch experimental inference engine; documentation-only at this snapshot.

**OrcEngineRuntime** — Future hypothetical TheOrc adapter; it does not exist today.

**Prompt evaluation** — Forward evaluation of an input prefix, often different in shape/performance from single-token decode.

**Prompt template** — Formatting of roles/messages/tools into model-facing text/tokens; separate from transformer math.

## Q–R

**Quantization** — Representing weights/cache with reduced precision or packed blocks to reduce memory/compute cost.

**Reference path** — Clear, usually slower implementation retained to validate optimized paths.

**RMSNorm** — Root-mean-square normalization used by Llama-style architectures.

**RoPE** — Rotary position embedding applied to query/key components according to model-specific conventions.

**Runtime** — In TheOrc, an `IModelRuntime` implementation. OrcEngine itself may call its core an engine to avoid ambiguity.

## S–Z

**Sampler** — Pipeline transforming logits and selecting a token; greedy selection is the baseline sampler.

**SwiGLU** — Gated feed-forward construction using SiLU activation and elementwise gating.

**Tensor** — Typed multidimensional view over storage with shape/stride/lifetime metadata.

**Tolerance profile** — Named per-operator numerical comparison limits with evidence and versioning.

**Tokenizer** — Model-specific mapping between byte/text sequences and token IDs, including normalization and special-token rules.

**Truth label** — VERIFIED, DECIDED, PROPOSED, HYPOTHESIS, UNKNOWN, or OUT OF SCOPE state applied to claims.

**VRAM** — GPU memory. Report measured allocations separately from estimates and system free-memory snapshots.

**Workspace** — Reusable temporary memory for intermediate execution tensors.
