# TheOrc — Training Pit Guide

The Training Pit is where TheOrc gets smarter over time. Every time you run the swarm and it does a good job, that behavior can be captured, reviewed, and eventually used to fine-tune an AI model — one that gets better at TheOrc's specific style of task planning.

This guide walks you through the whole process, from capture to trained adapter.

For the review workflow details, see [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md). For system-level context, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## What the Training Pit Is

The Training Pit is TheOrc's pipeline for turning swarm behavior into a custom AI adapter.

Here's the basic idea: when the boss AI makes a good plan during a swarm run, that plan gets saved as an example. After enough examples are collected and reviewed, you can train a new AI model that knows how to plan like TheOrc's boss — but fine-tuned on your actual usage.

The result is a LoRA adapter (a lightweight add-on to a base AI model) that you can run locally.

---

## The Current Dataset

As of v1.6, the dataset contains:

- **900** approved training examples
- **87** approved evaluation examples
- **25** approved negative examples (examples of what NOT to do)

These counts pass all current training thresholds. The v1 adapter (`theorc-boss:gemma4-ft`) is live and available to pull.

---

## The End-to-End Flow

Here's the full journey from swarm run to trained adapter:

```text
Swarm run
  -> Capture system saves the boss plan
  -> Prescreen script catches obvious defects automatically
  -> Judge script flags likely fabrication risk (AI-assisted triage)
  -> You review and approve examples
  -> Export script builds the training files
  -> Preflight script checks everything is ready
  -> ORC ACADEMY runs the training
  -> Adapter, checkpoints, and summary are saved
```

Each step is explained below.

---

## Step 1: The Capture System

During every swarm run, the capture system (built into the swarm session) evaluates the boss's plan as it's created.

- Plans scoring **70 or above** are staged as positive training examples
- Plans scoring **39 or below** are staged as negative examples (showing bad planning)
- Plans in between are skipped — ambiguous examples don't help training

Staged plans are saved as JSON files waiting for review.

---

## Step 2: Prescreen

The prescreen script (`prescreen_captures.py`) runs automatically and catches obvious mechanical problems — things like invalid role assignments or a TESTER lane trying to write files. These are clear errors that shouldn't need human review time.

It's deterministic: same input, same output every time.

---

## Step 3: Judge Triage

The judge script (`judge_captures.py`) uses a local AI model to sort captures by "fabrication risk" — how likely is it that the boss made up steps that wouldn't actually work?

This is AI-assisted triage, not a final decision. The judge flags things for your attention; it doesn't approve or reject on its own.

---

## Step 4: Human Review

You look at the flagged and unflagged captures and make the final call on each one: keep it or throw it away.

Your decisions are recorded in the **review file** (`training_pit/datasets/manifests/reviewed_v1.json`). This file is the official record of what's approved. Nothing becomes training data unless it's in here.

See [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) for the detailed review workflow.

---

## Step 5: Export

Once you've reviewed a batch of captures, the export script (`review_captures.py`) reads your approved decisions and builds the actual training files (JSONL format — one JSON object per line, one example per object).

The three files produced are:

- `train.jsonl` — the examples the model learns from
- `eval.jsonl` — examples used to measure how well training is going
- `negative.jsonl` — examples of plans to avoid

---

## Step 6: Preflight Gate

Before training starts, the preflight script (`phase3_preflight.py`) checks that everything is actually ready:

- Are there enough examples?
- Is the manifest valid?
- Do the export files exist and match the manifest?
- Did the sanitizer pass?
- Are there duplicate examples?
- Is the eval set isolated from the training set?

If anything fails, training doesn't start. This is intentional — it's better to catch a problem now than to waste hours training on bad data.

---

## Step 7: ORC ACADEMY

ORC ACADEMY is the in-app training interface. Once preflight passes, you kick off training here.

What you can do in ORC ACADEMY:

- **Start** a new training run
- **Resume** a run that was interrupted
- **Dry run** — check the setup without actually training
- **Set a VRAM cap** — limit how much GPU memory training uses
- **Watch progress** — a live heartbeat bar shows training status
- **Re-attach** — if you close and reopen the app during training, ORC ACADEMY reconnects to the still-running trainer process

What happens during training:

- The base model (Google Gemma 4 12B) is loaded in 4-bit compressed format to fit in memory
- LoRA fine-tuning runs on your approved examples
- Checkpoints are saved periodically so progress isn't lost if something interrupts
- A progress JSON file is updated constantly (this is what the heartbeat bar reads)
- A final summary JSON is saved when training completes

The trained adapter is saved under `training_pit/outputs/lora_v1/adapter/`.

---

## Pit Boss — The Setup Wizard

Not sure how to get started with training? Pit Boss is a wizard that walks you through it.

It asks you 8 questions:

1. What kind of task do you want the model to be better at?
2. What's the target goal style?
3. What roles should it use?
4. How many examples do you want to generate?
5. What quality threshold?
6. What base model?
7. Any negative examples needed?
8. Ready to generate?

Based on your answers, Pit Boss creates a training plan and generates an initial dataset automatically. It then hands the results to ORC ACADEMY to start training.

Pit Boss is the fastest way to go from "I want to fine-tune something" to actually training — without setting up every piece manually.

---

## ORC ACADEMY v1 Adapter

The first trained adapter — `theorc-boss:gemma4-ft` — is live. It was trained on 900 examples from real TheOrc swarm runs.

You can pull it and use it as the boss model in Swarm mode to see the difference between the base model and the fine-tuned version.

---

## The Foundry — Specialist Models

The newest Training Pit section. While ORC ACADEMY trains the boss adapter, THE
FOUNDRY is where TheOrc's *small specialist* models are trained — narrow models
for jobs like tool-call proposal, dataset screening, and routing, per the
[Foundry strategy](THEORC_FOUNDRY.md).

What's in the panel section:

- The six planned specialist tracks with their status (only `theorc-toolcaller`
  is active; the other five are gated templates that refuse to train until their
  baseline evidence exists)
- Live counts of toolcaller captures: staged → accepted → exported
- **Validate captures** — runs the mechanical admission gate (ToolcallerBench)
- **Export dataset** — converts accepted captures into train/eval JSONL
- **Train toolcaller** — a gated LoRA run; a preflight blocks it until the
  dataset passes every gate. Dry run is the default; a real run is one explicit
  training experiment.

Everything the section does is also runnable from the command line — see
[training_pit/foundry/README.md](../training_pit/foundry/README.md) for the
pipeline, gates, and per-track recipes.

Organic toolcaller captures are staged automatically during swarm runs once you
enable the capture setting (`ToolcallerDatasetCaptureEnabled`) — this is TheOrc
generating its own training data from real usage.

---

## NIGHT HARVEST

NIGHT HARVEST is an unattended run mode that collects more training examples while you sleep. You set it going before bed, and by morning you have a batch of new captures waiting for review.

The harvest marker watcher (`Tools/harvest_marker_watch.ps1`) can stop NIGHT HARVEST automatically once you've hit your target example count, and leaves a summary note for when you wake up.

---

## What to Read Next

- [DATASET_REVIEW_WORKFLOW.md](DATASET_REVIEW_WORKFLOW.md) — the review and manifest workflow in detail
- [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) — evaluating models before and after training
- [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) — running training jobs on a different machine in your hive
