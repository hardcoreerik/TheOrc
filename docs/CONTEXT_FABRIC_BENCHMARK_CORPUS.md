# TheOrc Context Fabric Benchmark Corpus

## The Independent Mind Corpus

The Independent Mind Corpus is the public benchmark shelf for TheOrc Context Fabric.

It is built for local-first, source-grounded AI work. The tone is independent builder energy: practical, skeptical of lock-in, evidence-first, and willing to read the source instead of renting authority from a summary.

Context Fabric is not magic large-context theater. It is a local-first, source-grounded memory fabric that lets finite-context models work across large corpora by reopening verified source evidence when needed.

> The system is bloated, rented, cloud-locked, and slow. So we built our own.
>
> The Independent Mind Corpus benchmarks Context Fabric against works that questioned authority, built new systems, tested reality directly, and changed how humans think. A finite-context model does not remember the whole shelf. The Fabric knows where to reopen the source.

Current repo truth:

- The Darwin text fixture, primary Darwin PDF fixture, United States Constitution fixture, and Federalist Papers fixture are pinned today in `OrchestratorIDE.UnitTests/TestData/ContextFabric/`.
- Each pinned fixture has a checked-in manifest plus import/rebuild coverage in `OrchestratorIDE.UnitTests/ContextFabricCf1Tests.cs`.
- Additional public benchmark works below still define the intended shelf and later phase targets.
- Listing a work here does not mean the repo is already shipping it as a checked-in fixture.

## Why These Works

This shelf is not decorative branding. Each work stresses a real Context Fabric capability:

- source citation
- hierarchy parsing
- cross-document retrieval
- graph extraction
- exhaustive search
- quote verification
- contradiction and caveat handling
- long-form synthesis
- source reopening instead of model-memory guessing

The shelf is intentionally mixed:

- public-domain books stress long-form reasoning, structure, and source reopening
- official public standards stress tables, IDs, sections, and formal language
- synthetic fixtures stress exact ground truth and hostile edge cases
- private or licensed corpora stress deployment reality without leaking protected content

## Public Benchmark Shelf

| Work | Theme | TheOrc interpretation | Context Fabric capability tested | Notes |
|---|---|---|---|---|
| Charles Darwin — *On the Origin of Species* | evolution through evidence | Question the default model. Test against reality. Evolve. | long scientific argument, terminology drift, cross-chapter evidence, examples and exceptions, global synthesis | Pinned today as the first public CF benchmark anchor; text and primary text-extractable PDF fixtures exist in repo. |
| Henry David Thoreau — *Civil Disobedience* | moral refusal and individual conscience | When the system is wrong, refusal can be rational. | dense argument extraction, short-form philosophical reasoning, claim and evidence mapping | Good compact reasoning corpus once CF reader and graph phases widen beyond Darwin. |
| Henry David Thoreau — *Walden* | self-reliance and deliberate living | Build your own cabin. Run your own stack. | recurring themes, metaphor tracking, reflective prose, long-form concept clustering | Strong fit for hierarchy and theme retrieval. |
| Thomas Paine — *Common Sense* | anti-monarchy, independence, plain-language argument | Stop renting permission from kings. | persuasive structure, direct argument, rhetorical claims, source-grounded civic reasoning | Useful early civic benchmark because the prose is direct and the sections are short. |
| Thomas Paine — *The Rights of Man* | rights, representation, inherited authority | Rights are not a premium subscription. | multi-part political argument, definitions, cross-reference retrieval, caveats and counterclaims | Good CF-2 or later graph and caveat benchmark. |
| Frederick Douglass — *Narrative of the Life of Frederick Douglass* | literacy, power, agency, liberation | Knowledge is leverage. Literacy is power. | autobiographical structure, historical source grounding, oppression and resistance themes, quote verification | High-value narrative benchmark with clear thematic recurrence. |
| Mary Shelley — *Frankenstein* | creation, responsibility, unintended consequences | Build the monster. Own the consequences. | nested narration, character and entity tracking, ethical argument, creator and creation relationship graphs | Excellent for CF-2 graph extraction and CF-4 hierarchy testing. |
| John Stuart Mill — *On Liberty* | freedom, experimentation, individual development | Progress needs people who are allowed to try weird things. | dense philosophical claims, definitions, exceptions, caveats, argument hierarchy | Strong later-phase reasoning and contradiction benchmark. |
| Sun Tzu — *The Art of War* | strategy, preparation, asymmetry | Win before the battle starts. | aphoristic structure, short dense passages, theme clustering, exact quote verification | Good short-form retrieval and exact quote benchmark. |
| Niccolò Machiavelli — *The Prince* | power systems, incentives, leadership | Understand the machine before you fight it. | political concept graphs, strategy extraction, morally complex claims, cross-chapter comparison | Useful for graph and morally ambiguous claim handling. |
| Marcus Aurelius — *Meditations* | self-command, discipline, internal operating system | Own your stack. Own your mind. | fragmented structure, aphorisms, theme clustering, non-linear retrieval | Excellent non-linear retrieval target. |
| *The Federalist Papers* | institutional design, union, faction, constitutional argument | Build the system before the crisis hits. | multi-document retrieval, authorship metadata, repeated concepts, exhaustive mode, argument graphing | Pinned today as a reproducible public text fixture; strong multi-document benchmark for CF-2 through CF-7. |
| United States Constitution — full text including Amendments I-XXVII | self-governance, limits on power, rights, amendment, institutional structure | The operating agreement. Read it yourself. Cite the clause. | article and section hierarchy, clause-level citation, amendment overlay behavior, exact quote verification, exhaustive civic retrieval | Pinned today as a reproducible public text fixture. Base text only. Constitution Annotated belongs in a separate commentary lane, not as the Constitution source itself. |
| *Moby-Dick* | obsession, systems, hierarchy, long-form symbolic structure | Long voyages expose weak maps. | long narrative hierarchy, chapter structure, repeated symbols, callback retrieval, global synthesis | Best held for CF-4 or later hierarchy and synthesis testing. |
| Complete Works of William Shakespeare | language, power, identity, conflict, performance | Track every voice in the room. | speaker and entity tracking, play/act/scene hierarchy, repeated names, quote attribution, corpus-scale retrieval | Good large public shelf benchmark once multi-document hierarchy is mature. |
| Plato — *The Republic* | justice, order, education, ideal systems | Design the city. Then question the designer. | dialogue structure, speaker tracking, nested definitions, argument chains | Strong dialogue and speaker-tracking benchmark. |
| NIST SP 800-53 Rev. 5 | security controls, formal requirements, institutional hardening | Trust is not a vibe. It is a control. | table extraction, control IDs, cross-references, formal language, compliance-style retrieval | Official public standard, not public domain. Best fit for later PDF, tables, and graph phases. |
| FDA public prescribing-label examples / FDALabel corpus | official medical labeling, warnings, contraindications, structured risk language | When the stakes are high, cite the label. | structured sections, warnings, contraindications, tables, exact source citation | Official public source. Educational and source-grounded only; not a clinical authority claim. |

## Private / Licensed Benchmark Shelf

Private and licensed corpora are valid benchmark targets, but they are never public fixtures and never branding props.

Examples:

- DSM-5 / DSM-5-TR
- commercial repair manuals
- legal treatises and paid standards
- internal company SOPs
- proprietary engineering manuals

Rules for private and licensed works:

- user-supplied only
- never committed to the repo
- never shipped as fixtures
- never included in public telemetry
- never included in public answer keys
- never redistributed through HIVE except to authorized enrolled nodes for that corpus
- public reports may describe aggregate metrics only when no protected text or derived answer key leaks

DSM rule:

- DSM-5 and DSM-5-TR are private licensed benchmarks only
- Context Fabric is not described as weight-trained from DSM content
- no DSM excerpts belong in repo benchmark docs
- no diagnosis or clinical authority is implied

## Phase Mapping

This phase map is a benchmark-program overlay on top of the technical phase plan in [The Orc Context Fabric.md](The%20Orc%20Context%20Fabric.md).

### CF-0

Phase goal:
prove that a real local model can emit evidence cards that survive strict host-side verification.

Recommended corpus:
synthetic ground truth plus Darwin.

What it tests:
exact source citation, bounded map/reduce, quote anchoring, and real-model proof beyond toy examples.

Acceptance note:
Current repo truth already matches this framing. CF-0 passed on synthetic ground truth and was then challenged on Darwin-style public-source reasoning.

Marketing line:
First proven on synthetic ground truth, then challenged with Darwin.

### CF-1

Phase goal:
preserve the source deterministically before asking the model to reason across it.

Recommended corpus:
Darwin, Constitution, Federalist Papers, Shakespeare.

What it tests:
deterministic ingestion, stable document and segment identity, reproducible rebuilds, public-domain and official-public fixture discipline, and parser boundaries across text and PDF.

Acceptance note:
Current repo truth: Darwin text, Darwin primary PDF, United States Constitution, and Federalist Papers fixtures are pinned and reproducible now. The rest of this shelf remains recommended expansion work, not shipped fixtures.

Marketing line:
Preserve the source before you ask the model.

### CF-2

Phase goal:
add graph-backed local retrieval on top of deterministic source storage.

Recommended corpus:
Darwin, Federalist Papers, Constitution, Plato, NIST.

What it tests:
graph extraction, cross-document links, argument chains, repeated concepts, section and control identifiers, and provenance-safe local retrieval.

Acceptance note:
Treat these works as the target shelf for graph and retrieval maturity. Repo truth should not claim them as implemented until pinned manifests and tests exist.

Marketing line:
Search text. Map arguments. Cite the source.

### CF-3

Phase goal:
make every reader claim survive a source check under native-runtime conditions.

Recommended corpus:
synthetic adversarial corpus, Darwin, Constitution, NIST, FDA labels.

What it tests:
quote verification, source-range integrity, hostile inputs, formal section language, and fail-closed evidence handling.

Acceptance note:
Current repo truth is stronger on framework than on public benchmark breadth: `ContextFabricCf3Tests` now prove the intrinsic no-fallback reader lane, hostile-source handling, valid source ranges, trusted quote digests, and reusable stitcher path against the synthetic adversarial corpus. Public-source CF-3 benchmark claims should wait for explicit real-model benchmark evidence on the recommended shelf.

Marketing line:
Every claim must survive a source check.

### CF-4

Phase goal:
teach the system to reopen the right part of a long source instead of pretending the whole book fits in memory.

Recommended corpus:
Moby-Dick, Shakespeare, Federalist Papers, Darwin.

What it tests:
hierarchy traversal, callback retrieval, long-form synthesis, and cognitive paging when summaries are insufficient.

Acceptance note:
This is the right place for book-scale hierarchy claims. Until then, use the language of targeted source reopening, not total-book memory.

Marketing line:
The model does not remember the whole book. The Fabric knows where to reopen it.

### CF-5

Phase goal:
put the source-grounded library flow in front of users.

Recommended corpus:
Darwin, Constitution, Federalist Papers, TheOrc docs, NIST.

What it tests:
attach-and-ask UX, citation navigation, indexing lifecycle, and source-grounded answers in the product surface.

Acceptance note:
A good public demo here is not flashy rhetoric. It is a clean question, a readable answer, and a source the user can reopen immediately.

Marketing line:
Attach the source. Ask the question. Get the citation.

### CF-6

Phase goal:
spread the reading work across a Warband without losing provenance or source control.

Recommended corpus:
synthetic benchmark, Darwin, Shakespeare, Federalist Papers.

What it tests:
distributed readers, recovery after worker loss, deterministic import, and generation-safe evidence merge.

Acceptance note:
Public language should emphasize coordinated reading and verified merge behavior, not vague swarm mystique.

Marketing line:
One Orc reads. A Warband studies.

### CF-7

Phase goal:
make exhaustive mode a measurable benchmark gate instead of a marketing promise.

Recommended corpus:
synthetic ground truth, Constitution, Federalist Papers, NIST, Shakespeare.

What it tests:
coverage reporting, exhaustive retrieval, repeated concepts, clause-level checks, and benchmark go or no-go evaluation.

Acceptance note:
The standard here is measured coverage with publishable metrics, not confidence theater.

Marketing line:
Context Fabric can report what it checked, not just what it guessed.

### CF-8

Phase goal:
expand from clean text into scanned books, tables, multimodal documents, and hardened real-world ingestion.

Recommended corpus:
scanned public-domain books, NIST PDFs, FDA labels, patents, and repair-manual-style private corpora.

What it tests:
OCR, table fidelity, scan resilience, multimodal evidence, and large mixed-source hardening.

Acceptance note:
This is the right home for the scan-heavy Darwin PDFs already pinned as future candidates. They should not be oversold as solved before OCR and scan handling exist.

Marketing line:
Books, manuals, standards, labels, and scans. One source-grounded memory fabric.

## Safe Marketing Language

Approved lines:

- TheOrc Context Fabric - benchmarked on Darwin, hardened on standards, verified by source.
- A finite-context model. A corpus-scale memory. Every claim tied back to source.
- Benchmarked on the books that questioned authority, built new systems, and changed how humans think.
- From *On the Origin of Species* to the United States Constitution, Context Fabric tests whether local AI can reason across works that challenged the old order.
- A source-grounded memory system for people who would rather own the machine than rent permission from one.
- Local AI for independent builders.
- Don't ask the machine to guess. Make it reopen the source.

## Claims We Do Not Make

Avoid these claims outside this warning section:

- trained on DSM-5
- trained on the Constitution
- infinite context
- perfect recall
- reads everything perfectly
- equivalent to billion-token attention
- clinician-grade
- medical-grade
- diagnoses mental disorders
- legal advice

Clarification:

Context Fabric benchmarks against source corpora. It does not train model weights on these works unless a separate training process explicitly does so and the licensing allows it.

## Benchmark Manifest Fields

Current repo truth uses fixture manifests that pin both source identity and deterministic rebuild outputs.
See [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md](CONTEXT_FABRIC_BENCHMARK_MANIFEST.md) for the field reference and sample JSON.

Required pinned-fixture fields today:

- `FixtureId`
- `SourceUrl`
- `DownloadedAtUtc`
- `Edition`
- `MediaType`
- `SourceSha256`
- `ParserId`
- `ParserVersion`
- `SegmenterVersion`
- `ExpectedDocumentId`
- `ExpectedNormalizedSha256`
- `ExpectedSegmentCount`
- `ExpectedSegmentIdsSha256`
- `ExpectedFirstSegmentId`
- `ExpectedLastSegmentId`

## Professional Rebel Guardrail

Good:

- independent
- self-reliant
- anti-lock-in
- source-grounded
- local-first
- builder-owned
- evidence-first
- verified citations
- user-owned compute

Bad:

- extremist
- illegal
- anti-law ranting
- chaos branding
- medical or legal authority claims
- copyrighted data misuse
- fake large-context hype

## Acceptance Checklist

- All required public works are listed.
- The United States Constitution entry explicitly requires the full text including Amendments I-XXVII.
- The private and licensed shelf is clearly separate from the public benchmark shelf.
- DSM-5 and DSM-5-TR are private and user-supplied only.
- No DSM excerpts appear.
- The document does not claim training on benchmark works.
- The document does not claim literal dense attention over an unlimited corpus.
- The benchmark-vs-training distinction is preserved.
- The tone stays professional, independent, practical, and source-grounded.
