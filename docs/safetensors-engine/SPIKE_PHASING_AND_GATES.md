# TheOrc — Spike Phasing And Gates

> **Status: 🔲 Planned.** Day-by-day breakdown of the two-week (10 working day)
> Safetensors Engine Spike. Tasks are numbered `ST-xxx`, written to be handed to a coding
> agent individually: each names its inputs (spec sections), outputs, and acceptance
> criteria. Phase exit gates name the specific evidence required — a gate without its
> evidence is not passed, it is skipped, and skipped gates are the road to an indefensible
> verdict. Days are effort buckets, not calendar promises; the **hard boundary is the
> two-week box itself** (charter K-4).

---

## 1. Phase map

| Phase | Days | Theme | Exit gate |
|---|---|---|---|
| P0 | 1 (half) | Environment + pins | G-P0 |
| P1 | 1–2 | Format layer | G-P1 |
| P2 | 3–5 | Reference oracle + CPU fp32 parity | G-P2 |
| P3 | 6–8 | GPU backend + fp16 pipeline | G-P3 |
| P4 | 9–10 | Benchmark, determinism, report, verdict | G-P4 |

Dependency rule: a task may start early if its dependencies are met, but **no gate may be
crossed out of order** — G-P3 with G-P2 open means the GPU numbers have no correctness
anchor.

---

## 2. Phase 0 — Environment (Day 1, morning)

| ID | Task | Acceptance criteria |
|---|---|---|
| ST-001 | Create `Tools/SafetensorsSpike/` + `Tools/SafetensorsSpike.Tests/` per [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §2; SPDX headers; CLI skeleton with `parse`/`generate`/`parity`/`bench` verbs returning usage exit code 64 when misused (ContextFabricBench convention) | Solution builds; `dotnet list Tools/SafetensorsSpike/SafetensorsSpike.csproj reference` shows **no** OrchestratorIDE project references from the engine; tests project runs empty-green |
| ST-002 | Download `meta-llama/Llama-3.2-1B-Instruct` (safetensors + config + tokenizer files) and its official F16 GGUF; record sha256 of every artifact in `fixtures/manifest-models.json` | Files on disk outside the repo tree, manifest committed with hashes + source URLs; `config.json` values cross-checked against the table in [SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) §5 and any deviation corrected **in the doc** |
| ST-003 | Pin versions: ILGPU + ComputeSharp packages (both, pre-bake-off), llama.cpp commit for `convert_hf_to_gguf.py`, Python `transformers`/`torch` in `reference/requirements.txt` | All pins committed; recorded in the provenance section of the (empty) report skeleton |

---

## 3. Phase 1 — Format layer (Days 1–2)

Inputs: [SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) (all sections).

| ID | Task | Depends | Acceptance criteria |
|---|---|---|---|
| ST-101 | `SafetensorsHeaderReader.TryRead` + dtype enum + error taxonomy | ST-001 | The 8 synthetic-fixture header tests from format spec §7 pass; never throws (fuzz loop over 1,000 random-mutation corruptions of a valid fixture returns error results, zero exceptions) |
| ST-102 | Validation rules V-1…V-8 | ST-101 | Each rule has a failing-fixture test naming it; `checked` arithmetic verified by the overflow test |
| ST-103 | `SafetensorsRepo.TryOpen`: index.json resolution, cross-shard validation, mmap tensor access | ST-101 | `TryOpen_RealLlama32Repo_TotalBytesMatchHeaderSum` passes against the ST-002 download; a real *sharded* repo parses (parse-only, any local ≥2-shard checkpoint) |
| ST-104 | `LlamaConfig` parse (§5) incl. `rope_scaling` block and int-or-list `eos_token_id` | ST-103 | Config test against real file; missing-required-key refusal test |
| ST-105 | BF16→F32/F16 conversion with overflow counting | ST-101 | Bit-pattern table test (subnormals, ±∞, NaN) passes |
| ST-106 | `PredictSize` (weights-as-stored, weights-as-computed, KV formula) | ST-104 | Prediction for the 1B at ctx 4096 matches hand-derivation in the test; formula term-for-term identical to `OrcScheduler.EstimateRequiredBytes`'s KV expression (cited in a comment, not copied blind) |

### Gate G-P1 — evidence required

| Evidence |
|---|
| All P1 tests green in CI-style run (`dotnet test` output captured into `.orc/safetensors-spike/gates/G-P1.txt`) |
| `parse` verb output for the real 1B repo: tensor count, total bytes, per-dtype census, size prediction — captured to the gate file |
| Fuzz loop result: N mutations, 0 uncaught exceptions |

---

## 4. Phase 2 — Reference oracle + CPU fp32 parity (Days 3–5)

Inputs: [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md),
[LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §2–§5,
[SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) §4.

| ID | Task | Depends | Acceptance criteria |
|---|---|---|---|
| ST-201 | Author `fixtures/promptset-v1` (buckets per benchmark plan §4) + offline tokenization via the pinned HF tokenizer; store token-ID arrays + hashes | ST-003 | Fixture committed; token counts per bucket within spec ranges; round-trip detokenization table stored |
| ST-202 | `reference/capture_logits.py`: R32 + R16 logits for all parity cases; per-layer hidden states for the 4 localization cases; write safetensors artifacts + manifest | ST-201 | Artifacts hashed in manifest; R16-vs-R32 calibration numbers computed and recorded (oracle sanity row, harness §5) |
| ST-203 | CPU fp32 engine: tensor/allocator/kernels (`IKernelSet` all 7 ops), per-op unit fixtures from forward-pass spec §9 | ST-104, ST-105 | All op fixtures pass, including `Attention_SingleTokenDecode_MatchesFullPrefillLastRow` bit-exact |
| ST-204 | `LlamaModel` + `DecodeSession` on CPU: load real 1B weights, prefill + greedy decode, F32 logits out | ST-203, ST-106 | Session generates 32 greedy tokens from a short prompt without error; weight inventory validation (forward-pass §1) enforced |
| ST-205 | Parity harness runner (`parity` verb): loads reference artifacts, computes §4 comparison, writes verdict JSON | ST-202, ST-204 | Runs end-to-end; output schema matches harness spec |
| ST-206 | Fix loop: drive B32-vs-R32 to threshold using §7 localization as needed | ST-205 | B32 row passes: max-abs ≤ 1e-3, top-1 = 100%, greedy 32/32 all cases |

### Gate G-P2 — evidence required

| Evidence |
|---|
| Parity verdict JSON: B32 vs R32 **PASS** with the raw numbers |
| Oracle calibration recorded: R16-vs-R32 divergence table (defines the fp16 floor for G-P3) |
| Localization log (even if empty): every divergence chased during ST-206, findings-log style |

**G-P2 is the spike's keel.** Charter K-4: if the box expires before this gate, the verdict
is NO-GO by rule, regardless of how promising the GPU work looked.

---

## 5. Phase 3 — GPU backend + fp16 pipeline (Days 6–8)

Inputs: [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §6–§7.

| ID | Task | Depends | Acceptance criteria |
|---|---|---|---|
| ST-301 | **Backend bake-off (half day, time-boxed hard):** tiled fp16 matmul at the spike's real shapes (e.g. `[1,2048]×[2048ᵀ,2048]`, `[512,2048]×[8192ᵀ,2048]`) on ILGPU and, if ILGPU's fp16 path disappoints, ComputeSharp; record tok-shape GFLOPs + correctness vs CPU | G-P2 | Decision recorded in `.orc/safetensors-spike/gates/OD-A1-decision.md` against the pre-registered criteria + reversal conditions; losing backend's package reference removed |
| ST-302 | GPU allocator + upload (BF16→F16 on upload), no-alloc-in-decode-loop arena (arch §5) | ST-301 | `AllocatedBytes` matches hand-computed expectation for the 1B; decode loop allocation-free (assert via allocator call counter) |
| ST-303 | GPU kernels: remaining 6 ops fp16-storage/F32-accumulate per forward-pass §8; deterministic reduction order | ST-301 | Each GPU op matches CPU op within F16-explainable delta on the op fixtures (upcast compare) |
| ST-304 | GPU `DecodeSession`: full pipeline, KV cache F16, F32 logits | ST-302, ST-303 | Generates end-to-end; VRAM steady state within 10% of ST-106 prediction or the delta explained in writing |
| ST-305 | GPU parity: B16 vs R32 / B16 vs B32; fix loop with localization | ST-304, ST-205 | B16 gate row passes per harness §5 (relative-to-A thresholds — requires ST-401's A-vs-R32 numbers; run ST-401 early if needed) |
| ST-306 | Determinism suite: 10 same-seed runs + cross-process | ST-304 | Bit-identical, or first-divergence analysis written |

### Gate G-P3 — evidence required

| Evidence |
|---|
| OD-A1 decision file with bake-off numbers |
| Parity verdict JSON: B16 rows with raw numbers |
| Determinism result (10/10 bit-identical or the analysis) |
| Measured-vs-predicted VRAM table |

---

## 6. Phase 4 — Benchmark, report, verdict (Days 9–10)

Inputs: [SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) (all).

| ID | Task | Depends | Acceptance criteria |
|---|---|---|---|
| ST-401 | Path A bench driver: convert with pinned `convert_hf_to_gguf.py` → F16 GGUF → drive `LLamaSharpRuntime` (greedy, ctx 4096, full offload); capture logits for A-vs-R32; run full metric suite | ST-201, G-P2 | A's result JSON complete per schema; `NoKvSlot` grep clean (T-9); full-offload confirmed (no degraded `EffectiveGpuLayers`) |
| ST-402 | Context points: Q4_K_M + Q8_0 quantized runs, labeled `context-*` | ST-401 | Result files carry the `context-` path label; excluded from every A/B table by construction |
| ST-403 | Path B full benchmark: throughput/memory/fidelity/determinism suites, alternating with A per T-5 | ST-305, ST-306 | B's result JSON complete; warmup + thermal rules observed (flags recorded) |
| ST-404 | `REPORT.md` per the benchmark plan's template; verdict against charter G-1…G-5 / K-1…K-4, each criterion cited to its evidence file | ST-401–403 | Every checked criterion links a specific artifact; threats-to-validity section instantiated with what actually occurred, not restated generically |
| ST-405 | Cross-machine determinism on a second fleet box — **only if the box has time left**; explicitly droppable | ST-403 | Result recorded descriptively either way ("not-run" is a valid, honest value) |

### Gate G-P4 — evidence required (this is the spike's exit)

| Evidence |
|---|
| Complete result-file set under `.orc/safetensors-spike/results/<runId>/` |
| `REPORT.md` with the GO / NO-GO verdict and per-criterion citations |
| Findings log: every bug found, every dead end, every threshold that had to move (with its rationale commit) |
| A one-page update to this folder's docs marking anything the spike disproved (accuracy-first rule: specs must not outlive contradicting evidence) |

---

## 7. Standing rules for every phase

| Rule | Source |
|---|---|
| No diffs under `OrchestratorIDE/Core/Runtime/` — ever, for any reason, during the spike | Charter §4 |
| Review: each phase's PR goes through the normal review flow (one independent review before merge) | Project convention (`RUNTIME_PHASE0_SPEC.md` §8's reviewer-gate rule) |
| SPDX headers on every new source file | Repo convention |
| No Ollama dependency anywhere in the spike | Standing project rule |
| A gate slipping by more than a day triggers an explicit scope decision (cut ST-402/ST-405 first), never a silent quality cut in parity work | Charter K-4 discipline |
