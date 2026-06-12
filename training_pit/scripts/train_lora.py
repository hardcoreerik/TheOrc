#!/usr/bin/env python3
"""Phase 3 trainer — QLoRA fine-tune of the boss/planner on the reviewed dataset.

Implements training_pit/configs/lora_job_template.json + qlora_job_template.json
via HuggingFace PEFT + TRL (the templates' documented fallback; unsloth's
Windows support is too fragile to gate Phase 3 on).

  python training_pit/scripts/train_lora.py            # full run
  python train_lora.py --dry-run                       # load model+data, one step, exit
  python train_lora.py --base unsloth/gemma-4-12b-it   # alternate un-gated mirror

Outputs the adapter to training_pit/outputs/lora_v1/adapter (+ a training
summary JSON beside it). GGUF export/Ollama deploy is Phase 4 — not done here.
"""
import argparse, json, os, time
from pathlib import Path

# Reduce fragmentation on the 16 GB card (must be set before torch import)
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="google/gemma-4-12b-it",
                    help="HF base repo (fallback mirror: unsloth/gemma-4-12b-it)")
    ap.add_argument("--train", default="training_pit/datasets/train_v1.jsonl")
    ap.add_argument("--eval",  default="training_pit/datasets/eval_v1.jsonl")
    ap.add_argument("--out",   default="training_pit/outputs/lora_v1/adapter")
    ap.add_argument("--epochs", type=float, default=3)
    ap.add_argument("--lr", type=float, default=2e-4)
    ap.add_argument("--max-seq", type=int, default=1536)   # examples are ~600-1200 tokens
    ap.add_argument("--dry-run", action="store_true",
                    help="verify model+data load and a single forward step, then exit")
    ap.add_argument("--vram-cap", type=float, default=0,
                    help="GB of VRAM this process may use (0 = no cap). Lets the "
                         "trainer coexist with other GPU workloads; layers beyond "
                         "the cap offload to system RAM (slower).")
    args = ap.parse_args()

    # Imports deferred so --help works without the training stack installed
    import torch
    from datasets import load_dataset
    from transformers import (AutoModelForCausalLM, AutoTokenizer,
                              BitsAndBytesConfig)
    from peft import LoraConfig
    from trl import SFTConfig, SFTTrainer

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

    cfg = SFTConfig(
        output_dir=str(out_dir.parent / "checkpoints"),
        num_train_epochs=0.01 if args.dry_run else args.epochs,
        learning_rate=args.lr,
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
        metric_for_best_model="eval_loss",
        bf16=True,
        max_length=args.max_seq,
        report_to=[],
    )

    trainer = SFTTrainer(
        model=model,
        args=cfg,
        train_dataset=data["train"],
        eval_dataset=data["eval"],
        processing_class=tok,
        peft_config=lora,
    )

    t0 = time.time()
    result = trainer.train()
    minutes = (time.time() - t0) / 60

    final_eval = trainer.evaluate()
    trainer.save_model(str(out_dir))
    tok.save_pretrained(str(out_dir))

    summary = {
        "base_model": args.base,
        "train_examples": len(data["train"]),
        "eval_examples": len(data["eval"]),
        "epochs": args.epochs,
        "train_loss": round(result.training_loss, 4),
        "eval_loss": round(final_eval.get("eval_loss", -1), 4),
        "minutes": round(minutes, 1),
        "adapter_dir": str(out_dir),
        "finished": time.strftime("%Y-%m-%d %H:%M"),
        "dry_run": args.dry_run,
    }
    (out_dir.parent / "training_summary.json").write_text(
        json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
