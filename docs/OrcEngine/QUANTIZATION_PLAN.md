# Quantization Plan

## Principle

Quantization is a storage and arithmetic contract, not a file-size checkbox. OrcEngine will add formats one at a time after float32 inference is correct.

## Proposed order

1. F32 semantic baseline.
2. F16 storage with F32 conversion/accumulation if required.
3. Q8_0 block parse and dequantize-to-F32 reference.
4. Q8_0 direct dot-product kernel after profiling.
5. Q4_0 block parse and dequantize-to-F32 reference.
6. Q4_0 direct kernel after profiling.
7. One modern grouped format only if a target model justifies it.

## Per-format specification

Each supported format records:

- GGUF dtype ID and source specification revision;
- block element count and encoded byte count;
- scale/minimum/zero-point representation;
- bit packing and signedness;
- alignment requirements;
- supported tensor dimensions and tail policy;
- accumulator type;
- reference encode/decode vectors;
- CPU and CUDA availability;
- known numerical error profile.

## Reference dequantization

First implement an obviously correct block decoder producing float32. Compare hand-built edge blocks and trusted converter output. Only then use it to run the full float operator path.

This is slow by design and remains the oracle for direct quantized kernels.

## Direct kernels

Add only after profiling. A direct kernel must match reference dequantization within an approved format-specific tolerance and pass randomized block tests, odd legal shapes, extremes, all-zero blocks, and malformed-block rejection.

## Mixed tensor types

Real GGUF files may store different tensors in different types. The compatibility manifest lists allowed dtype per semantic tensor. Unsupported combinations fail at model validation before allocation/execution.

## Accuracy evidence

Report three levels:

1. block reconstruction error;
2. operator/layer/logit error against the float model;
3. end-to-end token and fixed-corpus quality differences.

Token agreement alone can hide degradation; perplexity/task quality alone cannot localize kernel errors.

## Performance evidence

Measure file/host/device bytes, load/dequantization cost, prompt rate, decode rate, memory bandwidth, workspace, and backend identity. Compare cold and warm paths.

## Quantization creation

An OrcEngine quantizer is not initially required. Use a pinned, license-reviewed external converter for fixtures and record its revision/command/hash. Building quantization tooling becomes separate scope only if runtime needs cannot be met otherwise.

## KV-cache quantization

Explicitly separate from weight quantization. It changes mutable inference state, memory estimates, and attention arithmetic. It is not inherited automatically when weight support lands.

## Stop conditions

Reject or pause a format if its authoritative layout is unclear, trusted fixtures are unavailable, numerical behavior cannot be bounded, licensing/provenance is uncertain, or the target workloads do not justify maintenance.
