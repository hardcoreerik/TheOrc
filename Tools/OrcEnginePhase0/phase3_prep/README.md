# Phase 3 prep — tokenizer golden-fixture corpus

Test/fixture work only, permitted under the Phase 0 stop gate (see
`../phase2_prep/README.md` for why) — no Phase 3 tokenizer implementation
exists here or anywhere else in the repository yet.

## What's here

`tokenizer_golden_fixtures.py` generates the full "Golden fixtures" list
from `docs/OrcEngine/TOKENIZER_AND_PROMPT_PIPELINE.md`: empty input, ASCII
words/punctuation, whitespace variants, non-ASCII (Latin/CJK/emoji/combining
marks), text resembling special tokens, unknown/byte-fallback cases,
BOS/EOS combinations, multibyte UTF-8 boundaries, and encode-decode
caveats — 20 fixtures total, run against the REAL pinned tokenizer
(SmolLM2-135M's `tokenizer.json`), recording raw bytes, token IDs, token
pieces, offsets, and decoded bytes for each, per the doc's own required
comparison fields.

## Real finding, not just fixtures

Two fixtures ("text resembling special tokens") do **not** round-trip
exactly under the `tokenizers` library's default `decode()` behavior
(`skip_special_tokens=True`): text containing the literal substring
`<|endoftext|>` loses that substring on decode, because it happens to
match token id 0, the model's actual `<|endoftext|>` control token, which
gets silently dropped. Confirmed this is exactly a `skip_special_tokens`
default effect (not a tokenizer bug) by re-decoding with
`skip_special_tokens=False`: both fixtures then round-trip exactly, 18/18.

This is precisely the caveat `TOKENIZER_AND_PROMPT_PIPELINE.md`'s
"special-token recognition policy... explicit, never an invisible guess"
requirement anticipates. Phase 3 must pick and document a decode default
deliberately — this fixture set is the concrete evidence for why that
choice matters, not a hypothetical.

## Reproducing

```bash
cd Tools/OrcEnginePhase0
python3 -m phase3_prep.tokenizer_golden_fixtures
```

Requires `artifacts/smollm2-135m/tokenizer.json` (run
`python3 -m oracle.download_candidate` first if not present). Writes
`artifacts/tokenizer_golden_fixtures.json` (committed — 20 fixtures,
~33KB, no large binary data since it's token IDs/strings, not the
tokenizer itself).
