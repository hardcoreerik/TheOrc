#!/usr/bin/env python3
"""ORC ACADEMY Training Pit — Pipeline Manager.

Orchestrates the full collect → review → train → eval → deploy lifecycle.
Every knob lives in training_pit/configs/pit_config.json.

State:  training_pit/outputs/pit_state.json   (written each step — crash-safe)
Signal: training_pit/outputs/pit_signal.txt   (write "stop" or "pause" to control)

Usage:
  python training_pit/scripts/pit_manager.py start
  python training_pit/scripts/pit_manager.py start --from train
  python training_pit/scripts/pit_manager.py stop
  python training_pit/scripts/pit_manager.py pause
  python training_pit/scripts/pit_manager.py resume
  python training_pit/scripts/pit_manager.py status
  python training_pit/scripts/pit_manager.py monitor
  python training_pit/scripts/pit_manager.py config
"""
import argparse, json, os, subprocess, sys, time
from datetime import datetime
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8", errors="replace")
sys.stderr.reconfigure(encoding="utf-8", errors="replace")

ROOT       = Path(__file__).resolve().parents[2]
CONFIG_F   = ROOT / "training_pit" / "configs" / "pit_config.json"
STATE_F    = ROOT / "training_pit" / "outputs" / "pit_state.json"
SIGNAL_F   = ROOT / "training_pit" / "outputs" / "pit_signal.txt"
SCRIPTS    = ROOT / "training_pit" / "scripts"
STOP_FLAG  = ROOT / ".orc" / "swarm" / "HARVEST_STOP"

ALL_PHASES = ["collect", "review", "train", "eval", "deploy"]


def load_config():
    return json.loads(CONFIG_F.read_text(encoding="utf-8"))


def load_state():
    if STATE_F.exists():
        return json.loads(STATE_F.read_text(encoding="utf-8"))
    return {
        "status": "idle",
        "current_phase": None,
        "completed_phases": [],
        "started": None,
        "updated": None,
        "adapter_name": None,
        "phase_results": {},
        "errors": [],
    }


def save_state(state):
    STATE_F.parent.mkdir(parents=True, exist_ok=True)
    state["updated"] = datetime.now().isoformat(timespec="seconds")
    STATE_F.write_text(json.dumps(state, indent=2), encoding="utf-8")


def check_signal():
    """Returns 'stop', 'pause', or None."""
    if SIGNAL_F.exists():
        sig = SIGNAL_F.read_text().strip().lower()
        if sig in ("stop", "pause"):
            return sig
    return None


def clear_signal():
    if SIGNAL_F.exists():
        SIGNAL_F.unlink()


def send_signal(sig):
    SIGNAL_F.parent.mkdir(parents=True, exist_ok=True)
    SIGNAL_F.write_text(sig)


def wait_on_pause(state):
    print("[PIT] Paused. Write 'resume' to pit_signal.txt or delete it to continue.")
    while True:
        time.sleep(5)
        sig = check_signal()
        if sig == "stop":
            return "stop"
        if sig != "pause":
            clear_signal()
            print("[PIT] Resuming.")
            return "resume"


def run_script(cmd, cwd=None):
    """Run a subprocess, streaming output. Returns exit code."""
    print(f"[PIT] Running: {' '.join(str(c) for c in cmd)}")
    result = subprocess.run(
        [str(c) for c in cmd],
        cwd=str(cwd or ROOT),
        text=True, encoding="utf-8", errors="replace",
    )
    return result.returncode


def run_ps(script_path, args=None, cwd=None):
    cmd = ["pwsh", "-ExecutionPolicy", "Bypass", "-File", str(script_path)]
    if args:
        cmd.extend(args)
    return run_script(cmd, cwd)


# ── Phases ────────────────────────────────────────────────────────────────────

def phase_collect(cfg, state):
    c = cfg["collection"]
    ws = cfg["pipeline"]["workspace"]
    prefix = f"PIT{datetime.now().strftime('%y%m%d_%H%M')}"

    print(f"\n[COLLECT] Generating up to {c['max_goals_per_run']} goals (batch size {c['goals_per_batch']})…")
    goals_file = ROOT / "training_pit" / f"batch_{prefix}_goals.psv"

    rc = run_script([
        sys.executable, SCRIPTS / "generate_goals.py",
        "--count", str(c["goals_per_batch"]),
        "--model", c["gen_model"],
        "--workspace", ws,
        "--prefix", prefix,
    ])
    if rc != 0:
        raise RuntimeError(f"generate_goals.py exited {rc}")

    if not goals_file.exists():
        # find the file that was actually created
        candidates = sorted(ROOT.glob(f"training_pit/batch_{prefix}*_goals.psv"))
        if candidates:
            goals_file = candidates[-1]
        else:
            raise FileNotFoundError("No goals PSV found after generate_goals.py")

    print(f"[COLLECT] Farming {goals_file.name} with swarmcli --plan-only…")
    done_file = goals_file.with_name(goals_file.stem.replace("_goals", "_done") + ".csv")
    rc = run_ps(SCRIPTS / "farm_batch.ps1", [
        "-GoalsFile", str(goals_file),
        "-DoneFile", str(done_file),
        "-TimeoutSec", str(c["farm_timeout_sec"]),
    ] + (["-PlanOnly"] if c["farm_plan_only"] else []))
    if rc != 0:
        print(f"[COLLECT] farm_batch.ps1 exited {rc} (non-fatal, some goals may have failed)")

    state["phase_results"]["collect"] = {
        "goals_file": str(goals_file),
        "done_file": str(done_file),
        "prefix": prefix,
    }


def phase_review(cfg, state):
    r = cfg["review"]
    ws = cfg["pipeline"]["workspace"]
    collect = state["phase_results"].get("collect", {})
    goals_file = collect.get("goals_file")

    if r["auto_prescreen"] and goals_file:
        print("\n[REVIEW] Pre-screening captures for mechanical defects…")
        run_script([sys.executable, SCRIPTS / "prescreen_captures.py",
                    "--goals", goals_file, "--apply"])

    if r["auto_judge"] and goals_file:
        print("[REVIEW] Running fabrication-risk triage…")
        triage_out = Path(goals_file).with_name(
            Path(goals_file).stem.replace("_goals", "_triage") + ".tsv"
        )
        run_script([sys.executable, SCRIPTS / "judge_captures.py",
                    "--goals", goals_file,
                    "--model", cfg["harvest"]["judge_model"],
                    "--out", str(triage_out)])

    # Always show status after automated passes
    run_script([sys.executable, SCRIPTS / "review_captures.py", "--status"])

    if r["require_human_approval"]:
        print("\n[REVIEW] Human approval required before training.")
        print("  Run:  python training_pit/scripts/review_captures.py --inspect <file>")
        print("        python training_pit/scripts/review_captures.py --approve  <file>")
        print("        python training_pit/scripts/review_captures.py --export-train")
        print("\n  Then rerun:  python training_pit/scripts/pit_manager.py start --from train")
        state["status"] = "waiting_review"
        save_state(state)
        sys.exit(0)


def phase_train(cfg, state):
    t = cfg["training"]
    adapter = cfg["pipeline"]["adapter_name"]
    out_dir = ROOT / "training_pit" / "outputs" / adapter

    print("\n[TRAIN] Running Phase 3 preflight…")
    rc = run_script([sys.executable, SCRIPTS / "phase3_preflight.py"])
    if rc != 0:
        raise RuntimeError("Phase 3 preflight failed — training blocked. Fix issues above.")

    out_dir.mkdir(parents=True, exist_ok=True)
    print(f"[TRAIN] Starting QLoRA training → {out_dir}")

    train_script = SCRIPTS / "forge_dataset.py"
    if not train_script.exists():
        # fallback: look for any trainer script
        candidates = list(SCRIPTS.glob("*train*.py")) + list(SCRIPTS.glob("*forge*.py"))
        if candidates:
            train_script = candidates[0]
        else:
            raise FileNotFoundError("No training script found in training_pit/scripts/")

    rc = run_script([sys.executable, str(train_script),
        "--base", t["base_model"],
        "--out", str(out_dir / "adapter"),
        "--train", str(ROOT / "training_pit" / "datasets" / "train_v1.jsonl"),
        "--eval",  str(ROOT / "training_pit" / "datasets" / "eval_v1.jsonl"),
        "--epochs", str(t["epochs"]),
        "--rank", str(t["lora_rank"]),
        "--alpha", str(t["lora_alpha"]),
        "--batch", str(t["batch_size"]),
        "--grad-accum", str(t["gradient_accumulation_steps"]),
        "--max-seq", str(t["max_seq_length"]),
        "--lr", str(t["learning_rate"]),
    ])
    if rc != 0:
        raise RuntimeError(f"Training exited {rc}")

    state["phase_results"]["train"] = {"adapter_dir": str(out_dir / "adapter")}


def phase_eval(cfg, state):
    e = cfg["eval"]
    adapter = cfg["pipeline"]["adapter_name"]
    adapter_dir = ROOT / "training_pit" / "outputs" / adapter / "adapter"
    out_file = ROOT / "training_pit" / "outputs" / adapter / "ab_eval.json"
    eval_set = ROOT / "training_pit" / "datasets" / "eval_v1.jsonl"

    print(f"\n[EVAL] Running A/B eval — base vs {adapter}…")
    rc = run_script([sys.executable, SCRIPTS / "eval_adapter.py",
        "--adapter", str(adapter_dir),
        "--eval", str(eval_set),
        "--out", str(out_file),
        "--max-new", str(e["max_new_tokens"]),
    ])
    if rc != 0:
        raise RuntimeError(f"eval_adapter.py exited {rc}")

    results = json.loads(out_file.read_text(encoding="utf-8"))
    ft_pct = results["ft_pass_pct"]
    n = results["eval_examples"]
    perfect = results["ft_perfect"]
    perfect_pct = round(100 * perfect / n, 1)

    print(f"\n[EVAL] Results: FT {ft_pct}% overall | {perfect}/{n} perfect ({perfect_pct}%)")
    state["phase_results"]["eval"] = {
        "ft_pass_pct": ft_pct,
        "ft_perfect": perfect,
        "ft_perfect_pct": perfect_pct,
        "result_file": str(out_file),
    }

    min_pct = e["min_ft_pass_pct_to_deploy"]
    min_perfect = e["min_perfect_plan_pct_to_deploy"]
    if ft_pct < min_pct or perfect_pct < min_perfect:
        print(f"[EVAL] BLOCKED — thresholds not met (need {min_pct}% overall, {min_perfect}% perfect)")
        state["status"] = "eval_blocked"
        save_state(state)
        sys.exit(1)


def phase_deploy(cfg, state):
    d = cfg["deployment"]
    adapter = cfg["pipeline"]["adapter_name"]
    adapter_dir = ROOT / "training_pit" / "outputs" / adapter / "adapter"

    if d["require_manual_approval"]:
        print("\n[DEPLOY] Manual approval required.")
        print(f"  Adapter path: {adapter_dir}")
        print(f"  Target tag:   {d['ollama_model_tag']}")
        print(f"  Eval results: {state['phase_results'].get('eval', {}).get('result_file')}")
        print("\n  If satisfied, run:")
        print(f"    python training_pit/scripts/pit_manager.py deploy-confirm")
        state["status"] = "waiting_deploy_approval"
        save_state(state)
        sys.exit(0)

    _do_deploy(cfg, state)


def _do_deploy(cfg, state):
    d = cfg["deployment"]
    adapter = cfg["pipeline"]["adapter_name"]
    adapter_dir = ROOT / "training_pit" / "outputs" / adapter / "adapter"
    tag = d["ollama_model_tag"]

    modelfile = adapter_dir.parent / "Modelfile"
    modelfile.write_text(
        f"FROM {cfg['training']['base_model']}\n"
        f"ADAPTER {adapter_dir}\n",
        encoding="utf-8"
    )

    print(f"\n[DEPLOY] Creating Ollama model {tag}…")
    rc = run_script(["ollama", "create", tag, "-f", str(modelfile)])
    if rc != 0:
        raise RuntimeError(f"ollama create exited {rc}")

    registry_f = ROOT / "training_pit" / "adapters" / "registry.json"
    if registry_f.exists():
        registry = json.loads(registry_f.read_text(encoding="utf-8"))
        registry[adapter] = {
            "status": "deployed",
            "ollama_tag": tag,
            "adapter_dir": str(adapter_dir),
            "deployed": datetime.now().isoformat(timespec="seconds"),
            "eval_results": state["phase_results"].get("eval", {}),
        }
        registry_f.write_text(json.dumps(registry, indent=2), encoding="utf-8")

    print(f"[DEPLOY] Done. Model '{tag}' is live in Ollama.")
    state["phase_results"]["deploy"] = {"ollama_tag": tag}


# ── CLI Commands ───────────────────────────────────────────────────────────────

def cmd_start(args):
    cfg = load_config()
    state = load_state()
    clear_signal()

    from_phase = args.from_phase or state.get("current_phase") or ALL_PHASES[0]
    phases = ALL_PHASES[ALL_PHASES.index(from_phase):]

    state["status"] = "running"
    state["adapter_name"] = cfg["pipeline"]["adapter_name"]
    if not state["started"]:
        state["started"] = datetime.now().isoformat(timespec="seconds")
    save_state(state)

    print(f"[PIT] Starting pipeline: {' → '.join(phases)}")
    print(f"[PIT] Adapter: {cfg['pipeline']['adapter_name']}")
    print(f"[PIT] Config:  {CONFIG_F}")

    for phase in phases:
        sig = check_signal()
        if sig == "stop":
            print("[PIT] Stop signal received.")
            state["status"] = "stopped"
            save_state(state)
            return
        if sig == "pause":
            result = wait_on_pause(state)
            if result == "stop":
                state["status"] = "stopped"
                save_state(state)
                return

        state["current_phase"] = phase
        state["status"] = f"running:{phase}"
        save_state(state)

        print(f"\n{'='*60}")
        print(f"  PHASE: {phase.upper()}")
        print(f"{'='*60}")

        try:
            phase_fn = {
                "collect": phase_collect,
                "review":  phase_review,
                "train":   phase_train,
                "eval":    phase_eval,
                "deploy":  phase_deploy,
            }[phase]
            phase_fn(cfg, state)
        except SystemExit:
            raise
        except Exception as exc:
            state["status"] = f"error:{phase}"
            state["errors"].append({"phase": phase, "error": str(exc),
                                    "time": datetime.now().isoformat()})
            save_state(state)
            print(f"[PIT] ERROR in {phase}: {exc}")
            sys.exit(1)

        state["completed_phases"].append(phase)
        state["status"] = f"done:{phase}"
        save_state(state)

    state["status"] = "complete"
    state["current_phase"] = None
    save_state(state)
    print("\n[PIT] Pipeline complete.")


def cmd_stop(_):
    send_signal("stop")
    print("[PIT] Stop signal sent. Harvest will halt at next checkpoint.")
    if STOP_FLAG.parent.exists():
        STOP_FLAG.touch()
        print(f"[PIT] Also planted HARVEST_STOP at {STOP_FLAG}")


def cmd_pause(_):
    send_signal("pause")
    print("[PIT] Pause signal sent.")


def cmd_resume(_):
    clear_signal()
    print("[PIT] Cleared signal — pipeline will resume at next checkpoint.")


def cmd_status(_):
    state = load_state()
    cfg = load_config()
    print(f"\nORC ACADEMY Training Pit — Status")
    print(f"  Status:    {state.get('status', 'idle')}")
    print(f"  Phase:     {state.get('current_phase') or '—'}")
    print(f"  Adapter:   {cfg['pipeline']['adapter_name']}")
    print(f"  Started:   {state.get('started') or '—'}")
    print(f"  Updated:   {state.get('updated') or '—'}")
    completed = state.get("completed_phases", [])
    print(f"  Completed: {', '.join(completed) if completed else '—'}")
    if state.get("phase_results", {}).get("eval"):
        ev = state["phase_results"]["eval"]
        print(f"  Eval:      {ev.get('ft_pass_pct')}% pass | {ev.get('ft_perfect_pct')}% perfect")
    if state.get("errors"):
        print(f"  Errors:    {len(state['errors'])} (see pit_state.json)")
    sig = check_signal()
    if sig:
        print(f"  Signal:    {sig} (pending)")


def cmd_monitor(_):
    print("[PIT] Live monitor — Ctrl+C to exit\n")
    try:
        while True:
            state = load_state()
            cfg = load_config()
            os.system("cls" if os.name == "nt" else "clear")
            print(f"ORC ACADEMY Training Pit  [{datetime.now().strftime('%H:%M:%S')}]")
            print(f"{'─'*50}")
            print(f"  Status:  {state.get('status', 'idle')}")
            print(f"  Phase:   {state.get('current_phase') or '—'}")
            print(f"  Adapter: {cfg['pipeline']['adapter_name']}")
            completed = state.get("completed_phases", [])
            for ph in ALL_PHASES:
                mark = "[x]" if ph in completed else "[ ]"
                cur = " <--" if ph == state.get("current_phase") else ""
                print(f"    {mark} {ph}{cur}")
            if state.get("phase_results", {}).get("eval"):
                ev = state["phase_results"]["eval"]
                print(f"\n  Eval: FT {ev.get('ft_pass_pct')}% | Perfect {ev.get('ft_perfect_pct')}%")
            if state.get("errors"):
                print(f"\n  Errors: {len(state['errors'])}")
            print(f"\n  Updated: {state.get('updated') or '—'}")
            sig = check_signal()
            if sig:
                print(f"  Signal pending: {sig}")
            time.sleep(10)
    except KeyboardInterrupt:
        print("\n[PIT] Monitor exited.")


def cmd_config(_):
    cfg = load_config()
    print(json.dumps(cfg, indent=2))


def cmd_deploy_confirm(args):
    cfg = load_config()
    state = load_state()
    _do_deploy(cfg, state)
    state["status"] = "complete"
    save_state(state)


# ── Entry point ────────────────────────────────────────────────────────────────

def main():
    ap = argparse.ArgumentParser(description="ORC ACADEMY Training Pit Manager")
    sub = ap.add_subparsers(dest="command")

    p_start = sub.add_parser("start", help="Start the pipeline")
    p_start.add_argument("--from", dest="from_phase", choices=ALL_PHASES,
                         help="Start from a specific phase")

    sub.add_parser("stop",           help="Signal graceful stop")
    sub.add_parser("pause",          help="Signal pause")
    sub.add_parser("resume",         help="Resume from pause")
    sub.add_parser("status",         help="Print current state")
    sub.add_parser("monitor",        help="Live auto-refresh monitor")
    sub.add_parser("config",         help="Print active config")
    sub.add_parser("deploy-confirm", help="Proceed with deploy after manual approval")

    args = ap.parse_args()
    if not args.command:
        ap.print_help()
        sys.exit(1)

    commands = {
        "start":          cmd_start,
        "stop":           cmd_stop,
        "pause":          cmd_pause,
        "resume":         cmd_resume,
        "status":         cmd_status,
        "monitor":        cmd_monitor,
        "config":         cmd_config,
        "deploy-confirm": cmd_deploy_confirm,
    }
    commands[args.command](args)


if __name__ == "__main__":
    main()
