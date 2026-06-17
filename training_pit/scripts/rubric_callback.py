#!/usr/bin/env python3
"""Rubric-in-the-loop checkpoint selection for QLoRA training.

Generates plans on a small fixed slice of the eval set at each evaluation,
scores them with the deterministic plan rubric (eval_adapter.score_plan), and
injects `eval_rubric_pass_pct` into the metrics dict. With
`metric_for_best_model="rubric_pass_pct"` + `load_best_model_at_end=True`, the
trainer then keeps the best-*behaving* checkpoint instead of the best-*loss* one.

Why this exists: ORC ACADEMY v2 had a LOWER eval_loss than v1 (0.2595 vs 0.266)
but REGRESSED on real behavior (77.8% vs 99.3%). eval_loss is a proxy; the
rubric is the objective. The regression went undetected for 3.5 h because
checkpoints were selected on loss. See TrainingFlags_Guide.md, Part 3.

Cost control: generation is ~15-30 s/example on a 12B 4-bit model, so scoring
every eval would add hours. Two guards keep it cheap:
  * a small fixed slice (default 24 examples), and
  * `after_frac` — real scoring only kicks in past this fraction of training
    (the best checkpoint for a 2-3 epoch LoRA is essentially always late).
    Earlier evals inject 0.0 (no generation) so best-model tracking never picks
    an early checkpoint and never KeyErrors on a missing metric.

Shares the exact rubric and decoding with eval_adapter so the in-loop number is
comparable to the post-hoc A/B (no second source of truth to drift).
"""
from transformers import TrainerCallback

from eval_adapter import score_plan   # same dir; only side-effect is stdout reconfigure

_DIMS = ["valid_json", "task_count_ok", "roles_valid", "files_named", "no_tester_write"]

# Key HF looks for when metric_for_best_model="rubric_pass_pct" (it prepends eval_).
METRIC_KEY = "eval_rubric_pass_pct"


class RubricEvalCallback(TrainerCallback):
    """Injects eval_rubric_pass_pct at each on_evaluate."""

    def __init__(self, tokenizer, eval_rows, slice_size=24, max_new_tokens=1024,
                 after_frac=0.4, beat=None):
        self.tok = tokenizer
        # Fixed positional slice → run-to-run comparable. Index by position so
        # this works for both a HF Dataset and a plain list of row dicts.
        n = min(slice_size, len(eval_rows))
        self.rows = [eval_rows[i] for i in range(n)]
        self.max_new = max_new_tokens
        self.after_frac = after_frac
        self.beat = beat

    def on_evaluate(self, args, state, control, metrics=None, **kwargs):
        if metrics is None:
            return control

        # Before the active window: stamp 0.0 with no generation. Keeps the key
        # present (no KeyError in best-model tracking) and guarantees early
        # checkpoints are never selected as "best".
        progress = state.global_step / max(1, state.max_steps)
        if progress < self.after_frac:
            metrics[METRIC_KEY] = 0.0
            return control

        model = kwargs.get("model")
        if model is None:
            metrics[METRIC_KEY] = 0.0
            return control

        import torch

        prev_cache = getattr(model.config, "use_cache", False)
        was_training = model.training
        model.config.use_cache = True          # generation needs the KV cache
        model.eval()

        passed = 0.0
        try:
            for row in self.rows:
                msgs = [m for m in row["messages"] if m["role"] in ("system", "user")]
                enc = self.tok.apply_chat_template(
                    msgs, add_generation_prompt=True,
                    return_dict=True, return_tensors="pt").to(model.device)
                with torch.no_grad():
                    out = model.generate(
                        **enc, max_new_tokens=self.max_new, do_sample=False,
                        temperature=None, top_p=None,
                        pad_token_id=self.tok.eos_token_id)
                text = self.tok.decode(
                    out[0][enc["input_ids"].shape[1]:], skip_special_tokens=True)
                s = score_plan(text)
                passed += sum(s[d] for d in _DIMS) / len(_DIMS)
        finally:
            model.config.use_cache = prev_cache
            if was_training:
                model.train()

        pct = round(100.0 * passed / max(1, len(self.rows)), 2)
        metrics[METRIC_KEY] = pct
        if self.beat:
            self.beat("rubric_eval", step=state.global_step,
                      rubric_pass_pct=pct, eval_loss=metrics.get("eval_loss"))
        return control
