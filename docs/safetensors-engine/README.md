# TheOrc — Safetensors Engine Spike Docs

> **Status: 🔲 Planned.** Specification suite for a time-boxed two-week spike: can a
> managed .NET inference engine run HuggingFace safetensors models directly — coverage,
> not fit — with numbers honest enough to make a go/no-go call? Nothing in this folder is
> implemented. Start with the charter.

---

## Reading order

| # | Doc | What it is |
|---|---|---|
| 1 | [SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md) | **Charter** — problem, coverage-vs-fit framing, purity ladder, scope, go/no-go + kill criteria |
| 2 | [SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) | Byte-level safetensors parsing, validation, sharding, `config.json`/tokenizer mapping, size prediction |
| 3 | [NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) | Components + interfaces, GPU backend decision matrix, residency seam, `IModelRuntime` integration design note |
| 4 | [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) | The math — every op with formulas, named shapes, dtypes, stability notes |
| 5 | [SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) | Head-to-head methodology, metrics, statistics, result schema, threats to validity |
| 6 | [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) | Correctness oracle — reference logits, tolerances, pass/fail, divergence localization |
| 7 | [SPIKE_PHASING_AND_GATES.md](SPIKE_PHASING_AND_GATES.md) | Day-by-day plan, ST-xxx task IDs, per-phase evidence-backed exit gates |
| 8 | [RISK_REGISTER.md](RISK_REGISTER.md) | Risks with likelihood, impact, early-warning signals, mitigations |

Related existing docs: [../RUNTIME_SUPPORT_MATRIX.md](../RUNTIME_SUPPORT_MATRIX.md) (the
four shipped runtime lanes this spike must not disturb),
[../RUNTIME_PHASE0_SPEC.md](../RUNTIME_PHASE0_SPEC.md) (the native lane's original spec and
phasing style these docs follow).
