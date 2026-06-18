# DATASET_VERIFY.md — v2gold → v3gold / tester_v1 Split Verification

**Date:** 2026-06-17  
**Role:** Adversarial data engineer (no source modified)  
**Commanded output:** `.grok/DATASET_VERIFY.md` (this file)

## Scope
Verified the split performed by `training_pit/scripts/split_v2gold.py` (which embeds its own `_classify()` / `_resolve_lane()`) on:
- `training_pit/datasets/train_v2gold.jsonl` (1861)
- `training_pit/datasets/eval_v2gold.jsonl` (199)

Produced:
- `train_v3gold.jsonl` (906 boss_clean)
- `eval_v3gold.jsonl` (87 boss_clean)
- `train_tester_v1.jsonl` (955 tester_poison)
- `eval_tester_v1.jsonl` (112 tester_poison)

No `split_invalid.json` was written.

## 1. ARITHMETIC: PASS

Exact line counts (via `Get-Content | Measure-Object -Line` + Python `len([l for l in ...splitlines() if l.strip()])`):

- `train_v2gold.jsonl`: 1861
- `eval_v2gold.jsonl`: 199
- `train_v3gold.jsonl`: 906
- `eval_v3gold.jsonl`: 87
- `train_tester_v1.jsonl`: 955
- `eval_tester_v1.jsonl`: 112

**Sums:**
- 906 + 955 = 1861 (exact match)
- 87 + 112 = 199 (exact match)

**No lines dropped.** Total in = total out. 0 invalid / quarantined rows (no `split_invalid.json` created; `_route_file` would have populated it for json-parse errors or `no_valid_json` cases).

All source rows had parseable plans and were routed to exactly one bucket.

## 2. SPOT-CHECK BOSS: PASS

Read `train_v3gold.jsonl` (906 lines). Performed full scan + explicit first 10 / last 10.

**Logic applied (mirrors split_v2gold.py:57-80):**
- Extract last assistant message content.
- `re.search(r"\{.*\}", content, re.S)` + `json.loads`
- For every task: `role.upper().strip()`, `_resolve_lane(role)`, `blob = title + " " + description`
- If lane=="TESTER" and `_WRITE_VERBS.search(blob)` → poison.

**Full scan result:** 0 violations (0/906 examples contained a TESTER-lane task with a write verb).

**First 10 (L1-L10) and last 10 (L897-L906) — all CLEAN:**

Example details read:

- `train_v3gold.jsonl:1`
  plan: "Research Windows-specific file path constraints, implement an NUnit te..."
  tasks:
  - RESEARCHER "Research C# System.IO Windows path constraint"
  - CODER "Write OrchestratorIDE.UITests/Tests/T25_FileV..."   (write verb on CODER = OK)
  - TESTER "Run OrchestratorIDE.UITests/Tests/T25_FileVal..."   (TESTER role + "Run" — **no** create/write/implement/build/generate/author/compose in blob)

- `train_v3gold.jsonl:2` to `:10`: 2-task plans, CLEAN (no TESTER+write).

- `train_v3gold.jsonl:906` (last)
  plan: "Extract validation, gateway, and logging into separate modules..."
  tasks:
  - CODER "Create validators/paymentValidator.js, gatewa..."
  - TESTER "Run Mocha tests for paymentService"   (TESTER + "Run" — no write verb)

**No task with TESTER/QA/QUALITY_ASSURANCE role + write verb slipped through in the 20 spot samples or the entire file.**

All TESTER tasks observed in boss bucket were read-only verification ("Run ... test", "Test ...").

## 3. SPOT-CHECK TESTER: PASS

Read `train_tester_v1.jsonl` (955 lines). Full scan + first 10 / last 10.

**Full scan result:** 0 violations. Every example has ≥1 poisoned task (TESTER-lane + write verb). 0 clean-in-tester false positives.

**First 10 and last 10 examples — all POISON (≥1 each):**

- `train_tester_v1.jsonl:1`
  plan: "Patch the Get-AzureUserInfo function..."
  tasks:
  - CODER "Patch Get-AzureUserInfo..."
  - TESTER "Add regression test for Get-AzureUserInfo..."  → verb=Write (in description: "Write a Pester test in ...")

- `train_tester_v1.jsonl:2` — TESTER "Add overflow detection test" (verb Write in blob)
- `train_tester_v1.jsonl:3..10`: similar; titles/descriptions contain "Write"/"Create" assigned to TESTER (e.g. "Write tests/test_moderation.py", "Create ... test", "Add 500-error test" + description verb).

- `train_tester_v1.jsonl:946` to `955` (last):
  - L951: TESTER verb="write" (lowercase caught by re.I) "Add divide-by-zero test..."
  - L955: TESTER "Add test in test_tasks.py" (verb in description)
  - All had exactly one poisoned task in the observed plans.

**No false positives found.** Every example routed here legitimately poisons the boss (teaches TESTER to write code/tests).

## 4. CROSS-SET: PASS

Verbatim raw-line (exact string match) intersection check:

- `set(train_v3gold lines) & set(train_tester lines)` → **0**
- `set(eval_v3gold lines) & set(eval_tester lines)` → **0**

Additional:
- All 906 + 955 lines are subsets of original `train_v2gold.jsonl` (no invented lines).
- Same for eval.
- No line appears in >1 output bucket.

Routing is disjoint by construction and by observed content.

## 5. SCRIPT LOGIC AUDIT (split_v2gold.py)

Read full file. Key functions:

**`_resolve_lane` (split_v2gold.py:43-47):**
```python
_TESTER_ROLES   = {"TESTER", "QA", "QUALITY_ASSURANCE"}
...
def _resolve_lane(role: str) -> str:
    if role in _TESTER_ROLES:   return "TESTER"
    ...
    return "CODER"
```

**`_classify` (split_v2gold.py:57-80):**
```python
for t in tasks:
    ...
    if lane == "TESTER" and _WRITE_VERBS.search(blob):
        return "tester_poison"   # <--- early return, not break
return "boss_clean"
```

### Findings

- **Break handling:** No `break` statement exists in `split_v2gold.py`'s `_classify`. It uses `return "tester_poison"` which exits immediately on first match. Equivalent (and slightly stronger) to the `flags.add + break` pattern in `suitability_gate.py:97-98`.
  - Poisoned-first + clean-later: correctly returns tester_poison (no misclassification).
  - Clean-first + poisoned-later: loop reaches the bad task → returns tester_poison. **Correct per intent ("any poisoned task = tester_poison").**

- **Multi-task first-CODER / later-TESTER+write:** Correctly classified (loop does not early-exit on clean tasks). Observed in real data (tester L1: CODER then TESTER-write).

- **Role edge cases (HIGH/MEDIUM risk for future data):**
  - Exact-match only after `.upper().strip()`.
  - `"QA_ENGINEER"`, `"TEST_ENGINEER"`, `"TESTING"`, `"TestLead"`, `"QA "`, `"quality assurance"` (space vs `_`), `"TEST-ENGINEER"` etc. → all resolve to **CODER**.
  - Current v2gold dataset only contains exact `"TESTER"` (1961 occurrences), `"CODER"`, `"UIDEVELOPER"`, `"RESEARCHER"`. No variant roles present, so no misroutes occurred.
  - If future data or generators emit `"QA_ENGINEER"` for a test writer that also does "write test_foo.py", it would be routed to `train_v3gold` (boss_clean) — **contamination**.
  - Severity: **MEDIUM** (dataset currently safe; logic brittle to role naming).

- **Other notes (no fixes applied):**
  - Logic duplicated verbatim between `split_v2gold.py` and `suitability_gate.py` (different `_classify` signatures: str vs set[str]). Drift risk.
  - `re.search(r"\{.*\}", ..., re.S)` is greedy (first { to last }). Worked for all 2060 source examples (0 invalids), but fragile if assistant content ever contains multiple top-level objects or post-json text.
  - Only poison vs clean routing here; other flags (task_overflow, no_valid_json) only quarantine. By design for this split.
  - Raw lines written verbatim (metadata preserved).

No logic bugs that affected this run's outputs.

## 6. DATASET INVENTORY (training_pit/datasets/*.jsonl)

All 22 `.jsonl` files (counts via Python line filter; sizes in KiB; mtime date):

| File | Lines | Size (KB) | Purpose / Status | Notes / Flags |
|------|-------|-----------|------------------|---------------|
| `cerebras[api].synthetic.boss.1458.jsonl` | 1458 | 5763 | Synthetic boss plans (Cerebras) — historical source | Generated data |
| `cerebras[api].synthetic.boss.1534.jsonl` | 1534 | 6045 | Synthetic boss plans (Cerebras) — source | Active snapshot |
| `cerebras_gold.work.jsonl` | 1534 | 6045 | Work artifact from generate_cerebras_gold.py | Duplicate of 1534 file at generation time; transient? |
| `codex[api].synthetic.boss.217.jsonl` | 217 | 1001 | Synthetic (Codex) — source | Small set |
| `codex_gold.work.jsonl` | 217 | 1001 | Work artifact | Likely duplicate of above |
| `eval_tester_v1.jsonl` | 112 | 458 | Generated: eval split of tester_poison (from split_v2gold) | **Active generated** (2026-06-17 17:14) |
| `eval_v1.jsonl` | 138 | 702 | Legacy eval set (v1) | Old; superseded |
| `eval_v2gold.jsonl` | 199 | 829 | Source eval for split (contaminated gold) | Still default in eval_adapter.py, pit scripts |
| `eval_v3gold.jsonl` | 87 | 370 | Generated: eval boss_clean (v3) | **Active generated** (2026-06-17); ≥20 gate met |
| `hardcorepc[6gb].synthetic.boss.638.jsonl` | 638 | 2200 | Synthetic (HardcorePC 6GB) | Processing variant |
| `hardcorepc[8gb].normalized.boss.860.jsonl` | 860 | 3428 | Normalized synthetic | See below |
| `hardcorepc[8gb].raw.boss.840.jsonl` | 840 | 1582 | Raw captured/synth | See below |
| `hardcorepc[8gb].synthetic.boss.20.jsonl` | 20 | 37 | Tiny synthetic subset | Edge case set |
| `hardcorepc_boss_normalized.jsonl` | 860 | 3365 | Normalized (alt name) | **Redundant?** Same line count, different size vs [8gb] version; unclear which is canonical |
| `hardcorepc_raw_overnight.jsonl` | 840 | 1587 | Raw overnight run | Close but != [8gb].raw; possible re-run or tweak |
| `hardcorepc_raw_synthetic20.jsonl` | 20 | 37 | Tiny raw synth | Duplicate of [8gb] 20-line? |
| `mainpc[24gb].captured.boss.1384.jsonl` | 1384 | 7162 | Real captured (main PC 24GB run) | High-value Tier-1 style source |
| `merged[mixed].normalized.boss.2244.jsonl` | 2244 | 10591 | Merged normalized mix of sources | Large combined file; purpose unclear vs v2gold |
| `train_tester_v1.jsonl` | 955 | 3894 | Generated: train tester_poison (v1) | **Active generated** (2026-06-17 17:14) |
| `train_v1.jsonl` | 1246 | 6365 | Legacy train set (v1) | Old; has .bak sibling |
| `train_v2gold.jsonl` | 1861 | 7695 | Source train gold (v2, 51% tester_poison) | Current default in `train_lora.py:26`, `train_v2_overnight.ps1`, pit_manager etc. Still referenced post-split |
| `train_v3gold.jsonl` | 906 | 3801 | Generated: train boss_clean (v3) | **Active generated** (2026-06-17); scripts still default to v2gold |

**Flags:**
- **Redundant / unclear copies:** `hardcorepc_*` bracketed vs plain names (different byte content despite matching counts for some). `*_gold.work.jsonl` appear to be exact copies of the numbered synthetic at generation time.
- **Stale / legacy:** `*_v1.jsonl`, old dated synthetics (2026-06-13).
- **Not yet wired:** v3gold/tester_v1 not referenced outside `split_v2gold.py` itself and its summary. `train_lora.py` and callers still use `train_v2gold.jsonl` (the contaminated source).
- **Backups not in .jsonl glob:** `train_v1.jsonl.bak`, `train_v2.jsonl.bak`, `hardcorepc_*.bak` exist (archived prior versions).
- **Active gold sources for current pipelines:** v2gold (contaminated), plus the various synthetic/captured as potential feeds.

## 7. OVERALL VERDICT: VERIFIED

**All invariants hold exactly. No routing errors, no cross-set leaks, no dropped lines, no tester_poison in boss buckets (0/993), 100% poison coverage in tester buckets (1067/1067). Spot + full scans clean. Script early-exit and multi-task logic correct for observed data.**

One latent logic observation (no impact on this dataset): `_resolve_lane` exact-set match can silently treat variant tester roles as CODER (MEDIUM if role vocabulary expands).

The split itself is sound. Adoption (wiring v3gold into train_lora defaults + updating docs) is a separate downstream task.

**Read artifacts (cited):**
- split_v2gold.py:43 (_resolve_lane), :57 (_classify + return), :106-128 (routing)
- Full line scans + explicit L1/L906 plans from train_v3gold.jsonl and train_tester_v1.jsonl
- Verbatim set intersections on raw lines
- mtimes and counts on 2026-06-17 files

**No source files were modified.**