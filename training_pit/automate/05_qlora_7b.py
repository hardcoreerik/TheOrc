#!/usr/bin/env python3
"""QLoRA fine-tuning for 7B models on 6GB VRAM (HARDCOREPC).

Target use case: fine-tune qwen2.5-coder:7b as a goblin CODER/TESTER adapter.
Settings are tuned for 6GB VRAM — batch_size=1, grad_accum=16, seq_len=512,
rank=8, gradient_checkpointing=True.

Usage:
  python 05_qlora_7b.py --base Qwen/Qwen2.5-Coder-7B-Instruct ^
      --train coder_train.jsonl --out C:\OrcWork\adapters\goblin_coder_v1
"""
import argparse, json, sys, time
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")


def build_trainer(args):
    import torch
    from datasets import Dataset
    from transformers import (
        AutoModelForCausalLM, AutoTokenizer,
        BitsAndBytesConfig, TrainingArguments,
    )
    from peft import LoraConfig, get_peft_model, prepare_model_for_kbit_training
    from trl import SFTTrainer

    print(f"[QLoRA] Base model:   {args.base}")
    print(f"[QLoRA] Train data:   {args.train}")
    print(f"[QLoRA] Output:       {args.out}")
    print(f"[QLoRA] Epochs:       {args.epochs}")
    print(f"[QLoRA] Rank:         {args.rank} / alpha {args.alpha}")
    print(f"[QLoRA] Seq length:   {args.max_seq}")
    print(f"[QLoRA] Batch size:   {args.batch} x {args.grad_accum} (effective {args.batch * args.grad_accum})")
    print()

    # Load training JSONL
    rows = []
    for line in Path(args.train).read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line:
            rows.append(json.loads(line))

    print(f"[QLoRA] Loaded {len(rows)} training examples")

    # Format as text for SFTTrainer
    def format_example(row):
        msgs = row.get("messages", [])
        parts = []
        for m in msgs:
            role = m["role"]
            content = m["content"]
            if role == "system":
                parts.append(f"<|system|>\n{content}</s>")
            elif role == "user":
                parts.append(f"<|user|>\n{content}</s>")
            elif role == "assistant":
                parts.append(f"<|assistant|>\n{content}</s>")
        return {"text": "\n".join(parts)}

    dataset = Dataset.from_list([format_example(r) for r in rows])
    print(f"[QLoRA] Dataset ready: {len(dataset)} rows")

    # 4-bit quantization config — tuned for 6GB
    bnb = BitsAndBytesConfig(
        load_in_4bit=True,
        bnb_4bit_compute_dtype=torch.bfloat16,
        bnb_4bit_quant_type="nf4",
        bnb_4bit_use_double_quant=True,
    )

    print("[QLoRA] Loading model in 4-bit...")
    tok = AutoTokenizer.from_pretrained(args.base, trust_remote_code=True)
    if tok.pad_token is None:
        tok.pad_token = tok.eos_token

    model = AutoModelForCausalLM.from_pretrained(
        args.base,
        quantization_config=bnb,
        device_map="auto",
        trust_remote_code=True,
        attn_implementation="eager",
    )
    model = prepare_model_for_kbit_training(model, use_gradient_checkpointing=True)

    # LoRA config — small rank for 6GB headroom
    lora_cfg = LoraConfig(
        r=args.rank,
        lora_alpha=args.alpha,
        lora_dropout=0.05,
        target_modules=["q_proj", "k_proj", "v_proj", "o_proj",
                         "gate_proj", "up_proj", "down_proj"],
        bias="none",
        task_type="CAUSAL_LM",
    )
    model = get_peft_model(model, lora_cfg)
    model.print_trainable_parameters()

    out_dir = Path(args.out)
    out_dir.mkdir(parents=True, exist_ok=True)

    training_args = TrainingArguments(
        output_dir=str(out_dir),
        num_train_epochs=args.epochs,
        per_device_train_batch_size=args.batch,
        gradient_accumulation_steps=args.grad_accum,
        learning_rate=args.lr,
        warmup_ratio=0.05,
        lr_scheduler_type="cosine",
        fp16=False,
        bf16=True,
        logging_steps=10,
        save_strategy="epoch",
        save_total_limit=2,
        report_to="none",
        gradient_checkpointing=True,
        optim="paged_adamw_8bit",
        dataloader_num_workers=0,
    )

    trainer = SFTTrainer(
        model=model,
        tokenizer=tok,
        train_dataset=dataset,
        dataset_text_field="text",
        max_seq_length=args.max_seq,
        args=training_args,
        packing=False,
    )
    return trainer, out_dir


def main():
    ap = argparse.ArgumentParser(description="QLoRA 7B training for HARDCOREPC (6GB VRAM)")
    ap.add_argument("--base",      default="Qwen/Qwen2.5-Coder-7B-Instruct")
    ap.add_argument("--train",     required=True, help="Training JSONL path")
    ap.add_argument("--out",       required=True, help="Output adapter directory")
    ap.add_argument("--epochs",    type=int,   default=3)
    ap.add_argument("--rank",      type=int,   default=8,    help="LoRA rank (8 recommended for 6GB)")
    ap.add_argument("--alpha",     type=int,   default=16)
    ap.add_argument("--batch",     type=int,   default=1)
    ap.add_argument("--grad-accum",type=int,   default=16,   dest="grad_accum")
    ap.add_argument("--max-seq",   type=int,   default=512,  dest="max_seq",
                    help="Max sequence length (512 recommended for 6GB)")
    ap.add_argument("--lr",        type=float, default=2e-4)
    args = ap.parse_args()

    t0 = time.time()
    trainer, out_dir = build_trainer(args)

    print("\n[QLoRA] Training starting...")
    trainer.train()

    print("[QLoRA] Saving adapter...")
    trainer.model.save_pretrained(str(out_dir / "adapter"))
    trainer.tokenizer.save_pretrained(str(out_dir / "adapter"))

    elapsed = round((time.time() - t0) / 60, 1)
    summary = {
        "base_model": args.base,
        "epochs": args.epochs,
        "lora_rank": args.rank,
        "max_seq_length": args.max_seq,
        "minutes": elapsed,
        "finished": datetime.now().isoformat(timespec="seconds"),
        "adapter_dir": str(out_dir / "adapter"),
    }
    (out_dir / "training_summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")

    print(f"\n[QLoRA] Done in {elapsed} min")
    print(f"[QLoRA] Adapter: {out_dir / 'adapter'}")
    print("[QLoRA] Next: copy adapter to main machine and run eval_adapter.py")


if __name__ == "__main__":
    main()
