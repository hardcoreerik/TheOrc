# The Foundry — TheOrc's Specialist Training Suite

This is the Training Pit's Foundry section: the templates, gates, and scripts for
training TheOrc's own small specialist models on local hardware. Strategy and
governance live in [docs/THEORC_FOUNDRY.md](../../docs/THEORC_FOUNDRY.md); this
directory is the executable half.

The suite was scaffolded 2026-07-07 to begin the F-1 → F-2 path for the first
proof model. Building these scripts does **not** change the governance rules:
every track still needs its baseline comparison, and a no-training or
no-promotion outcome remains an acceptable result.

---

## Layout

```text
training_pit/foundry/
  configs/      one recipe per specialist track (the track registry — the UI reads these)
  scripts/      export → preflight → train pipeline
  examples/     reference toolcaller captures (one per decision type)
  baselines/    baseline reports land here (F-1 deliverable, not yet produced)
```

## The Six Tracks

Recipes record the docs' size hypotheses and follow-on order. Only the first is
active; the rest are gated templates whose configs refuse to train
(`status: "template"` + `gates.blocked_reason`).

| Config | Track | Status | Why this order |
|---|---|---|---|
| `toolcaller_v0.json` | theorc-toolcaller | **ACTIVE** | First proof — bounded output space, measurable failures ([docs/THEORC_TOOLCALLER_V0.md](../../docs/THEORC_TOOLCALLER_V0.md)) |
| `dataset_judge_v0.json` | theorc-dataset-judge | template | Follow-on #1 — protects the data flywheel |
| `fabric_v0.json` | theorc-fabric | template | Follow-on #2 — most differentiated specialist |
| `router_v0.json` | theorc-router | template | Follow-on #3 — deterministic baseline must exist first; may end up non-LLM |
| `reviewer_v0.json` | theorc-reviewer | template | Follow-on #4 — needs a stable gold finding set |
| `boss_v2.json` | theorc-boss (smaller) | template | Follow-on #5 — the live 12B adapter is the baseline to beat |

Activating a template later = produce its baseline report, freeze its dataset
contract, flip `status` to `active`, and fill the null dataset paths. The
scripts need no changes; they are config-driven.

## The Toolcaller Pipeline (first proof)

```text
swarm runs stage organic captures        (.orc/swarm/dataset-staging/toolcaller/ — opt-in:
                                          AppSettings.ToolcallerDatasetCaptureEnabled, off by default)
  -> human review accepts/rejects        (training_pit/datasets/toolcaller/)
  -> validate                            (Tools/ToolcallerBench mechanical admission gates)
  -> export                              (scripts/export_toolcaller_dataset.py)
  -> preflight                           (scripts/foundry_preflight.py — counts, leakage, hashes, review state)
  -> train                               (scripts/train_foundry.py — LoRA on Qwen2.5-1.5B-Instruct)
  -> Arena comparison + human promotion  (docs/FOUNDRY_ARENA.md — NOT automated here)
```

Commands (run from the repo root):

```bash
# 1. Export accepted captures to train/eval JSONL (validator gate runs first)
python training_pit/foundry/scripts/export_toolcaller_dataset.py

# 2. Check every gate without touching the GPU
python training_pit/foundry/scripts/foundry_preflight.py --config training_pit/foundry/configs/toolcaller_v0.json

# 3. Verify the full setup end-to-end (loads model + data, one token step)
python training_pit/foundry/scripts/train_foundry.py --config training_pit/foundry/configs/toolcaller_v0.json --dry-run

# 4. ONE real training experiment (this flag IS the explicit per-experiment approval)
python training_pit/foundry/scripts/train_foundry.py --config training_pit/foundry/configs/toolcaller_v0.json --confirm-experiment
```

Or use the **⚒ THE FOUNDRY** section in the Training Pit panel, which wraps the
same scripts (validate / export / train with dry-run default).

## What the Gates Enforce

`foundry_preflight.py` blocks training when:

- the track is a template (no baseline yet)
- exported datasets are missing, under the minimum counts, or malformed
- the export contains unreviewed (pending) captures
- the mechanical validator did not record a PASS
- a lineage group or an identical `call` response appears in both splits
- the frozen tool inventory changed since the dataset was exported
  (hashes are LF-normalized — a CRLF checkout is not a changed inventory)

`train_foundry.py` additionally:

- requires `--confirm-experiment` for any non-dry run (one approval per experiment)
- freezes an immutable `run_manifest.json` (config/dataset/base/seed/git hashes)
  and refuses to silently overwrite a previous run's record
- writes the same `progress.json` heartbeat + `training_summary.json` contract
  as the boss trainer, so the panel monitors it unchanged

A missing baseline report is a loud warning, not a block, for a first
experiment — but it is a hard kill gate for promotion: an adapter cannot enter
the Arena without the baseline comparison it must beat.

## What This Suite Deliberately Does NOT Do

- No automatic promotion — Arena comparison and human approval stay manual.
- No model judges its own training data — the validator is deterministic code.
- No default-runtime integration — a trained adapter is an artifact under
  `training_pit/outputs/foundry_*`, nothing more, until it passes
  [docs/FOUNDRY_ARENA.md](../../docs/FOUNDRY_ARENA.md).

## Current F-1 Status (toolcaller)

Done: frozen inventory + hash, capture schema, mechanical validator, live organic
capture hook, this export/preflight/train pipeline, run-manifest contract.

Open before a promotable result: enough reviewed captures (150 train / 30 eval
minimum, balanced eval), scripted bootstrap for `clarify`/`unsupported` coverage
(organic capture can't produce them), the baseline report
(deterministic + prompt-layer + constrained-base), the frozen promotion margin,
and the chat-template round-trip fixture.
