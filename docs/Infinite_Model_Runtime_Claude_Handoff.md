# Infinite Model Runtime
## A Practical R&D Plan for Running Models Larger Than Available VRAM

**Project goal:** Make VRAM capacity a performance constraint rather than a hard model-size limit.

**Target hardware:** NVIDIA GeForce RTX 5070 Ti, 16 GB VRAM  
**Primary use cases:** Oversized-model inference, model evaluation, ablation sweeps, LoRA/QLoRA experimentation, and eventually Training Pit integration.

---

## 1. Core Principle

The runtime should never treat:

> `MODEL DOES NOT FIT IN VRAM`

as a terminal failure.

Instead, it should automatically select a slower execution strategy using some combination of:

- GPU VRAM
- Pinned system RAM
- Normal system RAM
- NVMe storage
- Quantized weight representations
- Progressive weight decoding
- Layer/tensor paging
- Speculative decoding
- KV-cache compression
- MoE expert caching
- Adaptive mixed precision
- GPU-side decompression/transcoding

The target philosophy is:

> **Run anything eventually. Then optimize the hell out of "eventually."**

This is not about pretending a 16 GB GPU behaves like an 80 GB GPU. It is about designing an execution runtime in which the full model never needs to reside in VRAM at once.

---

# 2. The Model Should Not "Live" in VRAM

Treat VRAM like the fastest level of a memory hierarchy rather than the permanent home of the model.

Conceptual tiers:

| Tier | Role | Possible Representation |
|---|---|---|
| GPU VRAM | Active computation / hottest pages | NVFP4, FP8, INT4, native kernel format |
| Pinned RAM | Immediate staging / prefetch queue | Q2/Q3/Q4 compressed |
| System RAM | Warm model cache | Quantized shards |
| NVMe | Cold model backing store | Highly compressed pages |
| Secondary NVMe/network | Optional overflow | Same indexed page format |

The runtime should have an explicit **residency manager** that knows where every weight page currently exists.

---

# 3. Existing Ideas We Should Borrow From

We should aggressively study and borrow concepts from systems such as:

- **AirLLM**
  - Layer-wise model streaming
  - Models much larger than VRAM
  - Sparse-MoE expert streaming

- **FlexGen**
  - GPU + CPU + disk scheduling
  - Treating heterogeneous memory as one managed execution hierarchy
  - Throughput-oriented scheduling to amortize weight movement

- **PowerInfer / PowerInfer-2**
  - Hot/cold neuron placement
  - Fine-grained scheduling
  - Neuron-cluster residency rather than only whole-layer residency

- **Hugging Face Accelerate**
  - CPU/disk offload
  - Memory-mapped weight access

- **DeepSpeed**
  - NVMe offload
  - Partitioned model state
  - Optimizer offload

- **QLoRA**
  - NF4
  - Double quantization
  - Paged optimizers

- **AQLM / QuIP# and other extreme low-bit quantizers**
  - ~2–3 bit weight representations
  - Vector/codebook/additive quantization ideas

- **KIVI and similar KV-cache quantization work**
  - Independent compression of KV cache

- **Speculative decoding / speculative offload work**
  - Use a small resident model to propose tokens
  - Use the large model to verify multiple proposed tokens per expensive traversal

- **KTransformers**
  - CPU/GPU heterogeneous execution
  - Large MoE expert scheduling

We are not trying merely to reimplement any one of these.

The interesting work is in **combining their useful ideas into one adaptive runtime**.

---

# 4. Proposed System: `InfiniteModelRuntime`

Suggested top-level abstraction:

```text
InfiniteModelRuntime
├── ArtifactInspector
├── ExecutionPlanner
├── ModelPageIndex
├── ResidencyManager
├── PrefetchScheduler
├── QuantizationPlanner
├── GPUTranscoder
├── KVCacheManager
├── SpeculativeDecoder
├── MoEExpertCache
├── TelemetryProfiler
└── BackendAdapters
```

---

# 5. Artifact Inspection Must Come First

The current LoRA adapter issue exposed a fundamental requirement:

**Never assume every GGUF/Safetensors artifact is a complete standalone model.**

Before model loading, inspect and classify the artifact.

Possible types:

- Full base model
- LoRA adapter
- QLoRA adapter
- Delta adapter
- Multimodal projector
- Tokenizer artifact
- Embedding model
- Reranker
- Dense transformer
- MoE transformer
- Unsupported/unknown artifact

For every artifact, produce metadata such as:

```text
artifact_type
architecture
parameter_count
layer_count
tensor_count
dtype
quantization
estimated_weight_bytes
estimated_vram_native
estimated_vram_q8
estimated_vram_q4
estimated_vram_q2
base_model_requirement
adapter_rank
moe_expert_count
experts_per_token
metadata_completeness
supported_backend_candidates
```

If it is an adapter, do not send it through the standalone-model loading path.

---

# 6. Build a Real Model Page Table

Do not make the entire model or even a whole transformer block the smallest scheduling unit.

Index model weights into logical pages.

Possible identifier:

```text
model_id
layer_id
tensor_name
tile_id
codec
precision
byte_offset
compressed_size
decoded_size
sensitivity_score
frequency_score
residency
last_used
predicted_next_use
```

Example:

```text
Qwen-Large
  block_27
    mlp.up_proj
      tile_000
      tile_001
      tile_002
      ...
```

This enables much finer control than simple layer offload.

---

# 7. Tensor-Level or Tile-Level Paging

Initial implementation can page whole layers because it is simple.

But the long-term system should page:

- Individual tensors
- Tensor tiles
- Attention matrices separately from MLP matrices
- Hot neuron groups
- MoE experts

Potential benefit:

```text
Block 27:
  attention.q_proj     VRAM
  attention.k_proj     VRAM
  attention.v_proj     RAM
  mlp.gate_proj        NVMe
  mlp.up_proj hot      VRAM
  mlp.up_proj cold     RAM/NVMe
```

The page scheduler should eventually learn which regions are frequently used.

---

# 8. Compressed Transport Instead of Early Decompression

Avoid this:

```text
SSD Q2
 -> CPU Q2
 -> expand to FP16 in RAM
 -> transfer FP16 over PCIe
 -> FP16 VRAM
 -> compute
```

That wastes PCIe bandwidth and staging memory.

Prefer:

```text
SSD Q2
 -> pinned RAM Q2
 -> PCIe Q2
 -> GPU
 -> GPU transcode/dequantize
 -> compute
```

Better still:

```text
SSD compressed representation
 -> pinned RAM compressed
 -> PCIe compressed
 -> fused GPU decode/GEMM
```

The **transport representation** and **compute representation** do not need to be identical.

---

# 9. Separate Storage Precision From Compute Precision

This is one of the most promising directions.

Example:

```text
NVMe format:         2.2 bits/weight additive quantization
PCIe transport:      same compressed representation
GPU compute format:  NVFP4
```

The runtime would transcode a page after it reaches the GPU.

This means:

- storage can optimize for minimum bytes
- PCIe can optimize for minimum traffic
- GPU kernels can optimize for Blackwell Tensor Cores

Do not choose one quantization format and force it to satisfy all three jobs.

---

# 10. Progressive / Layered Weight Compression

Explore a progressively decodable weight format.

Instead of:

```text
W = Q4
```

use something conceptually like:

```text
W ≈ BaseLowBit
    + RefinementA
    + RefinementB
    + SparseOutlierResidual
```

Possible quality levels:

```text
FAST:
  Base only

BALANCED:
  Base + RefinementA

HIGH:
  Base + RefinementA + RefinementB

REFERENCE:
  Base + all refinements + sparse residual
```

This allows the runtime to choose precision dynamically based on:

- layer sensitivity
- available bandwidth
- current VRAM pressure
- requested quality
- evaluation mode vs interactive generation

---

# 11. Use Ablation Results to Allocate Bits

This is extremely important.

The current ablation sweep should not only generate reports.

It can become a **quantization calibration engine**.

If a layer/tensor is insensitive:

```text
2 bits
```

If moderately important:

```text
3–4 bits
```

If highly sensitive:

```text
NVFP4 / FP8 / BF16
```

Potential policy:

```text
embedding           6–8 bit
early attention     4 bit
insensitive MLP     2 bit
sensitive MLP       4 bit
critical attention  FP4/FP8
lm_head             6–8 bit
```

Eventually calculate precision per **tensor** rather than only per layer.

Possible objective:

```text
minimize:
    total_model_bytes
    + transfer_cost
    + compute_cost

subject to:
    quality_loss <= threshold
```

The ablation/sensitivity data can drive this optimization.

---

# 12. Triple-Buffered Streaming

Execution should overlap:

1. GPU compute
2. Host-to-device transfer
3. NVMe read

Conceptual pipeline:

```text
GPU:
  compute page N

PCIe:
  transfer page N+1

Pinned RAM / NVMe:
  load page N+2
```

Then rotate buffers.

Suggested state:

```text
buffer_A = COMPUTE
buffer_B = H2D
buffer_C = DISK_READ
```

Use asynchronous CUDA streams where possible.

Measure:

- transfer time
- kernel time
- disk read latency
- queue depth
- GPU idle time

The target is to keep the GPU from waiting for the next page.

---

# 13. Build Our Own Model Swap File

Do not rely on the Windows page file as the main mechanism.

The OS does not understand transformer execution order.

We do.

Create a large indexed model backing file such as:

```text
model.imr
model.imr.index
```

Possible layout:

```text
[header]
[metadata]
[page directory]
[page 000 compressed]
[page 001 compressed]
[page 002 compressed]
...
```

Each page should ideally be contiguous and aligned for large sequential I/O.

The scheduler can then predict:

> Page X will be required after pages A, B, and C.

That is much more controllable than general OS virtual memory.

We should still benchmark Windows page-file behavior, memory mapping, and OS caching, but **explicit application-managed paging should be the primary design.**

---

# 14. Memory-Mapped Files

Experiment with:

- `mmap`
- large-file mappings
- sequential access hints
- async reads
- direct/unbuffered I/O where useful
- pinned staging buffers

Potential flow:

```text
mapped model file
 -> logical page lookup
 -> staging buffer
 -> compressed H2D transfer
 -> GPU transcode
```

Benchmark OS cache behavior vs explicit asynchronous reads.

---

# 15. Speculative Decoding + Paging

Dense autoregressive decoding has a terrible property:

For each generated token, the large model traverses every layer.

If weights are streamed, that can imply repeatedly streaming huge amounts of data.

Speculative decoding can amortize those traversals.

Design:

```text
Small resident draft model:
  proposes K tokens

Huge paged target model:
  verifies K-token sequence together

Accept valid prefix
Repeat
```

Example VRAM budget:

```text
16 GB usable

3 GB  draft model
9 GB  large-model page cache
2 GB  KV cache
2 GB  workspace / staging
```

The exact values should be dynamically planned.

A successful speculative pass may let one expensive traversal validate several tokens instead of only one.

This could be one of the largest performance wins for disk/RAM-paged dense models.

---

# 16. Adaptive Speculation Depth

The number of speculative tokens should not be fixed.

Adjust based on:

- observed draft acceptance rate
- model streaming cost
- disk throughput
- page-cache hit rate
- context size
- GPU utilization

Potential rule:

```text
if page traversal is very expensive:
    increase speculation depth

if acceptance rate falls:
    reduce speculation depth
```

---

# 17. MoE Models Need a Different Strategy

MoE models are particularly interesting because only some experts are active for each token.

Do not page the entire MoE layer.

Page/cache experts.

Possible expert states:

```text
HOT      -> VRAM
WARM     -> pinned RAM
COLD     -> NVMe
```

The router itself can provide prefetch clues.

Even more experimental:

Maintain transition statistics such as:

```text
expert_17 at layer N
often followed by
expert_42 / expert_81 at layer N+1
```

Use those probabilities to prefetch likely experts before they are selected.

This is conceptually similar to branch prediction/cache prefetching.

---

# 18. Learned VRAM Cache

Eventually make page placement adaptive.

Track:

```text
page_frequency
reuse_distance
prompt_type
layer
model
expert
latency
transfer_cost
miss_penalty
```

Then calculate a residency score.

Possible rough form:

```text
score =
    access_probability
    * transfer_cost
    * reuse_frequency
    * sensitivity_weight
```

High-score pages stay resident.

This can eventually be workload-aware.

Example:

```text
coding workload cache profile
general chat cache profile
vision workload cache profile
reasoning workload cache profile
```

---

# 19. KV Cache Is a Separate Paging Problem

Do not mix weight management and KV management into one simplistic memory pool.

Create a dedicated `KVCacheManager`.

Potential techniques:

- INT8 KV
- INT4 KV
- 2-bit KV experimentation
- NVFP4 KV where supported
- CPU offload
- page old context to RAM
- sliding window
- importance-aware token retention
- selective recomputation

Treat the memory budget as:

```text
VRAM =
    weight_pages
  + KV_cache
  + activations
  + workspace
  + optional_draft_model
```

The planner decides these dynamically.

---

# 20. Activation Paging / Recomputation

For training or very large evaluation batches, also explore:

- activation checkpointing
- CPU activation offload
- compressed activations
- recomputation instead of retention

Sometimes recomputation is cheaper than PCIe/NVMe transfer.

The planner should eventually choose between:

```text
KEEP
OFFLOAD
COMPRESS
RECOMPUTE
```

for intermediate states.

---

# 21. Redesign the Ablation Sweep Around Weight Reuse

The existing ablation fleet runner is an ideal test bed.

Do not necessarily execute:

```text
ablate layer 0 -> run full model
ablate layer 1 -> run full model
ablate layer 2 -> run full model
...
```

Instead explore **layer-major evaluation**.

Concept:

```text
Load layer 0 once
Process all prompts / variants through layer 0
Evict layer 0

Load layer 1 once
Process all prompts / variants through layer 1
Evict layer 1

...
```

Maintain hidden states for multiple experiments in RAM.

Because hidden states are much smaller than full weight sets, this can amortize model weight I/O dramatically.

---

# 22. Hidden-State Forking for Ablations

For layer-specific ablations:

1. Run baseline hidden state up to the target layer.
2. Fork the state.
3. Apply baseline transformation to one branch.
4. Apply ablated transformation to another.
5. Continue both branches.

Potentially share earlier results instead of recomputing them independently.

Example:

```text
shared hidden state after block 14
      |
      +-- baseline block 15
      |
      +-- ablated block 15
```

This can make the ablation system substantially more efficient.

---

# 23. LoRA / Adapter Handling

Adapters should never be mistaken for full models.

Expected flow:

```text
Inspect adapter
Determine architecture/base-model hint
Locate or request matching base
Load base through InfiniteModelRuntime
Keep LoRA matrices resident where possible
Apply delta during matmul
```

Do not permanently merge LoRA weights unless explicitly desired.

Potential execution:

```text
Y = BaseMatmul(X) + LoRA_B(LoRA_A(X))
```

This fits the paging design extremely well because LoRA matrices are comparatively small.

---

# 24. Oversized QLoRA / Training Mode

Long-term Training Pit target:

```text
frozen base model:
    NVMe/RAM paged
    2–4 bit storage

active base page:
    GPU transcode to compute format

LoRA:
    resident in VRAM

optimizer:
    CPU RAM or NVMe paged

activations:
    checkpointed/offloaded

backward:
    blockwise
```

The goal is not necessarily fast training.

The goal is:

> Model too large for VRAM should not automatically prevent a LoRA experiment.

---

# 25. Execution Planner

Before running, the runtime should benchmark and inspect the current machine.

Gather:

```text
GPU model
VRAM total
VRAM currently free
system RAM total
system RAM free
page file
NVMe capacity
NVMe sequential read
NVMe random read
PCIe H2D throughput
PCIe D2H throughput
supported CUDA features
supported tensor formats
CPU cores
CPU memory bandwidth
```

Then calculate candidate plans.

Example:

```text
PLAN A
Full GPU Q4
Impossible

PLAN B
GPU + RAM Q4
Possible
Estimated 3.2 tok/s

PLAN C
GPU + RAM + NVMe Q2 transport -> FP4 compute
Possible
Estimated 1.4 tok/s

PLAN D
Speculative paged mode
Possible
Estimated 2.1 tok/s
```

Automatically select the best viable strategy unless overridden.

---

# 26. OOM Recovery

An allocation failure should trigger replanning.

Example:

```text
GPU allocation failed
↓
reduce weight cache
↓
reduce KV reservation
↓
lower compute precision
↓
move cold tensors to RAM
↓
move more pages to NVMe
↓
retry
```

Never immediately terminate unless no valid execution strategy remains.

Even then, report exactly what resource was impossible.

---

# 27. Desired User Experience

Eventually a huge model should produce output similar to:

```text
MODEL
  Parameters:          141.2B
  Native weight size:  282.4 GB

SYSTEM
  GPU:                 RTX 5070 Ti
  VRAM available:      15.7 GB
  RAM available:       52.3 GB
  NVMe cache:          1.4 TB

FULL GPU
  Impossible

PLANNED MODE
  InfiniteModelRuntime / paged

STORAGE FORMAT
  Adaptive Q2/Q3/Q4

GPU COMPUTE
  NVFP4

WEIGHT CACHE
  10.8 GB

KV CACHE
  2.0 GB

STAGING
  1.8 GB

DRAFT MODEL
  1.1 GB

PREFETCH
  Triple buffered

SPECULATION
  Enabled, K=6

ESTIMATED SPEED
  0.82 tok/s

MODEL WILL RUN.
```

That last line matters.

---

# 28. Telemetry Is Mandatory

Log everything needed to optimize the runtime.

Suggested metrics:

```text
tokens_per_second
time_to_first_token

gpu_utilization
gpu_memory_used
gpu_memory_peak

cpu_utilization
ram_used

disk_read_MBps
disk_queue_depth

H2D_MBps
D2H_MBps

page_hits
page_misses
page_evictions

prefetch_hits
prefetch_misses

gpu_idle_due_to_IO_ms

decompression_time_ms
transcode_time_ms

kernel_time_ms

speculative_accept_rate
speculative_tokens_proposed
speculative_tokens_accepted

KV_bytes
weight_cache_bytes
staging_bytes
```

Every experimental run should save structured JSON/CSV telemetry.

---

# 29. Benchmark Matrix

We need repeatable comparisons.

Test models across several ranges:

```text
< 16 GB
16–32 GB
32–64 GB
64–128 GB
128+ GB
MoE 100B+
```

For each:

```text
baseline backend
CPU offload
layer streaming
tensor paging
compressed transport
triple buffering
speculative paging
adaptive precision
```

Measure both:

- correctness / quality
- performance

---

# 30. Correctness Must Be Preserved

For every optimization, compare against a reference execution where possible.

Record:

```text
logit error
KL divergence
top-k agreement
perplexity delta
task accuracy
generation similarity
ablation ranking stability
```

We are willing to trade speed for execution, but should know exactly how much accuracy each compression mode costs.

---

# 31. Suggested Development Phases

## Phase 0 — Fix Current Fleet Robustness

- Correctly distinguish full models from LoRA adapters.
- Ensure malformed/unsupported artifacts are skipped without stopping the fleet.
- Improve stdout/stderr logging order.
- Always emit a final per-model status.
- Record failure type and traceback separately.
- Keep existing results intact.

---

## Phase 1 — Oversized Model Proof

Goal:

> Successfully execute at least one model that cannot fit in 16 GB VRAM.

Start with existing offload/streaming technology.

Implement a backend abstraction if one does not already exist.

Possible backends:

```text
Native
Transformers
Accelerate
llama.cpp
AirLLM
Infinite/Paged experimental
```

Do not prematurely optimize.

First prove the invariant:

```text
model > VRAM
still runs
```

---

## Phase 2 — Layer Streaming Runtime

Implement:

```text
PagedModelExecutor
ResidencyManager
LayerLoader
PinnedBufferPool
```

Start at whole-layer granularity.

Measure:

```text
load
transfer
compute
eviction
GPU idle
```

---

## Phase 3 — Triple Buffering

Overlap:

```text
compute N
transfer N+1
disk read N+2
```

Verify actual overlap using timing events.

---

## Phase 4 — Compressed Transport

Transfer Q4/Q3/Q2 pages without expanding to FP16 in system RAM.

Initially GPU-dequantize into a temporary buffer.

Later investigate fused kernels.

---

## Phase 5 — Tensor/Tiled Paging

Break layers into smaller independently pageable components.

Determine whether finer granularity improves effective cache hit rate enough to justify overhead.

---

## Phase 6 — Sensitivity-Aware Precision

Connect the ablation sweep output to the quantization planner.

Create per-layer first.

Then per-tensor.

Eventually per-tile if worthwhile.

---

## Phase 7 — Progressive Weight Codec

Prototype:

```text
base
refinement
outlier residual
```

Benchmark quality versus:

```text
Q2
Q3
Q4
```

---

## Phase 8 — Layer-Major Fleet Sweeps

Rework evaluation scheduling so model pages are reused across prompts and ablation variants before eviction.

This may be one of the highest-return improvements for the current testing workflow.

---

## Phase 9 — Speculative Paged Inference

Keep a small draft model resident.

Use the enormous model as a paged verifier.

Experiment with:

```text
K=2
K=4
K=8
K=16
```

Track acceptance rate and actual bytes transferred per accepted token.

---

## Phase 10 — MoE Expert Paging

Implement expert-level residency and prefetching.

Then add expert-transition prediction.

---

## Phase 11 — Training Pit Oversize Mode

Use the paging engine for frozen base weights during LoRA/QLoRA training.

Only after inference/evaluation paging is stable.

---

# 32. Experimental Ideas That Are Allowed to Fail

We specifically want research, not only safe engineering.

Explore:

### A. Ultra-low-bit transport codec

A transport-only representation potentially below 2 bits/weight, decoded to FP4 on GPU.

---

### B. Sparse residual enhancement

Store:

```text
Q2 base
+
small sparse FP8 correction tensor
```

for important outliers.

---

### C. Predictive page prefetching

Train or design a lightweight predictor to estimate which tensor/expert pages will be needed next.

---

### D. Hot-weight permanence

Profile weights/experts and permanently reserve VRAM for the most repeatedly useful regions.

---

### E. Dynamic precision during inference

Allow precision to vary depending on token position or confidence.

Example:

```text
ordinary token:
    Q2

uncertain token:
    load refinement pages

high-confidence token:
    cheap path
```

---

### F. Draft-model-guided precision

Use the small draft model's confidence/disagreement to decide whether the huge model should load higher-quality refinement pages.

---

### G. Quality-on-demand paging

If logits are ambiguous:

```text
load additional residual pages
recompute only sensitive block(s)
```

instead of always paying for maximum precision.

---

### H. Multi-NVMe striping

If several SSDs are available, stripe model pages and asynchronously fetch from multiple drives.

---

### I. Network model memory

Eventually allow another PC to act as a cold/warm weight cache.

This is not distributed compute necessarily.

It may simply be:

```text
remote RAM/NVMe
 -> network
 -> local GPU
```

---

### J. RAM compression cache

Maintain very cold pages compressed more aggressively in RAM and hot pages in less expensive codecs.

---

### K. Recompute versus transfer optimizer

For some states, recomputing may cost less than reading them from disk.

Have the planner benchmark both.

---

# 33. Things We Should NOT Assume

Do not assume:

- Every model must fit in VRAM.
- Every model must fit in system RAM.
- Every tensor must use one quantization format.
- Storage format must equal compute format.
- Whole layers are the correct paging granularity.
- Every generated token requires a completely independent weight traversal.
- KV cache must remain entirely on GPU.
- Windows page file is a sufficient paging architecture.
- Q4 is automatically the best precision.
- All layers are equally sensitive.
- All experts deserve equal residency.
- A slow model is a failed model.

---

# 34. Immediate Claude Assignment

Continue from the existing project and **do not jump directly into a giant rewrite**.

First inspect the current implementation and produce an implementation plan grounded in the actual code.

Claude should:

1. Inspect the current `ablation_sweep_fleet.py`.
2. Find the current model-loading paths.
3. Identify where standalone GGUF assumptions are made.
4. Fix/plan robust artifact classification so LoRA adapters cannot crash or pollute standalone model evaluation.
5. Map the existing abstraction boundaries around model loading/evaluation.
6. Identify the least invasive insertion point for a future `PagedModelExecutor`.
7. Determine whether the current ablation runner is prompt-major, model-major, or layer-major and document the consequences.
8. Determine what intermediate hidden-state reuse is feasible with the current model abstraction.
9. Inventory available RAM, VRAM, storage, CUDA/PyTorch stack, and current supported quantization backends.
10. Investigate what currently installed libraries can already provide:
   - CPU offload
   - disk offload
   - memory mapping
   - layer loading
   - quantized loading
11. Design a Phase-1 experiment that runs a model whose weights exceed the RTX 5070 Ti's 16 GB VRAM.
12. Preserve current working fleet behavior.
13. Add telemetry rather than guessing about bottlenecks.
14. Do not reject a model solely because its estimated VRAM exceeds physical VRAM.
15. Treat slow execution as acceptable for the first oversized-model proof.

---

# 35. Non-Negotiable Goal

The runtime's eventual decision tree should look like:

```text
Does it fit in VRAM?
  YES -> run normally.
  NO  -> can it run VRAM + RAM?
          YES -> run offloaded.
          NO  -> can it run VRAM + RAM + NVMe?
                  YES -> run paged.
                  NO  -> can precision be reduced?
                          YES -> recompute plan and run.
                          NO  -> can progressive pages / expert paging help?
                                  YES -> run.
                                  NO -> report the actual physical/software blocker.
```

**"Too big for VRAM" alone is never an acceptable final blocker.**

---

# 36. Definition of Success

Near term:

- The fleet runner survives adapters and malformed artifacts.
- A >16 GB model executes successfully on the RTX 5070 Ti.
- Memory and I/O telemetry is captured.
- Oversized models are no longer skipped automatically.

Medium term:

- Models larger than VRAM and RAM can execute using NVMe paging.
- Weight transfer overlaps compute.
- Compressed weights travel over PCIe.
- Ablation results influence precision.
- Fleet evaluation reuses staged weights efficiently.

Long term:

- Arbitrarily large supported models can execute given sufficient storage and time.
- Dense models use speculative paging.
- MoE models use predictive expert caching.
- LoRA training can operate on frozen bases far larger than VRAM.
- The system automatically chooses the best available memory/precision schedule.

---

# 37. Operating Philosophy

We are intentionally willing to reinvent parts of the wheel.

Existing frameworks are references and sources of proven ideas, not boundaries on the design.

When evaluating an idea, ask:

1. Does it make an otherwise impossible model executable?
2. Does it reduce bytes moved?
3. Does it increase reuse of a staged weight page?
4. Does it hide I/O behind computation?
5. Can model sensitivity tell us where precision actually matters?
6. Can we trade latency for capacity instead of failing?
7. Can the runtime learn a better schedule from previous runs?

The project should favor measurable experiments over assumptions.

---

# 38. Final Directive to Claude

**Maximum-effort research mode is encouraged.**

Do not stop at conventional `device_map="auto"` offload.

Use current techniques as baselines, but actively investigate new combinations of:

- virtualized model memory
- tensor paging
- GPU-side decompression
- progressive quantization
- ablation-driven bit allocation
- speculative decoding
- KV paging/compression
- learned cache residency
- MoE expert prediction
- custom model backing files
- asynchronous NVMe pipelines
- recompute-vs-transfer optimization

The central objective is simple:

> **Make model size stop being synonymous with VRAM size.**

A model may be extremely slow.

It may require RAM.

It may require hundreds of gigabytes of NVMe.

It may need aggressive quantization.

It may require one layer, tensor, or expert at a time.

But if the architecture is supported and the machine has enough storage to represent the model, the runtime should make a serious attempt to execute it rather than simply declaring it too large.

---

## Working Name Ideas

Possible names for this subsystem:

- **Infinite Model Runtime (IMR)**
- **VRAMless**
- **ModelPager**
- **NeuralVM**
- **TensorVM**
- **PagedInfer**
- **InfinityCache**
- **The Bottomless Pit**
- **Orc Infinite Runtime**
- **The Training Pit: Infinite Mode**

The name is secondary.

The architecture is the project.
