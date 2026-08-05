# TheOrc — Native Engine Forward Pass Spec

> **Status: 🔲 Planned.** The mathematical specification a coding agent implements from —
> without reading a reference implementation. Model: Llama 3.2 1B Instruct
> ([SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md) §3). Every named dimension
> below is bound from `config.json` at load
> ([SAFETENSORS_FORMAT_SPEC.md](SAFETENSORS_FORMAT_SPEC.md) §5) — the concrete numbers in
> parentheses are the 1B's expected values, present for readability, and must never be
> hard-coded.

---

## 1. Named dimensions and weight inventory

| Symbol | Meaning | Source key | 1B value |
|---|---|---|---|
| `V` | vocab size | `vocab_size` | 128256 |
| `D` | model width | `hidden_size` | 2048 |
| `L` | layer count | `num_hidden_layers` | 16 |
| `H` | query heads | `num_attention_heads` | 32 |
| `H_kv` | key/value heads | `num_key_value_heads` | 8 |
| `d` | head dim | `head_dim` (else `D / H`) | 64 |
| `F` | MLP inner width | `intermediate_size` | 8192 |
| `ε` | RMSNorm epsilon | `rms_norm_eps` | 1e-5 |
| `θ` | RoPE base | `rope_theta` | 500000.0 |
| `T` | current sequence length (tokens so far) | runtime | ≤ 4096 in spike |
| `G` | GQA group size = `H / H_kv` | derived | 4 |

Weight tensors (HF names — these are the exact keys in the safetensors header). Storage
convention is PyTorch `nn.Linear`: weight shape is `[out_features, in_features]` and the op
is `y = x · Wᵀ`. Getting this transpose wrong produces shape-compatible garbage at `D = F`
boundaries only by luck — treat the convention as load-time-validated, not assumed:

| Tensor | Shape | Dtype (stored) |
|---|---|---|
| `model.embed_tokens.weight` | `[V, D]` | BF16 |
| `model.layers.{i}.input_layernorm.weight` | `[D]` | BF16 |
| `model.layers.{i}.self_attn.q_proj.weight` | `[H·d, D]` | BF16 |
| `model.layers.{i}.self_attn.k_proj.weight` | `[H_kv·d, D]` | BF16 |
| `model.layers.{i}.self_attn.v_proj.weight` | `[H_kv·d, D]` | BF16 |
| `model.layers.{i}.self_attn.o_proj.weight` | `[D, H·d]` | BF16 |
| `model.layers.{i}.post_attention_layernorm.weight` | `[D]` | BF16 |
| `model.layers.{i}.mlp.gate_proj.weight` | `[F, D]` | BF16 |
| `model.layers.{i}.mlp.up_proj.weight` | `[F, D]` | BF16 |
| `model.layers.{i}.mlp.down_proj.weight` | `[D, F]` | BF16 |
| `model.norm.weight` | `[D]` | BF16 |
| `lm_head.weight` | **absent** — tied | see §7 |

Llama attention and MLP projections have **no bias tensors**. A loader that "helpfully"
zero-fills missing biases hides a wrong-model bug; absence is asserted, not defaulted.

Load-time validation: every expected tensor present with exactly the shape above (bound
dims, not 1B literals); any extra `model.*` tensor is an error (wrong architecture), any
missing one is an error naming it.

---

## 2. Top-level structure

For input token IDs `t₀ … t_{T−1}`:

```
x⁰ = Embed(t)                                   # [T, D]
for i in 0 … L−1:
    a  = RMSNorm(xⁱ, w_in[i])                   # [T, D]
    h  = xⁱ + Attention_i(a)                    # [T, D]   (residual 1)
    b  = RMSNorm(h, w_post[i])                  # [T, D]
    xⁱ⁺¹ = h + MLP_i(b)                         # [T, D]   (residual 2)
y      = RMSNorm(x^L, w_final)                  # [T, D]
logits = y · Embedᵀ                             # [T, V]   (tied head, §7)
```

Pre-norm residual structure: the norm is applied to the branch input; the residual adds the
**un-normed** stream. Dtype at each stage: activations are F16 on the GPU path and F32 on
the CPU reference path; all accumulations in F32 regardless (per-op notes below).

Prefill processes all `T` positions in one pass and populates the KV cache; decode
processes one new position (`T ← T + 1`) reusing the cache. Both use the identical per-op
math — prefill is just the `T > 1` case with a causal mask.

---

## 3. Op: RMSNorm

Input `x : [T, D]`, weight `w : [D]`, output `y : [T, D]`. Per row `x_t`:

```
rms(x_t)  = sqrt( (1/D) · Σ_{j=0}^{D−1} x_{t,j}² + ε )
y_{t,j}   = ( x_{t,j} / rms(x_t) ) · w_j
```

- The Σ of squares is accumulated in **F32** (F16 accumulation loses ~3 decimal digits over
  2048 terms and is the classic first-divergent-layer culprit).
- `ε` is added **inside** the sqrt, to the mean, not to the rms — the two placements differ
  and HF/Llama uses mean + ε.
- Note: Llama's RMSNorm multiplies by `w` only — there is no `(1 + w)` form here (that is a
  Gemma-family convention; using it on Llama weights is wrong).
- Reduction order: sequential over `j` (CPU) / fixed-tree (GPU) — pinned for determinism.

---

## 4. Op: RoPE (with Llama-3 frequency scaling)

Applied to Q and K only (never V), per head, after projection, before attention. For head
vector `v : [d]` at absolute position `p` (0-based over the whole sequence — the KV cache
means decode positions continue from prefill, so `p` is the token's index in the full
sequence, tracked by the session, never reset per call):

Base inverse frequencies, `i ∈ [0, d/2)`:

```
invFreq_i = θ^(−2i / d)
```

**Llama-3 scaling** (from `config.json.rope_scaling`; parameters `factor` = 32.0,
`lowFactor` = 1.0, `highFactor` = 4.0, `origCtx` = 8192):

```
λ_i        = 2π / invFreq_i                      # wavelength
lowWavelen  = origCtx / lowFactor                # 8192
highWavelen = origCtx / highFactor               # 2048

invFreq'_i =
    invFreq_i                          if λ_i < highWavelen          # high-freq: unchanged
    invFreq_i / factor                 if λ_i > lowWavelen           # low-freq: fully scaled
    (1−s)·(invFreq_i / factor) + s·invFreq_i     otherwise           # smooth ramp
        where s = ( origCtx/λ_i − lowFactor ) / ( highFactor − lowFactor )
```

This scaling is **not optional**. Skipping it leaves short-prompt outputs looking plausible
while long-context behavior silently corrupts — the parity harness's long-prompt cases
([LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §4) exist specifically to catch this.

Rotation — **HF half-split ("rotate_half") convention**, `i ∈ [0, d/2)`:

```
c_i = cos(p · invFreq'_i)        s_i = sin(p · invFreq'_i)
v'_i        = v_i        · c_i  −  v_{i+d/2} · s_i
v'_{i+d/2}  = v_{i+d/2}  · c_i  +  v_i        · s_i
```

> ⚠️ **Convention trap, pre-registered:** llama.cpp applies an *interleaved-pair* rotation
> and compensates by having `convert_hf_to_gguf.py` **permute the rows of `q_proj`/`k_proj`
> during GGUF conversion**. This engine reads the HF weights unpermuted, so it must use the
> HF half-split form above. Copying llama.cpp's rotation while loading HF weights produces
> confidently wrong logits at every position — the expected #1 candidate if the parity
> harness's layer localization points at layer 0's attention.

- `cos`/`sin` computed in F32 (angle `p · invFreq'` reaches ~4096 · 1 ≈ 4096 rad; F16
  cannot represent the angle, let alone the trig).
- The `[ctx, d/2]` cos/sin tables are precomputed once at session load (they depend only on
  config), stored F32.

---

## 5. Op: Grouped-query attention with KV cache

Per layer `i`. Input `a : [T_new, D]` (normed branch input; `T_new` = tokens in this call).

**Projections** (matmul, `y = x · Wᵀ`, F32 accumulation):

```
Q = a · Wqᵀ      # [T_new, H·d]    reshape → [T_new, H,    d]
K = a · Wkᵀ      # [T_new, H_kv·d] reshape → [T_new, H_kv, d]
V = a · Wvᵀ      # [T_new, H_kv·d] reshape → [T_new, H_kv, d]
```

RoPE (§4) applied to Q and K with each token's absolute position. Then K, V rows are
**appended** to the layer's cache:

```
kCache_i : [T_total, H_kv, d]     vCache_i : [T_total, H_kv, d]     (F16 on GPU; F32 on CPU ref)
```

**Scores + weighted sum**, per query head `h ∈ [0, H)`, using kv head `g = h / G`
(integer division — head 0..3 → kv 0, head 4..7 → kv 1, …):

```
score_{t,j} = ( Q_{t,h} · kCache_{j,g} ) / sqrt(d)      # F32 dot over d
masked:  score_{t,j} = −∞  for j > pos(t)               # causal: keys strictly after the query's absolute position are unreachable
p_{t,·}  = softmax(score_{t,·})                          # F32, max-subtracted:
           m = max_j score      p_j = exp(score_j − m) / Σ exp(score_k − m)
out_{t,h} = Σ_j p_{t,j} · vCache_{j,g}                   # [d], F32 accumulation
```

Concatenate heads → `[T_new, H·d]`, then output projection:

```
attnOut = out · Woᵀ        # [T_new, D]
```

- Softmax is always F32; scores never stored F16 (exp of an F16-rounded score is a real
  parity error source).
- The mask uses **absolute** positions: during decode (`T_new = 1`) the single query
  attends to all `T_total` cached positions including itself; no mask entry is −∞.
- `−∞` is implemented as F32 `float.NegativeInfinity`, applied *before* the max-subtraction
  (max of a fully-masked row would be −∞ — cannot occur here since `j = pos(t)` is always
  legal, but the kernel asserts a finite max anyway).
- No sliding window, no attention sinks, no logit softcapping — plain Llama attention.
  Asserted absent from config rather than silently unimplemented.

---

## 6. Op: SwiGLU MLP

Input `b : [T_new, D]`:

```
g = b · Wgateᵀ          # [T_new, F]
u = b · Wupᵀ            # [T_new, F]
silu(x) = x · σ(x) = x / (1 + e^{−x})
h = silu(g) ⊙ u         # [T_new, F]   elementwise
mlpOut = h · Wdownᵀ     # [T_new, D]
```

- `silu` computed in F32 (`e^{−x}` for F16-boundary x values is where cheap
  implementations diverge); result rounded to F16 only at the elementwise-product output on
  the GPU path.
- `⊙` is elementwise multiply; the gate/up order matters (`silu` on **gate**, not up).

---

## 7. Final norm, tied LM head, and sampling

```
y      = RMSNorm(x^L, model.norm.weight)         # [T, D]
logits = y · Embedᵀ                              # [T, V] — Embed is model.embed_tokens.weight [V, D]
```

`config.json.tie_word_embeddings = true` for the 1B: there is **no** `lm_head.weight`
tensor, and the head matmul reuses the embedding matrix (mathematically
`logits_{t,v} = y_t · Embed_v`, i.e. the same `y = x · Wᵀ` primitive with `W = Embed`).
The load-time check keys off the config flag: tied + `lm_head.weight` present is an error
(ambiguous checkpoint), untied + absent likewise.

- Logits are produced and stored in **F32** always — they are the parity harness's raw
  material and must not pass through an F16 bottleneck after the head matmul's F32
  accumulation.
- Parity/benchmark runs compute logits only for the positions the harness needs (last
  position during decode; all positions during prefill capture, per
  [LOGIT_PARITY_HARNESS.md](LOGIT_PARITY_HARNESS.md) §3).

**Sampling (spike scope = greedy only):**

```
next = argmin_{v} such that logits_v is maximal    # i.e. argmax; ties → lowest token ID
```

The tie-break is part of the determinism contract
([NATIVE_ENGINE_ARCHITECTURE.md](NATIVE_ENGINE_ARCHITECTURE.md) §7). Temperature/top-p are
out of spike scope; the session refuses non-greedy configs rather than half-implementing
them. Generation stops at any configured EOS token ID (`eos_token_id` list from config) or
the requested max-token cap.

---

## 8. Dtype summary table

| Stage | GPU path storage | Accumulation | CPU reference path |
|---|---|---|---|
| Weights at rest | F16 (converted from BF16 at upload) | — | F32 (converted from BF16 directly) |
| Activations | F16 | — | F32 |
| All matmul dots | — | F32 | F32 |
| RMSNorm Σx² | — | F32 | F32 |
| RoPE angles + trig | F32 tables | — | F32 |
| Attention scores/softmax | F32 | F32 | F32 |
| KV cache | F16 | — | F32 |
| silu | F32 compute, F16 store | — | F32 |
| Logits | **F32** | F32 | F32 |

The CPU path exists to answer one question per the parity harness: when GPU-F16 diverges,
is it F16 rounding (CPU-F32 agrees with HF) or a math bug (CPU-F32 disagrees too)?

---

## 9. Per-op unit fixtures

Each op above gets a fixture test with tiny, hand-checkable dimensions (e.g. `D=4, H=2,
H_kv=1, d=2`) whose expected values are computed in the test from the formulas — not
golden files, so a reviewer can verify by hand. Plus one integration fixture: a single
transformer layer with fixed small random weights (seeded, generated in-test), CPU-F32,
compared against a NumPy hand-derivation checked into the test as literals with its
derivation script in a comment. Naming per repo convention:
`RmsNorm_UnitVarianceRow_ScalesByWeight`, `Rope_PositionZero_IsIdentity`,
`Attention_SingleTokenDecode_MatchesFullPrefillLastRow`,
`SwiGlu_NegativeGate_SuppressesOutput`, etc. The last of these
(`…MatchesFullPrefillLastRow`) is the KV-cache correctness test: decode-with-cache must
equal prefill-from-scratch bit-for-bit on the CPU path.
