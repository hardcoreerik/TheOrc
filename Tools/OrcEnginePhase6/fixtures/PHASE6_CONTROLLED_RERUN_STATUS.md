# Phase 6 Stage 1, Commit 2: controlled external-oracle rerun -- Outcome P2 confirmed, not caused by KV-cache/flash-attn confound

This document supersedes the provisional Outcome P2 classification in
`PHASE6_THREE_AUTHORITY_STATUS.md` (which used an uncontrolled
llama.cpp invocation) with a controlled rerun using explicit
`--cache-type-k f32 --cache-type-v f32 --flash-attn off` on every
llama.cpp leg. Same 7 prompts, same exact token IDs, same compared
position (final prompt position), same model files, same server binary
and implementation DLL, same neutral greedy completion payload.

## Effective controlled settings (both F32 and Q8_0 legs, byte-identical)

```
--cache-type-k f32 --cache-type-v f32 --flash-attn off
```

(`phase6_llama_cpp_q8_0_oracle.CONTROLLED_NUMERICAL_SERVER_ARGS`; every
regenerated evidence row below carries this exact tuple in its
`controlled_server_args`/`llama_controlled_server_args` field, so a
later reviewer does not have to trust this document's transcription.)

## Controlled five-authority selected-token table

| Prompt | PyTorch F32 | OrcEngine F32 | OrcEngine Q8_0 | llama.cpp F32 (controlled) | llama.cpp Q8_0 (controlled) | Token-ID identity |
|---|---|---|---|---|---|---|
| dev_capital_of_france | 260 | 260 | 260 | 260 | 260 | exact (all legs) |
| dev_once_upon_a_time | 28 | 28 | 28 | 28 | 28 | exact (all legs) |
| dev_code_snippet | 253 | 253 | 253 | 253 | 253 | exact (all legs) |
| dev_year_weather | 523 | 523 | 523 | **436** | **436** | exact (all legs) |
| holdout_hello_world | 253 | 253 | 253 | 253 | 253 | exact (all legs) |
| holdout_she_walked | 3589 | 3589 | 3589 | **38734** | **9612** | exact (all legs) |
| holdout_quick_fox | 27003 | 27003 | 27003 | **28** | **28** | exact (all legs) |

Compared position: final prompt-token position (index `len(token_ids)-1`)
for every prompt, unchanged from all prior runs. Token-ID identity was
independently re-verified against the live server on every leg for
every prompt in this controlled rerun -- exact match throughout, no
regression.

Raw outputs: `phase6_llama_cpp_q8_0_oracle_run_output_controlled.txt`
(Q8-vs-Q8 leg), `phase6_f32_localization_run_output_controlled.txt`
(F32-vs-F32 leg), `phase6_paired_four_way_run_output_controlled.txt`
(paired four-way), `phase6_three_authority_f32_run_output_controlled.txt`
(three-authority). Structured reports: the `*_controlled.jsonl`
sibling of each.

## Overlapping-top-k numerical errors (explicitly subset metrics, not full-vocabulary)

Unchanged in kind from the uncontrolled run -- these compare only the
intersection of PyTorch's own top-10 with OrcEngine's top-5 / llama.cpp's
reported top-5, per `phase6_three_authority_f32_report_controlled.jsonl`'s
`comparisons_where_directly_comparable`/`max_abs_error_vs_orc`/
`max_abs_error_vs_llama` fields. PyTorch-vs-OrcEngine remains at
floating-point-precision scale (~1e-5 nats) on every prompt. This is
NOT a full-vocabulary equality proof.

## Classification: still Outcome P2, no longer provisional

**The controlled rerun reproduces the identical selected-token result
exactly** -- the same 3 prompts (`dev_year_weather`, `holdout_she_walked`,
`holdout_quick_fox`) disagree between {PyTorch, OrcEngine} and
llama.cpp, with the identical specific tokens on both sides, under
explicit F32 KV cache and disabled flash attention. **This confirms the
KV-cache-precision/flash-attention confound identified by Codex was NOT
the cause of the disagreement.** Per the governing instructions: "If
controlled llama.cpp F32 still disagrees: do not call llama.cpp wrong."
This finding does not localize a defect in llama.cpp, OrcEngine, or
elsewhere -- it rules out one specific candidate explanation
(KV-cache/flash-attention settings) and leaves the disagreement's true
cause unresolved.

## Sensitivity check (out of primary corpus; one-factor-at-a-time on the 3 divergent prompts)

Per instruction, this sensitivity check is recorded separately from the
primary controlled acceptance corpus above:

1. **Prior/default configuration** (F16 KV cache, `--flash-attn auto`):
   result recorded in `phase6_llama_cpp_q8_0_oracle_report.jsonl` /
   `phase6_f32_localization_report.jsonl` (the original, uncontrolled
   runs) -- `dev_year_weather`=436, `holdout_she_walked`=38734(F32)/9612(Q8),
   `holdout_quick_fox`=28.
2. **F32 K/V cache, flash-attention otherwise unchanged (`auto`)**: NOT
   separately run in this pass -- would require an additional server
   invocation isolating only the cache-type change. Not performed here;
   noted as a gap, not silently skipped.
3. **F32 K/V cache with flash attention explicitly disabled** (the
   controlled configuration used throughout this document): identical
   result to configuration 1 on all 3 divergent prompts (see table
   above).

Since configuration 3's result is identical to configuration 1's on
every divergent prompt, and configuration 2 was not separately run, this
sensitivity check's available evidence is consistent with (but does not
exhaustively prove, absent configuration 2's isolated result) neither
KV-cache precision nor flash-attention mode individually explaining the
divergence -- the combined controlled setting changed nothing.

## Recommended narrowest remaining parity questions (per instruction, none performed in this pass)

- RoPE base/frequency metadata: the HF `config.json` records
  `rope_theta: 100000`; confirm llama.cpp reads and applies the exact
  same value from the GGUF's `llama.rope.freq_base` metadata field for
  these specific prompts, not a silently different default.
- Position IDs and BOS/EOS handling: already partially checked (no BOS
  token appears in any recorded token_ids for this corpus; token-ID
  identity is independently verified on every leg of every run
  including this one) -- worth a fresh, explicit confirmation scoped to
  llama.cpp's own internal position/cache-slot bookkeeping rather than
  its tokenizer output.
- Compared sequence position: confirmed identical (final prompt
  position) across all three implementations by construction of this
  diagnostic; not a live suspect but recorded as checked.
- Context and cache initialization: llama.cpp's `--no-warmup` and
  `cache_prompt: false` are already set; whether its internal KV-slot
  allocation/initialization differs from OrcEngine's in a way that
  matters for a single-shot (non-multi-turn) completion is unverified.
- Model architecture/config interpretation: llama.cpp's own GGUF
  metadata parsing for this specific converted model (`tie_word_
  embeddings` handling, RoPE dimension count, attention head/KV-head
  counts) has not been independently audited against the HF config in
  this pass.
- Any remaining precision or offload defaults: CPU-only pinned build,
  no GPU offload configured on either side; not identified as a live
  suspect but not independently reconfirmed in this specific controlled
  run.

None of these were investigated in this pass -- per instruction, this
stops here rather than proceeding to a frozen-engine-math change
without a specific, evidence-localized defect.
