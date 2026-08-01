# CUDA Backend Design

## Status and boundary

CUDA is a later phase. No CUDA code or verified hardware result exists for OrcEngine.

The [CUDA Programming Guide](https://docs.nvidia.com/cuda/cuda-programming-guide/) defines a heterogeneous model with host/device memory, thread hierarchies, and explicit synchronization. The [cuBLAS documentation](https://docs.nvidia.com/cuda/cublas/) defines GPU BLAS APIs and their numerical/reproducibility considerations.

## First target

- one NVIDIA GPU and compute capability;
- one supported CUDA toolkit/driver combination;
- float32 only;
- one model entirely resident on the GPU;
- one context and one CUDA stream;
- cuBLAS/cuBLASLt decision recorded from an experiment;
- custom kernels only for required non-GEMM operations;
- no CPU/GPU layer split or multi-GPU.

## Ownership model

### Device instance

Owns selected device, stream, library handles, capability data, error state, and allocation ledger.

### Model device storage

Owns long-lived copied weights. Upload occurs after host validation. Partial upload must unwind completely.

### Context device storage

Owns KV cache, logits, token state where appropriate, and reusable workspace. Context cannot outlive device-model storage.

## Transfer policy

Initial proposal:

- validate and map GGUF on host;
- allocate exact device weight extents;
- copy weights once during load;
- keep cache and intermediate execution on device;
- copy only necessary tokens/control inputs to device;
- copy logits only when host sampling/debug capture requires them;
- measure transfer bytes and time explicitly.

Unified memory remains a research question, not a default.

## Dense operations

Use cuBLAS first because writing a competitive GEMM is a separate project. Required decisions include logical/physical layout, leading dimensions, operation flags, handle/stream binding, algorithm selection, workspace, math mode, and reproducibility settings.

Every selected algorithm is recorded in benchmark metadata where the API exposes it.

## Candidate custom kernels

- embedding lookup;
- RMSNorm;
- RoPE;
- causal masking and softmax;
- residual add;
- SiLU and gating;
- cache write/read layout transforms;
- argmax or top-k later.

Fusion is postponed until profiler evidence and independent unfused reference kernels exist.

## Synchronization

Asynchronous APIs do not make work complete. Define explicit synchronization at:

- model upload completion before first use;
- debug capture or host logits read;
- timing boundaries using CUDA events;
- resource destruction;
- error propagation where an asynchronous launch failure may surface later.

Do not call global device synchronization casually in steady-state code; the first correct implementation may use conservative sync, then remove measured bottlenecks with tests.

## Error handling

Check every CUDA and cuBLAS status. Preserve operation name, requested bytes/shapes, device ID, and original status. A failed context is not reused until its state is proven consistent or reset.

Out-of-memory errors include current OrcEngine allocations and requested bytes; they do not fabricate total free memory if the driver query fails.

## Numerical comparison

CUDA results compare to:

1. OrcEngine scalar CPU;
2. OrcEngine optimized CPU;
3. external oracle.

Tolerance profiles may differ by operator and selected CUDA math mode. CPU/GPU bitwise equality is not required, but token agreement without tensor bounds is insufficient.

## Benchmark partition

Measure separately:

- host parsing;
- device allocation;
- weight upload;
- cold first execution;
- warm prompt evaluation;
- steady one-token decode;
- device-to-host transfers;
- custom kernels and GEMM;
- peak device bytes.

## Future work requiring separate approval

- quantized CUDA dot products;
- tensor cores/mixed precision;
- multiple streams;
- CUDA graphs;
- overlapped transfers;
- CPU/GPU splitting;
- peer-to-peer and multi-GPU;
- HIVE distribution.

## Exit criteria

Correctness, sanitization/tooling where available, deterministic lifecycle, recorded environment, exact backend identity, no unexplained CPU/GPU divergence, and measured benefit over CPU for the target model.
