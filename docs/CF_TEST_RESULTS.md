# Context Fabric (CF-7) Test Results Log

Tracks every real CF-7 gate run performed during the NoKvSlot investigation: what was
tested, on which machine, with which model, what bugs were found along the way, and what
each run actually showed. For the full NoKvSlot root-cause investigation this file
summarizes into a results table, see
[CONTEXT_FABRIC_BUG_HISTORY.md](CONTEXT_FABRIC_BUG_HISTORY.md) §7/§7a. For how the harness
itself grades answers (independent of any run's result), see
[CONTEXT_FABRIC_GRADING_SPEC.md](CONTEXT_FABRIC_GRADING_SPEC.md).

---

## 1. Method

Each run uses `Tools/ContextFabricBench/Run-CF7GateExpanded.ps1` (wrapped by an ad-hoc
`run-cf7-smoke.ps1` helper during this investigation), which builds and runs
`context-fabric-bench.exe --suite cf7-gate-expanded` against the fixed 128-segment
expanded corpus and a held-out question suite, at `--context 8192`. `--max-questions N`
caps how many held-out questions run per system:

- **B0** — closed-book (no evidence, tests the model's own memorized knowledge)
- **B1** — truncated-prompt baseline
- **B2** — conventional top-k RAG baseline
- **B3** — single-node Context Fabric (the actual feature under test)
- **B4** — HIVE Context Fabric (frozen CF-6 acceptance artifact, not re-run each time)

Key metrics: `segment_terminal_coverage` (reader-stage evidence-card acceptance rate),
`question_pass_rate` (B3's own accuracy), `citation_precision` (fraction of citations that
verify against real source text), `max_prompt_tokens`, `boundary_stitch_pass_rate`.

`THEORC_KVCACHE_DIAGNOSTICS=1` (baked into the helper script) enables two opt-in
diagnostics used throughout: `AdapterManager`'s per-conversation recycle-eligibility log
(`[KvCacheDiag]`) and, later, a native `llama.cpp` log sink (`[NativeLog]`).

Which model actually gets used is decided by `ModelDepot`/`ModelAdmissionGate` from
whatever `.gguf` files are present in `%APPDATA%/OrchestratorIDE/Models`
(`.disabled`/`.setaside` suffixes are used throughout this investigation to force a
specific candidate).

## 2. Machines

| Machine | GPU | VRAM | Notes |
|---|---|---|---|
| NEWCOREPC | RTX (16GB-class) | 16,303 MiB | Local machine this session ran from |
| HARDCOREPC | RTX 3050 | 6,144 MiB | Tightest VRAM budget of the three |
| HARDCORELAPTOPMSI | RTX 4060 Laptop | 8,188 MiB (WMI misreports as 4,095 MiB) | Two local Windows accounts exist (`hardc` interactive/RDP vs `hardcoreerik` SSH-only) with separate model folders — a real gotcha, see §4 |

## 3. Bugs found and fixed (chronological)

All on branch `fix/nokvslot-cache-exhaustion` (PR #40), each rebuilt and re-tested before
moving to the next, per commit:

| Commit | Fix | Status |
|---|---|---|
| `5ef165a1` | Margin-adjusted two unmargined token-budget gates (`ContextFabricFeasibilityRunner.cs`); made `NoKvSlot` retryable instead of instant-fatal (`IRoleRuntime.cs`); disabled `SwaFull` (later reverted, see below) | Margin fix and retry logic: kept. `SwaFull=false`: reverted (see below) |
| `4542e1df` | Addressed Grok adversarial review: same margin fix applied to `FabricBoundaryStitcher.cs`; fixed an off-by-one in the retry give-up message; added retry-attempt diagnostic logging | Kept |
| `3fb2781f` | Fixed `Run-CF7GateExpanded.ps1` crashing (`Set-StrictMode` + `.Count` on a non-array) when the model folder has **exactly one** `.gguf` file | Kept |
| `28f881fc` | Added `AdapterManager.ForceRecycle`: recycle a role's executor immediately on any `NoKvSlot`, instead of waiting for the conversation-count threshold | Kept (correct, harmless defense-in-depth) — **empirically disproven as the fix for Gemma-4**: forced recycling to a fresh, empty pool before every single conversation still didn't stop `NoKvSlot` |
| `d2d5219c` | Reverted `SwaFull=false` back to the native default (`true`) | **Empirically disproven as the fix too**: two 100-question runs, identical except this setting, produced byte-for-byte identical results (`temperature=0` determinism) — confirmed via native log that full 8192-cell caches were allocated either way |
| `6e1f1a86` | Documented the actual root cause (§7a of the harness doc); downgraded Gemma-4-class models from `Admitted` to `Provisional` in `ModelAdmissionGate`; added an opt-in native `llama.cpp` log sink (reuses `THEORC_KVCACHE_DIAGNOSTICS`); margin-bumped the boundary-stitch prompt's token budget (reasonable improvement, confirmed **not** the fix for the separate boundary-stitch bug, see §5) | Kept |

**Root cause, confirmed:** Gemma-4-class models use a ["shared KV cache"
architecture](https://huggingface.co/blog/gemma4#shared-kv-cache) (some layers reuse
another layer's K/V tensors instead of computing their own) that breaks assumptions in
`llama.cpp`'s generic cache-management code. Two upstream issues document the same class
of failure: [ggml-org/llama.cpp#21468](https://github.com/ggml-org/llama.cpp/issues/21468),
[#23720](https://github.com/ggml-org/llama.cpp/issues/23720). The fix
([#23981](https://github.com/ggml-org/llama.cpp/pull/23981)) merged **2026-06-02**, after
the `llama.cpp` commit pinned by LLamaSharp 0.27.0 (2026-03-13, the latest official
release) *and* after the commit pinned by LLamaSharp's own unreleased `master` branch as
of this writing (2026-05-23). **Not fixable in our code; no available LLamaSharp version
has the fix yet.**

## 4. Other bugs / gotchas found along the way (not code fixes, but cost real time)

- **SSH-detached processes on Windows don't survive the SSH session ending.** `Start-Process`
  via an SSH-invoked PowerShell gets killed (or orphaned silently) when the parent SSH
  session tears down — sometimes the wrapping shell dies but the actual `context-fabric-
  bench.exe` child survives as an untracked orphan, consuming VRAM invisibly and starving a
  legitimate concurrent run on the same machine (observed on HARDCOREPC, PID `2948`,
  running unmonitored for ~50 minutes). Scheduled Tasks would also work but require
  standing authorization; the reliable fix used here was having the user run the benchmark
  manually inside their own interactive RDP session instead of over SSH.
- **Two separate Windows user profiles on the laptop.** SSH login account `hardcoreerik`
  and the interactively-logged-in RDP account `hardc` have entirely separate
  `%APPDATA%/OrchestratorIDE/Models` folders. A model copied to one is invisible to a
  benchmark run under the other — cost a full wasted run before being caught.
- **`mmproj-*.gguf` files get misidentified as standalone candidate models.** While testing
  model selection by disabling all other `.gguf` files, the depot picked
  `mmproj-gemma-4-12B-it-bf16.gguf` (a multimodal projector shard, not a real
  language model) as the "Researcher"/"Reviewer" model, producing a 3-second instant-fail
  run. Worked around by also disabling all `mmproj-*.gguf` files during model-swap testing;
  not fixed in `ModelAdmissionGate` itself.
- **`FabricJson.ParseModelObject`'s error message is a red herring for length.** The
  `"Extracted: {json[..200]}"` preview in its `JsonException` message looks like generation
  truncation but is just a display cap — cost real time chasing a `ReaderMaxTokens` fix that
  turned out not to be the cause (see §5).

## 5. Open, not yet fixed

**Boundary-stitch schema mismatch on `Meta-Llama-3.1-8B`.** `boundary_stitch_pass_rate`
failed 0/2 in both the 30- and 100-question NEWCOREPC runs. One case
(`cross-clause-result`) fails JSON deserialization to `FabricBoundaryStitchDraft` for a
reason not yet identified — confirmed **not** a token-budget/truncation issue
(`completionTokens` was 276, nowhere near the 1024/2048 budget tried). The other
(`cross-pronoun-reference`) is a genuine model comprehension gap (missing an expected
linked fact), not a bug. Needs a fresh instrumented run capturing the *full* raw model
output (only a short excerpt is persisted today) to pin down the schema issue precisely.

## 6. Results by machine

### NEWCOREPC

| # | Model | Questions | Code state | `question_pass_rate` | `segment_terminal_coverage` | `citation_precision` | `NoKvSlot` count | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | Gemma-4-12B | 100 | pre-fix (unmargined gates, no retry) | 12/100 | eventually 128/128 | 51/51 (100%) | 216/223 attempts | Original bug discovery — near-total infra-crash, not a real capability measurement |
| 2 | Gemma-4-12B | 100 | + force-recycle fix (`28f881fc`) | 12/100 | 128/128 | 51/51 | 704 | Byte-identical to #1 — disproves force-recycle as the fix |
| 3 | Gemma-4-12B | 100 | + `SwaFull` reverted (`d2d5219c`) | 12/100 | 128/128 | 51/51 | 704 | Byte-identical to #2 — disproves the SWA-cache-sizing theory |
| 4 | Gemma-4-12B | 1 | + native log sink, minimal repro | fail (1Q) | n/a | n/a | 1 (on first-ever fresh conversation) | Native log confirmed full 8192-cell caches genuinely allocated for both SWA and non-SWA layers; no extra native diagnostic text on the `NoKvSlot` itself |
| 5 | Meta-Llama-3.1-8B-Instruct | 30 | current | 12/30 (40%) | 105/128 (82%) | 30/30 (100%) | **0** | First non-Gemma data point — proves the crash is Gemma-specific |
| 6 | Meta-Llama-3.1-8B-Instruct | 100 | current | 31/100 (31%) | 105/128 (82%) | 113/114 (99.1%) | **0** | Confirms #5 at full scale; `boundary_stitch_pass_rate` 0/2 (see §5). Failure categorization: **100% retrieval misses** (49 total-miss + 20 partial, 0 model failures) — the finding behind CF_RETRIEVAL_IMPROVEMENT_PLAN.md |
| 7 | Meta-Llama-3.1-8B-Instruct | 100 | Tier 1 retrieval fix (`5fd6ad30`) | **45/100 (45%)** | 105/128 (82%) | 116/117 (99.1%) | **0** | **Tier 1 validation: B3 beats the B2 RAG baseline (44/100) for the first time.** Failure mix shifted from 69 all-retrieval to 55: 21 pure retrieval + 24 partial + **10 genuine model failures (first ever measured)**. Biggest remaining pure-miss bucket is Paraphrased questions with INVERTED entity word order ("the Meridian relay point" vs corpus "Relay Meridian") — contiguous phrase anchors can't match, exactly the paraphrase gap the plan defers to Tier 2 (or an unordered-proximity anchor variant). Exhaustive partials (10) persist despite Tier 1c. `boundary_stitch` still 0/2 (known separate bug) |
| 8 | Meta-Llama-3.1-8B-Instruct | 100 | Tier 1.5 proximity pairs (`6d280717`) | **56/100 (56%)** | 105/128 (82%) | 110/111 (99.1%) | **0** | **Tier 1.5 validation: focus metric nailed — Paraphrased pure-misses 13 → 3.** Failure mix: 10 pure retrieval (LocalFact 6, Paraphrased 3, MultiHop 1) + 24 partial (unchanged: MultiHop 9, Contradiction 5, Exhaustive 10) + 10 model (unchanged). Cumulative across three deterministic lexical fixes: pass rate 31 → 45 → 56, pure retrieval misses 49 → 21 → 10. The partial-miss bucket (24, multi-segment questions) is now the largest and was untouched by 1.5 — next target |
| 9 | Meta-Llama-3.1-8B-Instruct | **120 (full held-out set)** | merged master (`e6f8c7b7`: Tiers 1+1.5+2) | **58/120 (48.3%)** | 109/128 (85.2%) | 179/200 (89.5%) | **0** | **First complete 120-question run.** B3 (58) beats B2 RAG (46) by 12 questions on the full set. Citation precision 89.5% misses the 0.9 gate by one citation-equivalent. Ran in 36:52 against a dedicated `Models-CF7` root (hardlinked Meta-Llama) after shared-depot renames caused two exit-66 false starts (gate `cf7_gate_20260708_062430`). `boundary_stitch` still 0/2 (known Llama-specific bug, §5) |
| 10 | Meta-Llama-3.1-8B-Instruct | 120 | Tier 2.5 chase + stitch fix, **chase bug still present** (`12c91d82`) | 58/120 (48.3%, unchanged) | 109/128 (85.2%, unchanged) | **180/196 (91.8%) — clears the 0.9 gate** | **0** | **Boundary-stitch and citation-precision gates now PASS** (see §5 closure). But MultiHop stayed 0/24, byte-identical retrieval distribution to run #9 — proved the reference chase was a complete no-op. Root cause found immediately after: `ChaseTrackedReferences` scanned `CardHaystack` (Summary + Claims.Text, the reader model's own paraphrase), but tracked identifiers like `RPT-064` only survive verbatim in `FabricCitation.Quote`, which that haystack never included. Fixed in `7d9e3b85` (dedicated `ChaseHaystack` including citation quotes) — re-validation below |
| 11 | Meta-Llama-3.1-8B-Instruct | 120 | Tier 2.5 chase-haystack fix, **still 58/120** (`7d9e3b85`) | 58/120 (48.3%, unchanged again) | 109/128 (85.2%, unchanged) | 180/196 (91.8%, stable) | **0** | Same score a third time, but this run's failures were individually inspected against the live evidence cards rather than assumed: **all 6 fully-retrieved 2-hop MultiHop failures are answer-citation-discipline gaps, not retrieval gaps** — both target segments were in `includedSegmentIds` every time (e.g. `multihop-chain-2h-001`: expected `[xseg-0043,xseg-0044]`, both included), but the answer cited only 1 of the 2 required segments despite the explicit prompt instruction. The 18 partial-retrieval misses are mostly 3-5-hop `chain-lh-*` questions (e.g. 1/5, 2/4 hits) — root-caused to `ChaseDocFrequencyCap=4` excluding legitimate 4-5-hop chains as "filler" (a real N-hop chain's shared token appears in exactly N segments). Cap raised to 6 in `0f8338ab`; re-validation below. The 2-hop under-citation is logged as a **known model-capability limit**, not a code defect — an 8B model reliably complying with "cite from every contributing card" is a harder ask than the retrieval-side fixes address |
| 12 | Meta-Llama-3.1-8B-Instruct | 120 | Tier 2.5 doc-frequency-cap fix, **still 58/120, fourth run** (`0f8338ab`) | 58/120 (48.3%, unchanged a fourth time) | 109/128 (85.2%, unchanged) | 180/196 (91.8%, stable) | **0** | **Root cause is now fully diagnosed, not just narrowed.** Directly measured `multihop-chain-lh-002` (worst case, 1/5 hit): its chain token `CHN-201` has document frequency exactly 4 (`xseg-0108/0109/0110/0112`, one segment's read failed) — well within the raised cap of 6, and 3 of those 4 cards carry the token verbatim in their citation quotes (confirmed by direct inspection). The chase's own logic is correct; **the real problem is budget ordering**: `BuildEvidencePack`'s greedy anchor-fill runs to completion BEFORE the chase gets a turn, and this corpus deliberately reuses entity names across unrelated facts as distractors — the question names "Cache Alpha" and "Vessel Meridian" (real chain hops), but the greedy fill matched those same names in `xseg-0051`/`xseg-0053`, segments from two *entirely different, unrelated* 2-hop chains (`RPT-276`, `RPT-329`). Those wrong-context matches consumed most of the evidence budget (max_prompt_tokens 5689, EvidenceLimit ≈6528 — only ~800 tokens of headroom) before the chase could add the correct chain segments. **This needs an architectural fix (reserve budget for the chase, or run identifier-linking before the generic anchor fill), not another parameter tweak** — stopping blind iteration here after 4 flat-score cycles (~2.5h of compute) to scope it properly rather than burn a 5th 37-minute run on another guess |
| 13 | Meta-Llama-3.1-8B-Instruct | 120 | Tier 2.5 budget reservation, **58/120, but retrieval genuinely improved** (`a30229aa`) | 58/120 (48.3%, pass rate still flat) | 109/128 (85.2%, unchanged) | 147/163 (90.2%, still clears the 0.9 gate) | **0** | **The budget fix worked and is now conclusively separable from the remaining gap.** MultiHop full-retrieval count rose 6 → **9/24** (retrieval distribution changed for the first time across 5 cycles: `2/2` bucket 6→8, several partial buckets shifted) — direct evidence the greedy fill no longer starves the chase. But **all 9 fully-retrieved cases still fail on the exact same citation-discipline gap**: every 2-hop case attaches only 1 of the 2 required citations, one 4-hop case attaches 0. **Conclusion: the retrieval side of Tier 2.5 is done and working as designed; the remaining MultiHop gap is a model-instruction-compliance ceiling** (an 8B model not reliably producing N citations for an N-part chain despite an explicit system-prompt requirement), not a retrieval or code defect. Closing this investigation thread here — CF_RETRIEVAL_IMPROVEMENT_PLAN.md §3c updated with the conclusion and next options (few-shot examples, schema-enforced citation count, or defer to a larger/Tier-3 agentic model) |
| 14 | qwen2.5-coder-7b-instruct-q5_k_m | **120 (full held-out set)** | merged master (`8efa7fd8`: Tiers 1+1.5+2+2.5, SeqMax fix, thinking-suppression fix — none of the last two apply to this non-reasoning, plain-transformer model) | 29/120 (24.2%) | 118/128 (92.2%) | 130/203 (64.0%) | **0** | **First 120-question run for this model, and the first on NEWCOREPC** (prior qwen2.5-coder data was 100-question runs on HARDCOREPC/HARDCORELAPTOPMSI, both pre-Tier-1/1.5/2.5). Zero infrastructure crashes — clean baseline. **B3 (29) does NOT beat B2's top-k RAG baseline (52)** — consistent with the pre-Tier-1 HARDCOREPC/HARDCORELAPTOPMSI findings that "the fabric does not yet beat RAG on this smaller model," now confirmed at full 120-question scale and after all the retrieval-quality tiers that later let Meta-Llama pull ahead of its own RAG baseline (run #7 above). Under the Remediation-Phase-2 `Graded capability` gate (PR #59, not yet in effect for this run's own report — evaluated by hand against the raw B0-B3 counts here) this would still be a NO-GO: B3 loses to B2, not just to the 100%-pass-rate stretch goal. `boundary_stitch_pass_rate` and `segment_terminal_coverage` also below target (118/128, 92.2%). `citation_precision` 64.0% is well under the 0.90 gate — a real, model-quality citation-discipline gap, not an infra artifact (gate `cf7_gate_20260715_163502`, ran 28:24 against a hardlinked `Models-CF7` copy after the shared depot's copy was found `.gguf.disabled`) |

### HARDCOREPC

| # | Model | Questions | Code state | `question_pass_rate` | `segment_terminal_coverage` | `citation_precision` | `NoKvSlot` count | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | qwen2.5-coder-7b-instruct-q5_k_m | 3 | early (pre-margin-fix) | 0/3 | n/a | n/a | 0 | Small-sample noise — see #2 |
| 2 | qwen2.5-coder-7b-instruct-q5_k_m | 100 | current | 25/100 (25%) | 121/128 (94.5%) | 64/84 (76.2%) | **0** | Real, moderate citation-discipline gap — a model-quality question, unrelated to the infra bug |
| 3 | qwen2.5-coder-7b-instruct-q5_k_m | 100 | Tier 2 truncation fix (`9d71c5ad`) | 26/100 (26%) | 121/128 (94.5%) | 68/81 (84.0%) | **0** | Tier 2 validation, third machine: pass rate flat (25 → 26) but citation precision 76.2% → 84.0% — same direction as the laptop's 77.5% → 88.2%, confirming the truncation fix mainly buys citation discipline on qwen, not retrieval wins. `boundary_stitch` 2/2 PASS again on qwen. B2 RAG (49/100) still above B3 on this model (gate `cf7_gate_20260708`, ran overnight 9:09 PM → 1:13 AM) |

### HARDCORELAPTOPMSI

| # | Model | Questions | Code state | `question_pass_rate` | `segment_terminal_coverage` | `citation_precision` | `NoKvSlot` count | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | qwen2.5-coder-7b-instruct-q5_k_m | 3 | early | 0/3 | n/a | n/a | 0 | Small-sample noise |
| 2 | gemma-4-12B (wrong user profile) | 100 | pre-fix | n/a | 114/128 | n/a | some | Model file was in the `hardcoreerik` SSH profile, invisible to the `hardc` RDP session that actually ran the test — confounded result, not directly comparable |
| 3 | gemma-4-12B (correct `hardc` profile) | 100 | stale (`28f881f`, missing the doc/gate-flag commit) | 12/100 | ~127/128 | n/a | heavy, same pattern as NEWCOREPC | Confirms the same Gemma-4 failure on a third, independent machine/VRAM budget |
| 4 | qwen2.5-coder-7b-instruct-q5_k_m | 100 | current (`6e1f1a86`) | 26/100 (26%) | 114/128 (89.1%) | 62/80 (77.5%) | **0** | Consistent with HARDCOREPC's qwen result (#2 in that table). `boundary_stitch_pass_rate` 2/2 PASS — unlike Meta-Llama's 0/2, suggesting that bug is Llama-specific, not universal |
| 5 | qwen2.5-coder-7b-instruct-q5_k_m | 100 | Tier 2 truncation fix (`9d71c5ad`) | **30/100 (30%)** | 114/128 (89.1%) | 67/76 (88.2%) | **0** | **Tier 2 validation on a second model family**: pass rate 26 → 30, citation precision 77.5% → 88.2% (fewer, better-supported citations: 80 → 76 attempted). Coverage unchanged. `boundary_stitch` still 2/2 PASS. B2 RAG baseline 49/100 remains above B3 for qwen — unlike Meta-Llama on NEWCOREPC, the fabric does not yet beat RAG on this smaller model (gate `cf7_gate_20260708_041100_770`) |

## 7. Bottom line

- **Context Fabric's own mechanics work well** on non-Gemma-4 models: 99.1% citation
  precision on `Meta-Llama-3.1-8B` across 130 combined questions, zero infrastructure
  crashes on either `Meta-Llama-3.1-8B` or `qwen2.5-coder-7b` at 100-question scale.
- **`NoKvSlot` is a Gemma-4-specific, currently-unpatched upstream `llama.cpp` limitation**,
  not a bug in TheOrc's own code — proven via direct A/B testing that ruled out both of our
  own leading theories (cross-conversation exhaustion, SWA-cache sizing).
- **Remaining known gaps** are model-quality questions (citation discipline and
  segment-acceptance rate, consistent ~25-31% pass rates on both `qwen` and `Meta-Llama`
  across three independent machines), not infrastructure bugs — a fundamentally different,
  more tractable category of problem than what this investigation started with.
- **The boundary-stitch bug is NOT cleanly per-model after all** — the NEWCOREPC qwen run
  (§6, row 14) was the confirming data point this section asked for, and it complicates
  the earlier theory: `qwen2.5-coder-7b` passed both boundary-stitch cases (2/2) on
  HARDCORELAPTOPMSI/HARDCOREPC but only 1/2 on NEWCOREPC, while `Meta-Llama-3.1-8B` failed
  both (0/2) on NEWCOREPC. Same model, different machines, different results — this looks
  more like a machine/environment factor (or run-to-run noise on a single boundary-stitch
  case) than a clean per-model split. Not yet root-caused; flagging the earlier "Llama-
  specific" conclusion as unconfirmed rather than restating it as settled.
- **qwen2.5-coder-7b does not beat its own B2 RAG baseline at 120-question scale** (§6, row
  14: B3 29/120 vs. B2 52/120) — consistent with the earlier 100-question findings on this
  model (`B2 RAG baseline... remains above B3 for qwen`), now confirmed after all the
  Tier 1/1.5/2.5 retrieval-quality work that let `Meta-Llama-3.1-8B` pull ahead of its own
  RAG baseline (row 7 onward). Whatever Context Fabric's approach buys on Llama, it isn't
  reliably transferring to this smaller/different model family yet.
