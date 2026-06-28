# Native Model Admission Gate

> Last updated: 2026-06-27.
> Purpose: decide which local models Orc should auto-admit for which native workloads.

---

## Why this exists

TheOrc is moving toward being a local playground for all things AI, but "all models
should just work" cannot mean "every model is equally safe for every native job."

There are at least four very different demands hiding behind one model picker:

- open-ended chat
- tool use and agent loops
- strict structured output
- evidence-grade document work such as Context Fabric

A tiny chatty model may feel fine in OrcChat and still be a terrible choice for
strict JSON extraction or citation-bearing evidence reduction. The admission gate
exists to keep the product intuitive for the user while being honest and fail-closed
under the hood.

The operator should experience:

- "it works"
- "it works, but Orc labels it provisional"
- "Orc refuses this model for this workload and explains why"

Not:

- silent bad routing
- fake confidence
- running a 360M toy model as a document verifier

---

## The Orc power list

This is not a single universal ranking. It is the shortlist Orc should care about
by workload family.

### 1. General local default

- `Qwen3 8B`
  - best all-round starting point for local native chat, reasoning, and tool-oriented work
  - good candidate for "one model on one decent GPU" setups
- `Gemma 3 12B`
  - strong generalist with multimodal relevance and good local size/performance balance
- `Llama 3.3 70B Instruct`
  - heavyweight quality target when the user has exceptional hardware and wants broad general capability

### 2. Agentic coding and repo work

- `Devstral Small 2505`
  - top candidate for native coding-agent work
- `Qwen2.5 Coder 7B / 14B / 32B`
  - proven local-friendly coder ladder from midrange to flagship
- `Codestral`
  - still important when code specialization matters more than broad chat personality

### 3. Vision and mixed-document work

- `Qwen2.5-VL 7B`
  - strong vision/document candidate for screenshots, charts, and image-grounded reasoning
- `Gemma 3 12B`
  - important multimodal family for Orc's future image/document lane

### 4. Reasoning-heavy local work

- `Qwen3`
  - strongest practical open family to watch for local reasoning-first native work
- `DeepSeek R1 distills`
  - important reasoning references, especially for review and synthesis comparisons
- `Phi-4`
  - still useful where high reasoning density matters on smaller local footprints

### 5. Optional personalities, not strict defaults

- `Dolphin`
- `Hermes`
- other uncensored or personality-forward finetunes

These are valuable for creative, permissive, or roleplay-style chat, but should not
be Orc's default admission targets for strict structured-output or evidence-grade
native jobs.

---

## Workload classes

The first gate should classify native jobs into a small set of durable buckets:

- `OrcChat`
- `ToolCalling`
- `StrictStructuredOutput`
- `ContextFabricReader`
- `ContextFabricReviewer`
- `AgenticCoding`
- `VisionReasoning`

Those buckets are more stable than individual feature flags and let Orc explain
why a model was admitted or rejected in plain language.

---

## Admission states

### Admitted

Orc has enough prior evidence to auto-route this model to the workload class.

Sources of evidence may include:

- family heuristics
- parameter scale
- local tool-call probe history
- local capability tests
- Context Fabric benchmark results
- future OrcChat multimodal tests

### Provisional

The model is plausible, but Orc should warn the user and prefer a local test or
probe before promoting it.

This is the right state for:

- strong families at smaller sizes
- unknown GGUF variants
- newly downloaded models with no Orc-local test history yet

### Rejected

The model should not be auto-routed to the workload.

Typical reasons:

- too small
- wrong family for the task
- uncensored/personality finetune used for strict JSON or evidence work
- no multimodal signals for image reasoning

Rejected does not mean "never usable." It means "not a safe default for this
native workload."

---

## Orc policy by workload

### OrcChat

- very permissive
- almost any 1B+ instruct-style model can be allowed
- uncensored chat models can be admitted or provisional depending on size

### ToolCalling

- admit strong modern families such as Qwen, Qwen Coder, Qwen3, Gemma, Llama,
  Devstral, Codestral, Mistral Nemo, and Phi once the size is reasonable
- small models remain provisional even if they can do single-tool calls

### StrictStructuredOutput

- much stricter than chat
- require at least a real mid-size model
- reject small uncensored finetunes as defaults
- prefer families with a good history of JSON and schema obedience

### Context Fabric

- strongest gate in the first release
- reject toy models outright
- do not auto-admit uncensored chat finetunes
- admit only stronger generalist or reasoning families
- require benchmark evidence before promotion from provisional to admitted

### AgenticCoding

- prefer coding-native families
- allow strong generalists only as second-choice provisional options

### VisionReasoning

- require explicit multimodal family signals
- do not infer image capability from plain chat competence

---

## Current first implementation

The first implementation is intentionally conservative and heuristic-driven:

- model family inferred from GGUF filename
- parameter scale inferred from filename, with a few family defaults
- admission decision returned as `Admitted`, `Provisional`, or `Rejected`
- Context Fabric bench now performs a model-admission preflight before running

This is good enough to stop obvious foot-guns, especially:

- accidentally using tiny models for Context Fabric
- treating creative uncensored chat finetunes as strict evidence engines
- pretending any GGUF with "instruct" in the name is equally native-safe

---

## Consumer hardware target profiles

The admission gate must serve the product promise, not quietly redefine it.

For Context Fabric and other evidence-grade native jobs, Orc should benchmark and
explain models against named local target profiles rather than abstract quality
ideals alone.

Initial target profiles:

- `Windows/NVIDIA baseline`
  - single 12GB VRAM class GPU
- `Apple Silicon baseline`
  - 16GB unified-memory class machine
- `CPU-only diagnostic`
  - allowed for non-interactive or background-grade runs, not the default success path

This matters because a gate that only clears 70B-class hardware has solved the
technical benchmark while failing the product goal. If no model inside these
profiles can pass a workload such as Context Fabric, Orc should first revisit
the workload contract, benchmark method, or quality bar before silently pushing
the user toward unrealistic hardware assumptions.

---

## What this does not solve yet

- exact quality ranking between two strong admitted models
- automatic GGUF metadata inspection beyond filename heuristics
- local image-capability proof
- family/version drift in the curated catalog
- promotion from provisional to admitted based on persisted benchmark evidence

That is the next phase.

---

## Next implementation phases

### Phase A — heuristics and guard rails

- shipped scaffold:
  - workload classes
  - family fingerprinting
  - admission verdicts
  - Context Fabric bench preflight

### Phase B — local evidence integration

- fold in:
  - tool-call probe results
  - capability test results
  - native smoke history
  - Context Fabric benchmark outcomes
  - hardware-profile benchmark outcomes

At that point, admission stops being mostly "what family is this?" and becomes
"what has Orc actually seen this model do on this machine and within this hardware tier?"

### Phase C — operator UX

- surface admission state in Settings, OrcChat, and HIVE
- explain the reason in plain language
- let operators override with an explicit "Run provisionally anyway" path

### Phase D — curated flagship packs

TheOrc should eventually ship opinionated recommended native packs by hardware tier.

Example:

- `Starter Pack`
  - one fast generalist
  - one coding model
  - optional permissive chat model
- `Context Fabric Pack`
  - admitted reader model
  - admitted reviewer model
- `Vision Pack`
  - admitted multimodal model

That is how we make models "just work" without lying about capability.

---

## Practical Orc defaults

If Orc had to pick a future native-first shortlist today:

1. `Qwen3 8B` as the general local default
2. `Devstral Small 2505` as the coding-agent flagship
3. `Gemma 3 12B` as the multimodal and broad reasoning candidate
4. `Qwen2.5-VL 7B` as the first serious vision-native document model
5. `Llama 3.3 70B Instruct` as the heavyweight quality reference
6. `Dolphin` or `Hermes` as optional non-default personality models

These are the families Orc should be designed to understand, test, admit, and
explain well first.

---

## Source families reviewed

- Qwen model cards
- Gemma model cards
- Llama model cards
- Mistral / Devstral model cards

Use those primary sources when refreshing this file. Do not treat old Orc folklore
or old local installs as authoritative.
