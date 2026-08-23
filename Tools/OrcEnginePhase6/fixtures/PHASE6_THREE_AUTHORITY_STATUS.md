# Phase 6 Stage 1, Commit B: three-authority F32 diagnostic -- Outcome P2

**Question answered**: on the 3 F32-divergent prompts, does the pinned
PyTorch source-model authority (real HuggingFace `transformers`
`LlamaForCausalLM`, upstream third-party code) agree with OrcEngine or
with llama.cpp?

**Answer: PyTorch agrees with OrcEngine on all 7 prompts (both the 4
controls and all 3 divergent prompts) and disagrees with llama.cpp on
exactly the same 3 prompts where OrcEngine already disagreed with
llama.cpp. Classified as Outcome P2.**

This is a read-only diagnostic. No transformer math was changed, no
tolerance was widened, and Stages 4-6/freeze/FL-08 are not started as a
result of this finding.

## Established PyTorch authority (Gate 10)

- **Source model/revision**: `HuggingFaceTB/SmolLM2-135M`, revision
  `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` (pinned in
  `oracle/download_candidate.py`, per `OE-ADR-014`).
- **Local artifact**: `Tools/OrcEnginePhase0/artifacts/smollm2-135m/`
  (`config.json`, `model.safetensors`, tokenizer files -- present in
  the sibling `OrchestratorIDE-phase2-gguf` worktree at the same
  relative path; this diagnostic reads it via `--hf-model-dir`, no
  network access, no download, no regeneration).
- **PyTorch / Transformers versions actually used**: `torch 2.11.0+cu128`,
  `transformers 5.11.0` (the model's own `config.json` records
  `transformers_version: 4.40.1` as the version it was originally saved
  under -- a newer runtime than that was used here; `LlamaForCausalLM`'s
  forward-pass architecture is stable across this range, and this is
  disclosed rather than silently glossed over).
- **Weight identity, independently verified**: `model.safetensors`
  SHA-256 `80521b40281d6ce74e35c9282c22539e75aa0ac8578892b2a59955ef78d55da1`
  -- computed fresh by this diagnostic AND matches the
  `source_safetensors_sha256` field already committed in
  `Tools/OrcEnginePhase0/artifacts/real_candidate_conversion_manifest.json`
  (a Phase 0 artifact, not authored for this diagnostic).
- **Confirmation this is the source used to produce the F32 GGUF**:
  that SAME manifest's `output_gguf_sha256` field is
  `fffab10c5298f8b1399088e893c1ddd64e48cd7e5020982a5b2a848e445a4aac` --
  the EXACT pinned F32 GGUF hash used throughout Phase 6 Stage 1
  (`Q8_0_FIXTURE_PROVENANCE.md`'s "Input" section,
  `phase6_q8_0_comparison.cpp`'s `kF32ExpectedSha256`, and every prior
  evidence file's `f32_artifact_sha256`). This is not merely a
  same-named model -- it is provably the literal source of the F32 GGUF
  the whole Phase 6 comparison chain has been using.
- **This directory was already used once before as a real PyTorch
  ground-truth authority**: `oracle/hf_reference_check.py` (OE-ADR-017,
  2026-08-15) found the Phase 0 NumPy oracle matched this exact HF model
  exactly (0.0000 diff) on 10 previously-disputed tokens, with llama.cpp
  the outlier there too -- this diagnostic extends that same established
  pattern to OrcEngine and to the full Phase 6 corpus, rather than
  inventing a new authority.
- **Execution discipline** (Gate 10 items 6-12, all satisfied):
  `model.eval()` called; `torch.no_grad()` wraps the forward pass; no
  sampling (raw logits read directly, argmax taken in Python); **no
  tokenizer is invoked** -- `phase6_three_authority_f32_diagnostic.py`
  passes the exact committed `token_ids` from the schema-v3 evidence
  directly as `input_ids`, never calling `AutoTokenizer`; no BOS/EOS or
  other special token is inserted (the script does not add any token to
  the passed list); position IDs are HF's default `0..N-1` for a single
  non-cached forward pass, matching OrcEngine's own
  `forward_cached_step(..., start_position=0)` convention for these
  prompts; the comparison is the FINAL prompt position's full-vocabulary
  logits (`out.logits[0, -1]`, all 49152 entries).

No file was downloaded, no package was updated, no model was
regenerated, and no new oracle authority was created -- this reuses the
already-pinned Phase 0 artifact and provenance chain exactly as it
already existed.

## Fixed diagnostic corpus (Gate 11)

All 7 existing Phase 6 prompts, using the exact committed `token_ids`
from `phase6_q8_0_f32_comparison_evidence_v3.jsonl` -- no new corpus,
no changed tokenization. Control group (already-agreeing):
`dev_capital_of_france`, `dev_once_upon_a_time`, `dev_code_snippet`,
`holdout_hello_world`. Divergent group: `dev_year_weather`,
`holdout_she_walked`, `holdout_quick_fox`. Both groups ran through the
identical code path (same script, same model load, same per-prompt
function) -- see `tools/phase6_three_authority_f32_diagnostic.py`.

## Results table

| Prompt | Group | PyTorch selected | OrcEngine F32 | llama.cpp F32 | PT agrees Orc? | PT agrees llama? |
|---|---|---|---|---|---|---|
| dev_capital_of_france | control | 260 | 260 | 260 | True | True |
| dev_once_upon_a_time | control | 28 | 28 | 28 | True | True |
| dev_code_snippet | control | 253 | 253 | 253 | True | True |
| holdout_hello_world | control | 253 | 253 | 253 | True | True |
| dev_year_weather | divergent | 523 | 523 | 436 | **True** | **False** |
| holdout_she_walked | divergent | 3589 | 3589 | 38734 | **True** | **False** |
| holdout_quick_fox | divergent | 27003 | 27003 | 28 | **True** | **False** |

Raw run output: `phase6_three_authority_f32_run_output.txt`. Structured
report (per-prompt PyTorch top-10, comparisons, error metrics):
`phase6_three_authority_f32_report.jsonl`.

## Diagnostic error metrics (where directly comparable; not a pass/fail gate)

For every token ID in PyTorch's own top-10 that ALSO appears in
OrcEngine's top-5 (using OrcEngine's own recorded
`f32_full_vocab_logsumexp` to convert its raw logit to a log-
probability) or in llama.cpp's own reported top-5 (already
log-probabilities), max absolute error and RMSE in log-probability
space:

| Prompt | max\|err\| vs Orc | RMSE vs Orc | max\|err\| vs llama | RMSE vs llama |
|---|---|---|---|---|
| dev_capital_of_france | 2.0e-05 | 1.0e-05 | 0.954 | 0.547 |
| dev_once_upon_a_time | 1.8e-05 | 1.3e-05 | 2.017 | 1.079 |
| dev_code_snippet | 1.3e-05 | 1.0e-05 | 1.166 | 0.696 |
| holdout_hello_world | 4.1e-06 | 2.0e-06 | 0.749 | 0.481 |
| dev_year_weather | 1.0e-05 | 7.2e-06 | 0.768 | 0.768 |
| holdout_she_walked | 1.1e-05 | 5.9e-06 | N/A (no overlap) | N/A |
| holdout_quick_fox | 3.4e-05 | 2.1e-05 | 1.618 | 0.835 |

**PyTorch-vs-OrcEngine agreement is at floating-point-precision scale
(~1e-5 nats) on EVERY prompt, control and divergent alike** -- this is
essentially the strongest possible confirmation that OrcEngine's F32
forward pass reproduces the real reference implementation almost
exactly. **PyTorch-vs-llama.cpp shows large, systematic deviation
(0.5-2.0 nats) on EVERY prompt where the comparison was possible,
INCLUDING the control prompts where llama.cpp's greedy selection
happens to still match** -- meaning llama.cpp's distributional
disagreement with ground truth is not confined to the 3 divergent
prompts; it is a broader, consistent pattern that only crosses into a
different ARGMAX on 3 of the 7 prompts examined here. `holdout_she_walked`
shows no directly-comparable llama.cpp token (none of PyTorch's own
top-10 appeared in llama.cpp's reported top-5 for that specific
prompt) -- reported as `N/A`, not approximated.

These are diagnostic numbers, not a pass/fail gate; no new tolerance is
declared or applied.

## Outcome classification: **Outcome P2**

Per the governing taxonomy: PyTorch agrees with OrcEngine and differs
from llama.cpp on the divergent prompts, with the controls also
consistent. **Classified as Outcome P2**:

- The llama.cpp invocation/model interpretation is classified as the
  **likely** divergence source -- not confirmed, not declared
  numerically wrong yet.
- Recommended next check (NOT performed in this diagnostic pass, since
  it would require inspecting/modifying llama.cpp's own invocation, not
  OrcEngine's): llama.cpp's exact configuration for this GGUF (RoPE
  theta/scaling metadata interpretation, `rope_theta: 100000` per the HF
  config -- confirm llama.cpp reads and applies the SAME value from the
  GGUF's own `llama.rope.freq_base` metadata field, not a different
  default), position handling for a fresh (non-cached, `cache_prompt:
  false`) single-shot completion request, and exact input-token
  execution (already proven token-ID-identical via this project's
  existing tokenization checks, so this is about llama.cpp's INTERNAL
  handling of those same tokens, not what tokens it received).

## What this does NOT establish

- Does not prove llama.cpp is defective -- "likely divergence source"
  is a classification for where to look next, not a conclusion about
  root cause.
- Does not change, or recommend changing, any OrcEngine transformer
  math (RoPE, attention, RMSNorm, residuals, position handling) --
  none of that was touched or needs to be, given this result.
- Does not resolve or interact with Phase 6's SECOND, independent
  blocker -- the internal DEV-derived F32-vs-Q8_0 tolerance failing on
  `holdout_quick_fox` (`0.973504` vs observed `1.079983`, still
  standing; see `STAGE2_STAGE3_STATUS.md`'s "Two independent Phase 6
  blockers" section). That blocker is about OrcEngine's OWN F32-vs-Q8_0
  quantization behavior and is untouched by this diagnostic, which
  compares three DIFFERENT implementations all at F32.
- Does not authorize Stage 4, Stage 5, Stage 6, a freeze tag, or FL-08.
