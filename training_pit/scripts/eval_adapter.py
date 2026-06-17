#!/usr/bin/env python3
"""A/B evaluation: base boss vs ORC ACADEMY fine-tuned adapter.

Runs both models against the held-out eval set (prompts neither version
trained on), scores every generated plan with deterministic rubric checks,
and reports hard numbers. One base load; the adapter is toggled on/off per
prompt (PeftModel.disable_adapter) so it's a true apples-to-apples A/B —
identical prompt, identical decoding, only the weights differ.

  python training_pit/scripts/eval_adapter.py
  python training_pit/scripts/eval_adapter.py --limit 20   # quick smoke
"""
import argparse, gc, json, os, re, sys, time
from pathlib import Path

# Force UTF-8 stdout so cp1252-piped Windows shells don't crash on any character.
sys.stdout.reconfigure(encoding="utf-8", errors="replace", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8", errors="replace")
os.environ["PYTHONIOENCODING"] = "utf-8"


WRITE_VERBS = re.compile(r"\b(create|write|implement|build|generate|author|compose)\b", re.I)
FILE_RE = re.compile(r"[\w./\\-]+\.(cs|xaml|py|ps1|psm1|csproj|json|md|ts|js)\b", re.I)

# Mirrors SwarmSession.ParseBossPlan alias map.
# Any non-empty role string is valid — the runtime resolves unknowns to Coder.
# Roles that alias to TESTER (no write_file lane):
_TESTER_ROLES  = {"TESTER", "QA", "QUALITY_ASSURANCE"}
# Roles that alias to UIDEVELOPER:
_UI_ROLES      = {"UIDEVELOPER", "FRONTEND_DEVELOPER", "FRONTEND", "UI"}
# Roles that alias to RESEARCHER (no write_file lane):
_RESEARCH_ROLES = {"RESEARCHER", "ARCHITECT", "PLANNER", "REVIEWER", "ANALYST"}
# Everything else (CODER, BACKEND_DEVELOPER, BACKEND, DOCS, DEVOPS, SECURITY,
# ML_ENGINEER, DATA_ENGINEER, RELEASE_MANAGER, …) routes to Coder.
_CODER_ROLES   = {"CODER"}  # explicit name; catch-all handles the rest


def _resolve_lane(role: str) -> str:
    """Map any role string to its execution lane, matching ParseBossPlan."""
    if role in _TESTER_ROLES:   return "TESTER"
    if role in _UI_ROLES:       return "UIDEVELOPER"
    if role in _RESEARCH_ROLES: return "RESEARCHER"
    return "CODER"  # catch-all — matches the `_ => SwarmWorkerRole.Coder` branch


def score_plan(text: str) -> dict:
    """Deterministic rubric over a generated plan. Mirrors the pipeline's bar."""
    s = {"valid_json": 0, "task_count_ok": 0, "roles_valid": 0,
         "files_named": 0, "no_tester_write": 0}
    # Pull the first JSON object out of the model's output.
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

    roles_ok, files_ok, tester_ok = True, True, True
    for t in tasks:
        if not isinstance(t, dict):
            roles_ok = False; continue
        role = str(t.get("role", "")).upper().strip()
        # Any non-empty role string is valid — runtime accepts all via alias map.
        if not role:
            roles_ok = False
        lane = _resolve_lane(role)
        blob = f"{t.get('title','')} {t.get('description','')}"
        # Coder-lane tasks (including aliased roles) must name an output file.
        if lane == "CODER" and not FILE_RE.search(blob):
            files_ok = False
        # UIdev tasks must also name a file.
        if lane == "UIDEVELOPER" and not FILE_RE.search(blob):
            files_ok = False
        # Tester-lane tasks must not be assigned implementation/write work.
        if lane == "TESTER" and WRITE_VERBS.search(blob):
            tester_ok = False
    s["roles_valid"]     = int(roles_ok)
    s["files_named"]     = int(files_ok)
    s["no_tester_write"] = int(tester_ok)
    return s


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--base", default="google/gemma-4-12b-it")
    ap.add_argument("--adapter", default="training_pit/outputs/lora_v2/adapter")
    ap.add_argument("--eval", default="training_pit/datasets/eval_v2gold.jsonl")
    ap.add_argument("--out", default="training_pit/outputs/lora_v2/ab_eval.json")
    ap.add_argument("--limit", type=int, default=0)
    ap.add_argument("--max-new", type=int, default=1024)
    args = ap.parse_args()

    import torch
    from transformers import AutoModelForCausalLM, AutoTokenizer, BitsAndBytesConfig
    from peft import PeftModel

    rows = [json.loads(l) for l in Path(args.eval).read_text(encoding="utf-8").splitlines() if l.strip()]
    if args.limit:
        rows = rows[: args.limit]
    print(f"eval examples: {len(rows)}")

    bnb = BitsAndBytesConfig(load_in_4bit=True, bnb_4bit_compute_dtype=torch.bfloat16,
                             bnb_4bit_quant_type="nf4", bnb_4bit_use_double_quant=True)
    print("loading base (4-bit)…")
    tok = AutoTokenizer.from_pretrained(args.base)
    model = AutoModelForCausalLM.from_pretrained(
        args.base, quantization_config=bnb, device_map="auto",
        dtype=torch.bfloat16, attn_implementation="eager")
    print("attaching adapter…")
    model = PeftModel.from_pretrained(model, args.adapter)
    model.eval()

    def generate(messages):
        # Use only system+user (drop the gold assistant turn).
        prompt_msgs = [m for m in messages if m["role"] in ("system", "user")]
        enc = tok.apply_chat_template(prompt_msgs, add_generation_prompt=True,
                                      return_dict=True, return_tensors="pt").to(model.device)
        with torch.no_grad():
            out = model.generate(**enc, max_new_tokens=args.max_new,
                                 do_sample=False, temperature=None, top_p=None,
                                 pad_token_id=tok.eos_token_id)
        return tok.decode(out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True)

    dims = ["valid_json", "task_count_ok", "roles_valid", "files_named", "no_tester_write"]
    agg = {"base": {d: 0 for d in dims}, "ft": {d: 0 for d in dims}}
    per_example = []
    t0 = time.time()

    for i, row in enumerate(rows, 1):
        msgs = row["messages"]
        with model.disable_adapter():          # ← base behavior
            base_txt = generate(msgs)
        ft_txt = generate(msgs)                 # ← fine-tuned behavior
        bs, fs = score_plan(base_txt), score_plan(ft_txt)
        for d in dims:
            agg["base"][d] += bs[d]; agg["ft"][d] += fs[d]
        per_example.append({"i": i,
                            "base_total": sum(bs.values()), "ft_total": sum(fs.values())})
        if i % 5 == 0 or i == len(rows):
            el = (time.time() - t0) / 60
            print(f"[{i}/{len(rows)}] base_pass={sum(agg['base'].values())} "
                  f"ft_pass={sum(agg['ft'].values())}  ({el:.1f} min)")

    n = len(rows)
    summary = {
        "eval_examples": n,
        "base_model": args.base,
        "adapter": args.adapter,
        "dimensions": {d: {"base": agg["base"][d], "ft": agg["ft"][d], "of": n} for d in dims},
        "base_pass_pct":  round(100 * sum(agg["base"].values()) / (n * len(dims)), 1),
        "ft_pass_pct":    round(100 * sum(agg["ft"].values())   / (n * len(dims)), 1),
        "base_perfect":   sum(1 for e in per_example if e["base_total"] == len(dims)),
        "ft_perfect":     sum(1 for e in per_example if e["ft_total"]   == len(dims)),
        "minutes": round((time.time() - t0) / 60, 1),
        "finished": time.strftime("%Y-%m-%d %H:%M"),
    }
    Path(args.out).write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print("\n" + json.dumps(summary["dimensions"], indent=2))
    print(f"\nBASE  overall {summary['base_pass_pct']}%  · perfect plans {summary['base_perfect']}/{n}")
    print(f"FT    overall {summary['ft_pass_pct']}%  · perfect plans {summary['ft_perfect']}/{n}")
    print(f"saved -> {args.out}")


if __name__ == "__main__":
    main()
