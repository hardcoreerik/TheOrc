# Context Fabric (CF-7) Test Results Log

Tracks every real CF-7 gate run performed during the NoKvSlot investigation: what was
tested, on which machine, with which model, what bugs were found along the way, and what
each run actually showed. For how the harness itself grades answers, see
[CONTEXT_FABRIC_TEST_HARNESS.md](CONTEXT_FABRIC_TEST_HARNESS.md) (§7/§7a has the full
NoKvSlot root-cause writeup this file summarizes into a results table).

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

### HARDCOREPC

| # | Model | Questions | Code state | `question_pass_rate` | `segment_terminal_coverage` | `citation_precision` | `NoKvSlot` count | Notes |
|---|---|---|---|---|---|---|---|---|
| 1 | qwen2.5-coder-7b-instruct-q5_k_m | 3 | early (pre-margin-fix) | 0/3 | n/a | n/a | 0 | Small-sample noise — see #2 |
| 2 | qwen2.5-coder-7b-instruct-q5_k_m | 100 | current | 25/100 (25%) | 121/128 (94.5%) | 64/84 (76.2%) | **0** | Real, moderate citation-discipline gap — a model-quality question, unrelated to the infra bug |

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
- **The boundary-stitch bug appears Llama-specific, not universal**: `qwen2.5-coder-7b`
  passed both boundary-stitch cases (2/2) on HARDCORELAPTOPMSI, while `Meta-Llama-3.1-8B`
  failed both (0/2) on NEWCOREPC — worth confirming with a qwen run on NEWCOREPC before
  concluding this is truly per-model rather than per-machine.
