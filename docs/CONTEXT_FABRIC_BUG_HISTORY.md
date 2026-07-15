# Context Fabric CF-7 Bug History

**This is a historical, chronological record — not current-state documentation.**
It exists so that a future investigation doesn't waste time re-discovering a
disproved hypothesis or re-litigating a fixed bug. If you want to know how the
harness currently grades an answer, see
[CONTEXT_FABRIC_GRADING_SPEC.md](CONTEXT_FABRIC_GRADING_SPEC.md). If you're
chasing a machine-specific crash or checking model compatibility, see
[CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md](CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md).

Section numbers below (`§7`, `§7a`, `§7b`) are preserved from this content's
original location in `CONTEXT_FABRIC_TEST_HARNESS.md` (retired 2026-07-15,
split into this document plus the two above) because existing code comments
and commit messages cite them directly.

## §6. Change history relevant to grading correctness

| Date | Commit / PR | What changed |
|---|---|---|
| 2026-07-03 | `01f3fd09` (PR #34) | JSON recovery pipeline (keyword-suffix sanitizer), `ModelAdmissionGate` 3B floor |
| 2026-07-04 00:21 | `c55e5058` (PR #34) | B2 `BuildTopKText` rewrite: IDF-weighted, budget-fill, no fixed `Take(4)` |
| 2026-07-04 01:23 | `c68e01cf` (PR #34) | B3 `BuildEvidencePack` fix: same IDF-weighted/budget-fill approach, removes the 1/2/4 `maxCards` cap — **the diagnosed root cause of a 56/120 NO-GO**. Before this fix, `BuildEvidencePack` hard-capped at 1/2/4 cards by question kind with no documented cost/latency justification. Global-synthesis questions need evidence from up to 8 segments — capped at 4, the method was *structurally* incapable of answering them correctly regardless of ranking quality. Comparing `ExpectedSegmentIds` against `IncludedSegmentIds` on failing questions showed CF frequently never gathered the segment containing the answer — an evidence-*selection* bug, not a reasoning failure. |
| 2026-07-04 01:54 | `3ef5fb0b` (PR #34) | `BuildExhaustiveAnswer` entity-scoped vs. category-wide term filtering — fixes all 12 Exhaustive-category failures from the NO-GO run. Before this fix, the answer over-included claims from unrelated categories because the old filter accepted a claim if it shared *any* word with the question, and corpus-idiomatic filler words ("ledger", "recorded") appear in nearly every claim across all 15 ledgers. This went through two earlier failed attempts: a pure IDF aggregate score couldn't discriminate between two equally-rare ledger IDs; hard-requiring the single rarest term broke a case where *every* segment is relevant. |
| 2026-07-04 02:00 | `40d79e1b` (PR #34) | Grok adversarial review of the Exhaustive fix: fixed a segment-lookup crash risk, documented the heuristic's known residual risk (now in `CONTEXT_FABRIC_GRADING_SPEC.md` §5.3) |
| 2026-07-04 04:25 | PR #37 | Unescaped-inner-quote JSON sanitizer + key-vs-value colon handling, composed with the keyword-suffix sanitizer |
| 2026-07-04 04:46 | PR #38 | PowerShell 5.1 compatibility fixes in `Run-CF7GateExpanded.ps1` (re-run tooling only, not scoring logic) |
| 2026-07-04 | Grok review, `.orc/reviews/grok_20260703_223402.md` | Independent review of the full fix set above (36 files, 5,099 insertions), focused specifically on false-failure/false-pass risk in the scoring/parsing paths. Verdict: **CLEAN**, no findings. |

The 120-question NO-GO run on record (2026-07-04T00:36:28Z, B3 56/120) predates
the `BuildEvidencePack` and `BuildExhaustiveAnswer` fixes — it measured the
old, known-buggy evidence selection.

## §7. Open bug (resolved 2026-07-06, see §7a): KV-cache exhaustion invalidates most of a full 120-question run

**Status as of 2026-07-04: unresolved, high priority.** This reproduced on
NEWCOREPC, the machine with the most headroom, and it likely invalidated most
B0/B3 results from any full run since the `BuildEvidencePack`/`BuildTopKText`
fixes landed.

The 2026-07-04 02:44:29-elapsed full 120-question run on NEWCOREPC
(Gemma-4-12B, 8192 context) reported B3 at 12/120 — *worse* than the pre-fix
NO-GO's 56/120. Inspecting the raw result JSON showed why: 216 of B3's 223
failed question-attempts had `verification.errors: ["Native inference failed
while draining a prompt batch: NoKvSlot."]` — a native KV-cache exhaustion,
not a wrong answer. Only 7 failures were genuine (6 "reducer output references
claims outside its children", 1 unterminated-JSON) — these are the
`ContextFabricFeasibilityRunner.cs:515` reducer-validation gate correctly
catching Gemma-4-12B inventing a claim ID not present in its supplied
children, which the reducer prompt explicitly forbids. That's the harness's
honesty check working as designed, not a harness bug — a real, if small,
model hallucination rate worth tracking separately from the infrastructure
noise, but not something to "fix" in the scoring logic. B0 (closed-book) then
failed near-identically once B3 had already burned through the shared KV pool.
**The 12/120 and B0's failure were not meaningful capability measurements** —
they were an infrastructure crash wearing a NO-GO costume.

Root cause, traced through the code: `AdapterManager.cs` already documented
and guarded against a *related* but distinct problem — llama.cpp's KV-cache
sequence IDs are minted monotonically and never recycled, even after a
`Conversation` is `Dispose()`d (see `AdapterManager.cs:48-56`, referencing a
prior crash "at exactly the 257th reader conversation"). The existing fix,
`SequenceRecycleThreshold = 128` (rebuild the role's executor — a fresh native
context — every 128 minted conversations, at an idle point) and
`SequenceHardLimit = 240` (as of 2026-07-04; lowered to 40 on 2026-07-14, see
§7b) (fail closed with a managed exception rather than let the native assert
kill the process), protected against exhausting the *count* of sequence IDs.
**It did not protect against exhausting actual KV-cache memory, which is a
function of prompt size × live-but-unrecycled sequences, not conversation
count.** Since `BuildEvidencePack`'s fix removed the `maxCards` cap, a single
LocalFact question observed in this run's JSON pulled in **26 segments**
(6,309 prompt tokens) where the old, capped code would have used 1-4 cards —
meaning each conversation now reserved far more of the shared KV pool before
being abandoned.

**Stopgap attempted 2026-07-04, empirically DID NOT WORK.**
`SequenceRecycleThreshold` was lowered from 128 to 24 on the theory that
conversation *count* was the gating factor. A second full 120-question run
with this change produced a **byte-for-byte identical** question-pass/fail
trace to the pre-fix run — same 33 failures, same 12 successes, same 75
failures after, in the exact same positions. A 5x lower threshold changing
literally nothing about the outcome meant conversation-count-based recycling
was never the actual mechanism in play.

Hypothesis considered: `AdapterManager.GetOrCreateConversationAsync`'s
recycle-eligible branch only runs when `existing.ActiveCount == 0` — if
`ActiveCount` were stuck above zero, recycling would never trigger regardless
of threshold. An opt-in diagnostic (`THEORC_KVCACHE_DIAGNOSTICS=1`, printing
one line per recycle-eligibility check to **stdout**, not stderr — PowerShell
treats native stderr output as a terminating `NativeCommandError` under
`Run-CF7GateExpanded.ps1`'s `2>&1 | Tee-Object`, which killed a run on first
use before this was caught) was added to test it directly.

**Result:** `ActiveCount` was 0 on every single check across hundreds of
checks, and recycling fired correctly at every threshold crossing — yet
`NoKvSlot` still occurred. **This ruled out the stuck-`ActiveCount` hypothesis
entirely.** The recycle mechanism worked exactly as designed; the actual root
cause was something recycling doesn't address at all.

### §7a. Resolution (2026-07-06): root-caused as a Gemma-4-specific upstream limitation, not a bug in our code

Three further fixes landed on `fix/nokvslot-cache-exhaustion` (PR #40) and
were each validated (or disproven) against real 100+-question runs on real
hardware:

1. `LLamaSharpRuntime.cs`: margin-adjusted the two unmargined token-budget
   gates in `ContextFabricFeasibilityRunner.cs` and `FabricBoundaryStitcher.cs`
   (Grok adversarial review finding) — a real, still-valid fix, independent
   of everything below.
2. `AdapterManager.cs`: added `RoleEntry.ForceRecycle`, set by a new
   `AdapterManager.MarkForRecycle` as soon as `IRoleRuntime` observes *any*
   `NoKvSlot` — the next conversation request for that role tears down and
   rebuilds its executor instead of waiting for `SequenceRecycleThreshold`.
   **Empirically disproved as the fix**: a 100-question run showed the
   Reviewer role force-recycling after *every single* conversation, yet the
   very first conversation on the resulting fresh, empty pool still hit
   `NoKvSlot` every time. This conclusively ruled out cumulative
   cross-conversation exhaustion as the mechanism — the fix is harmless and
   kept as defense-in-depth, but it was never the cause here.
3. `SwaFull = false` (originally landed, then reverted): the theory was that
   `swa_full=true`'s native default forces SWA layers to reserve full-context
   KV cache instead of a window-sized one — 6x more than Gemma-3's
   architecture needs. **Empirically disproved via direct A/B test**: two
   100-question runs, identical except `SwaFull` true vs. false, produced
   **byte-for-byte identical** results (same 12/100 pass count, same 51/51
   citations, same 704 `NoKvSlot` occurrences) — impossible unless `SwaFull`
   has zero effect, given `temperature=0` determinism. Confirmed directly
   from llama.cpp's own native log (`llama_kv_cache_iswa: creating SWA KV
   cache, size = 8192 cells` — full-size, not window-sized). Reverted to the
   native default.

**What actually distinguished the failure, proven by direct comparison at
100+-question scale:** it was specific to **Gemma-4-12B**, not a general
Context Fabric or LLamaSharp integration bug. `qwen2.5-coder-7b-instruct`
(100 questions, HARDCOREPC) and `Meta-Llama-3.1-8B-Instruct` (30 and 100
questions, NEWCOREPC) both ran their full B0-B4 pipelines with **zero**
`NoKvSlot` occurrences — `Meta-Llama-3.1-8B` in particular reached 99.1%
citation precision (113/114) with zero crashes, proving Context Fabric's own
mechanics work well once a compatible model is used.

**Root cause: an upstream `llama.cpp`/Gemma-4 limitation, not our code.**
Gemma-4 uses a ["shared KV cache" architecture](https://huggingface.co/blog/gemma4#shared-kv-cache)
where some layers reuse another layer's K/V tensors instead of computing their
own — a departure from the "every layer independently owns its KV state"
assumption baked into much of llama.cpp's generic cache-management code. Two
upstream issues document exactly this class of breakage:
[ggml-org/llama.cpp#21468](https://github.com/ggml-org/llama.cpp/issues/21468)
("cache reuse is not supported for Gemma 4 models despite `--swa-full`") and
[#23720](https://github.com/ggml-org/llama.cpp/issues/23720) ("Backend crash
due to fragmented unified KV cache", Gemma-4 MoE + `kv-unified`). The relevant
fix, [ggml-org/llama.cpp#23981](https://github.com/ggml-org/llama.cpp/pull/23981)
("kv-cache: SWA checkpoints store only non-masked cells"), merged
**2026-06-02** — after the llama.cpp commit pinned by LLamaSharp 0.27.0
(2026-03-13) *and* after the commit pinned by LLamaSharp's own unreleased
`master` as of that writing (2026-05-23). See
`CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md` for the current model-compatibility
guidance this produced.

**Separately, a real but unrelated bug found on `Meta-Llama-3.1-8B`:**
`boundary_stitch_pass_rate` failed 0/2 in both the 30- and 100-question runs.
Initially misdiagnosed as a token-budget truncation (fixed by bumping the
stitch prompt's `ReaderMaxTokens` — a reasonable improvement on its own, but
confirmed **not** the actual cause: `completionTokens` on the failing call
was 276, nowhere near either the 1024 or 2048 token budget). The real cause is
that `ContextFabricValidation.FabricJson.ParseModelObject`'s error message
truncates its *displayed* preview to 200 characters, which looks like
generation truncation but isn't — the full extracted JSON fails
deserialization to `FabricBoundaryStitchDraft` for a schema reason not yet
identified. **Not yet fixed** — tracked here as a known, low-priority,
separate gap.

### §7b. Second architecture-specific failure mode (2026-07-14): recurrent/hybrid models (Qwen3.5) need explicit `SeqMax`

Distinct from §7a's Gemma-4 shared-KV-cache issue. `Qwen3.5-9B-Q8_0` (a hybrid
attention + Gated Delta Net / recurrent architecture) hit `NoKvSlot` on
**literally the second conversation ever minted** on any role's executor —
not a slow climb toward `SequenceRecycleThreshold`, 100% reproducible from the
first recycle. Native log showed `find_slot: seq_id=1 >= n_seq_max=1` and
`init_batch: failed to prepare recurrent ubatches`.

Root cause: `LLamaSharpRuntime.cs`'s `ModelParams` never set `SeqMax`, so it
defaulted to 1. `AdapterManager` mints a fresh, monotonically-increasing
sequence id per conversation and only tears the executor down at
`SequenceRecycleThreshold`/`SequenceHardLimit` — an assumption that happened
to hold for plain-transformer architectures (llama.cpp's unified KV-cache path
tolerates `seq_id >= n_seq_max` there) but recurrent/hybrid architectures
validate `seq_id` strictly against `n_seq_max`, so every executor's second
conversation failed outright regardless of load.

Fix: set `ModelParams.SeqMax = AdapterManager.SequenceHardLimit`. This alone
traded one bug for another — llama.cpp allocates a per-sequence recurrent-
state ("rs cache") buffer sized by `n_seq_max` on hybrid models, and the old
`SequenceHardLimit = 240` (calibrated when native `n_seq_max` was effectively
unlimited) became a live ~12GB VRAM reservation, OOM-crashing the native
process (`cudaMalloc failed`, exit `0xC0000005`) on a 16GB GPU. Lowered
`SequenceHardLimit` to 40 (~2GB rs-cache reservation, still well above
`SequenceRecycleThreshold = 24`). A follow-up finding (CodeRabbit review, PR
\#56): `StreamCompletionAsync`'s `StatelessExecutor` was reusing the same
`_modelParams` instance, forcing every single-sequence stateless call to
reserve the same ~2GB rs-cache budget for no benefit — split into a separate
`_statelessModelParams` instance with the native default `SeqMax=1`.

Verified live: zero `NoKvSlot`, zero OOM, across full 120-question runs on
both `Qwen3.5-9B-Q8_0` (2h35m) and `Qwen3.5-9B-Q4_K_M` (2h31m). See PR #56.
Both runs' actual benchmark scores were low (B3 16/120 and 1/120
respectively, both NO-GO). **This was reported at the time as "a real
capability finding about Qwen3.5-9B" — that conclusion turned out to be
wrong; see §7c below.** The infrastructure bug documented in this section
(§7b) is still real and still fixed; it was simply not the last
infrastructure problem blocking a valid Qwen3.5 measurement.

**Practical guidance:** any future `SequenceHardLimit` change must be checked
against rs-cache VRAM cost for the largest hybrid-architecture model in use,
not just against the native `LLAMA_MAX_SEQ` sequence-count cap.

### §7c. Third architecture-specific failure mode (2026-07-15): thinking-mode models need the reasoning-suppression seed applied manually

Distinct from both §7a (Gemma-4 shared-KV-cache) and §7b (recurrent-model
`SeqMax`). After §7b's fix, Qwen3.5-9B ran with zero crashes but still scored
essentially zero on B3 (16/120 Q8_0, 1/120 Q4_K_M) — both NO-GO. Inspecting
the raw `cf0_*.json` segment-read results showed why: **0/128 segments were
ever accepted**, on both quants, every single time. `segmentResults[].metrics.
rawOutputExcerpt` showed all 128 reader outputs starting with a `<think>`
block, mean completion length 2,054 tokens (103/128 at or near the reader's
~2000-token completion cap) — the model spent its entire completion budget
reasoning and the actual `FabricEvidenceCard` JSON was truncated or never
started. All 128 rejections were JSON-parse failures (68 "could not be
parsed", 59 "did not contain a JSON object", 1 unterminated).

**The 16/120 and 1/120 pass counts were not a capability measurement.** The
held-out set has exactly 16 Unanswerable questions; with zero segments ever
ingested, B3 had no evidence for anything and could only "pass" the questions
where abstaining is the correct answer — B3's score was, in effect, measuring
"how many of the 120 questions are Unanswerable," not model capability. This
is the same class of harness-bug-wearing-a-NO-GO-costume documented in §7
(and `ModelAdmissionGate` had already flagged this exact risk pre-emptively:
Qwen3.5 was admitted only as **Provisional** for Context Fabric roles, with
the stated reason "Reasoning-tuned models require a clean structured-output
benchmark pass for Context Fabric" and the explicit detail "Visible reasoning
traces can consume the response budget or precede the required JSON object"
(`ModelAdmissionGate.cs:234`) — a correct prediction of precisely what
happened, made before any Qwen3.5 CF-7 run was ever attempted).

Root cause: Qwen3.5's own GGUF chat template (`tokenizer.chat_template`)
implements the standard `enable_thinking` opt-out pattern — its trailing
`add_generation_prompt` block appends `<think>\n\n</think>\n\n` (a pre-closed,
empty reasoning block) whenever `enable_thinking` is not explicitly `true`,
which causes the model to skip straight to its real answer. `LLamaSharpRuntime.
ApplyEmbeddedTemplate` uses LLamaSharp's `LLamaTemplate` — a minimal Jinja
subset, not a full interpreter — which renders the template through
`<|im_start|>assistant\n` without throwing (confirmed via
`segmentResults[].metrics.promptPath == "EmbeddedTemplate"`, not a ChatML
fallback) but does not evaluate that trailing `enable_thinking is defined`
conditional at all. With no seed present, the model free-generates its
trained default: full reasoning mode.

Fix: added `LLamaSharpRuntime.SupportsThinkingSuppression` (checks the raw
`tokenizer.chat_template` metadata string, exposed via `LLamaWeights.Metadata`,
for **both** the `enable_thinking` marker **and** the literal
`<think>\n\n</think>\n\n` empty-seed text — requiring both, not just the
variable name, avoids a false-positive match on some other template that
happens to reference `enable_thinking` for unrelated semantics or a
differently-shaped seed) and `ApplyThinkingSuppression` (appends that same
empty block the official template would have rendered, with its own
tail-only idempotency guard). Detected generically, not by model
family/filename, so this covers any future reasoning model using the same
pattern. Both are pure static functions with direct unit test coverage
(`LLamaSharpRuntimeThinkingSuppressionTests.cs`), applied once per rendered
prompt in `ApplyEmbeddedTemplate`, cached per loaded model.

Verified live on `Qwen3.5-9B-Q8_0` (3-question smoke test): segment acceptance
0/128 → **123/128**, `<think>`-prefixed outputs 128/128 → **0/128**, mean
completion tokens 2,054 → **294** (a ~7x reduction — the fix also
substantially speeds up every CF-7 run against a thinking-mode model, since
none of the completion budget is wasted on reasoning). The 5 remaining
rejections on that run were a distinct, minor issue ("claims must contain at
least 1 item") unrelated to thinking-mode. See PR #58 for the fix; a
full-scale 120-question re-score under it had not yet been run as of this
writing (see `CONTEXT_FABRIC_INFRASTRUCTURE_NOTES.md`'s compatibility table
for current status).

**Practical guidance:** any newly-admitted model should be checked for a
`enable_thinking`-style opt-out in its own `tokenizer.chat_template` before
its CF-7 numbers are trusted — a 0/128 (or near-0) segment-acceptance rate
alongside `<think>`-prefixed raw outputs is the signature of this exact
failure mode, not a model-capability result.

### §7d. Exhaustive-category heuristic hardening (2026-07-15, Remediation Phase 3)

Distinct from §7a/§7b/§7c (all runtime/architecture crash modes) — this is a
grading-logic gap, not an infrastructure bug. The Grading Spec §5.3 has
flagged since 2026-07-04 that `BuildExhaustiveAnswer`'s entity-scoped vs.
category-wide classification (§6's `3ef5fb0b` fix, above) is a heuristic, not
a proof, and could misclassify a genuinely category-wide question as
entity-scoped if its content terms happen to have <50% document frequency by
corpus coincidence. That risk had never been reproduced — "this boundary case
does not [have a regression test]" per the Grading Spec's own known-limitations
note.

**Investigation finding, corrected after Grok review: the fallback heuristic
IS live, but the specific failure mode is not (yet) triggered by any real
question.** The 150-question expanded-corpus suite's 15 Exhaustive questions
all name a hyphenated identifier (`case-ledger-NN`), routing through Tier
1c's verbatim anchor match (`CF_RETRIEVAL_IMPROVEMENT_PLAN.md` §1c) — those
15 never reach the fallback heuristic. But the smaller, frozen 16-segment
`DeterministicFabricCorpus` fixture (used by `cf7-gate`, not
`cf7-gate-expanded`) has its own Exhaustive question,
`exhaustive-archive-tokens` ("List every archive token in section order.") —
no hyphenated identifier, so it genuinely exercises the fallback heuristic on
every `cf7-gate` run. It currently classifies correctly (category-wide, as
intended) only because every segment's text literally contains the phrase
"archive token" (`DeterministicFabricCorpus.cs:251`), giving "token" 100%
document frequency in that specific corpus — nowhere near the <50% threshold
that triggers misclassification. **The heuristic is live and currently
correct on this question by fortunate corpus construction, not because the
heuristic is safe in general** — the boundary-case risk (a term with <50%
document frequency by coincidence) remains real and untriggered by any
current question, which is exactly what the new regression test below
reproduces synthetically rather than waiting for it to happen on a real
corpus.

**Fixes landed:**
1. A regression test
   (`BuildExhaustiveAnswer_HeuristicMisclassifiesCategoryWideQuestion_KnownBoundaryCase`)
   reproducing the exact failure with a synthetic 10-card corpus: a
   genuinely category-wide "archive token" question where only 3 of 10
   relevant cards happen to use the literal word "token" (the other 7 use
   "marker"/"designation"), causing the heuristic to hard-require "token"
   and silently drop the 7 differently-phrased-but-equally-relevant cards.
   This closes the "boundary case does not [have a test]" gap directly.
2. `FabricBenchmarkQuestion.ExhaustiveIsEntityScopedOverride` (nullable
   `bool`): an optional, authored ground-truth annotation. When set,
   `BuildExhaustiveAnswer` uses it directly instead of inferring via the
   heuristic — the "pre-compute ground-truth classification... instead of
   inferring it at grading time" resolution the Grading Spec had tracked as
   not-yet-implemented. Proven by a second test
   (`BuildExhaustiveAnswer_OverrideFixesTheBoundaryCase`) using the
   identical fixture with the override set, confirming all 10 cards match.
3. **Deliberately not backfilled onto any existing question**: the 15
   expanded-suite Exhaustive questions are already correctly handled by Tier
   1c, and `exhaustive-archive-tokens` is already correctly classified by the
   fallback heuristic (100% document frequency on "token" in its specific
   frozen corpus) — setting the override on any of them would be a no-op
   that adds authoring surface without fixing anything live. The override is
   documented (Grading Spec §5.3) as something future question authors
   should set explicitly for any new Exhaustive question whose category-wide
   term might have lower, coincidental document frequency, not retrofitted
   onto questions that don't need it.
