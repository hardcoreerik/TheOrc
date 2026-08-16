# OrcEngine Fringe Lab

**This is not a roadmap phase.** It is not production architecture, and running an
experiment here is not permission to rewrite OrcEngine around a speculative idea.
It is a controlled laboratory whose job is to attack our own assumptions and steal
concepts from unrelated engineering fields, cheaply, before we build the wrong
engine well.

> Weird ideas are cheap to test. Weird ideas are expensive to believe.

Branch/worktree: `research/orcengine-fringe-lab`, isolated from
`feat/orcengine-phase0` (frozen evidence) and `feat/orcengine-phase1` (the
correctness ruler). Fringe Lab may consume Phase-0 oracle code, streaming code,
retained artifacts, fixtures, and benchmark utilities, but its own code and
conclusions stay out of both those branches until explicitly promoted.

Code lives under `Tools/OrcEnginePhase0/fringe_lab/`.

## Every experiment records

`hypothesis`, `why it might work`, `why it might be stupid`, `setup`,
`measurements`, `result`, `interpretation`, `confidence`, `next_experiment`,
`promotion_verdict`. See `36. THE DATA I WANT` in the originating steering
document for the full machine-readable result schema
(`RESULT_SCHEMA_FIELDS` in each experiment script).

## Promotion levels

Nothing here becomes OrcEngine architecture merely because one run looked cool.

| Verdict | Meaning |
|---|---|
| REJECTED | Neat idea, doesn't work |
| INTERESTING | Evidence incomplete |
| RESEARCH CANDIDATE | Worth more testing |
| ARCHITECTURE CANDIDATE | Strong enough for an ADR discussion |
| ROADMAP CANDIDATE | Deserves implementation planning |

No skipping levels.

## Experiment log

| ID | Title | Verdict | One-line result |
|---|---|---|---|
| C | Chunked lm_head streaming | RESEARCH CANDIDATE | Exact greedy argmax + top-5 at every chunk size down to 1 row -- max-over-partition is exact, not approximate, by construction |
| A | Execution-trace cache simulator | INTERESTING | LRU scores **0.0%** hit rate on a plain sequential decode trace (textbook cyclic-access-defeats-LRU pathology); NextUse reaches 54.7% at the same 4GB budget. Don't default to LRU for the future layer cache. |

Full JSON reports are reproducible via:

```bash
cd Tools/OrcEnginePhase0
python fringe_lab/experiment_a_cache_sim.py
python fringe_lab/experiment_c_chunked_lm_head.py
```

Deferred (designed in the steering doc, not yet run this checkpoint): B (tiled
matmul beyond VRAM budget), D (ablation-vs-quantization sensitivity
correlation), E (multi-context byte amortization), F (256MB challenge).

## Cross-discipline notes

| Field | Concept borrowed | OrcEngine analogy | Experiment | Result | Useful? |
|---|---|---|---|---|---|
| Databases / OS | Working-set caching, page replacement | VRAM as hot cache, RAM/NVMe as colder tiers | A | LRU pathologically bad on cyclic decode access; NextUse-style scheduling wins because the future access sequence is fully known | Yes -- concrete 6B/6C guidance |
| Databases | Partitioned aggregation (max over row-blocks) | Streamed/chunked lm_head, never fully resident | C | Exact, not approximate -- associativity of max() | Yes -- research candidate for 6C |

(Sections for Compilers, Signal Processing, Compression, Distributed Systems,
Fault Tolerance, and Scientific Computing are reserved for later experiments
B/D/E/F and beyond -- not populated yet, per the "don't overclaim ahead of
evidence" rule that governs the rest of this project.)
