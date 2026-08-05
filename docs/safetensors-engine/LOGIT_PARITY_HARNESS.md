# TheOrc — Logit Parity Harness

> **Status: 🔲 Planned.** The correctness oracle for the Safetensors Engine Spike. It
> answers one question with numbers: *does the managed engine compute the same function as
> the reference implementation, to within the precision it claims?* — and when the answer
> is no, it localizes the first divergent layer instead of leaving "the output looks
> wrong" as the debugging starting point.

---

## 1. Roles and definitions

| Symbol | System | Role |
|---|---|---|
| **R32** | HF `transformers`, fp32, CPU or GPU | Ground truth. The definition of "correct" for this spike |
| **R16** | HF `transformers`, fp16, GPU | Calibrates how much divergence fp16 arithmetic *itself* causes in a known-good implementation |
| **A** | F16 GGUF on the shipped LLamaSharp lane | Baseline under the same measurement; also calibrates real-world tolerance |
| **B32** | Spike engine, CPU fp32 path | Isolates math bugs from precision effects |
| **B16** | Spike engine, GPU fp16 path | The system under test |

The comparisons that matter, in debugging order:

| Comparison | What a failure means |
|---|---|
| B32 vs R32 | **Math bug.** Precision cannot explain fp32-vs-fp32 disagreement beyond ~1e-4-scale accumulation-order noise |
| B16 vs B32 | Precision cost of our own F16 path, isolated from correctness |
| B16 vs R32 | The headline fidelity number (charter G-2) |
| A vs R32 | The empirical yardstick: what a mature F16 engine's divergence from truth actually looks like |
| R16 vs R32 | Floor calibration: divergence attributable to fp16 arithmetic alone |

---

## 2. Reference logit generation

A pinned Python script, `Tools/SafetensorsSpike/reference/capture_logits.py` (committed;
`requirements.txt` with pinned `transformers`/`torch` versions; run manually on the dev
box — **not** a build dependency, keeping the .NET side dependency-light; consistent with
the standing rule that TheOrc tooling never grows a hidden service dependency):

1. Load `meta-llama/Llama-3.2-1B-Instruct` with `torch_dtype=float32` (R32) and again
   `float16` (R16), `attn_implementation="eager"` (SDPA/Flash kernels may change reduction
   order; eager is the deterministic reference), `device_map` fixed, seeds fixed
   (irrelevant under greedy but recorded anyway).
2. For each parity case in the prompt set
   ([SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) §4):
   feed the stored token IDs, capture **fp32 logits at every prompt position** plus the
   greedy continuation (32 tokens) with logits at each generated position.
3. Also capture **per-layer hidden states** (`output_hidden_states=True`) for the four
   designated localization cases (§7) — R32 only (fp32 hidden states for 17 checkpoints
   × a few positions is megabytes, not gigabytes, because only 4 cases capture them).

## 3. Storage format

Reference artifacts are **safetensors files** — the spike's own parser reads its own
oracle's container, which both dog-foods the parser and avoids a NumPy dependency in .NET:

```
Tools/SafetensorsSpike/fixtures/reference-v1/
  manifest.json                    # case list, tokenizer id, model sha, script git commit,
                                   # transformers/torch versions, capture date
  case-<id>.logits.safetensors     # tensors: "logits.r32" [T, V] F32, "logits.r16" [T, V] F32
                                   #          (r16 logits upcast to F32 for storage),
                                   #          "greedy.tokens" [32] I64
  case-<id>.hidden.safetensors     # localization cases only:
                                   # "hidden.r32.layer{0..16}" [P, D] F32 (P = probe positions),
                                   # "probe.positions" [P] I64
```

Reference files are committed via Git LFS if any exceeds the repo's large-file comfort;
`manifest.json` carries each file's sha256 either way, and every parity run verifies hashes
before comparing (a stale oracle silently invalidates everything downstream).

---

## 4. Comparison procedure

For each case, at each captured position:

| Step | Detail |
|---|---|
| 1 | Load reference logits (F32) and candidate logits (F32 — every path emits F32 logits per [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) §7) |
| 2 | Compute per-position: max-abs delta, mean-abs delta, top-1 match, top-10 overlap |
| 3 | KL(softmax(ref) ‖ softmax(candidate)) in **fp64** with max-subtracted softmax — fp32 KL of near-identical distributions is dominated by its own rounding |
| 4 | Aggregate per case, then per suite: max of maxes, mean of means, agreement rate with Clopper–Pearson 95% bounds |
| 5 | Greedy continuation: token-ID exact match over 32 steps; first divergent step index recorded when not |

Long-prompt parity cases are mandatory in the suite (they exercise the RoPE llama3-scaling
branch — the pre-registered silent-corruption risk in the forward-pass spec §4).

---

## 5. Tolerance thresholds

Fixed constants are stated where defensible; where honesty requires an empirical yardstick,
the threshold is **defined relative to measured calibration numbers**, with the calibration
run itself part of harness bring-up (Phase 2 exit,
[SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) G-P2):

| Comparison | Threshold | Justification |
|---|---|---|
| Oracle sanity: R16 vs R32 | measured, recorded — no pass/fail | This *defines* the fp16 floor; if it is wildly off published fp16 behavior (top-1 agreement ≪ 99%), the oracle setup is broken (T-6) and everything stops |
| B32 vs R32 | max-abs logit delta ≤ **1e-3**; top-1 agreement = **100%** on prompt positions; greedy 32-token match = 32/32 on all cases | fp32-vs-fp32 with identical math differs only by accumulation order; 1e-3 in logit units is generous for that and far below decision-flipping scale. Any miss → math bug → localize (§7) |
| B16 vs R32 (**gate, charter G-2**) | KL-mean ≤ **1.5 ×** A-vs-R32's KL-mean; top-1 agreement ≥ A-vs-R32's agreement − **0.5 pp**; greedy match count ≥ A's − 1 case | "No worse than the mature F16 engine diverges from truth, with a small margin." Pre-registered as *relative* because an absolute fp16 KL constant would be a number invented in a doc; A's own divergence is the honest, measurable meaning of "acceptable fp16 engine" |
| B16 vs B32 | measured, recorded — no pass/fail | Diagnostic decomposition only |
| Determinism (charter G-3) | byte-identical across 10 same-seed runs | Not a tolerance — identical or failed |

Threshold changes after first measurement follow the pre-registration rule: own commit,
written rationale, flagged in the report.

---

## 6. Pass/fail semantics

| Verdict | Condition |
|---|---|
| **PASS** | B32 row passes AND B16 gate row passes AND determinism passes |
| **PASS-WITH-FINDINGS** | Gate rows pass; any diagnostic row shows something worth a written finding (e.g. B16-vs-B32 gap concentrated in one op) |
| **FAIL-LOCALIZED** | A gate row fails and §7 localized it to a specific layer/op — actionable; feeds the fix loop, not the kill decision, while the time box allows |
| **FAIL-UNLOCALIZED** | A gate row fails and localization cannot isolate it — this is charter kill-criterion K-1 territory |

Cross-machine determinism, if measured, is reported descriptively and affects no verdict
(charter G-3 note).

---

## 7. Divergence localization

When parity fails, binary-search the network depth using the stored per-layer hidden
states (§3):

1. Run the spike engine on a localization case with **hidden-state capture enabled**
   (a debug flag on `DecodeSession` that downloads `xⁱ` after each layer at the probe
   positions — debug-only path, never in timed runs).
2. For layer i = 0 … L: compute max-abs delta between engine `xⁱ` and reference
   `hidden.r32.layer{i}` at the probe positions (CPU path compares fp32-to-fp32; GPU path
   upcasts).
3. The **first layer where the delta jumps discontinuously** (rule of thumb: > 10× the
   running median of previous layers' deltas, flagged automatically by the harness) is the
   suspect. Within that layer, re-run with op-level capture (post-attn vs post-MLP taps)
   to split attention from MLP.
4. Map to the pre-registered suspect table:

| First divergent point | Prime suspects |
|---|---|
| Layer 0, before attention | Embedding lookup, BF16 conversion, tensor-name mapping |
| Layer 0, attention | **RoPE convention (HF half-split vs interleaved — the #1 pre-registered trap)**, GQA head-to-kv mapping, causal mask off-by-one |
| Grows smoothly with depth, small | Accumulation-order noise / F16 rounding — precision, not a bug; check B16-vs-B32 to confirm |
| One specific deep layer only | Shard/tensor offset bug for that layer's weights (format spec V-rules missed something — fix the validator too, not just the bug) |
| Post-final-norm only | Tied-head handling, final RMSNorm weight, logit dtype path |
| Long prompts only | RoPE llama3 scaling branch |
| Decode diverges, prefill parity clean | KV-cache append/indexing (`…MatchesFullPrefillLastRow` fixture should have caught it — extend the fixture with the real repro) |

Every localization outcome — including dead ends — is appended to the spike's findings log
in the report, in the `CONTEXT_FABRIC_BUG_HISTORY.md` style: what was tried, what was
measured, what was disproven.
