# The Training Pit — Architecture

> **Status:** Phase 1 scaffolding complete. No training backend implemented yet.

---

## System Overview

The Training Pit is TheOrc's adapter lifecycle system. It is not a standalone training
framework — it is a structured layer that sits between TheOrc's runtime (SwarmSession,
ModelProfiles, OllamaClient) and the external training ecosystem (Unsloth, HuggingFace,
Ollama model management).

```
┌─────────────────────────────────────────────────────────┐
│                      TheOrc Runtime                      │
│  SwarmSession → OllamaClient → Ollama server             │
│  ModelProfiles (scores, temperatures, supplements)        │
│  SwarmConfigAdvisor (role routing)                       │
└───────────────────────┬─────────────────────────────────┘
                        │ eval results, adapter tags
                        ▼
┌─────────────────────────────────────────────────────────┐
│                   The Training Pit                        │
│                                                          │
│  [Capture] ──► [Dataset] ──► [Eval] ──► [Train]         │
│      │              │            │          │            │
│  plan captures   chat JSONL  rubric     LoRA/QLoRA       │
│  terminal logs   validated   scores     adapter GGUF     │
│  swarm traces    sanitized   scorecards  registry        │
│                                                          │
│  [Adapters] ◄─────────────────────────────────────────  │
│  registry.json, local/, imported/                        │
└─────────────────────────────────────────────────────────┘
                        │ ollama create / model pull
                        ▼
┌─────────────────────────────────────────────────────────┐
│                    Ollama Server                          │
│  theorc-boss:gemma4   (current: QAT base + prompt tuned) │
│  theorc-boss:gemma4-ft (future: QAT base + LoRA adapter) │
└─────────────────────────────────────────────────────────┘
```

---

## Concepts

### Dataset

Two formats are used:

1. **Chat JSONL** (`DATASET_SCHEMA.md`) — canonical training format. Each line is a
   `{messages: [...], metadata: {...}}` object. This is what gets fed to a LoRA trainer.

2. **Plan capture** (`PLAN_CAPTURE_SCHEMA.md`) — specialized format for capturing boss/swarm
   planning outputs with quality scores. Plan captures can be converted to chat JSONL for
   training, or used as DPO/ORPO contrastive pairs (future work).

### Evals

Two rubrics exist (see `EVAL_RUBRIC.md`):

1. **Plan Quality Rubric** (0–100) — scores the structural quality of a boss decomposition plan.
   Dimensions: task count, description depth, filename presence, API contract, domain accuracy, JSON validity.

2. **Boss Behavior Rubric** (0–26) — scores TheOrc-specific behavioral qualities across 13 categories.
   Used to evaluate whether an adapter actually improves TheOrc behavior vs the base model.

Eval prompts live in `evals/`. Scorecards from before/after evaluations live in `evals/scorecards/`.

### Adapters

Adapters are LoRA/QLoRA weight files that modify a base model's behavior. They are:
- Stored in `adapters/local/` (trained here) or `adapters/imported/` (external source)
- Registered in `adapters/registry.json` with status, eval state, and role targets
- Deployed by creating a new Ollama model via Modelfile with the merged GGUF

An adapter is never activated in TheOrc until:
1. It is registered in `adapters/registry.json`
2. Its `eval_status` is `approved`
3. A corresponding entry exists in `ModelProfiles.cs`

### Scripts

Utility scripts in `scripts/` cover the data pipeline:
- `validate_dataset.py` — structural validation before training
- `sanitize_dataset.py` — secret/PII scanning before training
- `check_hardware.py` — VRAM detection and training tier estimation
- `check_model_compatibility.py` — base model compatibility check
- `inspect_adapter.py` — adapter metadata inspection

These scripts are standalone Python utilities. They do not depend on the C# application.

---

## Integration Points in TheOrc Runtime

These are the hooks where The Training Pit connects to the live application.
None are implemented yet — this documents where they go when Phase 2 starts.

### SwarmSession — DatasetCapture hook

Built and live in `OrchestratorIDE/Services/Swarm/DatasetCapture.cs`.
Called in `RunInternalAsync()` after `ParseBossPlan()` succeeds:

```csharp
// After Tasks = ParseBossPlan(bossRaw) and Tasks.Count > 0 guard:
await DatasetCapture.StageAsync(_runId, userGoal, bossRaw, Tasks, _bossModel, DatasetStagingDir);
```

`EvalRubric.Score()` determines whether the plan qualifies:
- Score ≥ 70 → staged as `plan_capture_good_<runId>_<score>.json`
- Score ≤ 39 → staged as `plan_capture_bad_<runId>_<score>.json`
- Score 40–69 → silently skipped (marginal)

`StageAsync` is best-effort — all exceptions are swallowed so capture failures never disrupt swarm runs.

### ModelProfiles — Adapter entry

```csharp
// When an adapter is approved, add an entry to the _profiles dictionary
["theorc-boss:gemma4-ft"] = new(
    "theorc-boss:gemma4-ft", "TheOrc Boss — Gemma 4 12B QAT + LoRA v1",
    ...
    BossScore: 8,   // measured, not guessed
    ...
)
```

### SwarmConfigAdvisor — A/B routing

```csharp
// Future: allow SwarmConfigAdvisor to prefer the fine-tuned model
// when it is available and approved
// Location: PickBestForRole()
```

---

## Phase Roadmap

| Phase | Status | Description |
|---|---|---|
| 1 — Scaffolding | **Done** | Schemas, rubrics, configs, scripts, examples, adapter registry |
| 2 — Data collection | **Active** | DatasetCapture.cs built and live; accumulating real captures; see DATASET_STRATEGY.md for Phase 3 gate |
| 3 — Training | **Blocked** | Blocked pending ≥150 reviewed positive + ≥25 negative examples; Unsloth LoRA, eval loop, GGUF export |
| 4 — Deployment | Future | ModelProfiles entry, A/B routing, swarm-metrics benchmark |

---

## Training Targets by Role

| Role | Primary adapter target | Why |
|---|---|---|
| Boss (TheOrc) | `gemma4:12b` QAT base | Planning collapse is the highest-priority failure |
| Goblin Python | `qwen2.5-coder:7b` | Fast, cheap, domain-focused adapter |
| Goblin UI | `gemma4:12b` or `qwen2.5-coder:7b` | UI-specific examples (XAML, React) |
| Goblin Embedded | Separate eval required | ESP-IDF, C++, hardware domain |
| Goblin Docs | `qwen2.5:14b-instruct` | Writing quality adapter |
| Goblin Tests | `qwen2.5-coder:7b` | Test generation discipline |

---

*Last updated: 2026-06-09 — Phase 1 architecture documented.*
