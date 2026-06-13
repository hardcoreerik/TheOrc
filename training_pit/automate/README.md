# ORC ACADEMY — HARDCOREPC Automate Suite

Everything needed to run the training pit pipeline from HARDCOREPC's 6GB GPU.

## Quick Start

1. Copy this folder to `C:\Users\<you>\Desktop\Automate\`
2. Edit `config.env` — set your main machine IP and workspace path
3. Run `setup.ps1` once to verify connectivity and install dependencies
4. Run `menu.cmd` to launch the text menu

## What Each Script Does

| Script | GPU | Description |
|--------|-----|-------------|
| `01_scout_goals.ps1` | 3-4 GB | Generate goal batches via local Qwen 7B |
| `02_farm_remote.ps1` | 0 GB | Farm goals through boss on main machine |
| `03_night_harvest.ps1` | 3-4 GB | Overnight gen+farm loop (runs unattended) |
| `04_synthetic_gen.py` | 3-4 GB | Generate synthetic boss plan SFT examples |
| `05_qlora_7b.py` | 5-6 GB | Fine-tune Qwen2.5-Coder-7B (goblin adapter) |
| `06_eval_subset.py` | 5-6 GB | A/B eval on 20 examples (+ platform coherence) |
| `07_dataset_stats.py` | 0 GB | Dataset health dashboard |
| `08_review_batch.py` | 0 GB | Interactive plan review (works over network) |
| `09_sync_results.ps1` | 0 GB | Push local work back to main machine |

## VRAM Budget (6GB)

```
Qwen2.5-Coder-7B at 4-bit NF4:   ~3.5 GB weights
Optimizer (paged_adamw_8bit):     ~0.8 GB
Activations + grads (seq=512):    ~1.2 GB
                                  ─────────
Total (tight but fits):           ~5.5 GB
```

Use `--max-seq 512` and `--batch 1` for training. If OOM, reduce `--rank 4`.

## Night Harvest Workflow

```
[HARDCOREPC]                     [MAIN MACHINE]
01_scout_goals.ps1 ──────────>  writes goals to \\MAIN\F$\...\training_pit\
02_farm_remote.ps1  ─ remote ─> theorc-boss:gemma4 decomposes goals
                                 staged captures land in dataset-staging\
```

Then on main machine:
```
python review_captures.py --status
python review_captures.py --inspect <file>
python review_captures.py --approve <file>
```

## 7B Goblin Adapter Training

HARDCOREPC is the right machine for goblin adapter training (smaller models).
The boss model (Gemma 4 12B) is too large for 6GB — that stays on main.

Target adapters for HARDCOREPC:
- `qwen2.5-coder:7b` → CODER / TESTER goblin adapter
- `qwen2.5:7b-instruct` → RESEARCHER goblin adapter

Training data for goblin adapters: captured worker outputs from swarm runs
(future — DatasetCapture.cs currently only captures boss plans).

## Platform Coherence Check

`06_eval_subset.py` includes a 6th rubric dimension not in the main eval:
**platform_coherent** — detects when the model writes Python files for a C# goal
(or vice versa). This catches the hallucination type seen in early smoke tests.

If `platform_coherent` improves significantly with the FT adapter, that's
evidence the training data is teaching language-appropriate output.
