# TheOrc — Foundry Arena

> **Status: this policy is in force, has judged a real promotion, and is now
> partly mechanically enforced.** The Training Pit's Stage 4 **ARENA** panel
> and **Refusal Gauntlet** panel are live UI that run real evaluation against a
> sealed 260-example set and a 4,788-case adversarial suite (see
> [TOOLCALLER_REFUSAL_GAUNTLET.md](TOOLCALLER_REFUSAL_GAUNTLET.md)). `theorc-toolcaller`
> round r3 was promoted under this policy in v1.12.0, via human review — before
> the command below existed.
>
> **`training_pit/foundry/scripts/foundry_promote.py`** now mechanically checks
> 14 of the criteria this document describes (dataset/eval/artifact hashes,
> frozen group split, confidence lower bound, Arena regression ceiling, JSON
> validity floor, per-family safety floor, tool-schema identity, rollback
> availability, deployed-artifact evidence, explicit human approval) and
> refuses to write a promotion record on any unmet one — it is the actual
> registry-of-record (`training_pit/foundry/PROMOTION_REGISTRY.json`), not a
> Markdown checklist. Two criteria remain genuinely open, not silently
> skipped: **latency/memory budget** has no measurement wired in yet (the gate
> blocks on this unless explicitly overridden with a logged flag), and the gate
> has not yet judged a real candidate-vs-different-incumbent promotion — its
> first live run correctly *refused* to re-promote r3 against itself, which is
> the behavior a working gate should have. See
> [docs/CURRENT_STATE.yaml](CURRENT_STATE.yaml) (`foundry_toolcaller.promotion_process`).

---

## Purpose

Foundry Arena is the evidence and lifecycle policy for custom TheOrc models.
It prevents training loss, subjective impressions, or self-scored synthetic data
from being mistaken for product improvement.

"Arena" today means this policy plus the Training Pit's live evaluation UI and
manually reviewed evidence — not yet a single mechanical promotion gate. See the
status note above for the exact boundary.

The governing rule is:

> No candidate is promoted unless it beats the current baseline on its declared
> target while passing every safety and regression gate.

The baseline may be deterministic code, a prompt-layer workflow, an existing
adapter, or another model. A trained model is not automatically the baseline.

For a proof of concept, Arena must also answer the null hypothesis: does training
improve the system enough to justify owning a specialist at all? If the answer is
no, retaining the simpler baseline is the correct outcome.

### Manual F-1 Artifact Checklist

Before any candidate training is approved, the experiment folder must contain:

- `dataset_health.md`
- `baseline_report.md`
- `run_manifest.json` contract or completed manifest, as appropriate
- `eval_report.md` template with frozen metrics and thresholds
- `promotion_decision.md` template identifying the human decision owner

These are small evidence artifacts, not new services. Current Training Pit
formats and gates remain authoritative until an explicit migration changes them.

---

## Separation Of Responsibilities

No single actor should control the complete promotion chain.

| Responsibility | Requirement |
|---|---|
| Generate or capture data | Record provenance and the producing model/runtime |
| Accept training examples | Apply mechanical gates plus independent review |
| Freeze evaluation data | Keep it out of training and candidate generation context |
| Train candidate | Record inputs, recipe, environment, and output hashes |
| Score candidate | Use the frozen rubric and reproducible evaluator |
| Approve promotion | Explicit human decision during early Foundry phases |

A model may help generate examples or judge candidates, but it cannot be the
sole authority for data it produced or a candidate derived from that data.

---

## Dataset Admission Gate

Every training run starts with a versioned dataset health report. The report
must include:

- dataset identifier and content hash
- lineage-group identifier for every example family
- schema/tool version
- total, accepted, rejected, train, and evaluation counts
- human-reviewed and synthetic proportions
- producing model and prompt/recipe provenance
- duplicate and near-duplicate analysis
- train/eval leakage analysis
- role and permission violations
- fabricated or unverifiable file references where applicable
- malformed structured outputs
- missing provenance or labels
- applicable domain-specific checks

Splits are assigned by lineage group, not by individual row. Captures,
paraphrases, repaired variants, and synthetic siblings derived from the same
source must stay in one split. Exact assistant-response hashing is useful but is
not sufficient leakage protection.

Hard failures include:

- known train/eval overlap
- missing source or transformation provenance
- examples that violate the target role's permissions
- invalid target outputs that would teach unsupported behavior
- evaluation examples exposed to candidate training or teacher generation
- unreviewed synthetic data admitted as ground truth

A failed dataset may be repaired and assigned a new version. Its previous report
remains immutable.

The future Dataset Doctor may automate these checks. Until implemented, the same
contract applies to manual reports and existing Training Pit scripts.

---

## Evaluation Contract

Before training, each model specification must freeze:

1. target task and non-goals
2. input and output schemas
3. current baseline
4. held-out evaluation set and hash
5. primary metric
6. safety and regression gates
7. performance budget
8. promotion threshold
9. rollback trigger

For the proof of concept, use one development set for iteration and one sealed
test set for the final decision. The sealed set and close derivatives must not be
shown to candidate-data teachers or used to select hyperparameters.

The evaluation set must contain expected success cases, malformed inputs,
ambiguous requests, unsupported operations, and adversarial or permission-boundary
cases relevant to the role.

Where stochastic inference is allowed, the report must state the seed policy,
sampling settings, run count, and variance. A single favorable run is not enough
to establish a win.

Per-case outcomes must be retained so candidate and baseline can be compared as
paired results. The promotion report must include a confidence interval or other
predeclared uncertainty bound appropriate to the metric. "Zero failures" must
always state the number of tested cases.

---

## Baseline Comparison

Every report compares like with like:

- same task set
- same tool/schema version
- same acceptance parser
- same hardware when performance is part of the claim
- documented inference settings
- equivalent opportunity to decline unsupported tasks

Foundry may compare more than one baseline, but one must be identified as the
current production or deterministic baseline that the candidate must beat.

Example report shape:

```text
Candidate: theorc-toolcaller-v0-candidate-01
Baseline: current prompt-layer tool adaptation
Dataset hash: <sha256>
Eval hash: <sha256>
Runtime: <backend and version>

Exact semantic proposals: 97.2% -> 99.1%
Correct clarification:     93.0% -> 98.0%
Policy hard-gate passes:   100% -> 100%
P95 latency:               110 ms -> 145 ms

Recommendation: PROMOTE or DO NOT PROMOTE
Reason: <primary evidence and any tradeoff>
```

Numbers above illustrate the report format only; they are not project results.

---

## Promotion Policy

A candidate may be recommended for promotion only when all conditions hold:

- dataset admission passed
- evaluation is reproducible from recorded artifacts
- primary metric clears the model specification's threshold
- no hard safety gate regresses
- no protected secondary metric regresses beyond its allowed tolerance
- runtime cost fits the declared performance budget
- artifact, recipe, and report hashes are recorded
- rollback target remains available
- a human approves the promotion

Offline promotion applies first to the training artifact. Production promotion
requires the same held-out evaluation to pass on the exact exported or quantized
artifact through the intended runtime backend. A PEFT adapter result alone does
not prove that its deployed GGUF/adapter form is equivalent.

The first successful candidate does not receive a weaker bar. If no candidate
beats the baseline, Foundry records a failed experiment and retains the baseline.

---

## Lifecycle States

| State | Meaning |
|---|---|
| Experimental | Artifact exists; no trust claim |
| Candidate | Training record is complete; Arena evaluation pending or active |
| Promoted | Arena gates passed for a declared role and scope |
| Production | Selected default for that role and scope |
| Retired | Superseded or no longer supported; retained for provenance as policy permits |
| Quarantined | Unsafe behavior, corrupted provenance, leakage, or invalid evaluation discovered |

Promotion and production are separate. A promoted candidate may enter a limited
trial before becoming the default.

---

## Quarantine

Quarantine is mandatory when any of the following is discovered:

- train/eval leakage invalidates the result
- source or artifact hashes do not match the record
- unsafe behavior violates a hard gate
- dataset provenance is materially incomplete or false
- runtime/schema drift makes prior acceptance evidence unreliable
- a production regression exceeds its rollback trigger

Quarantine actions:

1. stop new assignment of the artifact
2. preserve the artifact and evidence unless security policy requires isolation
3. restore the last known-good baseline
4. record affected datasets, models, runs, and downstream artifacts
5. require a new candidate/version for remediation; do not rewrite history

---

## Rollback

Every production promotion requires:

- prior production artifact or deterministic baseline
- versioned registry entry
- configuration needed to restore it
- compatible runtime/schema version or documented migration
- explicit rollback trigger

Rollback does not erase the failed production record. The registry must show when
the candidate was promoted, why it was rolled back, and which evidence triggered
the decision.

---

## Drift And Re-Evaluation

A previous win does not automatically survive changes to:

- tool or output schemas
- role permissions
- prompt/template rendering
- base model or adapter format
- inference backend or quantization
- Context Fabric or dataset contracts
- evaluation parser or rubric

Material changes require either a compatibility proof or a new Arena run. Reports
must identify the exact versions they cover.

---

## Minimum Registry Record

Each candidate record should eventually contain:

- stable model/candidate identifier
- lifecycle state
- declared role and scope
- base model and license
- adapter/full-model artifact hash
- dataset and evaluation hashes
- training recipe and environment summary
- runtime and schema versions
- Arena report location
- promotion/retirement/quarantine decision and approver
- rollback target

For the first proof, do not introduce a new registry or experiment-tracking
service. A single immutable `run_manifest.json` beside the candidate is enough.
It must record:

- full base-model identifier, immutable revision, and license
- tokenizer and chat-template identity or hashes
- train, development, and sealed-test hashes
- lineage/split manifest hash
- LoRA and quantization settings
- seed and determinism policy
- trainer/runtime/dependency versions and hardware summary
- source commit and whether the worktree was dirty
- adapter and deployment-artifact hashes
- evaluation harness, tool-catalog, grammar, and schema hashes

The existing adapter registry remains production truth for current adapters.
Foundry registry changes are future implementation work and must not fork that
truth silently.

---

## Model-Specific Addenda

Each Foundry model supplies its own metrics and hard gates. The first contract is
[THEORC_TOOLCALLER_V0.md](THEORC_TOOLCALLER_V0.md). Future model specifications
may add stricter rules but may not bypass dataset admission, independent held-out
evaluation, provenance, or rollback.
