# Context Fabric Retrieval Improvement Plan

**Status: Tier 1 in implementation as of 2026-07-07.** This document is the durable
reference for the retrieval-quality work that follows the NoKvSlot investigation
([CONTEXT_FABRIC_TEST_HARNESS.md §7a](CONTEXT_FABRIC_TEST_HARNESS.md)); per-run results
land in [CF_TEST_RESULTS.md](CF_TEST_RESULTS.md).

---

## 1. The evidence this plan is built on

From the Meta-Llama-3.1-8B 100-question run on NEWCOREPC (2026-07-06, artifact
`cf0_20260706_130803_138_*.json`), all 69 failed B3 questions were categorized by whether
the question's `expectedSegmentIds` appeared in the `includedSegmentIds` the model was
actually given:

| Failure category | Count |
|---|---|
| ALL required segments absent from the evidence pack (pure retrieval miss) | 49 |
| SOME required segments absent (partial miss) | 20 |
| Required segments present but the model answered wrong (model failure) | **0** |

**100% of failures are retrieval failures.** The reader stage is *not* implicated: spot
checks confirmed the correct evidence cards exist and contain the answer verbatim (e.g.
the card for `xseg-0002` contains "The recorded designation for Station Alpha was
BR-048" — the question that failed asking exactly that).

By question kind: LocalFact 19 pure misses, Paraphrased 15 pure, MultiHop 12 pure + 4
partial, Contradiction 2 pure + 6 partial, Exhaustive 10 partial, Unanswerable 1.

### Root mechanism (verified quantitatively)

`ContextFabricFeasibilityRunner.BuildEvidencePack` ranks cards by **unigram
bag-of-words IDF with binary membership** (`ScoreIdf`/`ScoreTextIdf`,
`ContextFabricFeasibilityRunner.cs:~1036`): the question is tokenized into single
lowercase words (hyphens are split boundaries), stopwords removed, and each card scores
the sum of `1/documentFrequency` over matching words.

Two fatal properties on entity-dense corpora:

1. **Multi-word entity names dissolve.** For "What was the recorded designation for
   Station Alpha?": of 105 accepted cards, 79 contain the token `alpha` (Chapter Alpha,
   Beacon Alpha, Depot Alpha…), 78 contain `station` (Station Kestrel, Station Cairn…),
   and **63 contain both** — every one scoring identically to the single correct card on
   those terms. Only **3 cards** contain the contiguous phrase "Station Alpha".
2. **Hyphenated identifiers dissolve too.** The tokenizer splits `BR-048` into
   `{br, 048}` and `case-ledger-01` into `{case, ledger, 01}` — `br`, `case`, `ledger`
   are corpus-universal, so an identifier's only distinguishing token is its numeric
   suffix, and even that collides (`CHN-112-0-1-2` vs `CHN-112`).
3. Ties break on `SegmentId` ordinal sort — deterministic, semantically arbitrary.

This also explains B2 (top-k RAG) outscoring B3 with the same model (44-49 vs 31):
B2 only needs the answer text *somewhere* in a large raw-text context; B3 ranks over
compressed cards *and* must cite the exact expected segment — it pays the retrieval tax
twice.

---

## 2. Tier 1 — Deterministic lexical fixes (no new dependencies) — **IN PROGRESS**

Attacks the proven mechanism directly. All changes in
`ContextFabricFeasibilityRunner.cs`, exercised by `ContextFabricEvidencePackTests.cs`.

### 1a. Anchor-phrase extraction and scoring

Extract **anchor phrases** from the question:

- **Identifier tokens**: hyphenated compounds matching `\b\w+(?:-\w+)+\b` — `BR-048`,
  `case-ledger-01`, `CHN-112-0-1-2`. Matched case-insensitively as contiguous
  substrings, so `CHN-112` and `CHN-112-0-1-2` stay distinct.
- **Proper-noun runs**: two or more consecutive capitalized words
  (`\b[A-Z][a-z0-9]*(?: [A-Z][a-z0-9]*)+\b`) — "Station Alpha", "Depot Fathom".
  Sentence-initial question words ("What was…") never match because the second word is
  lowercase.

Ranking becomes **lexicographic**: `(anchorScore DESC, unigramIdfScore DESC, segmentId)`
where `anchorScore = Σ 1/df(anchor)` over anchors contained contiguously
(case-insensitive) in the card haystack. Any anchor match outranks any amount of
unigram noise — no magic weighting constant to tune, and questions with no extractable
anchors degrade exactly to today's behavior.

Expected effect: collapses the "Station Alpha" candidate set from 63 tied cards to 3.
Directly addresses the 34 pure LocalFact/Paraphrased misses and most MultiHop misses.

### 1b. Coverage-aware greedy fill (MMR-style)

For the 20 partial misses (MultiHop/Contradiction/Exhaustive questions needing 2-5
specific segments): after each card is selected, discount anchors/terms it already
covered and pick the next card by *uncovered* score, so a question naming both
`RPT-064` and `CK-086` spends its budget covering both entities instead of stacking
near-duplicates of the first. When all question anchors/terms are covered, remaining
budget fills in original score order (preserves today's context-stuffing benefit for
GlobalSynthesis).

### 1c. Exhaustive-path anchor filter

`BuildExhaustiveAnswer`'s rarest-term filter has the same identifier-dissolution problem
(its own doc comment describes the `case-ledger-01` collision). Where the question
yields identifier anchors, use anchor containment as the entity filter instead of the
rarest-unigram heuristic.

### What Tier 1 deliberately does NOT touch

- **B2/B1/B0 baselines stay as-is.** They represent *conventional* alternatives; the
  benchmark's comparison stays honest. (BM25-with-phrases is arguably "conventional",
  but changing baselines mid-investigation would break comparability with all prior
  runs logged in CF_TEST_RESULTS.md.)
- The reader stage, verifier, and scoring/grading logic — all proven working.

### Validation gate for Tier 1

Re-run the 100-question suite on NEWCOREPC with Meta-Llama-3.1-8B (identical conditions
to the 2026-07-06 baseline run: 31/100 pass, 82% segment coverage, 99.1% citation
precision). Success criteria:

- Retrieval-miss count (expected-segment-absent) drops from 69 to near zero, measured
  by the same JSON categorization script.
- `question_pass_rate` rises materially (if retrieval is truly the whole story, the
  ceiling is whatever the model's real comprehension rate is — unknown until unmasked).
- No regression in `citation_precision` (was 99.1%).

Whatever failures *remain* after Tier 1 are the first genuine model-capability
measurements this benchmark has ever produced for B3.

---

## 3. Tier 2 — Embedding-based retrieval (deliberately deferred)

The conventional next step — rank cards by cosine similarity from a small embedding
model — is **ranked below Tier 1 for this failure mode, on purpose**:

- "Station Alpha designation" and "Depot Alpha designation" embed nearly identically;
  semantic similarity *inherits* the near-duplicate-entity collision rather than fixing
  it. Embeddings solve paraphrase, not entity precision.
- It adds a model dependency (a GGUF embedding model in the depot, VRAM, load time) and
  cross-version nondeterminism to a benchmark that currently reproduces byte-for-byte
  at `temperature=0`.

**When it becomes worth it**: if post-Tier-1 failures show *paraphrase* misses
(Paraphrased-kind questions failing because the question's wording shares no surface
tokens with the segment), a hybrid "lexical-anchor + embedding" score is the right
tool. Design sketch: embed cards once at corpus-build time (cacheable, deterministic
per model version), combine `anchorScore` lexicographically above
`α·cosine + β·unigramIdf`. Candidate models: any MiniLM/BGE-class GGUF via the
existing LLamaSharp embedding API; admission via `ModelAdmissionGate` with a new
`EmbeddingRetrieval` workload kind.

---

## 4. Tier 3 — Agentic retrieval (the strategic direction)

Two escalating designs, both aligned with TheOrc's long-term goal of a **tiny custom
Foundry model whose job is to *run* the Context Fabric framework**
([THEORC_FOUNDRY.md](THEORC_FOUNDRY.md)):

### 3a. LLM reranker pass

Retrieve top-20 candidates lexically (cheap, high recall), then one extra inference:
show the reader model the question plus the 20 card summaries, ask it to select the
relevant card IDs (strict JSON, same recovery machinery as every other CF stage). Cuts
the pack to genuinely-relevant cards before the answer stage.

- Cost: +1 inference per question (~5-10s on current fleet hardware).
- Risk: the reranker itself can drop the right card — needs its own precision/recall
  measurement against the same JSON categorization used in §1.

### 3b. Retrieval as a tool-call loop

Let the answering model drive: if its evidence pack lacks the entity it needs, it emits
a `fetch_segments(query)` tool call, the host runs the (Tier-1-improved) lexical search,
and the loop continues until the model asserts sufficiency or a budget cap hits. This is
"agentic RAG" — the frontier-system shape.

- This is exactly the narrow, well-defined, capturable skill the Foundry program wants:
  `question + current evidence → next retrieval query | answer`. Every passing CF run
  generates training traces for it (real-usage capture, per the standing self-hosted
  development preference).
- Prerequisite: Tier 1 must land first — the tool the agent calls *is* the lexical
  search; an agent driving a broken search inherits the brokenness.

### Decision gates

- Implement 3a only if Tier 1 leaves >10% of questions failing on retrieval.
- Design 3b's capture schema once B3 passes the gate on any model — the traces from
  passing runs are its dataset; don't build the trainer before the data exists.

---

## 5. Cross-cutting: measurement discipline

Every tier's claim gets validated the same way, no exceptions (lesson from this
investigation — two "obvious" fixes were disproven only because full runs were re-run
and compared):

1. Same machine, same model, same question count as the baseline being compared.
2. Categorize failures via the `expectedSegmentIds ⊆ includedSegmentIds` script against
   the raw `cf0_*.json`, not just the headline pass rate.
3. Log the run in [CF_TEST_RESULTS.md](CF_TEST_RESULTS.md) with the commit hash.
4. `temperature=0` determinism means identical results = change had no effect; treat
   that as disproof, not coincidence.
