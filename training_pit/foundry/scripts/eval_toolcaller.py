#!/usr/bin/env python3
"""
Toolcaller Arena — benchmark a PEFT adapter (or base model) against the
held-out eval set, writing live progress so the TheOrc Training Pit UI
can track it in real time.

Usage:
    python training_pit/foundry/scripts/eval_toolcaller.py \
        --adapter training_pit/outputs/foundry_toolcaller_v0_r2/adapter \
        --eval    training_pit/datasets/eval_toolcaller_v0.jsonl \
        --out     training_pit/outputs/foundry_toolcaller_v0_r2/arena

Flags:
    --base-only   Load only the base model (no adapter) — baseline comparison
    --max N       Evaluate first N examples only (smoke tests)

Output:
    <out>/progress.json   — updated every 5 examples during eval
    <out>/results.json    — written on clean completion
    <out>/arena.log       — stdout/stderr (redirected by the UI launcher)
"""
import argparse, json, sys, time
from pathlib import Path

# Windows consoles default to cp1252; the summary bars use box glyphs.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")


# ── Argument parsing ──────────────────────────────────────────────────────────

def parse_args():
    ap = argparse.ArgumentParser(description="Toolcaller Arena benchmark")
    ap.add_argument("--adapter",   required=True, type=Path,
                    help="Path to the PEFT adapter directory (contains adapter_config.json)")
    ap.add_argument("--eval",      required=True, type=Path,
                    help="Path to the eval JSONL file")
    ap.add_argument("--out",       required=True, type=Path,
                    help="Output directory for progress.json and results.json")
    ap.add_argument("--base-only", action="store_true",
                    help="Evaluate the base model without loading the adapter")
    ap.add_argument("--max",       type=int, default=0,
                    help="Limit evaluation to first N examples (0 = all)")
    return ap.parse_args()


# ── Progress / results I/O ────────────────────────────────────────────────────

def write_progress(out: Path, status: str, step: int, total: int, metrics: dict):
    out.mkdir(parents=True, exist_ok=True)
    tmp = out / "progress.tmp"
    tmp.write_text(json.dumps({
        "status":  status,
        "step":    step,
        "total":   total,
        "metrics": metrics,
        "updated": time.strftime("%Y-%m-%d %H:%M:%S"),
    }, indent=2))
    # On Windows os.replace fails with PermissionError if a UI poller holds
    # progress.json open at that instant. Progress is advisory — retry briefly,
    # then skip this update rather than killing a multi-minute eval run.
    for attempt in range(5):
        try:
            tmp.replace(out / "progress.json")
            return
        except PermissionError:
            time.sleep(0.1 * (attempt + 1))
    print("WARN: progress.json locked by a reader; skipping this progress write", flush=True)


# ── JSON extraction from raw model output ─────────────────────────────────────

def extract_json(text: str) -> dict | None:
    text = text.strip()
    try:
        return json.loads(text)
    except Exception:
        pass
    start = text.find("{")
    if start == -1:
        return None
    depth = 0
    for i, ch in enumerate(text[start:], start):
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                try:
                    return json.loads(text[start : i + 1])
                except Exception:
                    break
    return None


# ── Metric computation ────────────────────────────────────────────────────────

def compute_metrics(results: list[dict]) -> dict:
    if not results:
        return {}

    total     = len(results)
    correct   = sum(1 for r in results if r["pred_decision"] == r["exp_decision"])
    valid_j   = sum(1 for r in results if r["valid_json"])

    call_exp  = [r for r in results if r["exp_decision"] == "call"]
    tool_ok   = sum(1 for r in call_exp
                    if r["pred_decision"] == "call" and r["pred_tool"] == r["exp_tool"])
    arg_ok    = sum(1 for r in call_exp
                    if r["pred_decision"] == "call"
                    and r["pred_tool"] == r["exp_tool"]
                    and r["args_match"])

    per_class: dict[str, dict] = {}
    for cls in ("call", "no_tool", "clarify", "unsupported"):
        tp = sum(1 for r in results if r["exp_decision"] == cls and r["pred_decision"] == cls)
        fp = sum(1 for r in results if r["exp_decision"] != cls and r["pred_decision"] == cls)
        fn = sum(1 for r in results if r["exp_decision"] == cls and r["pred_decision"] != cls)
        n  = sum(1 for r in results if r["exp_decision"] == cls)
        prec = tp / (tp + fp) if tp + fp > 0 else 0.0
        rec  = tp / (tp + fn) if tp + fn > 0 else 0.0
        f1   = 2 * prec * rec / (prec + rec) if prec + rec > 0 else 0.0
        per_class[cls] = {
            "precision": round(prec, 4),
            "recall":    round(rec,  4),
            "f1":        round(f1,   4),
            "count":     n,
            "tp":        tp,
        }

    return {
        "decision_accuracy": round(correct / total, 4),
        "json_validity":     round(valid_j / total, 4),
        "tool_precision":    round(tool_ok / len(call_exp), 4) if call_exp else None,
        "arg_exact_match":   round(arg_ok  / len(call_exp), 4) if call_exp else None,
        "total":             total,
        "correct_decision":  correct,
        "per_class":         per_class,
    }


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    args = parse_args()

    # ── Load eval set ─────────────────────────────────────────────────────────
    if not args.eval.exists():
        print(f"ERROR: eval file not found: {args.eval}", file=sys.stderr)
        raise SystemExit(1)

    eval_rows: list[dict] = []
    with args.eval.open(encoding="utf-8") as fh:
        for line in fh:
            line = line.strip()
            if line:
                eval_rows.append(json.loads(line))

    if args.max > 0:
        eval_rows = eval_rows[: args.max]

    total = len(eval_rows)
    print(f"Loaded {total} eval examples from {args.eval}", flush=True)

    write_progress(args.out, "loading_model", 0, total, {})

    # ── Load model ────────────────────────────────────────────────────────────
    import torch
    from transformers import AutoTokenizer, AutoModelForCausalLM

    config_path = args.adapter / "adapter_config.json"
    if not config_path.exists():
        print(f"ERROR: adapter_config.json not found at {config_path}", file=sys.stderr)
        raise SystemExit(1)

    base_model_id = json.loads(config_path.read_text(encoding="utf-8"))["base_model_name_or_path"]
    print(f"Base model: {base_model_id}", flush=True)

    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype  = torch.bfloat16 if torch.cuda.is_available() else torch.float32
    print(f"Device: {device}  dtype: {dtype}", flush=True)

    tokenizer = AutoTokenizer.from_pretrained(base_model_id, trust_remote_code=True)
    base_model = AutoModelForCausalLM.from_pretrained(
        base_model_id,
        torch_dtype=dtype,
        device_map=device,
        trust_remote_code=True,
    )

    if args.base_only:
        model = base_model
        model.eval()
        print("Running BASE MODEL only (no adapter)", flush=True)
    else:
        from peft import PeftModel
        model = PeftModel.from_pretrained(base_model, str(args.adapter))
        model.eval()
        print(f"Loaded adapter from {args.adapter}", flush=True)

    write_progress(args.out, "evaluating", 0, total, {})

    # ── Evaluate ──────────────────────────────────────────────────────────────
    results: list[dict] = []

    for i, row in enumerate(eval_rows):
        msgs = row.get("messages", [])
        if not msgs:
            continue

        # Split prompt from expected response
        if msgs[-1].get("role") == "assistant":
            expected_text = msgs[-1]["content"]
            prompt_msgs   = msgs[:-1]
        else:
            expected_text = ""
            prompt_msgs   = msgs

        # Parse expected
        try:
            expected = json.loads(expected_text) if expected_text else {}
        except Exception:
            expected = {}

        exp_decision = expected.get("decision", "")
        exp_tool     = expected.get("tool",     "")
        exp_args     = expected.get("arguments", {})

        # Tokenize + generate
        try:
            text   = tokenizer.apply_chat_template(
                prompt_msgs, tokenize=False, add_generation_prompt=True
            )
            inputs = tokenizer(text, return_tensors="pt").to(device)

            with torch.no_grad():
                output_ids = model.generate(
                    **inputs,
                    max_new_tokens=256,
                    do_sample=False,
                    temperature=None,
                    top_p=None,
                    pad_token_id=tokenizer.eos_token_id,
                )

            new_tokens = output_ids[0][inputs["input_ids"].shape[1]:]
            response   = tokenizer.decode(new_tokens, skip_special_tokens=True).strip()
        except Exception as exc:
            print(f"  [{i+1}/{total}] generation error: {exc}", flush=True)
            results.append({
                "valid_json":   False,
                "exp_decision": exp_decision,
                "pred_decision": "",
                "exp_tool":      exp_tool,
                "pred_tool":     "",
                "args_match":    False,
            })
            continue

        # Parse prediction
        pred       = extract_json(response)
        valid_json = pred is not None
        pred_decision = (pred.get("decision", "") if pred else "")
        pred_tool     = (pred.get("tool",     "") if pred else "")
        pred_args     = (pred.get("arguments", {}) if pred else {})

        args_match = (
            json.dumps(pred_args, sort_keys=True)
            == json.dumps(exp_args,  sort_keys=True)
        )

        results.append({
            "valid_json":    valid_json,
            "exp_decision":  exp_decision,
            "pred_decision": pred_decision,
            "exp_tool":      exp_tool,
            "pred_tool":     pred_tool,
            "args_match":    args_match,
        })

        # Write progress every 5 steps or on the last example
        if (i + 1) % 5 == 0 or (i + 1) == total:
            metrics = compute_metrics(results)
            write_progress(args.out, "evaluating", i + 1, total, metrics)
            acc = metrics.get("decision_accuracy", 0.0)
            print(f"  [{i+1:>4}/{total}]  decision_acc={acc:.3f}", flush=True)

    # ── Final results ─────────────────────────────────────────────────────────
    final_metrics = compute_metrics(results)
    final = {
        "adapter":   str(args.adapter),
        "eval_path": str(args.eval),
        "base_only": args.base_only,
        "total":     total,
        "metrics":   final_metrics,
        "finished":  time.strftime("%Y-%m-%d %H:%M"),
    }
    (args.out / "results.json").write_text(json.dumps(final, indent=2))
    write_progress(args.out, "done", total, total, final_metrics)

    # ── Print summary ─────────────────────────────────────────────────────────
    print("\n" + "=" * 50)
    print("  ARENA RESULTS")
    print("=" * 50)
    print(f"  Decision accuracy:  {final_metrics['decision_accuracy']:.1%}")
    print(f"  JSON validity:      {final_metrics['json_validity']:.1%}")
    if final_metrics.get("tool_precision") is not None:
        print(f"  Tool precision:     {final_metrics['tool_precision']:.1%}")
    if final_metrics.get("arg_exact_match") is not None:
        print(f"  Arg exact match:    {final_metrics['arg_exact_match']:.1%}")
    print("\n  Per-class F1:")
    for cls, vals in final_metrics.get("per_class", {}).items():
        bar = "█" * int(vals["f1"] * 20)
        print(f"    {cls:<12}  {bar:<20}  F1={vals['f1']:.3f}  ({vals['tp']}/{vals['count']})")
    print("=" * 50)


if __name__ == "__main__":
    main()
