# TheOrc — Model Guide

> **Local model disclaimer:** A model's behavior depends on size, quantization level,
> GPU backend, VRAM headroom, context length, and prompt format. Profile scores are
> guidelines, not guarantees. Run **Models → Run Model Capability Test…** to get
> local evidence for your specific hardware and model.

---

## Model Profile Scores

Every model in TheOrc's catalogue has a `ModelProfile` with numeric role scores:

| Score | Range | What it measures |
|---|---|---|
| `BossScore` | 0–10 | Task decomposition quality, plan format adherence, multi-task JSON output |
| `CoderScore` | 0–10 | Code generation quality, write_file reliability, language coverage |
| `ResearcherScore` | 0–10 | Context reading, summarization, fetch_url / grep_code quality |
| `TesterScore` | 0–10 | Test execution quality, pass/fail verdict reliability, no-write-file behavior |
| `SwarmScore` | 0–10 | Overall swarm multi-role performance (composite) |
| `MinVramGb` | GB | Minimum VRAM required to load the model at recommended quantization |
| `Speed` | Fast/Medium/Slow | Inference throughput tier relative to its size class |
| `NativeToolUse` | bool | Whether the model supports native function-call tool format |

> **Important:** `NativeToolUse = true` means the model supports the OpenAI-style tool
> call API. It does **not** mean the model will reliably complete long `write_file` payloads.
> Tool support is not binary — see [Tool Support Is Not Binary](#tool-support-is-not-binary) below.

---

## Tool Support Is Not Binary

A model can:
- Have `NativeToolUse = true` ✅
- Pass GOBLIN MIND format probes (short tool calls) ✅
- Successfully start a `write_file` tool call ✅
- **Still fail** when the JSON payload represents a large file ❌

The critical threshold is the size of the JSON string value inside the tool call.

`write_file hello.txt` with "Hello, World!" → ~30 chars → almost any model handles this.

`write_file app.py` with 150 lines of Python, all newlines escaped as `\n` → ~5 KB JSON string
→ the model must maintain JSON schema context, escape state, and code coherence for hundreds
of tokens. This is a parameter-count capacity problem, not a format issue.

**The FileWriteSmall / FileWriteMedium / FileWriteLarge tests** measure this directly.
Run them from **Models → Run Model Capability Test…**.

---

## Role Recommendations

### Boss (Swarm Orchestrator)

The boss model receives the user's goal and produces a JSON plan with 2–4 tasks.
It needs strong structured output capability and plan format adherence.

| Model | BossScore | Notes |
|---|---|---|
| `theorc-boss:gemma4` | Profile-rated | Custom Modelfile: `temperature=0.2, think=false, 16K context cap`. QAT wrapper — not LoRA-trained. Best available boss for local use. |
| `qwen2.5-coder:14b` | 6 | Strong planning quality. Proven in swarm benchmarks (Combo A). |
| `gemma4:12b` | Partial | ⚠️ Planning collapse observed without Modelfile tuning. Use `theorc-boss:gemma4` instead. |
| `phi-4` 14B | High | Strong reasoning boss. |
| `deepseek-r1` distill 14B | High | Chain-of-thought boss; verbose but high-quality plans. |

### Coder / UIDeveloper

These workers receive a task from the boss and write code files. They need strong
`write_file` JSON reliability for their expected file sizes.

| Model | CoderScore | Notes |
|---|---|---|
| `qwen2.5-coder:14b` | High | Recommended primary coder for 10–16 GB VRAM. |
| `qwen2.5-coder:7b` | Good | Solid worker for 6–8 GB VRAM. |
| `gemma4:12b` | Good | Excellent as swarm coder/researcher in worker role. |
| `codestral:22b` | High | Best pure code worker. Requires 16 GB+. |
| `nemotron-3-nano:4b` | ⚠️ Limited | **Fails long write_file payloads.** Suitable for short tasks only. |

### Researcher

The Researcher worker investigates APIs, reads docs, and summarizes findings.
It has no `write_file` access by design. Context window matters most.

| Model | ResearcherScore | Notes |
|---|---|---|
| `gemma4:12b` | High | 256K context. Excellent summarizer and doc reader. |
| `mistral-nemo:12b` | High | 128K context. Strong long-document research. |
| `nemotron-3-nano:4b` | Good | Fast. Suitable for short lookups. Fails on long payloads but Researcher never needs write_file. |
| `llama3.1:8b` | Good | Versatile 8B model. Good researcher in most setups. |

### Tester

The Tester worker runs shell commands, reads files, and reports pass/fail verdicts.
It has **no** `write_file` access by design — this is a hard constraint, not a config option.

| Model | TesterScore | Notes |
|---|---|---|
| `nemotron-3-nano:4b` | Good | Fast, inexpensive. Short verdict output. Well-suited for tester role since no write_file needed. |
| `qwen2.5-coder:7b` | Good | More context-aware verdicts. |

### Single Agent

In Single Agent mode, one model does everything: planning, coding, research, and testing.
It needs high scores across all dimensions and reliable long-payload write_file.

| Model | Notes |
|---|---|
| `qwen2.5-coder:14b` | Best single-agent choice at 10–16 GB. |
| `qwen2.5-coder:7b` | Good for lighter tasks on 6–8 GB. |
| `gemma4:12b` | Strong coder and researcher. Boss/planner role may need Modelfile tuning for decomposition. |
| `nemotron-3-nano:4b` | ❌ Not recommended for T06-style autonomous coding. Use for chat/research only. |

---

## Local Model Observations (Current Hardware)

These observations come from live tests on local hardware (RTX 5070 Ti 16 GB, Ollama 0.30.6).
They are recorded in `OrchestratorIDE/Resources/model-wiki-observations.json` and displayed
in the Model Wiki.

### `nemotron-3-nano:4b-q8_0`

- **T06 result:** FAIL — zero project files written across 3 passes
- **Observed:** Starts `write_file` JSON for `main.py` and `file_manager.py`, but JSON truncates
  before closing braces on passes 1 and 2; pass 3 returns empty response
- **Evidence:** `len=2000, opens=2, closes=0` (pass 1); `len=85, opens=2, closes=0` (pass 2)
- **Root cause:** 4B active parameter ceiling for long nested JSON payloads
- **Classification:** `not_recommended_for_long_write_file`
- **Suitable for:** Short chat, lightweight tester role, log summarization, short command/report tasks
- **Not suitable for:** Primary single-agent Execute coder, multi-file autonomous generation

### `nemotron-3-nano:4b`

- **Status:** Not directly tested (inferred from Q8 result)
- **Expectation:** Q4 precision is lower than Q8 — performance should be the same or worse
- **Classification:** `not_recommended_for_long_write_file`

### `theorc-boss:gemma4`

- **Base:** `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0` (Google official QAT GGUF, 6.7 GB)
- **Serving via:** Ollama with custom `theorc-boss-gemma4.Modelfile`
- **Modelfile settings:** `temperature=0.2`, `think=false`, `num_ctx=16384`, few-shot examples
- **Result:** PASS — consistently produces 3–4 task plans with non-empty descriptions
- **Important:** This is a **QAT Modelfile wrapper**, not a LoRA-trained model. Phase 3
  LoRA training (boss-planning adapter) is blocked at 0/150 examples.
- **Classification:** `recommended_boss_model`
- **Not suitable for:** Raw use without Modelfile (planning collapse observed on base `gemma4:12b`)

### `gemma4:12b`

- **Result:** PARTIAL — excellent coder/researcher, not recommended as boss without tuning
- **Boss behavior:** Planning collapse observed — outputs `title='Execute goal', description=''`
  when asked to decompose tasks without the calibrated Modelfile
- **Suitable for:** Swarm coder, swarm researcher, single-agent coding tasks
- **Not suitable for:** Boss role without Modelfile tuning
- **Classification:** `recommended_coder_researcher_not_boss`

### `hf.co/google/gemma-4-12B-it-qat-q4_0-gguf:Q4_0`

- **What it is:** Google official QAT GGUF, same base as `theorc-boss:gemma4` but without boss-planning Modelfile calibration
- **Result:** PARTIAL — same observations as `gemma4:12b`
- **Classification:** `recommended_coder_researcher_not_boss`

---

## GOBLIN MIND Probe Scores

GOBLIN MIND probes measure a model's behavioral capability at runtime:

| Probe type | What it measures |
|---|---|
| **Format Fingerprinting** | Which tool-call serialization format the model prefers (OpenAI JSON, Hermes XML, bare JSON, Python-style, YAML) |
| **Category Boundary Mapping** | Which task categories the model reliably succeeds at (7 categories × 2 tests each) |
| **Schema Reduction** | Whether the model needs simplified tool schemas (auto-applied by `AgentLoop`) |

Run probes from: **Models → Run Tool Call Tests…**

Results are stored in `%APPDATA%\OrchestratorIDE\tool-call-profiles.json` and shown in the Model Wiki.

---

## Capability Test Results

Results from **Models → Run Model Capability Test…** are stored in:
```
%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl
```

Each result includes `ValidJson`, `Truncated`, `OpenBraceCount`, `CloseBraceCount`, and `Notes`
so you can see exactly what went wrong when a model fails a write_file test.

---

## VRAM Quick Reference

| VRAM | Practical ceiling | Notes |
|---|---|---|
| 4 GB | 3B Q8 / 7B Q4 | Very limited; mostly CPU-assisted inference |
| 6 GB | 7B Q5 | Smallest tier with reliable coding capability |
| 8 GB | 7B Q8 / 12B Q4 | Good for Qwen 7B or Gemma 4 12B Q4 |
| 10–12 GB | 14B Q4 | Qwen 14B fits; strong coder/researcher |
| 16 GB | 14B Q5 / 22B Q4 | `theorc-boss:gemma4` + Qwen 14B worker; very capable setup |
| 24 GB+ | 32B Q4 | Qwen 2.5 Coder 32B — best available locally; not yet in test matrix |

See [HARDWARE_GUIDE.md](HARDWARE_GUIDE.md) for more detail.
