# Tokenizer and Prompt Pipeline

## Why this is a first-class subsystem

The model consumes token IDs, not strings. A numerically perfect transformer fed different IDs is a different computation. Tokenization, special-token insertion, prompt templates, and incremental byte decoding are correctness boundaries.

## Layer separation

```text
TheOrc messages/tools
  -> explicit prompt-template formatter
  -> raw UTF-8 bytes/text policy
  -> model tokenizer
  -> token IDs
  -> OrcEngine model
  -> generated token IDs
  -> tokenizer decoder / byte accumulator
  -> streamed text chunks
```

Prompt templates do not belong inside tensor execution. The engine may expose tokenizer and template capabilities separately.

## First tokenizer scope

Exact scope depends on the pinned model. Possible families include SentencePiece unigram/BPE or GPT-style BPE stored in GGUF metadata. The [SentencePiece project](https://github.com/google/sentencepiece) documents raw-sentence tokenization, whitespace representation, normalization, and reversible decode properties for its model.

The first implementation supports exactly one verified tokenizer configuration.

## Required semantics

- normalization rule and whether it is enabled;
- byte-to-Unicode mapping or byte fallback;
- pre-tokenization rules;
- vocabulary tokens and IDs;
- merge ranks or unigram scores;
- unknown-token behavior;
- BOS/EOS/padding IDs;
- add-BOS/add-EOS call semantics;
- prefix-space behavior;
- special-token recognition policy;
- decoding of partial UTF-8 sequences across token boundaries.

No ID is guessed from conventional values.

## Golden fixtures

Include:

- empty input;
- ASCII words and punctuation;
- leading/trailing/repeated whitespace;
- newline and tab;
- non-ASCII Latin, CJK, emoji, combining marks;
- embedded NUL byte if supported by API;
- text resembling special tokens;
- unknown or byte-fallback cases;
- BOS/EOS combinations;
- tokens that split a multibyte UTF-8 code point;
- encode-decode and decode-encode caveats.

Compare raw bytes, token IDs, token pieces, offsets where available, and decoded bytes.

## Chat templates

The current `LLamaSharpRuntime` probes an embedded model template and has explicit fallback prompt paths. OrcEngine must not duplicate that behavior blindly.

Proposed integration rule:

- standalone engine accepts raw prompt text/bytes and explicit special-token options;
- a separate template layer may interpret the model’s `tokenizer.chat_template`;
- TheOrc adapter can reuse or share `NativePromptBuilder` only after compatibility is proven;
- actual template path is included in telemetry;
- missing/unsupported template fails or uses an explicitly named caller-selected fallback, never an invisible guess.

## Tools and constrained output

Phase 1 has no tool schema or grammar. Later TheOrc integration may format tools through existing prompt policy and parse text output. Grammar-constrained decoding is a later decoder capability with its own correctness/security review.

## Streaming decode

Generated tokens may not align with Unicode scalar boundaries. Maintain a byte accumulator and emit only valid complete sequences according to an explicit invalid-byte policy. Cancellation must not emit corrupted replacement text silently.

## Performance

Cache immutable tokenizer tables after validation. Pre-tokenized Context Fabric blocks remain a future experiment; cache keys must include tokenizer/model revision, template identity, exact bytes, and special-token policy.

## Acceptance gate

The tokenizer passes every golden fixture against the primary oracle, the exact Phase 0 prompt token IDs match, detokenization handles split bytes, special-token policy is explicit, and prompt-template identity appears in artifacts.
