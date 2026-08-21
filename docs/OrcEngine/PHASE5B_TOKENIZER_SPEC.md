# Phase 5B: Tokenizer / Text-Token Boundary Reference

Status: **IMPLEMENTATION IN PROGRESS — STAGE 1 (METADATA CONSTRUCTION) COMPLETE, STAGE 2A (PRETOKENIZATION) COMPLETE, NOT COMPLETE OR FROZEN**

Branch: `feat/orcengine-phase5b-tokenizer`, worktree
`F:\Ai\OrchestratorIDE-phase5b-tokenizer`, forked from `orcengine-phase5a-freeze`.

Prepared: 2026-08-20 America/Los_Angeles, specification-only pass.
Corrected: 2026-08-20, same day, following a focused review pass
(evidence/policy corrections only — see Sections 4, 5, 7, 11, 18, 19).
**Accepted: 2026-08-20, same day.** The maintainer explicitly approved
all seven Section 19 policy recommendations as binding Phase 5B
implementation requirements (see the "Maintainer-approved" markers in
Sections 18-19 and `DECISION_LOG.md` OE-ADR-030). This was a
specification-acceptance decision, not an implementation-completion or
freeze decision.

**Stage 1 implemented: 2026-08-20, same day.** Native GGUF tokenizer-
metadata construction and fail-closed validation
(`Tools/OrcEnginePhase5B/`) — the pinned compatibility tuple (Section
3/8) is validated and an immutable `TokenizerProfile` is constructed
from already-parsed GGUF metadata, reusing the frozen Phase 2 GGUF
reader's public API without modifying it. **Text encoding, BPE merge
execution, pretokenization, token decoding, streaming UTF-8
accumulation, and frozen-engine integration are NOT implemented** —
Stage 1 constructs and validates tables only; it does not tokenize
anything. Phase 5B is **not** complete, accepted as a finished
implementation, or frozen. Acceptance does not waive the required
llama.cpp secondary-oracle comparison (Section 9's availability
note) — that remains an outstanding validation dependency before Phase
5B can be considered complete or frozen.

**Stage 1 closure-correction pass: 2026-08-20, same day.** Real pinned
GGUF artifacts now exercised (not synthetic metadata only); merge-
result (concatenation) validation added and confirmed against real
metadata before being enforced; adversarial tests now verify exception
type and diagnostic content, not merely "some exception was thrown";
two tautological `check(true, ...)` runtime passes removed. **Real
finding, not a Phase 5B defect:** `smollm2-135m-tied.gguf` has zero
`tokenizer.ggml.*` metadata keys (15 architecture-only fields vs. the
explicit artifact's 24) — `load_tokenizer_profile` correctly rejects
it with `GgufError`. The explicit artifact fully validates.

**Green-lane classification pass: 2026-08-20, same day (`DECISION_LOG.md`
OE-ADR-031).** The maintainer resolved the artifact-classification
question the closure-correction pass surfaced: `smollm2-135m.gguf` is
the canonical, positive, tokenizer-bearing Phase 5B artifact;
`smollm2-135m-tied.gguf` is a frozen legacy tensor/output-head-
equivalence fixture, not a positive tokenizer-bearing artifact, and its
missing metadata is a **required fail-closed rejection**, not a
temporary gap. Explicit-vs-tied tokenizer-table equality — introduced
by the closure-correction prompt, never part of the specification
accepted at commit `83083145` — is removed as a requirement. The three
real-artifact contracts are now independent and each returns exit 0
only when its own checks pass:

- `tokenizer_metadata` (synthetic suite): 136/136 checks pass.
- `tokenizer_metadata_real_explicit` (canonical positive suite against
  `smollm2-135m.gguf`): all checks pass.
- `tokenizer_metadata_legacy_tied_rejection` (expected-rejection suite
  against `smollm2-135m-tied.gguf`): all checks pass — the tied
  artifact's `GgufError` naming the missing `tokenizer.ggml.model` key
  is the required, passing outcome, not a failure.

No registered Phase 5B test intentionally fails. The historical
discovery that the tied fixture lacks tokenizer metadata is preserved
in `tokenizer.cpp`'s merge-result comment, in the rejection test's own
comments, and in `DECISION_LOG.md` OE-ADR-031 — it is not hidden by
this reclassification.

**Stage 2A implemented: 2026-08-20, same day (`DECISION_LOG.md`
OE-ADR-032).** Exact native reproduction of the pinned tokenizer's
`Digits(individual_digits=true) -> ByteLevel(add_prefix_space=false,
trim_offsets=true, use_regex=true)` pretokenization sequence
(`Tools/OrcEnginePhase5B/src/pretokenize.cpp`), producing pretoken BYTE
RANGE boundaries only — no byte-to-Unicode alphabet remapping, no BPE
merge execution, no token-ID production, no decoding. Classification
tables (`\p{L}`, `\p{N}`, `\s`) were generated from Python's
`unicodedata` and then validated against the live `tokenizers==0.22.2`
oracle rather than assumed correct — 2,831 range-boundary probes plus a
4,974-codepoint seeded random sample found 0 mismatches after
correcting one discovered gap (Python's `str.isspace()` incorrectly
includes U+001C–U+001F, which the oracle does not treat as
whitespace); a separate 565-probe check confirmed the `Digits` stage's
isolation predicate is identical to the `\p{N}` table. Full
methodology in `Tools/OrcEnginePhase5B/tools/generate_pretok_tables.py`
and `DECISION_LOG.md` OE-ADR-032. The native scanner (`test_pretokenize`)
was checked byte-for-byte against a 63-entry oracle-derived fixture
corpus (golden fixtures, raw-prompt-identity fixtures, and hand-authored
boundary/transition cases) plus 8 invalid-UTF-8 rejection cases: 209/209
checks pass, 0 failures, across Debug, Release, strict, and ASan. This
is boundary-agreement on that corpus and the described sampling, not a
claim of exhaustive equivalence over all possible Unicode strings.
Encoding (token-ID production), BPE merge execution, byte-to-Unicode
mapping, decoding, streaming, and frozen-engine integration remain
unimplemented.

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
`tokenizer.ggml.token_type` array, **every valid UTF-8 input accepted
by the Phase 5B boundary is representable through this vocabulary
without a vocabulary-level unknown-token fallback.** This claim is
deliberately narrower than "every possible input byte sequence is
representable" (an earlier draft's wording): it says nothing about
*invalid* UTF-8, which is a separate input-validation decision
(Decision Register item 6), not a vocabulary-coverage question — an
input can be rejected as malformed before ever reaching the byte-level
mapping this note describes, and that rejection is not evidence of a
vocabulary gap. `unk_token` in `tokenizer_config.json` names
`<|endoftext|>` only because the GPT-2 tokenizer format requires *some*
value for that field, not because it is ever actually produced by
encoding valid UTF-8. This is DERIVED from the confirmed metadata
above, not independently verified by running the tokenizer against
adversarial byte input — see Decision Register item 6 and Section 5's
"Unknown-token behavior" row.

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
- **Reuse the existing GGUF metadata reader without modifying it.**
  `Tools/OrcEnginePhase2/include/orcengine/gguf.hpp` is a frozen Phase
  1-5A file and must not be changed by Phase 5B (correction from an
  earlier draft of this document, which incorrectly said a future
  implementation "must extend `gguf.hpp`"). Direct inspection of its
  public surface confirms this constraint is not a problem: `GgufArtifact::
  metadata` is a public `std::map<std::string, GgufValue>` field;
  `require_metadata(artifact, key)` returns a public `const GgufValue&`;
  `GgufValueType::Array` and `GgufValue::data`'s `std::variant` already
  include `std::vector<GgufValue>`. Every field this profile needs
  (`tokenizer.ggml.tokens`/`merges`/`token_type` as arrays; `model`/
  `pre` as strings; `bos_token_id`/`eos_token_id`/`add_bos_token`/
  `add_eos_token` as scalars) is reachable through this already-public
  surface. A future implementation therefore needs only a **Phase-5B-
  local** free function that calls `index_gguf()` (already public) and
  extracts typed arrays from the returned `GgufValue`s — no Phase 2
  header or implementation change, and no missing operation to record
  as a blocker. **No native tokenizer or GGUF-tokenizer-parsing code
  exists anywhere in the C++ tree today** (confirmed by an exhaustive
  `tokeniz`-pattern search across `Tools/OrcEnginePhase1` through
  `Tools/OrcEnginePhase5A`'s `.cpp`/`.hpp` files — the sole match was an
  unrelated comment), so this local helper would be new code, not a
  duplicate of anything existing.
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
| Empty input | Previously demonstrated: `empty_input` fixture (`tokenizer_golden_fixtures.json`) — `token_ids: []`, `encode_decode_round_trips_exactly: true`. Confirmed by direct read of the committed artifact, not inferred from the category list. |
| Repeated spaces | Previously demonstrated in the 20-fixture golden corpus ("leading/trailing/repeated whitespace" category) |
| Leading and trailing spaces | Previously demonstrated in the 20-fixture golden corpus |
| Newlines and tabs | Previously demonstrated in the 20-fixture golden corpus |
| ASCII punctuation | Previously demonstrated (`tokenizer_dual_source_agreement`'s "punctuation" fixture) |
| Latin text (non-ASCII) | Previously demonstrated (`tokenizer_dual_source_agreement`'s "café résumé" fixture; golden corpus's "non-ASCII Latin" category) |
| CJK text | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Emoji | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Combining characters | Previously demonstrated in the 20-fixture golden corpus category, not independently re-verified this pass |
| Embedded NUL (if supported) | Previously demonstrated: `embedded_nul_char` fixture — raw text `"before\x00after"` (12 bytes), `token_ids: [17985, 190, 9110]`, `encode_decode_round_trips_exactly: true`. The pinned `tokenizers` 0.22.2 API confirmed to support an embedded NUL byte and round-trip it exactly. Confirmed by direct read of the committed artifact. |
| Invalid UTF-8 | Behavior that remains undecided — no existing fixture found for this case; see Decision Register item 6 |
| Literal text resembling a special token | Previously demonstrated with exact IDs: `text_resembling_special_tokens` (`"this <\|endoftext\|> looks like a special token"`) encodes `<\|endoftext\|>` as token ID **0** at position 3 of `[8232, 216, 0, 5117, 702, 253, 1767, 9624]`; `text_resembling_special_tokens_2` (`"<\|im_start\|>not really a chat turn<\|im_end\|>"`) encodes `<\|im_start\|>`/`<\|im_end\|>` as IDs **1** and **2** at the start/end of `[1, 1766, 2159, 253, 11743, 1607, 2]`. Both confirmed by direct read of `tokenizer_golden_fixtures.json`. **This is the pinned oracle's DEFAULT encode-time behavior** — it recognizes literal special-token substrings and maps them to their special IDs; it does not treat them as ordinary text by default. See Section 7's encode-mode evidence subsection for the mechanism that produces the alternative (literal-text) behavior. |
| Recognition of allowed-control special tokens | Previously demonstrated at the fault-injection level (`tokenizer_special_token_error`: mislabeling `<\|im_start\|>` CONTROL→NORMAL in GGUF `token_type` diverges `"<\|im_start\|>user"` tokenization from `[1, 4093]` to an 8-token shattered sequence) |
| Distinction: ordinary text vs. explicitly authorized special-token input | Proposed Phase 5B requirement — not yet a settled policy; see Section 7 and Decision Register items 1-2 |

## 6. Required decode semantics

| Behavior | Status |
|---|---|
| Token IDs to raw decoded bytes | Proposed Phase 5B requirement |
| Ordinary vocabulary tokens | Previously demonstrated at the Python-oracle level |
| Control/special tokens | Proposed Phase 5B requirement, directly informed by the finding below |
| `skip_special_tokens=true` vs. `false` | **Previously demonstrated, with a real documented divergence, confirmed by direct read of the committed artifact:** both `text_resembling_special_tokens` and `text_resembling_special_tokens_2` show `encode_decode_round_trips_exactly: false` (the default decode drops the control-token text) alongside `encode_decode_round_trips_with_special_tokens_kept: true` (decoding with special tokens kept round-trips exactly). Concretely: `text_resembling_special_tokens`'s `decoded_text_repr` is `"this  looks like a special token"` — the literal `<\|endoftext\|>` substring (token ID 0) is silently dropped under the default `skip_special_tokens=True`; `text_resembling_special_tokens_2`'s default-decoded text drops both `<\|im_start\|>`/`<\|im_end\|>`, leaving only `"not really a chat turn"`. Re-decoding either fixture with `skip_special_tokens=False` round-trips exactly. Source: `tokenizer_golden_fixtures.json` fixture fields directly, corroborated by `Tools/OrcEnginePhase0/phase3_prep/tokenizer_golden_fixtures.py`'s module docstring and `phase3_prep/README.md` (both dated 2026-08-15). This specification does **not** hide that result — Section 7 and Decision Register item 5 require an explicit decode policy exactly because of it. |
| `add_special_tokens=true` vs. `false` (encode-time, distinct from decode's `skip_special_tokens`) | Previously demonstrated for ordinary text: the `bos_eos_combination` fixture shows `plain_ids: [2129]` and `with_add_special_tokens_true_ids: [2129]` — identical, `add_special_tokens_changes_output: false`, for text containing no special-token substrings. **This does NOT establish what `add_special_tokens` does for text containing literal special-token substrings** — see Section 7's encode-mode evidence subsection, which tested that specific case directly and found `add_special_tokens` has no effect there either; the actual controlling mechanism is a different, separately-discovered property. |
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

### Two proposed explicit encode modes

An earlier draft of this document recommended, as the safe default,
that plain user text not activate control tokens — without checking
whether that default is actually what the pinned oracle does. It is
**not**: Section 5's "Literal text resembling a special token" row
shows the pinned `tokenizers` 0.22.2 oracle's *default* encode behavior
already converts literal `<|endoftext|>`/`<|im_start|>`/`<|im_end|>`
substrings to their special IDs. This mismatch is not hidden here — it
is exactly why Phase 5B needs two explicit, separately named modes
rather than one silent default:

**A. Literal/ordinary-text mode.** Intended for ordinary user text.
Control-token-looking substrings are treated as literal text — encoded
through the normal byte-level BPE path — rather than silently
activating control semantics. **Confirmed independently producible**
against the pinned oracle: see the evidence subsection below. This is
the recommended safe default for Decision Register items 1-2, since an
equivalent, independently testable oracle configuration exists.

**B. Explicit-control-token mode.** Requires deliberate caller opt-in.
Recognizes the 17 pinned control tokens according to the GGUF
`token_type` metadata — this is the pinned oracle's *default* behavior
(Section 5). Used for already-formatted prompts where the caller
intentionally supplies control tokens (e.g. a caller that has already
rendered `<|im_start|>user\n...`).

### Encode-mode evidence subsection

Targeted, read-only oracle investigation performed this pass to
determine what actually controls the distinction between modes A and B.

**Tool and version:** `tokenizers` Python library, `0.22.2` (confirmed
via `python3 -c "import tokenizers; print(tokenizers.__version__)"`,
exit 0, output `0.22.2`). Tokenizer loaded via
`Tokenizer.from_file("artifacts/smollm2-135m/tokenizer.json")`.

**Finding 1 — `add_special_tokens` does NOT control literal
special-token recognition**, despite the name. Command: `tok.encode(t,
add_special_tokens=True)` vs. `tok.encode(t, add_special_tokens=False)`
for both special-token-lookalike fixture texts. Exit 0 both times.
Result: **identical token IDs both ways** —
`"this <|endoftext|> looks like a special token"` →
`[8232, 216, 0, 5117, 702, 253, 1767, 9624]` under both settings;
`"<|im_start|>not really a chat turn<|im_end|>"` →
`[1, 1766, 2159, 253, 11743, 1607, 2]` under both settings. Per this
document's own instruction not to assume `add_special_tokens` controls
literal special-token recognition merely because of its name: it does
not, empirically, for this tokenizer/library combination. (`add_special_tokens`
controls BOS/EOS insertion, which this profile has disabled — see
Section 3 — so it has no visible effect on these particular fixtures
either way.)

**Finding 2 — `Tokenizer.encode_special_tokens` is the actual
controlling property.** This is a boolean attribute on the `Tokenizer`
object itself (not an `encode()` call parameter), default `False`
(confirmed: `tok.encode_special_tokens` → `False` on a freshly loaded
tokenizer). Command: set `tok.encode_special_tokens = False` then
`True`, re-running `tok.encode(t, add_special_tokens=False)` for both
fixture texts under each setting. Exit 0 both times.

| `encode_special_tokens` | `"this <\|endoftext\|> looks like a special token"` → IDs | `"<\|im_start\|>not really a chat turn<\|im_end\|>"` → IDs |
|---|---|---|
| `False` (default) | `[8232, 216, 0, 5117, 702, 253, 1767, 9624]` — `<\|endoftext\|>` matched as ID 0 | `[1, 1766, 2159, 253, 11743, 1607, 2]` — `<\|im_start\|>`/`<\|im_end\|>` matched as IDs 1/2 |
| `True` | `[8232, 2067, 108, 486, 1714, 2692, 108, 46, 5117, 702, 253, 1767, 9624]` — `<\|endoftext\|>` broken into ordinary byte-level pieces (`<`,`\|`,`end`,`of`,`text`,`\|`,`>`) | `[44, 108, 306, 79, 3738, 108, 46, 1766, 2159, 253, 11743, 1607, 44, 108, 306, 79, 486, 108, 46]` — `<\|im_start\|>`/`<\|im_end\|>` similarly broken into ordinary pieces |

This is a definitive, independently reproducible mechanism: `encode_special_tokens
= False` (the library default) is **Mode B** (explicit-control-token
mode is actually the oracle's default); `encode_special_tokens = True`
is **Mode A** (literal/ordinary-text mode). Both modes are confirmed
independently producible against the pinned primary oracle by toggling
this one property — no blocker for the primary oracle.

**Finding 3 — the secondary oracle (llama.cpp) could not be checked
this pass.** `Tools/OrcEnginePhase0/oracle/tokenizer_dual_source_check.py`
resolves its llama.cpp binary path from the `ORC_LLAMA_TOKENIZE_PATH`
environment variable, which was unset in this session, and a targeted
filesystem search (project directories plus common local tool/download
locations) found no `llama-tokenize`/`llama-server` binary present on
this machine. **This is recorded as an evidence gap, not invented
behavior**: whether llama.cpp's tokenizer exposes an equivalent
special-token-recognition option (its public documentation describes a
`parse_special` concept for this purpose, but that was not
independently confirmed by running anything locally this pass) remains
unverified. Mode A/B's independent producibility is confirmed only
against the primary oracle at this time.

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
2. **Secondary independent oracle:** llama.cpp reading the GGUF's `tokenizer.ggml.*` metadata. **Availability note (confirmed this pass):** no `llama-tokenize`/`llama-server` binary was found on this machine and `ORC_LLAMA_TOKENIZE_PATH` is unset — this oracle is used successfully elsewhere in the project's own history (`PHASE_0_ACCEPTANCE.yaml`'s `tokenizer_dual_source_agreement` entry) but was not independently re-exercised by this reconnaissance/correction pass (see Section 7's evidence subsection, Finding 3).
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

**Correction from an earlier draft:** the `[1, 5, 28, 284, 260, 198]`
sequence used pervasively in Phase 2-5A real-model evidence is **not**
entirely a tokenized prompt. It is `[1, 5]` (arbitrary explicit initial
IDs, never established as the tokenization of any real text) followed
by `[28, 284, 260, 198]` (four IDs the frozen model *generated*, not
tokenized from input text). No raw-prompt-identity fixture was found
that independently tokenizes to `[1, 5]` — the six fixtures in
`raw_prompt_identity_manifest.json` were checked directly and none
produce that pair (the closest are `"Hello, world!"` → `[19556, 28,
905, 17]` and `"12345 test"` → `[33, 34, 35, 36, 37, 1028]`). Per this
document's own instruction not to invent an input that would produce
`[1, 5]`, this specification uses a different, already-established
fixture instead.

A future integration test must demonstrate:

1. Select a frozen raw-prompt-identity fixture with established raw
   bytes and oracle token IDs — e.g. `raw_prompt_identity_manifest.json`'s
   `"smollm2-135m:'Hello, world!'"` record (`raw_text: "Hello, world!"`,
   oracle-established `token_ids: [19556, 28, 905, 17]`).
2. Tokenize that raw prompt through the future native Phase 5B
   boundary.
3. Require exact equality between the native boundary's output and the
   fixture's oracle-established token IDs (`[19556, 28, 905, 17]` for
   the example above) — this is the tokenization-correctness half of
   the proof.
4. Run the frozen Phase 5A engine once with those native-tokenized IDs.
5. Run the same frozen engine again with those identical IDs supplied
   explicitly (the existing explicit-ID path every current Phase 1-5A
   test already uses).
6. Require identical logits, selected tokens, and generated
   continuation between steps 4 and 5.

**This test does not require the tokenizer to produce generated
continuation IDs from input text** — generation is the frozen engine's
job, not the tokenizer's; the proof's scope is strictly that
Phase 5B's tokenized IDs are indistinguishable, once produced, from
hand-supplied explicit IDs.

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

**Note:** this section governs acceptance/freeze of a future Phase 5B
*implementation*, distinct from the specification acceptance recorded
2026-08-20 (Section 18-19, `DECISION_LOG.md`'s new ADR) — approving the
seven policy decisions this document specifies is not the same as
satisfying these criteria, none of which can be evaluated before
implementation exists.

Phase 5B implementation must not be accepted as complete or frozen
until all required claims have direct evidence. At minimum, later
implementation must prove:

- Exact agreement for the pinned compatibility tuple (Section 3)
- Exact IDs for all required fixtures (Sections 5, 9)
- Explicit and tested BOS/EOS behavior (Section 7, Decision Register
  items 3-4)
- Explicit and tested special-token policy per the seven approved
  contracts (Section 7, Decision Register items 1-2, 5; Section 19)
- Exact decoded-byte behavior (Section 6)
- Safe partial-UTF-8 streaming behavior (Section 6, Decision Register
  item 7)
- Fail-closed malformed-metadata handling (Section 8)
- No change to frozen engine numerical results (Section 11)
- No unintended modification of frozen Phase 1-5A files
- Clean required validation lanes (Section 13)
- **The llama.cpp secondary-oracle comparison (Section 9's
  availability note), still outstanding as of the specification
  acceptance date** — not satisfied by specification acceptance, and
  required before Phase 5B can be considered complete or frozen, not
  merely before implementation begins
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

Future implementation deliverables, defined narrowly. None of these
existed when this section was first drafted; status is annotated per
item below as later stages deliver them:

- One concrete native tokenizer component — **Stage 1 delivered
  2026-08-20**: `Tools/OrcEnginePhase5B/` constructs and validates the
  immutable tokenizer table from GGUF metadata (`TokenizerProfile`).
  Encoding/decoding are separate, not-yet-authorized stages.
- One focused test executable, or the smallest existing test target
  that fits, for the golden-fixture and adversarial comparisons —
  **Stage 1 delivered a metadata-construction test executable**
  (`test_tokenizer_metadata`) exposing three independent contracts: the
  synthetic suite (136 checks, no arguments), the canonical explicit
  real-artifact positive suite (`--real-explicit <path>`, against
  `smollm2-135m.gguf`), and the legacy tied-artifact expected-rejection
  suite (`--expect-missing-tokenizer <path>`, against
  `smollm2-135m-tied.gguf`, per `DECISION_LOG.md` OE-ADR-031); the
  golden-fixture/adversarial *encode/decode* comparisons this bullet
  also describes remain unimplemented, since encoding/decoding do not
  exist yet.
- Reused golden fixtures (Section 9) — not recreated; not yet exercised
  (no encode/decode to run them against)
- Targeted malformed-metadata tests (Section 8, 10) — **Stage 1
  delivered these** (41 adversarial cases × 3 checks each -- throws,
  correct exception type, diagnostic fragment -- in the synthetic suite
  of `test_tokenizer_metadata`)
- One frozen-engine integration test (Section 11) — not implemented;
  requires encode/decode to exist first
- Documentation evidence — this section and Section 17
- Native pretokenization (Section 5's text-boundary responsibility, first
  half) — **Stage 2A delivered 2026-08-20**:
  `Tools/OrcEnginePhase5B/src/pretokenize.cpp` reproduces the pinned
  `Digits->ByteLevel` sequence exactly, oracle-validated (see status
  paragraph above and `DECISION_LOG.md` OE-ADR-032); produces byte-range
  boundaries only. BPE merge execution, byte-to-Unicode mapping, and
  token-ID production remain unimplemented.

No empty source directories, placeholder files, interfaces, or
scaffolding were pre-created by the specification-drafting pass, and
none were introduced by Stage 1 either (Section 4's "concrete, not an
interface/factory/plugin system" constraint was followed).

## 17. Stop gate

**Updated 2026-08-20:** all seven previously-unresolved policy
decisions in Section 18 (Decision Register) have been explicitly
approved by the maintainer (Section 19, `DECISION_LOG.md`'s new ADR).
This specification is now **ACCEPTED FOR IMPLEMENTATION**, not merely
a draft. This is still not authorization for implementation to be
considered complete or frozen — Section 14's acceptance criteria,
including the outstanding llama.cpp secondary-oracle comparison, remain
unsatisfied until implementation exists and is validated against them.
**Updated again 2026-08-20 (green-lane closure, `DECISION_LOG.md`
OE-ADR-031):** Stage 1 (native GGUF tokenizer-metadata construction and
fail-closed validation, `Tools/OrcEnginePhase5B/`) has been implemented
and targeted-validated across Debug, Release, strict, and ASan. All
three registered test contracts are clean in every lane: the synthetic
suite (136/136), the canonical explicit real-artifact positive suite
(against `smollm2-135m.gguf`), and the legacy tied-artifact
expected-rejection suite (against `smollm2-135m-tied.gguf`, whose
missing `tokenizer.ggml.*` metadata is the required, passing outcome
per OE-ADR-031, not a failure). No registered Phase 5B test
intentionally fails. Stage 1 is closed only now that all three
contracts pass. **Updated again 2026-08-20 (Stage 2A, `DECISION_LOG.md`
OE-ADR-032):** native pretokenization (`pretokenize.cpp`) is implemented
and oracle-validated (`test_pretokenize`, 209/209 checks, 0 failures,
across Debug/Release/strict/ASan) -- see the Stage 2A status paragraph
above for exact counts. Stage 1+2A together still construct/validate
tables and determine pretoken boundaries only -- text encoding (token-ID
production), BPE merge execution, byte-to-Unicode mapping, decoding,
streaming, and frozen-engine integration are separate, not-yet-
authorized stages. Section 14's acceptance criteria remain unsatisfied.
Phase 5B is not complete, accepted as a finished implementation, or
frozen. The llama.cpp secondary-oracle comparison remains outstanding.
Phase 5C remains deferred, prohibited until Phase 5B is separately
accepted (as
an implementation, not just this specification) and frozen.

## 18. Decision register

| # | Decision | Existing evidence | Phase 5B contract | Maintainer decision |
|---|---|---|---|---|
| 1 | Default encoding treatment of literal special-token-looking text | **Directly confirmed this pass** (Section 7 evidence subsection): the pinned oracle's own *default* (`encode_special_tokens=False`) already recognizes literal `<\|endoftext\|>`/`<\|im_start\|>`/`<\|im_end\|>` substrings and maps them to their special IDs — it does NOT treat them as ordinary text by default. `add_special_tokens` (the encode-call parameter) was confirmed to have no effect on this recognition either way. | Mode A (literal/ordinary-text, `encode_special_tokens=True` on the pinned oracle) as the Phase 5B default for ordinary user text — a deliberate DEPARTURE from the pinned oracle's own default (Mode B), made explicitly and for a stated reason (safety against literal user text silently invoking control tokens), not silently inherited. | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 2 | Whether control-token recognition requires explicit caller opt-in | **Directly confirmed this pass**: Mode B (`encode_special_tokens=False`, the oracle's default) and Mode A (`encode_special_tokens=True`) are both independently producible and empirically distinct (Section 7). `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "special-token recognition policy" principle supports opt-in. | Explicit opt-in per call to select Mode B; Mode A (literal text) is the default. Backed by a concrete, tested mechanism (the `encode_special_tokens` property), not merely a naming convention borrowed from HF's `add_special_tokens`. | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 3 | BOS insertion default | GGUF `add_bos_token = false`, confirmed | Do not insert BOS by default, matching the pinned metadata exactly | No maintainer decision needed — metadata is unambiguous; implementation should follow it directly |
| 4 | EOS insertion default | GGUF `add_eos_token = false`, confirmed | Do not insert EOS by default, matching the pinned metadata exactly | No maintainer decision needed — metadata is unambiguous |
| 5 | Default decode `skip_special_tokens` behavior | Directly confirmed divergence: `True` drops literal `<\|endoftext\|>` substrings; `False` round-trips exactly | Default to `skip_special_tokens=false` (preserve bytes exactly, fail-safe for a decode boundary) with an explicit, separately-named caller option to strip special tokens for display purposes | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 6 | Invalid UTF-8 input behavior | None found locally — no fixture exercises this; Section 3's architectural note establishes there is no vocabulary-level "unknown" path, but says nothing about malformed *input bytes* (which is a different failure class from "valid UTF-8 the vocabulary can't represent," which cannot happen given the byte-level design) | Reject with an explicit error at the encode boundary (fail closed) rather than silently substituting a replacement character | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 7 | Incomplete UTF-8 at end-of-stream behavior | None found locally; `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "Streaming decode" section states the general principle only, never implemented or tested | Buffer incomplete trailing bytes; treat end-of-stream with a still-incomplete sequence as an explicit error condition surfaced to the caller, not silently discarded or replaced | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 8 | Invalid token-ID decode behavior | None found locally | Reject with an explicit error at the decode boundary (fail closed) rather than silently mapping to a placeholder token | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |
| 9 | Exact GGUF metadata fields required for construction | Fully confirmed this pass (Section 8's field list) | Require exactly the 9 fields listed in Section 8; reject construction if any is missing or malformed | No maintainer decision needed — this is directly evidenced, not a policy choice |
| 10 | Round-trip correctness basis (Unicode string / raw bytes / both) | Existing fixtures already record both `raw_bytes_sha256`-style hashes and decoded text; no explicit prior decision on which is authoritative | Both — compare raw bytes as the primary correctness criterion (bytes are what the byte-level decoder actually produces), with Unicode-string comparison as a secondary, human-readable check | **APPROVED 2026-08-20** — binding implementation requirement; see Section 19 |

**Seven of the ten items above (1, 2, 5, 6, 7, 8, 10) were unresolved
policy questions requiring maintainer confirmation; all seven were
explicitly approved by the maintainer on 2026-08-20** (Section 19 has
the full decision packet for each). The other three (3, 4, 9) were
never policy questions — they are directly determined by confirmed
metadata and needed no decision. **Approval of these seven is a
specification-acceptance decision, not an implementation-completion
decision — Phase 5B implementation has still not started.** Changing
any of the seven approved contracts after this point requires a later,
separately documented decision (a new dated entry here or in
`DECISION_LOG.md`), not a silent edit.

## 19. Maintainer decision packet

**All seven items below were explicitly approved by the maintainer on
2026-08-20**, recorded in `DECISION_LOG.md`'s new ADR. None was
silently marked accepted — each was presented with its evidence, a
recommended choice, the consequence of accepting versus rejecting it,
and whether the oracle evidence gathered this pass was sufficient to
support the recommendation, and the maintainer confirmed the
recommendation for each individually. These are now **binding Phase 5B
implementation requirements**. Implementation has not started.
Changing any of them later requires a new, separately documented
decision — not a silent edit to this document.

**1. Ordinary user text does not activate control tokens by default
(Decision Register item 1).**
- *Existing evidence:* Section 7's evidence subsection — the pinned
  oracle's own default (`encode_special_tokens=False`) activates
  control tokens for literal matches; Mode A
  (`encode_special_tokens=True`) is confirmed to suppress that and is
  independently producible.
- *Maintainer decision (APPROVED 2026-08-20):* accept — default to Mode A (literal text) for
  ordinary user text.
- *Consequence of accepting:* Phase 5B's default behavior deliberately
  diverges from the pinned oracle's own default. Every fixture and
  comparison must be explicit about which mode was used, since "the
  oracle's result" is no longer synonymous with "Phase 5B's result" at
  default settings.
- *Consequence of the alternative* (default to Mode B, matching the
  oracle's own default): simpler to state ("Phase 5B's default matches
  the oracle's default"), but means ordinary user text containing
  `<|endoftext|>`-like substrings silently invokes control-token
  semantics unless the caller knows to opt out — the exact silent-guess
  failure mode this project's own tokenizer principles warn against.
- *Oracle evidence sufficient?* Yes for the mechanism (both modes are
  proven producible); the choice of which is the *default* remains a
  genuine policy call, not something the mechanism itself decides.

**2. Control-token recognition requires explicit caller opt-in
(Decision Register item 2).**
- *Existing evidence:* same as item 1 — both modes are independently
  producible and distinct.
- *Maintainer decision (APPROVED 2026-08-20):* accept — Mode B requires an explicit,
  separately-named caller flag; it is never implicit.
- *Consequence of accepting:* every call site that intends to tokenize
  an already-formatted, control-token-bearing prompt must say so
  explicitly.
- *Consequence of the alternative* (permissive default, e.g. detect
  control-token-looking substrings heuristically): reintroduces the
  invisible-guess failure mode item 1 exists to avoid.
- *Oracle evidence sufficient?* Yes — this follows directly from item 1's evidence.

**3. Decode preserves special tokens by default (Decision Register
item 5).**
- *Existing evidence:* Section 6 — `skip_special_tokens=True` (the
  pinned oracle's default) silently drops literal `<|endoftext|>`/
  `<|im_start|>`/`<|im_end|>` substrings on decode;
  `skip_special_tokens=False` round-trips exactly for both affected
  fixtures.
- *Maintainer decision (APPROVED 2026-08-20):* accept — default to `skip_special_tokens=false`
  equivalent (preserve special-token text), with an explicit,
  separately-named option to strip them for display.
- *Consequence of accepting:* decode is byte-exact by default, at the
  cost of callers who want "clean" display text needing to opt in to
  stripping.
- *Consequence of the alternative* (default-strip, matching the
  oracle's default): matches the oracle's own default, but a decode
  boundary that silently drops bytes by default is a correctness risk
  for any caller that assumes decode is lossless.
- *Oracle evidence sufficient?* Yes — the divergence is directly
  measured on two real fixtures, not hypothetical.

**4. Invalid UTF-8 input is rejected explicitly (Decision Register item 6).**
- *Existing evidence:* none found locally — no fixture in the 20-fixture
  corpus or elsewhere exercises malformed UTF-8 input.
- *Maintainer decision (APPROVED 2026-08-20):* reject explicitly (fail closed) rather than
  substitute a replacement character.
- *Consequence of accepting:* callers must handle an explicit error for
  malformed input; no silent data loss or corruption is possible.
- *Consequence of the alternative* (substitute/replace): matches some
  other tokenizer ecosystems' conventions, but risks silently
  corrupting input the caller believed was preserved.
- *Oracle evidence sufficient?* **No** — this is a genuinely open
  policy choice with zero local evidence either way; the recommendation
  is a safety default, not an evidenced fact.

**5. Incomplete UTF-8 at end of stream is an explicit error (Decision
Register item 7).**
- *Existing evidence:* none found locally;
  `TOKENIZER_AND_PROMPT_PIPELINE.md`'s "Streaming decode" section
  states the general principle only, never implemented or tested
  against this tokenizer.
- *Maintainer decision (APPROVED 2026-08-20):* buffer incomplete trailing bytes during
  streaming; treat a still-incomplete sequence at end-of-stream as an
  explicit error surfaced to the caller.
- *Consequence of accepting:* streaming callers must handle an explicit
  end-of-stream error path distinct from normal completion.
- *Consequence of the alternative* (silently discard or replace
  incomplete trailing bytes): simpler for callers, but hides a genuine
  truncation/corruption signal.
- *Oracle evidence sufficient?* **No** — no local evidence either way;
  this is a design principle applied for the first time to this
  tokenizer, not a measured fact.

**6. Invalid token IDs are rejected explicitly (Decision Register item 8).**
- *Existing evidence:* none found locally.
- *Maintainer decision (APPROVED 2026-08-20):* reject with an explicit error at the decode
  boundary rather than silently mapping to a placeholder token.
- *Consequence of accepting:* a caller that (through its own bug)
  passes an out-of-vocabulary ID gets a clear error instead of a
  plausible-looking but wrong decoded token.
- *Consequence of the alternative* (map to a placeholder, e.g. UNK):
  matches some tokenizer conventions, but this vocabulary's own
  `unk_token` is `<|endoftext|>` (Section 3) — silently mapping invalid
  IDs to the same token that also means BOS/EOS would be actively
  misleading, not merely imprecise.
- *Oracle evidence sufficient?* **No** — no local fixture exercises
  this; the recommendation is reasoned from Section 3's evidence about
  what `unk_token` actually means for this profile, not directly
  measured.

**7. Raw decoded bytes are the primary round-trip authority, with
Unicode comparison secondary for valid UTF-8 (Decision Register item 10).**
- *Existing evidence:* existing fixtures already record both
  `raw_bytes_sha256`/`decoded_bytes_sha256`-style hashes and
  human-readable `decoded_text_repr`, without a stated priority between
  them.
- *Maintainer decision (APPROVED 2026-08-20):* accept — raw bytes are authoritative (that is
  what the `ByteLevel` decoder actually produces); Unicode-string
  comparison is a secondary, human-readable check, valid only when the
  bytes happen to be valid UTF-8.
- *Consequence of accepting:* test failures are diagnosed byte-first;
  a byte-level mismatch that happens to still decode to a plausible
  Unicode string is still a failure.
- *Consequence of the alternative* (Unicode-string-first): simpler for
  human review, but could mask a byte-level bug that doesn't happen to
  break Unicode decoding.
- *Oracle evidence sufficient?* Partial — the fixtures show both are
  already recorded, which supports treating bytes as primary (they are
  the more fundamental of the two data points already captured), but no
  case was found this pass where the two comparison bases actually
  disagree, so the practical stakes of this choice remain unmeasured.
