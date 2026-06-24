# TheOrc — Worktree-Per-Task Isolation (Design)

> **Status:** ✅ Shipped v1.5 (opt-in via `AppSettings.HiveWorktreeIsolation`).
> `FileOwnershipLedger`, `WorktreeManager`, per-task git worktrees, strict file
> ownership, and the T16 test suite all landed. This document is the original
> design contract the implementation followed. Pattern adapted from SigmaLink's
> worktree-per-task model and its "strict file ownership" rule, fitted to TheOrc's
> staged-run architecture and HIVE MIND distributed swarm.

> Related: [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) (distributed swarm),
> [ARCHITECTURE.md](ARCHITECTURE.md) (system view),
> [SWARM_GUIDE.md](SWARM_GUIDE.md) (user-facing swarm flow).

---

## 1. The Problem

TheOrc isolates work at the **run** level, not the **task** level.

Today every task in a swarm run writes into one shared staging directory:

```
<workspace>/.orc/swarm/runs/<runId>/staging/
```

Multiple tasks in the same run can target the same file. When a CODER task
and a UIDEVELOPER task both write `MainWindow.xaml.cs`, the result is
**last-write-wins** — silent, unordered, and impossible to attribute. There is
no record of which task produced which change, so a bad change from one task
cannot be discarded without discarding the whole run.

SigmaLink solved the equivalent problem with two coupled mechanisms:

1. **Worktree per task** — each task works in its own isolated git worktree
   (`sigmalink/<role>/<task>-<8char>`), so concurrent writes never collide on
   disk.
2. **Strict file ownership** — "No two concurrent tasks can own the same file,
   period." The orchestration layer sequences tasks that would share a file.

Together these make parallel multi-agent work **conflict-free by
construction**: if ownership is enforced before dispatch, merges cannot
conflict, because no two tasks ever touch the same file at the same time.

This document adapts both mechanisms to TheOrc.

---

## 2. Goals and Non-Goals

### Goals

- **G1** — Each task's file output is isolated from every other task's output
  during execution.
- **G2** — Two concurrent tasks can never own the same file. Conflicting tasks
  are sequenced (dependency edge) instead of parallelized.
- **G3** — Per-task changes are individually attributable and individually
  reversible. Keeping task A's work while discarding task B's is a supported
  operation.
- **G4** — Works in both **repo mode** (workspace is a git repo) and
  **greenfield mode** (workspace is empty / non-git — TheOrc building a new
  project from a goal).
- **G5** — Composes with HIVE MIND without changing the wire format. Remote
  workers stay stateless.
- **G6** — Opt-in and non-breaking. The existing flat-staging path remains the
  default until the new path is proven.

### Non-Goals (this document)

- **N1** — "Zero idle chatter" message discipline. Separate concern; lands in
  the HiveTaskQueue message contract, not here.
- **N2** — `/events` lifecycle side-channel. Related (it would observe worktree
  lifecycle), but specified separately.
- **N3** — Boss session resume across restarts. Orthogonal; belongs with the
  SQLite task board work (v1.6).
- **N4** — Cross-machine shared worktrees. Workers remain stateless text/file
  producers; isolation is Warchief-side only.

---

## 3. Two Modes

TheOrc is not always operating on an existing repository — it frequently builds
a new project from a one-sentence goal. The design must handle both.

### Repo mode — workspace is a git repository

Each task gets a **real git worktree** branched off the run's integration
branch:

```
git worktree add <runDir>/trees/<role>-<taskId> <integrationBranch>
```

- Native git isolation: the task sees a full checkout, edits freely, commits to
  its own branch `orc/run-<runId>/<role>-<taskId>`.
- Merge back is `git merge --no-ff` of the task branch into the integration
  branch. Ownership guarantees no conflict.

### Greenfield mode — workspace is empty or non-git

There is no repo to branch from. Each task gets an **isolated directory** with
the same ownership semantics:

```
<runDir>/trees/<role>-<taskId>/
```

- A scratch repo is `git init`-ed at the **run** level
  (`<runDir>/integration/`) so the same merge/attribution machinery works.
- Task directories are plain working copies; "merge" copies owned files into
  the integration tree and commits them with the task as author.
- If the user later applies the run to the workspace, they get a clean git
  history of which task produced which file — a strict upgrade over today's
  flat staging.

**Mode is detected once per run** by probing for `.git` at the workspace root
(reuse `LibGit2Sharp.Repository.IsValid`). The chosen mode is recorded in the
run manifest so resume/inspection is unambiguous.

---

## 4. Core Primitives

Two new services, both pure-C#, no UI, no Ollama dependency — so they are unit
testable without a live model (per the repo's pure-logic test convention).

### 4.1 `WorktreeManager`

Owns the lifecycle of per-task working areas.

```csharp
namespace OrchestratorIDE.Services.Swarm;

public sealed class WorktreeManager : IDisposable
{
    public WorktreeManager(string runDir, string? workspaceRoot);

    /// <summary>Detected once: true if workspaceRoot is a valid git repo.</summary>
    public bool RepoMode { get; }

    /// <summary>Create an isolated worktree for a task. Idempotent per taskId.</summary>
    public WorktreeHandle Acquire(string taskId, string role);

    /// <summary>Merge a finished task's owned files into the integration tree.
    /// Throws WorktreeConflictException if a file collision is detected —
    /// which means ownership was violated upstream (a scheduler bug).</summary>
    public MergeResult Merge(WorktreeHandle handle, IReadOnlyCollection<string> ownedFiles);

    /// <summary>Remove a task's worktree. Failed tasks keep theirs for
    /// inspection unless force=true.</summary>
    public void Release(WorktreeHandle handle, bool force = false);

    /// <summary>Reap worktrees older than maxAge (abandoned runs). Mirrors
    /// HiveTaskQueue's terminal-entry eviction.</summary>
    public int ReapStale(TimeSpan maxAge);
}

public sealed record WorktreeHandle(string TaskId, string Role, string Path, string? Branch);
public sealed record MergeResult(int FilesMerged, IReadOnlyList<string> Files);
public sealed class WorktreeConflictException : Exception { /* carries the colliding path + both taskIds */ }
```

### 4.2 `FileOwnershipLedger`

The enforcement core. Maps a file path to the single task that owns it for the
duration of a run.

```csharp
public sealed class FileOwnershipLedger
{
    /// <summary>Claim ownership of files for a task. Returns the subset that
    /// could NOT be claimed because another task already owns them — the
    /// caller (scheduler) uses this to add a sequencing dependency instead
    /// of dispatching the task in parallel.</summary>
    public IReadOnlyList<OwnershipConflict> TryClaim(string taskId, IEnumerable<string> files);

    /// <summary>Release a task's claims (on completion or abandonment) so a
    /// sequenced successor can claim the same files.</summary>
    public void Release(string taskId);

    /// <summary>Who owns this file right now? Null = unowned.</summary>
    public string? OwnerOf(string normalizedPath);

    /// <summary>Full snapshot for the run manifest + UI.</summary>
    public IReadOnlyDictionary<string, string> Snapshot();
}

public sealed record OwnershipConflict(string Path, string RequestedBy, string OwnedBy);
```

Path normalization is critical and must be exact: case-fold on Windows,
forward-slash separators, repo-relative. Reuse the same normalization the
review_sweep tooling uses so `Services/Foo.cs` and `services\foo.cs` resolve to
one key.

---

## 5. Where Owned Files Come From — Declare-then-Verify

The hard part. Ownership enforcement needs to know *which files a task will
touch* **before** the task runs, so the scheduler can sequence conflicts. But
the boss plan describes files in prose, and parsing them reliably is the same
fragile regex problem the dataset review tooling hit.

The design uses **declare-then-verify**, never trusting either signal alone:

1. **Declare (pre-dispatch, best-effort).** Parse declared output files from
   the task description using the existing filename-anchor regex
   (`FILE_ANCHOR` in `generate_goals.py` / `review_sweep.py` — already
   battle-tested). Claim them in the ledger. Conflicts → sequence the task.

2. **Verify (post-execution, authoritative).** After a task finishes, diff its
   worktree against the integration branch to get the **actual** touched files.
   This is ground truth — git knows exactly what changed.

3. **Reconcile.** If actual files ⊆ declared files → clean merge. If a task
   touched a file it did not declare, and that file is owned by another task →
   `WorktreeConflictException` → escalate to the user with both task IDs and
   the path. This converts a silent last-write-wins corruption (today) into a
   loud, attributable, reviewable event.

The verify step means **even imperfect declaration is safe**: under-declaration
is caught at merge, never silently applied.

---

## 6. Lifecycle

```
Boss plan (N tasks, each with declared output files)
        │
        ▼
Scheduler.AssignOwnership()
        │  TryClaim per task in priority order
        │  conflict → add dependency edge (sequence, don't parallelize)
        ▼
For each dispatch-ready task (parallel where ownership-disjoint):
        │
        ├─ WorktreeManager.Acquire(taskId, role)   → isolated tree
        │
        ├─ Worker executes (local OR remote HIVE)
        │     • local: writes directly into the worktree
        │     • remote: returns files as HiveTaskResult; Warchief writes them
        │       into the locally-acquired worktree (workers stay stateless)
        │
        ├─ Verify: git diff worktree vs integration → actual touched files
        │
        ├─ WorktreeManager.Merge(handle, actualFiles)
        │     • ownership-clean → fast-forward/copy, commit as task author
        │     • violation → WorktreeConflictException → escalate
        │
        ├─ FileOwnershipLedger.Release(taskId)   → unblock sequenced successors
        │
        └─ WorktreeManager.Release(handle)       → teardown (keep on failure)
        ▼
Integration tree holds the merged result, one commit per task,
fully attributable — replaces today's flat staging dir.
```

---

## 7. HIVE MIND Integration

The crucial design choice: **worktree isolation is entirely Warchief-side.**

A remote worker receives a `HiveTaskBundle`, executes it against its own local
Ollama, and POSTs a `HiveTaskResult` containing the produced files as text. It
has no knowledge of worktrees, branches, or ownership. **The wire format does
not change.**

When the Warchief receives a remote result, the integration path is identical
to a local task:

1. Warchief already holds the worktree it `Acquire`d for that task ID
   (acquired at dispatch time, before the bundle was pushed to the queue).
2. Warchief writes the worker's returned files into that worktree.
3. Verify + Merge + Release proceed exactly as for a local task.

This means file-ownership enforcement happens at **one** place — the Warchief
integration point — regardless of where the task physically ran. Distributed
and local tasks share one enforcement path. No new failure modes are introduced
into the queue protocol.

A second-order benefit: the ownership ledger becomes part of
`HiveSessionContext`. A worker that fetches session context could be told its
declared file lane (advisory — "you should only be producing these files"),
giving us the "strict scope" discipline as a *soft* signal to the worker and a
*hard* signal at Warchief merge. Optional; not required for v1.

---

## 8. Conflict and Failure Handling

| Situation | Handling |
|---|---|
| Two tasks declare the same file | Scheduler sequences them (dependency edge). Never parallel. No conflict possible. |
| Task touches an undeclared, owned file | Caught at Merge. `WorktreeConflictException`. Escalate to user with both task IDs + path. Run pauses for that file. |
| Task touches an undeclared, unowned file | Allowed. Claimed retroactively at merge. (Common and benign — boss under-declared.) |
| Worker crashes / times out | Worktree kept (not released). Run continues with other tasks. User can inspect the dead worktree. Reaper cleans it after maxAge. |
| Merge genuinely conflicts (repo mode) | Impossible if ownership held — so a real conflict is a *scheduler bug*, surfaced loudly, never auto-resolved. |
| App restart mid-run | Worktrees + integration branch survive on disk. Run manifest records mode + ledger snapshot → resumable (ties into session-resume work, N3). |

The guiding principle, borrowed from the HiveTaskQueue stale-worker guards:
**fail loud and attributable, never silent and lossy.** Today's flat staging
fails silently (last-write-wins). The whole point of this design is to convert
that class of bug into a visible, reviewable escalation.

---

## 9. Phased Rollout (Non-Breaking)

### Phase 1 — Primitives, opt-in, default off

- Ship `WorktreeManager` + `FileOwnershipLedger` + their unit tests.
- New setting `HiveWorktreeIsolation` (default **false**).
- When false: existing flat-staging path runs unchanged. Zero behavior change.
- When true: SwarmSession routes task output through worktrees.
- **Deliverable:** primitives proven by tests; opt-in users can try it.

### Phase 2 — Scheduler ownership enforcement

- Scheduler computes declared files, calls `TryClaim`, adds sequencing edges
  for conflicts.
- Verify-at-merge wired in. `WorktreeConflictException` escalation surfaced in
  the Swarm board UI.
- **Deliverable:** conflict-free parallel runs in repo mode.

### Phase 3 — Default on for repo-mode

- `HiveWorktreeIsolation` defaults **true** when the workspace is a git repo.
- Greenfield mode remains opt-in one release longer (scratch-repo path needs
  more soak time).
- **Deliverable:** the SigmaLink-grade guarantee as the default for repo users.

Each phase is independently shippable and independently revertible.

---

## 10. Testing Strategy

Pure-logic tests (no FlaUI, no Ollama), in `OrchestratorIDE.UITests/Tests` as
`T##_WorktreeIsolation*.cs`:

- `FileOwnershipLedger_TwoTasksSameFile_SecondIsRejected`
- `FileOwnershipLedger_ReleaseUnblocksSuccessor`
- `FileOwnershipLedger_PathNormalization_CaseAndSeparators`
- `WorktreeManager_GreenfieldMode_InitsScratchRepo`
- `WorktreeManager_RepoMode_AcquireCreatesBranch` (uses a temp git repo fixture)
- `WorktreeManager_Merge_DisjointOwnership_NoConflict`
- `WorktreeManager_Merge_UndeclaredOwnedFile_Throws`
- `WorktreeManager_ReapStale_RemovesOldAbandonedTrees`
- `Scheduler_DeclaredCollision_AddsSequencingEdge`

A temp-repo fixture (`git init` in a temp dir, seeded with one file) gives the
repo-mode tests a real LibGit2Sharp target without touching the user's
workspace.

---

## 11. Open Questions

1. **Declared-file extraction accuracy.** The `FILE_ANCHOR` regex is good but
   not perfect. How often does under-declaration force a merge-time
   reconciliation vs. clean fast-path? Measure on the existing captured plans
   before committing to Phase 2 thresholds.
2. **Greenfield scratch-repo cost.** A `git init` + per-task commit per run adds
   overhead. Is it acceptable for fast iterative runs, or do we need a
   "lightweight greenfield" path that skips git and just uses directory
   isolation + a JSON ownership log?
3. **Worktree count ceiling.** A 4-task run = 4 worktrees. A 20-task HIVE run =
   20 local worktrees on the Warchief. Disk + inode pressure? Cap concurrent
   worktrees and queue beyond the cap?
4. **Interaction with DatasetCapture.** Plan captures stage independently of
   task output. Confirm worktree routing does not change the capture path
   (it should not — captures happen at plan time, before any worktree exists).
5. **Should the ledger persist to SQLite (v1.6 task board) or stay in-memory +
   manifest JSON?** In-memory is fine for a single run; persistence matters
   only for cross-restart resume (N3).

---

## 12. Why This Composes With What We Already Have

This is not a rewrite. It slots into existing seams:

- **Run directory structure** already exists (`.orc/swarm/runs/<runId>/`).
  Worktrees are a new subdirectory (`trees/`) beside the existing `staging/`.
- **LibGit2Sharp** is already a dependency (used for the status bar git stamp
  and repo operations).
- **The boss already names output files** in task descriptions — the
  declaration signal exists, we just start enforcing it.
- **HIVE's stateless-worker model** is preserved exactly; isolation is
  Warchief-side, so the queue protocol and wire format are untouched.
- **The "fail loud" philosophy** matches the HiveTaskQueue stale-worker guards
  already in the codebase.

The net effect: TheOrc gains SigmaLink's conflict-free-by-construction
guarantee while keeping its distinctive strengths (greenfield generation,
distributed execution, local models) fully intact.

---

## Version History

| Version | Date | Notes |
|---|---|---|
| 0.1 | 2026-06-13 | Initial design. Adapted from SigmaLink worktree-per-task + strict file ownership; fitted to TheOrc staged-run + HIVE architecture. No code yet. |
