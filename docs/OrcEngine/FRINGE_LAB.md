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
| C | Chunked lm_head streaming (NumPy) | RESEARCH CANDIDATE | Exact greedy argmax + top-5 at every chunk size down to 1 row -- max-over-partition is exact, not approximate, by construction |
| A | Execution-trace cache simulator | INTERESTING | LRU scores **0.0%** hit rate on a plain sequential decode trace (textbook cyclic-access-defeats-LRU pathology); NextUse reaches 54.7% at the same 4GB budget. Don't default to LRU for the future layer cache. |
| C2 | Chunked lm_head streaming (real disk I/O) | RESEARCH CANDIDATE | Real `Meta-Llama-3.1-8B` output head (128256x4096), real `seek()`/`read()` calls, exact argmax at every chunk size incl. 128256 individual 1-row reads. Caveat: OS page cache was warm (file just written), so this proves syscall-cheap exactness, not disk-bandwidth-bound cost yet. |
| A2 | Adversarial cache traces (5 shapes) | RESEARCH CANDIDATE | NextUse's advantage is real but regime-dependent: huge on cyclic single-context/speculative traces (LRU 0% vs NextUse up to 58.6%), shrinks as concurrent contexts grow (LRU stops being pathological once streams are staggered), and nearly disappears for skewed MoE routing until memory is nearly exhausted. The valuable primitive is "expose known future execution," not "always pick NextUse." |

**Important correction, logged for honesty**: the first draft of A2's
interpretation guessed "multi-context traces are worse for LRU than
single-context" before checking the actual numbers -- the real data showed
the opposite (LRU stops being pathological once multiple streams are
interleaved, because interleaving hands it genuinely-recent pages to find).
The guess was corrected against the measured `survives_summary` output before
being recorded here. Left as a reminder that plausible-sounding narratives
still need to be checked against the run's own numbers.

Full JSON reports are reproducible via:

```bash
cd Tools/OrcEnginePhase0
python fringe_lab/experiment_a_cache_sim.py
python fringe_lab/experiment_c_chunked_lm_head.py
python fringe_lab/experiment_c2_real_io_chunked_lm_head.py
python fringe_lab/experiment_a2_adversarial_cache_traces.py
```

Deferred (designed in the steering doc, not yet run this checkpoint): B (tiled
matmul beyond VRAM budget), D (ablation-vs-quantization sensitivity
correlation), E (multi-context byte amortization), F (256MB challenge),
9 (fake-slow-storage emulator -- needed before C2's disk-bandwidth-bound
crossover question can actually be answered).

## Cross-discipline notes

| Field | Concept borrowed | OrcEngine analogy | Experiment | Result | Useful? |
|---|---|---|---|---|---|
| Databases / OS | Working-set caching, page replacement | VRAM as hot cache, RAM/NVMe as colder tiers | A, A2 | LRU pathologically bad on cyclic decode access; NextUse wins big there and in staggered multi-context, but is nearly moot for skewed MoE routing except under severe pressure | Yes -- regime-aware 6B/6C guidance, not a blanket policy choice |
| Databases | Partitioned aggregation (max over row-blocks) | Streamed/chunked lm_head, never fully resident | C, C2 | Exact, not approximate -- associativity of max(); holds under real disk I/O too, though disk-bandwidth-bound cost is still unmeasured (warm cache confound) | Yes -- research candidate for 6C, needs the cold-cache follow-up |

(Sections for Compilers, Signal Processing, Compression, Distributed Systems,
Fault Tolerance, and Scientific Computing are reserved for later experiments
B/D/E/F and beyond -- not populated yet, per the "don't overclaim ahead of
evidence" rule that governs the rest of this project.)
