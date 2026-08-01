# Benchmark Strategy

## Purpose

Benchmarks answer whether a correct implementation is useful and whether a change improved the intended workload. They do not prove correctness.

## Mandatory metadata

Every result records:

- OrcEngine commit and dirty-tree state;
- compiler, version, configuration, flags, and link dependencies;
- operating system and power plan;
- CPU model, cores, instruction path, thread count, affinity if used;
- RAM and NUMA topology if relevant;
- GPU model, compute capability, driver, toolkit, cuBLAS version, clock/power settings if available;
- model file SHA-256, architecture, parameter size, and tensor types;
- context length, prompt tokens, generated tokens, batch/sequences;
- cache dtype/layout;
- cold/warm procedure and repetition count;
- actual runtime/backend with fallback count;
- raw sample output and summary method.

## Scenario separation

### Load

File open/parse, mapping, validation, host allocation/conversion, device allocation/upload, first-use initialization.

### Prompt evaluation

Fixed token counts such as 1, 32, 128, 512, and context-relevant larger sizes, subject to model limits.

### Decode

Steady one-token steps after fixed prefixes. Report distribution, not only average.

### Memory

Mapped bytes, resident host bytes, model allocations, cache, workspace, peak, device bytes, and allocations per call.

### Lifecycle

Repeated create/run/destroy and long decode for leaks, fragmentation, or throughput drift.

## Metrics

- load latency;
- time to first token;
- prompt tokens/second;
- decode tokens/second;
- per-token latency median/p90/p99;
- peak host/device bytes;
- allocation count/bytes where measurable;
- energy/power only when measurement tooling is reliable;
- correctness status and oracle profile used.

## Baselines

Compare against:

1. prior OrcEngine commit on identical environment;
2. OrcEngine scalar versus optimized backend;
3. pinned LLamaSharp/llama.cpp where configuration equivalence is documented;
4. Ollama only when server overhead and configuration differences are clearly separated.

An external engine comparison is informative, not an automatic pass/fail target.

## Statistical procedure

- predeclare warmup count;
- capture every sample;
- use median and percentiles;
- report variability and outliers;
- avoid interleaving unrelated GPU workloads;
- record thermal/power anomalies;
- rerun both baseline and candidate in alternating order for sensitive comparisons;
- never select only the best run.

## Cold versus warm

Cold filesystem cache, cold process, warm mapped pages, warm GPU, and reused context answer different questions. Name the state precisely. A command that cannot control OS cache must say so.

## Prompt corpus

Use deterministic token-ID fixtures so tokenizer changes do not masquerade as compute changes. Add text-level scenarios separately to measure the entire prompt pipeline.

## Regression gates

No hard performance gate before a stable baseline. Later thresholds should account for observed noise and require correctness green. A faster result with changed tokens, fallback, reduced context, or different quantization is invalid.

## Reporting template

```text
Benchmark ID / date:
Question:
Commit and tree:
Hardware/software:
Model/config hashes:
Exact command:
Warmup and samples:
Correctness gate:
Raw artifact:
Summary:
Comparison:
Limitations:
Conclusion:
```

## Anti-patterns

- comparing prompt rate to decode rate;
- omitting quantization or context;
- comparing CPU-only to GPU without saying so;
- claiming speedup from one sample;
- hiding fallback;
- excluding load time without naming warm service assumptions;
- using generated text length instead of token count;
- publishing throughput from a correctness-failing build.
