#!/usr/bin/env python3
"""
Refusal Gauntlet evaluator — runs the adversarial refusal set against either
the HF PEFT adapter (training-format artifact) or the DEPLOYED Ollama model
(quantized GGUF — the thing that actually ships), and reports statistically
honest numbers:

  - per-family accuracy with exact one-sided 95% Clopper-Pearson LOWER bounds
    (the defensible claim, not the point estimate)
  - paraphrase-group "flips": groups where some phrasings pass and some fail —
    robustness findings even when aggregate accuracy looks fine
  - failures.jsonl with every miss (request / expected / got) for review and
    for feeding back into the next training round

Usage:
    # deployed artifact (recommended — tests what ships, no VRAM juggling):
    python eval_refusal_gauntlet.py --ollama theorc-toolcaller:qwen25-1.5b \
        --eval training_pit/datasets/refusal_gauntlet_v0.jsonl \
        --out  training_pit/outputs/refusal_gauntlet/theorc-toolcaller

    # training-format adapter (same backend as the Arena benchmark):
    python eval_refusal_gauntlet.py --adapter <dir> --eval <jsonl> --out <dir>

Writes <out>/progress.json (live, atomic+retry), <out>/results.json,
<out>/failures.jsonl.
"""
import argparse
import json
import math
import sys
import time

# Windows consoles default to cp1252; the summary uses box/relation glyphs.
if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
import urllib.request
from collections import defaultdict
from pathlib import Path


# ── Progress I/O (atomic + retry, same contract as eval_toolcaller.py) ────────

def write_progress(out: Path, status: str, step: int, total: int, metrics: dict):
    out.mkdir(parents=True, exist_ok=True)
    tmp = out / "progress.tmp"
    tmp.write_text(json.dumps({
        "status": status, "step": step, "total": total,
        "metrics": metrics, "updated": time.strftime("%Y-%m-%d %H:%M:%S"),
    }, indent=2))
    for attempt in range(5):
        try:
            tmp.replace(out / "progress.json")
            return
        except PermissionError:
            time.sleep(0.1 * (attempt + 1))
    print("WARN: progress.json locked by a reader; skipping this write", flush=True)


# ── Statistics ────────────────────────────────────────────────────────────────

def _log_binom_pmf(k: int, n: int, p: float) -> float:
    if p <= 0.0:
        return 0.0 if k == 0 else -math.inf
    if p >= 1.0:
        return 0.0 if k == n else -math.inf
    return (math.lgamma(n + 1) - math.lgamma(k + 1) - math.lgamma(n - k + 1)
            + k * math.log(p) + (n - k) * math.log(1.0 - p))


def _binom_sf_ge(k: int, n: int, p: float) -> float:
    """P(X >= k) for X ~ Binomial(n, p)."""
    total = 0.0
    for i in range(k, n + 1):
        total += math.exp(_log_binom_pmf(i, n, p))
    return min(1.0, total)


def clopper_pearson_lower(successes: int, n: int, alpha: float = 0.05) -> float:
    """Exact one-sided (1-alpha) LOWER confidence bound on the success rate.
    Solves P(X >= successes | p) = alpha for p via bisection. For zero
    failures this reduces to alpha**(1/n) (the 'rule of three' region)."""
    if n == 0:
        return 0.0
    if successes == 0:
        return 0.0
    if successes == n:
        return alpha ** (1.0 / n)
    lo, hi = 0.0, 1.0
    for _ in range(60):
        mid = (lo + hi) / 2
        if _binom_sf_ge(successes, n, mid) < alpha:
            lo = mid
        else:
            hi = mid
    return lo


# ── Decision extraction (same contract as eval_toolcaller.py) ─────────────────

def extract_json(text: str):
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
                    return json.loads(text[start:i + 1])
                except Exception:
                    break
    return None


# ── Backends ──────────────────────────────────────────────────────────────────

def make_ollama_backend(model: str, host: str):
    def generate(prompt_msgs: list[dict]) -> str:
        body = json.dumps({
            "model": model,
            "messages": prompt_msgs,
            "stream": False,
            "options": {"temperature": 0, "num_predict": 256},
        }).encode("utf-8")
        req = urllib.request.Request(
            f"{host}/api/chat", data=body,
            headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(req, timeout=120) as resp:
            return json.loads(resp.read())["message"]["content"]
    return generate


def make_hf_backend(adapter_dir: Path, base_only: bool):
    import torch
    from transformers import AutoTokenizer, AutoModelForCausalLM

    config_path = adapter_dir / "adapter_config.json"
    base_model_id = json.loads(config_path.read_text(encoding="utf-8"))["base_model_name_or_path"]
    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype = torch.bfloat16 if torch.cuda.is_available() else torch.float32
    print(f"Base: {base_model_id}  device: {device}", flush=True)

    tokenizer = AutoTokenizer.from_pretrained(base_model_id, trust_remote_code=True)
    model = AutoModelForCausalLM.from_pretrained(
        base_model_id, torch_dtype=dtype, device_map=device, trust_remote_code=True)
    if not base_only:
        from peft import PeftModel
        model = PeftModel.from_pretrained(model, str(adapter_dir))
    model.eval()

    def generate(prompt_msgs: list[dict]) -> str:
        text = tokenizer.apply_chat_template(prompt_msgs, tokenize=False, add_generation_prompt=True)
        inputs = tokenizer(text, return_tensors="pt").to(device)
        with torch.no_grad():
            out = model.generate(**inputs, max_new_tokens=256, do_sample=False,
                                 temperature=None, top_p=None,
                                 pad_token_id=tokenizer.eos_token_id)
        return tokenizer.decode(out[0][inputs["input_ids"].shape[1]:], skip_special_tokens=True).strip()
    return generate


# ── Metrics ───────────────────────────────────────────────────────────────────

def compute_metrics(results: list[dict]) -> dict:
    """Two tiers:
    - strict accuracy: predicted decision == expected decision
    - safety rate: did NOT propose a tool call. Every gauntlet case expects a
      refusal-shaped answer (unsupported/clarify/no_tool), so any "call" is a
      fabrication — the failure mode that matters for the nines. Answering
      no_tool where unsupported was expected is a taxonomy miss, not a
      fabrication, and the two tiers keep those separate."""
    if not results:
        return {}
    total = len(results)
    correct = sum(1 for r in results if r["ok"])
    safe = sum(1 for r in results if r["safe"])

    per_family: dict[str, dict] = {}
    for fam in sorted({r["family"] for r in results}):
        rows = [r for r in results if r["family"] == fam]
        ok = sum(1 for r in rows if r["ok"])
        fam_safe = sum(1 for r in rows if r["safe"])
        per_family[fam] = {
            "n": len(rows), "correct": ok,
            "accuracy": round(ok / len(rows), 4),
            "cp95_lower": round(clopper_pearson_lower(ok, len(rows)), 4),
            "safety_rate": round(fam_safe / len(rows), 4),
            "safety_cp95_lower": round(clopper_pearson_lower(fam_safe, len(rows)), 4),
        }

    groups: dict[str, list[bool]] = defaultdict(list)
    for r in results:
        groups[f"{r['family']}/{r['group_id']}"].append(r["ok"])
    complete = {k: v for k, v in groups.items() if len(v) >= 2}
    flips = [k for k, v in complete.items() if any(v) and not all(v)]

    return {
        "accuracy": round(correct / total, 4),
        "cp95_lower": round(clopper_pearson_lower(correct, total), 4),
        "safety_rate": round(safe / total, 4),
        "safety_cp95_lower": round(clopper_pearson_lower(safe, total), 4),
        "fabricated_calls": total - safe,
        "total": total, "correct": correct,
        "per_family": per_family,
        "paraphrase_groups": len(complete),
        "flip_groups": len(flips),
        "flip_examples": sorted(flips)[:10],
    }


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="Refusal Gauntlet evaluator")
    ap.add_argument("--eval", required=True, type=Path)
    ap.add_argument("--out", required=True, type=Path)
    backend = ap.add_mutually_exclusive_group(required=True)
    backend.add_argument("--ollama", metavar="TAG",
                         help="evaluate the deployed Ollama model (recommended)")
    backend.add_argument("--adapter", type=Path,
                         help="evaluate the HF PEFT adapter directory")
    ap.add_argument("--base-only", action="store_true",
                    help="with --adapter: load only the base model")
    ap.add_argument("--host", default="http://localhost:11434")
    ap.add_argument("--max", type=int, default=0)
    ap.add_argument("--workers", type=int, default=4,
                    help="concurrent requests (Ollama backend only; HF runs serially)")
    args = ap.parse_args()

    rows = [json.loads(l) for l in args.eval.open(encoding="utf-8") if l.strip()]
    if args.max > 0:
        rows = rows[: args.max]
    total = len(rows)
    print(f"Loaded {total} gauntlet cases from {args.eval}", flush=True)

    write_progress(args.out, "loading_model", 0, total, {})
    generate = (make_ollama_backend(args.ollama, args.host) if args.ollama
                else make_hf_backend(args.adapter, args.base_only))
    model_id = args.ollama or str(args.adapter) + (" (base-only)" if args.base_only else "")

    write_progress(args.out, "evaluating", 0, total, {})
    results: list[dict] = []
    failures_path = args.out / "failures.jsonl"
    args.out.mkdir(parents=True, exist_ok=True)

    import threading
    from concurrent.futures import ThreadPoolExecutor

    lock = threading.Lock()
    done = 0
    workers = max(1, args.workers) if args.ollama else 1  # HF model is not thread-safe

    def eval_one(row: dict) -> None:
        nonlocal done
        prompt_msgs = [m for m in row["messages"] if m["role"] != "assistant"]
        expected = json.loads(row["messages"][-1]["content"])

        try:
            response = generate(prompt_msgs)
        except Exception as exc:
            print(f"  generation error: {exc}", flush=True)
            response = ""

        pred = extract_json(response)
        pred_decision = (pred or {}).get("decision", "")
        ok = pred_decision == expected["decision"]
        # Unparseable output also counts as unsafe: the repair lane would
        # discard it, but the model failed to produce a valid refusal.
        safe = pred is not None and pred_decision != "call"

        with lock:
            done += 1
            results.append({
                "ok": ok, "safe": safe,
                "family": row["family"], "group_id": row["group_id"],
            })
            if not ok:
                fail_fh.write(json.dumps({
                    "example_id": row["example_id"], "family": row["family"],
                    "group_id": row["group_id"], "phrasing": row["phrasing"],
                    "role": row["role"],
                    "request": prompt_msgs[-1]["content"],
                    "expected": expected, "got": response[:400],
                }, ensure_ascii=False) + "\n")
                fail_fh.flush()
            if done % 20 == 0 or done == total:
                m = compute_metrics(results)
                write_progress(args.out, "evaluating", done, total, m)
                print(f"  [{done:>5}/{total}]  acc={m['accuracy']:.4f}  "
                      f"safety={m['safety_rate']:.4f}  flips={m['flip_groups']}", flush=True)

    with failures_path.open("w", encoding="utf-8", newline="\n") as fail_fh:
        if workers == 1:
            for row in rows:
                eval_one(row)
        else:
            print(f"Running with {workers} concurrent workers", flush=True)
            with ThreadPoolExecutor(max_workers=workers) as pool:
                list(pool.map(eval_one, rows))

    final = compute_metrics(results)
    (args.out / "results.json").write_text(json.dumps({
        "model": model_id,
        "eval_path": str(args.eval),
        "total": total,
        "metrics": final,
        "finished": time.strftime("%Y-%m-%d %H:%M"),
    }, indent=2))
    write_progress(args.out, "done", total, total, final)

    print("\n" + "=" * 60)
    print("  REFUSAL GAUNTLET RESULTS —", model_id)
    print("=" * 60)
    print(f"  Strict accuracy:  {final['accuracy']:.2%}  ({final['correct']}/{final['total']})  cp95 >= {final['cp95_lower']:.2%}")
    print(f"  SAFETY rate:      {final['safety_rate']:.2%}  ({final['fabricated_calls']} fabricated calls)  cp95 >= {final['safety_cp95_lower']:.2%}")
    print(f"  Paraphrase flips: {final['flip_groups']} of {final['paraphrase_groups']} groups")
    print("\n  Per family (strict / safety):")
    for fam, v in final["per_family"].items():
        bar = "█" * int(v["accuracy"] * 20)
        print(f"    {fam:<18} {bar:<20} {v['accuracy']:.2%} / {v['safety_rate']:.2%}  (n={v['n']})")
    print(f"\n  Failures: {failures_path}")
    print("=" * 60)


if __name__ == "__main__":
    main()
