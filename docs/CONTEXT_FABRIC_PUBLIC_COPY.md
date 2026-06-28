# Context Fabric Public Copy

This page is the short public-facing layer for Context Fabric. It is meant for the README, a website landing section, release notes, or a launch post.

Use this instead of sending people straight into the full architecture spec when the goal is to explain what Context Fabric is and why it matters.

## Short Description

Context Fabric is TheOrc's source-grounded memory system for working across corpora that are larger than a model's live context window.

It does not pretend a local model remembered the whole shelf. It preserves the source, reopens the right evidence when needed, and keeps accepted claims tied back to what was actually read.

## README Excerpt

Context Fabric is how TheOrc approaches the "finite model, large corpus" problem.

It builds a deterministic local source library: import the document, preserve the artifact, segment it reproducibly, reopen source evidence on demand, and keep answers tied to citations you can inspect yourself.

The early benchmark shelf is called the **Independent Mind Corpus**. It starts with pinned public works like Darwin, the United States Constitution, and the Federalist Papers because those sources stress real capabilities: quote verification, hierarchy, cross-document retrieval, and source-grounded synthesis.

## Website Hero Copy

Local AI with a source-grounded memory.

Finite-context models should not bluff their way through a book, a manual, or a civic text. Context Fabric preserves the source, reopens evidence on demand, and shows you what the machine actually checked.

## Website Body Copy

Context Fabric is TheOrc's answer to the gap between local model limits and real-world source material.

The source lives outside the prompt. The model gets only the working set it needs for the current reasoning step, and the system can reopen the original text whenever the answer needs proof.

That means reproducible imports, durable artifacts, verified quotes, and a path toward corpus-scale reasoning without pretending dense attention over an unlimited shelf.

## Approved One-Liners

- A finite-context model. A source-grounded memory.
- Preserve the source before you ask the model.
- Do not ask the machine to guess. Make it reopen the evidence.
- Local AI for people who would rather own the machine than rent permission from one.
- Corpus-scale memory, not context-window theater.

## Guardrails

- Do not say `infinite context`.
- Do not say the model was trained on the benchmark works unless that is separately true and licensed.
- Do not imply professional legal or medical authority.
- Prefer `source-grounded`, `deterministic`, `reproducible`, `verified`, and `local-first`.
