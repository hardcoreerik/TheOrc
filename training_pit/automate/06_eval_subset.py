#!/usr/bin/env python3
"""Quick A/B eval on a subset using the local 7B model as a judge.

On 6GB VRAM: uses a smaller model to score plan quality on a subset
of eval examples. Not an authoritative A/B (different model), but gives
fast directional signal when the main machine is unavailable.

Usage:
  python 06_eval_subset.py --adapter path/to/adapter --eval eval_v1.jsonl --limit 20
"""
import argparse, json, re, sys, time
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")

VALID_ROLES = {"RESEARCHER", "CODER", "UIDEVELOPER", "TESTER"}
WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate|author|compose)\b", re.I)
FILE_RE     = re.compile(r"[\w./\\-]+\.(cs|xaml|py|ps1|psm1|csproj|json|md|ts|js)\b", re.I)

PLATFORM_SIGNALS = {
    "python": {".py"},
    "csharp": {".cs", ".xaml", ".csproj"},
    "powershell": {".ps1", ".psm1"},
    "typescript": {".ts", ".tsx"},
    "javascript": {".js", ".jsx"},
}


def detect_platform(goal):
    goal_lower = goal.lower()
    if any(k in goal_lower for k in ("c#", "csharp", "wpf", ".net", "xaml", "winforms")):
        return "csharp"
    if "python" in goal_lower:
        return "python"
    if "powershell" in goal_lower or " ps1" in goal_lower:
        return "powershell"
    if "typescript" in goal_lower:
        return "typescript"
    return None


def score_plan(text, goal=""):
    s = {"valid_json": 0, "task_count_ok": 0, "roles_valid": 0,
         "files_named": 0, "no_tester_write": 0, "platform_coherent": 0}
    m = re.search(r"\{.*\}", text, re.S)
    if not m:
        return s
    try:
        plan = json.loads(m.group(0))
    except json.JSONDecodeError:
        return s
    s["valid_json"] = 1
    tasks = plan.get("tasks") if isinstance(plan, dict) else None
    if not isinstance(tasks, list) or not tasks:
        return s
    if 2 <= len(tasks) <= 4:
        s["task_count_ok"] = 1

    platform = detect_platform(goal)
    roles_ok = files_ok = tester_ok = platform_ok = True

    for t in tasks:
        if not isinstance(t, dict):
            roles_ok = False
            continue
        role = str(t.get("role", "")).upper().strip()
        if role not in VALID_ROLES:
            roles_ok = False
        blob = f"{t.get('title','')} {t.get('description','')}"
        if role in ("CODER", "UIDEVELOPER"):
            if not FILE_RE.search(blob):
                files_ok = False
            elif platform and platform in PLATFORM_SIGNALS:
                expected_exts = PLATFORM_SIGNALS[platform]
                found_files = FILE_RE.findall(blob)
                file_exts = {Path(f).suffix.lower() for f in found_files}
                if not any(ext in expected_exts for ext in file_exts):
                    platform_ok = False
        if role == "TESTER" and WRITE_VERBS.search(blob):
            tester_ok = False

    s["roles_valid"]      = int(roles_ok)
    s["files_named"]      = int(files_ok)
    s["no_tester_write"]  = int(tester_ok)
    s["platform_coherent"] = int(platform_ok)
    return s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--adapter", required=True, help="Path to fine-tuned adapter")
    ap.add_argument("--eval",    required=True, help="Eval JSONL path")
    ap.add_argument("--base",    default="Qwen/Qwen2.5-Coder-7B-Instruct",
                    help="Base model for comparison (7B for local)")
    ap.add_argument("--limit",   type=int, default=20)
    ap.add_argument("--max-new", type=int, default=512, dest="max_new")
    ap.add_argument("--out",     default="eval_subset_results.json")
    args = ap.parse_args()

    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
    from peft import PeftModel

    rows = [json.loads(l) for l in Path(args.eval).read_text(encoding="utf-8").splitlines() if l.strip()]
    rows = rows[:args.limit]
    print(f"Eval examples: {len(rows)}")

    bnb = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_compute_dtype=torch.bfloat16,
                             bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True)
    print("Loading base (4-bit)...")
    tok = AutoTokenizer.from_pretrained(args.base, trust_remote_code=True)
    model = AutoModelForCausalLM.from_pretrained(
        args.base, quantization_config=bnb, device_map="auto",
        trust_remote_code=True, attn_implementation="eager")
    print("Attaching adapter...")
    model = PeftModel.from_pretrained(model, args.adapter)
    model.eval()

    def generate(messages):
        prompt_msgs = [m for m in messages if m["role"] in ("system", "user")]
        enc = tok.apply_chat_template(prompt_msgs, add_generation_prompt=True,
                                       return_dict=True, return_tensors="pt").to(model.device)
        with torch.no_grad():
            out = model.generate(**enc, max_new_tokens=args.max_new,
                                 do_sample=False, temperature=None, top_p=None,
                                 pad_token_id=tok.eos_token_id)
        return tok.decode(out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True)

    dims = ["valid_json", "task_count_ok", "roles_valid", "files_named",
            "no_tester_write", "platform_coherent"]
    agg = {"base": {d: 0 for d in dims}, "ft": {d: 0 for d in dims}}
    t0 = time.time()

    for i, row in enumerate(rows, 1):
        msgs = row["messages"]
        goal = next((m["content"] for m in msgs if m["role"] == "user"), "")

        with model.disable_adapter():
            base_txt = generate(msgs)
        ft_txt = generate(msgs)

        bs = score_plan(base_txt, goal)
        fs = score_plan(ft_txt, goal)
        for d in dims:
            agg["base"][d] += bs[d]
            agg["ft"][d]   += fs[d]

        if i % 5 == 0 or i == len(rows):
            el = (time.time() - t0) / 60
            print(f"[{i}/{len(rows)}] base={sum(agg['base'].values())} "
                  f"ft={sum(agg['ft'].values())}  ({el:.1f} min)")

    n = len(rows)
    result = {
        "eval_examples": n,
        "adapter": args.adapter,
        "dimensions": {d: {"base": agg["base"][d], "ft": agg["ft"][d], "of": n} for d in dims},
        "base_pass_pct": round(100 * sum(agg["base"].values()) / (n * len(dims)), 1),
        "ft_pass_pct":   round(100 * sum(agg["ft"].values())   / (n * len(dims)), 1),
        "note": "platform_coherent dimension added — catches language/framework hallucinations",
        "minutes": round((time.time() - t0) / 60, 1),
    }
    Path(args.out).write_text(json.dumps(result, indent=2), encoding="utf-8")
    print(f"\nBASE  {result['base_pass_pct']}%")
    print(f"FT    {result['ft_pass_pct']}%")
    print(f"\nPlatform coherence:  base {agg['base']['platform_coherent']}/{n}  ft {agg['ft']['platform_coherent']}/{n}")
    print(f"Saved -> {args.out}")


if __name__ == "__main__":
    main()
