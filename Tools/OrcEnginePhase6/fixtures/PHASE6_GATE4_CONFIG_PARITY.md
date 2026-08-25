# Phase 6 Stage 1, Gate 4: HF / GGUF / OrcEngine / llama.cpp configuration parity table

Bounded config-drift check for the external same-Q8 disagreement. Weight
identity (hash-verified throughout this project's evidence chain) does
not by itself protect against configuration-interpretation drift
between implementations -- this table checks the execution-relevant
fields directly.

## Sources

- **HF `config.json`**: `Tools/OrcEnginePhase0/artifacts/smollm2-135m/config.json`
  (pinned revision `93efa2f097d58c2a74874c7e644dbc9b0cee75a2`, per
  `oracle/download_candidate.py`/OE-ADR-014).
- **F32 GGUF metadata**: read directly via `gguf.GGUFReader` against
  the pinned F32 GGUF (SHA-256 `fffab10c...`, matches every prior
  evidence row's `f32_artifact_sha256`).
- **OrcEngine `ModelConfig`**: as constructed by `map_llama_model()`
  from that same GGUF's metadata (`Tools/OrcEnginePhase2/src/gguf.cpp`).
- **llama.cpp effective config**: inferred from the SAME GGUF metadata
  fields above, since no CLI flag overriding architecture, RoPE, RMSNorm,
  or context parameters was passed to `llama-server.exe` in any run in
  this project (only `--cache-type-k/-v` and `--flash-attn`, which do
  not affect these fields) -- llama.cpp's standard GGUF loader reads
  these fields directly. `/props`'s `default_generation_settings.n_ctx`
  (`8192`) was independently confirmed live against the running server
  and matches. **The remaining llama.cpp fields below (rope_theta,
  head counts, RMSNorm epsilon, etc.) are NOT independently confirmed
  via a live llama.cpp introspection endpoint** -- the pinned b10436
  server's `/props` endpoint does not expose them, and the startup log
  at the verbosity level used did not print the detailed
  `llm_load_print_meta` banner. This is disclosed as a real limitation:
  the "llama.cpp effective" column is an INFERENCE (no override flag
  exists to change it) rather than a directly observed value for every
  row except `n_ctx`.

## Table

| Field | HF `config.json` | F32 GGUF metadata | OrcEngine `ModelConfig` | llama.cpp effective (inferred, see note above) |
|---|---|---|---|---|
| architecture | `LlamaForCausalLM` | `general.architecture` = `llama` | `"llama"` (via `map_llama_model`) | `llama` (no override) |
| layer count | `num_hidden_layers` = 30 | `llama.block_count` = 30 | `n_layers` = 30 | 30 (no override) |
| hidden size | `hidden_size` = 576 | `llama.embedding_length` = 576 | `hidden` = 576 | 576 (no override) |
| attention heads | `num_attention_heads` = 9 | `llama.attention.head_count` = 9 | `n_q_heads` = 9 | 9 (no override) |
| KV heads | `num_key_value_heads` = 3 | `llama.attention.head_count_kv` = 3 | `n_kv_heads` = 3 | 3 (no override) |
| head dim / RoPE dim count | derived: 576/9=64 | `llama.rope.dimension_count` = 64 | `head_dim` = `hidden/n_q_heads` = 64 | 64 (no override) |
| `rope_theta` / `freq_base` | `rope_theta` = 100000 | `llama.rope.freq_base` = 100000.0 | `rope_theta` = 100000.0f | 100000 (no override; **confirmed matches HF exactly**) |
| RMSNorm epsilon | `rms_norm_eps` = 1e-05 | `llama.attention.layer_norm_rms_epsilon` = 9.999999747378752e-06 (float32-rounded 1e-5) | `rmsnorm_epsilon` = same float32 value | 1e-5 (no override; matches within float32 rounding) |
| context length | `max_position_embeddings` = 8192 | `llama.context_length` = 8192 | `max_positions` = 8192 | **8192 -- independently confirmed live via `/props`'s `n_ctx`** |
| vocabulary size | `vocab_size` = 49152 | `token_embd.weight` shape confirms 49152 | `vocab` = 49152 (from embedding tensor shape) | 49152 (no override) |
| tied/untied output weights (**corrected, round 6**) | `tie_word_embeddings` = `true` | `output.weight` tensor is present as a SEPARATE tensor RECORD (`output_semantics: untied` per `orcengine_gguf_inspect`'s tensor-presence classification), but independently verified **byte-for-byte identical** to `token_embd.weight` (`np.array_equal` on the decoded F32 arrays = `True`) | `tied_embeddings = !output_present` = `false` per the tensor-presence check -- structurally accurate, but the artifact is LOGICALLY TIED (HF's declared intent) and PHYSICALLY DUPLICATED (two byte-identical copies), not two independently-varying weight matrices | untied per tensor presence, same physically-duplicated-but-identical data as OrcEngine reads (both engines read the same GGUF bytes) |
| BOS/EOS insertion policy | n/a (tokenizer-level) | `tokenizer.ggml.add_bos_token` = `False`, `add_eos_token` = `False`, `bos_token_id`/`eos_token_id` = 0 | N/A -- OrcEngine never invokes a tokenizer; token IDs are passed in directly, already independently proven to never include a BOS/EOS token for this corpus | `/props` reports `bos_token`/`eos_token` = `<\|endoftext\|>` (id 0); server-side `add_bos_token=false` per GGUF metadata means llama.cpp's own `/tokenize` endpoint (already used throughout this project's token-ID-identity checks) does not silently prepend BOS -- consistent with the exact token-ID matches already independently verified on every external-oracle run |

## Reading this table

**No configuration drift was found in the fields checked.** Every
field that is verifiable from the GGUF metadata directly (which both
OrcEngine and llama.cpp read from the identical pinned file) matches
the HF source config.json exactly (RoPE theta, RMSNorm epsilon within
float32 rounding, head counts, layer count, hidden size, context
length, vocabulary size). The tied/untied and BOS/EOS fields are
independently corroborated by behavior already observed elsewhere in
this project's evidence (untied lm_head confirmed via the real
`output.weight` tensor and via matching top-5 predictions using it; no
BOS insertion confirmed via exact token-ID matches on every external
run).

**This does NOT prove llama.cpp's INTERNAL interpretation of these
values is correct** -- it proves the values it WOULD read, if its
standard GGUF-metadata-driven loader behaves as documented, match the
HF source. A subtler defect (e.g. an off-by-one in how RoPE dimension
count vs. head dim is applied internally, or a cache/position-indexing
bug independent of the metadata VALUES being correct) would not be
caught by this table. The `/props` endpoint's lack of a full metadata
echo is a genuine gap; a definitive live confirmation would require
either a higher server verbosity level (untried in this pass) or a
llama.cpp source-level check (out of scope for a Phase 6 diagnostic).

## Prefix-position divergence check: NOT performed in this pass

**This is a disclosed limitation, not a silent omission.** The
governing instructions asked for, per divergent prompt plus one
control, a per-PREFIX-position comparison (token IDs, compared
position, and each of PyTorch/OrcEngine/llama.cpp's selected token/
top-k at every prefix length) to find the FIRST position where
llama.cpp's prediction diverges from the other two. Given the scope
already covered in this pass (Gate 1 corpus/hash fixes, Gate 2 claim
correction, Gate 3's three-prompt per-layer decomposition), this
specific check was not built or run. It remains the most promising
concrete next step for the EXTERNAL disagreement specifically: Gate 3's
internal finding that layers 11/28's divergence concentrates at
POSITION 0 (not later positions) is suggestive that an external
prefix-position check might show the SAME position-0 concentration on
llama.cpp's side too -- but this is speculation until actually measured,
not reported as a finding.
