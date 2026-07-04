# Context Fabric CF-7 Test Harness — How It Grades Answers

This document explains, end to end, how the `cf7-gate-expanded` benchmark decides
whether an answer is right or wrong. It exists so the scoring logic itself can be
reviewed independently of any particular run's result — a NO-GO should mean "the
model got it wrong," not "the harness has a bug."

Companion docs: [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md](CONTEXT_FABRIC_BENCHMARK_MANIFEST.md)
(report schema, re-run recipe), [CONTEXT_FABRIC_BENCHMARK_CORPUS.md](CONTEXT_FABRIC_BENCHMARK_CORPUS.md)
(public/private corpus rules).

## 1. What's being tested

Four live systems plus one frozen artifact answer the same 120 held-out questions
against the same 128-segment, un-marked expanded corpus (43,968 estimated source
tokens):

| System | What it is | Code |
|---|---|---|
| B0 | Closed-book — no corpus access at all | `ContextFabricBaselineRunner` |
| B1 | Truncated prompt — corpus crammed in until it runs out of budget, no ranking | `ContextFabricBaselineRunner` |
| B2 | Conventional top-k RAG — IDF-ranked segment retrieval | `ContextFabricBaselineRunner.BuildTopKText` |
| B3 | Single-node Context Fabric — the actual product answering path | `ContextFabricFeasibilityRunner` |
| B4 | HIVE Context Fabric — frozen multi-node acceptance artifact, not re-run per gate | `cf6-acceptance-*.json` |

The corpus is deliberately **un-marked**: facts are embedded in ordinary prose, not
flagged with an `EVIDENCE:` line. That's a load-bearing property — an earlier
"GO" verdict was invalidated by an adversarial review that found the old fixture
let the model pattern-match markup instead of reading. See `DeterministicExpandedFabricCorpus`
and its `OpenExtractionReading` reader-prompt mode.

The held-out set is 120 of a 150-question suite (30 held back as a dev set for
prompt tuning — see `docs/The Orc Context Fabric.md:963`). Categories and minimum
counts: Needle/local fact (40), Unanswerable (20), Multi-hop — two-hop + three-to-
five-hop chains (30), Exhaustive enumeration (15), Contradiction/change (10),
Global synthesis (15), Paraphrased retrieval (20). Every question was mechanically
verified against the *rendered* corpus text before being frozen — the verifier
checks that every `ExpectedTerm` actually appears in its claimed `ExpectedSegmentId`,
which caught a real generator bug during authoring (see commit `dcffd05e`).

## 2. How an answer gets built (the part that can introduce false failures)

This is the part worth reviewing hardest, because a bug here produces a wrong
*grade*, not a wrong *answer* — the model could be right and the harness could
still mark it failed, or vice versa.

### B3 — `BuildEvidencePack` (`ContextFabricFeasibilityRunner.cs`)

This is not benchmark-only code — it's the same evidence selection used by
`FabricNativeReaderService` and `HiveNativeRoleExecutorAdapter` in the real
product. Given a question and the corpus's evidence cards:

1. Compute IDF (inverse document frequency) per term across the supplied cards,
   after tokenizing with a 2-character minimum (`TokenizeForScoring`) — short
   enough to keep 2-digit identifiers like `01` in `case-ledger-01`, since the
   3-character-minimum `Tokenize` would silently split that on the hyphen and
   destroy the exact signal needed to tell `ledger-01` from `ledger-09`.
2. Exclude English stopwords entirely from scoring (`the`, `and`, `this`, ...),
   so common words don't dilute the ranking signal that should come from
   distinctive terms.
3. Score every card via `ScoreTextIdf` and greedily fill the evidence budget
   (6,144 tokens by default, 3,072 for HIVE) in ranked order — **no fixed card
   count cap**. Cards scoring 0 are excluded outright.

**What was wrong before (fixed in commit `c68e01cf`):** `BuildEvidencePack` used
to hard-cap at 1/2/4 cards by question kind, with no documented cost/latency
justification. Global-synthesis questions need evidence from up to 8 segments —
capped at 4, the method was *structurally* incapable of answering them correctly
regardless of how good the ranking was. Comparing `ExpectedSegmentIds` against
`IncludedSegmentIds` on failing questions in the NO-GO run showed this was
exactly what was happening: CF frequently never gathered the segment containing
the answer. That's an evidence-*selection* bug, not a reasoning failure — and it
was inflating the failure count with cases where the model was never given a
chance to be right.

### B2 — `BuildTopKText` (`ContextFabricBaselineRunner.cs`)

Same fix, same reasoning, applied to the "conventional RAG" comparison baseline
(commit `c55e5058`). Before the fix, B2 used `Take(4)` with raw term-overlap
counting and no stopword filtering — it actually scored *worse* (21%) than the
dumber truncated-prompt baseline B1 (26%), which was itself a strong signal the
implementation was broken rather than that top-k RAG is inherently worse than
truncation. If B2 isn't fixed too, "B3 beats B2" isn't a fair claim — B2 would be
losing by construction, not by a real retrieval contest.

### Exhaustive-category answers — `BuildExhaustiveAnswer` (`ContextFabricFeasibilityRunner.cs:~740`)

Exhaustive questions ("list every case-file ID under ledger X") do **not** go
through `BuildEvidencePack` — they hit this separate method, because the goal
isn't "the top-N most relevant cards," it's "every card that actually belongs to
the named category." All 12 Exhaustive failures in the NO-GO run hit the same
error: the answer over-included claims from unrelated categories because the old
filter accepted a claim if it shared *any* word with the question — and corpus-
idiomatic filler words ("ledger", "recorded") appear in nearly every claim across
all 15 ledgers.

Current logic (commit `3ef5fb0b`, line ~763):

1. Tokenize the question, find which of its terms are actually present in the
   corpus's cards, and compute each one's document frequency.
2. Classify the question as **entity-scoped** if its rarest present term appears
   in fewer than half the cards (`minDocumentFrequency < cards.Count / 2.0`) —
   e.g. `"case-ledger-01"` is genuinely rare relative to the corpus, so hard-
   require that term.
3. Otherwise classify as **category-wide** (e.g. `"archive token"`, where every
   segment is genuinely relevant) and fall back to "any non-stopword term
   matches."

This went through two earlier failed attempts (a pure IDF aggregate score
couldn't discriminate between two equally-rare ledger IDs; hard-requiring the
single rarest term broke a case where *every* segment is relevant) before landing
on the entity-scoped/category-wide split — both failure modes now have dedicated
regression tests.

**Known residual risk, explicitly not fixed:** this classification is a
heuristic (`minDocumentFrequency < cards.Count / 2.0`), not a proof. A genuinely
category-wide question whose real content terms happen to have <50% document
frequency by corpus coincidence would still be mis-classified as entity-scoped.
Grok's adversarial review of this fix (`.orc/reviews/grok_20260703_185505.md`)
flagged this explicitly. Both real scenarios uncovered so far (ledger-scoped,
archive-token-wide) have tests; the boundary case does not. **If a future run
produces a new Exhaustive-category failure, check this heuristic first before
assuming it's a model capability gap.**

## 3. How an answer gets graded — `FabricAnswerVerifier.NormalizeAndVerify`

(`ContextFabricValidation.cs:838`)

Given the model's raw JSON answer, corpus, and the question's ground truth:

- **Structural sanity caps**, scaled to the question's own ground truth rather
  than fixed globally — `maxAnswerChars = max(12000, 80 * ExpectedTerms.Count)`,
  `maxCitationsPerClaim = max(32, ExpectedSegmentIds.Count)`. These exist to
  reject genuine model garbage (runaway repetition, hallucinated citation
  floods) without penalizing a legitimately large exhaustive enumeration, which
  scales with the question's own expected-term count.
- Every citation must reference a real segment ID and pass
  `FabricEvidenceProcessor.NormalizeCitation` (the quote must actually appear in
  that segment — this is what makes `citation_precision` meaningful rather than
  just "the model said a segment ID").
- For non-abstention questions: every term in `question.ExpectedTerms` must
  appear somewhere in the answer text or a citation quote, and every segment in
  `question.ExpectedSegmentIds` must have been actually cited
  (`verifiedSegments`) — not just any correct-sounding text, but evidence from
  the *specific* segments the question was authored against.
- For `ExpectAbstention` questions: the model must abstain and say the corpus
  doesn't establish the answer, and must not smuggle in factual claims anyway.

`citation_precision` = valid citations / total citations attempted. A question
only "passes" (`Verification.Passed`) if `errors.Count == 0` — all of the above
in one gate, not a partial-credit score.

## 4. JSON recovery — why answers don't get graded "wrong" for formatting noise

(`FabricJson.ParseModelObject<T>` in `ContextFabricValidation.cs`)

Autoregressive models emit two specific token-boundary artifacts that would
otherwise turn a correct answer into an unparseable one and grade it as failed
for the wrong reason:

1. **Keyword-suffix runs** — `falseC`, `trueX`, `nullValue` — where a JSON
   keyword token runs directly into the next word token with no boundary.
   `TrySanitizeLiteralSuffixes` walks the string state-aware and strips only
   out-of-string garbage suffixes.
2. **Unescaped inner quotes** — a model quotes a term inline (`called it
   "Chapter Alpha" a fitting name`) without escaping it, which otherwise
   terminates the JSON string early and corrupts everything after. This
   sanitizer tracks whether the current string is an object key vs. a value
   before deciding whether a `:` or `,`/`}`/`]` is a real terminator — a value
   string containing a quoted term immediately followed by `:` must not be cut
   there (only key strings terminate on `:`).

The parser tries, in order: strict parse → lenient parse (trailing
commas/comments) → both sanitizer orders composed together (`keyword→quote` and
`quote→keyword`, since either artifact can appear first and partially block the
other's own internal validity check) → throw. Composing both orders required
splitting each sanitizer into a raw scanning core (no internal validation) plus
a validated public wrapper, because a partially-repaired intermediate result
(quotes fixed, keyword suffix still broken) would otherwise be rejected by the
quote-sanitizer's own `JsonDocument.Parse` check before the keyword-fix pass ever
got a chance to run on it.

Separately, `ContextFabricBaselineRunner` splits its catch into `JsonException`
(counts as `Succeeded=true`, incorrect-answer-recorded) vs. any other `Exception`
(counts as `Succeeded=false`, a genuine runtime failure) — so a run of B0/B1/B2
always reaches `RunCompleted=true` unless the executor itself actually crashes,
rather than an unparseable answer masquerading as an infrastructure failure.

## 5. The gate report — `ContextFabricBenchmarkGateEvaluator`

Five metrics, each with a hardcoded target:

| Metric | Target | What it means if it fails |
|---|---|---|
| `segment_terminal_coverage` | 1.0 | Not every segment was accepted during ingestion — an ingestion bug, not a model problem |
| `question_pass_rate` | **1.0** | At least one held-out question failed verification |
| `citation_precision` | 0.90 | The model is citing segments that don't actually support its claims |
| `max_prompt_tokens` | ≤ context limit | The evidence pack overflowed the context budget |
| `boundary_stitch_pass_rate` | 1.0 | A question spanning a segment boundary wasn't stitched correctly |

**Important interpretation note:** `question_pass_rate`'s target is 1.0 — literal
100%. As configured, the gate reports `NO-GO` unless *every one* of 120
held-out questions passes verification exactly. This is a deliberate fail-closed
design (see `docs/CONTEXT_FABRIC_BENCHMARK_MANIFEST.md`'s "explicit `Missing`
entries, not omitted rows" philosophy for the same pattern elsewhere), but it
also means B3 can substantially outscore every baseline (56/120 vs. B1's 31/120,
B2's 25/120 in the last run) and the gate will still say `NO-GO`. When reviewing
a gate report, look at the `systems` table's raw pass counts, not just the
top-line verdict, to judge whether a NO-GO reflects "close but not perfect" or
"still fundamentally broken."

## 6. Change history relevant to grading correctness

| Date | Commit / PR | What changed |
|---|---|---|
| 2026-07-03 | `01f3fd09` (PR #34) | JSON recovery pipeline (keyword-suffix sanitizer), `ModelAdmissionGate` 3B floor |
| 2026-07-04 00:21 | `c55e5058` (PR #34) | B2 `BuildTopKText` rewrite: IDF-weighted, budget-fill, no fixed `Take(4)` |
| 2026-07-04 01:23 | `c68e01cf` (PR #34) | B3 `BuildEvidencePack` fix: same IDF-weighted/budget-fill approach, removes the 1/2/4 `maxCards` cap — **the diagnosed root cause of the 56/120 NO-GO** |
| 2026-07-04 01:54 | `3ef5fb0b` (PR #34) | `BuildExhaustiveAnswer` entity-scoped vs. category-wide term filtering — fixes all 12 Exhaustive-category failures from the NO-GO run |
| 2026-07-04 02:00 | `40d79e1b` (PR #34) | Grok adversarial review of the Exhaustive fix: fixed a segment-lookup crash risk, documented the heuristic's known residual risk (section 2 above) |
| 2026-07-04 04:25 | PR #37 | Unescaped-inner-quote JSON sanitizer + key-vs-value colon handling, composed with the keyword-suffix sanitizer |
| 2026-07-04 04:46 | PR #38 | PowerShell 5.1 compatibility fixes in `Run-CF7GateExpanded.ps1` (re-run tooling only, not scoring logic) |
| 2026-07-04 | Grok review, `.orc/reviews/grok_20260703_223402.md` | Independent review of the full fix set above (36 files, 5,099 insertions), focused specifically on false-failure/false-pass risk in the scoring/parsing paths. Verdict: **CLEAN**, no findings. |

**The 120-question NO-GO run on record (2026-07-04T00:36:28Z, B3 56/120) predates
the `BuildEvidencePack` and `BuildExhaustiveAnswer` fixes** — it measured the old,
known-buggy evidence selection. It is not yet known what B3 scores with the
current, fixed code; that is the open item this document supports reviewing
before the next run.

## 7. Open bug: KV-cache exhaustion invalidates most of a full 120-question run

**Status as of 2026-07-04: unresolved, high priority.** This is not a fleet
quirk like section 8 below — it reproduced on NEWCOREPC, the machine with the
most headroom, and it likely invalidates most B0/B3 results from any full run
since the `BuildEvidencePack`/`BuildTopKText` fixes landed.

The 2026-07-04 02:44:29-elapsed full 120-question run on NEWCOREPC (Gemma-4-12B,
8192 context) reported B3 at 12/120 — *worse* than the pre-fix NO-GO's 56/120.
Inspecting the raw result JSON showed why: 216 of B3's 223 failed
question-attempts had `verification.errors: ["Native inference failed while
draining a prompt batch: NoKvSlot."]` — a native KV-cache exhaustion, not a
wrong answer. Only 7 failures were genuine (6 "reducer output references
claims outside its children", 1 unterminated-JSON). B0 (closed-book) then
failed near-identically once B3 had already burned through the shared KV pool.
**The 12/120 and B0's failure are not meaningful capability measurements** —
they're an infrastructure crash wearing a NO-GO costume.

Root cause, traced through the code: `AdapterManager.cs` already documents and
guards against a *related* but distinct problem — llama.cpp's KV-cache sequence
IDs are minted monotonically and never recycled, even after a `Conversation` is
`Dispose()`d (see the comment at `AdapterManager.cs:48-56`, referencing a prior
crash "at exactly the 257th reader conversation"). The existing fix,
`SequenceRecycleThreshold = 128` (rebuild the role's executor — a fresh native
context — every 128 minted conversations, at an idle point) and
`SequenceHardLimit = 240` (fail closed with a managed exception rather than let
the native assert kill the process), protects against exhausting the *count* of
sequence IDs. **It does not protect against exhausting actual KV-cache
*memory*, which is a function of prompt size × live-but-unrecycled sequences,
not conversation count.** Since `BuildEvidencePack`'s fix removed the
`maxCards` cap, a single LocalFact question observed in this run's JSON pulled
in **26 segments** (6,309 prompt tokens) where the old, capped code would have
used 1-4 cards — meaning each conversation now reserves far more of the shared
KV pool before being abandoned. `NoKvSlot` is reachable well before the
128-conversation recycle point fires, and indeed did: the managed hard-limit
exception (which has its own distinct message, "has minted N native sequence
slots...") never appeared in the log — only the native `NoKvSlot` — confirming
the existing protection's own counters never tripped even though the native
pool was already exhausted.

**Recommended fix direction (not yet implemented — needs testing time this
doesn't have tonight):** recycle based on cumulative *prompt tokens* consumed
per role's executor, not conversation *count* — token usage is what actually
correlates with KV-cache pressure now that evidence-pack size varies per
question instead of being capped. A blind lower `SequenceRecycleThreshold`
would be a guess without knowing the real memory-per-token relationship for the
model/context combination in use; a token-budget-based recycle trigger would
be exact.

**What this means for reading any prior or future run's B3/B0 numbers:** check
`verification.errors` in the raw JSON, not just the summary line, before
trusting a low pass count as a real capability result — grep for `NoKvSlot`
across `cf0_*.json` and `cf7_baseline_b0_*.json`. If present in more than a
handful of entries, the run needs to be redone after this is fixed, not
interpreted as-is.

## 8. Known fleet/environment issues (not scoring-logic bugs)

These are infrastructure problems observed while running the gate on specific
machines. They affect whether a run *executes*, not whether the grading logic
above is correct — recorded here so a future NO-GO or crash isn't mistaken for
a scoring bug or a model capability gap.

- **HARDCOREPC (RTX 3050, 6GB VRAM) native-library load regression, 2026-07-04.**
  After a clean rebuild (`rmdir` of `bin`/`obj`/`publish` followed by
  `dotnet publish -r win-x64 --self-contained true`), every model load on this
  machine fails immediately with `TypeInitializationException: The type
  initializer for 'LLama.Native.NativeApi' threw an exception. | Inner:
  RuntimeError: Failed to load the native library.` — before any inference is
  attempted (`segments 0/128, questions 0/N`). Confirmed **not** model-specific:
  reproduced identically with both `Qwen3.5-4B-Q8_0.gguf` and
  `qwen2.5-coder-7b-instruct-q5_k_m.gguf` (the latter had loaded and run
  successfully on this same machine earlier in the same session, before the
  clean rebuild). Native DLLs in `publish/runtimes/win-x64/native/*` are
  present at expected file sizes across all variants (avx/avx2/avx512/cuda12/
  noavx), so this isn't a missing- or truncated-file problem — the underlying
  first-chance exception is being swallowed by .NET's cached
  `TypeInitializationException` behavior (a static constructor's exception is
  saved and rethrown verbatim on every later access), so the *real* root cause
  is not yet visible from application logs alone. **Not yet resolved** —
  needs investigation with a debugger attached or `COMPlus_LegacyExceptionHandling`/
  first-chance-exception logging enabled, ideally comparing against
  NEWCOREPC and HARDCORELAPTOPMSI where the identical `dotnet publish -r
  win-x64 --self-contained true` recipe succeeded the same night. HARDCOREPC
  was left idle (no benchmark process running) pending this investigation.

- **Windows/OpenSSH process detachment.** A benchmark launched via
  `ssh host "start /b ... "` does **not** survive the SSH session closing —
  Windows' OpenSSH server tears down the whole console process tree when the
  channel closes, killing detached children too. Two working alternatives:
  keep the `ssh host "long-running command"` invocation itself running under
  the orchestrating side's own background-task mechanism (simplest, used for
  NEWCOREPC/HARDCOREPC runs), or register a Task Scheduler job
  (`schtasks /create ... /tr <path-to-a-.bat-wrapper>`) and trigger it with
  `schtasks /run` (works even if the orchestrating side disconnects, used for
  the HARDCORELAPTOPMSI run). When using `schtasks`, the `/tr` command runs
  via `CreateProcess`, not a shell — `>`/`2>&1` redirection syntax is silently
  ignored unless wrapped in a `.bat` file or `cmd /c "..."`.

## 9. Re-running

See [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md § Re-Running The Expanded 120-Question Gate](CONTEXT_FABRIC_BENCHMARK_MANIFEST.md#re-running-the-expanded-120-question-gate)
for the canonical recipe (`Tools/ContextFabricBench/Run-CF7GateExpanded.ps1`).
