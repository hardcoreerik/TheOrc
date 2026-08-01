# Sampling and Decoding

## Baseline

Phase 1 supports greedy decoding only:

```text
next_token = smallest token ID whose logit equals the finite maximum
```

Tie behavior must be deterministic and documented. NaN handling must be explicit; a non-finite logits vector is an error, not a random token.

## Separation of responsibilities

- model execution produces raw logits;
- logit processors transform an owned working copy;
- selector chooses a token;
- stop policy decides whether to emit/continue;
- tokenizer decoder turns token IDs into bytes/text;
- generation loop commits token/cache state.

Keeping raw logits available is essential for oracle comparisons.

## Greedy acceptance

For every fixture record:

- logits hash and numerical metrics;
- top token ID and logit;
- runner-up ID and logit;
- top-token margin;
- selected token;
- stop reason.

A matching token with materially different logits is not a pass.

## Later stochastic pipeline

Proposed ordered components, each separately testable:

1. forbidden-token mask;
2. repetition/frequency/presence penalties as defined;
3. temperature;
4. top-k;
5. top-p;
6. min-p or other accepted filter;
7. normalization;
8. seeded categorical sample.

Order changes behavior and is part of the API contract. No sampler option is exposed unless every backend either implements it or reports unsupported honestly.

## Randomness

Use a well-defined seeded generator and record seed plus algorithm. Reproducibility is guaranteed only for the named algorithm/build contract, not implied across arbitrary versions. Greedy mode must not consume RNG state.

## Stop policy

Possible stop causes:

- EOS token;
- maximum generated tokens;
- context capacity;
- caller cancellation;
- explicit stop-token sequence;
- grammar terminal state later;
- backend or numerical error.

Return the actual reason. Do not report success when context exhaustion truncated generation unexpectedly.

## Stop strings

String-level stopping is postponed because token boundaries and partial UTF-8 complicate semantics. If added, define whether matched bytes are emitted, how overlapping patterns resolve, and how buffers are bounded.

## Grammar-constrained decoding

This is strategically relevant to TheOrc tool calls but outside the baseline. A future grammar system requires:

- formal grammar definition and limits;
- token-to-byte transition semantics;
- incremental parser state;
- allowed-token mask generation;
- protection against state explosion;
- exact failure behavior when no token is valid;
- adversarial tests for untrusted grammars.

## Log probabilities

Not needed initially. If exposed, specify normalization point, filtered versus raw distribution, precision, and whether the selected token probability is computed before or after each processor.

## Verification

- exact greedy vectors including ties and negative infinity;
- temperature limit cases;
- top-k/p boundary cases if implemented;
- deterministic seeded sequences;
- processor-order golden cases;
- EOS and max-token behavior;
- cancellation without extra committed token;
- decoder byte-boundary behavior.
