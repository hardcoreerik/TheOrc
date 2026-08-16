# INVALIDATED ARTIFACT — Meta-Llama-3.1-8B-Instruct-Q5_K_M ablation sweep

**Status: INVALIDATED.** The sibling file `Meta-Llama-3.1-8B-Instruct-Q5_K_M.yaml` in this
directory is preserved **unmodified** for provenance, but its results must not be cited as
evidence. Use `Meta-Llama-3.1-8B-Instruct-Q5_K_M.CORRECTED.yaml` instead.

## What was wrong

- **Original artifact:** `Tools/OrcEnginePhase0/artifacts/ablation_streaming/Meta-Llama-3.1-8B-Instruct-Q5_K_M.yaml`
- **SHA256:** `a89048bad6733abfcb53a259daf6a27b89af018401df630a828b2a2e5ac50903`
- **Producing commit:** `0338f2a935b8516c7d29935966bcc1c00c645ea9` ("feat: first-ever 8B-model
  ablation result (Llama-3.1-8B, 73.7s)")

**Affected mathematical assumption:** every forward-pass implementation in this codebase
(CPU oracle, GPU oracle, streaming oracle) computed final logits as
`final_normed @ token_embedding.T` unconditionally — the "tied embeddings" formula. The GGUF
loaders correctly *detected* whether a model's output projection was tied (via presence of a
separate `output.weight` tensor) and reported `tied_embeddings: false` in the artifact's own
`gguf_info`, but no code path ever actually *used* that real `output.weight` tensor for the
logit projection. Llama-3.1-8B is a genuinely untied model (confirmed: this artifact's own
`gguf_info.tied_embeddings` field reads `false`), so every logit in the original artifact was
computed with the **wrong output projection matrix**.

## How this was found and confirmed

1. During an OrcEngine architecture review, grep across `oracle/*.py` showed every
   `forward`/`forward_gpu`/`forward_streaming` implementation reading `token_embedding` (or
   `token_embedding.T`) for the final logits, with no corresponding read of `output.weight`
   anywhere the tensor was loaded and detected.
2. Checked this specific artifact's own retained `gguf_info.tied_embeddings` field: `false`.
   Confirms the bug was live for exactly the model this artifact reports on, not a hypothetical.
3. After implementing the fix (see below), reloaded Llama-3.1-8B via the streaming loader and
   confirmed `lm_head` is a real, distinct tensor: shape `(128256, 4096)`, same as
   `token_embedding`, but with a **0.354 max absolute difference** from `token_embedding` —
   genuinely different values, not a coincidental near-match that would make the bug harmless.

## Correcting commit and replacement artifact

- **Correcting commit:** (this session's fix — see the commit that introduces
  `ModelWeights.lm_head` / `TorchModelWeights.lm_head` / `StreamingGGUFModel.effective_lm_head()`
  and updates every forward pass to call `effective_lm_head()` instead of `token_embedding`
  directly)
- **Replacement artifact:** `Tools/OrcEnginePhase0/artifacts/ablation_streaming/Meta-Llama-3.1-8B-Instruct-Q5_K_M.CORRECTED.yaml`
- **Replacement SHA256:** `28722e6a98f18d2a502381273dd2598d8059dd17fbe3df85177453c27e03936e`

A synthetic regression fixture (`Tools/OrcEnginePhase0/oracle/fixture_untied_lm_head.py`) now
proves an untied `lm_head` actually changes computed logits (1.63 max logit diff on a synthetic
model where `lm_head` is independently seeded from `token_embedding`) — this is the check that
would have caught the original bug had it existed before this artifact was produced.

## How the corrected result differs from the invalid one

Both agree on the **qualitative** finding: early layers (0, 1) and late layers (29–31) dominate
impact; middle layers are comparatively safe to perturb. That "bookends" pattern survives the
correction. It does **not** survive at the same magnitudes or the same internal ranking:

| Rank | Old (invalid) | logit_l2 | New (corrected) | logit_l2 |
|---|---|---|---|---|
| 1 | layer0.full_layer | 623.45 | **layer31.full_layer** | **1006.61** |
| 2 | layer1.full_layer | 613.86 | layer0.full_layer | 968.10 |
| 3 | layer31.full_layer | 580.00 | layer1.full_layer | 953.73 |
| 4 | layer29.full_layer | 400.20 | layer29.full_layer | 660.10 |
| 5 | layer30.full_layer | 393.63 | layer30.full_layer | 624.86 |

Notably: **layer 31 (the last layer) was ranked 3rd under the wrong projection and is ranked
1st under the correct one.** Since logits are the direct output of `final_normed @ lm_head.T`,
and the last layer's output IS `final_normed` (post the final RMSNorm), it makes sense that
measuring "how much does ablating this layer change the output" through the WRONG projection
matrix would understate the last layer's true importance — the wrong matrix (`token_embedding`)
and the real one (`lm_head`) point in different directions in the embedding space, so a
divergence that's large under the real projection can appear smaller under the wrong one, and
vice versa. This is a real, structural reason the old ranking was wrong, not just numerical
noise.

Absolute magnitudes are also uniformly larger in the corrected version (roughly 1.5–1.6x) —
consistent with the real `lm_head` and `token_embedding` matrices having different scale/norm
properties from each other.

## Correct language going forward

- These findings are **"on this deterministic probe set"** (three fixed pseudo-random
  token-ID sequences), not a universal claim about Llama-3.1-8B's architecture, and not
  a universal claim across models.
- **Zero-ablation sensitivity is not quantization sensitivity.** A layer being sensitive to
  complete removal does not by itself prove it needs high-precision quantization, and a layer
  being safe to remove entirely does not by itself prove it's safe to quantize aggressively.
  Ablation is a prior for where to investigate precision, not a substitute for direct
  quantization-perturbation experiments.
