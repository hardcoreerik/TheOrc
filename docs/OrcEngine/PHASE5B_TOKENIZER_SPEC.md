# Phase 5B: Tokenizer / Text-Token Boundary Reference

Status: **DRAFT FOR MAINTAINER REVIEW — NO IMPLEMENTATION AUTHORIZED**

Branch: `feat/orcengine-phase5b-tokenizer`, worktree
`F:\Ai\OrchestratorIDE-phase5b-tokenizer`, forked from `orcengine-phase5a-freeze`.

Prepared: 2026-08-20 America/Los_Angeles, specification-only pass.

## 1. Authority and baseline

Phase 5B begins from `orcengine-phase5a-freeze` (annotated tag). The
exact base commit is `db3e5f38b37e6b342e28e6d208737ab8288b0c05`.

Phase 1 through Phase 5A behavior is **frozen** and out of scope for
this phase. Phase 5B may add a text/token boundary later, but may
**not** redesign tensor execution, model loading, weight residency, KV
caching, or numerical kernels. Nothing in this document proposes such a
change, and no implementation is authorized by this document.

## 2. Problem statement

The frozen engine (Phases 1 through 5A) currently accepts explicit
token IDs only. No text ever enters or leaves OrcEngine's own code —
every existing test and tool supplies token IDs directly.

Phase 5B is responsible for proving the two boundaries the frozen
engine does not yet have:

```
raw text/bytes -> tokenizer policy -> token IDs -> frozen OrcEngine execution
```

and the inverse:

```
generated token IDs -> tokenizer decoder/byte accumulator -> valid output chunks
```

Chat message templating and tool formatting remain outside the tensor
engine and **outside this phase**, per
`docs/OrcEngine/TOKENIZER_AND_PROMPT_PIPELINE.md`'s existing layer
separation, unless an already-frozen requirement explicitly requires an
identity check (none currently does).

## 3. Exact compatibility tuple

One compatibility tuple only. Every value below is confirmed local
evidence from this reconnaissance pass, not inference or convention:

| Field | Confirmed value | Source |
|---|---|---|
| Model | `HuggingFaceTB/SmolLM2-135M` | `PHASE_0_ACCEPTANCE.yaml` |
| Pinned revision | `93efa2f097d58c2a74874c7e644dbc9b0cee75a2` | `PHASE_0_ACCEPTANCE.yaml` |
| Tokenizer family | GPT-2-style byte-level BPE (`tokenizer_class: GPT2Tokenizer`, `model.type: BPE`) | `tokenizer_config.json`, `tokenizer.json` |
| GGUF tokenizer model | `tokenizer.ggml.model = "gpt2"` | real converted GGUF, read via `gguf-py` |
| GGUF pre-tokenizer profile | `tokenizer.ggml.pre = "smollm"` | real converted GGUF (the fix `PHASE_0_ACCEPTANCE.yaml`'s `tokenizer_dual_source_agreement` entry records) |
| Vocabulary size | 49,152 tokens | `tokenizer.json` `model.vocab`; GGUF `tokenizer.ggml.tokens` array, same length |
| Merge table size | 48,900 merge rules | `tokenizer.json` `model.merges`; GGUF `tokenizer.ggml.merges` array, same length |
| Normalization | **none** (`tokenizer.json` `normalizer: null`) | `tokenizer.json` |
| Pre-tokenization | `Sequence` composite, byte-level | `tokenizer.json` `pre_tokenizer.type` |
| Decoder | `ByteLevel` | `tokenizer.json` `decoder.type` |
| Special/control tokens | 17 tokens, IDs 0-16, all `special: true` in `tokenizer.json`, all GGUF `token_type = 3` (CONTROL) | `tokenizer.json` `added_tokens`; GGUF `tokenizer.ggml.token_type` distribution: `{1: 49135, 3: 17}` |
| Ordinary vocabulary token type | GGUF `token_type = 1` (NORMAL), 49,135 tokens | GGUF `tokenizer.ggml.token_type` |
| BOS token ID | `0` (`<\|endoftext\|>`) | GGUF `tokenizer.ggml.bos_token_id` |
| EOS token ID | `0` (`<\|endoftext\|>`) | GGUF `tokenizer.ggml.eos_token_id` |
| UNK token | `<\|endoftext\|>` per `tokenizer_config.json`'s `unk_token` field, but **no `tokenizer.ggml.unknown_token_id` key exists in the GGUF at all** | `tokenizer_config.json`; GGUF metadata scan (absent key, confirmed by exhaustive `tokenizer.*` field listing) |
| `add_bos_token` | `false` | GGUF `tokenizer.ggml.add_bos_token` |
| `add_eos_token` | `false` | GGUF `tokenizer.ggml.add_eos_token` |
| `add_prefix_space` | `false` | `tokenizer_config.json` |
| `clean_up_tokenization_spaces` | `false` | `tokenizer_config.json` |
| Padding token | not present in `tokenizer_config.json` or GGUF metadata | targeted key search, both sources |
| Primary test oracle | `tokenizers` library, version `0.22.2` (pinned) | `Tools/OrcEnginePhase0/requirements.txt` |
| Secondary independent oracle | llama.cpp reading the converted GGUF's `tokenizer.ggml.*` metadata | `PHASE_0_ACCEPTANCE.yaml`'s `tokenizer_dual_source_agreement` entry |

**Architectural fact this tuple implies, not previously stated
explicitly in project docs:** because the vocabulary uses GPT-2 byte-
level pre-tokenization/decoding (every one of the 256 possible byte
values maps to a printable-Unicode surrogate before BPE merging) and
there is no GGUF `unknown_token_id` and no `token_type` value 2
(UNKNOWN) or 6 (BYTE) present anywhere in the 49,152-entry
`tokenizer.ggml.token_type` array, **every possible input byte sequence
is representable by this vocabulary** — there is no vocabulary-level
"unknown token" fallback path to design for encode. `unk_token` in
`tokenizer_config.json` names `<|endoftext|>` only because the GPT-2
tokenizer format requires *some* value for that field, not because it
is ever actually produced by encoding arbitrary bytes. This is
DERIVED from the confirmed metadata above, not independently verified
by running the tokenizer against adversarial byte input — see Decision
Register item 6 and Section 5's "Unknown-token behavior" row.

The native runtime **must not** depend on Python `tokenizers` or
llama.cpp merely to perform tokenization. Those are test/review
oracles only, exactly as `PHASE_0_ACCEPTANCE.yaml`'s existing dual-
source-agreement evidence already establishes the pattern for.

## 4. Minimal future architecture

The smallest implementation shape that could prove the hypothesis, if
and when separately authorized:

- **One concrete native C++ tokenizer implementation** for this one
  pinned profile (GPT-2 byte-level BPE, `pre="smollm"`). Not an
  interface with one implementation, not a factory.
- **Existing C++/standard-library facilities** — no new external
  dependency has been demonstrated necessary by this reconnaissance
  pass.
- **Reuse the existing GGUF metadata reader.** `Tools/OrcEnginePhase2/
  include/orcengine/gguf.hpp`'s `GgufArtifact`/`GgufValue` (with
  `GgufValueType::Array` already supporting `std::vector<GgufValue>`)
  can already represent the `tokenizer.ggml.tokens`/`merges`/
  `token_type` arrays this profile requires. **No native tokenizer or
  GGUF-tokenizer-parsing code exists anywhere in the C++ tree today**
  (confirmed by an exhaustive `tokeniz`-pattern search across
  `Tools/OrcEnginePhase1` through `Tools/OrcEnginePhase5A`'s `.cpp`/
  `.hpp` files — the sole match was an unrelated comment). A future
  implementation must extend `gguf.hpp`'s typed-accessor pattern
  (`require_metadata`/`metadata_u64`/`metadata_f64`/`metadata_string`)
  with array-typed accessors for the tokenizer keys, not duplicate GGUF
  parsing.
- **Immutable tokenizer tables** — vocabulary and merge ranks loaded
  once at construction, never mutated.
- **A small, explicit encode/decode surface** — not a general-purpose
  configuration layer.
- **Clear error results at trust boundaries** — GGUF metadata is
  externally-sourced input and must fail closed (Section 8).

Explicitly not required, and not to be introduced without demonstrated
necessity: an interface with only one implementation, a tokenizer
factory, dynamic plugin discovery, multiple tokenizer algorithms, a new
external dependency, or speculative cache layers.

## 5. Required encode semantics

The following behaviors must eventually be proven against the pinned
compatibility tuple before implementation is accepted. No token ID may
be guessed or hard-coded without proving it against the pinned
tokenizer metadata.

| Behavior | Status |
|---|---|
| Raw UTF-8 input | Proposed Phase 5B requirement |
| Normalization | Proposed Phase 5B requirement — **none**, confirmed (`normalizer: null`) |
| Pre-tokenization | Proposed Phase 5B requirement — `Sequence`/byte-level, confirmed |
| Byte mapping / byte fallback | Proposed Phase 5B requirement — GPT-2 byte-to-Unicode surrogate mapping (`ByteLevel`), confirmed present; see Section 3's architectural note |
| Vocabulary lookup | Proposed Phase 5B requirement |
| Merge rank/order | Proposed Phase 5B requirement — 48,900 ranked merges, exact order not yet re-verified locally against the GGUF array ordering (previously demonstrated only at the Python-oracle level per `tokenizer_dual_source_agreement`) |
| Unknown-token behavior | Behavior that remains undecided at the policy level, though the underlying fact (no unknown-token path exists for this vocabulary) is derived from confirmed metadata — see Decision Register item 6 |
| `add_prefix_space` | Proposed Phase 5B requirement — `false`, confirmed |
| BOS insertion | Proposed Phase 5B requirement — default `false` (`add_bos_token`), confirmed; see Decision Register item 3 |
| EOS insertion | Proposed Phase 5B requirement — default `false` (`add_eos_token`), confirmed; see Decision Register item 4 |
| Empty input | Behavior that will require future native implementation proof — no existing fixture confirmed for this exact case at time of writing (the 20-fixture corpus's "empty input" category exists per its README but its exact result was not re-verified in this reconnaissance pass) |
| Repeated spaces | Previously demonstrated in the 20-fixture golden corpus ("leading/trailing/repeated whitespace" category) |
| Leading and trailing spaces | Previously demonstrated in the 20-fixture golden corpus |
| Newlines and tabs | Previously demonstrated in the 20-fixture golden corpus |
| ASCII punctuation | Previously demonstrated (`tokenizer_dual_source_agreement`'s "punctuation" fixture) |
| Latin text (non-ASCII) | Previously demonstrated (`tokenizer_dual_source_agreement`'s "café résumé" fixture; golden corpus's "non-ASCII Latin" category) |
| CJK text | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Emoji | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Combining characters | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Embedded NUL (if supported) | Golden corpus category exists ("embedded NUL byte if supported by API"); whether the pinned `tokenizers` 0.22.2 API supports NUL bytes was not re-verified this pass — behavior that will require future native implementation proof |
| Invalid UTF-8 | Behavior that remains undecided — no existing fixture found for this case; see Decision Register item 6 |
| Literal text resembling a special token | Previously demonstrated — the golden corpus's "text resembling special tokens" fixtures, and the specific `<\|endoftext\|>`-substring finding in Section 6 |
| Recognition of allowed-control special tokens | Previously demonstrated at the fault-injection level (`tokenizer_special_token_error`: mislabeling `<\|im_start\|>` CONTROL→NORMAL in GGUF `token_type` diverges `"<\|im_start\|>user"` tokenization from `[1, 4093]` to an 8-token shattered sequence) |
| Distinction: ordinary text vs. explicitly authorized special-token input | Proposed Phase 5B requirement — not yet a settled policy; see Section 7 and Decision Register items 1-2 |

## 6. Required decode semantics

| Behavior | Status |
|---|---|
| Token IDs to raw decoded bytes | Proposed Phase 5B requirement |
| Ordinary vocabulary tokens | Previously demonstrated at the Python-oracle level |
| Control/special tokens | Proposed Phase 5B requirement, directly informed by the finding below |
| `skip_special_tokens=true` vs. `false` | **Previously demonstrated, with a real documented divergence**: under the `tokenizers` library's *default* `skip_special_tokens=True`, a fixture containing the literal substring `<\|endoftext\|>` loses that substring on decode, because it is token ID 0 — the model's actual `<\|endoftext\|>` control token — and gets silently dropped. Re-decoding the same fixture with `skip_special_tokens=False` round-trips exactly. Source: `Tools/OrcEnginePhase0/phase3_prep/tokenizer_golden_fixtures.py` module docstring and `phase3_prep/README.md`, both dated 2026-08-15. This specification does **not** hide that result — Section 7 and Decision Register item 5 require an explicit decode policy exactly because of it. |
| Unknown or invalid token IDs | Behavior that remains undecided — see Decision Register item 8 |
| Empty token sequences | Behavior that will require future native implementation proof |
| Byte fragments split across tokens | Golden corpus category exists ("tokens that split a multibyte UTF-8 code point"); native streaming-accumulator behavior is a proposed Phase 5B requirement, not yet implemented anywhere |
| Partial UTF-8 sequences during streaming | Proposed Phase 5B requirement — no existing native implementation to draw from; `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "Streaming decode" section states the general principle ("maintain a byte accumulator and emit only valid complete sequences") but this has never been implemented or tested against this specific tokenizer |
| Bytes emitted to caller (timing) | Proposed Phase 5B requirement; see Decision Register item 7 |
| End-of-stream with incomplete UTF-8 | Behavior that remains undecided; see Decision Register item 7 |
| Reject vs. replace invalid sequences | Behavior that remains undecided; see Decision Register item 6 |
| Round-trip comparison basis (Unicode string / raw bytes / both) | Behavior that remains undecided; see Decision Register item 10 |

## 7. Special-token policy

This is a first-class, explicit contract per this document's own
requirement, not an incidental detail.

- **BOS token:** ID 0 (`<\|endoftext\|>`), GGUF `add_bos_token=false` —
  confirmed. Default behavior: do not insert. See Decision Register
  item 3.
- **EOS token:** ID 0 (`<\|endoftext\|>`, same token as BOS), GGUF
  `add_eos_token=false` — confirmed. Default behavior: do not insert.
  See Decision Register item 4.
- **UNK token:** named as `<\|endoftext\|>` in `tokenizer_config.json`,
  but no GGUF `unknown_token_id` field exists and no vocabulary entry
  has `token_type` UNKNOWN(2) — per Section 3's architectural note,
  this vocabulary has no practical unknown-token path for encode. Not
  yet proven adversarially (Decision Register item 6).
- **`<\|endoftext\|>` (ID 0):** the single token serving as BOS, EOS,
  and (nominally) UNK simultaneously. Also the specific token behind
  the documented `skip_special_tokens` divergence in Section 6.
- **`<\|im_start\|>` (ID 1) / `<\|im_end\|>` (ID 2):** chat-boundary
  control tokens. `<\|im_start\|>user` tokenizes to `[1, 4093]` under
  correct metadata (`tokenizer_special_token_error` fault-injection
  evidence, `PHASE_0_ACCEPTANCE.yaml`).
- **Other 14 pinned control tokens:** dataset-formatting tokens
  (`<repo_name>`, `<file_sep>`, `<gh_stars>`, `<jupyter_*>`, etc.) —
  confirmed present in both `tokenizer.json`'s `added_tokens` (all
  `special: true`) and the GGUF's `token_type` array (all value 3,
  CONTROL). No fixture evidence found exercising these 14 specifically
  beyond `<\|im_start\|>`/`<\|im_end\|>`/`<\|endoftext\|>` — behavior
  that will require future native implementation proof, though the
  mechanism (uniform CONTROL classification) is the same as the three
  tokens that do have direct evidence.
- **Literal special-token-looking text:** previously demonstrated by
  the golden corpus's dedicated fixture category and the `skip_special_
  tokens` finding above — this is not hypothetical, it is a confirmed
  local finding.
- **Allowed-special vs. disallowed-special encoding, and whether
  default user text may activate control tokens:** behavior that
  remains undecided at the policy level. See Decision Register items 1
  and 2. A safe-default recommendation, not a silent choice, is given
  there.
- **Decoding with and without special-token removal:** directly
  informed by the `skip_special_tokens` finding; the policy itself
  remains a labeled maintainer decision (Decision Register item 5), not
  silently selected by this document.

## 8. GGUF metadata contract and rejection behavior

Exact metadata fields required to construct this tokenizer, confirmed
present in the real converted GGUF by this reconnaissance pass:

`tokenizer.ggml.model` (string, `"gpt2"`), `tokenizer.ggml.pre`
(string, `"smollm"`), `tokenizer.ggml.tokens` (array, 49,152 entries),
`tokenizer.ggml.merges` (array, 48,900 entries), `tokenizer.ggml.
token_type` (array, 49,152 int32 entries, values observed: 1 and 3
only), `tokenizer.ggml.bos_token_id` (uint, `0`), `tokenizer.ggml.
eos_token_id` (uint, `0`), `tokenizer.ggml.add_bos_token` (bool,
`false`), `tokenizer.ggml.add_eos_token` (bool, `false`).

Fail-closed behavior must eventually be proven for at least:

- Missing `tokenizer.ggml.model` identifier
- Missing or unsupported `tokenizer.ggml.pre` profile (an unsupported
  value here is exactly the class of bug `PHASE_0_ACCEPTANCE.yaml`'s
  own history records — the original omission produced a real,
  llama.cpp-flagged quality degradation, not merely a hypothetical)
- Missing `tokenizer.ggml.tokens` vocabulary
- Missing `tokenizer.ggml.token_type`
- Missing `tokenizer.ggml.merges`
- `tokens`/`token_type` array length mismatch (must both be 49,152 for
  this profile; any other pairing is rejected)
- Invalid token IDs (bos/eos IDs outside `[0, vocab_size)`)
- Invalid token types (any value outside the confirmed `{1, 3}` set for
  this profile is unexpected and must not be silently accepted as
  NORMAL)
- Invalid merge records (malformed entries, not exactly two ranked
  sub-tokens)
- Duplicate or ambiguous merges
- Out-of-range special-token IDs
- Unsupported tokenizer configuration (any `tokenizer.ggml.model`/
  `pre` combination other than the exact pinned pair is out of scope
  for this phase and must be rejected, not silently attempted)
- Metadata that disagrees with the pinned compatibility tuple (e.g. a
  vocabulary size other than 49,152, or a merge count other than
  48,900, for a GGUF claiming this same model/pre pair)

This is deliberately **not** a generic GGUF validation framework.
Validate only what is required to safely construct and use this one
tokenizer profile.

## 9. Oracle and fixture strategy

Three-way comparison, required when implementation is later authorized:

1. **Primary oracle:** the pinned Hugging Face tokenizer via `tokenizers` 0.22.2.
2. **Secondary independent oracle:** llama.cpp reading the GGUF's `tokenizer.ggml.*` metadata.
3. **System under test:** the future native OrcEngine C++ implementation.

Existing evidence to reuse rather than recreate:

- The 20 tokenizer golden fixtures (`Tools/OrcEnginePhase0/phase3_prep/
  tokenizer_golden_fixtures.py`, output at `artifacts/
  tokenizer_golden_fixtures.json`).
- The raw-prompt identity fixtures (`Tools/OrcEnginePhase0/oracle/
  raw_prompt_identity.py`, 6 fixture records).
- The dual-source tokenizer agreement evidence (`Tools/OrcEnginePhase0/
  oracle/tokenizer_dual_source_check.py`, 5/5 fixtures, HF vs.
  llama.cpp-via-GGUF).
- The special-token metadata fault case (`Tools/OrcEnginePhase0/
  oracle/tokenizer_special_token_fault.py`, the `<\|im_start\|>`
  CONTROL→NORMAL mislabeling attack).

**None of these fixtures already prove the future native
implementation.** Every one of them currently compares the Python
`tokenizers` oracle against llama.cpp or against itself — no native
OrcEngine C++ code has ever been exercised by any of them. They are
reusable INPUT fixtures and reusable ORACLE-AGREEMENT evidence; they
are not evidence of the native implementation's correctness, which
does not yet exist.

The comparison must capture, where applicable: exact raw input bytes;
exact token IDs; token pieces; exact decoded bytes; policy flags used;
BOS/EOS settings; special-token settings; oracle version and model
revision; deterministic hashes where already established (the existing
fixtures already record SHA-256 hashes of raw bytes and token ID
lists — that convention should carry forward, not be reinvented).

## 10. Required adversarial tests

Future tests, not implemented in this pass:

- Missing metadata (each required key from Section 8, individually)
- Corrupted token types
- Corrupted special-token classification — **directly reuse** the
  existing `tokenizer_special_token_error` regression case
  (`<\|im_start\|>` CONTROL→NORMAL mislabeling) as a required
  regression, not a new design
- Merge-order changes
- Vocabulary/merge disagreement
- Out-of-range IDs
- Invalid UTF-8
- Incomplete streaming UTF-8
- Special-token injection through ordinary user text
- Incorrect BOS/EOS insertion
- Incorrect skip-special behavior
- Silent substitution or fallback to a different tokenizer profile
  (e.g. accepting a GGUF whose `tokenizer.ggml.pre` does not match
  `"smollm"` without rejecting it)

## 11. Frozen-engine integration proof

A future integration test must demonstrate:

1. A frozen raw prompt is tokenized by the native Phase 5B boundary.
2. The resulting IDs exactly equal the established explicit token IDs
   already used throughout Phase 1-5A's own fixtures (e.g. the
   `[1, 5, 28, 284, 260, 198]` sequence used pervasively in Phase 2-5A
   real-model evidence).
3. Those IDs are passed into the frozen Phase 5A engine without
   translation.
4. The resulting logits/tokens remain identical to the frozen
   explicit-ID path.

The purpose is narrowly to prove that Phase 5B adds a boundary without
changing numerical execution. **This test is not authorized or
implemented by this specification pass** — it is a future deliverable,
described here only to define what "done" will mean.

## 12. Memory and residency boundary

Tokenizer vocabulary, merge tables, and decoding state are a separate
accounting category from model-weight residency, backing I/O
accounting, KV-cache residency, and activation memory. Phase 5B must
not silently mix tokenizer-table memory into any of Phase 5A's existing
`StreamingTelemetry`/`ResidencyLedger` measurements. Any later
measurement of tokenizer memory must be reported separately, using its
own accounting, not folded into an existing Phase 5A total.

A generic tokenizer cache is not introduced by this specification and
should not be introduced later unless evidence shows one is necessary
(the vocabulary/merge tables are already immutable and loaded once per
Section 4 — an additional cache layer on top of that is a distinct,
unjustified claim until measured).

## 13. Future validation lanes

Eventual validation expectations, not run in this pass:

- Debug
- Release
- Strict warnings-as-errors
- ASan
- Golden-fixture oracle comparison (the 20-fixture corpus plus the
  dual-source and raw-prompt-identity fixtures, re-run against the new
  native implementation)
- Malformed-metadata attacks (Section 10)
- Frozen-engine integration proof (Section 11)
- Independent review before freeze

No test counts are invented here — exact counts come from
implementation and actual test discovery, not from this document.

**The Phase 5A ASan-timeout workaround (running a small number of
real-model tests directly, outside CTest's own timeout, because their
inherited timeouts were sized for Release-speed execution) must not
automatically be copied into Phase 5B.** Phase 5B's tokenizer-only
tests operate on vocabulary/merge tables and text fixtures, not
multi-hundred-megabyte real-model forward passes — there is no a
priori reason to expect the same ASan slowdown profile. Phase 5B should
use targeted, fast tokenizer tests first, and only reach for the
inherited expensive real-model test infrastructure where the Section 11
integration proof actually requires exercising the full frozen engine.

## 14. Acceptance criteria

Phase 5B must not be accepted or frozen until all required claims have
direct evidence. At minimum, later implementation must prove:

- Exact agreement for the pinned compatibility tuple (Section 3)
- Exact IDs for all required fixtures (Sections 5, 9)
- Explicit and tested BOS/EOS behavior (Section 7, Decision Register
  items 3-4)
- Explicit and tested special-token policy (Section 7, Decision
  Register items 1-2, 5)
- Exact decoded-byte behavior (Section 6)
- Safe partial-UTF-8 streaming behavior (Section 6, Decision Register
  item 7)
- Fail-closed malformed-metadata handling (Section 8)
- No change to frozen engine numerical results (Section 11)
- No unintended modification of frozen Phase 1-5A files
- Clean required validation lanes (Section 13)
- Independent review findings reconciled

## 15. Explicit non-goals

- Chat-template selection or rendering
- Tool-call formatting
- Sampling
- Generation scheduling
- UI/product integration
- .NET integration
- Multiple tokenizer families
- Generic tokenizer plugins
- Remote model downloads
- Performance optimization
- Phase 5C work
- Changes to tensor execution
- Changes to KV cache or weight residency
- A universal tokenizer framework, a generic Hugging Face tokenizer
  runtime, a multi-model tokenizer registry, a chat-template engine, a
  sampling/generation phase, a product/UI integration phase, a
  performance-optimization phase, or a tokenizer plugin architecture

## 16. Deliverables

Future implementation deliverables, defined narrowly but **not
created by this pass**:

- One concrete native tokenizer component
- One focused test executable, or the smallest existing test target
  that fits, for the golden-fixture and adversarial comparisons
- Reused golden fixtures (Section 9) — not recreated
- Targeted malformed-metadata tests (Section 8, 10)
- One frozen-engine integration test (Section 11)
- Documentation evidence

No empty source directories, placeholder files, interfaces, or
scaffolding are pre-created by this specification pass.

## 17. Stop gate

This document is a **draft**. No implementation is authorized by this
commit. Maintainer review is required. Any unresolved policy decision
in Section 18 (Decision Register) must be resolved — either by
maintainer confirmation of the recommended contract, or by an
explicit alternative decision — before implementation begins. Phase 5C
remains prohibited until Phase 5B is separately accepted and frozen.

## 18. Decision register

| # | Decision | Existing evidence | Recommended Phase 5B contract | Maintainer confirmation required? |
|---|---|---|---|---|
| 1 | Default encoding treatment of literal special-token-looking text | Golden corpus has a dedicated fixture category; `skip_special_tokens` finding shows control tokens have real, silent effects on decode | Encode literal text as ordinary bytes by default (do not let plain user text activate a control token's special ID unless explicitly requested) — matches the general safety instinct behind `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "never an invisible guess" requirement, but has not been proven against this tokenizer's actual encode-time special-token matching behavior | **Yes** — no direct local fixture proves what the pinned `tokenizers` 0.22.2 library actually does with literal `<\|endoftext\|>` text at *encode* time (only the *decode*-time `skip_special_tokens` effect is confirmed) |
| 2 | Whether control-token recognition requires explicit caller opt-in | None directly on encode-time opt-in; `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "special-token recognition policy" principle | Yes, require explicit opt-in per call (mirrors HF `tokenizers`' own `add_special_tokens`-style parameter conventions) | **Yes** |
| 3 | BOS insertion default | GGUF `add_bos_token = false`, confirmed | Do not insert BOS by default, matching the pinned metadata exactly | No — metadata is unambiguous; implementation should follow it directly |
| 4 | EOS insertion default | GGUF `add_eos_token = false`, confirmed | Do not insert EOS by default, matching the pinned metadata exactly | No — metadata is unambiguous |
| 5 | Default decode `skip_special_tokens` behavior | Directly confirmed divergence: `True` drops literal `<\|endoftext\|>` substrings; `False` round-trips exactly | Default to `skip_special_tokens=false` (preserve bytes exactly, fail-safe for a decode boundary) with an explicit, separately-named caller option to strip special tokens for display purposes | **Yes** — this is a deliberate behavior choice with a real, demonstrated user-visible consequence either way, not a metadata-determined fact |
| 6 | Invalid UTF-8 input behavior | None found locally — no fixture exercises this; Section 3's architectural note establishes there is no vocabulary-level "unknown" path, but says nothing about malformed *input bytes* (which is a different failure class from "valid UTF-8 the vocabulary can't represent," which cannot happen given the byte-level design) | Reject with an explicit error at the encode boundary (fail closed) rather than silently substituting a replacement character | **Yes** — no local evidence determines this; it is a genuinely open policy choice |
| 7 | Incomplete UTF-8 at end-of-stream behavior | None found locally; `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "Streaming decode" section states the general principle only, never implemented or tested | Buffer incomplete trailing bytes; treat end-of-stream with a still-incomplete sequence as an explicit error condition surfaced to the caller, not silently discarded or replaced | **Yes** |
| 8 | Invalid token-ID decode behavior | None found locally | Reject with an explicit error at the decode boundary (fail closed) rather than silently mapping to a placeholder token | **Yes** |
| 9 | Exact GGUF metadata fields required for construction | Fully confirmed this pass (Section 8's field list) | Require exactly the 9 fields listed in Section 8; reject construction if any is missing or malformed | No — this is now directly evidenced, not a policy choice |
| 10 | Round-trip correctness basis (Unicode string / raw bytes / both) | Existing fixtures already record both `raw_bytes_sha256`-style hashes and decoded text; no explicit prior decision on which is authoritative | Both — compare raw bytes as the primary correctness criterion (bytes are what the byte-level decoder actually produces), with Unicode-string comparison as a secondary, human-readable check | **Yes** — both are currently recorded in fixtures without a stated priority between them |

Where this table says "Yes" under maintainer confirmation, this
specification deliberately does not silently select a final policy —
an explicit unresolved decision is recorded instead of an unsupported
claim.
