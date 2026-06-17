# FIX_REVIEW.md — Verification of gap-closure fixes (2026-06-17)

**Scope:** Only the three listed fixes. Read files + `git diff HEAD`. Hardware baseline: single RTX 5070 Ti 16 GB, Gemma 4 12B QLoRA. No source modified.

## One-line verdicts
- FIX 1 (archive Anthropic generator): **PASS**
- FIX 2 (reproducibility seeds): **PASS**
- FIX 3 (rubric-in-the-loop): **PASS-WITH-NITS** (state safety closed; OOM risk on 16 GB noted but pre-existing)

## Summary table

| Fix | Verdict | Evidence (file:line) | Issue (if any) | Suggested change |
|-----|---------|----------------------|----------------|------------------|
| 1 | PASS | `git mv` confirmed (diff); Tools/_archived/generate_claude_gold.py.disabled:1-22 (old impl + ANTHROPIC_API_KEY + .env doc); Tools/_archived/README.md:7-21 (tombstone + rule citation); docs/ROADMAP.md:140,143 (Claude bullet struck + archived note + "use generate_cerebras_gold.py"); grep (all): only in _archived + tombstone (no live imports, no ANTHROPIC_ instructions outside archive) | Stale prose in README.md:208 still lists "Claude API, your pick" for Pit Boss (non-actionable; no key doc, no implementation path remains) | None required for closure; optional: s/Claude API/Cerebras or Ollama/ in top-level docs |
| 2 | PASS | training_pit/scripts/train_lora.py:48 (`--seed`, default 42); 69 (import set_seed with transformers); 77 (`set_seed(args.seed)` immediately after imports, before load_dataset + model); 157-158 (SFTConfig seed= + data_seed=); 232-239 (summary now records seed, learning_rate, rubric_checkpointing, git_sha, command, best_rubric_pass_pct); 218-225 (git_sha subprocess: cwd=Path(__file__).parent, stderr=DEVNULL, except→"unknown") | None. Command logging uses sys.argv (added at 15); argv here carries only paths/nums/seed (no secret args supported) | None |
| 3 | PASS-WITH-NITS | training_pit/scripts/rubric_callback.py:58-60 (early progress < after_frac → metrics["eval_rubric_pass_pct"]=0.0); 69-72 (save prev_cache/was_training, set use_cache=True + eval()); 90-93 (finally: restore cache + conditional model.train()); 96 (always set key); 84 (do_sample=False); train_lora.py:172 (load_best_model_at_end), 175 (`metric_for_best_model="rubric_pass_pct" if use_rubric`), 182-191 (callback wiring only when use_rubric and not dry-run), 208 (resume_from_checkpoint after attach); 239 (final_eval reads "eval_rubric_pass_pct") | Residual OOM risk on 16 GB (gen holds trainer model + slice during on_evaluate; no empty_cache); HF callback handler does forward `model=` via **kwargs (verified in site-packages transformers) so gen path reachable | None (per instructions: note only) |

## Regressions introduced?
**No.**

- Seed placement does not override later; both global + SFTConfig are used exactly as required by TRL/HF for data shuffling + init (comments at train_lora.py:155-156 document this).
- Rubric key contract: always present on on_evaluate → no KeyError for load_best_model_at_end.
- Git subprocess safe (hardcoded argv, cwd inside tree, devnull, bounded except).
- sys.argv in summary cannot leak secrets for this command surface (keys via env only; datasets are local).
- Defaults update (train_v2gold etc.) and rubric args are additive/backward-compatible for new runs.
- Syntax: both .py files pass py_compile.
- No other modules import/launch the archived claude_gold path (grep confirmed).
- Callback restoration and resume paths preserve prior trainer behavior when --no-rubric.

## Still open from the gap analysis
These fixes do **not** address: suitability / contamination gate before training (Critical), content-hash leakage + disjoint train/eval (High), roles.json single-source alias parity (Medium), pre-train automated filter, or several OOM/UX items outside the rubric callback itself.

**All claims cite only files actually opened during review.**
