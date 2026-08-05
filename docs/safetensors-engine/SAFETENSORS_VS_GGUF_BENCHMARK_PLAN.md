# TheOrc — Safetensors vs GGUF Benchmark Plan

> **Status: 🔲 Planned.** The head-to-head methodology for the spike's core experiment
> ([SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md) §5 consumes these numbers).
> The honesty bar is the CF-7 benchmark discipline: pre-registered thresholds, controls
> stated before results exist, runs invalidated by known confounds rather than reported
> anyway (see `docs/CONTEXT_FABRIC_BUG_HISTORY.md` and the retracted-then-rerun CF-7 GO in
> `README.md`).

---

## 1. The experiment: one source model, two execution paths

| Path | Pipeline |
|---|---|
| **A (baseline)** | HF safetensors → `convert_hf_to_gguf.py` (llama.cpp repo, pinned commit recorded in the report) → **F16 GGUF** → existing LLamaSharp/llama.cpp lane |
| **B (spike)** | The **same** HF safetensors weights → loaded and executed directly by the managed engine |
| **R (reference)** | The same weights on HF `transformers` (fp32 and fp16) — the correctness oracle, defined in [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md). R is not a competitor; both A and B are measured against it |

Path A runs through a thin bench verb that drives `LLamaSharpRuntime.LoadModelAsync` +
generation directly (the `Tools/ContextFabricBench` pattern), so A measures the shipped
in-process lane, not a raw llama.cpp CLI with different defaults.

### Precision controls — the most common way to get this wrong

- The **primary A/B parity and throughput comparison is F16 GGUF vs fp16 safetensors** —
  same weights, same precision. This is the only pairing from which an engine-quality
  conclusion may be drawn.
- Quantized GGUF runs (**Q4_K_M and Q8_0**) are collected as *separate reference points*
  labeled `context` in the result schema — they answer "what does the fit lever cost/buy on
  this hardware" for the report's narrative. They are **never** the parity control, and no
  sentence in the report may compare B's fidelity or speed against a quantized run as if it
  were the baseline. Conflating these invalidates the experiment: a Q4 comparison
  simultaneously flatters B on fidelity and flatters A on speed/VRAM, producing two wrong
  conclusions from one mistake.
- One asymmetry cannot be controlled away: A's weights passed through
  `convert_hf_to_gguf.py` (BF16 → F16 conversion + llama.cpp's Q/K row permutation), B's
  through the engine's own BF16 → F16 load conversion. Both are BF16→F16 round-to-nearest,
  so logit-level agreement is expected but must be *measured*, not assumed — that is
  precisely what measuring both A and B against R establishes. Weight *files* are not
  byte-comparable (the permutation); only outputs are.

### Controlled variables (identical across A and B, recorded in every result file)

| Variable | Control |
|---|---|
| Prompt set | Identical token-ID arrays (§4) — pre-tokenized once, consumed by A, B, and R |
| Context length | 4096 for all runs |
| Sampling | Greedy (`temperature = 0`) for all parity runs; A's sampler pinned to greedy via temperature 0 and the report records LLamaSharp's actual sampler config |
| Max new tokens | 256 for latency/throughput cases; 32 for parity cases |
| Hardware | One reference box, same physical GPU, no other GPU consumers (checked via `nvidia-smi` process list before each run; a non-empty list aborts the run — the concurrent-session-collision lesson) |
| Driver / CUDA | Recorded verbatim from `nvidia-smi` output into the result file |
| Thermal / power | GPU persistence mode noted; 60 s idle cool-down between path runs; GPU temperature and power-limit read before and after each repetition and stored — runs where start temperature differs by > 10 °C from the session median are flagged `thermal-outlier` and excluded from summary statistics (but still stored) |
| Process state | Fresh process per path per repetition set (no warm allocator inheritance between paths) |

---

## 2. Metrics

Every metric: definition, unit, how measured, repetitions, statistic reported.

### Throughput

| Metric | Definition | Unit | Measurement | Reps | Reported |
|---|---|---|---|---|---|
| Prefill rate | promptTokens / (time from first kernel dispatch after weights loaded to last prefill logit ready) | tok/s | monotonic `Stopwatch`, device-synchronized before stop ([NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) `Synchronize`) | 10 per prompt-length bucket | median + IQR |
| Decode rate | generatedTokens / (time from first to last decode-step completion) | tok/s | same | 10 | median + IQR |
| TTFT | time from generation request (weights already loaded) to first token available | ms | same | 10 | median + IQR |

Model **load time is its own metric**, never folded into TTFT.

### Memory

| Metric | Definition | Unit | Measurement | Reps | Reported |
|---|---|---|---|---|---|
| Peak VRAM | max process VRAM during load + one full generation | bytes | B: `ITensorAllocator.AllocatedBytes` **and** driver query, both reported; A: `LLamaSharpRuntime.LastMeasuredVramBytes` (llama.cpp's own log-parsed allocations — the shipped mechanism) plus the same driver query. The WDDM caveat applies: on Windows the per-process driver query may return null (documented in `NativeVramProbe`); when it does, the report says "null (WDDM)", never a substituted estimate | 3 | max of reps |
| Steady-state VRAM | VRAM after load + warmup, before generation | bytes | same sources | 3 | median |
| Host RAM | process working-set delta from pre-load baseline | bytes | `Process.WorkingSet64` | 3 | median |
| Load time | `Load` call start → session ready | s | Stopwatch | 5 | median + min/max |
| Predicted-vs-measured VRAM | prediction (format spec §4 / `OrcScheduler` formula) − measured | bytes + % | derived | — | single table |

### Fidelity (vs reference R — procedures in [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md))

| Metric | Definition | Unit |
|---|---|---|
| Max abs logit delta | max over positions × vocab of |logit − logit_R| | logit units |
| Mean abs logit delta | mean of same | logit units |
| KL divergence | KL(softmax(logits_R) ‖ softmax(logits)) per position, fp64, reported max + mean | nats |
| Top-1 agreement | fraction of positions where argmax matches R | % |
| Perplexity | exp(mean NLL of R's chosen corpus tokens under the path's logits) on the fixed perplexity corpus (§4) | — |

### Determinism

| Metric | Definition | Reported |
|---|---|---|
| Same-machine, same-seed | N = 10 runs, byte-compare full generated token-ID sequence + final logits buffer | bit-identical yes/no, first divergent run/position if no |
| Cross-process | same, each run in a fresh process | yes/no |
| Cross-machine | **measured on 2 fleet boxes if time allows; explicitly NOT a gate** ([SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md) G-3). Different GPU architectures may legitimately differ; the report states the honest scope of whatever claim the data supports | yes/no/not-run |

### Correctness

| Metric | Definition |
|---|---|
| Greedy string match | over the full prompt set, exact match of the detokenized greedy continuation (32 tokens) vs R-fp32's greedy continuation; report match count / total, and for mismatches the first divergent token index |

---

## 3. Statistical rigor

| Rule | Value |
|---|---|
| Warmup | 2 unrecorded generations per path per configuration before any timed rep — discards JIT/kernel-compile cost (B) and first-touch page-in cost (A and B). Kernel-compile time is separately reported once as its own number, not hidden |
| Repetitions | 10 for time metrics, 3 for memory (memory is near-deterministic), 10 for determinism |
| Statistic | median + IQR for times (robust to scheduler noise); never a bare mean without its spread |
| Confidence intervals | for top-1 agreement and greedy match rates, exact Clopper–Pearson 95% bounds — the same standard the Toolcaller Refusal Gauntlet already uses, so the project reports one kind of honest bound |
| Outliers | reported, flagged, excluded from summary only with the flag reason stored (thermal rule §1); no silent dropping |
| Pre-registration | thresholds (charter §5) and this plan are committed before the first measured run; any post-hoc threshold change must be its own visible commit with a rationale |

---

## 4. Prompt set and perplexity corpus

Fixture: `Tools/SafetensorsSpike/fixtures/promptset-v1/` — committed to the repo, hashed,
hash recorded in every result file.

| Bucket | Count | Prompt token lengths | Purpose |
|---|---|---|---|
| short | 8 | 16–64 | TTFT, decode rate |
| medium | 8 | 256–512 | balanced |
| long | 4 | 2048–3584 | prefill rate; exercises RoPE llama3-scaling range ([NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) §4) |
| parity | 12 | mixed, drawn from the above | logit capture positions ([LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §3) |

Content: plain English + code-flavored text authored for the fixture (no licensed book
text — the Independent Mind Corpus rules stay with CF). Stored as **token-ID arrays** with
the source text and tokenizer identity alongside (format spec OD-F1: tokenized once by HF
`transformers`, consumed identically by A, B, R).

Perplexity corpus: a fixed ~64k-token document set in the same fixture folder, same
provenance rules, evaluated at ctx 4096 with the standard sliding evaluation (stride 2048),
identical windows for every path.

---

## 5. Result schema (JSON)

One file per (path, configuration, repetition set):
`.orc/safetensors-spike/results/<runId>/<path>-<config>.json` — mirroring
ContextFabricBench's `.orc/...` output convention.

```json
{
  "schemaVersion": 1,
  "runId": "st-2026-xx-xx-a",
  "path": "A | B | R | context-q4km | context-q80",
  "engine": { "name": "LLamaSharp | SafetensorsSpike | transformers",
              "version": "...", "gitCommit": "...", "backend": "cuda12 | ilgpu-cuda | ..." },
  "model": { "source": "meta-llama/Llama-3.2-1B-Instruct",
             "artifact": "path + sha256", "precision": "F16 | BF16->F16 | F32",
             "converterCommit": "llama.cpp @ ... (path A only)" },
  "hardware": { "gpu": "...", "vramBytes": 0, "driver": "...", "cuda": "...",
                "powerLimitW": 0, "tempStartC": 0, "tempEndC": 0 },
  "config": { "contextLength": 4096, "sampling": "greedy", "maxNewTokens": 256,
              "promptSetSha256": "..." },
  "warmup": { "runs": 2, "kernelCompileMs": 0 },
  "throughput": { "prefillTokS": { "median": 0, "iqr": 0, "raw": [] },
                  "decodeTokS": { "median": 0, "iqr": 0, "raw": [] },
                  "ttftMs": { "median": 0, "iqr": 0, "raw": [] } },
  "memory": { "peakVramBytes": { "allocator": 0, "driver": null, "llamaLog": 0 },
              "steadyVramBytes": {}, "hostRamBytes": 0, "loadTimeS": 0,
              "predictedVramBytes": 0 },
  "fidelity": { "maxAbsLogitDelta": 0, "meanAbsLogitDelta": 0,
                "klMax": 0, "klMean": 0,
                "top1AgreementPct": 0, "top1Ci95": [0, 0], "perplexity": 0 },
  "determinism": { "sameMachineRuns": 10, "bitIdentical": true,
                   "crossProcess": true, "crossMachine": "not-run" },
  "correctness": { "greedyMatches": 0, "greedyTotal": 0, "firstDivergences": [] },
  "flags": ["thermal-outlier-rep-7"],
  "provenance": { "promptSetSha256": "...", "weightsSha256": "...", "timestampUtc": "..." }
}
```

Null means "not measurable here" (the `RuntimeStats` rule: null over invented numbers) —
e.g. `driver` VRAM on WDDM, `converterCommit` for path B.

### Report template

The human-readable report (`REPORT.md` in the same folder, committed at spike end) has
fixed sections in fixed order: Summary verdict against charter G-1…G-5 / K-1…K-4 → A vs B
headline table (F16-vs-fp16 only) → context table (quantized points, clearly labeled) →
fidelity vs R → determinism → memory predicted-vs-measured → threats to validity (§6
instantiated with what actually happened) → raw-file index.

---

## 6. Threats to validity (pre-registered)

| # | Threat | Mitigation / disclosure rule |
|---|---|---|
| T-1 | Quantized-vs-fp16 conflation | Structural: `path` enum separates `context-*` from `A`; report template keeps them in different tables |
| T-2 | Tokenizer mismatch between paths | Eliminated by pre-tokenized fixture (OD-F1); residual risk: A's lane re-detokenizes for output — string-match metric therefore compares token IDs first, strings second |
| T-3 | Path A misconfiguration (not greedy, wrong ctx, silent GPU-layer fallback) | A's result file records `LLamaSharpRuntime` health/stats + the resolved `GpuLayerCount`; a partial-offload admission (degraded `EffectiveGpuLayers`) invalidates the run for comparison — full-offload only |
| T-4 | JIT warmup contaminating B's times | Warmup rule §3; kernel-compile time reported separately |
| T-5 | Thermal drift across the run order | Cool-downs + temperature logging + outlier rule; path order alternates A/B rather than all-A-then-all-B |
| T-6 | Reference itself wrong (R misloaded, wrong dtype) | R-fp32 vs R-fp16 cross-check is part of harness bring-up ([LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §5) — if those two disagree beyond expected fp16 noise, the oracle is broken and no path measurement proceeds |
| T-7 | Single reference box → hardware-specific conclusion | Disclosed limitation in the report; cross-machine determinism data (if collected) partially probes it; no generalization claim beyond the measured box |
| T-8 | Same-author bias (engine author also runs the benchmark) | All thresholds pre-registered; raw per-rep arrays stored in the result files so any reviewer can recompute every statistic |
| T-9 | `NoKvSlot`-class silent degradation on A | Grep A's run logs for `NoKvSlot` before trusting scores — the standing CF-7 rule, applied verbatim |
