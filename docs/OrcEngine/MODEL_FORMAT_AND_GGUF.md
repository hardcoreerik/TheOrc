# Model Format and GGUF

## Position

GGUF is OrcEngine’s proposed first external model container, not OrcEngine’s internal architecture definition. The [GGUF specification](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md) describes an extensible typed metadata and tensor format designed for efficient loading and mmap use.

**DECIDED:** “GGUF support” will always be qualified by an explicit compatibility tuple.

## First supported tuple

To be selected in Phase 0:

- one GGUF structural version;
- little-endian first;
- one `general.architecture` value;
- one tokenizer model;
- F32 tensor data first, or one reviewed F16-to-F32 load conversion;
- no split files initially;
- no adapters or multimodal projector;
- one fixed set of required metadata keys and tensor names.

## Reader stages

The reader is two-pass and fail-closed. Pass one validates the entire structural envelope and produces no executable tensor views. Pass two builds a typed immutable `OrcModelManifest`, evaluates compatibility and resource requirements, and only then permits read-only mapped tensor views.

### 1. File envelope

- open read-only;
- obtain stable file size;
- validate minimum header length;
- read magic as explicit bytes;
- validate supported version and endianness;
- reject arithmetic overflow before calculating offsets.

### 2. Metadata table

- validate key length and UTF-8 policy;
- validate type tags;
- bound array and string lengths;
- reject duplicate required keys;
- preserve unknown well-formed keys for inspection while ignoring them semantically;
- cap total metadata bytes and entry counts.
- parse nested arrays with an explicit bounded stack; arrays may contain arrays under the official format;
- reject nesting depth, element count, string length, or cumulative decoded bytes above configured limits before allocation.

### 3. Tensor descriptors

- validate nonzero rank within supported maximum;
- validate every dimension and element-count multiplication;
- validate dtype/block-size divisibility;
- validate alignment and data offsets;
- prove each tensor extent lies within the file;
- reject overlapping extents unless the specification explicitly permits them;
- reject duplicate tensor names.
- interpret each tensor offset relative to the aligned tensor-data section, never relative to file byte zero;
- assume alignment 32 only when `general.alignment` is absent, as required by the specification;
- support a maximum tensor rank of four for the first tuple and reject larger ranks even if a future GGUF revision permits them.

### 4. Architecture binding

- require exact architecture identifier;
- map expected semantic weights to exact names;
- validate expected count and optional tied-output rule;
- validate dimensions against metadata and one another;
- report all missing/mismatched tensors together where safe.

### 5. Storage view

Only after validation may the loader create tensor views over mapped bytes. Views carry dtype, shape, strides/layout interpretation, byte length, and immutable storage lifetime.

## Metadata inventory for the first model

Expected categories include:

- general architecture, name, file type, and alignment;
- context length and embedding length;
- block/layer count;
- attention head and KV-head counts;
- feed-forward length;
- RMSNorm epsilon;
- RoPE dimension/base/scaling fields;
- tokenizer model, tokens, scores/types, merges as applicable;
- BOS, EOS, unknown, padding, and add-prefix flags;
- chat template only as prompt-layer metadata, not model math.

Exact keys remain UNKNOWN until the model is selected.

The selected candidate and its source configuration are recorded in [Phase 0 Architecture Profile](PHASE_0_ARCHITECTURE_PROFILE.md). The converted GGUF keys remain unknown until a pinned converter run produces a hashed manifest.

## Initial resource limits

The first compatibility tuple uses these conservative caps. They are compatibility limits, not claims about the maximum representable GGUF file:

| Resource | Initial cap |
|---|---:|
| File bytes | 4 GiB |
| Metadata entries | 100,000 |
| Tensor descriptors | 10,000 |
| Metadata-key bytes | 65,535, matching the specification maximum |
| One string value | 64 MiB |
| Array nesting depth | 8 |
| Elements in one array | 10,000,000 |
| Cumulative decoded metadata/manifest allocation | 512 MiB |
| Tensor rank | 4 |
| One dimension | 2,147,483,647 |

The loader may configure lower operational limits for a workload, never silently higher ones. A future larger-model tuple requires measured fixtures and a decision update. All size arithmetic uses checked unsigned 64-bit operations followed by checked conversion to platform allocation types; tensor byte extents must additionally fit inside the validated file. Phase 2 supports little-endian GGUF only and rejects any input whose byte order cannot be established as supported.

This is how OrcEngine can be better at GGUF ingestion without becoming incompatible: not by inventing `OrcGGUF`, but by producing a stable typed manifest, SHA-256 identities for source file/metadata/tensor descriptors/tokenizer/profile, precise unsupported reasons, pre-allocation memory estimates, and a conformance corpus of positive and hostile fixtures.

## Tensor layout rule

The descriptor’s dimension order must be tested, not inferred from printed mathematical notation. The [llama.cpp model-development guide](https://github.com/ggml-org/llama.cpp/blob/master/docs/development/HOWTO-add-model.md) explicitly warns that GGML dimensions are typically reversed relative to PyTorch dimensions.

For each projection:

1. record source checkpoint shape;
2. record converter mapping;
3. record GGUF descriptor dimensions;
4. construct a small known input;
5. compare the resulting multiplication to the oracle;
6. document the adopted logical view.

## Memory mapping

Mapping reduces copies but expands lifetime and failure concerns:

- the model owns the mapping;
- weight views cannot outlive it;
- file mutation after validation is unsupported;
- network/removable files may have different failure behavior;
- mapped length and offsets remain size-checked on every constructed view;
- optional page-fault warming belongs to benchmarking, not baseline correctness.

## Inspector output

`orc-gguf-inspect` should emit human-readable and JSON forms containing:

- format/version/endian/alignment;
- file hash and size;
- architecture and tokenizer summary;
- model dimensions;
- tensor name, dtype, dimensions, bytes, and offset;
- recognized/ignored metadata keys;
- compatibility verdict with exact unsupported reasons;
- estimated weight bytes by dtype.

Inspection must not claim the model is executable merely because parsing succeeded.

## Malformed-input suite

Fixtures cover bad magic, unsupported version, truncation at every structural boundary, huge counts, integer overflow, invalid type, invalid UTF-8 policy, duplicate key/name, zero dimensions, unsupported dtype, misalignment, offset before data, extent past EOF, overlapping tensors, missing required metadata, inconsistent dimensions, and decompression-style resource abuse if future encodings add it.

## Existing TheOrc reader

`OrchestratorIDE/Core/Runtime/GgufMetadataReader.cs` is a defensive metadata subset used for VRAM estimation. It is not a full OrcEngine loader and should not be silently promoted into one. Its tests and failure posture are useful repository evidence; code reuse requires a deliberate boundary decision.

## Compatibility changes

Adding a GGUF version, architecture, tokenizer, or dtype requires:

- new strict validation rules;
- positive and negative fixtures;
- oracle evidence;
- security review of parser surface;
- compatibility-table update;
- decision-log entry.
