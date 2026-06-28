# Context Fabric Critique Triage

> Last updated: 2026-06-27
> Purpose: turn the GLM 5.2 adversarial review into an evidence-backed disposition list and a targeted CF-0 through CF-2 revision plan.

---

## Why this exists

The Orc Context Fabric is ambitious enough that a strong adversarial review is useful. The goal of this document is not to "win the argument." The goal is to separate:

- critiques already addressed in code;
- critiques that expose doc/spec drift;
- critiques that need benchmark evidence;
- critiques that likely require design changes.

The current codebase is the source of truth over older prose. When doc and code disagree, the discrepancy must be fixed immediately because reviewers will reasonably judge the design from the published contract.

---

## Critique matrix

| GLM claim | Status | Repo evidence | Benchmark needed | Design action if validated |
|---|---|---|---|---|
| The model is being asked to count characters and emit authoritative SHA-256 digests. | `already addressed in code`, `doc drift is real` | `ContextFabricFeasibilityRunner` instructs the model to set `charStart/charEnd = -1`, emit the exact quote, and leave `quoteDigest` empty; `FabricEvidenceProcessor.NormalizeCitation` computes canonical offsets and digest host-side. | Quote anchoring across real admitted models. | Keep the host-trusted boundary; update all docs and schemas to present the model output as a draft rather than authoritative evidence. |
| A 3B local model failing CF-0 proves the theory is impossible. | `rejected` | The failure proves that the tested 3B model is below the quality bar, not that host-anchored evidence extraction is impossible. | Multi-model CF-0 reruns with admitted and provisional models. | Tighten model admission and stop using tiny models to judge the architecture. |
| Exhaustive mode is too slow to be an interactive replacement for long context. | `valid gap` | The main spec already describes Quick, Study, and Exhaustive as different modes, but the product language does not sharply separate interactive vs background-grade latency. | Exhaustive wall-clock and token-cost benchmarks across corpus sizes. | Treat Exhaustive as a premium verification mode and allow explicit background execution for large corpora. |
| Hierarchical reduction creates a telephone game that can drop rare facts. | `partial` | The spec already includes cognitive page faults and explicit coverage tracking, but there is no empirical hierarchy-loss benchmark yet. | Hierarchy-loss benchmark with single-occurrence low-salience facts. | If recall loss is too high, flatten reducer depth and bias toward source reopen rather than preserving a deep summary tree. |
| Boundary stitching may hallucinate links and then poison the hierarchy. | `valid gap` | Boundary stitching is part of the proposed architecture, but the current critique pass does not isolate stitch quality from hierarchy quality. | Boundary-stitch benchmark with cross-segment pronouns, split clauses, and overlap-dependent facts. | If stitch precision is weak, keep stitching narrow and subordinate to source reopen instead of treating stitched output as first-class truth. |
| Graph extraction from small models will be noisy and amplify hallucinations. | `valid gap` | The proposed graph is still pre-implementation. No benchmark yet proves safe relation extraction quality on local models. | Graph-noise benchmark with a restricted ontology and precision/recall scoring. | Demote graph edges to retrieval hints until verifier-backed relation classes are proven. |
| SQLite is the wrong tool for graph traversal at scale. | `partial` | The design already keeps SQLite as durable metadata storage and avoids remote writes, but traversal latency has not been benchmarked. | Bounded BFS and hybrid retrieval latency benchmarks at increasing graph sizes. | Keep SQLite as v1 truth store; add an in-memory traversal cache only if latency targets are missed. |
| The design underuses dense embeddings and leans too hard on lexical retrieval. | `partial` | The spec already includes embeddings, but the wording is conservative enough to imply they are secondary rather than co-equal when available. | Paraphrase-recall benchmark comparing BM25-only, embeddings-only, and hybrid retrieval. | Promote embeddings to a first-class retrieval input when an approved local embedding model is available. |
| The admission gate could solve the technical problem by silently requiring non-consumer hardware. | `valid gap` | The current admission logic is workload-aware, but the Context Fabric benchmark plan does not yet bind success to named hardware envelopes. | Hardware-profile benchmark across named local target tiers. | Treat consumer hardware as a release constraint; if no model passes inside the target envelope, relax workflow assumptions before raising hardware demands. |

---

## Locked decisions

- The model produces draft quotes. The host produces canonical offsets and digests.
- Summaries and graph edges are derived aids, never source truth.
- `Exhaustive` is verification-first, not the default chat path.
- SQLite remains the v1 durable store unless benchmark data shows it misses clear latency targets.
- Context Fabric architecture decisions should be promoted by benchmark evidence, not by chat competence or rhetorical confidence.
- Soft anchoring, if tested at all, starts as provisional evidence rather than silently trusted support.
- Consumer hardware is a product boundary, not just a benchmark convenience.

---

## Hypotheses to test

### H1. Quote anchoring is viable on admitted models

Success means:

- the model can return exact quotes often enough to clear CF-0 on admitted models;
- the host can repair malformed offsets and digests from the quote;
- ambiguous or invented quotes are rejected cleanly.

Failure interpretation:

- if admitted models still cannot return exact quotes, the evidence contract must loosen further or add a deterministic fallback extractor.

### H1a. Normalized exact-anchor repair is safe

Success means:

- the host can recover from punctuation, whitespace, line-break, smart-quote, and dash-variant drift without creating false supports;
- normalization materially improves anchor rate over exact raw matching.

Failure interpretation:

- keep anchor repair exact-only and fail closed when minor formatting drift breaks the quote.

### H1b. Soft anchoring can be measured without quietly becoming truth

Success means:

- token-overlap or edit-distance assisted anchoring can be benchmarked with an explicitly low false-positive rate;
- any soft-anchor result is clearly marked as provisional rather than silently promoted to trusted evidence.

Failure interpretation:

- drop soft anchoring entirely and require exact or normalized-exact quotes only.

### H2. Hierarchical reduction preserves enough recall with page-fault reopen

Success means:

- low-salience one-off facts remain recoverable through Quick or Study plus targeted reread;
- the system either recovers the fact or honestly reports incomplete confidence.

Failure interpretation:

- flatten hierarchy depth;
- increase adjacency and reopen bias;
- use summaries for navigation, not for final evidence decisions.

### H3. SQLite is good enough for first-scale traversal

Success means:

- bounded traversal and hybrid retrieval stay inside target latency for the intended first release corpus sizes.

Failure interpretation:

- preserve SQLite as truth and add an auxiliary in-memory traversal layer.

### H4. Embeddings materially improve paraphrase recall

Success means:

- hybrid retrieval materially beats lexical-only retrieval on low-overlap questions without causing quote-verification regressions.

Failure interpretation:

- if gains are marginal, keep embeddings optional;
- if gains are strong, promote them to default retrieval input when available.

### H5. Small-model graph extraction must be restricted

Success means:

- a narrow relation vocabulary can be extracted at acceptable precision and recall.

Failure interpretation:

- graph edges remain provisional retrieval hints;
- only verified claim/citation paths graduate to higher-trust use.

### H6. Boundary stitching does not introduce more errors than it resolves

Success means:

- overlap stitching improves recovery for cross-boundary references, split clauses, and pronoun-dependent facts;
- stitch output does not materially increase hallucinated joins or incorrect claim merges.

Failure interpretation:

- narrow the stitcher's remit to bounded adjacency help;
- prefer source reopen and pairwise evidence display over aggressive merged claims.

### H7. Context Fabric must clear a real consumer-hardware profile

Success means:

- at least one admitted or promotable model family can clear the benchmark gates within the intended local hardware envelope.

Failure interpretation:

- relax architecture expectations or schema strictness before quietly raising the hardware bar beyond the product promise.

---

## Benchmark suite additions

## 1. Quote anchoring benchmark

- Corpus: deterministic CF-0 corpus plus a small quote-ambiguity set.
- Models: one rejected tiny model baseline, one provisional 7B to 11B model, one admitted 12B+ model.
- Score:
  - exact quote rate
  - normalized-exact anchor rate
  - soft-anchor candidate rate
  - accepted evidence-card rate
  - host-repaired citation rate
  - ambiguous quote rejection rate
  - soft-anchor false-positive rate

Soft anchoring, if tested, must be reported separately from accepted trusted citations. It is a diagnostic lane until proven otherwise.

## 2. Paraphrase-recall benchmark

- Corpus: planted facts with intentionally low lexical overlap between source and question wording.
- Retrieval lanes:
  - BM25 only
  - embeddings only
  - hybrid
- Score:
  - recall@k
  - precision@k
  - downstream answer verification pass rate

## 3. Hierarchy-loss benchmark

- Corpus: facts that appear once inside otherwise low-salience segments.
- Query modes:
  - Quick
  - Study
  - hierarchical reduction plus reopen
- Score:
  - fact recovery rate
  - false "complete" rate
  - reopen trigger rate

## 4. Exhaustive-cost benchmark

- Corpus sizes:
  - small
  - medium
  - large
- Score:
  - wall-clock time
  - total LLM calls
  - prompt and completion token totals
  - coverage achieved
- Product outcome:
  - define when Exhaustive remains interactive and when it must become background-grade.

## 5. Graph-noise benchmark

- Corpus: synthetic entity/relation set with known gold labels.
- Restrict initial relation vocabulary to a small allow-list.
- Score:
  - entity precision/recall
  - relation precision/recall
  - contradiction tagging precision
  - provisional-edge rate vs verified-edge rate

## 6. Boundary-stitch benchmark

- Corpus: deterministic overlap fixtures with:
  - pronoun references split across boundaries
  - conclusions that begin in one segment and finish in the next
  - overlapping duplicate evidence that should not be double-counted
- Lanes:
  - no stitcher
  - stitcher enabled
- Score:
  - helped / unchanged / harmed rate
  - hallucinated-link rate
  - downstream answer-verification delta

## 7. SQLite traversal benchmark

- Generate increasing graph sizes from deterministic fixtures.
- Measure:
  - bounded BFS latency
  - hybrid search latency with FTS preselection
  - memory overhead
- Promotion rule:
  - only add an auxiliary traversal layer if clear first-scale latency targets are missed.

## 8. Consumer hardware profile benchmark

- Required target profiles:
  - Windows/NVIDIA baseline: single 12GB VRAM class GPU
  - Apple Silicon baseline: 16GB unified-memory class machine
  - optional CPU-only diagnostic lane for explicitly non-interactive runs
- Score:
  - benchmark pass/fail by profile
  - wall-clock latency
  - peak memory or VRAM consumption
  - admission verdict stability across profiles

Every benchmark run should record:

- resolved model display name and asset id;
- admission verdict;
- family and parameter estimate;
- corpus size and segmentation shape;
- precision/recall or pass/fail metrics;
- latency and token budget;
- hardware profile;
- whether any evidence relied on exact anchor, normalized anchor, or soft anchor;
- failure classification: `model-quality`, `planner-quality`, or `architecture-quality`.

---

## Targeted revision list

### CF-0

- **Complete:** correct the public evidence-card contract to match host-trusted citation normalization.
- **Complete:** persist benchmark environment metadata, prompt path, and bounded raw failure evidence in CF reports.
- **Complete:** run CF-0 only with workload-aware admission preflight and pin the exact selected bindings into execution.
- **Complete:** clear the real native exit gate on the pinned Hermes 3 Llama 3.1 8B lane: 16/16 segments, 5/5 questions, 100% citation precision, and all nine gates passed.

### CF-1

- Add paraphrase-recall and hierarchy-loss fixtures before shipping retrieval policy as settled.
- **Complete:** add quote-anchoring lanes for exact, normalized-exact, soft-anchor, and rejected hallucinated-anchor diagnostics.
- **Complete:** add a boundary-stitch benchmark; the pinned native lane passes 2/2 deterministic cases.
- Treat hybrid retrieval as the default design target when embeddings are available.
- Keep lexical search as mandatory fallback and exact-quote retrieval path.

### CF-2

- Restrict initial graph extraction to a bounded ontology and provisional trust class.
- Define a trust contract for `library_graph` and related UI/API surfaces:
  - `verificationStatus`
  - `trustLevel`
  - `confidence`
  - `evidenceCount`
  - visually distinct provisional vs verified edges
- Add traversal latency tests before committing to graph-heavy retrieval behavior.
- Keep graph edges as retrieval hints until verifier-backed relation classes are demonstrated.

### CF-3 readiness rule

- Do not declare Context Fabric product-ready unless at least one benchmark-cleared path fits the named consumer-hardware profiles.

---

## What this means in plain language

GLM was partly right, but not in the most fatal way it claimed.

The biggest technical correction is already in code: the model does not own authoritative offsets or hashes anymore. The real remaining risks are quote mutation, stitch hallucination, hierarchy loss, graph noise, exhaustive latency, and the current lack of benchmark evidence for embeddings, SQLite traversal, and consumer-hardware viability. Those are real and worth addressing directly.

Context Fabric should move forward by tightening the benchmark harness and correcting the public contract, not by abandoning the architecture or pretending the critique was wrong.
