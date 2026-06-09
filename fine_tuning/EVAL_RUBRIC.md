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

*Last updated: 2026-06-09 — v1.0 initial rubric.*
