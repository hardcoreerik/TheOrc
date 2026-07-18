# CF-7 NoKvSlot 7e fix — Claude handoff

Generated: 2026-07-15 (America/Los_Angeles)

> **SUPERSEDED 2026-07-17.** PR #63 (referenced below as "not merged") merged
> the same day this doc was generated. The NoKvSlot fix held under a clean
> full-120 replay on both quants with zero NoKvSlot occurrences. The CF-7
> benchmark gate itself was then found to still be NO-GO on the honest
> expanded corpus for two unrelated reasons (open-extraction reader recall,
> exhaustive-leaf-coverage gate logic) fixed on `feat/cf7-open-extraction-
> recall-fix`, closing GO on 2026-07-17. See `docs/CURRENT_STATE.yaml`
> `context_fabric` for the current status. This file is kept for its root-
> cause narrative of the NoKvSlot crash, not as a live status document.

## Executive status

**PR #63 is not merged.** It is still an open draft PR targeting `master`:

- PR: <https://github.com/hardcoreerik/TheOrc/pull/63>
- Title: `Fix CF-7 Qwen3.5 NoKvSlot prompt overflow`
- Base: `master`
- Head: `codex/fix-cf7-nokvslot-storm-7e`
- Current head commit: `33d6eab2c3cb4e6aba88ad77c825f81b0522c0d3`
- GitHub merge state at handoff: `CLEAN`
- GitHub checks at handoff: Windows build passed; CodeRabbit passed
- Full unit suite: 564 passed, 4 skipped, 0 failed
- Solution build: passed with 0 errors and 36 pre-existing warnings
- Required remaining gate: live Qwen3.5 Q8 and Q4 CF-7 benchmark replay with the model files restored

Do not merge this PR without explicit human approval. Do not interpret a clean merge state or green checks as proof that the live CF-7 gate has passed.

## Where Claude should look first

Read these in order:

1. This handoff document.
2. The original task prompt:
   `C:\Users\hardc\AppData\Local\Temp\claude\F--Ai-OrchestratorIDE-dev\e4e8f875-35a3-4802-8262-232d9d48d583\scratchpad\codex-prompt-nokvslot-7e.md`
3. PR #63 and its exact diff:
   `git diff 0b97c758b152c6897c217c20d7170f79632d5894..33d6eab2c3cb4e6aba88ad77c825f81b0522c0d3`
4. The corrected root-cause narrative in `docs/CONTEXT_FABRIC_BUG_HISTORY.md`, section `7e`.
5. The implementation anchors listed below, especially `CountPromptTokens`, `TokenizePromptForLoadedModel`, `BuildAnswerMessages`, and `GetCompletionTokenLimit`.
6. The two historical failing logs at the repository root and their corresponding `.orc/adversarial` artifacts.
7. Grok review output under `.orc/reviews/` and the current CodeRabbit result on PR #63.
8. `Tools/ContextFabricBench/Run-CF7GateExpanded.ps1` before attempting the remaining live gate.

Stay on the current PR and current head. Do not spend time on old PRs, inherited CodeRabbit history, or unrelated untracked artifacts.

## Safe orientation commands

```powershell
Set-Location F:\Ai\OrchestratorIDE-dev
git fetch origin
git switch codex/fix-cf7-nokvslot-storm-7e
git status -sb
git rev-parse HEAD
git rev-parse origin/master
git rev-list --left-right --count origin/master...HEAD
gh pr view 63 --json number,url,title,state,isDraft,mergedAt,mergeCommit,baseRefName,headRefName,headRefOid,mergeStateStatus,statusCheckRollup
gh pr checks 63
```

Expected at this handoff:

- `HEAD` is `33d6eab2c3cb4e6aba88ad77c825f81b0522c0d3`.
- `origin/master` is `0b97c758b152c6897c217c20d7170f79632d5894`.
- The branch is 0 commits behind and 3 commits ahead of `origin/master`.
- PR #63 is `OPEN`, draft, and has `mergedAt: null`.
- The tracked worktree is clean. There are pre-existing untracked logs, scripts, and training data; preserve them.

## What was diagnosed

The CF-7 failure was a per-request attention KV-context exhaustion, not an accumulated allocator or lifecycle leak.

The fully rendered B3 Reviewer request plus its requested completion exceeded the model's `n_ctx=8192`. The old admission check used `ContextManager.EstimateTokens`, effectively a character-count heuristic with a 15% allowance. It undercounted Qwen3.5's JSON-heavy prompt enough to admit a request that could not fit in the actual native context.

This explains the observed error chain:

```text
NoKvSlot
failed to find a memory slot for batch of size ...
init_batch: failed to prepare attention ubatches
```

In the pinned llama.cpp implementation, the hybrid memory layer first prepares recurrent memory and then attention memory. The reported message occurs when attention preparation fails. The attention KV cache searches its fixed, preallocated cells for a slot. No allocation is attempted at this point. A recurrent-state or `SeqMax` failure would follow a different path and emit recurrent-specific diagnostics, which were absent.

The two failing runs also contradict a cumulative leak theory:

- The first failure in each run occurred on the first B3 Reviewer workload after roughly 168 Researcher conversations.
- Fresh Reviewer contexts later ran B0-B2 successfully.
- Context allocations, constructions, and destructors balanced in both logs.
- There was no CUDA out-of-memory signal.
- The affected answers had already prefetched and generated content before filling the context.

The existing degraded-role recycle remains useful defense in depth, but it cannot make an individually oversized prompt fit. The primary correction therefore had to be exact native token admission plus completion clamping.

## Upstream evidence used

Pinned llama.cpp commit in this repository:

- `3f7c29d318e317b63f54c558bc69803963d7d88c`

Key upstream references:

- Matching llama.cpp issue and maintainer diagnosis of total context exhaustion: <https://github.com/ggml-org/llama.cpp/issues/20049#issuecomment-4054052141>
- Maintainer clarification: <https://github.com/ggml-org/llama.cpp/issues/20049#issuecomment-4055032269>
- Pinned hybrid-memory failure site: <https://github.com/ggml-org/llama.cpp/blob/3f7c29d318e317b63f54c558bc69803963d7d88c/src/llama-memory-hybrid.cpp#L93-L104>
- Current hybrid-memory equivalent: <https://github.com/ggml-org/llama.cpp/blob/505b1ed15ca80e2a19f12ff4ac365e40fb374053/src/llama-memory-hybrid.cpp#L104-L115>
- Attention KV prepare/find-slot logic: <https://github.com/ggml-org/llama.cpp/blob/505b1ed15ca80e2a19f12ff4ac365e40fb374053/src/llama-kv-cache.cpp#L747-L810> and <https://github.com/ggml-org/llama.cpp/blob/505b1ed15ca80e2a19f12ff4ac365e40fb374053/src/llama-kv-cache.cpp#L989-L1084>
- Context wrapper around memory preparation: <https://github.com/ggml-org/llama.cpp/blob/505b1ed15ca80e2a19f12ff4ac365e40fb374053/src/llama-context.cpp#L1753-L1782>
- Recurrent-memory failure path: <https://github.com/ggml-org/llama.cpp/blob/505b1ed15ca80e2a19f12ff4ac365e40fb374053/src/llama-memory-recurrent.cpp#L484-L516>

LLamaSharp 0.27 references used to confirm the correct integration point:

- Context size: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/LLamaContext.cs#L26>
- Context tokenization API: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/LLamaContext.cs#L100-L110>
- `Conversation.Prompt` token behavior: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/Batched/Conversation.cs#L303-L352>
- `BatchedExecutor.Infer` and `NoKvSlot` requeue: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/Batched/BatchedExecutor.cs#L141-L178>
- Executor disposal: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/Batched/BatchedExecutor.cs#L238-L246>
- Context disposal: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/LLamaContext.cs#L441-L445>
- Native safe-handle release: <https://github.com/SciSharp/LLamaSharp/blob/7cbbc45e421d55794d5050d126e0b96511007007/LLama/Native/SafeLLamaContextHandle.cs#L78-L121>

The latest published LLamaSharp version remained 0.27 during the investigation. An unreleased sequence-ID-pooling change did not match this failure. No matching LLamaSharp multi-context disposal bug or later llama.cpp attention-slot correction was found.

## Historical log forensics

Primary logs:

- `F:\Ai\OrchestratorIDE-dev\cf7-phaseE-full120-q8_0.log` — approximately 119,000 lines
- `F:\Ai\OrchestratorIDE-dev\cf7-phaseE-full120-q4km.log` — approximately 118,000 lines

Do not reread either file wholesale. Start with these anchors or targeted searches.

Q8 anchors:

- Researcher recycle near line 75,768
- Reviewer context creation near line 76,651
- `n_ctx=8192` near line 76,651
- KV diagnostics near line 76,697
- recurrent-state diagnostics near lines 76,733-76,734
- compute near line 76,755
- first failure near lines 76,768-76,770

Q4 anchors:

- first Reviewer context near line 73,556
- first failure near lines 73,824-73,826

Affected artifacts:

- Q8: `.orc\adversarial\cf0_20260715_180638_433_db8d87ef84b14b689c4baab0ec3116fc.json`
  - question: `local-fact-001`
  - heuristic prompt count: 5,652
  - completion count: 6
  - generation had begun emitting JSON before `NoKvSlot`
- Q4: `.orc\adversarial\cf0_20260715_183634_866_16f42c56db084c3b89e0863898ffe68d.json`
  - heuristic prompt count: 5,603
  - completion count: 126
  - generation produced a substantive BR-048 answer and partial citations before `NoKvSlot`

Incident/recovery evidence:

- Q8: 96 incidents. Minted-conversation distribution was 89×1, 3×2, 3×3, 1×4, representing 12 additional successful conversations after the first mint.
- Q4: 59 incidents. Distribution was 41×1, 8×2, 5×3, 2×4, 2×8, 1×12.
- Balanced lifecycle totals: Q8 119 and Q4 81 allocations/constructions/destructors.

The older documentation's statement that Q4 began after about 144 conversations was corrected. The active executor had served approximately 168 conversations before the first B3 Reviewer request.

## Implemented fix

The PR contains three commits:

1. `27be08b1` — `Fix CF-7 NoKvSlot prompt overflow`
2. `a112d3b8` — `Address exact-token admission review`
3. `33d6eab2` — `Restore CF-0 budget regression coverage`

### Runtime token admission

`OrchestratorIDE/Core/Runtime/IRoleRuntime.cs`

- Adds optional `CountPromptTokens(...)`, defaulting to `null` so non-native and scripted runtimes remain compatible.
- `NativeRoleRuntime` provides an exact count only when the requested role's base model is currently loaded.
- The native stream path tokenizes the fully rendered prompt once, calculates the completion limit from the executor's actual context size, and queues the same `LLamaToken[]` that was counted.
- `GetCompletionTokenLimit` rejects a prompt that alone exceeds the context and otherwise clamps the requested completion to remaining native capacity.
- NoKvSlot comments were corrected; the existing degraded-role recycle remains defense in depth.

Important symbols:

- `IRoleRuntime.CountPromptTokens`
- `NativeRoleRuntime.CountPromptTokens`
- `NativeRoleRuntime.StreamResponseCoreAsync`
- `NativeRoleRuntime.GetCompletionTokenLimit`

`OrchestratorIDE/Core/Runtime/LLamaSharpRuntime.cs`

- Adds `TokenizePromptForLoadedModel`.
- Uses the same model-specific rendered prompt and native tokenizer settings used for inference: `add_bos: true`, `special: true`, UTF-8.
- Adds/uses `IsModelLoaded` to avoid pretending an exact count exists before native model load.

Important symbols:

- `LLamaSharpRuntime.TokenizePromptForLoadedModel`
- `LLamaSharpRuntime.IsModelLoaded`

### Context Fabric admission

`OrchestratorIDE/Services/ContextFabric/ContextFabricFeasibilityRunner.cs`

- Extracts the exact answer system prompt and centralizes answer-message construction in `BuildAnswerMessages`.
- Reuses `NoCompleteRootSummary` and passes the active corpus ID through evidence-pack construction.
- Counts the complete, fully rendered candidate request before selecting an evidence pack.
- Reserves the full configured answer completion allowance.
- Preserves MultiHop's 30% chase budget by subtracting the chase reserve from the exact admission limit.
- Uses exact token counting in the general `InvokeAsync` path whenever the active runtime can provide it.
- Keeps the old heuristic only as a fallback before the first native model load and for runtimes without an exact-token capability.

Important symbols:

- `BuildAnswerMessages`
- `BuildEvidencePack`
- `InvokeAsync`
- `NoCompleteRootSummary`
- the `chaseReserve` / `exactContextLimit` calculation

`OrchestratorIDE/Services/ContextFabric/ContextFabricContracts.cs`

- Updates contract comments to identify the heuristic as fallback behavior and exact native admission as primary.

### Regression coverage

`OrchestratorIDE.UnitTests/ContextFabricEvidencePackTests.cs`

- Covers the exact token boundary.
- Covers preservation of the MultiHop chase reserve.

`OrchestratorIDE.UnitTests/ContextFabricCf0Tests.cs`

- Adds focused exact-prompt-token telemetry coverage.
- Retains the pre-existing content-scaled CF-0 budget regression test after Grok identified that the first revision had accidentally replaced it.

`OrchestratorIDE.UnitTests/ContextFabricScriptedRuntime.cs`

- Adds an optional fake `PromptTokenCounter` for exact-admission tests.

`OrchestratorIDE.UnitTests/NativeRuntimeTestSupportTests.cs`

- Covers uncapped, capped, exactly-full, and overflow completion-limit behavior.

### Documentation corrected

- `docs/CONTEXT_FABRIC_BUG_HISTORY.md` section 7e: root cause, evidence, implemented fix, and pending full validation.
- `docs/CF_TEST_RESULTS.md`: keeps historical rows 15/16 invalid while correcting their interpretation.
- `docs/CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md`: compatibility row updated to show the live gate is pending.

## Validation already completed

Focused tests were run first, followed by the full suite and solution build.

Latest full unit result:

```text
dotnet test OrchestratorIDE.UnitTests\OrchestratorIDE.UnitTests.csproj --no-restore --verbosity minimal
Passed: 564
Skipped: 4
Failed: 0
Total: 568
```

Latest solution build:

```text
dotnet build OrchestratorIDE.slnx --no-restore --verbosity minimal
Build succeeded.
Warnings: 36
Errors: 0
```

The 36 warnings were pre-existing SQLite package advisories and setup nullability/platform warnings. The Windows GitHub Actions build passed.

Earlier targeted validation included:

- Initial exact-admission tests: 2 passed.
- Combined AdapterManager, LLama/runtime, and thinking tests: 56 passed, 3 native-configuration tests skipped.
- Post-review exact-admission tests: 3 passed.

## Review history

Mandatory Grok command used:

```powershell
pwsh -NoProfile -File "Tools/grok-review.ps1" -PR 63 -Model "grok-4.5"
```

First Grok pass found two `MINOR` issues:

1. The exact MultiHop gate did not mirror the existing 30% chase reserve.
2. The general `InvokeAsync` path still used the heuristic even when exact counting was available.

Both were fixed in `a112d3b8`.

The next Grok pass found one test-quality `MINOR`: the new mock-driven test had replaced the original content-scaled ceiling regression. The original coverage was restored and the focused exact-token test retained in `33d6eab2`.

The final two raw Grok verdicts were `CLEAN`. The wrapper returned exit code 1 because Grok prefixed the verdict with a sentence rather than emitting the bare token expected by the parser; inspect the raw files under `.orc\reviews\` rather than treating that parser exit as a substantive review failure. One known early file is `.orc\reviews\grok_20260715_195558.md`; sort that directory by modification time to locate later passes.

CodeRabbit initially skipped automatic review because the PR is draft. A manual `@coderabbitai review` request was made. CodeRabbit reviewed base `0b97c758b152c6897c217c20d7170f79632d5894` through head `33d6eab2c3cb4e6aba88ad77c825f81b0522c0d3`, covering 11 files, and reported no actionable comments. There were zero review threads at handoff.

## Remaining live gate

This is the only material validation gap.

The expected model directory `%APPDATA%\OrchestratorIDE\Models-CF7` was absent at handoff, and neither required model file was available there. No 15.21 GB model download was started because that is a material network/storage action requiring user approval. A GPU-heavy game was also running, so the benchmark was deliberately not started.

Required model files:

- Q8, approximately 9.53 GB: <https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/blob/main/Qwen3.5-9B-Q8_0.gguf>
- Q4_K_M, approximately 5.68 GB: <https://huggingface.co/unsloth/Qwen3.5-9B-GGUF/blob/main/Qwen3.5-9B-Q4_K_M.gguf>

Use an existing restored model root if one is available, such as `F:\Ai\Models-CF7`, or recreate the expected app-data model root. `Run-CF7GateExpanded.ps1` accepts `-ModelRoot`; do not copy or download the models without approval.

Before running:

1. Confirm the current PR head and a clean tracked worktree.
2. Confirm both GGUF files and their sizes/hashes if hashes are available.
3. Confirm the GPU is idle enough for the run.
4. Run Q8 and Q4 sequentially, never concurrently.
5. Preserve diagnostics and output artifacts.

### One-question Q8 replay

This is the narrowest meaningful live smoke check. Note that `-MaxQuestions 1` still processes all 128 reader/reduction segments; it only shortens answer/baseline work.

```powershell
$stamp = Get-Date -Format yyyyMMdd_HHmmss
$env:THEORC_KVCACHE_DIAGNOSTICS = '1'
pwsh -NoProfile -File ".\Tools\ContextFabricBench\Run-CF7GateExpanded.ps1" `
  -RepoRoot "F:\Ai\OrchestratorIDE-dev" `
  -ModelRoot "<restored-model-root>" `
  -Model "Qwen3.5-9B-Q8_0" `
  -MaxQuestions 1 `
  -Context 8192 `
  -LogFile "F:\Ai\OrchestratorIDE-dev\cf7-7e-smoke1-q8_0-$stamp.log"
$code = $LASTEXITCODE
Remove-Item Env:THEORC_KVCACHE_DIAGNOSTICS -ErrorAction SilentlyContinue
$code
```

### Full sequential gates

After the one-question replay succeeds, rerun the same command for Q8 without `-MaxQuestions`. Then run Q4 with:

```powershell
-SkipBuild -Model "Qwen3.5-9B-Q4_K_M"
```

Use a distinct timestamped log for every run.

Interpret benchmark exit codes according to the script:

- `0`: completed GO
- `2`: completed NO-GO, not necessarily an infrastructure crash
- other nonzero: investigate as an execution/infrastructure failure

For the bug-specific gate, require:

- all expected 120 answer artifacts completed for each full run;
- zero matches for `NoKvSlot`;
- zero matches for `failed to find a memory slot`;
- zero matches for `failed to prepare attention ubatches`;
- no new role corruption or lifecycle imbalance;
- benchmark quality criteria still evaluated independently of the crash fix.

Suggested targeted search:

```powershell
rg -n "NoKvSlot|failed to find a memory slot|failed to prepare attention ubatches" <new-log-path>
```

If either full run fails, retain the complete log, first affected artifact, exact prompt/completion telemetry, model/context metadata, and lifecycle diagnostics before changing code.

## After the live gate

If both full gates complete cleanly:

1. Update only the pending validation language and append the new evidence in the three CF docs.
2. Run the narrow relevant tests, then the full suite only if the code changed or shared infrastructure was touched.
3. Commit and push the validation/doc update to the existing branch.
4. Rerun Grok 4.5 on the new head.
5. Request/verify CodeRabbit review for the new head.
6. Mark the PR ready only with user approval.
7. Merge only with explicit user approval, then verify the merge commit on `origin/master`.

If only documentation changes after the gate, do not broaden the implementation or refactor adjacent Context Fabric code.

## Boundaries and cautions

- `master` is the source of truth.
- PR #63 is a review lane, not landed work.
- Do not merge, rebase, force-push, close, or delete branches without approval.
- Do not create the next Context Fabric phase PR before this one is merged and verified.
- Do not alter large Context Fabric fixtures casually.
- Preserve deterministic hashes, document/segment identity, provenance, citations, and quote ranges.
- Preserve the pre-existing untracked logs and training artifacts in this worktree.
- Avoid old PR and stale CodeRabbit archaeology; assess only current PR #63 findings against current code.
- Do not mislabel the issue as VRAM fragmentation, a recurrent `SeqMax` fault, or a disposal leak unless new runtime evidence directly contradicts the existing forensics.

## Concise handoff conclusion

The code fix is implemented, reviewed, pushed, and unit/build/CI validated. It changes admission from a Qwen-inaccurate heuristic to exact native tokenization of the fully rendered request, preserves MultiHop reserve semantics, clamps completion to the actual context, and retains safe fallback behavior for unloaded/non-native runtimes. The historical evidence supports a single oversized B3 Reviewer request exhausting attention KV context, not cumulative runtime damage.

PR #63 remains an open draft and is not merged. Claude's next useful action is to verify live state, obtain explicit approval for access to the two GGUF files and GPU time, run the narrow Q8 replay, and then complete the sequential Q8/Q4 full gates. No merge should occur until those results are documented and the user explicitly approves it.
