# TheOrc — Safetensors Format Spec

> **Status: 🔲 Planned.** Parsing spec for the Safetensors Engine Spike
> ([SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md)). The rigor bar is set by the
> existing `OrchestratorIDE/Core/Runtime/GgufMetadataReader.cs`: header-only reads, sized
> skips, byte-exact size prediction, never throws to callers, memoized by (path, size,
> mtime). Everything below must be verified against real files during Phase 1 — where this
> doc and a real HuggingFace file disagree, the file wins and this doc gets corrected.

---

## 1. File layout (single shard)

A `.safetensors` file is exactly three regions, in order:

| Region | Size | Contents |
|---|---|---|
| Header length | 8 bytes | Unsigned 64-bit **little-endian** integer `N` = byte length of the JSON header |
| JSON header | `N` bytes | UTF-8 JSON object (may be padded with trailing ASCII spaces `0x20` to align the byte buffer — the padding is *inside* the `N` count) |
| Byte buffer | file length − 8 − `N` | Raw tensor data, concatenated |

There is no magic number. Identification is by extension plus successful header validation.

### JSON header schema

The header is a single JSON object. Every key except `__metadata__` names a tensor:

```json
{
  "__metadata__": { "format": "pt" },
  "model.embed_tokens.weight": {
    "dtype": "BF16",
    "shape": [128256, 2048],
    "data_offsets": [0, 525336576]
  }
}
```

| Field | Type | Rules |
|---|---|---|
| `dtype` | string | One of the enumeration in §2. Reject unknown strings — do not guess a size |
| `shape` | array of non-negative integers | May be `[]` (rank-0 scalar, 1 element). Element count = product of dims; product of `[]` is 1 |
| `data_offsets` | `[begin, end)` | Byte offsets **relative to the start of the byte buffer** (i.e. absolute offset = `8 + N + begin`). `end − begin` must equal element count × dtype size exactly |
| `__metadata__` | object, string→string values only | Optional; free-form provenance. Values that are not strings are a validation error per the reference implementation |

### Validation rules (all are hard failures → `null`/error result, never a partial parse)

| # | Rule |
|---|---|
| V-1 | `N` ≤ 100,000,000 (100 MB, the reference implementation's `MAX_HEADER_SIZE`) and `8 + N` ≤ file length |
| V-2 | Header parses as a single JSON object; **duplicate keys are an error** (a duplicate-tolerant parser silently drops a tensor — `System.Text.Json` must be driven in a mode that detects this, e.g. a manual `Utf8JsonReader` walk collecting keys into a set) |
| V-3 | Every `data_offsets` pair satisfies `0 ≤ begin ≤ end ≤ buffer length` |
| V-4 | `end − begin == dtypeSize(dtype) × product(shape)` for every tensor (guards both truncated files and shape/dtype lies) |
| V-5 | Tensor spans do not overlap one another (sort by `begin`, check pairwise) |
| V-6 | The spans plus padding exactly cover the byte buffer — a gap means either a corrupt file or a format revision this parser doesn't understand; refuse rather than skip |
| V-7 | Tensor names are non-empty and unique (follows from V-2) |
| V-8 | Element count × dtype size computed in `checked` arithmetic — a hostile header must overflow loudly, exactly as `OrcScheduler.EstimateRequiredBytes` does with corrupt GGUF headers |

### Error taxonomy

Mirroring `GgufMetadataReader`'s contract (never throw to callers; the caller decides policy):

| Error class | Examples | Parser behavior |
|---|---|---|
| `NotSafetensors` | bad extension, `N` insane, header not JSON | return failure result naming the first failed rule |
| `Malformed` | V-2..V-8 violations | failure result with rule ID + offending tensor name |
| `UnsupportedDtype` | valid file, dtype outside §2 | failure result listing the dtype(s) — this is the "coverage wall" error message the user actually sees, so it must name the dtype and the file |
| `Io` | share violation, disappearing file | failure result wrapping the exception message chain (full chain, per `LLamaSharpRuntime.FormatLoadFailure`'s lesson — never truncate at one level) |

---

## 2. Dtype enumeration and .NET mapping

| safetensors dtype | Bytes/elem | .NET type | Spike support | Notes |
|---|---|---|---|---|
| `F32` | 4 | `float` | ✅ read + compute | CPU reference path computes in F32 |
| `F16` | 2 | `System.Half` | ✅ read + compute | The spike's GPU compute dtype |
| `BF16` | 2 | none — `ushort` bit pattern | ✅ read, convert | Llama 3.2 official weights ship as BF16. Convert to `float` by `(uint)bits << 16` reinterpret; to F16 via float round-trip. Conversion policy: §5 |
| `F64` | 8 | `double` | 🔲 read-only (validate, refuse compute) | Rare in LLM checkpoints |
| `I64`/`I32`/`I16`/`I8`/`U8`/`BOOL` | 8/4/2/1/1/1 | `long`/`int`/`short`/`sbyte`/`byte`/`byte` | 🔲 read-only | Not used by Llama weights; parser must still size them correctly for V-4 |
| `F8_E4M3` / `F8_E5M2` | 1 | none | ❌ `UnsupportedDtype` | Named explicitly so the error message is precise, not "unknown" |

---

## 3. Multi-shard repos — `model.safetensors.index.json`

Checkpoints above ~5 GB ship sharded. The index file:

```json
{
  "metadata": { "total_size": 2471645608 },
  "weight_map": {
    "model.embed_tokens.weight": "model-00001-of-00002.safetensors",
    "model.layers.0.self_attn.q_proj.weight": "model-00001-of-00002.safetensors"
  }
}
```

Loader resolution order for a repo directory:

1. `model.safetensors.index.json` present → sharded load. Every shard named in
   `weight_map` must exist in the same directory; every tensor the model spec needs must
   appear in `weight_map` exactly once.
2. Else `model.safetensors` present → single-shard load.
3. Else → failure result naming what was looked for (mirror the depot's "print every
   candidate's verdict" preflight discipline).

Cross-shard validation: `metadata.total_size`, when present, must equal the sum of all
tensor byte spans across shards; a mismatch is `Malformed`. Llama 3.2 1B ships single-shard;
the sharded path is still required in Phase 1 because G-1
([SAFETENSORS_ENGINE_SPIKE.md](SAFETENSORS_ENGINE_SPIKE.md) §5) demands real multi-shard
handling — validate it against a real sharded repo (e.g. any 7B–8B fp16 checkpoint already
on the dev box; parse-only, no inference).

---

## 4. Memory-mapped read strategy

- Open each shard with `MemoryMappedFile.CreateFromFile(..., MemoryMappedFileAccess.Read)`
  over a `FileStream` opened `FileShare.Read` (same sharing discipline as
  `GgufMetadataReader.ReadCore`).
- Each tensor is exposed as a `ReadOnlySpan<byte>` / `ReadOnlyMemory<byte>` view over the
  map — **zero copy on the host**. The one unavoidable copy is host→device upload.
- The header (≤100 MB, typically ≤10 MB) is read via a plain buffered read, not the map —
  simpler, and the map's lifetime then covers only tensor data.
- Alignment: the format does not guarantee tensor `begin` offsets are aligned beyond the
  writer's 8-byte header padding convention. Kernels must not assume alignment of mapped
  spans; the host→device upload copies into properly aligned device buffers anyway, so this
  only constrains any future in-place CPU compute (Phase 2 CPU path copies BF16→F32 on load,
  which re-aligns as a side effect).
- Parse results are memoized keyed by (path, size, mtime-utc) exactly as
  `GgufMetadataReader.s_cache` does, because admission-style callers probe repeatedly.

### Byte-exact size prediction

The parser must expose the same style of prediction `GgufMetadataReader` feeds
`OrcScheduler`, computed from the header alone (no tensor data read):

| Quantity | Formula |
|---|---|
| Weight bytes (as stored) | Σ over tensors of `end − begin` |
| Weight bytes (as computed, fp16) | Σ over tensors of elementCount × 2 (differs from stored when BF16→F16 conversion happens on upload) |
| KV cache bytes | `blockCount × contextLength × headCountKv × (keyLength + valueLength) × 2` — the identical, spike-validated formula `OrcScheduler.EstimateRequiredBytes` uses, with the parameters now sourced from `config.json` (§5) instead of GGUF KV pairs |

Acceptance: predicted VRAM vs measured allocation is compared in the benchmark report; the
existing lane's precedent is file-size-within-~0.5%-of-model-buffer (see `OrcScheduler`'s
doc comment), and the spike must report its own delta honestly rather than claim the
precedent's number.

---

## 5. `config.json` → runtime parameter mapping

`config.json` is the architecture's source of truth (safetensors itself carries no
architecture metadata — a deliberate contrast with GGUF, and the reason this section
exists). Required keys for the spike, with the reference model's published values —
**verify against the actual downloaded file in Phase 1; the table is a checklist, not a
substitute for reading the file:**

| `config.json` key | Engine parameter | Llama 3.2 1B expected | Notes |
|---|---|---|---|
| `architectures[0]` | must equal `"LlamaForCausalLM"` | `LlamaForCausalLM` | Anything else → refuse (single-architecture spike) |
| `hidden_size` | `d_model` | 2048 | |
| `num_hidden_layers` | `n_layers` | 16 | = GGUF `block_count` |
| `num_attention_heads` | `n_heads` | 32 | |
| `num_key_value_heads` | `n_kv_heads` | 8 | GQA group size = `n_heads / n_kv_heads` = 4 |
| `head_dim` (or `hidden_size / num_attention_heads` when absent) | `d_head` | 64 | llama.cpp's own fallback convention, mirrored from `GgufMetadataReader` |
| `intermediate_size` | `d_ff` | 8192 | |
| `vocab_size` | `n_vocab` | 128256 | |
| `rms_norm_eps` | `ε` | 1e-5 | |
| `rope_theta` | `θ_base` | 500000.0 | |
| `rope_scaling` | Llama-3 frequency scaling block | `{"rope_type": "llama3", "factor": 32.0, "low_freq_factor": 1.0, "high_freq_factor": 4.0, "original_max_position_embeddings": 8192}` | **Must be implemented, not ignored** — see [NATIVE_ENGINE_FORWARD_PASS.md](NATIVE_ENGINE_FORWARD_PASS.md) §4. Ignoring it silently corrupts long-context outputs while short prompts still look fine — exactly the class of silent wrongness the parity harness exists to catch |
| `tie_word_embeddings` | LM head = embedding matrix | `true` | 1B/3B tie; larger Llamas don't. Both paths must work off this flag, not off tensor presence alone |
| `max_position_embeddings` | context ceiling | 131072 | Spike runs ≤ 4096; ceiling recorded for validation only |
| `torch_dtype` | stored dtype hint | `bfloat16` | Cross-check against actual header dtypes; mismatch is a warning, not an error (headers win) |
| `bos_token_id` / `eos_token_id` | special tokens | 128000 / [128001, 128008, 128009] | `eos_token_id` may be an int **or** a list — parse both shapes |

Unknown extra keys are ignored (forward compatibility); *missing* required keys are a
refusal with the key named.

### BF16 → F16 conversion policy

Llama 3.2 ships BF16; the spike computes in F16 (and F32 on the CPU reference path).
BF16→F16 is lossy in the low-magnitude direction (BF16 has F32's exponent range, F16
doesn't) and can overflow for |x| > 65504. Policy:

- Conversion happens once, at load, via F32 intermediate with round-to-nearest-even.
- Overflow to ±∞ during conversion is a **counted, reported** event per tensor. Expected
  count for Llama-3.2-class weights: 0 (weights are small-magnitude); a non-zero count is
  an early-warning signal (see [RISK_REGISTER.md](RISK_REGISTER.md) R-08), not a silent
  saturation.
- The CPU reference path converts BF16→F32 directly (exact, no F16 stop) so parity
  comparisons can distinguish "conversion loss" from "kernel bug".

---

## 6. Tokenizer handling

HF repos carry `tokenizer.json` (fast-tokenizer BPE definition) + `tokenizer_config.json`.
A full, correct BPE implementation (with Llama-3's regex pre-tokenizer) is real work with
real edge cases and is **not** on the spike's critical path.

**Open Decision OD-F1 — tokenizer strategy.**

| Option | Trade-off |
|---|---|
| **A. Pre-tokenized prompt set (recommended)** | The benchmark prompt set is tokenized **once, offline**, by HF `transformers`' own tokenizer, and stored as token-ID arrays in the prompt-set fixture ([SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md](SAFETENSORS_VS_GGUF_BENCHMARK_PLAN.md) §4). All three execution paths consume identical IDs. Removes an entire class of parity confounders (llama.cpp's tokenizer and HF's disagree on some byte sequences — a known, documented ecosystem issue) and an entire week of implementation. Cost: the spike engine cannot take free-text input; acceptable for a spike whose output is numbers, not chat |
| B. Minimal in-engine BPE | Free-text input works; costs 2–3 days and introduces a tokenizer-parity risk orthogonal to the question the spike is asking |
| C. Shell out to a Python tokenizer at runtime | Adds a runtime Python dependency — collides with the dependency-light differentiator and the project's no-Ollama-style external-dependency discipline |

**Recommendation: A.** Resolution criterion: none needed — A is strictly better for the
spike's question; B becomes Tier-A work if the spike GOes. Detokenization for the greedy
string-match metric similarly uses the stored HF `id → token string` table from
`tokenizer.json` (a flat lookup, not BPE — trivially implementable and shared by all paths).

---

## 7. Type and method surface (implementation target)

All spike source files carry the standard headers:

```csharp
// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
```

```csharp
namespace TheOrc.SafetensorsSpike.Format;

/// <summary>Result of parsing one shard's header. Never constructed for an invalid file.</summary>
public sealed record SafetensorsHeader(
    IReadOnlyDictionary<string, SafetensorsTensorInfo> Tensors,
    IReadOnlyDictionary<string, string> Metadata,
    long ByteBufferOffset,      // 8 + N
    long ByteBufferLength);

public sealed record SafetensorsTensorInfo(
    string Name,
    SafetensorsDtype Dtype,
    IReadOnlyList<long> Shape,
    long Begin,                 // relative to byte buffer
    long End)
{
    public long ElementCount { get; }   // checked product of Shape; [] => 1
    public long ByteLength => End - Begin;
}

public enum SafetensorsDtype { F64, F32, F16, BF16, I64, I32, I16, I8, U8, Bool, F8E4M3, F8E5M2 }

/// <summary>Never throws: any failure returns a result carrying the error class + detail.</summary>
public static class SafetensorsHeaderReader
{
    public static SafetensorsParseResult TryRead(string path);
}

public sealed record SafetensorsParseResult(
    SafetensorsHeader? Header,          // null on failure
    SafetensorsParseError? Error);      // null on success

public sealed record SafetensorsParseError(
    SafetensorsErrorClass Class,        // NotSafetensors | Malformed | UnsupportedDtype | Io
    string Detail);                     // rule ID + tensor name / dtype / exception chain

/// <summary>Repo-level loader: resolves index.json vs single-shard, validates cross-shard.</summary>
public sealed class SafetensorsRepo : IDisposable
{
    public static SafetensorsRepoResult TryOpen(string directory);
    public LlamaConfig Config { get; }                       // parsed config.json (§5)
    public ReadOnlyMemory<byte> GetTensorData(string name);  // mmap-backed, zero-copy
    public SafetensorsTensorInfo GetTensorInfo(string name);
    public SafetensorsSizePrediction PredictSize(int contextLength); // §4 formulas
}
```

### Required tests (spike project, xUnit, `Method_Scenario_Expectation` naming)

| Test | Covers |
|---|---|
| `TryRead_ValidSingleTensorFile_ParsesHeaderAndOffsets` | happy path, synthetic fixture built in-test |
| `TryRead_HeaderLengthExceedsFile_ReturnsNotSafetensors` | V-1 |
| `TryRead_DuplicateTensorKey_ReturnsMalformed` | V-2 |
| `TryRead_OffsetsOverlap_ReturnsMalformed` | V-5 |
| `TryRead_ByteLengthMismatchesShapeDtype_ReturnsMalformed` | V-4 |
| `TryRead_ByteBufferGap_ReturnsMalformed` | V-6 |
| `TryRead_UnknownDtype_ReturnsUnsupportedDtypeNamingIt` | §2, error message quality |
| `TryRead_ShapeProductOverflows_ReturnsMalformedNotWrapped` | V-8, checked arithmetic |
| `TryOpen_IndexJsonMissingShard_ReturnsError` | §3 |
| `TryOpen_RealLlama32Repo_TotalBytesMatchHeaderSum` | integration, real downloaded file |
| `PredictSize_ReferenceModel_MatchesConfigDerivedFormula` | §4 |
| `Bf16ToF32_KnownBitPatterns_ExactExpectedFloats` | §5, incl. subnormals, ±∞, NaN |
