# TheOrc — Safetensors Engine Spike Risk Register

> **Status: 🔲 Planned.** Enumerated risks for the spike defined in
> [SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md). Likelihood/impact are
> planning-time judgments, recorded before work starts so hindsight can grade them.
> "Impact" is impact **on the spike's ability to produce a defensible go/no-go verdict**,
> not on the product — spike code is disposable; a wrong verdict is not.

---

## 1. Register

| ID | Risk | Likelihood | Impact | Early-warning signal | Mitigation |
|---|---|---|---|---|---|
| R-01 | **GPU kernel performance shortfall** — C#-authored (Level 2) kernels land far below the 25% floor (charter G-4), dominated by naive matmul | High | High | ST-301 bake-off matmul ≪ 10% of cuBLAS on the real shapes | Pre-authorized Level-3 fallback: cuBLAS for GEMM only, everything else stays C# ([NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §6 reversal conditions). If even that misses G-4 → K-3 evaluation, not another week of kernel tuning inside the box |
| R-02 | **Numerical divergence** — parity fails and resists debugging | Medium | High | B32-vs-R32 failing (math bug, not precision) after Day 4; localization (harness §7) pointing nowhere discontinuous | The harness *is* the mitigation: fp32 reference path isolates precision from math; per-layer localization with pre-registered suspect table; K-1 caps how long an unlocalizable failure can burn |
| R-03 | **RoPE / architecture-specific edge cases** — HF-vs-llama.cpp rotation convention, llama3 frequency scaling, tied embeddings | High (it is *the* classic porting bug) | Medium (loud at parity time, cheap once localized) | Layer-0-attention localization hit; long-prompt-only divergence; post-final-norm-only divergence | Pre-registered in three places: forward-pass §4 convention trap, §7 tied-head check, harness §7 suspect table. Long-prompt parity cases mandatory in the suite |
| R-04 | **Scope creep into quantization** — "while we're here, int8 would fix the fit problem too" | Medium | High (silently converts a coverage spike into an unbounded engine project) | Any quantization-shaped code, issue, or doc edit appearing mid-spike | Charter lists it as an explicit non-goal; phasing has no task for it; the two-week box + G-P2 keel rule make the cost of smuggling it visible immediately |
| R-05 | **Second-engine maintenance cost** (post-GO risk, registered now) — a GO creates a permanent parallel lane: every future runtime feature (admission, telemetry, chat templates, tool grammar) lands twice or diverges | Certain, conditional on GO | High, long-term | — (structural) | The GO bar is deliberately high (all five G-criteria); [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §8 routes integration through the existing `IModelRuntime`/depot/gate seams so shared policy stays shared; the report's verdict section must include a maintenance-cost paragraph — a GO that ignores this row is incomplete |
| R-06 | **Backend bet fails late** — chosen GPU backend hits a wall (fp16 arithmetic emulated, JIT bug, driver incompatibility) after Day 6 investment | Medium | High | Bake-off anomalies ignored; op fixtures passing on CPU but failing oddly on GPU | Bake-off is a hard half-day time box with pre-registered reversal conditions *before* pipeline work starts; both candidate packages pinned in ST-003 so switching is a project-file edit, not procurement |
| R-07 | **Reference-oracle setup breaks** — transformers version quirk, wrong dtype, eager-vs-SDPA mismatch makes R32 itself unreliable | Low | High (everything downstream compares against it) | R16-vs-R32 calibration far off published fp16 behavior (T-6 check) | Calibration is a gate-G-P2 evidence item, run before any B comparison is trusted; pins in `requirements.txt`; `attn_implementation="eager"` fixed in the script |
| R-08 | **BF16→F16 conversion loss** — weight values overflow/denormalize in F16 and degrade fidelity in a way that looks like a kernel bug | Low | Medium | Non-zero overflow counter at load ([SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) §5); B16-vs-B32 gap concentrated at layer 0 | Counted-and-reported conversion events; CPU path converts BF16→F32 directly so the comparison pair isolates conversion loss from arithmetic |
| R-09 | **Tokenizer drift between paths** corrupts parity/string-match metrics | Low (by design) | Medium | Token-ID mismatch in fixture round-trip table | Eliminated structurally by OD-F1 (single offline tokenization consumed by A, B, and R); residual detokenization risk covered by comparing token IDs before strings (T-2) |
| R-10 | **Hidden integration cost misjudged** (post-GO risk) — chat-template application (Jinja from `tokenizer_config.json`) has no existing managed path; the GGUF lane's `LLamaTemplate` doesn't transfer | Certain, conditional on GO | Medium | — (structural) | Registered in arch §8 so the GO decision prices it in; spike itself avoids it entirely (pre-tokenized fixtures) — which is exactly why the report must not imply chat-readiness |
| R-11 | **Fleet/driver environment surprises** — the HARDCOREPC native-lib regression precedent: a clean rebuild broke the native lane on one box for weeks | Medium | Medium (lost days, contaminated numbers) | First GPU run failing on environment rather than code | Single designated reference box for all gated numbers; environment recorded in provenance; cross-machine work is explicitly droppable (ST-405) rather than load-bearing |
| R-12 | **Concurrent-session collision** — another session/build/test grabs the GPU mid-benchmark | Medium | Medium (silently noisy numbers) | `nvidia-smi` process list non-empty at run start | Pre-run process-list check aborts the run (benchmark plan §1 hardware control) — the standing fleet lesson applied mechanically |

---

## 2. Review cadence

The register is reviewed at every phase gate (G-P1…G-P4): each row gets `unchanged` /
`fired` / `retired` plus a one-liner, appended to the gate evidence file. A risk that fired
without its early-warning signal firing first means the signal was wrong — fix the register
entry, that's what it's for.
