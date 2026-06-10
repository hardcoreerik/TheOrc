# The Training Pit — Dataset Strategy

> **Status:** Active (Phase 2.5 — Dataset Review / Approval Valve). Phase 3 training is BLOCKED.
> Use `review_captures.py` to approve captures and track progress toward the Phase 3 gate.
> Run `python training_pit/scripts/review_captures.py --status` for live gate counters.

---

## What This LoRA Does — Narrow Goal First

The first LoRA is **not** a general coding improvement.

Gemma 4 12B is already a capable coder and researcher. The problem is
specific: as TheOrc's boss/planner, it collapses single-task plans, produces
vague descriptions, omits filenames, and hallucinates file structures when
given open-ended goals.

**First LoRA target behavior:**

> Given a user's coding goal (with optional project context), produce a valid
> JSON swarm plan: 2–4 tasks, concrete descriptions, named output files,
> consistent API contracts between tasks, no hallucinated dependencies.

Everything in the dataset should teach this specific behavior. Training on
general coding, general Q&A, or random instruction-following is a distraction
for this first adapter.

---

## Three-Tier Source Strategy

### Tier 1 — Real TheOrc captures (target: ~80% of final training set)

Real boss plans captured during normal TheOrc use. These are the
**highest-value examples** because they:

- Come from real user goals, not curated hypotheticals
- Carry the exact system prompt, goal phrasing, and JSON structure
  the fine-tuned model will see in production
- Represent the distribution of actual TheOrc use

**How they are generated:**

1. User runs a swarm via TheOrc
2. `DatasetCapture.cs` (in `OrchestratorIDE/Services/Swarm/`) evaluates
   the boss plan using `EvalRubric.Score()`
3. Qualifying plans are written to `.orc/swarm/dataset-staging/`
   - `plan_capture_good_<runId>_<score:D3>.json` — score ≥ 70
   - `plan_capture_bad_<runId>_<score:D3>.json`  — score ≤ 39
4. Plans in staging are reviewed via `review_captures.py`:
   - `--inspect` shows full detail, conversion preview, validation results
   - `--approve` records the decision in `reviewed_v1.json` manifest
   - `--reject` marks the capture as rejected (documented, not lost)
5. `--export-train` atomically writes `train_v1.jsonl` after validate+sanitize gate
   (similarly `--export-eval` and `--export-negative` for their respective splits)

**Marginal plans (score 40–69) are never staged.** They are noisy — good
enough to not be clear failures but not good enough to be clean training
signal. Adding them harms convergence.

### Tier 2 — Hand-authored golden examples (target: ~15% of training set)

Examples written by a human to represent the exact behavior we want.
These should cover common TheOrc workflows that real captures may not
fully represent early in data collection:

- Code refactor planning (identify which files change, keep others intact)
- Docs-update planning (RESEARCHER reads existing docs, CODER updates)
- Test and verification planning (test file creation, coverage check)
- Model compatibility updates (Training Pit phase control planning)
- UI/backend wiring (UIDEVELOPER ↔ CODER API contract examples)
- Training Pit phase control (e.g. "start Phase 3" request → boss correctly
  checks the Phase 3 gate before proceeding)
- Avoiding hallucinated files (no invented module names)
- Avoiding over-decomposition (5+ tasks when 3 suffice)
- Avoiding one-task collapse ("Execute goal" failure mode)
- Distinguishing verified facts, assumptions, and unknowns in descriptions

Hand-authored examples must use the **exact production system prompt** from
`SwarmSession.BossDecomposeSystemPrompt` (synced copy in
`training_pit/scripts/convert_plan_captures.py` as `BOSS_SYSTEM_PROMPT`).
Examples using a different system prompt are not valid training examples.

Store in: `training_pit/examples/chat_sft_good_*.jsonl`
Validate before promoting: `validate_dataset.py` + `sanitize_dataset.py`

### Tier 3 — Synthetic edge-case examples (~5% of training set, eval-only)

Synthetically constructed examples that stress specific failure modes.
These are used primarily for **eval regression tests and negative example
mining** — not for training.

Synthetic examples are tagged `"source": "synthetic"` and `"quality":
"rejected"` or `"draft"` in metadata. They are never promoted to
`train_v1.jsonl`.

Use synthetic examples to stress:

- Invalid / unparseable JSON output
- Vague plans with no filenames ("Implement the feature")
- One-task collapse ("Execute goal" with empty description)
- Over-decomposed plans (8+ tasks for a simple goal)
- Fake file names that don't match the goal domain
- Ignored user constraints (e.g. user says "no external libs", plan imports 5)
- Private data leakage (IPs, keys, paths embedded in plan descriptions)
- Phase 3 started too early (boss inventing a training job without gate check)

Store in: `training_pit/examples/` with `_eval_` or `_synthetic_` in name
Run `validate_dataset.py` to confirm structure; sanitize to confirm no real
secrets accidentally included.

---

## Why No Public Datasets for the First LoRA

Public datasets (e.g. HuggingFace Code Instructions, ShareGPT, WizardCoder)
are intentionally **not used** for the first Training Pit LoRA. Reasons:

1. **Wrong format.** Public datasets use general chat templates, not
   TheOrc's `BOSS_SYSTEM_PROMPT + "Goal: ..." → JSON plan` format.
   The model would learn a different prompt-response mapping.

2. **Wrong goal distribution.** Public coding datasets mostly contain
   single-function requests ("write a sort function"), not multi-agent
   decomposition tasks ("decompose this goal into 2–4 worker subtasks
   with named output files and API contracts").

3. **Dilution.** Even a small proportion of off-distribution examples
   can shift the adapter away from TheOrc-specific planning behavior.
   With a small dataset (150–200 examples), dilution has outsized effect.

4. **Unknown quality.** Public datasets may contain the exact failure
   patterns we are trying to train out: hallucinated APIs, collapsed tasks,
   wrong role names, vague descriptions.

**Public datasets may be useful in later phases** (e.g. general coding skill
boosters for worker goblins), but they are explicitly excluded from the
first boss-planning adapter.

---

## Dataset Mix — Target for Phase 3

| Source | Target % | Files | Notes |
|--------|----------|-------|-------|
| Real TheOrc captures (reviewed) | ~80% | `train_v1.jsonl` | Highest priority to grow |
| Hand-authored golden examples | ~15% | `train_v1.jsonl` | Covers rare/important workflows |
| Synthetic edge-case | ~5% | `negative_v1.jsonl` | Eval/regression only, not training |

This is a **target range**, not a hard constraint. If real captures are
scarce early, hand-authored examples can temporarily fill a higher proportion
while data collection accumulates. Do not pad with synthetic to hit the
target — synthetic examples dilute.

---

## Phase 3 Gate — Unblock Conditions

Phase 3 training is **blocked** until ALL of the following are true:

| Condition | Required | Current |
|-----------|---------|---------|
| Reviewed positive examples in `train_v1.jsonl` | ≥ 150–200 | 0 |
| Reviewed negative/collapse examples in `negative_v1.jsonl` | ≥ 25–50 | 0 |
| Fixed eval prompts in `evals/` (never used for training) | ≥ 20–40 | 20 starter prompts |
| All training data passed `validate_dataset.py` | 0 errors | — |
| All training data passed `sanitize_dataset.py` | 0 rejects | — |
| `configs/qlora_job_template.json` reviewed for target counts | Done | Needs review at gate |
| Hardware confirmed via `check_hardware.py` | Done | RTX 5070 Ti confirmed |
| Base model compat confirmed via `check_model_compatibility.py` | Done | Gemma4 confirmed |

**Do not start Phase 3 early.** An underfitted boss adapter is worse than
the QAT base model + prompt engineering. The threshold exists because:
- Below ~150 examples, LoRA overfits to the exact examples seen
- With < 25 negatives, the eval loop cannot detect regression on failure modes
- Without held-out eval prompts, there is no reliable pre/post improvement signal

**The Phase 3 gate is enforced programmatically.** All training scripts must call
`phase3_preflight.py` before starting and abort if it exits non-zero:

```powershell
python training_pit/scripts/phase3_preflight.py
# exit 0 = READY, exit 1 = BLOCKED, exit 2 = error
```

`phase3_preflight.py` checks 9 conditions: manifest validity, count thresholds, JSONL
file presence, manifest/file consistency, validation, sanitization, duplicates,
eval isolation, and staging safety.

---

## Dataset Staging Flow

```
[TheOrc swarm run]
        │ boss plan generated
        ▼
DatasetCapture.cs
  EvalRubric.Score() ──► score ≥ 70 or ≤ 39 → staged
  score 40–69 → silently skipped (marginal, noisy)
        │
        ▼
.orc/swarm/dataset-staging/
  plan_capture_good_<runId>_<score>.json   ← raw capture (gitignored)
  plan_capture_bad_<runId>_<score>.json    ← raw capture (gitignored)
        │
        │ [review_captures.py --list / --inspect]
        ▼
training_pit/scripts/review_captures.py
  --approve <path> --split train --quality silver
  --reject  <path> --note "reason"
        │ writes to reviewed_v1.json manifest (tracked in git)
        │ NO JSONL written yet
        ▼
review_captures.py --export-train
  convert approved entries → temp file
  validate_dataset.py gate ──► abort on error
  sanitize_dataset.py gate ──► abort on REJECT
  atomic replace final file
        ▼
training_pit/datasets/
  train_v1.jsonl     ← approved positive examples (gitignored)
  eval_v1.jsonl      ← approved eval examples (gitignored)
  negative_v1.jsonl  ← approved negative/collapse examples (gitignored)
```

**Captures are not automatically promoted to training data.**
Every example that enters `train_v1.jsonl` has been reviewed by a human
and explicitly approved through `review_captures.py`.

---

## Eval Data Isolation

The eval prompts in `training_pit/evals/` are **fixed** and **never used
for training**:

- `boss_behavior_eval_prompts.jsonl` — 10 boss behavior prompts
- `plan_quality_eval_prompts.jsonl`  — 10 plan quality prompts

These prompts exist to measure improvement. If they leak into training,
the model memorizes them and improvements cannot be measured.

Negative examples in `negative_v1.jsonl` are used for:
- Regression eval (confirm the fine-tuned model does NOT produce these outputs)
- DPO/ORPO contrastive pairs (future work)
- They are NOT used as SFT training examples (training on bad outputs teaches bad outputs)

---

## What Enters train_v1.jsonl

Only examples that satisfy ALL of the following:

1. **Source:** real TheOrc capture (reviewed) OR hand-authored golden example
2. **Format:** valid canonical chat-JSONL per `DATASET_SCHEMA.md`
3. **System prompt:** exact `SwarmSession.BossDecomposeSystemPrompt` — no other system prompt
4. **Quality:** `gold` (score ≥ 90) or `silver` (score ≥ 70)
5. **Rubric score:** ≥ 70 composite (for captures) or manually judged equivalent
6. **Validation:** `validate_dataset.py` passed with 0 errors
7. **Sanitization:** `sanitize_dataset.py` passed with 0 rejects

**What does NOT enter train_v1.jsonl:**
- Synthetic examples (use `negative_v1.jsonl` instead)
- Unreviewed auto-captures (review is non-optional)
- Examples with the wrong system prompt
- Public dataset examples
- Examples where the assistant content contains hallucinated APIs or filenames
- Examples where `quality` is `rejected` or `draft`

---

## Role Strategy for Training Examples

### Use execution roles only in training data

Dataset examples must use only the execution role names currently advertised
in `BOSS_SYSTEM_PROMPT`: **RESEARCHER**, **CODER**, **UIDEVELOPER**.

Do not include examples with unadvertised roles (TESTER, DOCS, ARCHITECT, etc.)
in `train_v1.jsonl`. Training the model on roles it cannot find in the system prompt
produces inconsistent decomposition behavior.

### The logical/execution role distinction

TheOrc's runtime supports a two-layer role model (see `ROLE_ARCHITECTURE.md`):

- **Logical role** — what kind of software work the task represents (e.g. TESTER, DOCS)
- **Execution role** — which worker lane runs the task (RESEARCHER, CODER, UIDEVELOPER, TESTER)

The parser (`ParseBossPlan`) normalizes logical roles to execution lanes via an alias map.
Unknown roles fall through to CODER — the runtime never crashes on unrecognized role strings.

For the first LoRA, training examples use execution roles directly. The boss model
is trained to emit exactly what the system prompt describes.

### When to expand dataset roles

Add a new role to training examples only after:
1. The execution lane is fully implemented and tested
2. The role is added to `BOSS_SYSTEM_PROMPT`
3. At least 10 hand-authored golden examples for that role are reviewed

---

## Version History

| Version | Date | Notes |
|---------|------|-------|
| 1.0 | 2026-06-09 | Initial dataset strategy document |
| 1.1 | 2026-06-09 | Phase 2.5: replaced manual staging flow with review_captures.py approval valve |
| 1.2 | 2026-06-09 | Phase 2.5: added phase3_preflight.py programmatic gate for Phase 3 |
