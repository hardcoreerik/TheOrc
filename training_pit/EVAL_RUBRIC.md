# Boss Plan Evaluation Rubric

> **Version:** 1.0
> **Status:** Defined. Not yet wired to auto-scoring in SwarmSession.
> This rubric drives two things: (1) dataset capture qualification, (2) the `autoScore`
> field in `swarm-metrics.json`. Currently `autoScore` is set manually. Phase 2 wires
> this rubric to auto-populate it.

---

## Composite Score: 0–100

```
autoScore = task_count + description_depth + filename_presence + api_contract + domain_accuracy + json_validity
```

Maximum per dimension and weight rationale:

| Dimension | Max | Weight rationale |
|---|---|---|
| `task_count` | 20 | Correct decomposition is the core capability |
| `description_depth` | 25 | Empty descriptions are the primary failure mode |
| `filename_presence` | 15 | Filenames prevent worker ambiguity |
| `api_contract` | 15 | Mismatched function names cause runtime errors |
| `domain_accuracy` | 10 | Task content must match the goal domain |
| `json_validity` | 15 | Non-parseable output is a hard failure |
| **Total** | **100** | |

---

## Dimension Definitions

### 1. Task Count (0–20)

Measures whether the boss produced the right number of tasks.

| Count | Score | Reasoning |
|---|---|---|
| 0 | 0 | Complete failure |
| 1 | 0 | Under-planning (gemma4 collapse pattern) |
| 2 | 12 | Acceptable minimum |
| 3 | 18 | Ideal for most goals |
| 4 | 20 | Ideal for complex goals |
| 5+ | 10 | Over-decomposition; tasks likely overlap |

**Auto-scoring:** Count `tasks[]` array length.

---

### 2. Description Depth (0–25)

Measures whether task descriptions are self-contained and actionable.

| Condition | Score |
|---|---|
| All tasks have descriptions ≥ 80 chars AND ≥ 2 sentences | 25 |
| All tasks have descriptions ≥ 40 chars | 18 |
| Most tasks (≥50%) have descriptions ≥ 40 chars | 10 |
| Any task has description = "" or null | 0 |
| Any task has description < 10 chars | 2 |

**Auto-scoring:** Check each task's `description` field length and sentence count
(split on `. ` and `.\n`).

**Key signal:** If ANY description is empty, this dimension scores 0 regardless of
other tasks — an empty description is the primary failure mode.

---

### 3. Filename Presence (0–15)

Measures whether task titles name the output file(s).

| Condition | Score |
|---|---|
| All task titles contain a filename (e.g. `cleaner.py`, `index.html`, `README.md`) | 15 |
| ≥75% of tasks have filenames in title | 10 |
| ≥50% of tasks have filenames in title | 6 |
| No titles contain filenames | 0 |

**Auto-scoring:** Regex match `[a-zA-Z0-9_-]+\.[a-zA-Z0-9]{1,6}` in each `title` field.

**Note:** Researcher tasks like "Research pandas API" with no filename are acceptable
only when their result feeds into a named file in a subsequent task.

---

### 4. API Contract Consistency (0–15)

Measures whether function/class names are consistent across tasks that share interfaces.

| Condition | Score |
|---|---|
| All cross-task function/class names match exactly | 15 |
| Minor inconsistency (one name differs between tasks) | 8 |
| Major inconsistency (same module referenced with different function names) | 2 |
| No cross-task dependencies (single coder task) | 10 (not applicable) |

**Auto-scoring (Phase 2):** Extract `from X import Y` and `def Y(` patterns from
descriptions; check that names referenced in task B appear in task A's description.

**Manual scoring (Phase 1):** Human annotator checks whether worker A and worker B
use the same function names for shared modules.

---

### 5. Domain Accuracy (0–10)

Measures whether the tasks are correctly scoped to the goal's domain.

| Condition | Score |
|---|---|
| All tasks are clearly in the goal's domain | 10 |
| Most tasks match, one is off-domain | 6 |
| Tasks solve a different problem than the goal | 0 |

**Example of score 0:** Goal = "Build a CSV cleaner", boss produces tasks for a web scraper
(Combo D failure pattern: gemma4:12b boss + qwen2.5-coder:7b coder).

**Auto-scoring (Phase 2):** Cosine similarity between goal embedding and task description
embeddings using `nomic-embed-text`. Threshold: <0.4 similarity = off-domain.

**Manual scoring (Phase 1):** Human annotator.

---

### 6. JSON Validity (0–15)

Measures whether the boss output is parseable and schema-compliant.

| Condition | Score |
|---|---|
| Valid JSON, all required fields present, no markdown fences | 15 |
| Valid JSON but wrapped in markdown fences (ParseBossPlan strips these) | 12 |
| Valid JSON but missing `plan` field | 10 |
| Valid JSON but `tasks` is empty array | 5 |
| Invalid JSON (parse error) | 0 |

**Required fields check:**
- Root: `tasks` (array, non-empty)
- Each task: `role`, `priority`, `title`, `description`

**Auto-scoring:** Attempt `JsonNode.Parse()`, check field presence.

---

## Score Thresholds

| Range | Class | Meaning |
|---|---|---|
| 90–100 | Excellent | Dataset gold example. Use as few-shot in Modelfile. |
| 70–89 | Pass | Qualifies for training dataset (positive example). |
| 40–69 | Marginal | Log but don't train on. Review manually. |
| 10–39 | Fail | Negative example. Include in training dataset as a failure to unlearn. |
| 0–9 | Hard fail | JSON invalid or empty output. Negative example only. |

**Auto-capture threshold:** ≥ 70 (positive), ≤ 39 (negative).

---

## Benchmark vs Rubric Alignment

The `swarm-metrics.json` `autoScore` field should be derivable from this rubric.
Current benchmark scores were set manually and are close but not exact:

| Run | autoScore (manual) | Rubric estimate |
|---|---|---|
| Combo A (qwen2.5-coder:14b boss) | 85 | ~88 (missed filename on one task) |
| Combo B (gemma4 boss, empty desc) | 30 | ~5 (json_valid=12, task_count=0, desc=0, rest=0) |
| Combo C (qwen2.5 boss, unintegrated UI) | 38 | ~42 (good plan, bad execution reflected) |
| Combo D (gemma4 boss, off-domain) | 18 | ~15 (empty desc=0, off-domain=0, poor plan) |
| Combo E (qwen2.5 boss, gemma4 workers) | 68 | ~65 (good plan, missing sample + README) |

> Note: The rubric scores boss *planning quality*, not end-to-end output quality.
> Combo C's low score reflects a weak plan even though csv_cleaner.py was correct —
> the boss didn't specify the UI integration contract precisely enough.

---

## Future: Automated Scoring

When Phase 2 starts, add `EvalRubric.cs` to `OrchestratorIDE/Services/Swarm/`:

```csharp
public static class EvalRubric
{
    public static RubricResult Score(List<SwarmTask> tasks, string userGoal)
    {
        return new RubricResult
        {
            TaskCount        = ScoreTaskCount(tasks),
            DescriptionDepth = ScoreDescriptionDepth(tasks),
            FilenamePresence = ScoreFilenamePresence(tasks),
            ApiContract      = ScoreApiContract(tasks),
            DomainAccuracy   = 10,  // placeholder until embedding model wired
            JsonValidity     = 15,  // tasks parsed = valid
        };
    }
    // ... per-dimension implementations
}

public record RubricResult(
    int TaskCount, int DescriptionDepth, int FilenamePresence,
    int ApiContract, int DomainAccuracy, int JsonValidity)
{
    public int Composite => TaskCount + DescriptionDepth + FilenamePresence
                          + ApiContract + DomainAccuracy + JsonValidity;
    public string Class => Composite switch { >= 90 => "excellent", >= 70 => "pass",
                                              >= 40 => "marginal", _ => "fail" };
}
```

Do not implement this class until Phase 2.

---

## TheOrc Boss Behavior Rubric (0–26)

This rubric measures TheOrc-specific behavioral quality across 13 categories.
It is used to evaluate whether an adapter actually improves boss behavior vs the base model,
and as a before/after benchmark after fine-tuning.

**Scoring:** Each category is scored 0, 1, or 2.
- **2** = fully meets the criterion
- **1** = partially meets (present but incomplete or inconsistent)
- **0** = absent or fails

**Overall thresholds:**

| Range | Grade | Meaning |
|---|---|---|
| 23–26 | Strong | Ship it. Use as production boss model. |
| 17–22 | Good | Viable for testing. Minor issues tolerable. |
| 9–16 | Partial | Some behaviors present but inconsistent. Do not ship. |
| 0–8 | Poor | Fundamental failures. Not usable as boss. |

---

### Categories

#### 1. TheOrc Boss Format (0–2)
Responds only with valid JSON matching the `{plan: string, tasks: [...]}` schema.
No markdown prose, no explanatory text outside the JSON, no code fences.

- **2**: Pure JSON, correct schema, every time
- **1**: JSON present but occasionally wrapped in markdown or extra text
- **0**: Prose response, invalid JSON, or missing required fields

---

#### 2. Task Count Correctness (0–2)
Produces 2–4 tasks that map to distinct, non-overlapping units of work.

- **2**: Always 2–4 tasks, each clearly distinct
- **1**: Occasionally produces 1 task or 5+ overlapping tasks
- **0**: Consistently produces 1 task ("Execute goal") or 0 tasks

---

#### 3. Root Cause Identification (0–2)
When the goal involves a bug, error log, or failing test, identifies the root cause before
prescribing the fix. Does not patch symptoms.

- **2**: Names the specific root cause (e.g. "the upload_file() signature mismatch") and directs the fix at it
- **1**: Identifies a general problem area but not the specific cause
- **0**: Prescribes a fix without diagnosing the cause, or misidentifies the problem

---

#### 4. Hallucination Resistance (0–2)
Does not invent files, functions, classes, or project structure that has not been
shown to exist. When context is missing, asks rather than guesses.

- **2**: Never references unconfirmed code paths; explicitly marks assumptions
- **1**: Occasionally references plausible-but-unconfirmed names
- **0**: Freely invents file names, function signatures, or project structure

---

#### 5. Minimal Patch Direction (0–2)
Scopes worker tasks to the smallest change that achieves the goal. Does not assign
rewrites when a targeted change is possible.

- **2**: Every task is scoped to specific functions or sections; no broad rewrites
- **1**: Most tasks are targeted, but one over-scopes to a module or file rewrite
- **0**: Tasks assign full rewrites, "refactor everything," or are underspecified

---

#### 6. Validation Commands Included (0–2)
Each coding task includes a concrete validation step: a test command, smoke test,
or acceptance criterion the worker can check.

- **2**: Every CODER task has a concrete validation command or acceptance test
- **1**: Some tasks have validation; others lack it
- **0**: No tasks include any validation or acceptance criteria

---

#### 7. Appropriate Delegation (0–2)
Assigns the right role (RESEARCHER, CODER, TESTER, DOCS) to each task and respects
role boundaries. Does not assign research to CODER or coding to RESEARCHER.

- **2**: Every role assignment is correct and appropriate
- **1**: One role mismatch (e.g. CODER assigned to do dependency research)
- **0**: Roles ignored, all tasks assigned to one role, or role field missing

---

#### 8. Uncertainty Handling (0–2)
When asked about something it doesn't know (missing files, unknown API, ambiguous goal),
acknowledges uncertainty explicitly rather than fabricating a confident answer.

- **2**: Flags missing context; asks one targeted question; does not invent
- **1**: Sometimes flags uncertainty, sometimes guesses
- **0**: Presents invented answers as fact; never acknowledges gaps

---

#### 9. Windows/PowerShell Defaults (0–2)
When writing shell commands or scripting instructions, defaults to PowerShell syntax,
Windows paths, and Windows-native tools. Does not default to bash, `ls`, or `/usr/bin/` paths.

- **2**: All commands use PowerShell syntax; paths use `\` or `$env:` variables
- **1**: Mostly correct; occasional bash-ism (`ls` instead of `Get-ChildItem`, etc.)
- **0**: Defaults to bash/Unix; requires user correction for Windows usage

---

#### 10. Architecture Preservation (0–2)
Respects existing project structure. Does not introduce new dependency management systems,
new build configurations, or new module layouts without explicit user request.

- **2**: Tasks build on existing architecture; no unexplained structural changes
- **1**: Minor structural addition that is not destructive but wasn't requested
- **0**: Proposes or implements architectural changes that break existing structure

---

#### 11. Conciseness (0–2)
The `plan` field is a single focused sentence. Task descriptions are dense and directive,
not padded with qualifications, caveats, or restated context.

- **2**: Plan is ≤ 1 sentence; descriptions are 2–5 focused sentences
- **1**: Plan is acceptable; some task descriptions padded with redundant context
- **0**: Plan is a paragraph; tasks have excessive caveats, repeated goals, or filler

---

#### 12. Constraint Compliance (0–2)
Follows explicit user constraints: "no external dependencies," "keep it in one file,"
"Python only," etc. Does not silently override user-specified limitations.

- **2**: All constraints observed without prompting
- **1**: Most constraints followed; one constraint overlooked
- **0**: User constraints ignored or violated; wrong language, framework, or scope

---

#### 13. Assumption Transparency (0–2)
Separates what is known (from the goal/context) from what is assumed (inferred defaults).
Labels assumptions explicitly when they affect task direction.

- **2**: Clear distinction between confirmed facts and stated assumptions
- **1**: Implicit assumptions present but not harmful; would benefit from labeling
- **0**: Assumptions presented as facts; no acknowledgment that context is incomplete

---

## Using Both Rubrics

| Rubric | Scale | Use case |
|---|---|---|
| Plan Quality Rubric | 0–100 | Auto-scoring boss plan decomposition quality. Dataset capture qualification. |
| Boss Behavior Rubric | 0–26 | Before/after adapter evaluation. Behavioral alignment check. |

A model can score high on Plan Quality (valid JSON, correct task count, good descriptions)
while scoring low on Boss Behavior (hallucinating APIs, ignoring constraints, using bash defaults).
Both rubrics must be evaluated when comparing base model vs adapter.

---

*Last updated: 2026-06-09 — v1.1: Boss Behavior Rubric added.*
