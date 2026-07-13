# TheOrc Toolcaller v0 — Baseline Comparison Report

> **Status: ✅ Kill gate PASSED.** The trained specialist beats the untuned base model
> by +33.4 points decision accuracy on the sealed 260-example held-out set.
> Per [THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md) this authorizes the opt-in
> Native Runtime trial only — not default runtime integration.

---

## Run Identity

| Field | Value |
|---|---|
| Date | 2026-07-09 |
| Commit | `789d2e764519d91411f4939a0869ea4fdafd69f8` |
| Frozen tool schema hash (LF) | `c456ca416882788664b14ea332aa968de76735171a2e53a76eac7c4c6e2bfefd` |
| Eval set | `training_pit/datasets/eval_toolcaller_v0.jsonl` (260 examples, held out before training) |
| Train set | `train_toolcaller_v0.jsonl` (1003 examples), eval_loss 0.0831 after 3 epochs |
| Candidate | `foundry_toolcaller_v0_r2/adapter` — Qwen2.5-1.5B-Instruct + LoRA (r=16, α=32, 6 proj targets, 70.5 MB) |
| Baseline | Qwen/Qwen2.5-1.5B-Instruct, no adapter, identical prompt/parser/eval harness |
| Harness | `training_pit/foundry/scripts/eval_toolcaller.py`, greedy decoding (do_sample=False), max_new_tokens=256 |
| Hardware | NEWCOREPC — RTX 5070 Ti 16 GB, bf16 |
| Raw outputs | `training_pit/outputs/foundry_toolcaller_v0_r2/arena/results.json`, `training_pit/outputs/arena_baseline/Qwen_Qwen2.5-1.5B-Instruct/results.json` |

Both runs executed through the Training Pit ARENA panel (Stage 4) — same code path
a promotion re-run will use.

## Results

| Metric | Base (no adapter) | Trained (r2) | Δ |
|---|---:|---:|---:|
| **Decision accuracy** | 63.9% (166/260) | **97.3% (253/260)** | **+33.4 pts** |
| JSON validity | 97.7% | 99.2% | +1.5 |
| Tool precision (call class) | 63.0% | 96.2% | +33.2 |
| Arg exact match (strict JSON equality) | 12.0% | 21.2% | +9.2 |

### Per-class F1

| Decision class | n | Base F1 | Trained F1 |
|---|---:|---:|---:|
| call | 184 | 0.757 | **0.981** |
| no_tool | 34 | 0.615 | **1.000** |
| clarify | 28 | 0.337 | **0.918** |
| unsupported | 14 | **0.000** | **1.000** |

## Interpretation

- The base model **never produces an `unsupported` decision** (0/14) and reaches only
  0.337 F1 on `clarify` — i.e., untuned it *fabricates tool calls instead of admitting
  a gap or asking*. These are exactly the safety-relevant behaviors the spec's
  evaluation section calls out (Missing information → clarify, Unsupported tool →
  unsupported). Training did not merely improve accuracy; it installed the refusal
  behaviors the base model lacks.
- JSON validity was already ~98% untuned, confirming the spec's position that parse
  validity is not the primary metric — the base model formats well and *selects wrong*.
- Arg exact match is strict string-equality over argument JSON; both models produce
  semantically-equivalent-but-differently-phrased args (e.g. reworded `ask_user`
  questions), so 21.2% understates practical quality. Semantic argument scoring is
  future work before this number gates anything.

## Deployed-artifact check (spec: eval must transfer to the exported artifact)

The r2 adapter was converted to GGUF (f16, 36.9 MB) and registered in Ollama as
**`theorc-toolcaller:qwen25-1.5b`** over the Q4_K_M base
(`training_pit/modelfiles/toolcaller-qwen25-1.5b.modelfile`). Spot checks through the
Ollama API reproduce training behavior (exact-match `clarify` decision; correct
tool + semantically exact args on `call` cases). The full 260-example re-run on the
quantized artifact is still owed before promotion — tracked as the remaining
spec requirement.

## Runtime integration state

Opt-in **repair lane** only (`AppSettings.ToolcallerRepairEnabled`, default OFF):
when a swarm worker turn produces content but no parseable tool call, the specialist
gets one shot at converting the stated intent into a proposal
(`Services/Swarm/ToolcallerService.cs`, hook in `SwarmSession.RunWorkerLoopAsync`).
Proposals re-enter the normal execution loop — `ToolPolicyEngine` and human approval
remain authoritative, and a node without the model silently falls back to today's
behavior. Prompt serialization is locked to the training format by
`ToolcallerServiceTests` (20/20 passing).

## Not yet done (before any default-on promotion)

1. Full Arena re-run against the **quantized GGUF artifact** via Ollama (not just the fp16 PEFT adapter)
2. Deterministic and prompt-layer baselines (spec baselines 1–2; this report covers baseline 3)
3. Predeclared promotion margin — this comparison was run before a margin was frozen, so it reads as evidence, not as a formal promotion decision
4. Latency/memory budget measurement in the repair lane under real swarm load
5. Semantic (not string-exact) argument scoring
