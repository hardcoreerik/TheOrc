# TheOrc — Troubleshooting

> This guide covers the failure modes most likely to block local use of the current app. For normal operation, see [USER_GUIDE.md](USER_GUIDE.md). For model evidence, see [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md).

---

## App Starts But Feels Wrong

Check the status bar first.

Healthy startup should show:

- a build stamp
- a workspace badge once a workspace is open
- a model label
- a sensible status message

If the build stamp is missing or obviously stale, you may be running the wrong binary.

---

## No Models Or Red Backend State

If the app cannot see models:

1. verify your inference backend is running
2. verify the configured host is correct
3. confirm the backend actually has at least one model installed

Typical Ollama checks:

```powershell
ollama serve
ollama list
```

---

## Swarm Launch Is Disabled

The Swarm Board can disable launch for legitimate reasons.

Current gate causes include:

- no workspace
- not enough Ollama parallel slots
- boss model below the minimum planning threshold
- coder model below the minimum coding threshold

If the board explains the gate, trust the explanation. It is computed from real runtime checks.

---

## Model Behaves Inconsistently

Do not assume the catalog score is wrong or right in isolation.

Instead:

1. open Model Wiki / Lab
2. inspect local observations
3. inspect trends
4. run a capability test
5. run or refresh tool-call probes if needed

This is the fastest path to separating "bad model" from "bad fit for this task or hardware."

---

## Training GUI Looks Stuck

ORC ACADEMY uses heartbeat and log freshness, not just optimism.

If training appears hung:

- check `training_pit/outputs/lora_v1/progress.json`
- check `training_pit/outputs/lora_v1/forge.log`
- look for the panel's hang warning
- use Resume if a checkpointed run was interrupted

The panel can also re-attach to a surviving trainer after an app restart.

---

## Dataset Counts Look Wrong

Check these in order:

1. `training_pit/datasets/manifests/reviewed_v1.json`
2. `python training_pit/scripts/review_captures.py --status`
3. `python training_pit/scripts/phase3_preflight.py --json`

If the manifest and exported JSONL disagree, preflight should catch it before training.

---

## Help Window Or Docs Links Break

If in-app docs navigation seems wrong:

- confirm the relevant file exists in `docs/`
- confirm the link format is `[User Guide](USER_GUIDE.md)`
- avoid raw HTML or complex Markdown features the viewer does not support

The help system is intentionally built around simple Markdown and internal relative links.
