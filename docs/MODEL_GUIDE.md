# TheOrc — Model Guide

> This guide explains how TheOrc reasons about models today: catalog scores, local evidence, and GOBLIN MIND data. For the richer UI surface, see [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md). For terminology, see [GLOSSARY.md](GLOSSARY.md).

---

## How TheOrc Judges A Model

TheOrc does not reduce model selection to one benchmark or one boolean "tool support" flag.

The current system combines:

- built-in `ModelProfiles`
- installed-model discovery
- GOBLIN MIND tool-call profiles
- local capability test results
- swarm run history
- built-in observations

That merged view is what powers the Model Wiki, Swarm Board badges, and routing recommendations.

---

## Built-In Score Axes

`ModelProfile` provides built-in role scores for:

- boss planning
- coding
- research
- testing

It also carries practical fields such as:

- minimum VRAM
- speed tier
- description
- native-tool-use expectation

These scores are useful, but they are not the final truth. Local evidence can override optimism.

---

## Tool Support Is Not Binary

A model can:

- accept a tool schema
- emit a valid-looking tool call
- still fail on long JSON payloads or multi-file writes

This is why TheOrc distinguishes short-call reliability from long-`write_file` suitability.

If you care about real file generation, the important question is not "Does it support tools?" It is "Can it survive the size and structure of this actual tool payload on this hardware?"

---

## Where GOBLIN MIND Fits

GOBLIN MIND adds runtime evidence in three especially important ways:

- preferred tool-call format
- category boundary map
- schema reduction requirements

That evidence changes prompt format, schema shape, and swarm routing. It is not a passive report.

---

## Long `write_file` Warnings

The model layer distinguishes between:

- general coding usefulness
- safe primary-coder use for long file payloads

If a model has a long-write warning, it may still be useful as:

- a researcher
- a tester
- a swarm-side helper
- a narrow single-agent assistant for small edits

It simply should not be trusted as the main generator for large structured writes.

---

## What The Model Wiki Adds

The Model Wiki / Lab is where the model picture becomes operational.

It shows:

- built-in score profile
- installed status
- observations
- local capability tests
- trends strip
- routing recommendation
- GOBLIN MIND probe data

Use it when you need evidence. Use this guide when you need the logic behind that evidence.

---

## Practical Selection Rules

### For single-agent work

Prefer models that:

- have strong coder scores
- do not carry long-write warnings
- show acceptable local capability-test behavior

### For boss role

Prefer models with:

- strong boss scores
- strong structured-output behavior
- usable task-planning category results

### For researcher role

Prefer models that are:

- good at reading and summarizing
- good on network and data-transform categories
- light enough to avoid starving the implementation phase

### For tester role

Prefer models that are:

- reliable at structured verdicts
- good at code execution and system inspection
- fast enough to keep verification cheap

---

## How To Decide Faster

If you are making a choice in the app:

1. open Model Wiki / Lab
2. inspect routing recommendation
3. inspect local observations
4. inspect trends
5. compare two candidates if the decision is close

That is the shortest evidence-based workflow currently available.
