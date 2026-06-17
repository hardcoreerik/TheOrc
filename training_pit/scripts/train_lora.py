#!/usr/bin/env python3
"""Phase 3 trainer — QLoRA fine-tune of the boss/planner on the reviewed dataset.

Implements training_pit/configs/lora_job_template.json + qlora_job_template.json
via HuggingFace PEFT + TRL (the templates' documented fallback; unsloth's
Windows support is too fragile to gate Phase 3 on).

  python training_pit/scripts/train_lora.py            # full run
  python train_lora.py --dry-run                       # load model+data, one step, exit
  python train_lora.py --base unsloth/gemma-4-12b-it   # alternate un-gated mirror

Outputs the adapter to training_pit/outputs/lora_v2/adapter (+ a training
summary JSON beside it). GGUF export/Ollama deploy is a separate step.
"""
import argparse, json, os, sys, time
from pathlib import Path

# Reduce fragmentation on the 16 GB card (must be set before torch import)
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="google/gemma-4-12b-it",
                    help="HF base repo (fallback mirror: unsloth/gemma-4-12b-it)")
    ap.add_argument("--train", default="training_pit/datasets/train_v2gold.jsonl")
    ap.add_argument("--eval",  default="training_pit/datasets/eval_v2gold.jsonl")
    ap.add_argument("--out",   default="training_pit/outputs/lora_v2/adapter")
    ap.add_argument("--epochs", type=float, default=3)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--max-seq", type=int, default=1536)   # examples are ~600-1200 tokens
    ap.add_argument("--dry-run", action="store_true",
                    help="verify model+data load and a single forward step, then exit")
    ap.add_argument("--vram-cap", type=float, default=0,
                    help="GB of VRAM this process may use (0 = no cap). Lets the "
                         "trainer coexist with other GPU workloads; layers beyond "
                         "the cap offload to system RAM (slower).")
    ap.add_argument("--resume", action="store_true",
                    help="resume from the latest checkpoint in the output dir")
    ap.add_argument("--rubric", action=argparse.BooleanOptionalAction, default=True,
                    help="select the best checkpoint by plan-rubric pass-rate "
                         "instead of eval_loss (--no-rubric to disable)")
    ap.add_argument("--rubric-slice", type=int, default=24,
                    help="eval examples scored per rubric pass (cost vs signal)")
    ap.add_argument("--rubric-after", type=float, default=0.4,
                    help="fraction of training after which the rubric actually "
                         "generates+scores; earlier evals are stamped 0.0 (no gen)")
    ap.add_argument("--seed", type=int, default=42,
                    help="RNG seed for python/numpy/torch/cuda — fixed so the "
                         "same config reproduces the same adapter (logged in summary)")
    args = ap.parse_args()

    # ── Progress heartbeat for the WARCHIEF FORGE GUI ─────────────────────────
    # The panel polls this file; a stale mtime while the process lives = hung.
    progress_path = Path(args.out).parent / "progress.json"
    progress_path.parent.mkdir(parents=True, exist_ok=True)

    def beat(status, **kw):
        kw.update(status=status, updated=time.strftime("%Y-%m-%d %H:%M:%S"),
                  pid=os.getpid())
        progress_path.write_text(json.dumps(kw), encoding="utf-8")

    beat("starting")

    # Imports deferred so --help works without the training stack installed
    import torch
    from datasets import load_dataset
    from transformers import (AutoModelForCausalLM, AutoTokenizer,
                              BitsAndBytesConfig, TrainerCallback, set_seed)
    from peft import LoraConfig
    from trl import SFTConfig, SFTTrainer

    # Reproducibility: seed python/numpy/torch/cuda so the same config yields the
    # same adapter. (Full bitwise CUDA determinism is intentionally NOT forced —
    # it breaks some fused kernels and slows training; seeding the RNGs removes
    # the dominant source of run-to-run divergence: init + data shuffling.)
    set_seed(args.seed)
    print(f"seed: {args.seed}")

    class HeartbeatCallback(TrainerCallback):
        """Streams step/loss/eta into progress.json for the Forge GUI."""
        def on_log(self, args_, state, control, logs=None, **kw):
            logs = logs or {}
            beat("training",
                 step=state.global_step, max_steps=state.max_steps,
                 epoch=round(state.epoch or 0, 2),
                 loss=logs.get("loss"), eval_loss=logs.get("eval_loss"),
                 lr=logs.get("learning_rate"))
        def on_step_end(self, args_, state, control, **kw):
            if state.global_step % 5 == 0:
                beat("training", step=state.global_step,
                     max_steps=state.max_steps,
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
        # Hard allocator cap (fail fast past the budget) + load-time placement
        # budget (overflow layers go to CPU RAM instead of GPU).
        torch.cuda.set_per_process_memory_fraction(
            min(1.0, args.vram_cap / total_gb), 0)
        max_memory = {0: f"{max(1.0, args.vram_cap - 1.5):.1f}GiB", "cpu": "48GiB"}
        print(f"VRAM cap: {args.vram_cap:.1f} GB "
              f"(model placement budget {max_memory[0]}, rest offloads to RAM)")

    # ── Data: chat-format JSONL ({messages:[system,user,assistant]}) ─────────
    data = load_dataset("json", data_files={"train": args.train, "eval": args.eval})
    print(f"train={len(data['train'])}  eval={len(data['eval'])}")

    # ── Base model, 4-bit NF4 + double quant (qlora_job_template.json) ───────
    bnb = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_compute_dtype=torch.bfloat16,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True,
    )
    beat("loading_model", base=args.base)
    print(f"loading base: {args.base} (4-bit NF4)…")
    tok = AutoTokenizer.from_pretrained(args.base)
    model = AutoModelForCausalLM.from_pretrained(
        args.base, quantization_config=bnb, device_map="auto",
        max_memory=max_memory,
        dtype=torch.bfloat16, attn_implementation="eager")
    model.config.use_cache = False  # incompatible with gradient checkpointing
    print(f"model footprint: {model.get_memory_footprint() / 2**30:.1f} GB "
          f"(4-bit applied: {'yes' if model.get_memory_footprint() < 12 * 2**30 else 'NO — investigate'})")

    # ── LoRA per lora_job_template.json ───────────────────────────────────────
    lora = LoraConfig(
        r=16, lora_alpha=32, lora_dropout=0.05, bias="none",
        task_type="CAUSAL_LM",
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                        "gate_proj", "up_proj", "down_proj"],
    )

    out_dir = Path(args.out)
    out_dir.parent.mkdir(parents=True, exist_ok=True)

    # Rubric checkpoint selection needs load_best_model_at_end, which a dry-run
    # disables — so it only engages on a real run.
    use_rubric = args.rubric and not args.dry_run

    cfg = SFTConfig(
        output_dir=str(out_dir.parent / "checkpoints"),
        num_train_epochs=0.01 if args.dry_run else args.epochs,
        learning_rate=args.lr,
        # Pass the seed into the Trainer too — it re-seeds from these at train()
        # time, so the global set_seed() above alone would be overridden for the
        # data-shuffling RNG. Both must agree for a run to be reproducible.
        seed=args.seed,
        data_seed=args.seed,
        per_device_train_batch_size=1,          # 16 GB card: bs1 + accum 8
        per_device_eval_batch_size=1,           # default 8 OOMs: 262k-vocab logits
        gradient_accumulation_steps=8,
        gradient_checkpointing=True,
        warmup_steps=10,
        weight_decay=0.01,
        optim="adamw_8bit",
        lr_scheduler_type="cosine",
        logging_steps=10,
        eval_strategy="steps",
        eval_steps=50,
        save_steps=50,
        save_total_limit=2,
        load_best_model_at_end=not args.dry_run,
        # Pick the best-*behaving* checkpoint (rubric) over best-*loss* — v2's
        # regression hid behind a lower eval_loss. See TrainingFlags_Guide.md P3.
        metric_for_best_model="rubric_pass_pct" if use_rubric else "eval_loss",
        greater_is_better=True if use_rubric else False,
        bf16=True,
        max_length=args.max_seq,
        report_to=[],
    )

    callbacks = [HeartbeatCallback()]
    if use_rubric:
        from rubric_callback import RubricEvalCallback
        callbacks.append(RubricEvalCallback(
            tokenizer=tok, eval_rows=data["eval"],
            slice_size=args.rubric_slice, max_new_tokens=args.max_seq,
            after_frac=args.rubric_after, beat=beat))
        print(f"rubric-in-the-loop: best checkpoint by pass-rate over "
              f"{min(args.rubric_slice, len(data['eval']))} eval examples "
              f"(scoring after {args.rubric_after:.0%} of training)")

    trainer = SFTTrainer(
        model=model,
        args=cfg,
        train_dataset=data["train"],
        eval_dataset=data["eval"],
        processing_class=tok,
        peft_config=lora,
        callbacks=callbacks,
    )

    # Resume from the latest checkpoint when asked (Forge "Resume" button)
    ckpt_dir = out_dir.parent / "checkpoints"
    resume = args.resume and ckpt_dir.exists() and any(ckpt_dir.glob("checkpoint-*"))

    t0 = time.time()
    result = trainer.train(resume_from_checkpoint=resume or None)
    minutes = (time.time() - t0) / 60

    beat("final_eval")
    final_eval = trainer.evaluate()
    beat("saving")
    trainer.save_model(str(out_dir))
    tok.save_pretrained(str(out_dir))

    # Capture the exact git revision so an adapter ties back to its source state.
    try:
        import subprocess
        git_sha = subprocess.check_output(
            ["git", "rev-parse", "--short", "HEAD"],
            cwd=Path(__file__).resolve().parent, stderr=subprocess.DEVNULL,
            text=True).strip()
    except Exception:
        git_sha = "unknown"

    summary = {
        "base_model": args.base,
        "train_examples": len(data["train"]),
        "eval_examples": len(data["eval"]),
        "epochs": args.epochs,
        "learning_rate": args.lr,
        "seed": args.seed,
        "rubric_checkpointing": use_rubric,
        "git_sha": git_sha,
        "command": " ".join(sys.argv),
        "train_loss": round(result.training_loss, 4),
        "eval_loss": round(final_eval.get("eval_loss", -1), 4),
        "best_rubric_pass_pct": final_eval.get("eval_rubric_pass_pct"),
        "minutes": round(minutes, 1),
        "adapter_dir": str(out_dir),
        "finished": time.strftime("%Y-%m-%d %H:%M"),
        "dry_run": args.dry_run,
    }
    (out_dir.parent / "training_summary.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8")
    beat("done", train_loss=summary["train_loss"], eval_loss=summary["eval_loss"],
         minutes=summary["minutes"])
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
