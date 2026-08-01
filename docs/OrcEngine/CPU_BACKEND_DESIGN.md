# CPU Backend Design

## Objective

The CPU backend is first a correctness vehicle, then a portable performance baseline. It must remain understandable enough to diagnose numerical differences.

## Staged implementation

### Stage C0: scalar reference

- float32 loops;
- one thread;
- contiguous tensors;
- explicit bounds and shape checks;
- no fusion;
- deterministic loop order;
- tiny synthetic fixtures only at first.

### Stage C1: real-model baseline

- reusable workspace;
- mapped weights;
- established BLAS allowed for dense multiplication;
- explicit BLAS thread count;
- scalar fallback for test-size tensors and unsupported layouts.

### Stage C2: profiled optimization

- tiled matmul or matrix-vector paths where measured;
- bounded thread pool;
- cache-aware work partition;
- reduced conversions/copies;
- AVX2 or later SIMD behind capability checks;
- optional fused operations only with differential coverage.

## Threading policy

The initial API admits one active context. Internal threading must avoid oversubscription between OrcEngine and BLAS.

Record:

- physical/logical CPU identity;
- selected thread count;
- affinity/NUMA policy if any;
- BLAS library and its thread settings;
- operator partition boundaries.

Default thread count remains a calibration input, because real hardware and coexistence with TheOrc workloads matter more than theoretical core count.

## Matrix execution

Prompt evaluation and token decode have different shapes. Prompt work may favor matrix-matrix operations; single-token decode often becomes matrix-vector and memory-bandwidth sensitive. Benchmark them separately before choosing a single kernel strategy.

## SIMD policy

- scalar code defines semantics;
- vector code handles tails explicitly;
- unaligned input is either supported or rejected by contract;
- runtime dispatch reports the selected path;
- every SIMD kernel compares against scalar random and adversarial vectors;
- no instruction set becomes a silent build-time assumption for portable artifacts.

## Stable softmax and normalization

These numerically sensitive operations should remain clear before fusion. Test large positive/negative values, nearly equal values, zero vectors, non-finite values, and causal ranges of length one.

## Memory behavior

Measure and report:

- mapped weight bytes and resident-set changes;
- workspace and KV bytes;
- allocations per prompt/decode call;
- page faults during cold and warm runs;
- peak working set;
- memory bandwidth where profiling tools allow.

Cold load, warm prompt, and steady decode are separate benchmark scenarios.

## Portability

Initial verified platform may be Windows x64 because that is the current workstation. Portable C++ and CMake are desired, but Linux/macOS support remains UNKNOWN until built and run there. Do not claim support from successful cross-compilation alone.

## Failure behavior

- unsupported CPU feature: choose verified scalar path or fail clearly;
- BLAS failure/absence: build configuration decides fallback, never silent numerical substitution;
- allocation failure: structured out-of-memory error with requested bytes;
- cancellation: checked between layers/major operations;
- non-finite diagnostic: return named tap and location in verification builds.

## Acceptance evidence

For every optimized operator:

1. scalar microcase passes;
2. randomized differential suite passes;
3. full layer taps pass;
4. end-to-end logits and tokens pass;
5. benchmark shows benefit on named hardware;
6. sanitizer and repeated-lifecycle checks pass.

Optimization that does not improve the relevant workload should be removed.
