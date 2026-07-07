#!/usr/bin/env python3
"""Foundry trainer — config-driven LoRA/QLoRA fine-tune of a TheOrc specialist.

Generic launcher for the training_pit/foundry/configs/*.json recipes. Runs the
Foundry preflight gate, freezes an immutable run_manifest.json (F-1 deliverable
#9), then trains via HuggingFace PEFT + TRL using the same conventions as the
boss trainer (train_lora.py): progress.json heartbeat, checkpoints/, adapter/,
training_summary.json — so the Training Pit panel can watch it unchanged.

  python training_pit/foundry/scripts/train_foundry.py --config training_pit/foundry/configs/toolcaller_v0.json --dry-run
  python train_foundry.py --config ...toolcaller_v0.json --confirm-experiment   # real run

A real (non-dry) run requires --confirm-experiment: docs/THEORC_TOOLCALLER_V0.md
makes "explicit approval to begin one training experiment" an F-1 deliverable,
and this flag is that approval made mechanical.
"""
import argparse, hashlib, json, os, subprocess, sys, time
from pathlib import Path

# Reduce fragmentation (must be set before torch import)
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")

SCRIPTS_DIR = Path(__file__).resolve().parent
REPO_ROOT = SCRIPTS_DIR.parents[2]
sys.path.insert(0, str(SCRIPTS_DIR))
from foundry_preflight import load_config, run_preflight, sha256_file  # noqa: E402


def git_sha() -> str:
    try:
        return subprocess.check_output(
            ["git", "rev-parse", "--short", "HEAD"], cwd=REPO_ROOT,
            stderr=subprocess.DEVNULL, text=True).strip()
    except Exception:
        return "unknown"


def write_run_manifest(cfg: dict, config_path: Path, out_dir: Path, args) -> Path:
    """One immutable record per output dir. A second, different run must pick a
    new output name — silently retraining over an existing record is refused."""
    manifest_path = out_dir / "run_manifest.json"
    ds = cfg["dataset"]
    manifest = {
        "foundry_track": cfg["foundry_track"],
        "job_name": cfg["job"]["name"],
        "config_path": str(config_path),
        "config_sha256": sha256_file(config_path),
        "base_model": cfg["base_model"]["hf_repo"],
        "train_path": ds["train_path"],
        "train_sha256": sha256_file(REPO_ROOT / ds["train_path"]),
        "eval_path": ds["eval_path"],
        "eval_sha256": sha256_file(REPO_ROOT / ds["eval_path"]),
        "tool_schema_hash": cfg["gates"].get("tool_schema_hash"),
        "seed": args.seed,
        "git_sha": git_sha(),
        "command": " ".join(sys.argv),
        "created": time.strftime("%Y-%m-%d %H:%M:%S"),
        "dry_run": args.dry_run,
    }
    if manifest_path.exists():
        prior = json.loads(manifest_path.read_text(encoding="utf-8"))
        immutable = {k: v for k, v in manifest.items() if k not in ("command", "created", "dry_run")}
        prior_cmp = {k: prior.get(k) for k in immutable}
        if prior.get("dry_run") and not args.dry_run:
            pass  # a dry-run record may be superseded by the real run
        elif prior_cmp != immutable and not args.resume:
            print(f"[MANIFEST] {manifest_path} already records a different run "
                  "(config/dataset/base/seed changed). Pick a new --out name or pass --resume "
                  "to continue the recorded run. Refusing to overwrite an immutable record.")
            raise SystemExit(1)
        elif args.resume:
            return manifest_path  # keep the original record intact
    manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest_path


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--config", required=True, type=Path,
                    help="Foundry recipe (training_pit/foundry/configs/*.json)")
    ap.add_argument("--out", default=None,
                    help="override output dir (default: config output.dir)")
    ap.add_argument("--dry-run", action="store_true",
                    help="preflight + model/data load + a token training step, then exit")
    ap.add_argument("--confirm-experiment", action="store_true",
                    help="explicit approval to run ONE real training experiment "
                         "(required for non-dry runs; see docs/THEORC_TOOLCALLER_V0.md)")
    ap.add_argument("--resume", action="store_true",
                    help="resume from the latest checkpoint in the output dir")
    ap.add_argument("--vram-cap", type=float, default=0,
                    help="GB of VRAM this process may use (0 = no cap)")
    ap.add_argument("--seed", type=int, default=42)
    ap.add_argument("--skip-gates", action="store_true",
                    help="bypass the Foundry preflight gate (document why; the run manifest "
                         "still records everything)")
    args = ap.parse_args()

    cfg = load_config(args.config)
    out_dir = Path(args.out) if args.out else REPO_ROOT / cfg["output"]["dir"]
    out_dir.mkdir(parents=True, exist_ok=True)

    # ── Progress heartbeat (same contract as train_lora.py) ──────────────────
    progress_path = out_dir / "progress.json"

    def beat(status, **kw):
        kw.update(status=status, updated=time.strftime("%Y-%m-%d %H:%M:%S"),
                  pid=os.getpid(), track=cfg["foundry_track"])
        progress_path.write_text(json.dumps(kw), encoding="utf-8")

    beat("starting")

    # ── Gates before VRAM ─────────────────────────────────────────────────────
    if args.skip_gates:
        print("[GATES] SKIPPED by flag — this run cannot feed an Arena promotion")
    else:
        findings = run_preflight(cfg)
        if findings:
            print(f"[GATES] BLOCKED — {len(findings)} finding(s):")
            for f in findings:
                print(f"  [x] {f}")
            beat("blocked_preflight", findings=findings[:10])
            raise SystemExit(1)
        print("[GATES] PASS — every Foundry preflight gate satisfied")

    if not args.dry_run and not args.confirm_experiment:
        print("[APPROVAL] A real training run requires --confirm-experiment (one explicit "
              "approval per experiment — docs/THEORC_TOOLCALLER_V0.md, F-1 deliverable #11). "
              "Use --dry-run to validate the setup without approval.")
        beat("blocked_approval")
        raise SystemExit(1)

    manifest_path = write_run_manifest(cfg, args.config, out_dir, args)
    print(f"run manifest: {manifest_path}")

    # Imports deferred so --help and gate failures work without the stack installed
    import torch
    from datasets import load_dataset
    from transformers import (AutoModelForCausalLM, AutoTokenizer,
                              BitsAndBytesConfig, TrainerCallback, set_seed)
    from peft import LoraConfig
    from trl import SFTConfig, SFTTrainer

    set_seed(args.seed)
    print(f"seed: {args.seed}")

    class HeartbeatCallback(TrainerCallback):
        def on_log(self, args_, state, control, logs=None, **kw):
            logs = logs or {}
            beat("training", step=state.global_step, max_steps=state.max_steps,
                 epoch=round(state.epoch or 0, 2),
                 loss=logs.get("loss"), eval_loss=logs.get("eval_loss"),
                 lr=logs.get("learning_rate"))
        def on_step_end(self, args_, state, control, **kw):
            if state.global_step % 5 == 0:
                beat("training", step=state.global_step, max_steps=state.max_steps,
                     epoch=round(state.epoch or 0, 2))
        def on_evaluate(self, args_, state, control, metrics=None, **kw):
            beat("evaluating", step=state.global_step, max_steps=state.max_steps,
                 eval_loss=(metrics or {}).get("eval_loss"))

    assert torch.cuda.is_available(), \
        "CUDA unavailable — install the cu128 torch build before training."
    total_gb = torch.cuda.get_device_properties(0).total_memory / 2**30
    print(f"GPU: {torch.cuda.get_device_name(0)} ({total_gb:.0f} GB)")

    max_memory = None
    if args.vram_cap > 0:
        torch.cuda.set_per_process_memory_fraction(min(1.0, args.vram_cap / total_gb), 0)
        max_memory = {0: f"{max(1.0, args.vram_cap - 1.5):.1f}GiB", "cpu": "48GiB"}
        print(f"VRAM cap: {args.vram_cap:.1f} GB (placement budget {max_memory[0]})")

    ds_cfg = cfg["dataset"]
    data = load_dataset("json", data_files={
        "train": str(REPO_ROOT / ds_cfg["train_path"]),
        "eval": str(REPO_ROOT / ds_cfg["eval_path"])})
    print(f"train={len(data['train'])}  eval={len(data['eval'])}")

    base_cfg = cfg["base_model"]
    base = base_cfg["hf_repo"]
    beat("loading_model", base=base)
    quant = None
    if base_cfg.get("quantization") == "4bit":
        qc = base_cfg.get("quantization_config", {})
        quant = BitsAndBytesConfig(
            load_in_4bit=True,
            bnb_4bit_compute_dtype=torch.bfloat16,
            bnb_4bit_quant_type=qc.get("bnb_4bit_quant_type", "nf4"),
            bnb_4bit_use_double_quant=qc.get("bnb_4bit_use_double_quant", True))
        print(f"loading base: {base} (4-bit NF4)…")
    else:
        print(f"loading base: {base} (bf16)…")
    tok = AutoTokenizer.from_pretrained(base)
    model = AutoModelForCausalLM.from_pretrained(
        base, quantization_config=quant, device_map="auto", max_memory=max_memory,
        dtype=torch.bfloat16, attn_implementation="eager")
    print(f"model footprint: {model.get_memory_footprint() / 2**30:.1f} GB")

    ad = cfg["adapter"]
    lora = LoraConfig(
        r=ad["rank"], lora_alpha=ad["alpha"], lora_dropout=ad["dropout"],
        bias=ad.get("bias", "none"), task_type=ad.get("task_type", "CAUSAL_LM"),
        target_modules=ad["target_modules"])

    hp = cfg["hyperparams"]
    if hp.get("gradient_checkpointing", True):
        model.config.use_cache = False  # incompatible with gradient checkpointing

    adapter_dir = out_dir / "adapter"
    sft = SFTConfig(
        output_dir=str(out_dir / "checkpoints"),
        num_train_epochs=0.01 if args.dry_run else hp["num_epochs"],
        learning_rate=hp["learning_rate"],
        seed=args.seed,
        data_seed=args.seed,
        per_device_train_batch_size=hp["per_device_train_batch_size"],
        per_device_eval_batch_size=1,
        gradient_accumulation_steps=hp["gradient_accumulation_steps"],
        gradient_checkpointing=hp.get("gradient_checkpointing", True),
        warmup_steps=hp.get("warmup_steps", 10),
        weight_decay=hp.get("weight_decay", 0.01),
        optim=hp.get("optimizer", "adamw_8bit"),
        lr_scheduler_type=hp.get("lr_scheduler", "cosine"),
        logging_steps=hp.get("logging_steps", 10),
        eval_strategy="steps",
        eval_steps=hp.get("eval_steps", 25),
        save_steps=hp.get("save_steps", 25),
        save_total_limit=2,
        load_best_model_at_end=not args.dry_run,
        metric_for_best_model=hp.get("metric_for_best_model", "eval_loss"),
        greater_is_better=False,
        bf16=True,
        max_length=hp.get("max_seq_length", 2048),
        report_to=[],
    )

    trainer = SFTTrainer(
        model=model, args=sft,
        train_dataset=data["train"], eval_dataset=data["eval"],
        processing_class=tok, peft_config=lora,
        callbacks=[HeartbeatCallback()])

    ckpt_dir = out_dir / "checkpoints"
    resume = args.resume and ckpt_dir.exists() and any(ckpt_dir.glob("checkpoint-*"))

    t0 = time.time()
    result = trainer.train(resume_from_checkpoint=resume or None)
    minutes = (time.time() - t0) / 60

    beat("final_eval")
    final_eval = trainer.evaluate()
    beat("saving")
    trainer.save_model(str(adapter_dir))
    tok.save_pretrained(str(adapter_dir))

    summary = {
        "foundry_track": cfg["foundry_track"],
        "job_name": cfg["job"]["name"],
        "base_model": base,
        "train_examples": len(data["train"]),
        "eval_examples": len(data["eval"]),
        "epochs": hp["num_epochs"],
        "learning_rate": hp["learning_rate"],
        "seed": args.seed,
        "git_sha": git_sha(),
        "command": " ".join(sys.argv),
        "train_loss": round(result.training_loss, 4),
        "eval_loss": round(final_eval.get("eval_loss", -1), 4),
        "minutes": round(minutes, 1),
        "adapter_dir": str(adapter_dir),
        "run_manifest": str(manifest_path),
        "finished": time.strftime("%Y-%m-%d %H:%M"),
        "dry_run": args.dry_run,
    }
    (out_dir / "training_summary.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8")
    beat("done", train_loss=summary["train_loss"], eval_loss=summary["eval_loss"],
         minutes=summary["minutes"])
    print(json.dumps(summary, indent=2))
    print("\nNext: rerun the FULL held-out evaluation on the exported/quantized artifact, "
          "then a Foundry Arena comparison against the frozen baselines "
          "(docs/FOUNDRY_ARENA.md) before any promotion.")


if __name__ == "__main__":
    main()
