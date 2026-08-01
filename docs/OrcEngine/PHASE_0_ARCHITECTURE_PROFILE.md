# Phase 0 Architecture Profile

## Purpose

“Llama-style” is a family label, not an executable specification. This document pins the exact model mathematics used by the synthetic Phase 0 fixtures and records the selected real-model candidate separately. It does not choose an implementation language.

## Profile A — OE-L0-SYNTH-1

**DECIDED:** `OE-L0-SYNTH-1` is the first independently executable synthetic architecture profile.

| Property | Value |
|---|---:|
| Modality | text token IDs only |
| Vocabulary | 32 |
| Hidden size | 16 |
| Intermediate size | 32 |
| Decoder layers | 2 |
| Query heads | 4 |
| KV heads | 2 |
| Head dimension | 4 |
| Maximum positions | 16 |
| Weight dtype | float32 |
| Activation/accumulator dtype | float32 |
| Biases | none |
| RMSNorm epsilon | `1e-5` |
| RoPE base theta | `10000.0` |
| RoPE scaling | none |
| Token embedding/output weights | tied |
| Attention/dropout | causal / disabled |
| Decode | greedy, lowest token ID wins an exact tie |

### Exact block semantics

For row-vector hidden state `x` at each position:

1. `a = RMSNorm(x, attn_norm_weight, epsilon)`.
2. `q = a Wq^T`, `k = a Wk^T`, and `v = a Wv^T`.
3. Reshape Q to `[position, 4, 4]`; reshape K and V to `[position, 2, 4]`.
4. Apply non-interleaved Llama RoPE to Q and K. Split each head into equal first/second halves and rotate `[-second_half, first_half]` using position-indexed cosine and sine values.
5. Map query head `h` to KV head `floor(h / 2)`. This is the only accepted GQA mapping for this profile.
6. Compute scaled dot-product attention with scale `1 / sqrt(4)`. A query at position `p` may attend only to key positions `0..p`. Apply the causal mask before a max-subtracted float32 softmax.
7. Concatenate query-head outputs and compute `attn_out = context Wo^T`.
8. First residual: `r = x + attn_out`.
9. `f = RMSNorm(r, ffn_norm_weight, epsilon)`.
10. SwiGLU: `ffn = (SiLU(f W_gate^T) * (f W_up^T)) W_down^T`.
11. Second residual and block result: `y = r + ffn`.
12. After the final block, apply final RMSNorm and multiply by the transposed token-embedding matrix to produce logits.

RMSNorm is `x * rsqrt(mean(x^2) + epsilon) * weight`. All reductions and stored intermediates are float32. The reference implementation must document if a host library internally changes reduction order.

### Synthetic storage contract

The synthetic manifest describes every matrix mathematically as `[out_features, in_features]` and stores it in C row-major order. Tensor artifacts record dtype, logical shape, byte strides, endianness, and SHA-256. No GGUF dimension convention participates in Profile A.

Weights are generated from a pinned algorithm and seed, then committed or retained as immutable artifacts. “Same seed” is not an adequate identity unless the generator implementation and version are also pinned.

## Profile B candidate — SmolLM2-135M

**DECIDED:** [`HuggingFaceTB/SmolLM2-135M`](https://huggingface.co/HuggingFaceTB/SmolLM2-135M) is the first real-model candidate for Phase 0. Candidate means research target, not supported model.

The following facts were verified on 2026-07-18 from Hugging Face revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2`:

| Property | Verified value |
|---|---:|
| Architecture/model type | `LlamaForCausalLM` / `llama` |
| License metadata | Apache-2.0 |
| Vocabulary | 49,152 |
| Hidden size | 576 |
| Intermediate size | 1,536 |
| Decoder layers | 30 |
| Query heads | 9 |
| KV heads | 3 |
| Head dimension | 64, derived exactly as `576 / 9` |
| Maximum positions | 8,192 |
| RMSNorm epsilon | `1e-5` |
| RoPE theta / scaling | `100000` / none |
| RoPE interleaving | false |
| Attention bias/dropout | false / `0.0` |
| Hidden activation | SiLU |
| Token embedding/output weights | tied |
| Source dtype metadata | bfloat16 |
| BOS/EOS/unknown token ID | 0 |
| Tokenizer class | `GPT2Tokenizer` |

The source configuration is not yet the OrcEngine execution contract. Phase 0 must still pin:

- source model, tokenizer, and converted GGUF SHA-256 values;
- converter repository revision and exact command;
- resulting GGUF version, tensor dtypes, names, shapes, and embedded tokenizer metadata;
- source-framework and GGUF tokenization agreement fixtures;
- applicable notices and artifact-distribution policy;
- float32 materialization and oracle capture procedure.

If conversion or tokenizer reconciliation introduces unexplained semantics, the candidate is rejected or replaced; OrcEngine does not bend Profile A to imitate an unresolved conversion artifact.

## Implementation-language decision

**DECIDED:** the oracle and engine use different roles:

- Python with NumPy and/or PyTorch for rapid, inspectable Phase 0 oracle construction;
- C++20 for the standalone OrcEngine core after the oracle gate;
- CUDA C++ and cuBLAS for a later GPU backend;
- a small C ABI and C# `SafeHandle` wrapper only after standalone correctness.

Rust remains a legitimate future alternative, but it offers no demonstrated advantage for the first TheOrc integration and would add another CUDA/interop decision now. Language choice does not alter model dimensions, GQA, RoPE, normalization, residual ordering, or tying rules.

## Change control

Any semantic change to Profile A creates a new profile ID and new oracle artifacts. It may not silently overwrite existing expected outputs. Profile B becomes accepted only when the requirements in [Phase 0 Reference Oracle](PHASE_0_REFERENCE_ORACLE.md) and [Phase 0 Acceptance Contract](PHASE_0_ACCEPTANCE.yaml) pass.
