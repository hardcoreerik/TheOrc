# The Training Pit — Toolcaller Capture Schema

> **Schema version:** toolcaller-v0
> **Status:** Defined. Not yet auto-populated — no capture hook exists for this format.
> Neither `DATASET_SCHEMA.md` (chat-JSONL SFT format) nor `PLAN_CAPTURE_SCHEMA.md`
> (boss plan-decomposition format) can hold a tool-call example: neither has
> `available_tools`, a call/no_tool/clarify/unsupported decision enum, or a
> `tool` + `arguments` output shape. This is a new sibling format, not a
> replacement for either existing schema.
>
> This schema exists to satisfy F-1 deliverable #3 ("mapping to existing Training
> Pit dataset formats") from
> [THEORC_TOOLCALLER_V0.md](../docs/THEORC_TOOLCALLER_V0.md). Defining the schema
> does not authorize dataset generation or training — F-1's other deliverables
> (baseline report, frozen manifests, promotion margin) remain open work.

---

## What Toolcaller Captures Are For

A toolcaller capture records a single bounded tool-proposal decision:
`role + available tools + request → expected decision (+ tool/arguments) → policy outcome`

Unlike a plan capture (which records an open-ended multi-task decomposition), a
toolcaller capture's target output is small and enumerable: one of `call`, `no_tool`,
`clarify`, or `unsupported`, plus an exact tool/argument pair when the decision is `call`.
This is what makes `theorc-toolcaller` a bounded v0 proof rather than a general
planning or coding task.

The frozen tool universe, per-role tool subsets, and schema hash this format depends on
are defined in
[docs/TOOLCALLER_V0_FROZEN_INVENTORY.md](../docs/TOOLCALLER_V0_FROZEN_INVENTORY.md).
Every capture must reference that hash so a later change to a tool's registered
schema is detectable against examples generated under the old one.

---

## Schema

```jsonc
{
  // ── Identity ─────────────────────────────────────────────────────────────
  "schema_version": "toolcaller-v0",
  "tool_schema_hash": "c456ca416882788664b14ea332aa968de76735171a2e53a76eac7c4c6e2bfefd",
  "example_id": "tc_20260703_001",       // tc_YYYYMMDD_NNN
  "lineage_group_id": "tc_lg_00042",     // shared by every paraphrase/repair/synthetic
                                          // sibling derived from the same source case;
                                          // train/eval split must not divide a group
  "captured_at": "2026-07-03T19:04:01Z",

  // ── Source and transformation provenance ────────────────────────────────
  "provenance": {
    "source_type": "human-authored",     // "human-authored" | "swarm_capture" |
                                          // "corrected_model_output" | "synthetic" |
                                          // "repair" | "paraphrase"
    "producing_model": null,             // model id if source_type implies model output
    "teacher_model": null,               // teacher id if synthetic (proposed data, not gold)
    "prompt_or_recipe_id": null,         // authoring prompt/recipe version, if applicable
    "derived_from_example_id": null      // example_id this was paraphrased/repaired from,
                                          // if any (must share lineage_group_id)
  },

  // ── Input context ────────────────────────────────────────────────────────
  "role": "coder",                       // "researcher" | "coder" | "ui_developer" |
                                          // "tester" (SwarmWorkerRole, lowercase)
  "request": "Create the approved config file with the given contents.",
  "available_tools": ["write_file", "read_file", "run_shell", "list_files", "grep_code"],
                                          // must equal the frozen per-role subset from
                                          // TOOLCALLER_V0_FROZEN_INVENTORY.md, not an
                                          // arbitrary list
  "approval_state": "approved",          // "approved" | "pending" | "denied" | "n/a" —
                                          // upstream approval context the request carries
                                          // in, NOT the model's own decision

  // ── Expected output ──────────────────────────────────────────────────────
  "expected": {
    "decision": "call",                  // "call" | "no_tool" | "clarify" | "unsupported"
    "tool": "write_file",                // required when decision == "call"; must be a
                                          // member of available_tools
    "arguments": {                       // required when decision == "call"; must match
      "path": "config/example.json",     // the tool's frozen parameter schema exactly —
      "content": "{\"key\": \"value\"}"  // no invented or obsolete fields
    },
    "reason_code": null                  // required when decision is "clarify" or
                                          // "unsupported" (see Reason Codes below);
                                          // null when decision is "call" or "no_tool"
  },

  // ── Deterministic policy cross-check ────────────────────────────────────
  "policy_outcome": {
    "evaluated": true,                   // false only for "no_tool"/"clarify"/"unsupported"
                                          // examples where no call was proposed to evaluate
    "risk_level": "read_workspace",      // ToolRiskEngine.ToolRiskLevel value, lowercase
    "is_destructive": false,
    "touches_outside_workspace": false,
    "network_access": false,
    "block_reason": null,                // non-null string means ToolPolicyEngine hard-blocks
    "policy_gap_tool": false             // true when this example's tool is grep_code or
                                          // ask_user, i.e. ToolPolicyEngine.Evaluate() has no
                                          // dedicated case for it and fell through to the
                                          // default ReadWorkspace assessment — see
                                          // TOOLCALLER_V0_FROZEN_INVENTORY.md's known gap
  },

  // ── Review and split ─────────────────────────────────────────────────────
  "review_status": "accepted",           // "pending" | "accepted" | "rejected"
  "reviewer": "human:hce",               // "auto" | "human:<initials>"
  "split": "train",                      // "train" | "eval" — assigned before any candidate
                                          // training; every member of a lineage_group_id
                                          // must share the same split
  "notes": "",
  "tags": []
}
```

---

## Decision Taxonomy

| Value | Meaning |
|---|---|
| `call` | Exactly one tool call is the correct proposal; `tool` and `arguments` are required and must be exact |
| `no_tool` | The request is answerable without invoking any tool in the frozen v0 universe |
| `clarify` | Required information is missing or the request is ambiguous; a `reason_code` is required |
| `unsupported` | The request cannot be represented by any tool in the frozen v0 universe; a `reason_code` is required |

`policy_outcome` is evaluation context recorded alongside the example, never the model's
target decision. A `call` example's proposed tool/arguments are separately run through
the real `ToolPolicyEngine.Evaluate()` to confirm the recorded `policy_outcome` matches —
disagreement between the two is a hard dataset-admission failure (see below), not
something to silently reconcile by editing the expected decision.

## Reason Codes (`clarify` / `unsupported`)

| Value | Applies to | Meaning |
|---|---|---|
| `missing_required_argument` | `clarify` | The tool is clear but a required argument value is absent from the request |
| `ambiguous_target` | `clarify` | Multiple plausible tools or targets exist and the request doesn't disambiguate |
| `ambiguous_intent` | `clarify` | The request's goal itself is unclear, independent of tool/argument choice |
| `no_matching_tool` | `unsupported` | No tool in the frozen v0 universe can represent the request at all |
| `tool_outside_role` | `unsupported` | A matching tool exists in the frozen 6 but not in the originating role's available subset |

## Role Taxonomy

Matches `SwarmWorkerRole` (`OrchestratorIDE/Agents/SwarmSession.cs`), lowercased:
`researcher`, `coder`, `ui_developer`, `tester`. Do not use "boss/reviewer/worker" —
those are not current `SwarmWorkerRole` values (see
[TOOLCALLER_V0_FROZEN_INVENTORY.md](../docs/TOOLCALLER_V0_FROZEN_INVENTORY.md)).

---

## Dataset Admission Gates

In addition to [FOUNDRY_ARENA.md](../docs/FOUNDRY_ARENA.md)'s general dataset admission
gate, a toolcaller capture hard-fails mechanical validation on:

- `expected.tool` absent from the frozen tool universe (`toolcaller_v0_frozen_tools.json`)
- `expected.tool` present but absent from the example's own `available_tools`
- `expected.arguments` containing a key not in the tool's frozen parameter schema
  (invented argument), or missing a required parameter
- `decision == "call"` with `expected.arguments` absent or incomplete
- `decision` in `{"clarify", "unsupported"}` with `reason_code` null
- `policy_outcome.evaluated == true` but the recorded outcome disagrees with a fresh
  `ToolPolicyEngine.Evaluate()` run against `expected.tool`/`expected.arguments`
- `approval_state` implying the call already executed or was already approved by the
  model itself, rather than being upstream context the request carries in
- any two examples sharing a `lineage_group_id` assigned to different `split` values
- `tool_schema_hash` not matching the currently frozen inventory hash (stale example,
  must be regenerated or explicitly re-validated before use)

Mechanical validation runs before any model-based judge, matching the general Foundry
Arena admission gate.

---

## File Naming

```
training_pit/datasets/toolcaller/
  toolcaller_capture_{split}_{example_id}.json
```

One JSON object per file, mirroring the plan-capture convention
(`PLAN_CAPTURE_SCHEMA.md`) rather than JSONL — captures are reviewed and admitted
individually before any export/conversion step produces a training-ready JSONL.

---

## Relationship To Existing Formats

| Format | Captures | Toolcaller-v0 reuses |
|---|---|---|
| `DATASET_SCHEMA.md` (chat JSONL) | Final SFT training format (`messages[]` + flat metadata) | File-per-example → reviewed-manifest → JSONL export pipeline shape; not the field layout |
| `PLAN_CAPTURE_SCHEMA.md` (plan capture) | Boss plan decomposition + quality rubric | Identity/versioning conventions (`schema_version`, `example_id` date-stamped ID), one-JSON-per-file staging, `annotator`/review fields |
| `TOOLCALLER_CAPTURE_SCHEMA.md` (this doc) | Bounded tool-proposal decision + policy cross-check | — |

No existing auto-capture hook targets this schema. Building one (e.g. a
`ToolcallerDatasetCapture` mirroring `DatasetCapture.cs`) is F-2+ work, not authorized by
this schema definition alone.

---

## Version History

| Version | Date | Changes |
|---|---|---|
| toolcaller-v0 | 2026-07-03 | Initial schema, derived from THEORC_TOOLCALLER_V0.md's canonical example shape and dataset requirements |
