# Context Fabric CF-7 Grading Specification

**Purpose:** this is the normative reference for how `cf7-gate-expanded` decides
whether an answer is right or wrong, and how a run's overall verdict is computed.
It describes **current behavior only** — no history, no disproved hypotheses, no
incident narrative. If you're debugging *why* something is the way it is, or
want the story behind a past bug, see
[CONTEXT_FABRIC_BUG_HISTORY.md](CONTEXT_FABRIC_BUG_HISTORY.md). If you're
dealing with a machine-specific crash or checking model compatibility, see
[CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md](CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md).

Companion docs: [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md](CONTEXT_FABRIC_BENCHMARK_MANIFEST.md)
(report schema, re-run recipe), [CONTEXT_FABRIC_BENCHMARK_CORPUS.md](CONTEXT_FABRIC_BENCHMARK_CORPUS.md)
(public/private corpus rules).

## 1. Non-goals

This document does not explain why a bug happened, does not track fleet/machine
issues, and does not carry forward disproved hypotheses from past investigations.
A NO-GO verdict should mean "the model got it wrong," not "the harness has a
bug" — that separation is the entire reason this spec is kept narrow.

## 2. Systems under test

Four live systems plus one frozen artifact are compared on the same question
set against the same corpus:

| System | What it is | Code | Scored against the 120-question CF-7 set? |
|---|---|---|---|
| B0 | Closed-book — no corpus access at all | `ContextFabricBaselineRunner` | Yes |
| B1 | Truncated prompt — corpus crammed in until it runs out of budget, no ranking | `ContextFabricBaselineRunner` | Yes |
| B2 | Conventional top-k RAG — IDF-ranked segment retrieval | `ContextFabricBaselineRunner.BuildTopKText` | Yes |
| B3 | Single-node Context Fabric — the actual product answering path | `ContextFabricFeasibilityRunner` | Yes |
| B4 | HIVE Context Fabric — frozen multi-node acceptance artifact | `ContextFabricBaselineRunner.LoadHiveAcceptanceGate` | **No — see §2.1** |

### 2.1 B4 is structurally different from B0–B3, not just "not re-run"

B4 does **not** answer the 120 CF-7 held-out questions. `LoadHiveAcceptanceGate`
(`ContextFabricBaselineRunner.cs:102`) loads a frozen JSON artifact from
`.orc/cf6-acceptance/cf6-acceptance-*.json` — the output of an entirely
separate benchmark (CF-6, HIVE distributed-node acceptance) run against a
**different, smaller corpus** (`cf0-synthetic-book-v1`, 16 segments, not the
CF-7 expanded corpus's 128) and a **different, smaller question set** (5
questions, not 120). The artifact currently on file
(`cf6-acceptance-20260701-161235.json`) was produced 2026-07-01, across 3
reader nodes (HARDCOREPC, HARDCORELAPTOPMSI, NEWCOREPC).

B4's contribution to the gate is a single structural pass/fail, computed from
six checks on the artifact JSON (`ContextFabricBaselineRunner.cs:129-180`),
none of which touch `question_pass_rate` or `citation_precision`:

1. `passed == true` in the artifact
2. `gateMode == "acceptance"`
3. `readerNodeCount >= 2`
4. Every entry in the artifact's `verifiers` array has `validated == true`,
   and the array is non-empty
5. Every entry in the artifact's `questions` array has `answerValidated ==
   true`, and the array is non-empty
6. Every entry in the artifact's `stitchCases` array has `validated == true`,
   and the array is non-empty

**Practical consequence:** B4 in the systems table tells you "the last
distributed-HIVE acceptance run structurally succeeded as of its own date,"
not "HIVE Context Fabric answers CF-7's 120 questions correctly." Do not
compare B4's presence/absence against B0–B3's pass counts as if they measure
the same thing. If HIVE Context Fabric needs to be scored against the current
CF-7 expanded corpus, that requires a new acceptance run against that corpus —
not a re-read of the existing frozen artifact.

## 3. Corpus and held-out question set

The corpus is deliberately **un-marked**: facts are embedded in ordinary prose,
not flagged with an `EVIDENCE:` line. This is load-bearing — an earlier GO
verdict was invalidated by an adversarial review that found the old fixture let
the model pattern-match markup instead of reading. See
`DeterministicExpandedFabricCorpus` and its `OpenExtractionReading`
reader-prompt mode. The expanded corpus has 128 segments, ~43,968 estimated
source tokens.

### 3.1 Question suite and the 120/150 split

The full question suite has 150 questions
(`.orc/adversarial/expanded-question-suite.json`), stratified-split 80/20 into
a 120-question held-out set used for grading
(`.orc/adversarial/expanded-question-suite-heldout.json`) and a 30-question
dev set used for prompt tuning (`.orc/adversarial/expanded-question-suite-dev.json`).
The split is held back specifically so prompt/heuristic tuning never sees the
graded questions — tuning against the dev set and then measuring on the
held-out set is what makes a pass rate meaningful rather than overfit.

The split is **exactly 20% per category**, not an aggregate 20% that could
concentrate in one category — verified directly against both files:

| Category | Full suite | Dev set (20%) | Held-out set (80%) |
|---|---|---|---|
| Needle/local fact | 40 | 8 | 32 |
| Paraphrased retrieval | 20 | 4 | 16 |
| Multi-hop (two-hop + three-to-five-hop chains) | 30 | 6 | 24 |
| Global synthesis | 15 | 3 | 12 |
| Contradiction/change | 10 | 2 | 8 |
| Exhaustive enumeration | 15 | 3 | 12 |
| Unanswerable | 20 | 4 | 16 |
| **Total** | **150** | **30** | **120** |

`Unanswerable` count (16) matches the held-out set's `expectAbstention == true`
count exactly (16) — abstention is expected only for the Unanswerable category,
confirmed directly against the JSON.

Every question was mechanically verified against the *rendered* corpus text
before being frozen — the verifier checks that every `ExpectedTerm` actually
appears in its claimed `ExpectedSegmentId`, which caught a real generator bug
during authoring (see `CONTEXT_FABRIC_BUG_HISTORY.md`, commit `dcffd05e`).

## 4. Decision flowchart

This is the exact branch structure of `FabricAnswerVerifier.NormalizeAndVerify`
(`ContextFabricValidation.cs:852`), given the model's raw JSON answer, the
corpus, and the question's ground truth. Verified line-by-line against the
current source, not transcribed from memory.

```mermaid
flowchart TD
    A[Model answer JSON] --> B{schemaVersion correct?}
    B -- no --> ERR[errors += schema mismatch<br/>NOT terminal, falls through]
    B -- yes --> C{answer text under<br/>max(12000, 80*ExpectedTerms) chars?}
    ERR --> C
    C -- no --> ERR2[errors += answer too long<br/>NOT terminal, falls through]
    C -- yes --> D{claim count <= 64?}
    ERR2 --> D
    D -- no --> ERR3[errors += too many claims<br/>NOT terminal, falls through]
    D -- yes --> E[For each claim]
    ERR3 --> E
    E --> E2{claim object is null?}
    E2 -- yes --> ERR4b[errors += null claim item<br/>this claim is skipped entirely]
    E2 -- no --> F{claim.Text non-empty?}
    F -- no --> ERR4[errors += empty claim text<br/>NOT skipped, falls through]
    F -- yes --> FC{draftCitations.Count ><br/>maxCitationsPerClaim?}
    ERR4 --> FC
    FC -- yes --> ERR4c[errors += too many citations<br/>NOT skipped, falls through]
    FC -- no --> G[For each citation in claim]
    ERR4c --> G
    G --> G2{citation object is null?}
    G2 -- yes --> ERR5b[errors += null citation item<br/>NOT counted in totalCitations]
    G2 -- no --> TC[totalCitations++<br/>counted regardless of what follows]
    TC --> H{citation.SegmentId set?}
    H -- no --> ERR5[errors += missing segmentId]
    H -- yes --> I{segmentId exists in corpus?}
    I -- no --> ERR6[errors += unknown segment]
    I -- yes --> J["NormalizeCitation:<br/>quote actually found in segment text?"]
    J -- no --> ERR7[errors += citation not grounded]
    J -- yes --> K[validCitations++<br/>segment added to verifiedSegments]
    E --> M{claim has >=1 valid citation,<br/>unless answer is abstained?<br/>(checked per-claim, inside the loop)}
    M -- no --> ERR8[errors += claim has no valid citation]
    E -- after ALL claims processed --> N{question.ExpectAbstention?<br/>evaluated ONCE, not per-claim}
    N -- yes --> O{draft.Abstained?}
    O -- no --> ERR9[errors += did not abstain]
    O -- yes --> P{answer text contains<br/>'does not establish'?}
    P -- no --> ERR10[errors += missing abstention phrase]
    ERR9 & ERR10 & P -- yes --> Q{draftClaims.Count > 0?<br/>always checked, regardless of<br/>how O/P came out}
    Q -- yes --> ERR11[errors += abstained answer has factual claims]
    N -- no --> R{draft.Abstained?}
    R -- yes --> ERR12[errors += unexpectedly abstained]
    R -- no --> S{every ExpectedTerm found in<br/>answer text, a claim's text,<br/>or a citation quote?}
    ERR12 --> S
    S -- no --> ERR13[errors += missing expected term]
    S -- regardless --> T{every ExpectedSegmentId<br/>in verifiedSegments?}
    T -- no --> ERR14[errors += missing required evidence]
    T -- yes --> PASS{errors.Count == 0?<br/>final check, all branches<br/>above converge here}
    Q -- no --> PASS
    ERR14 --> PASS
    ERR4b & ERR5 & ERR5b & ERR6 & ERR7 & ERR8 & ERR9 & ERR10 & ERR11 & ERR12 & ERR13 --> PASS
    PASS -- no --> FAIL[Verification.Passed = false]
    PASS -- yes --> PASSED[Verification.Passed = true]
```

Every error path is additive (`errors.Add(...)`) — the verifier does not
short-circuit on the first error, so a single answer can accumulate multiple
error strings, all of which are reported. `Verification.Passed` is
`errors.Count == 0` — a strict AND gate, not partial credit. There is no
severity weighting between error types: a missing expected term and a
malformed citation are equally fatal to a question's pass/fail status.

## 5. Evidence selection

### 5.1 B3 — `BuildEvidencePack` (`ContextFabricFeasibilityRunner.cs:661`)

This is the benchmark's own evidence-selection implementation, used to
produce B3's answers. **It is not the same code as the real product's
evidence selection** — the actual product path (`FabricAskService`, via
`EvidencePackBuilder.cs`) is a structurally separate implementation that
operates on `FabricLibraryRepository`/`DocumentGraphRepository` and a
`FabricQueryPlan`, not on benchmark corpus cards, and does not share
`ScoreTextIdf`/`TokenizeForScoring` with `BuildEvidencePack`. The two are
conceptually similar (both are budget-filling evidence selectors) but should
not be assumed to behave identically — a B3 result is a measurement of the
benchmark harness's own evidence-selection algorithm, not a direct proxy for
the shipped product's retrieval quality.

Given a question and the corpus's evidence cards, `BuildEvidencePack`:

1. Computes IDF (inverse document frequency) per term across the supplied
   cards, after tokenizing with a 2-character minimum (`TokenizeForScoring`) —
   short enough to keep 2-digit identifiers like `01` in `case-ledger-01`,
   since a 3-character-minimum tokenizer would silently split that on the
   hyphen and destroy the exact signal needed to tell `ledger-01` from
   `ledger-09`.
2. Excludes English stopwords entirely from scoring (`the`, `and`, `this`, ...).
3. Scores every card via `ScoreTextIdf` and greedily fills the evidence budget
   (6,144 tokens by default, 3,072 for HIVE) in ranked order — **no fixed
   card-count cap**. Cards scoring 0 are excluded outright.

### 5.2 B2 — `BuildTopKText` (`ContextFabricBaselineRunner.cs:410`)

B2 is **not** the same algorithm as B3, despite both being IDF-weighted,
budget-filling retrieval — they diverged after the original bug fix that
made them comparable, and this doc previously overstated the similarity.
Concretely: B2 scores whole corpus **segments** (`fixture.Corpus.Segments`),
not the evidence **cards** B3 scores; it tokenizes with the stricter
3-character-minimum `Tokenize` (not `TokenizeForScoring`) against its own
`_stopwords` set; and its per-term weight is a plain `1.0 /
documentFrequency` sum rather than B3's `ScoreTextIdf`. Both independently
implement "rank candidates by IDF-ish term overlap, greedily fill a token
budget in ranked order," which is what makes a B3-vs-B2 comparison a fair
retrieval contest rather than B2 losing by construction (the pre-fix
`Take(4)` implementation did lose by construction) — but they are two
separate implementations of that idea, not one shared code path.

### 5.3 Exhaustive-category answers — `BuildExhaustiveAnswer` (`ContextFabricFeasibilityRunner.cs:962`)

Exhaustive questions ("list every case-file ID under ledger X") do **not** go
through `BuildEvidencePack` — the goal isn't "the top-N most relevant cards,"
it's "every card that actually belongs to the named category." Current logic
(`ClaimMatches`, `ContextFabricFeasibilityRunner.cs:1013-1015`) tries two
tiers in order, the second only as a fallback from the first:

1. **Tier 1c — hyphenated-identifier anchor match (takes precedence when it
   applies).** Extract hyphenated identifier anchors from the question (e.g.
   `case-ledger-01`, `BR-048` — anything matching `\b\w+(?:-\w+)+\b`) whose
   document frequency across cards is both `>0` and `<50%`
   (`scopedIdentifierAnchors`, lines 1006-1011). If any such anchors exist, a
   claim matches only if its text contains one of them as a **verbatim
   contiguous substring** — bypassing unigram scoring entirely. This exists
   because the tokenizer splits identifiers into corpus-common fragments
   (`case`, `ledger`, `01`), so unigram rarity alone cannot distinguish
   `case-ledger-01` from `case-ledger-09`; the exact scenario that motivated
   this tier is documented in
   [CF_RETRIEVAL_IMPROVEMENT_PLAN.md §1c](CF_RETRIEVAL_IMPROVEMENT_PLAN.md).
2. **Fallback — entity-scoped vs. category-wide unigram classification**,
   used only when no hyphenated anchors are found for the question. Tokenize
   the question, find which of its terms are actually present in the corpus's
   cards, and compute each one's document frequency. Classify as
   **entity-scoped** if the rarest present term appears in fewer than half
   the cards (`minDocumentFrequency < cards.Count / 2.0`) and hard-require
   that term; otherwise classify as **category-wide** (e.g. `"archive
   token"`, where every segment is genuinely relevant) and fall back to "any
   non-stopword term matches."

> **Known limitation, not yet fixed:** the Tier 1c anchor match resolved the
> specific `ledger-01`-vs-`ledger-09` collision that originally motivated this
> section, but the fallback unigram classification (step 2, used for any
> exhaustive question with no hyphenated identifier in it) is still a
> heuristic, not a proof. A genuinely category-wide question with no
> hyphenated anchors, whose real content terms happen to have <50% document
> frequency by corpus coincidence, would still be mis-classified as
> entity-scoped. Both real scenarios uncovered so far (ledger-scoped,
> archive-token-wide) have regression tests; this boundary case does not.
> Planned resolution: pre-compute ground-truth classification per exhaustive
> question at authoring time instead of inferring it at grading time —
> tracked for a future phase, not yet implemented. See
> `CONTEXT_FABRIC_BUG_HISTORY.md` for the two earlier approaches that were
> tried and rejected before the fallback heuristic was adopted.

## 6. Grading algebra — `FabricAnswerVerifier.NormalizeAndVerify`

(`ContextFabricValidation.cs:852-975`)

### 6.1 Per-question citation precision

For a single question:

```text
totalCitations  = count of every non-null citation object across every claim
                  in the model's answer, REGARDLESS of validity
                  (a hallucinated segmentId still increments this)
validCitations  = count of citations where:
                    1. segmentId is non-empty
                    2. segmentId exists in the corpus
                    3. NormalizeCitation confirms the quote text actually
                       appears in that segment

precision = totalCitations == 0
            ? (question.ExpectAbstention ? 1.0 : 0.0)
            : validCitations / totalCitations
```

The abstention special-case exists because an abstained answer correctly has
zero citations — scoring that as `0.0` would penalize correct abstention.
Conversely, a non-abstention question that cites nothing scores `0.0`
precision, not `undefined` or `1.0` — a deliberate fail-closed choice.

**Worked example:** a claim cites 3 segments; 1 references a real segment with
a quote that doesn't actually appear there (invalid), 1 references a segment
ID that doesn't exist in the corpus at all (invalid), 1 is fully correct
(valid). `totalCitations = 3`, `validCitations = 1`, `precision = 0.333`. Both
invalid citations still count toward the denominator — a model that
hallucinates a plausible-looking but nonexistent segment ID is penalized in
the precision score, not silently excluded from it.

### 6.2 Gate-level `citation_precision` — this is NOT the mean of per-question precision

`ContextFabricBenchmarkGateEvaluator.cs:97-102` computes the metric reported
in the gate as:

```text
gate_citation_precision = sum(ValidCitations across all 120 questions)
                         / sum(TotalCitations across all 120 questions)
```

This is a **citation-weighted aggregate**, not an average of the 120
per-question precision scores. The two can diverge substantially:

- A handful of citation-heavy Exhaustive-category questions (which can carry
  dozens of citations each) dominate the aggregate far more than their 1-in-120
  share of the question count would suggest.
- Abstention questions contribute `0/0` to both the numerator and denominator
  of the aggregate (since `totalCitations == 0` for a correct abstention) —
  their per-question `precision = 1.0` **never appears** in the gate-level
  metric. A run with many Unanswerable questions, all correctly abstained,
  gets zero credit for that in `gate_citation_precision`, even though those
  questions "passed."

If you need "how well does the model cite, on average, per question" rather
than "citation accuracy weighted by how many citations were attempted," you
must compute `mean(per-question precision)` yourself from the raw JSON — the
gate report does not currently expose it.

### 6.3 Structural sanity caps

Scaled to the question's own ground truth rather than fixed globally:
`maxAnswerChars = max(12000, 80 * ExpectedTerms.Count)`,
`maxCitationsPerClaim = max(32, ExpectedSegmentIds.Count)`. These reject
genuine model garbage (runaway repetition, hallucinated citation floods)
without penalizing a legitimately large exhaustive enumeration.

### 6.4 Non-abstention vs. abstention verification

- **Non-abstention questions:** every term in `question.ExpectedTerms` must
  appear somewhere in the `groundedTrace` — the answer text, any claim's own
  text, or a citation quote (`ContextFabricValidation.cs:948-950` joins all
  three). Only quotes from citations that already passed `NormalizeCitation`
  (§6.1's `validCitations`) are included — `groundedTrace` is built from
  `normalizedClaims`, which only ever holds the `citations` list populated at
  line 927, after a citation clears the segment-exists and quote-grounded
  checks. A hallucinated or invalid citation's quote text can't be used to
  satisfy an `ExpectedTerm` match. Separately, every segment in
  `question.ExpectedSegmentIds` must be in `verifiedSegments` — evidence from
  the *specific* segments the question was authored against, not just any
  correct-sounding text.
- **`ExpectAbstention` questions:** the model must abstain, must say the
  corpus "does not establish" the answer (exact substring match, case
  insensitive), and must not include any claims.

## 7. JSON recovery

(`FabricJson.ParseModelObject<T>` in `ContextFabricValidation.cs`)

Autoregressive models emit two specific token-boundary artifacts that would
otherwise turn a correct answer into an unparseable one and grade it as failed
for the wrong reason:

1. **Keyword-suffix runs** — `falseC`, `trueX`, `nullValue` — where a JSON
   keyword token runs directly into the next word token with no boundary.
   `TrySanitizeLiteralSuffixes` walks the string state-aware and strips only
   out-of-string garbage suffixes.
2. **Unescaped inner quotes** — a model quotes a term inline without escaping
   it, which otherwise terminates the JSON string early and corrupts
   everything after. The sanitizer tracks whether the current string is an
   object key vs. a value before deciding whether a `:`/`,`/`}`/`]` is a real
   terminator.

The parser tries, in order: strict parse → lenient parse (trailing
commas/comments) → both sanitizer orders composed together (`keyword→quote`
and `quote→keyword`, since either artifact can appear first and partially
block the other's own internal validity check) → throw.

Separately, `ContextFabricBaselineRunner` splits its catch into
`JsonException` (counts as `Succeeded=true`, incorrect-answer-recorded) vs.
any other `Exception` (counts as `Succeeded=false`, a genuine runtime
failure) — so a run of B0/B1/B2 always reaches `RunCompleted=true` unless the
executor itself actually crashes, rather than an unparseable answer
masquerading as an infrastructure failure.

## 8. Gate metrics and thresholds

(`ContextFabricBenchmarkGateEvaluator.cs`) — current values, as of this
document's last edit:

| Metric | Target | Computed as | What it means if it fails |
|---|---|---|---|
| `segment_terminal_coverage` | 1.0 | `AcceptedSegments / ExpectedSegments` | Not every segment was accepted during ingestion — an ingestion bug, not a model problem |
| `question_pass_rate` | 1.0 | `PassedQuestions / TotalQuestions` | At least one held-out question failed verification |
| `citation_precision` | 0.90 | See §6.2 — aggregate, not mean | The model is citing segments that don't actually support its claims |
| `max_prompt_tokens` | ≤ context limit | `max(prompt tokens across all calls)` | The evidence pack overflowed the context budget |
| `boundary_stitch_pass_rate` | 1.0 | `passed stitch cases / total stitch cases` | A question spanning a segment boundary wasn't stitched correctly |

**Interpretation note:** `question_pass_rate`'s target is literally 100% — the
gate reports `NO-GO` unless *every one* of 120 held-out questions passes
verification exactly. This means B3 can substantially outscore every baseline
and the gate will still say `NO-GO`. When reviewing a gate report, look at the
`systems` table's raw pass counts, not just the top-line verdict, to judge
whether a NO-GO reflects "close but not perfect" or "still fundamentally
broken." (This all-or-nothing gate design is a known area under review — see
the open item tracked for a future phase.)

## 9. Known limitations (current, not historical)

- **Exhaustive-category classification is a heuristic** (§5.3) — a boundary
  case exists in theory with no regression test covering it yet.
- **`question_pass_rate` at 100%** produces a binary GO/NO-GO signal that
  doesn't distinguish "56/120 passed" from "119/120 passed" — both are
  NO-GO. Always read the raw systems table.
- **`citation_precision` is an aggregate, not a mean** (§6.2) — a small number
  of citation-heavy questions can dominate the reported number.
- **B4 measures a different, smaller benchmark entirely** (§2.1) — its
  presence in the systems table is not evidence about the current 120-question
  corpus.

For the story behind any of these — what was tried, what broke, what's still
open — see [CONTEXT_FABRIC_BUG_HISTORY.md](CONTEXT_FABRIC_BUG_HISTORY.md).
