# 🧠 GOBLIN MIND — Project Updates Log

> Milestone tracking for TheOrc v1.1 — Universal Model Capability Discovery
> See `GOBLIN_MIND_TODO.md` for full task breakdown.

---

## T06 Runtime Observation — Nemotron Nano 4B (2026-06-09)

> This is not a GOBLIN MIND phase result — it is pre-probe runtime evidence
> gathered by the T06 FlaUI integration test. It informs the FileWrite Probe
> design (Phase 4b) and updates model placement across the project.

**Model tested:** `nemotron-3-nano:4b-q8_0`
**Mode:** Single-agent Execute (NOT swarm)
**Task:** Build OrcResearcher — write 6 Python files via write_file tool calls

**Observed:**
- App launched ✅, workspace confirmed ✅, AutoSend triggered Execute mode ✅
- Pass 1: model started `write_file main.py` — JSON truncated (opens=2, closes=0)
- Pass 2: model started `write_file file_manager.py` — JSON truncated (opens=2, closes=0)
- Pass 3: empty response (len=0)
- Zero files written across all 3 passes

**Conclusion:**
Tool support is not binary. A model can initiate a native tool call and still fail when
the payload is large. The 4B active-parameter ceiling prevents the model from maintaining
JSON schema context across a ~150–300 line Python file encoded as `\n`-escaped JSON.

**Actions taken:**
- `ModelProfiles.cs` — CoderScore lowered (4b: 4→2, 4b-q8_0: 5→3), descriptions updated,
  preferred uses documented, "coding" removed from Strengths
- `GOBLIN_MIND_TODO.md` — Phase 4b (FileWrite Payload Probe) added with Small/Medium/Large tiers
- `training_pit/MODEL_COMPATIBILITY.md` — Nemotron Nano 4B section added with T06 evidence
- `training_pit/HARDWARE_GUIDE.md` — LoRA/QLoRA guidance for small models added
- `T06_BuildResearchTool.cs` — Class summary expanded with failure interpretation guide

**Next step for this model:**
Run FileWriteSmall/Medium/Large probes (Phase 4b) to find the actual payload ceiling.
Candidate lightweight roles: TESTER, log summarizer, short report generator.

---

## Milestone Overview

**Codename:** GOBLIN MIND
**Goal:** Give TheOrc's goblin swarm self-knowledge — every model learns what tools it can use, in what format, for what task categories. TheOrc uses this to route tasks intelligently.

**Research origin:** 2026-06-08 research session exploring universal tool generation.
Key finding: nobody has built a unified runtime system that does:
`probe → fingerprint → generate → validate → route → feedback loop`
as a single persistent system feeding swarm task routing. This is the pioneer move.

---

## Research Summary (2026-06-08)

### What exists in the field
| System | What it does | Our gap |
|---|---|---|
| BFCL (2024–2025) | Tests conformance to fixed schemas (AST-based) | Static. No discovery or generation. |
| ToolBench/ToolLLM (ICLR 2024) | Auto-generates schemas from 16K APIs | One-directional. No failure-mode adaptation. |
| ToolACE (ICLR 2025) | Self-evolution schema synthesis | Training-time only. Not a runtime system. |
| Toolformer (Meta 2023) | Model learns *when* to call tools | Tool set predefined. No format discovery. |
| AutoGen/LangGraph | Dynamic tools via Python code-gen | Same format always. No behavioral probing. |

### What doesn't exist (our territory)
1. Adaptive schema generation from *observed failure modes* at runtime
2. Systematic format mutation testing per model
3. Unified pipeline: probe → fingerprint → schema generate → validate → persist → route

### 5 Options Rated
| # | Option | Rating | Priority |
|---|---|---|---|
| 1 | Behavioral Format Fingerprinting | 9/10 | **Phase 1** |
| 2 | Evolutionary Schema Search | 8/10 | Phase 5 |
| 3 | Category Boundary Mapping | 8.5/10 | **Phase 2** |
| 4 | Few-Shot Format Bootstrapping | 7/10 | Phase 3 (add-on) |
| 5 | Adaptive Schema Reduction | 7.5/10 | Phase 4 (middleware) |

---

## Foundation Completed (pre-milestone work)

These were built as the capability probe foundation before the Goblin Mind milestone was defined:

### ToolCallProbeEngine.cs ✅
- 5 deterministic tests × 2 modes (Native API + Text-JSON) = 10 checks per model
- Tests: BasicCall, IntArgs, MultilineContent, ToolSelection, StructuredOutput
- Binary pass/fail on specific assertions (name match, arg value match)
- No execution — verifies call structure only

### ToolCallProfileStore.cs ✅
- Persists per-model probe results to `%AppData%\OrchestratorIDE\tool-call-profiles.json`
- `GetMode(modelId, profileDefault)` — returns `ToolCallMode` (Native/TextJson/Both/None/Unknown)
- `ShouldSendNativeTools()` / `ShouldUseTextJson()` helpers

### AgentLoop.cs dispatch hook ✅
- Reads profile store before each session
- If `NativePasses` failed → sets `tools = []` to force text-JSON path
- No more silent failures from wrong dispatch mode

### ToolCallTestWindow.xaml/.cs ✅
- WPF UI window — grid of models × test results
- Live progress log during probe runs
- Load existing profiles for models not tested this session
- Accessible from Models menu → "Run Tool Call Tests…"

### tool-probe.exe (CLI) ✅
- Standalone `net10.0` console tool in `Tools/ToolCallTester/`
- Flags: `--host`, `--model`, `--json`, `--list`, `--help`
- Writes to same JSON profile store
- Exit codes: 0=all pass, 1=some fail, 2=no Ollama

### SwarmConfigAdvisor.cs ✅
- Hardware-aware swarm model recommender
- `DetectHardwareAsync()` — nvidia-smi multi-GPU query
- `Recommend(hardware, models)` — benchmark scores + VRAM budget + observed metrics
- Auto-Config UI panel in SwarmBoardPanel

### SwarmRunMetrics / SwarmRunRecord ✅
- Emits `SwarmRunRecord` at end of every swarm run
- JSONL persistence at `%AppData%\OrchestratorIDE\swarm-runs.jsonl`
- Tracks: models used, duration, files written, retry counts, quality scores

---

## Session Log

### Session 001 — 2026-06-08 | Research & Milestone Definition
**Type:** Research / Planning
**Status:** ✅ Complete

**Work done:**
- Full research discussion: state of the art in tool call benchmarking (BFCL, ToolBench, ToolACE, Toolformer)
- Identified the pioneer gap: runtime behavioral probing → schema generation → swarm routing
- Defined 5 options with ratings
- Named milestone "GOBLIN MIND"
- Created `GOBLIN_MIND_TODO.md` with 6 phases + completion criteria
- Updated `README.md` — shipped features, Goblin Swarm section, new roadmap
- Committed all foundation work (tool probe system, swarm config advisor, run metrics, auto-config UI)

**Decisions made:**
- Start with Phase 1 (Format Fingerprinting) — cheapest, most immediately actionable
- Phase 2 (Category Mapping) second — directly feeds swarm routing
- Phases 3-5 follow as infrastructure allows
- Phase 6 (TheOrc steering) is the capstone — boss reads capability profiles

**Key architectural decision:**
> The `FormatFingerprint` and `CategoryBoundaryMap` become first-class citizens in `ToolCallProfile`.
> `AgentLoop` reads both before every session. No static config flags — only behavioral evidence.

---

## Next Session Checklist

When picking up Phase 1:

1. Read `GOBLIN_MIND_TODO.md` Phase 1 tasks
2. Create `Services/ToolCalls/FormatProbeEngine.cs`
3. Extend `ToolCallProfile` with `FormatFingerprint` field
4. Wire into `AgentLoop.ApplyFormatFingerprint()`
5. Add "Format" column to `ToolCallTestWindow`
6. Test against 2-3 models to validate format variance exists

**Models to prioritize for initial probing:**
- `qwen2.5-coder:14b` — primary coder
- `hf.co/bartowski/NousResearch_Hermes-4-14B-GGUF:Q5_K_M` — primary boss candidate
- `phi4-mini:latest` — small/fast reference point
- Any new model added to Ollama

---

*Milestone started: 2026-06-08 | Target version: v1.1*
