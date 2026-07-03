# TheOrc — Toolcaller v0

> **Status: 🔲 First-proof specification.** This document defines a dataset and
> evaluation contract only. It does not authorize training or runtime changes.

---

## Objective

`theorc-toolcaller` is the first planned Foundry specialist. It converts a bounded
request and tool context into an exact tool proposal.

The v0 proof asks one question:

> Can a locally trained specialist beat deterministic and prompt-layer baselines
> on semantic tool selection and argument extraction without unacceptable cost?

V0 is not a planner, coder, or general chat assistant. It does not decide whether
the user's broader goal is wise, invent tools, execute tools, or grant approval.
The proof is allowed to conclude that training is unnecessary.

---

## Relationship To ORCISH TONGUE

ORCISH TONGUE is the planned name and direction for TheOrc's universal
tool-format/tool-calling layer. The current code still contains existing names
and prompt-layer format probing, instruction building, and defensive parsing.
The rename and Native Runtime decoder-constrained path have not fully landed.

The responsibilities are complementary:

| Layer | Responsibility |
|---|---|
| Toolcaller model | Select the intended tool and produce arguments from the supplied task context |
| ORCISH TONGUE direction | Render schemas/templates, adapt model dialects, constrain output when available, and parse results |
| Tool policy/approval layer | Decide whether the proposed call may execute |

Grammar-constrained decoding can guarantee syntax against a grammar; it cannot
guarantee that the selected tool or arguments are semantically correct or safe.
V0 therefore measures semantic correctness separately from parse validity.
The learned model never replaces `ToolPolicyEngine` or human approval.

---

## V0 Scope

The v0 corpus must use a frozen, explicitly enumerated subset of existing TheOrc
tools. The exact subset and schema version are an F-1 decision and must be recorded
before examples are generated.

Proposed starting subset, verified against current code:

- `read_file`
- `list_files`
- `grep_code`
- `write_file`
- `run_shell`
- `ask_user`

This is a handoff default, not a frozen inventory. F-1 may remove tools to keep the
proof smaller, but it must record the final catalog and schema hash before baseline
generation.

The dataset should cover:

- one valid tool call
- valid refusal or no-tool outcome
- malformed call repair
- missing required arguments
- extra or invented arguments
- wrong tool for the request
- calls that deterministic policy will later allow, require approval for, or block
- ambiguous requests that require clarification
- unsupported tool requests

Excluded from v0:

- multi-step planning
- executing the proposed tool
- unrestricted shell design
- generating new tool schemas
- broad natural-language answer quality
- automatic model promotion

---

## Canonical Example Shape

The training/export representation may reuse the existing canonical chat JSONL
where appropriate, but every logical example must contain or resolve to:

```json
{
  "example_id": "stable-id",
  "schema_version": "toolcaller-v0",
  "role": "CODER",
  "request": "Create the approved file.",
  "available_tools": ["write_file"],
  "approval_state": "approved",
  "expected": {
    "decision": "call",
    "tool": "write_file",
    "arguments": {
      "path": "example.txt",
      "content": "example"
    }
  },
  "provenance": {
    "source_type": "human-authored",
    "review_status": "accepted"
  }
}
```

This is a contract illustration, not a commitment to a new stored schema. F-1
must map the required fields to existing Training Pit schemas before adding a new
format.

Valid decisions are:

- `call`: emit exactly one allowed tool call
- `no_tool`: answer requires no tool in the frozen v0 universe
- `clarify`: required information is missing or ambiguous
- `unsupported`: the request cannot be represented by the frozen v0 tool universe

Policy outcome is recorded separately as evaluation context. It is not a target
decision for the learned model. The same proposed call must be passed through the
real deterministic policy layer to verify `allow`, `require_approval`, or `block`.

The target serialization used for inference must be frozen separately and must
match the parser/grammar being evaluated.

---

## Dataset Requirements

Each accepted example requires:

- stable identifier
- stable `lineage_group_id` shared by captures, paraphrases, repairs, and synthetic siblings
- source and transformation provenance
- frozen tool/schema version
- role and approval context
- expected decision
- exact expected tool/arguments when decision is `call`
- reason code for `clarify` or `unsupported`
- expected deterministic policy outcome when a call is proposed
- independent review status
- split assignment made before candidate training

Synthetic examples are allowed only when the teacher, prompt/recipe, source
inputs, mechanical checks, and reviewer are recorded. Synthetic examples are
candidate data, not automatic gold.

The split must prevent templated siblings, paraphrases, and derived repairs from
crossing train/eval boundaries. Exact string deduplication alone is insufficient.

---

## Dataset Admission Gates

In addition to [FOUNDRY_ARENA.md](FOUNDRY_ARENA.md), v0 hard-fails on:

- a target tool absent from the frozen tool universe
- invented or obsolete arguments
- `call` examples missing exact expected arguments
- `unsupported` or `clarify` examples without a reason label
- unresolved disagreement between expected target and policy
- examples that present a proposed call as already executed or approved
- policy-sensitive calls without the expected deterministic policy outcome
- train/eval groups sharing a template or source lineage

Mechanical validation should be completed before any model-based judge is used.

---

## Baselines

V0 must compare against:

1. deterministic schema validation/repair where it can solve the case
2. the current prompt-layer tool-format adaptation and defensive parser
3. the selected general base model with constrained output where supported

An optional general-model baseline may be added, but it does not replace the
current product/deterministic baselines.

The baseline snapshot must record:

- commit and tool/schema version
- model and inference backend where applicable
- prompt/template and parser version
- generation settings
- hardware
- per-case outputs and aggregate metrics

This phase is a kill gate. If the simplest baseline already meets the
predeclared semantic, latency, and safety targets, do not train a specialist.

---

## Evaluation Set

The held-out set should be balanced by decision type, tool, role, and failure
category rather than mirroring only common happy paths.

Required categories:

| Category | Required behavior |
|---|---|
| Exact valid call | Correct tool and semantically exact arguments |
| Syntax damage | Repair only when intent and arguments remain unambiguous |
| Missing information | Return `clarify`, not fabricated arguments |
| Policy-sensitive call | Propose the semantically correct call; deterministic policy produces the expected outcome |
| Unsupported tool | Return `unsupported` |
| No-tool request | Return `no_tool` |
| Adversarial instruction | Do not invent or blend tools; deterministic policy remains authoritative |
| Near-match tools | Select the correct tool without schema blending |

Evaluation examples and close derivatives must not be provided to a teacher used
for candidate-data generation.

---

## Metrics

Primary metric:

- **exact semantic proposal rate**: correct decision; and, for `call`, correct tool
  plus semantically exact arguments

Supporting metrics:

- parse-valid output rate
- correct tool selection rate
- exact argument rate
- clarify precision and recall
- unsupported precision and recall
- malformed-call repair success
- invented tool/argument rate
- deterministic policy outcome accuracy after the proposal is evaluated
- average and P95 latency
- peak memory and model load cost

Parse validity is not the primary metric. A perfectly formatted incorrect call
is a model failure. A semantically correct but disallowed proposal is successful
only if the real deterministic policy subsequently blocks or gates it as expected.

---

## Promotion Rule

The numerical promotion margin is frozen during F-1 after baseline variance is
measured; it must not be chosen after seeing candidate results.

At minimum, a promotable candidate must:

- improve exact semantic proposal rate beyond the predeclared margin
- preserve the deterministic policy layer's expected outcome on every hard-gate case
- not regress clarification/unsupported behavior beyond declared tolerance
- not invent tools or schema fields on the hard-gate set
- remain within the predeclared latency and memory budget
- reproduce its result across the declared run/seed policy
- satisfy every Foundry Arena provenance and rollback requirement

If it does not beat the baseline, retain the baseline and record the experiment.

---

## Native Runtime Trial

Training success does not authorize default runtime integration. A candidate that
passes offline evaluation may enter a separately approved opt-in trial.

The trial must record:

- base model and adapter/artifact identity
- runtime/backend version
- prompt/template, grammar, and schema versions
- load time, throughput, latency, and memory
- constrained and unconstrained results when both are supported
- explicit behavior when the grammar/backend is unavailable

The complete held-out evaluation must be rerun on the final exported or quantized
artifact. Training-format success does not transfer automatically to the deployed
artifact.

The runtime must not silently fall back to an unmeasured candidate. Existing
production behavior remains the rollback target.

---

## F-1 Deliverables

Before training begins, the next phase must produce:

1. frozen v0 tool/schema inventory
2. explicit model/ORCISH TONGUE/policy ownership boundary
3. mapping to existing Training Pit dataset formats
4. provenance and lineage-group split rules
5. mechanical validator contract
6. development and sealed-test manifests and hashes
7. deterministic, prompt-layer, and constrained-base baseline report
8. predeclared promotion margin, uncertainty method, and performance budget
9. minimal `run_manifest.json` contract
10. chat-template/tool-schema rendering round-trip fixture
11. explicit approval to begin one training experiment

No implementation or training work is authorized by this specification alone.
