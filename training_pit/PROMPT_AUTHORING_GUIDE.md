# TheOrc Training Pit — Capture Prompt Authoring Guide

> **Purpose:** a self-contained spec for writing swarm goal prompts that farm
> high-quality boss-plan training examples. Share this file with any AI or human
> who is authoring a new prompt tranche. No other project context is required.

> **WPF/`.xaml` note:** The WPF/`MainWindow.xaml` references in the examples below reflect the
> **v1 training corpus**, which targeted TheOrc's WPF codebase (deleted 2026-06-20; the app is
> now Avalonia). Treat them as illustrations of the goal-anchoring *pattern*, not the current
> UI stack — retargeting the corpus to Avalonia is a separate, deliberate decision.

## What these prompts are for

TheOrc is a Windows-native multi-agent coding IDE. A "boss" model (Gemma 4 12B)
receives a user's coding goal and must decompose it into a JSON swarm plan of
2–4 tasks. Each task has a role, title, and description. We are fine-tuning the
boss with LoRA to do this decomposition reliably — **the prompts below generate
the training data**. Each prompt is run headless (`swarmcli --plan-only`); the
boss's plan is auto-scored by a rubric and staged for review. Approved plans
become training examples.

We are NOT teaching the model to code. We are teaching it to plan: 2–4 concrete
tasks, named output files, clear descriptions, consistent API contracts.

## The four roles (never invent others)

| Role | May write files? | Use for |
|---|---|---|
| RESEARCHER | no | investigate APIs, summarize approaches |
| CODER | yes | code files (.cs, .py, .ps1, logic) |
| UIDEVELOPER | yes | XAML/UI files |
| TESTER | **no — run-and-report only** | execute tests/programs, report results |

## File format

One prompt per line, pipe-delimited, no double quotes anywhere (they break
PowerShell argument passing — use single quotes inside prose):

```
V4-T001|wpf_ui|<goal text on one line>
```

Domains used so far: `wpf_ui`, `swarm`, `ollama`, `model_wiki`, `csharp_core`,
`testing`, `git`, `python_utility`, `powershell`, `training_pit`. Keep any
single domain under ~25% of a tranche.

## The rules (each one paid for in rejected captures)

1. **Anchor the stack with filenames.** Goals without explicit file names make
   the boss hallucinate a Python/web stack for what should be C# WPF work.
   Every goal names its target files: `MainWindow.xaml`, `Services/Foo.cs`,
   `training_pit/scripts/bar.py`. This is the single most important rule.

2. **One language stack per goal.** A C# goal that mentions `.py` tooling (or
   vice versa) confuses planning and produces collapsed or mixed plans. If two
   stacks are genuinely involved, pick one as the deliverable and drop the other
   from the wording.

3. **Never bait TESTER with creation verbs.** "Write tests for X" makes the
   boss assign file-writing to the no-write TESTER lane — automatic reject.
   Phrase test-creation goals so the new test FILE is the CODER deliverable,
   and TESTER "runs the new tests and reports results". Example ending:
   *"...The TESTER lane should run the new tests and report results."*

4. **Modify existing files, create only new ones.** Use "Add ... to
   MainWindow.xaml" / "Update X in Foo.cs" for files that exist; reserve
   "Create" for genuinely new paths. A plan that says "create" an existing
   file is a defect class we reject.

5. **No docs-edit goals.** Documentation updates produce single-task plans
   (auto-reject: a plan needs ≥2 tasks).

6. **Right-size the scope to 2–4 tasks.** A good goal naturally splits into
   research + code + UI, or code + integration + run-check. Too small
   (one-liner change) collapses to a single task; too sweeping invites
   fabrication. Roughly 25–50 words of specific intent works best.

7. **Name the integration point.** Don't just say what to build — say where it
   lands: "...and bind it into the status bar in MainWindow.xaml.cs",
   "...and call it from SwarmSession.cs after OnTasksPlanned fires".

8. **Stay deterministic-checkable.** Avoid vague verbs (improve, optimize,
   clean up, make better) in train prompts — those are negative-bait wording
   and will score as fabrication or collapse.

## Anatomy of a good prompt

```
Add a dark/light theme toggle to OrchestratorIDE. Create Services/ThemeManager.cs
that swaps merged ResourceDictionaries at runtime and persists the chosen theme
to app settings, then add a toggle button to the toolbar in MainWindow.xaml
wired up in MainWindow.xaml.cs.
```

Why it works: stack anchored by three filenames · single stack (C#/WPF) · new
file uses Create, existing files use add/wire · implies 3 tasks (service, XAML,
code-behind) · names the persistence and integration points.

## Anti-patterns (real rejects)

| Bad prompt | Failure it causes |
|---|---|
| "Add tests." | TESTER assigned file creation; also too vague |
| "Make the Model Wiki faster." | confident fabrication — invents profiling stacks |
| "Add a feature that shows GPU usage." | no filename anchor → Python/Flask hallucination |
| "Write T09 tests that verify review_captures.py" (in a C# goal) | mixed stack → repeated collapse |
| "Update the README with the new flags." | single-task plan |

## Reference workspace facts (so prompts name real files)

Real, existing files prompts may reference for *modification*:
`MainWindow.xaml(.cs)`, `SwarmBoardPanel.xaml(.cs)`, `ModelWikiWindow.xaml(.cs)`,
`SwarmSession.cs`, `SwarmTask.cs`, `OllamaClient.cs`, `DatasetCapture.cs`,
`review_captures.py`, `phase3_preflight.py`, `validate_dataset.py`,
`sanitize_dataset.py`, `convert_plan_captures.py`.
New files go under `Services/`, `Services/Swarm/`, `Controls/`, `Dialogs/`,
`Tools/`, `tools/` (PowerShell), or `training_pit/scripts/` (Python).
Tests are NUnit classes in `OrchestratorIDE.UITests/Tests/` named `T<nn>_<Name>Tests.cs`.

**Avoid duplicating prior tranches** — check `batch_v3_goals.psv` and
`BATCH_CAPTURE_PLAN_V2.md` before authoring; exact-duplicate goals are useless
and near-duplicates weaken the dataset.

## Delivery checklist for a new tranche

- [ ] Pipe-delimited `.psv`, one goal per line, IDs sequential (`V<n>-T<nnn>`)
- [ ] Zero double-quote characters in the file
- [ ] Every goal names at least one target file with extension
- [ ] No goal mixes language stacks
- [ ] Any test-creation goal makes CODER the file-writer, TESTER run-only
- [ ] No vague verbs (improve/optimize/clean up) in train goals
- [ ] Domain mix: no domain above ~25%

---
*Distilled from tranches v1–v3 (2026-06). Evidence trail: BATCH_CAPTURE_PLAN_V2.md
"What v1 taught us" and the reviewed_v1.json manifest rejection notes.*
