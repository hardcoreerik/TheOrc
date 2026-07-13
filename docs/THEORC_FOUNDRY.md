# TheOrc — Foundry

> **Status: mixed — see [docs/CURRENT_STATE.yaml](CURRENT_STATE.yaml) (`foundry_toolcaller`,
> `foundry_other_tracks`) for the authoritative per-track state.** This document
> defines the umbrella strategy for the custom TheOrc model program. The first
> track — `theorc-toolcaller` — is **promoted and shipped** (v1.12.0, round r3):
> trained, adversarially evaluated, deployed via Ollama, wired into Swarm as an
> opt-in repair lane. Every other hypothesized track (dataset screening, Context
> Fabric repair, routing, review, swarm planning) remains strategy-only — this
> document does not claim those exist. Do not assume "Foundry" as a whole is
> either fully shipped or fully unimplemented; check the specific track.

---

## Mission

TheOrc Foundry is the program for designing, training, evaluating, deploying,
and improving AI models specialized for TheOrc on locally controlled consumer
hardware.

The goal is not a home-built frontier generalist. The long-term hypothesis is
that TheOrc-native specialists may beat current baselines on narrow, measurable
jobs such as tool-call validity, dataset screening, Context Fabric repair,
routing, review, and swarm planning. The first job of Foundry is to test that
hypothesis cheaply, not assume it is true.

The central hypothesis is:

> A smaller model trained for TheOrc's execution universe can outperform a
> larger general model on TheOrc-native work because protocol obedience, tool
> fluency, role safety, and measured workflow behavior matter more than broad
> world knowledge.

Foundry work is successful only when a candidate improves a real subsystem.
Training completion, lower loss, or a larger dataset is not success by itself.
An experiment that shows deterministic logic or an existing general model is
already better is also a useful Foundry result: retain the simpler baseline and
stop that model track.

---

## Product Boundaries

Foundry includes:

- definitions and contracts for the TheOrc model family
- locally generated, captured, reviewed, and versioned training data
- LoRA/QLoRA adaptation and distillation recipes
- experimental from-scratch training for narrowly scoped small models
- hardware-specific training recipes backed by measurements
- baseline comparison, promotion, quarantine, and rollback policy
- Native Runtime requirements for specialist execution
- eventual distribution of capture, evaluation, and training jobs through HIVE
  and Warbands

Foundry is not:

- a generic "upload text and make an AI" product
- a promise to pretrain a frontier-class general model at home
- evidence that every internal decision needs an LLM
- permission for a model to generate, judge, and promote its own data without
  independent gates
- a replacement for broad general-purpose coding and reasoning models

The smallest safe solution wins. A rule, schema validator, or classical model
remains preferable when it beats an LLM on cost, reliability, and maintainability.

---

## Relationship To Existing Systems

Foundry is the umbrella strategy; existing subsystems retain distinct ownership.

For current behavior and file formats, the checked-in Training Pit implementation
and its implementation docs remain authoritative until an explicit Foundry
migration changes them. This strategy does not silently redefine that subsystem.

| System | Responsibility | Current relationship to Foundry |
|---|---|---|
| Training Pit | Capture, review, dataset schemas, suitability checks, and export | Existing foundation; expanded datasets are planned |
| ORC ACADEMY | Local adapter training, checkpoints, resume, and training telemetry | Existing execution surface for initial LoRA/QLoRA work |
| Native Runtime | Local model execution, persistent role contexts, scheduling, and telemetry | Opt-in proof paths exist; Foundry-specific routing and constrained decoding remain planned |
| ORCISH TONGUE | Directional name for the universal tool-format/tool-calling layer | Prompt-layer adaptation exists under current code names; rename and decoder-constrained direction are not fully landed |
| Context Fabric | Source-addressable document memory and retrieval | Potential producer and consumer of future Foundry datasets/models |
| HIVE / Warbands | Distributed execution on other nodes | Capture/evaluation first; remote training is later work |
| Reviewer Quality Gate | Evidence-based trust and review authority | Supplies the trust pattern for Foundry promotion |

The production inference path remains Ollama-first. Native Runtime has real
opt-in components, including `ModelDepot`, `SessionManager`, `AdapterManager`,
`RuntimeOrchestrator`, and early scheduler admission logic, but it is not yet the
default runtime. Foundry planning must preserve that distinction.

---

## Core Architecture

The long-term model architecture is layered:

```text
small specialists  -> frequent constrained decisions
medium specialists -> structured orchestration and review
general models     -> broad reasoning and code generation
```

The runtime question is:

> What is the smallest model that can safely make this decision?

Not every specialist is a chat model. A specialist may be a classifier, scorer,
validator, repair model, constrained generator, LoRA adapter, or full model.

An expected cascade is:

```text
user goal
  -> router selects lane, model, and node
  -> Fabric specialist inspects source context when needed
  -> boss decomposes work when planning is needed
  -> general coder performs broad implementation
  -> reviewer assesses the resulting diff
  -> dataset judge decides whether the run is suitable training material
```

This is a target architecture, not current runtime behavior.

---

## Model Family

Size ranges below are hypotheses to test, not supported hardware claims.

> **Candidate tracks, not commitments:** Listing a model here does not place it on
> the delivery roadmap. Every track must first identify a measurable gap and
> survive its own baseline comparison before training work begins.

| Model | Planned job | Initial size hypothesis | Primary evidence |
|---|---|---:|---|
| `theorc-toolcaller` | Propose semantically correct TheOrc tool calls; deterministic policy remains authoritative | 500M-3B | Tool/argument accuracy, clarification quality, latency |
| `theorc-dataset-judge` | Screen captures for contamination, leakage, and suitability | 300M-3B | Recall/precision against reviewed data |
| `theorc-fabric` | Diagnose and repair Context Fabric ingestion/retrieval failures | 300M-3B | Frozen corpus and failure-injection results |
| `theorc-router` | Select lane, model, node, or deterministic path | 50M-300M, or non-LLM | Routing accuracy, safety, latency |
| `theorc-reviewer` | Review diffs and emit evidence-backed findings | 3B-14B | Finding recall/precision and false-CLEAN rate |
| `theorc-boss` | Plan, decompose, assign roles, and name expected files | 3B-12B | Existing structured planning rubric |

### First Proof: `theorc-toolcaller`

The first planned proof is `theorc-toolcaller` because its output space is
bounded, its failures are measurable, and it directly connects Foundry to the
Native Runtime and ORCISH TONGUE direction. The proof begins with existing
deterministic and prompt-layer baselines. Training proceeds only if those
baselines leave a measured gap that a small specialist could plausibly close.
Its contract is defined in
[THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md).

### Follow-On Order

The default order after the first proof is:

1. `theorc-dataset-judge` to protect the data flywheel
2. `theorc-fabric` as the most differentiated specialist
3. `theorc-router` after deterministic routing establishes a baseline
4. `theorc-reviewer` after a stable gold finding set exists
5. a smaller or next-generation `theorc-boss`

This order may change only when baseline evidence or an active product need
justifies it.

---

## Model-Making Paths

### Practical: LoRA / QLoRA

Use an open base model and train an adapter on reviewed TheOrc examples. This is
the default near-term path because it is already aligned with ORC ACADEMY and
consumer GPU constraints.

### Distillation

Use one or more stronger teachers to produce candidate examples, then apply
mechanical gates and independent human/model review before training a smaller
student. Teacher output is proposed data, never automatic ground truth.

### Research: From Scratch

From-scratch work starts with narrow models and scaling experiments, for example
30M, 100M, 300M, 500M, then 1B parameters. Suitable research targets include a
router, retrieval scorer, tool-call validator, or contamination classifier.

A larger from-scratch model is not scheduled until smaller experiments beat
rules, prompting, and adapted open models on a frozen task.

---

## Local Self-Improvement Loop

The planned loop is:

```text
local work or generated task
  -> capture with provenance
  -> mechanical validation
  -> suitability and contamination review
  -> frozen train/eval split
  -> candidate training
  -> Foundry Arena comparison
  -> human-approved promotion or rejection
  -> monitored production use with rollback
```

TheOrc may eventually propose goals, generate candidate examples, and train
candidate specialists locally. It must not silently promote a model that judged
its own training data or evaluation. Independent held-out evaluation and explicit
human approval are required until a later trust policy is separately specified
and accepted.

Each accepted example must retain enough provenance to answer:

- what produced it
- which model, prompt, tools, and project state were involved
- which transformations or synthetic teachers touched it
- which checks and reviewers accepted it
- which train/eval split and model version consumed it

Detailed promotion policy lives in [FOUNDRY_ARENA.md](FOUNDRY_ARENA.md).

---

## Native Runtime Requirements

Foundry requires the Native Runtime direction to mature in measured increments:

1. Load and identify base models and adapters deterministically.
2. Record model, adapter, prompt/schema, and runtime versions with every result.
3. Enforce structured output where the backend supports constrained decoding.
4. Measure load time, time to first token, throughput, memory, and failure mode.
5. Schedule around active user workloads and explicit VRAM budgets.
6. Keep small specialists resident only when measurements justify the cost.
7. Fail explicitly when a required capability is unavailable; do not silently
   substitute an unmeasured model.
8. Preserve current Ollama-first production behavior until native candidates pass
   their own admission gates.

ORCISH TONGUE is the planned name/direction for universal tool-call adaptation.
Today, prompt-layer probing and defensive parsing exist under current code names.
The rename and Native Runtime grammar-constrained path remain planned and must not
be described as shipped.

---

## Hardware Hypotheses

Initial measurement should use hardware already available to the project rather
than generalized internet tables:

- RTX 5070 Ti 16 GB
- RTX 3080 10 GB
- RTX 3050 6 GB
- CPU-only baseline

Future coverage may include a 24 GB GPU, Apple Silicon/MLX, and AMD or Intel GPU
paths. Until tested, these are compatibility targets, not promises.

[HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) remains the operator-facing hardware truth.
This section only identifies Foundry experiments and must not duplicate or
override measured guidance there.

| Tier | Initial experiment hypothesis |
|---|---|
| CPU only | Dataset validation, evaluation preparation, tiny classifiers |
| 6 GB VRAM | Tiny router/judge experiments and small adapter feasibility |
| 10 GB VRAM | 1B-3B specialists and constrained 7B adapter experiments |
| 16 GB VRAM | 7B-12B adapter work and larger specialist comparisons |
| 24 GB+ VRAM | Larger reviewer, boss, or distillation experiments |

Every published Forge Recipe must include the exact base model, precision,
sequence length, batch/accumulation settings, peak memory, elapsed time, power or
idle policy when available, output artifact, and evaluation result.

"Slow Train Mode" is a planned product capability: idle-only scheduling, VRAM
caps, pause/resume, checkpoint recovery, and exclusion while an active swarm run
needs the same GPU. Existing ORC ACADEMY capabilities may support parts of this,
but the complete policy is not yet implemented.

---

## Proof-Of-Concept Path

| Phase | Scope | Exit or stop evidence |
|---|---|---|
| F-0 | Canonical strategy, Arena policy, and first-model contract | Documents accepted and linked from roadmap |
| F-1 | Freeze the real tool subset, lineage rules, held-out set, and production-shaped baselines | Baseline report identifies a specific gap; otherwise stop the toolcaller training track |
| F-2 | Train one conservative local candidate with one immutable run manifest | Reproducible adapter artifact and complete record |
| F-3 | Compare the candidate with the baseline, then test the final deployment artifact | Candidate wins the predeclared comparison and the deployed form preserves the win; otherwise retain baseline |
| F-4 | Limited opt-in trial with rollback | Real-use evidence supports or rejects production promotion |

Only F-0 is authorized by this documentation pass.

Dataset automation, additional specialists, self-improvement, HIVE distribution,
and from-scratch research remain future directions. They do not enter the proof
of concept until F-4 produces a defensible win.

---

## Decisions And Open Questions

Accepted strategy defaults:

- TheOrc Foundry is the umbrella name.
- `theorc-toolcaller` is the first proof model.
- LoRA/QLoRA is the first training path; distillation follows.
- From-scratch models remain a measured research track.
- Dataset gates and Foundry Arena comparison are mandatory.
- Warbands begin with capture and evaluation before remote training.
- Model sizes and hardware tiers remain hypotheses until measured.
- Deterministic policy and human approval remain outside the learned model.
- A no-training or no-promotion result is acceptable when the baseline wins.

Open questions to resolve during F-1:

1. Which exact open base models and licenses qualify for the first comparison?
2. What frozen tool/schema version defines the v0 task universe?
3. Which inference backend supplies the canonical latency baseline?
4. What minimum sample count and confidence interval are required for promotion?
5. Which artifacts may be shared publicly, and which remain project-local?
6. What retention policy applies to captures containing repository content?

These questions are gates, not invitations to build supporting platforms. F-1
should answer them in the smallest reproducible baseline package possible.
