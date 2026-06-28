# The Orc Context Fabric

> Status: CF-0 native feasibility gate passed; CF-1 implementation ready to begin
> Owner: TheOrc native runtime, OrcChat, CodeGraph, and HIVE MIND
> Last updated: 2026-06-27
> Product goal: make corpus size effectively independent of the active model context window while preserving source coverage, provenance, and reproducible answers on consumer hardware.

---

## Executive Summary

The Orc Context Fabric is a persistent, source-grounded memory and distributed reading system for OrcChat. It is intended to let a local native model work with books, manuals, document collections, and other corpora that are far larger than its prompt window.

The design does not claim that an 8K or 32K model performs literal dense attention over one billion tokens. That would be technically false. Instead, Context Fabric gives a finite-context model an effectively unbounded address space:

1. Every source is stored immutably and divided into overlapping, addressable segments.
2. Native reader jobs examine every segment and produce typed evidence linked to exact source locations.
3. Evidence is stitched into section, chapter, document, and corpus-level memory.
4. A document knowledge graph records claims, concepts, entities, relationships, contradictions, and citations.
5. OrcChat keeps only the current working set inside the model context.
6. Missing information causes a "cognitive page fault": the system reopens and rereads the original source.
7. Questions can use Quick, Study, or Exhaustive execution. Exhaustive mode can dispatch a question to every relevant source segment and prove the resulting coverage.
8. HIVE MIND distributes independent reader, reducer, query, and verification work units across enrolled native-runtime Warbands.

This is the practical bridge between consumer hardware and the need for enormous context. The source corpus lives outside the model; the model receives the smallest evidence set needed for the current reasoning step, with the ability to fetch more evidence recursively.

The core product promise is:

> OrcChat can locate, reread, connect, and verify any part of a corpus without requiring the entire corpus to fit in one model invocation.

---

## The Problem We Are Solving

A context window is working memory, not durable knowledge. Increasing it helps, but does not remove four practical limits:

- Attention and KV-cache costs increase with context length.
- Consumer GPUs and system RAM constrain usable context well before theoretical model limits.
- Models can overlook evidence placed deep inside long prompts.
- Repeatedly sending an entire book wastes time on text unrelated to the current question.

Ordinary retrieval-augmented generation improves efficiency but is incomplete. Top-k similarity search is good for local fact questions and weak for:

- "What are all of the arguments in this book?"
- "How does the author's definition change between chapters?"
- "Find every exception to this rule."
- questions whose answer requires several distant passages;
- questions that use different wording from the source;
- proving that the answer is absent from the whole corpus.

Context Fabric combines exhaustive preprocessing, hierarchical memory, lexical and semantic retrieval, graph traversal, distributed query fan-out, and source verification. No one layer is trusted by itself.

---

## Operational Definition Of "Infinite In Practice"

Context Fabric is successful when corpus size no longer determines whether OrcChat can use a source. Corpus size may increase indexing time, storage, and exhaustive-query latency, but it must not require a larger live prompt.

"Infinite in practice" means:

- The corpus address space is limited by disk, not the model's context length.
- Every source segment has a stable address and content digest.
- Every derived statement can be traced to one or more original source ranges.
- All derived memory can be deleted and rebuilt from immutable sources.
- The final synthesis prompt remains within a configured context budget.
- A global query can recursively map and reduce across all source segments.
- The system can report measured coverage rather than merely claiming that it read everything.
- A query can trade time for coverage by selecting a deeper execution mode.

It does not mean:

- Exact equivalence to a transformer attending over one billion tokens at once.
- Perfect recall or perfect reasoning.
- That summaries become authoritative substitutes for source text.
- That hundreds of model copies must reside in memory simultaneously.
- That adding more agents automatically improves quality.

The Fabric must always distinguish these properties in UI and documentation.

---

## Design Principles

### Source truth is immutable

Original files and normalized text artifacts are content-addressed by SHA-256. Derived summaries, embeddings, graph edges, and answers are disposable indexes. They never overwrite source truth.

### Summaries are caches, not truth

A summary accelerates navigation. It must preserve lineage to all child nodes and original segments. When a statement matters, OrcChat rehydrates the relevant source text.

### Coverage is measurable

Every reader and reducer output carries a coverage manifest. A parent summary is complete only when its manifest accounts for every expected child or explicitly records missing children.

### Context is a budgeted working set

The query planner allocates prompt tokens among instructions, conversation state, evidence, counter-evidence, and response reserve. Retrieval cannot silently consume the entire window.

### Readers are logical workers

Hundreds of reader jobs may exist, while each physical machine normally keeps one native model binding loaded and executes only as many concurrent generations as its memory budget permits.

### Claims require evidence

Answers are assembled from typed claims and citations. Unsupported model prose is not promoted into durable corpus memory.

### Verification is independent

Important answers are checked by a separate pass that receives the proposed claims and cited source segments, not the original chain-of-thought or a request to agree.

### Native means native

Context Fabric inference runs through `NativeRoleRuntime` and the shared headless loop. Campaign jobs do not invoke Ollama or silently fall back to it.

---

## User Experience

### Library workflow

OrcChat gains a local Library surface:

1. Add a file, folder, or approved URL.
2. Select or create a corpus.
3. Review detected format, edition, digest, source extent (pages, blocks, or segments), and parser warnings.
4. Start indexing locally or across HIVE.
5. Watch parse, read, stitch, reduce, graph, and verify progress.
6. Attach the completed corpus to a conversation.
7. Ask questions with visible source scope and reasoning depth.

### Query modes

| Mode | Behavior | Expected use |
|---|---|---|
| **Quick** | Hybrid retrieval over segment text, evidence cards, summaries, and graph neighbors. | Direct facts and routine questions. |
| **Study** | Iterative retrieval plus targeted rereading of relevant and adjacent segments. | Multi-hop questions, interpretation, and comparison. |
| **Exhaustive** | Dispatch the question across every segment or every qualifying subtree, recursively reduce findings, then verify citations with code-computed coverage and explicit budgets. | "Find all," absence claims, audits, and high-confidence research; not the default interactive path. |

The response UI shows:

- corpus and edition;
- query mode;
- source coverage in format-appropriate units, such as `912 / 912 pages considered` for a paginated PDF or `384 / 384 segments considered` for text;
- direct citations with page and heading anchors;
- whether citations were independently verified;
- whether the run remained interactive-grade or crossed into background-grade latency;
- unresolved conflicts or missing pages;
- model, adapter, prompt, parser, and index versions in expandable provenance;
- an explicit warning when an answer is interpretive rather than directly stated.

### Source-bound expertise

The persona layer may say "answer as a source-bound Darwin scholar" or "use the attached DSM edition," but the system must not claim professional authority it does not possess.

For medical or mental-health material:

- answers identify the edition and cite the supplied source;
- educational interpretation is allowed;
- the assistant does not impersonate a licensed clinician;
- diagnostic or treatment claims use the product's medical safety policy;
- the local source is not redistributed by HIVE beyond enrolled nodes authorized for that corpus.

---

## System Architecture

```text
                           ORCCHAT
              +-------------------------------+
              | corpus selector + query mode  |
              | answer + citations + coverage |
              +---------------+---------------+
                              |
                              v
              +---------------+---------------+
              | Context Fabric Query Planner  |
              | context budget + search plan  |
              +----+-------------+------------+
                   |             |
          local/graph search     | HIVE query fan-out
                   |             |
                   v             v
       +-----------+-----+   +---+------------------+
       | Document Graph  |   | Campaign Engine      |
       | FTS + vectors   |   | readers/reducers     |
       | hierarchy       |   | verifiers/retries    |
       +-----------+-----+   +---+------------------+
                   |             |
                   +------+------+
                          v
              +-----------+------------+
              | Evidence Pack Builder  |
              | dedupe + counterproof  |
              | token-bounded packing  |
              +-----------+------------+
                          |
                          v
              +-----------+------------+
              | NativeRoleRuntime      |
              | answer + claim draft   |
              +-----------+------------+
                          |
                          v
              +-----------+------------+
              | Citation Verifier      |
              | reopen original text   |
              +-----------+------------+
                          |
                          v
                    accepted answer

 Immutable artifacts: .orc/fabric/objects/<sha256>
 Operational metadata: .orc/theorc.db
```

### Processing pipeline

```text
source
  -> parse and normalize
  -> structural segmentation with overlap
  -> segment reader jobs
  -> boundary stitching
  -> claim/entity/relation extraction
  -> section reducers
  -> chapter reducers
  -> document reducer
  -> corpus communities and summaries
  -> query planning
  -> source rehydration
  -> answer synthesis
  -> citation verification
```

---

## Core Components

### `FabricLibraryService`

Owns corpus creation, source import, versioning, deletion, rebuild requests, and status reporting. It coordinates services but does not perform model inference.

### `DocumentParserRegistry`

Selects deterministic parsers by media type. The first release supports plain text, Markdown, and text-based PDF. EPUB, DOCX, OCR, and multimodal page parsing follow through the native function-pack plan.

Parser output is a normalized document artifact containing:

- ordered blocks;
- page or location anchors;
- headings and hierarchy;
- tables, captions, and footnotes when available;
- character offsets into normalized text;
- parser warnings and confidence;
- parser name and version.

Untrusted complex documents should be parsed in a bounded local process or trusted container pack. Parsing code must never interpret document text as instructions.

### `FabricSegmenter`

Creates stable segments along semantic boundaries while respecting a token target. It must:

- prefer headings, paragraphs, and list boundaries;
- include configurable overlap;
- preserve page and character ranges;
- emit neighboring segment IDs;
- avoid splitting tables when possible;
- derive stable IDs from document digest, chunker version, and source range;
- record expected segment count before readers begin.

The initial target is 1,500-3,000 model tokens per segment with 10-15% overlap. Page groups are a UI concept; token-aware structural segments are the execution unit.

### `FabricReader`

A reader is a native work unit over one segment plus lightweight structural context. It emits an evidence card, not an unconstrained essay.

Reader roles can be composed without loading separate models:

- extractor: claims, definitions, examples, exceptions, and citations;
- linker: entities, concepts, relationships, and cross-references;
- skeptic: ambiguity, contradictions, limitations, and missing context;
- visual reader: figures, tables, layout, and OCR evidence when supported.

The first release uses one combined reader prompt to control cost. Separate role passes are benchmarked as an ablation before becoming default.

### `BoundaryStitcher`

Examines adjacent segment cards and their overlap. It resolves duplicate claims, incomplete sentences, heading transitions, pronoun references, and concepts that span a segment boundary.

### `FabricReducer`

Builds a hierarchy of memory nodes. Each reducer receives child evidence artifacts and produces:

- a bounded summary;
- canonical claims;
- conflicts and minority interpretations;
- entity and relationship updates;
- child membership list;
- a coverage digest;
- questions requiring source rehydration.

Reduction repeats until the root fits inside a configured global-summary budget. A large corpus can add levels without changing the final context budget.

### `DocumentGraphRepository`

Persists document-specific graph and provenance metadata in SQLite. It is intentionally separate from `GraphRepository`, whose node and edge semantics are code-specific.

### `FabricSearchService`

Runs hybrid retrieval:

1. FTS5/BM25 lexical candidates.
2. Embedding similarity candidates when an approved local embedding model is available.
3. Graph expansion around matching claims, concepts, citations, and summaries.
4. Hierarchy expansion to parents, children, and adjacent source segments.
5. Optional native reranking within a strict candidate budget.
6. Diversity selection so one repetitive chapter does not crowd out other evidence.

Lexical search remains a required fallback. Context Fabric's retrieval direction is hybrid: when an approved local embedding model is available, embeddings should be treated as a first-class retrieval input alongside FTS and graph/hierarchy expansion. The lexical path remains mandatory because the system must still function without embeddings and must retain exact-match recall for quote-driven verification.

### `FabricQueryPlanner`

Classifies a question as local, multi-hop, global, exhaustive, comparative, temporal, contradiction-seeking, or absence-seeking. It produces a deterministic plan with budgets and stop conditions.

The planner can request additional evidence after synthesis, but every loop is bounded by:

- maximum rounds;
- maximum segment rereads;
- maximum prompt tokens;
- maximum wall-clock time;
- maximum HIVE work units;
- maximum artifact bytes.

### `EvidencePackBuilder`

Builds the live model context. It reserves tokens in this order:

1. system and safety instructions;
2. user question and required output contract;
3. corpus identity and global orientation;
4. direct evidence and citations;
5. counter-evidence and unresolved conflicts;
6. concise conversation memory;
7. response reserve.

Evidence is deduplicated by claim and source range. The pack records anything excluded by the token budget so the planner can issue a second pass if needed.

### `FabricCitationVerifier`

Takes answer claims and cited ranges, reloads the exact normalized source blocks, and labels each claim:

- supported;
- partially supported;
- contradicted;
- citation mismatch;
- interpretive;
- unverifiable.

The verifier may repair citations but may not silently strengthen a claim. High-risk answers fail closed when required evidence is missing.

---

## Evidence Card Contract

Reader output must validate against a versioned JSON schema, but the reader is not treated as an authoritative offset or digest engine. The model returns a draft evidence card with exact quotes; the trusted host computes canonical offsets and digests from the normalized source text and rejects any draft that cannot be anchored unambiguously.

A representative draft contract is:

```json
{
  "schemaVersion": "cf0-evidence-card-1.0",
  "corpusId": "corpus-darwin-origin-1859",
  "documentId": "doc-sha256-prefix",
  "segmentId": "seg-sha256-prefix",
  "promptVersion": "cf0-reader-1.0",
  "summary": "Bounded segment orientation, not a replacement for the source.",
  "claims": [
    {
      "claimId": "c1",
      "type": "assertion",
      "text": "Canonical claim text distilled from the segment",
      "confidence": 0.84,
      "citations": [
        {
          "segmentId": "seg-sha256-prefix",
          "charStart": -1,
          "charEnd": -1,
          "quote": "Exact source substring copied from the segment.",
          "quoteDigest": ""
        }
      ]
    }
  ],
  "entities": [],
  "conflicts": [],
  "openQuestions": []
}
```

Rules:

- Unknown fields are rejected for released schemas.
- Text and collection lengths are bounded.
- Draft citations must be anchorable to an exact quote inside the assigned segment.
- Canonical `charStart`, `charEnd`, and `quoteDigest` are computed by the Warchief or trusted local host.
- The worker cannot mark evidence accepted; it can only submit it.
- Ambiguous or unanchorable quotes are rejected rather than silently coerced.
- Invalid output is retried with a schema-repair prompt, then failed explicitly.
- Raw chain-of-thought is neither requested nor stored.

---

## CodeGraph Integration

CodeGraph is the architectural precedent and a continuing peer index. Context Fabric must reuse its proven patterns without corrupting its code semantics.

### What is reused

- The single workspace `SqliteStore` at `.orc/theorc.db`.
- WAL mode, pooled per-operation connections, foreign keys, and busy timeout.
- Ordered, transactional migrations.
- `RepositoryBase` parameter binding and transaction discipline.
- Background lifecycle attachment when a workspace opens.
- FTS5 indexes synchronized with canonical relational rows.
- Idempotent bulk replacement and incremental rebuild patterns.
- Explicit node and edge models.
- Bounded graph traversal and degree-based diagnostics.
- Tool surfaces that return compact, model-readable results.
- Provenance-first behavior and index rebuildability.

### What remains separate

`GraphRepository` continues to own Roslyn-derived code symbols and structural edges such as `CALLS`, `IMPLEMENTS`, and `ROUTES_TO`.

`DocumentGraphRepository` owns document segments, claims, citations, entities, summaries, and relationships such as:

- `SUPPORTS`;
- `CONTRADICTS`;
- `DEFINES`;
- `EXEMPLIFIES`;
- `QUALIFIES`;
- `REFERS_TO`;
- `DERIVED_FROM`;
- `SUMMARIZES`;
- `PRECEDES`;
- `SAME_AS`.

Do not add document rows to `graph_nodes`. Code nodes require qualified names, files, source lines, and complexity metrics; document nodes require corpus identity, editions, page ranges, confidence, coverage, and source citations. A forced shared table would weaken constraints and make both query models ambiguous.

### Shared graph primitives

After the document graph is working, extract only proven common mechanics:

- `FtsQueryNormalizer`;
- bounded breadth-first traversal;
- graph path result formatting;
- degree and neighborhood calculations;
- lifecycle status events;
- index generation/version records.

Do not begin implementation with a broad `GenericGraphRepository` refactor. First build `DocumentGraphRepository` beside CodeGraph, benchmark it, then extract duplication that is demonstrably stable.

### Cross-graph links

A later bridge table may connect document evidence to code symbols, for example a specification paragraph to the method that implements it. Cross-graph edges store typed references, not foreign keys into a universal node table:

```text
fabric_external_links
  source_kind      document_claim | summary | segment
  source_id
  target_kind      code_node | campaign | artifact
  target_key
  relation_type
  confidence
  provenance_digest
```

This permits questions such as "Which requirements in this manual are implemented by this class?" while allowing each graph to evolve independently.

### Tool integration

The initial read-only tool family is:

- `library_list`: list corpora, editions, status, and coverage.
- `library_search`: hybrid search with source anchors.
- `library_open`: load exact source ranges.
- `library_graph`: inspect entities, claims, relationships, and paths.
- `library_ask`: run Quick, Study, or Exhaustive source-bound QA.
- `library_verify`: verify answer claims and citations.

`library_graph` must not blur provisional graph hints with verified knowledge. Graph responses should carry enough trust metadata for OrcChat and future graph UI to distinguish:

- verified edges and nodes;
- provisional edges produced by unverified extraction;
- rejected or contradicted candidate links;
- confidence and evidence counts for every displayed relationship.

If provisional graph extraction ships before verifier-backed relation quality is proven, the UI should render those relationships as explicitly tentative rather than visually authoritative.

`AgentLoop` and OrcChat should prefer CodeGraph tools for code structure and Context Fabric tools for document knowledge. Mixed questions can use both through an explicit planner step.

---

## SQLite And Artifact Architecture

### Ownership model

The Warchief or local app process is the only SQLite writer. Warbands never open the Warchief database over a file share. They receive immutable input artifacts over authenticated HIVE endpoints and return content-addressed output artifacts.

SQLite contains operational metadata, searchable normalized text, graph structure, provenance, and benchmark metrics. Large originals, normalized documents, evidence-card payloads, images, and generated reports live in content-addressed storage.

### Migration strategy

Context Fabric begins with a new additive migration after the current campaign migration. Migrations must remain forward-only and transactional. No existing CodeGraph or campaign table is repurposed.

Suggested migration sequence:

- v8: corpora, documents, segments, normalized text, and FTS.
- v9: evidence cards, claims, citations, entities, and relations.
- v10: hierarchy, coverage, embeddings, and query provenance.
- v11: campaign stages, dependencies, and benchmark runs if not delivered earlier through a campaign-engine migration.

The exact migration numbers must be assigned from current repository state at implementation time.

### Proposed schema

#### Corpus and source tables

```text
fabric_corpora
  corpus_id TEXT PRIMARY KEY
  name TEXT NOT NULL
  description TEXT
  policy_profile TEXT NOT NULL
  status TEXT NOT NULL
  created_at TEXT NOT NULL
  updated_at TEXT NOT NULL

fabric_documents
  document_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  source_digest TEXT NOT NULL
  normalized_digest TEXT
  display_name TEXT NOT NULL
  media_type TEXT NOT NULL
  edition TEXT
  parser_id TEXT NOT NULL
  parser_version TEXT NOT NULL
  page_count INTEGER
  status TEXT NOT NULL
  warnings_json TEXT NOT NULL
  created_at TEXT NOT NULL
  UNIQUE(corpus_id, source_digest, parser_id, parser_version)

fabric_segments
  segment_id TEXT PRIMARY KEY
  document_id TEXT NOT NULL REFERENCES fabric_documents ON DELETE CASCADE
  ordinal INTEGER NOT NULL
  heading_path TEXT
  page_start INTEGER
  page_end INTEGER
  char_start INTEGER NOT NULL
  char_end INTEGER NOT NULL
  token_count INTEGER NOT NULL
  text_digest TEXT NOT NULL
  previous_segment_id TEXT
  next_segment_id TEXT
  chunker_version TEXT NOT NULL
  UNIQUE(document_id, ordinal, chunker_version)

fabric_segment_text
  segment_id TEXT PRIMARY KEY REFERENCES fabric_segments ON DELETE CASCADE
  normalized_text TEXT NOT NULL

fabric_segment_fts
  FTS5 external-content index over normalized_text and heading_path
```

FTS triggers must mirror CodeGraph's external-content pattern. Tests must cover insert, update, delete, rebuild, Unicode tokenization, punctuation-heavy queries, and malformed MATCH input.

#### Evidence and graph tables

```text
fabric_derivation_runs
  derivation_run_id TEXT PRIMARY KEY
  segment_id TEXT NOT NULL REFERENCES fabric_segments ON DELETE CASCADE
  campaign_id TEXT
  work_unit_id TEXT
  stage_kind TEXT NOT NULL          read | stitch | reduce | verify
  model_hash TEXT NOT NULL
  adapter_hash TEXT
  backend TEXT NOT NULL
  prompt_version TEXT NOT NULL
  schema_version TEXT NOT NULL
  attempt INTEGER NOT NULL
  status TEXT NOT NULL
  artifact_digest TEXT
  prompt_tokens INTEGER
  completion_tokens INTEGER
  duration_ms INTEGER
  worker_id TEXT
  trace_digest TEXT
  error_code TEXT
  created_at TEXT NOT NULL

fabric_claims
  claim_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  canonical_text TEXT NOT NULL
  claim_type TEXT NOT NULL
  confidence REAL NOT NULL
  status TEXT NOT NULL
  canonical_hash TEXT NOT NULL
  UNIQUE(corpus_id, canonical_hash, claim_type)

fabric_claim_derivations
  claim_id TEXT NOT NULL REFERENCES fabric_claims ON DELETE CASCADE
  derivation_run_id TEXT NOT NULL REFERENCES fabric_derivation_runs ON DELETE CASCADE
  contribution_kind TEXT NOT NULL  created | confirmed | contradicted | merged
  PRIMARY KEY(claim_id, derivation_run_id, contribution_kind)

fabric_citations
  citation_id TEXT PRIMARY KEY
  claim_id TEXT NOT NULL REFERENCES fabric_claims ON DELETE CASCADE
  segment_id TEXT NOT NULL REFERENCES fabric_segments ON DELETE CASCADE
  page_number INTEGER
  char_start INTEGER NOT NULL
  char_end INTEGER NOT NULL
  quote_digest TEXT NOT NULL
  verification_status TEXT NOT NULL

fabric_entities
  entity_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  entity_type TEXT NOT NULL
  canonical_name TEXT NOT NULL
  normalized_name TEXT NOT NULL
  description_digest TEXT
  UNIQUE(corpus_id, entity_type, normalized_name)

fabric_entity_aliases
  entity_id TEXT NOT NULL REFERENCES fabric_entities ON DELETE CASCADE
  alias TEXT NOT NULL
  normalized_alias TEXT NOT NULL
  source_segment_id TEXT REFERENCES fabric_segments
  PRIMARY KEY(entity_id, normalized_alias)

fabric_relations
  relation_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  src_kind TEXT NOT NULL
  src_id TEXT NOT NULL
  dst_kind TEXT NOT NULL
  dst_id TEXT NOT NULL
  relation_type TEXT NOT NULL
  confidence REAL NOT NULL
  evidence_digest TEXT NOT NULL
  created_by_run TEXT NOT NULL REFERENCES fabric_derivation_runs
  UNIQUE(corpus_id, src_kind, src_id, dst_kind, dst_id, relation_type, evidence_digest)

fabric_claim_fts
  FTS5 external-content index over fabric_claims.canonical_text

fabric_entity_fts
  contentless FTS5 index with entity_id UNINDEXED, canonical_name, and aliases;
  maintained in the same transaction as entity and alias changes
```

Polymorphic graph IDs are validated in repository code against strict kind allow-lists. External input never controls table or column names.

#### Hierarchy and coverage tables

```text
fabric_memory_nodes
  memory_node_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  document_id TEXT REFERENCES fabric_documents ON DELETE CASCADE
  parent_id TEXT REFERENCES fabric_memory_nodes ON DELETE CASCADE
  level TEXT NOT NULL              segment | section | chapter | document | corpus
  ordinal INTEGER NOT NULL
  title TEXT
  summary_digest TEXT NOT NULL
  reducer_model_hash TEXT NOT NULL
  reducer_prompt_version TEXT NOT NULL
  expected_child_count INTEGER NOT NULL
  covered_child_count INTEGER NOT NULL
  coverage_digest TEXT NOT NULL
  status TEXT NOT NULL

fabric_memory_members
  parent_id TEXT NOT NULL REFERENCES fabric_memory_nodes ON DELETE CASCADE
  child_kind TEXT NOT NULL         segment | memory
  child_id TEXT NOT NULL
  ordinal INTEGER NOT NULL
  PRIMARY KEY(parent_id, child_kind, child_id)

fabric_embeddings
  object_kind TEXT NOT NULL
  object_id TEXT NOT NULL
  embedding_model_hash TEXT NOT NULL
  dimensions INTEGER NOT NULL
  vector_blob BLOB NOT NULL
  vector_norm REAL NOT NULL
  created_at TEXT NOT NULL
  PRIMARY KEY(object_kind, object_id, embedding_model_hash)
```

The first implementation may calculate cosine similarity in a bounded in-memory candidate set after FTS preselection. A packaged vector extension is optional only after cross-platform native loading, migration, corruption recovery, and performance are tested. Context Fabric must remain functional without it.

#### Query and answer provenance

```text
fabric_query_runs
  query_run_id TEXT PRIMARY KEY
  corpus_id TEXT NOT NULL REFERENCES fabric_corpora ON DELETE CASCADE
  mode TEXT NOT NULL
  question_hash TEXT NOT NULL
  planner_version TEXT NOT NULL
  model_hash TEXT NOT NULL
  adapter_hash TEXT
  context_limit INTEGER NOT NULL
  evidence_token_budget INTEGER NOT NULL
  status TEXT NOT NULL
  segments_considered INTEGER NOT NULL
  segments_total INTEGER NOT NULL
  prompt_tokens INTEGER
  completion_tokens INTEGER
  duration_ms INTEGER
  answer_digest TEXT
  created_at TEXT NOT NULL

fabric_query_evidence
  query_run_id TEXT NOT NULL REFERENCES fabric_query_runs ON DELETE CASCADE
  evidence_kind TEXT NOT NULL
  evidence_id TEXT NOT NULL
  rank INTEGER NOT NULL
  lexical_score REAL
  vector_score REAL
  graph_score REAL
  included_in_prompt INTEGER NOT NULL
  PRIMARY KEY(query_run_id, evidence_kind, evidence_id)

fabric_answer_claims
  query_run_id TEXT NOT NULL REFERENCES fabric_query_runs ON DELETE CASCADE
  answer_claim_id TEXT NOT NULL
  claim_text TEXT NOT NULL
  verification_status TEXT NOT NULL
  verifier_run_id TEXT
  PRIMARY KEY(query_run_id, answer_claim_id)
```

### Transaction and concurrency rules

- Corpus creation and document registration are atomic.
- Segment replacement for one document occurs in one transaction.
- Reader artifacts are hash-verified before relational evidence is committed.
- Evidence-card import is one transaction per card or bounded batch.
- Reducer nodes are committed only after every declared child and coverage digest validate.
- Failed imports leave artifacts quarantined but no partially accepted graph rows.
- Read queries use pooled connections under WAL.
- Bulk writes are serialized through a bounded Warchief ingestion channel to prevent many completed workers from stampeding SQLite.
- Every SQL value is parameterized through `RepositoryBase`; dynamic graph kinds and sort modes use allow-lists.

### Rebuild and invalidation

Derived data is keyed by:

- source digest;
- parser ID and version;
- chunker version;
- evidence schema version;
- reader model and adapter hashes;
- reader prompt version;
- reducer model and prompt version;
- embedding model hash.

Changing one key marks only dependent generations stale. Old generations remain query-ineligible but retained until the configured cleanup window expires. Rebuild never mutates source artifacts.

---

## HIVE Distributed Execution

### Why HIVE fits

Context Fabric consists of many independent, verifiable work units. It does not require low-latency tensor exchange. That matches HIVE's throughput-oriented campaign model:

- segment readers can run independently;
- boundary stitchers depend only on neighboring cards;
- reducers form bounded fan-in stages;
- exhaustive questions can fan out across all segments;
- verifiers can be assigned to a different node;
- stale claims and retries are already part of campaign execution.

### Physical and logical concurrency

A 900-page book might produce 300-500 segment jobs. Those are logical readers. On three consumer machines, HIVE may execute only three native generations at once if each machine exposes one slot. Faster machines can advertise additional slots only when runtime admission proves sufficient RAM or VRAM.

The goal is a deep durable queue, not hundreds of simultaneous model copies.

### Context Fabric campaign pack

Add a built-in pack:

```text
PackId: theorc.context-fabric
Version: 1.0.0
ExecutionKind: native_agent
Required inputs: normalized segment artifact or reducer child bundle
Required model: exact native GGUF hash
Optional adapter: exact LoRA hash
Output: schema-valid JSON artifact plus execution attestation
Network during execution: none
```

### Required campaign-engine extensions

The existing engine can submit and lease flat campaigns. Context Fabric requires a dependency-aware stage model:

```text
CampaignStage
  StageId
  Kind                 parse | read | stitch | reduce | graph | query | verify
  DependsOnStageIds
  CompletionPolicy     all | quorum | threshold
  FailurePolicy        fail | continue_with_gap | retry_then_fail
  MaxParallelism

WorkUnit additions
  StageId
  DependsOnWorkUnitIds
  OutputSchemaVersion
  DeterministicSeed
  SourceGeneration
```

Stage release is atomic: work in a dependent stage cannot be leased until its completion policy is satisfied and input artifacts are committed.

For very large fan-in, reducers reference an artifact manifest rather than embedding thousands of dependency IDs in a task bundle.

### Native input staging gap

Today, container packs materialize `InputArtifacts`; native-agent jobs do not. Before Context Fabric reader campaigns are valid, `HiveWorkerAgent` must:

1. Resolve every declared input through the authenticated artifact endpoint.
2. Resume interrupted downloads.
3. Verify declared size and SHA-256.
4. Materialize files beneath the job's isolated `.orc/remote-work/<campaign>/<unit>/inputs` directory.
5. Expose only those paths to the headless tool profile.
6. Reject path traversal, duplicate names, undeclared files, and quota overflow.
7. Include all input digests in execution attestation.

The native reader receives a file path and source metadata, not the whole segment pasted into `Spec`.

### Distributed stage sequence

1. The Warchief parses or accepts a normalized document and computes the source manifest.
2. It stores segment artifacts and registers expected segment count.
3. Read-stage work units are scheduled by cached-input locality and loaded model hash.
4. Warbands run `NativeRoleRuntime`, validate local output shape, and upload evidence artifacts.
5. The Warchief verifies hashes, schema, offsets, and quote digests before importing evidence.
6. Boundary and reducer stages are released when dependencies complete.
7. Missing or failed segments prevent a `complete` coverage state unless policy explicitly permits a visible gap.
8. Verification samples are rerun on different nodes.
9. Query campaigns use the completed generation and cannot mix incompatible index generations.

### Exhaustive query campaign

An Exhaustive question creates one query work unit per leaf segment or selected subtree. Each worker receives:

- the exact question;
- the assigned source artifact;
- neighboring headings and global corpus orientation;
- a strict finding schema;
- instructions to return `relevant=false` when no evidence exists;
- no findings from other workers.

Reducers merge findings without discarding minority or contradictory evidence. The final verifier reopens every citation used in the answer. Coverage reports both completed leaves and failed leaves.

### Fault tolerance

- Work-unit IDs are deterministic and idempotent.
- At-least-once execution is safe because evidence imports are keyed by generation and canonical hash.
- Expired leases are reissued.
- Late stale-token results are rejected.
- A retry preserves the source generation but increments attempt and records worker/model telemetry.
- Reducers cannot start from uncommitted artifacts.
- Cancellation rejects late completion while retaining already accepted source and audit artifacts.

---

## Benchmark And Feasibility Program

The benchmark must be capable of disproving the design. A polished answer is not sufficient evidence.

### Experimental systems

Every benchmark uses the same native model hash, adapter hash, deterministic seed where supported, temperature, output budget, and question text.

| ID | System | Purpose |
|---|---|---|
| B0 | Closed-book native model | Measures memorization and unsupported confidence. |
| B1 | Truncated prompt | Establishes the finite-context floor and position effects. |
| B2 | Conventional lexical/vector top-k RAG | Establishes the normal local-RAG baseline. |
| B3 | Single-node Context Fabric | Measures the architecture's quality independent of HIVE. |
| B4 | HIVE Context Fabric | Measures distributed throughput, recovery, and result equivalence. |

Optional diagnostic runs may compare native runtime implementations, but production acceptance does not depend on Ollama and no Context Fabric campaign may fall back to it.

### Corpus A: deterministic synthetic book

Generate a versioned 128-section corpus from a fixed seed. It contains:

- one unique canary fact per segment;
- facts placed at the beginning, middle, and end;
- paraphrased facts with low lexical overlap;
- 30 cross-segment two-hop chains;
- 15 three-to-five-hop chains;
- 20 deliberate contradictions with dated or scoped resolutions;
- repeated names referring to different entities;
- tables and lists;
- 20 questions with no answer;
- an exact set of occurrences for exhaustive enumeration;
- instructions inside source text that attempt to manipulate the reader.

The generator emits a private ground-truth manifest containing every answer, supporting segment, citation range, relationship, and expected absence. The system under test never receives that manifest.

### Corpus B: real public-domain book

Use one pinned edition of Charles Darwin's *On the Origin of Species* from Project Gutenberg. Store:

- source URL;
- download timestamp;
- source SHA-256;
- edition;
- parser and chunker versions;
- a checked-in benchmark question manifest that does not contain copyrighted modern commentary.

Questions are independently authored and reviewed. They cover direct facts, argument structure, examples, exceptions, terminology, cross-chapter synthesis, and global themes.

### Corpus C: standardized long-context subset

Add a pinned subset of LongBench/LongBench v2 tasks where licensing permits local evaluation. Preserve original task IDs and official scoring. This tests whether gains transfer beyond our custom fixtures.

### Question suite

The initial controlled suite contains at least 150 questions:

| Category | Minimum | Primary measurement |
|---|---:|---|
| Needle/local fact | 40 | exact answer and citation recall |
| Paraphrased retrieval | 20 | retrieval recall under lexical mismatch |
| Multi-hop | 30 | complete evidence-chain recovery |
| Global synthesis | 15 | rubric coverage and diversity |
| Exhaustive enumeration | 15 | precision and recall over all occurrences |
| Contradiction/change | 10 | conflict detection and correct scoping |
| Unanswerable | 20 | calibrated abstention and false-citation rate |

Questions and expected evidence are split into development and held-out sets. Prompt tuning uses only development questions.

### Metrics

#### Ingestion metrics

- parse coverage: source blocks represented / expected blocks;
- segment coverage: accepted cards / expected segments;
- schema-valid output rate;
- citation-offset validity rate;
- duplicate-claim rate;
- unresolved boundary rate;
- indexing wall time;
- tokens, artifact bytes, and energy proxy per source token;
- rebuild determinism by artifact digest.

#### Retrieval metrics

- Recall@k for expected segments and claims;
- mean reciprocal rank;
- evidence diversity across chapters;
- graph-path recall for multi-hop questions;
- false-positive retrieval rate;
- percentage of answer-critical evidence excluded by context packing.

#### Answer metrics

- exact match or token F1 where appropriate;
- multi-hop chain completeness;
- exhaustive precision, recall, and F1;
- citation precision: cited ranges that support the attached claim;
- citation recall: gold supporting ranges recovered;
- claim support rate;
- contradiction handling accuracy;
- no-answer precision and recall;
- unsupported-claim rate;
- global-question rubric score.

Automated exact and citation metrics are primary. A blinded human rubric is primary for subjective synthesis. A model judge may be reported only as a secondary metric and must not be the sole acceptance gate.

#### Context and performance metrics

- maximum and mean synthesis prompt tokens;
- number of source tokens represented per live prompt token;
- time-to-first-answer;
- full verified-answer latency;
- p50/p95 work-unit duration;
- segments per minute;
- HIVE node utilization;
- retries and failures by node/backend;
- speedup `T1 / TN`;
- normalized parallel efficiency relative to each node's measured standalone throughput.

### Required ablations

Run B3 with one component removed at a time:

- no overlap;
- no boundary stitcher;
- FTS only;
- embeddings only;
- no graph expansion;
- no hierarchy;
- summaries trusted without source rehydration;
- no counter-evidence slot;
- no citation verifier;
- single combined reader versus extractor/linker/skeptic passes.

This determines which complexity earns its maintenance cost.

### HIVE scale experiment

Run the identical frozen ingestion campaign on one, two, and three enrolled machines.

Before the distributed run, benchmark each node alone to obtain its standalone segments/minute. The heterogeneous efficiency metric is:

```text
observed distributed throughput
---------------------------------
sum of standalone node throughputs
```

This is more honest than expecting three different machines to produce a 3x speedup.

During the three-node run:

1. Queue at least 128 read units.
2. Confirm each eligible node receives work.
3. Kill one worker during generation.
4. Confirm lease expiry and reissue.
5. Restart the old worker and submit its stale result.
6. Confirm the Warchief rejects it.
7. Resume to 100% segment coverage.
8. Compare all accepted evidence-card digests and metrics with the single-node run.

### Go/no-go gates

The first architecture gate passes only when all conditions hold:

- 100% of expected source segments reach an explicit terminal state.
- At least 99% produce schema-valid evidence without manual repair.
- At least 95% of planted local facts are recovered regardless of source position.
- At least 90% citation precision on answer claims.
- At least 95% correct abstention on controlled unanswerable questions.
- Multi-hop accuracy improves by at least 15 absolute percentage points over B2, or the result is documented as a failed hypothesis.
- Exhaustive enumeration reaches at least 95% recall and 95% precision.
- Final synthesis remains within the configured 8K test context.
- HIVE distributed throughput reaches at least 65% of summed standalone throughput.
- Worker-loss recovery, stale-result rejection, hash mismatch rejection, pause/resume, and cancellation all pass.
- B3 and B4 answer quality are statistically equivalent within the predefined tolerance; distribution may improve speed but must not silently alter semantics.

Targets may be revised only before held-out evaluation and with the change recorded in an ADR. Failed gates are reported, not tuned away after results are known.

### Reproducibility manifest

Every benchmark run records:

- git commit and dirty state;
- operating system and architecture;
- node IDs represented by privacy-safe aliases in published reports;
- CPU, RAM, GPU, backend, and driver/runtime telemetry;
- model and adapter hashes;
- parser, chunker, prompt, schema, planner, reducer, and verifier versions;
- corpus and question-manifest digests;
- random seeds and generation parameters;
- context and output budgets;
- campaign, work-unit, retry, and artifact IDs;
- raw machine-readable metrics;
- acceptance-gate verdict.

---

## Test Implementation

### Unit tests

Add deterministic tests for:

- structural segmentation, overlap, stable IDs, and page ranges;
- evidence-card schema bounds and citation-range validation;
- quote-digest verification;
- FTS trigger synchronization and query sanitization;
- graph deduplication, edge allow-lists, traversal bounds, and cycle handling;
- hierarchy construction and coverage digests;
- generation invalidation and stale-index exclusion;
- context-budget packing and response reserve;
- query classification and stop conditions;
- citation-verifier labels;
- campaign stage transitions and dependency release;
- native input path safety, resumable transfer, digest mismatch, and quota failure;
- idempotent evidence import and retry handling;
- benchmark metric calculations.

Use `SqliteStore(":memory:")` for repository tests and filesystem-isolated temporary content stores for artifact tests.

### Integration tests

Create a small deterministic corpus fixture and run:

- parse -> segment -> import -> FTS search;
- evidence import -> graph path -> source reopen;
- leaf cards -> reducer tree -> complete coverage root;
- incomplete child -> visible coverage gap;
- Quick query -> evidence pack -> cited answer contract;
- Exhaustive map -> recursive reduce -> citation verification;
- campaign artifact upload -> native input staging -> output import;
- application restart -> index and campaign recovery.

Inference-independent integration tests use scripted fake readers. Real-GGUF tests are opt-in and clearly labeled.

### Native model tests

The opt-in native lane runs the frozen synthetic corpus against the approved GGUF. It verifies:

- schema adherence;
- source prompt-injection resistance;
- citation accuracy;
- deterministic-seed stability where supported;
- native backend telemetry;
- no Ollama process or endpoint requirement;
- explicit failure on missing model, backend, or memory admission.

### HIVE end-to-end tests

The acceptance harness starts a Warchief and at least two workers with isolated identities and stores. It verifies:

- authenticated artifact movement;
- capability-aware leases;
- exact model-hash admission;
- stage barriers;
- heterogeneous scheduling;
- node failure and retry;
- different-node verification;
- stale completion rejection;
- final coverage and provenance;
- no direct SQLite access from workers.

### OrcChat headless tests

Add Avalonia headless coverage for:

- corpus attachment and removal;
- indexing state and progress;
- Quick/Study/Exhaustive selector;
- coverage and verification badges;
- citation click opening the correct source location;
- missing-source and stale-index warnings;
- cancellation and resumed conversation state;
- safe medical-source wording;
- artifact links and image/page previews.

### Test project organization

Suggested locations:

```text
OrchestratorIDE.UnitTests/
  ContextFabricSegmenterTests.cs
  ContextFabricRepositoryTests.cs
  ContextFabricSearchTests.cs
  ContextFabricReducerTests.cs
  ContextFabricQueryPlannerTests.cs
  ContextFabricCitationVerifierTests.cs
  ContextFabricBenchmarkMetricTests.cs
  HiveCampaignStageTests.cs
  HiveNativeInputStagingTests.cs

OrchestratorIDE.Avalonia.HeadlessTests/
  OrcChatLibraryTests.cs
  OrcChatContextFabricQueryTests.cs
  OrcChatCitationNavigationTests.cs

Tools/ContextFabricBench/
  deterministic corpus generator
  frozen question manifests
  runner and report generator
  gate evaluator
```

The benchmark runner emits JSON first and Markdown second. The report is generated from immutable result JSON so presentation cannot change gate outcomes.

---

## Implementation Path

### Phase CF-0: contracts, ADRs, and feasibility spike

Deliver:

- this design accepted or amended through an ADR;
- evidence-card and finding JSON schemas;
- corpus/index generation identifiers;
- context budget contract;
- deterministic 16-segment prototype corpus;
- a single-node map/reduce spike using `NativeRoleRuntime`;
- measured answer and citation output inside an 8K context.

Exit gate:

- all 16 segments produce valid cards;
- a cross-segment question is answered with valid citations;
- all artifacts can be deleted and deterministically rebuilt.

Implementation status (2026-06-27): **CF-0 exit gate passed; CF-1 unblocked**.

- The shared native-runtime project now contains versioned CF-0 contracts, a deterministic 16-segment corpus, strict host-side quote/digest/citation verification, hierarchical reducers, bounded evidence packing, frozen gates, and JSON/Markdown report generation.
- `Tools/ContextFabricBench` runs the spike directly through `NativeRoleRuntime`; it has no Ollama path or fallback. Workload-aware model selection and pinned role bindings ensure that the model checked by admission preflight is the model that actually executes the run.
- The deterministic scripted-runtime lane passes all gates in 26 calls and remains below the 8K context ceiling. The focused CF-0 suite covers deterministic rebuilds, exact quote anchoring, ambiguous and forged citations, canonical ID collisions, context-budget errors, and full map/reduce verification.
- The first real CUDA baseline processed the 15,595-token corpus but produced 0/16 valid evidence cards. Bounded raw-output and prompt-path diagnostics showed that this was a model/contract failure rather than an Ollama fallback.
- The first real run exposed a native batching defect for prompts larger than one LLamaSharp batch; `NativeRoleRuntime` now drains all pending inference batches before sampling.
- The passing native lane uses `Hermes-3-Llama-3.1-8B.Q5_K_M.gguf` through its embedded template on a single 16GB NVIDIA GPU. The final report accepted 16/16 segments, verified 5/5 questions, reached 100% citation precision, held the live context to 8K, and achieved an 11.50x source-to-working-context ratio. All nine frozen gates passed.
- A second real native lane now passes on `gemma-4-12b.gguf` through the runtime's verified `GemmaNativeFallback` prompt path after the embedded-template apply path failed. The final report accepted 16/16 segments, verified 5/5 questions, reached 100% citation precision, held the live context to 8K, and achieved an 11.48x source-to-working-context ratio.
- Reader inputs expose deterministic evidence units, incomplete cards receive one bounded missing-evidence repair pass, and the merged card is revalidated against the untouched source. Three cards required repair in the passing run. Exhaustive answers aggregate the highest-matching grounded claim per segment in source order; local, multi-hop, contradiction, and abstention lanes remain model-backed.
- Quote-anchor diagnostics cover exact, normalized-exact, soft-candidate, and rejected hallucinated anchors. The real native boundary-stitch lane passes 2/2 cases.
- CF-1 may now begin. Hierarchy-loss, embedding-impact, graph-noise, exhaustive-cost, and SQLite-traversal benchmarks remain acceptance work for later phases; they are not blockers to starting deterministic ingestion and content storage.

### Phase CF-1: deterministic ingestion and content storage

Deliver:

- `FabricLibraryService`;
- text/Markdown parser and normalized artifact format;
- PDF text parser behind a bounded interface;
- `FabricSegmenter`;
- content-addressed source and normalized-text storage;
- v8 corpus/document/segment/FTS migration;
- import, delete, rebuild, and corruption-recovery tests.

Exit gate:

- pinned Darwin edition imports reproducibly;
- segment IDs and normalized digest remain stable across two clean rebuilds;
- malformed and oversized documents fail safely.

Implementation status (2026-06-27): **framework in progress**.

- Migration v8 adds dedicated corpus, document, segment, normalized segment text, and external-content FTS5 storage beside CodeGraph in the shared WAL database; migration v9 retrofits segment range constraints for existing v8 databases and marks documents with invalid legacy segments for deterministic rebuild from their source artifacts.
- `FabricLibraryService` and `FabricLibraryRepository` provide corpus creation, bounded file import, deterministic rebuild, lexical segment search, and cascade deletion. Original and normalized artifacts reuse the existing quota-bounded SHA-256 object store.
- The first parser accepts strict UTF-8 plain text and Markdown, canonicalizes newlines and Unicode, preserves normalized character offsets, and records Markdown heading paths. PDF remains behind the parser boundary and fails explicitly as unsupported.
- `FabricSegmenter` prefers parsed block boundaries, splits oversized blocks safely, adds bounded overlap, wires neighbors, and derives stable IDs from document identity, chunker version, source range, and text digest.
- Focused CF-1 tests cover the v8-to-v9 upgrade, malformed UTF-8 and NUL rejection, deterministic bounded segmentation, stable import/rebuild IDs, immutable document identity, FTS search and cleanup, partial artifact recovery, oversized input, cascade deletion, and fail-closed missing-artifact rebuilds.
- Remaining CF-1 exit work is the pinned Darwin import/rebuild fixture, a real text-based PDF parser, artifact reference tracking and garbage collection, and product integration.

### Phase CF-2: DocumentGraph and local retrieval

Deliver:

- `DocumentGraphRepository`;
- evidence, claim, citation, entity, relation, and FTS migrations;
- evidence-card importer;
- `FabricSearchService` lexical baseline;
- graph traversal and hierarchy APIs;
- read-only `library_list`, `library_search`, `library_open`, and `library_graph` tools.

Exit gate:

- repository and FTS tests pass in memory and on disk;
- query results always include corpus/document/segment provenance;
- CodeGraph tests remain unchanged and green.

### Phase CF-3: native readers and source verification

Deliver:

- versioned reader prompts and schemas;
- native combined reader;
- boundary stitcher;
- quote-digest verifier;
- schema repair with bounded retry;
- prompt-injection-resistant source handling;
- native no-fallback smoke suite.

Exit gate:

- synthetic corpus reaches ingestion quality gates on one node;
- source instructions cannot invoke tools or change reader policy;
- every accepted claim has a valid source range.

### Phase CF-4: hierarchy and cognitive paging

Deliver:

- `FabricReducer`;
- memory node, membership, coverage, and generation migrations;
- recursive reduction with configurable fan-in;
- query planner and `EvidencePackBuilder`;
- cognitive page-fault source rehydration;
- citation verifier;
- Quick and Study query modes.

Exit gate:

- final prompt remains below the 8K benchmark limit;
- multi-hop questions trigger source rereads when summaries are insufficient;
- incomplete hierarchy coverage is visible and cannot be reported as complete.
- if hierarchy recall proves too lossy, flatten reducer depth and bias toward source reopen instead of preserving a deep summary tree for its own sake.

### Phase CF-5: OrcChat Library experience

Deliver:

- Library management UI;
- corpus attachment to chat;
- indexing progress and failure repair;
- source-bound chat prompt contract;
- citation rendering and navigation;
- coverage, mode, and verification indicators;
- conversation notebook that stores concise, cited conclusions rather than raw growing history.

Exit gate:

- a user can add Darwin, wait for indexing, start a fresh chat, and ask cited cross-chapter questions without manually managing context.

### Phase CF-6: HIVE stage engine and distributed readers

Deliver:

- campaign stages and dependency barriers;
- native input artifact staging;
- `theorc.context-fabric@1.0.0` pack;
- reader, stitcher, reducer, exhaustive-query, and verifier work-unit builders;
- generation-safe evidence import;
- per-node throughput and coverage telemetry;
- pause/resume/cancel and recovery UI.

Exit gate:

- two-node and three-node synthetic-book acceptance passes;
- worker death is recovered without duplicate accepted evidence;
- campaign paths never invoke Ollama.

### Phase CF-7: exhaustive mode and benchmark gate

Deliver:

- Exhaustive query planner;
- recursive map/reduce findings;
- benchmark corpus generator and frozen manifests;
- B0-B4 runners;
- ablation suite;
- JSON and Markdown reports;
- automated go/no-go evaluator.

Exit gate:

- all architecture gates in the Benchmark section pass, or the failed hypothesis and measured causes are published internally before further expansion.

### Phase CF-8: scale, multimodal documents, and hardening

Deliver only after CF-7:

- EPUB and DOCX structure-aware ingestion;
- OCR and page-image evidence;
- tables and figures as first-class evidence;
- optional vector-extension acceleration;
- cross-corpus and cross-CodeGraph links;
- incremental source updates;
- cache eviction and cold-storage policy;
- larger LongBench and million-token corpus runs.

Exit gate:

- no regression against frozen CF-7 quality, citation, context, and recovery metrics.

---

## Security, Privacy, And Safety

- Corpus files remain local unless the user enables HIVE distribution for that corpus.
- HIVE distribution is limited to enrolled trusted Warbands.
- Source and result transfers are authenticated, resumable, size-limited, and hash-verified.
- Workers receive the minimum source shard required for their work unit.
- Source text is untrusted data, never system instruction.
- Reader profiles expose read-only input and bounded output; arbitrary networking, package installation, host administration, and `ask_user` are unavailable.
- Complex parsers run with explicit resource limits.
- Artifact names never determine storage paths without sanitization.
- Deleting a corpus removes metadata and schedules unreferenced artifact collection; shared content-addressed objects are removed only when no corpus references them.
- Copyrighted user-supplied works are not included in releases, benchmark fixtures, telemetry, or public reports.
- Medical and legal corpora remain educational source tools, not professional impersonation features.

---

## Observability

Context Fabric adds first-class events and metrics:

- corpus imported, parsed, segmented, indexed, stale, rebuilt, deleted;
- stage queued, running, blocked, verifying, completed, failed;
- segments expected, accepted, failed, and missing;
- cards repaired or rejected by schema;
- claims accepted, contradicted, merged, or quarantined;
- query mode, coverage, rereads, evidence packed, evidence excluded;
- citation verification outcomes;
- per-node tokens/sec, segments/minute, model load state, retries, and failures;
- SQLite write queue depth and transaction latency;
- artifact-store bytes, deduplication savings, and quota pressure.

The HIVE control room shows coverage as a tree, not only a task count. A campaign with 499 successful segments and one missing segment is visibly incomplete.

---

## Known Risks

### Compression loss

Hierarchical summaries can omit details. Mitigation: source lineage, query-time rehydration, exhaustive mode, and ablation testing.

### Reader hallucination

Readers can invent claims or citations. Mitigation: strict schemas, offset and digest checks, independent verification, and source reopening.

### Cross-segment reasoning failure

Important relationships can span arbitrary distances. Mitigation: overlap, boundary stitching, entity graph links, global reducers, and exhaustive query fan-out.

### Graph pollution

Entity resolution can merge distinct concepts. Mitigation: retain aliases and evidence, use confidence thresholds, never erase minority interpretations, and permit rebuilds with improved resolvers.

### False completeness

A reducer may sound comprehensive despite missing inputs. Mitigation: coverage manifests computed by code, not by the model.

### SQLite write pressure

Many Warbands may finish together. Mitigation: artifact-first submission, bounded import channel, batched transactions, WAL, and no remote database connections.

### Cost explosion

Exhaustive rereading is intentionally expensive. Mitigation: visible estimates, query modes, hard budgets, reusable evidence cards, and cached subtree findings keyed by question hash and index generation.

Exhaustive mode is therefore a premium verification path, not the default user experience. Small corpora may still finish interactively, but larger corpora should be allowed to shift into explicit background-grade execution with honest latency messaging.

### Benchmark overfitting

Prompts may become specialized to planted facts. Mitigation: held-out seeds, hidden manifests, real-book questions, standardized datasets, and frozen pre-declared gates.

---

## Research Foundations

Context Fabric is an original integration for TheOrc, but its main mechanisms have strong precedents:

- [MemGPT: Towards LLMs as Operating Systems](https://arxiv.org/abs/2310.08560) - virtual context management and movement between memory tiers.
- [RAPTOR: Recursive Abstractive Processing for Tree-Organized Retrieval](https://arxiv.org/abs/2401.18059) - recursive clustering and summaries for retrieval at multiple abstraction levels.
- [From Local to Global: A Graph RAG Approach to Query-Focused Summarization](https://www.microsoft.com/en-us/research/publication/from-local-to-global-a-graph-rag-approach-to-query-focused-summarization/) - graph communities and map/reduce answers for global corpus questions.
- [Lost in the Middle](https://arxiv.org/abs/2307.03172) - evidence that nominal long-context capacity does not guarantee reliable use of information throughout the prompt.
- [Ring Attention with Blockwise Transformers for Near-Infinite Context](https://arxiv.org/abs/2310.01889) - a true distributed-attention direction, useful as a contrast to the consumer-throughput design chosen here.
- [LongBench](https://arxiv.org/abs/2308.14508) and [LongBench v2](https://arxiv.org/abs/2412.15204) - standardized long-context evaluation categories and deeper realistic reasoning tasks.
- [Project Gutenberg: On the Origin of Species](https://www.gutenberg.org/ebooks/1228) - public-domain real-book feasibility corpus.

These references support the architecture direction; they do not substitute for TheOrc's own controlled benchmark.

---

## Definition Of Done

Context Fabric is production-ready only when:

- OrcChat can ingest, index, attach, query, cite, and remove a corpus entirely through the native runtime path.
- CodeGraph and document graph operate concurrently in the same SQLite store without schema ambiguity or test regression.
- Original sources and every derivative generation have reproducible digests and lineage.
- Quick, Study, and Exhaustive modes expose honest coverage and budget behavior.
- HIVE can distribute readers and reducers with stage barriers, verified artifacts, recovery, and no Ollama fallback.
- The frozen feasibility benchmark passes its quality, citation, context, scale, and failure-recovery gates.
- The final answer can be produced inside the configured 8K acceptance context even when the indexed corpus is orders of magnitude larger.
- Documentation never describes this as literal billion-token dense attention.

At that point, TheOrc will not possess an infinite context window. It will possess something more practical for local hardware: a durable cognitive filesystem with exhaustive readers, hierarchical memory, graph navigation, source paging, and proof of what it actually examined.
