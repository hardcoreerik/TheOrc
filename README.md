<div align="center">

![TheOrc Banner](Assets/banner.png)

[![Platform](https://img.shields.io/badge/platform-Windows-0B6DFF?style=for-the-badge&logo=windows)](https://github.com/hardcoreerik/TheOrc/releases)
[![.NET](https://img.shields.io/badge/.NET-10.0-6B38FB?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/10.0)
[![Local](https://img.shields.io/badge/local--first-cloud--optional-21C55D?style=for-the-badge)](#quick-start)
[![License](https://img.shields.io/badge/license-AGPL--3.0-39FF6A?style=for-the-badge)](LICENSE)
[![Release](https://img.shields.io/github/v/release/hardcoreerik/TheOrc?style=for-the-badge&color=13E9B4)](https://github.com/hardcoreerik/TheOrc/releases)

**Local-first AI orchestration, native runtimes, and source-grounded memory for people who would rather own the machine than rent permission from one.**

[**Download**](https://github.com/hardcoreerik/TheOrc/releases) · [**Docs**](docs/ARCHITECTURE.md) · [**Context Fabric**](docs/The%20Orc%20Context%20Fabric.md) · [**Benchmark Corpus**](docs/CONTEXT_FABRIC_BENCHMARK_CORPUS.md) · [**Roadmap**](docs/ROADMAP.md)

</div>

---

> ## v1.13.0 — Context Fabric is complete. The road to v2.0 begins.
>
> Context Fabric — TheOrc's source-grounded memory system — has closed its benchmark gate at **GO** on an honest, un-marked corpus: **104 of 120** held-out questions answered, **97.1%** citation precision, **128 of 128** segments read, against a best competing baseline of 52/120. That is the milestone the whole CF program was built to reach, and it is now behind us.
>
> With durable memory proven, **v2.0 turns to the runtime and the operator's hands**: making native local inference the default lane (Ollama fully optional), and giving agents real reach — browser automation and page understanding, workspace and shell intelligence, and multimodal document intake. [Jump to the v2.0 roadmap ↓](#the-road-to-v20)

---

## Project Credits

TheOrc is created and maintained by [Erik / hardcoreerik](https://github.com/hardcoreerik), with AI-assisted development and review from Claude Sonnet, OpenAI Codex, and Grok Build.

| Contributor | Role |
|---|---|
| [Erik / hardcoreerik](https://github.com/hardcoreerik) | Creator, maintainer, product direction |
| Claude Sonnet | Architecture planning, implementation support, code review |
| OpenAI Codex | Implementation support, adversarial review, verification |
| Grok Build | Adversarial review, PROJECT_TRUTH audits, runtime critique |

See [AI_DEVELOPMENT_DISCLOSURE.md](docs/AI_DEVELOPMENT_DISCLOSURE.md) for what "AI-assisted development" means in practice — what's verified, what isn't yet, and how to report a doc/code mismatch.

---

## What is this thing?

GitHub Copilot helps you write the next line. Cursor rewrites the current file. ChatGPT gives you code to paste.

TheOrc receives a **goal** — *"build a Python CSV cleaner with a GUI"* — breaks it into parallel tasks, and sends each one to a specialist AI agent. While you wait, a Researcher is reading the pandas docs, two Coders are writing separate files, and a UIDeveloper is setting up the README. When they're done, your workspace has the whole project.

The difference is that TheOrc is not just a chat window. It is a local orchestration shell with:

- an Avalonia desktop operator surface
- local chat and swarm execution
- native-runtime and Ollama-backed model paths
- HIVE MIND for distributed local work
- ORC ACADEMY for training a better boss model from reviewed swarm behavior
- Context Fabric, a source-grounded memory system for working across corpora larger than a model context window

**Local-first and cloud-optional.** The core application, models, data stores, training pipeline, and orchestration can run entirely on infrastructure you control. Network tools (web search, URL fetch), external dataset-generation providers, update checks, and remote HIVE nodes are explicit, operator-controlled features — not silent defaults. TheOrc is built around inspectability, approval gates, local ownership, and source reopening instead of magic-context marketing.

It's basically a tiny software company that lives in your PC and does what you tell it. The staff are goblins. This is intentional.

---

## Why TheOrc feels different

Most AI coding tools sell autocomplete, cloud convenience, or one giant context window.

TheOrc is going after a stranger and more useful target:

- **Local-first orchestration**: the shell, runtime paths, approvals, artifacts, and training loop are designed around operator control.
- **Warband execution**: one boss can route work to specialist agents and, increasingly, to other enrolled machines.
- **Source-grounded memory**: Context Fabric lets finite-context local models work across a corpus-scale source library by reopening verified evidence instead of bluffing from summaries.
- **Self-improvement on your hardware**: ORC ACADEMY closes the loop from reviewed swarm plans to a better boss adapter.

If Copilot is a better autocomplete, TheOrc is trying to become a better local AI workbench.

---

## Where the project is right now

TheOrc is a production local AI orchestrator, not a swarm experiment. The major subsystems are built, shipped, and verified in the running app:

- **Avalonia-only desktop shell** — one cross-platform codebase. WPF has been removed entirely.
- **Native runtime** — in-process local inference is a first-class runtime lane, with automatic Ollama fallback on any fault.
- **HIVE MIND** — distributed worker and campaign execution across enrolled machines, secured by a full cryptographic identity layer.
- **ORC ACADEMY** — the self-training loop that turns reviewed swarm plans into a better boss model; the shipped `theorc-boss:gemma4-ft` adapter scores 99.3% on structured planning.
- **TheOrc Foundry** — the specialist-model pipeline; its first model, `theorc-toolcaller`, is trained, benchmarked, and deployed.
- **Context Fabric — complete.** CF-0 through CF-8 have landed on `master`, and the CF-7 benchmark gate closed at **GO** on an honest un-marked corpus (2026-07-17). Source-grounded memory across corpus-scale libraries is now a proven, measured capability. See [The Orc Context Fabric.md](docs/The%20Orc%20Context%20Fabric.md) for the authoritative per-phase status.

With those foundations in place, the project's focus now shifts to **v2.0** — see [The road to v2.0](#the-road-to-v20) below.

---

## Meet the Warband

<div align="center">

![Goblin Swarm](Assets/goblin%20swarm.png)

</div>

TheOrc is the boss. He reads your goal, writes the plan, and keeps everyone pointed in the right direction. The rest of the swarm handles execution — in parallel, surprisingly fast, and with a work ethic that would shame most interns.

| Role | What they do |
|---|---|
| **TheOrc** | Reads your goal, writes the plan, routes each task to the right goblin |
| **Researcher** | Digs through docs, APIs, and libraries — never touches production code |
| **Coder** | Writes the actual implementation using whatever the Researcher found |
| **UIDeveloper** | Handles all the UI work — Avalonia XAML, HTML/CSS, styles |
| **Tester** | Runs tests and reads logs — read-only, no write access, very trustworthy |

The boss model is a **fine-tuned local Gemma 4 12B** (`theorc-boss:gemma4-ft`) — trained by TheOrc's own pipeline on 900 reviewed swarm plans. It scores **99.3%** on structured planning evals. We made the AI smarter by feeding it examples of itself doing a good job. Yes, really.

---

## How it fits your day

<div align="center">

![TheOrc at work](Assets/badge1.png)

</div>

TheOrc runs **beside your IDE**, not inside it. Keep VS Code, Visual Studio, or whatever you're used to — TheOrc doesn't care. It just needs a folder to work in.

```
1. Point TheOrc at a workspace folder
2. Describe what you want built
3. Watch the swarm plan and execute in real time
4. Review every file and command before it lands — approve, reject, or redirect
5. Commit the result from your normal editor like nothing happened
```

Nothing gets written, no shell command runs, and no git operation executes without going through the approval flow you configure. You're always in the loop. The goblins are enthusiastic but not unsupervised.

---

## vs the tools you're already paying for

| | GitHub Copilot | Cursor | ChatGPT | **TheOrc** |
|---|:---:|:---:|:---:|:---:|
| Runs locally | ❌ | ❌ | ❌ | ✅ |
| Your code stays on your machine | ❌ | ❌ | ❌ | ✅ |
| Multi-agent parallel execution | ❌ | ❌ | ❌ | ✅ |
| Writes files autonomously | ❌ | Partial | Copy-paste | ✅ |
| Monthly cost | $10–19 | $20 | $20 | **$0** |
| Can train its own boss model | ❌ | ❌ | ❌ | ✅ |

TheOrc is not trying to replace your editor. It's the AI **project runner** that sits next to it and does the parts that were never fun to do yourself.

---

## Flagship system: Context Fabric

**Context Fabric is TheOrc's answer to the "finite model, large corpus" problem** — a source-grounded memory fabric that stores durable artifacts, reopens evidence on demand, and keeps every accepted claim tied back to its source.

The user experience is the point: OrcChat behaves as though it has a far larger memory than the active model's live context window. The source corpus stays on disk, parsed into stable addresses with hashes and provenance. The model receives a budgeted working set for the current question, and when an answer needs proof, Context Fabric reopens the original source, verifies the quote and range, and shows the citation — instead of pretending the whole book fit inside one prompt.

**As of v1.13.0, the full CF program is complete and the benchmark gate is closed at GO:**

- **CF-0 through CF-8 have landed on `master`** — native feasibility, deterministic ingestion, graph-backed retrieval, native readers and boundary stitching, hierarchical reduction and budgeting, OrcChat Library citations and source opening, distributed HIVE readers, benchmark-gate contracts, and hard-ingestion (structured formats, OCR contracts, immutable versions, cache policy, vector fallback, cross-corpus/CodeGraph links).
- **The CF-7 benchmark gate closed GO on an honest, un-marked corpus (2026-07-17).** On `cf-expanded-book-v1` (128 segments, 120 real held-out questions), Qwen3.5-9B answered **104/120** questions at **97.1%** citation precision with **128/128** segments read — against a best competing baseline (closed-book, truncated-prompt, top-k RAG) of 52/120, all on GPU-verified native inference.
- **This GO replaces, and does not merely re-confirm, an earlier one.** A 2026-07-03 GO was later found to have run against a corpus containing marked/leaked evidence lines; that result was retracted and the corpus rebuilt un-marked. The v1.13.0 result is the honest replacement — the kind of correction the project's [truth-audit discipline](docs/AI_DEVELOPMENT_DISCLOSURE.md) exists to force.
- **The million-token product proof has run and passed:** a 1.82M-token deterministic corpus processed unattended on one node — 640/640 segments, 5/5 verified questions including a 640-citation exhaustive enumeration, a 563× source-to-working-context ratio on an 8K native context, zero Ollama involvement.
- **Still not oversold:** standardized LongBench / LongBench v2 subset runs and full multimodal page understanding remain future benchmark and product work, not claims of this release.
- The public benchmark lane has a name: **The Independent Mind Corpus** — a shelf built around works that stress evidence, liberty, literacy, institutional design, strategy, and source-grounded truth.

Context Fabric is what makes TheOrc more than "an AI swarm for code": a local system for reading, checking, and reasoning across source material without pretending the model remembered the whole shelf.

---

## Flagship system: Native Runtime

**Ollama is a great piece of software, and TheOrc doesn't need it.** That's the whole pitch: instead of installing a separate server, managing its lifecycle, and talking to it over HTTP for every single message, TheOrc can load a model straight into its own process and run it directly — no subprocess, no local server to keep alive, no round trip. Ollama remains fully supported (it's still the default, on purpose — more on that below), but the native path is real, fast, and getting more real every week: **a genuine CUDA build measured 67.7 tok/s on an RTX 4060, versus ~6 tok/s CPU-only** — the difference between a tool that keeps up with you and one that doesn't.

**The hard part was never "load a GGUF file and generate text."** Plenty of hobby projects do that. The hard part is doing it the way production software has to: never crash the GPU by overcommitting memory, never let a user-visible failure turn into a silent, unexplained personality-swap to a different backend, and never let one AI role's bad moment take the whole system down with it.

- **It won't load a model it doesn't have room for — and it doesn't guess.** Before anything loads, TheOrc reads the model's own file header and computes a byte-exact prediction of how much GPU memory the request will actually need — cache, compute buffers, everything — then checks that against real, live-queried VRAM headroom. If it doesn't fit, the request is refused with a clear reason. No silent overcommit, no crash, no "it usually works."
- **The prediction gets checked against reality, every time.** Once a model is actually loaded, TheOrc reads the exact allocation numbers straight out of the inference engine's own logs and reports *measured* memory usage, not a theoretical estimate — the same discipline a production monitoring system holds itself to, applied to a desktop app.
- **A broken request never lies about what happened.** If native inference can't serve a request, it fails loudly and explicitly — it does not quietly hand the conversation to Ollama behind your back and pretend nothing happened. Silent backend swaps are exactly the kind of thing that erodes trust in a tool, so TheOrc simply doesn't do it. This is a tested guarantee, not a policy on paper: a dedicated always-on check proves it on every single build.
- **Found a real bug by actually looking, and fixed it properly.** Cancelling a generation partway through turned out to be able to permanently wedge that AI role until restart — a genuinely nasty class of bug that only shows up under real, adversarial testing, not casual use. It was caught by a test built specifically to try it, root-caused, and fixed by reusing an existing, already-proven recovery mechanism rather than a quick patch. Verified fixed, repeatedly, on real hardware.

**Where it stands today:** the native path is real, fast, measured, and — as of this release — has a full recorded proof of one complete real-model run: discovery, a genuine capacity check against live GPU memory, model load, real inference, and a full telemetry snapshot, all in one pass, with the results saved to disk. Ollama stays the default and the safety net until that same rigor has been proven not just on one machine, but across a real multi-machine HIVE fleet — see [the road to v2.0](#the-road-to-v20) below for exactly what that bar is.

---

## Flagship system: ORC ACADEMY — the swarm teaches itself

Here's the part that gets genuinely weird in the best way: **TheOrc trains itself.**

Every good swarm run captures the boss's plan. Those captures go through a review pipeline. When you have enough reviewed examples, ORC ACADEMY trains a LoRA adapter on your own GPU — no cloud training service, no API bill. The new boss model is better at planning the next run. Which produces better captures. Which trains a better adapter. You get the idea.

**v1 shipped — and it's the one still running today.** 900 reviewed boss plans, harvested overnight while the machine sat idle, trained locally in 148 minutes on a single consumer GPU. Result: **99.3% structured planning pass rate**, up from 94.5% on the un-tuned base model. It shipped as a 125 MB adapter file anyone running TheOrc can pull today.

**And here's the part most projects would never put in a README: v2 made things worse, and we said so.** A later training run used nearly twice the data — and a quiet labeling mistake taught the boss the exact wrong lesson for half of it. The result measurably regressed: planning quality dropped from 99.3% down to 77.8%. We caught it, retired the adapter, and kept the original v1 in production rather than ship a worse model because it was newer. **v3 fixed the root cause** — a data-quality gate that now catches that exact contamination before a single GPU-hour is spent on it — and re-ran clean. It still didn't clear the bar to replace v1, so v1 remains the production adapter. No spin, no "technically an improvement somewhere" — the number simply has to go up, or the new model doesn't ship.

That standard — **a new model earns its spot, or it doesn't deploy** — is the whole reason this loop is trustworthy instead of just a fun toy. *Run → capture → review → gate → train → deploy* is part of the product, not a research side-quest, and every step of it happens on your own hardware. Nothing about how TheOrc gets smarter requires sending your data anywhere.

### Pit Boss — making it self-serve

Training your own model sounds like a research project. ORC ACADEMY plus **Pit Boss** makes it a form.

Tell Pit Boss what you want the swarm to get better at — eight questions about goal types, languages, edge cases, and how many examples you want — and it turns your answers into a structured training plan, kicks off dataset generation, and hands the finished dataset to ORC ACADEMY for training on your own GPU. You go from "I want a smarter boss" to a queued training run without writing a script or touching a command line.

---

## The road to v2.0

With Context Fabric complete, v2.0 is about two things: making the **native runtime the default**, and giving agents **real operational reach** beyond generating text. Four workstreams define the release. None of these are claimed as shipped — this is what the project is building next, in priority order.

### 1. Native Runtime becomes the default
The defining change of v2.0. Local in-process inference — already a real, verified runtime lane — is promoted from opt-in to the default path, with Ollama becoming fully optional rather than the assumed backend. The `RuntimeOrchestrator` / `AdapterManager` / `OrcScheduler` layer (per-role persistent LoRA contexts, VRAM-budget admission control, automatic fallback) graduates out of experimental status. This flip is **gated on multi-machine HIVE validation across a real LAN/Tailscale network** — a measured bar, not a calendar date.

### 2. Browser automation + page understanding
The highest-impact new capability: agents that can drive a real browser and understand what they see. Playwright-backed navigation, interaction, and page comprehension turn "read the docs" and "check the live site" from hand-offs into tasks a worker completes itself — under the same approval gates as every other tool.

### 3. Workspace + shell intelligence
Sharper hands on the local machine: fast ripgrep-backed search, safe structured file I/O, real diffs, and bounded build/test/shell execution. Every command still flows through the operator approval flow — the goal is capability with control, not an unsupervised shell.

### 4. Multimodal intake + artifact export
Closing the loop on documents. Image and OCR intake (Tesseract plus multimodal native chains) lets the swarm read scanned and visual sources; Pandoc-backed export turns results into polished `.docx`, PDF, and HTML deliverables instead of leaving everything as raw markdown.

**Beyond v2.0**, the longer-term direction is a daemon-centric HIVE: the headless Warband daemon becomes the canonical node on every machine, collapsing today's dual GUI/daemon stack into one. See [ROADMAP.md](docs/ROADMAP.md) for the full picture.

---

## Historical release notes

Everything below this line is preserved release history. The sections above describe where the project is now and where it's going; the sections below describe what changed at each tagged release.

## What's new in v1.13.0

**Context Fabric closes its benchmark gate at GO — honestly.** The CF-7 gate reached **GO** on `cf-expanded-book-v1`, a 128-segment un-marked corpus with 120 real held-out questions: Qwen3.5-9B answered **104/120** at **97.1%** citation precision with **128/128** segments read, versus a best competing baseline of 52/120. Critically, this run *replaces* an earlier 2026-07-03 GO that was found to have scored against a corpus containing marked/leaked evidence lines — that result was retracted, the corpus rebuilt clean, and the gate re-run from scratch. Two independent bugs surfaced during live validation and were fixed to reach the honest verdict: the open-extraction reader was silently dropping facts on dense filler-heavy segments (now backed by a completeness-repair pass), and the exhaustive-leaf-coverage gate was checking a stale whole-corpus assumption left over from the old 16-segment fixture (now scoped to each question's own segment set).

**A stricter NoKvSlot admission fix underneath the gate.** The §7e prompt-overflow fix (exact-token admission) closed a class of `Qwen3.5` KV-cache exhaustion crashes that had been corrupting benchmark scores — runs are now checked for `NoKvSlot` before their numbers are trusted, and Qwen3.5-Q8_0 is the new best-performing configuration on the gate.

**Cost-tiered external code review.** The `grok-review` tooling was reworked into four explicit modes — `quick` (default, cheap, latest commit), `diff` (pre-commit uncommitted check), `full` (PR-scope with repo reads and project conventions), and `adversary` (a red-team pass that hunts what the prior reviewer missed) — with a real `-PR <n>` flag, corrected tool-policy enforcement, and a verdict parser hardened against both narration-glued findings and fail-open false-CLEAN results. The tool proved itself during its own review, catching three real regressions in its own pull request before merge.

## What's new in v1.12.0

**TheOrc Foundry ships its first specialist model.** `theorc-toolcaller` — Qwen2.5-1.5B fine-tuned to propose the correct tool call (or correctly refuse) from a worker's role, its available tools, and a natural-language request — went from spec to a promoted, benchmarked, deployed model in one release cycle. The full pipeline landed in the Training Pit: a synthetic dataset generator with decision-type balance guarantees, a gated LoRA training runner (config-driven, immutable run manifests, GPU exclusivity enforced against every other Foundry consumer), and a new Stage 4 **ARENA** panel that benchmarks decision accuracy, tool precision, and per-class F1 live against a sealed 260-example held-out set.

**A statistically honest refusal benchmark, not a vibe check.** Alongside the sealed Arena set, a new **Refusal Gauntlet** generates thousands of deterministic adversarial cases across six failure families — foreign tools, out-of-role requests, near-miss tools, prompt injection, missing arguments, and ordinary no-tool conversation — and scores them with exact Clopper-Pearson confidence bounds and paraphrase-consistency checks, so the reported number is the defensible lower bound, not an optimistic point estimate. The gap it found got closed: retraining from r2 to r3 raised sealed-eval decision accuracy 97.3% → 98.5%, and gauntlet safety (never fabricating a tool call) 90.3% → 98.3% on held-out phrasings the model never trained on.

**The trained specialist is live, opt-in, and learning from real use.** `theorc-toolcaller:qwen25-1.5b` is deployed via Ollama and wired into the Swarm worker loop as an opt-in repair lane — when a worker's response contains no parseable tool call, the specialist gets one shot at proposing one before the turn falls through to today's behavior, still gated by the same deterministic tool-policy engine as every other call. Real usage now feeds the next training round from two organic sources, both off by default under one settings toggle: Swarm tool-call decisions, and — new this release — OrcChat single-agent chat decisions, captured under a wider "v1" tool inventory that's a deliberate sibling to Swarm's frozen six-tool set, not an edit to it.

## What's new in v1.11.3

**Context Fabric's NoKvSlot mystery is finally closed — and it isn't TheOrc's bug.** A months-old class of infrastructure crashes on `Gemma-4-12B` runs turned out, after direct A/B elimination of every leading theory (cross-conversation KV exhaustion, SWA cache sizing, force-recycle timing), to be a Gemma-4-specific limitation in upstream `llama.cpp`, not a defect in TheOrc's own runtime. `Meta-Llama-3.1-8B` and `qwen2.5-coder-7b` show **zero** `NoKvSlot` occurrences across 500+ combined benchmark questions on three independent machines — the crash is isolated to one model family, and Context Fabric's own retrieval/reduction mechanics are proven sound underneath it.

**Retrieval quality climbs from 31% to a validated 48%+ pass rate through three deterministic fixes, not guesswork.** Each tier targets a specific, measured failure mode found by categorizing every B3 miss against the evidence actually supplied to the model: **Tier 1** fixes multi-word entity names dissolving into common-word noise (bag-of-words scoring couldn't tell "Station Alpha" from any card containing "station" and "alpha" separately); **Tier 1.5** adds proximity-pair matching for paraphrased questions that invert entity word order; **Tier 2** stops silently rejecting oversized evidence cards and truncates them instead. Cumulative effect on the 100-question suite: pass rate 31→45→56, pure retrieval misses 49→21→10, B3 now clearly beats a conventional top-k RAG baseline for the first time.

**Tier 2.5 closes two more real gaps and finds the honest edge of what's left.** Multi-hop chain questions were failing because the greedy evidence-fill spent its whole token budget on distractor segments that share an entity name with the question but belong to an unrelated fact chain — a reference-chasing pass now follows the chain's own shared identifiers into the linked segments, with 30% of the evidence budget reserved specifically so it always gets a turn. Measured live: full-retrieval MultiHop cases rose 6→9 out of 24. Separately, root-caused and fixed the long-standing boundary-stitch failure (Meta-Llama emits its `linkedFacts` field as JSON objects instead of strings — a tolerant parser now handles both) and closed a citation-precision shortfall, both now passing their gates and holding stable across five independent live validation runs. What's left is honestly reported, not hidden: the remaining MultiHop gap is a model-instruction-compliance ceiling (the model under-citing a multi-part chain answer), not a retrieval defect — logged with three scoped next options rather than claimed as solved.

**Model Benchmark window (Phase 1) and safer benchmark tooling.** A new read-only panel under Models → Model Benchmark… surfaces every model's CF-7 benchmark history — GO/NO-GO verdicts, question pass rate, citation precision, segment coverage — scanned automatically from `.orc/adversarial/` artifacts. The benchmark CLI also gained `--model`/`THEORC_CF_MODEL` pinning so a shared model depot changing mid-run (a model toggled on or off by unrelated work) can no longer silently swap which model a benchmark run actually measures, plus verbose preflight diagnostics that print every candidate model's admission verdict and any disabled GGUFs on rejection.

**Training Pit tab redesign.** The dataset/training dashboard now scrolls as one page (a right-side scrollbar reaches every section regardless of window size), and the three pipeline stages — Generate Dataset, Orc Academy, The Foundry — are laid out side by side as numbered, color-coded cards instead of stacking as individually-expandable accordion sections. Inventory tiles (datasets/adapters/models) now read as stat cards with a colored accent bar and a large count.
**Known issue:** opening a workspace folder while the Training Pit tab is active can trigger a native stack-overflow crash under investigation — isolated reproduction attempts (real window, real repo data, multiple layout widths) have not yet caught it live; tracked for a follow-up patch.

## What's new in v1.11.2

**Context Fabric becomes real product surface, not just architecture.** CF-0 through CF-8 are now landed on `master`: deterministic ingestion, document graph retrieval, native readers/reducers, OrcChat Library attachment, verified citation labels, citation popup/source opening, distributed HIVE readers, benchmark-gate contracts, and hard-ingestion support for structured formats, OCR contracts, immutable versions, cache policy, vectors, and cross-links. The honest caveat stays intact: unattended million-token/LongBench runs and full multimodal page understanding are future benchmark/product work, not release claims.

**Phase 3B native campaign engine ships.** The HIVE MIND can now coordinate native-runtime campaign work instead of pretending distributed shell access is the product. This release lands the first full campaign-engine slice: typed campaign/work-unit contracts, capability-aware leasing, content-addressed model and artifact storage, worker-side native execution plumbing, verifier-oriented result metadata, and the first showcase packs including **Native AI Eval Factory** and **Alien Signal Search**.

**OrcChat grows into a real modern chat surface.** Chat now has a first-class tool pack built around the workflows people actually use in web chat: web search, page fetch, URL fetch, workspace browse/search/read/write, outline, test runs, and markdown document generation. It also gains **file attachments** and **image attachments**: images are carried through the multimodal message payload, text-like attachments are inlined into the prompt, and generated markdown docs come back as clickable links right in the conversation.

**Release-quality native/runtime plumbing behind the scenes.** The shared native runtime now sits in its own cross-platform project, headless agent-loop work can run without Ollama, and campaign/worker execution uses the same native path instead of an Ollama fallback. The release pipeline and tests were expanded around this path, and the final pass verified the OrcChat attachment/tooling slice plus the Phase 3B unit suite before tagging.

## What's new in v1.11.0

**The HIVE MIND screen, redesigned.** The constellation is now a living neural-swarm view drawn by a new immediate-mode renderer: each node is a **role-shaped silhouette** — a crowned hexagon for the Warchief, diamond for Coder, circle for Researcher, rounded square for UIDeveloper, triangle for Tester — with glow, a breathing core, and signal particles (all frozen by Lite Mode for thin machines). A new **metrics rail** shows nodes online, aggregate VRAM, and a role legend (live where the data exists, clearly-marked demo where the telemetry backend is still to come). **Left-click a node** for a detail panel; **right-click → ⬡ Set role** to assign HIVE roles and worker lanes. The rail is **resizable** (drag the splitter) and can be **moved to either side** (⇄), and your layout choice persists.

**Remote task dispatch to a Warband.** You can now dispatch a task to a headless HIVE node from anywhere via a new authenticated `POST /hive/tasks/submit` endpoint, with a configurable `WarchiefUrl` so a Warband can pull from a remote Warchief. Found and fixed the blocker that made this impossible: a worker polling its own queue had no way to authenticate to itself — same-machine callers are now trusted (the established `req.IsLocal` pattern), so a submitted task actually gets claimed and run. Verified end-to-end on a real Raspberry Pi.

**Cleaner, self-healing constellation.** A machine reached over both LAN and Tailscale now shows as **one node, not two** (dedup by identity, with automatic address fallback so it stays reachable whether you're home or roaming). The Tailscale scan only adds devices actually running TheOrc, so phones and other tailnet devices stop appearing as phantom nodes. Paired headless nodes now show up automatically.

**HIVE repair that actually un-sticks a split fleet.** When machines ended up in separate hives, there was no way to merge them — and no escape. Now the repair wizard shows your current hive up front and offers **"Leave the current hive and join"** in one click (keeping your keys and paired peers), plus a standalone **🚪 Leave current hive**. This is how you pull a machine out of its own hive and into your main one.

**Warband CI + Docker.** The release pipeline now publishes the headless `theorc-warband` binary for `linux-x64`/`osx-arm64`, and ships a `warband.compose.yml` Docker template.

## What's new in v1.10.0

**OrcChat: uncensored multi-backend chat, built from scratch in C#.** A new chat surface — model-agnostic backend routing, streaming, user-controlled generation params, no frontend content filtering, no injected system prompt by default. Three uncensored Dolphin-line models added to the model catalogs with an UNCENSORED badge (opt-in only, never auto-recommended). Date/time grounding, a persisted system prompt across restarts, a live context-window usage indicator, HIVE node routing to run a chat on a paired machine, and inline image rendering in markdown output (`![alt](src)`, http(s)/data:/local-file, background-thread decode).

**Native runtime: real, working, and now actually reachable.** The in-process LLamaSharp runtime, `ModelDepot`/`SessionManager`/`AdapterManager`/`RuntimeOrchestrator`/`OrcScheduler` VRAM-aware admission control — all of it was already implemented in prior releases, but two real opt-in paths (the llama.cpp server backend, and an experimental native main-chat mode) had zero Settings UI to actually turn them on. Both are now exposed, with automatic fallback to Ollama on any native failure. Verified on real hardware: a genuine CUDA build hit 67.7 tok/s on an RTX 4060, vs. ~6 tok/s CPU-only.

**Found and fixed a real OrcChat bug on the first real end-to-end test against the new backend:** tool definitions were serialized in the wrong wire shape — Ollama silently tolerated it, llama.cpp's stricter OpenAI-compatible server rejected it outright with a 500. Fixed; verified both single-turn and multi-turn conversations now work correctly against a local llama.cpp server with zero Ollama involvement.

**Model downloader hardening.** Downloads now auto-retry with resume on a dropped connection instead of silently stalling. SHA-256 verification — previously implemented but never actually wired up, since nothing fetched a hash to check against — now runs for real using HuggingFace's own LFS metadata, deleting a corrupted download before it gets registered as a usable model.

## What's new in v1.9.5

**The installer now genuinely targets three OSes, not one.** `OrchestratorSetup` was rewritten from a Windows-only WPF wizard to a cross-platform Avalonia app (Phase 1), with every OS-coupled action — hardware detection, firewall, launchers, uninstall — moved behind one `IPlatformInstaller` interface with real Windows, Linux, and macOS implementations (Phases 2, 4, 5). This release closes the gap those phases left open: nothing upstream of the installer's own logic could actually hand a non-Windows machine a real binary to install. `release.yml` now publishes a macOS (`osx-arm64`) build alongside Windows; the model manifest, the llama.cpp runtime resolver, and three separate spots in the *running app* (the update checker, self-updater, and llama-server launcher) all needed their own fixes — each only ever recognized Windows binary names, which would have shipped a Mac install that completes successfully and then can't update itself or find its own runtime.

**Headless HIVE nodes are real now too.** `OrchestratorIDE.Daemon` (`theorc-warband`) is the cross-platform, no-GUI HIVE node — first deployed to actual ARM64 hardware this release (a Raspberry Pi 4), running as a systemd service. It gained `--pair`/`--show-identity` CLI modes (same fingerprint-gated safety contract as `swarmcli`'s) so a headless box can join an existing HIVE without ever needing a display.

**Found and fixed a HIVE reachability gap no existing diagnostic caught:** a node can have its URL ACL reservation and firewall rules all correctly in place and *still* be completely unreachable to peers, because the network interface a peer actually connects through was classified "Public" by Windows — the firewall rules are deliberately Private-profile-only, so they silently never apply there. Added `HiveNetworkEnroller`, shared between the installer and the app itself, with the missing diagnostic (`FindPublicInterfacesAsync`) and a one-click fix.

## Looking ahead to v2.0

v2.0's defining change: **Native Runtime becomes the default, Ollama becomes fully optional.** That flip is explicitly gated on multi-machine HIVE MIND validation of this release's native opt-in path across a real LAN/Tailscale network — not a fixed date. Also planned, not yet started:
- Promoting the experimental `RuntimeOrchestrator`/`AdapterManager`/`OrcScheduler` layer out of opt-in status once the v1.9 HIVE testing round validates it under real concurrent multi-role load.
- HIVE MIND Phase 3B — full multi-step `AgentLoop`-style tool execution on remote workers (file writes, shell commands, web search running on the worker machine itself), not just single-pass LLM calls.
- A first-class container/GHCR release lane for Warband deployments. The raw `linux-x64` and `osx-arm64` Warband binaries now ship in GitHub Releases; container publishing is the remaining deployment-product step.
- A from-scratch, data-bound Avalonia rebuild of the Model Wiki/catalogue browsing experience retired in v1.9.0.

## What's new in v1.9.4

**HIVE_MEMBERSHIP_SPEC.md, all four phases.** A hive-wide `HiveId` that survives Warchief elections (unlike per-node identity); membership certificates so a node can prove hive membership to a peer it never directly paired with (avoids O(n²) manual-approval pairing at "100s of nodes" scale); an authenticated role-assign RPC + "👑 Declare this machine Warchief" UI action; and a first-run/repair discovery wizard (scan the LAN, group results by `HiveId`, join an existing hive or found a new one) that now runs automatically on a fresh HIVE-enabled install instead of leaving the node stuck at `HiveRole.Unset` until pairing assigned one as a side effect. One intentional remainder: presenting a membership cert at the request-time auth gate needs its own signature scheme, not a bolt-on — issuance and verification shipped, wire-gate consumption didn't.

**The HIVE constellation view animates now** — a small spark travels along each active peer connection (amber outbound, green inbound), the center node breathes with a slow pulse. A new "Lite Mode" setting turns it off for weaker hardware, live-toggleable without leaving the Hive tab.

**"Enable HIVE MIND" is a real Settings toggle now**, not JSON-only. `AppSettings.HiveMindEnabled` had gated startup the whole engagement, but no control ever read or wrote it — the only way to turn it on was hand-editing `settings.json` with the app closed, which didn't survive the next relaunch's fresh write.

**A cluster of real bugs found during live multi-machine testing, each fixed as found:**
- Firewall rules were accumulating duplicates on every install/repair run (an unconditional unelevated "delete" that needs elevation too, silently failing every time).
- A stale downloaded app exe could get permanently skipped on upgrade, or — worse — deleted on a failed download with nothing left in its place.
- Pairing dialogs and Activity-feed entries for pairing actions were silently not showing at all in the GUI (a missing `ConfirmAsync` wire-up, and an event the panel never subscribed to).
- `HivePairingClient`'s approval poll used case-sensitive JSON deserialization against a server that sends camelCase — every poll silently missed and reported "pending" forever, even after the peer had genuinely approved within seconds.
- The constellation view conflated "Ollama reachable" with "HIVE port reachable" — a node could show fully green/online while having no running HIVE node server at all, the only symptom being a confusing 10-second timeout when actually trying to pair.
- Auto Tester was silently skipped after a retry-recovered write in the swarm pipeline.
- The native LLamaSharp runtime's chat template had `AddAssistant` set to the wrong value, breaking output for that opt-in path.

**`swarmcli` additions**: `--native` (run a full `SwarmSession` against the native runtime instead of Ollama), `--native-test` (headless equivalent of the GUI's "Run Native Test"), `--warchief --no-run` (start pairing/queue-server only, no goal execution).

## What's new in v1.9.2

**Pairing actually works now.** Validating v1.9.1's reachability fix across three real machines surfaced the next real gap: pairing — the step where two machines agree to trust each other — never had a way to actually *start*. The "approve" side was fully built, but nothing in either UI ever called the endpoint that initiates a request. Added a "Pair with this node" action, building the missing initiator side end to end.

This went through real adversarial review (two independent AI reviewers, multiple rounds) before shipping, and it caught genuine problems with the first draft:
- The approval-polling endpoint is unauthenticated by design, and the first version trusted a new peer as soon as it got an "approved" response — before the one real check (a human comparing a fingerprint between the two machines) ever happened. Fixed: trust isn't written until the operator explicitly confirms the fingerprint matches.
- A newly-paired peer was being granted enough standing to become eligible for real Warchief authority later, regardless of whether it should be. Capped to a safer default.

Also: the code-review tooling used throughout this project kept falsely flagging legitimate security-related code as a risk and refusing to review it. Switched the underlying model.

## What's new in v1.9.1

**HIVE MIND actually reachable across machines now.** v1.9.0's fix made the node server start without crashing; it didn't make it *reachable*. Real multi-machine testing found the fix's own fallback path — binding `localhost` only when the wildcard bind fails — was itself the unfixed problem: a localhost-only listener is invisible to every other machine, with no error shown.

**Root cause:** binding the wildcard prefix as a normal, non-admin process requires an **http.sys URL ACL reservation** (`netsh http add urlacl`). Nothing ever created one. Fixed: the installer's HIVE enrollment step now reserves URL ACLs for both HIVE ports alongside its firewall rules, batched into a single UAC prompt. `HiveTaskQueue` had the identical bug as `HiveNodeServer`, fixed the same way. The gated Phi-4 Mini boss-model download now points at a working mirror.

If you installed v1.9.0 and HIVE MIND only ever discovered other nodes one-directionally (or not at all), this is why.

## What's new in v1.9.0

The biggest cutover since the Avalonia migration started: **WPF is gone.** `OrchestratorIDE/OrchestratorIDE.csproj` and every WPF-only window, dialog, panel, and control were deleted outright — not archived, not stubbed, deleted. Avalonia is no longer "primary," it's the *only* desktop shell. `ModelWikiWindow`/`ModelCompareWindow` were retired rather than ported (their data layer stays; a from-scratch, data-bound rebuild is a real future feature, not a blocker). Everything operators actually use day to day — `ask_user`, the first-run wizard, sandbox bypass, self-update, model library/downloader, workspace/global agent rules — already had a real Avalonia home going into this release. Shared service code (`Core/`, `Services/`, `Models/`, `Trust/`) is untouched; only the WPF-exclusive UI layer is gone, which is what made deleting an entire desktop framework in one night tractable at all.

**CodeGraph v1** — a Roslyn + SQLite code knowledge graph lets the agent query structure (callers, callees, complexity hotspots, architecture) instead of grepping files for every question. Five tools wired into the swarm; lifecycle-managed (background re-index on workspace open); ships with a `codegraph-query` dev skill for low-token structural lookups against the underlying SQLite DB directly.

**Native Runtime, two layers, both real:**
- The **installer already defaults fresh installs to a local llama.cpp server** instead of Ollama (`IModelRuntime` → `LlamaCppServerRuntime`) — this isn't new in v1.9, but it's worth being explicit about: most new installs of TheOrc have never required Ollama at all.
- **New this release, experimental and opt-in:** an in-process LLamaSharp runtime (no server process), `AdapterManager` for per-role persistent LoRA contexts with hot-swap, `RuntimeOrchestrator` tying both runtimes together, and `OrcScheduler` doing real VRAM-budget admission control so concurrent roles don't blow out a GPU. The first live path is an experimental HIVE-worker / main-chat opt-in (`ExperimentalNativeHiveWorkerEnabled` / `ExperimentalNativeMainChatEnabled` in Settings) with automatic fallback to Ollama on any runtime fault. Main chat, research chat, and SwarmSession stay on the configured default runtime unless explicitly opted in.

**HIVE MIND — fixed a release-blocking startup bug.** A pre-release smoke test caught `HiveNodeServer.Start()` silently failing on every normal-user install: a failed wildcard `HttpListener` bind (needs admin rights / a URL ACL reservation no normal process has) left the listener disposed internally, and the fallback cleanup code's own property access threw a second exception that masked the first inside an unobserved background task. Net effect: enabling HIVE MIND did *nothing* — no error, no log line, nothing listening — on every machine that wasn't running elevated. Fixed and verified live (`/hive/info` returns 200, UDP beacon confirmed listening). This is exactly the kind of bug `dotnet test` can't catch, since it requires a real socket bind; found by actually launching the built app before shipping it.

**Training Pit suitability gate** — a deterministic pre-training check that blocks write-task examples mislabeled into TESTER-lane roles before they reach the trainer — the exact contamination pattern that regressed ORC ACADEMY v2 (51.3% of v2's examples had this mislabeling).

**Infrastructure fixes that came out of actually shipping this release:**
- `.github/workflows/ci.yml` was still building the now-deleted WPF project — every push had been failing CI since the WPF deletion commit landed. Fixed, and CI now runs both test suites on every push instead of just build-checking.
- `.github/workflows/release.yml` was still publishing and packaging the WPF build as the actual downloaded artifact. Fixed to publish Avalonia with the same output filename, so every downstream consumer (installer, self-updater) keeps working unchanged.
- `OrchestratorIDE.Daemon` (the headless cross-platform HIVE node, `theorc-warband`) had been failing to build since before this release cycle started — a dependency on the heavy native LLamaSharp stack that a lightweight daemon shouldn't need. Decoupled via a new `IHiveNativeRoleExecutor` interface; the daemon now builds clean without pulling in native-runtime dependencies at all.
- Internal dev-only docs and scratch files (`.grok/` specs, prompts, spike code; loose planning notes) stopped being published to GitHub — they're still on disk for development, just not part of the public repo going forward. `README.md`, `SECURITY.md`, `LICENSING.md`, `CLA.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, and `.grok/PROJECT_TRUTH.md` stay public.

---

## What's new in v1.8

### v1.8.0 — Avalonia markdown renderer + first test suite

The Avalonia cross-platform shell gets its first real renderer and its first test coverage.

**Phase 6 — Native Markdown Renderer (`MarkdownView`)**

Assistant responses in the Avalonia shell now render in rich Markdown — zero new NuGet dependencies. The renderer maps directly to Avalonia's native control tree: headings, bullet and numbered lists, fenced code blocks, blockquotes, inline bold/italic/code/links. It's streaming-safe: an `IsVisible` guard short-circuits rebuilds during token streaming and triggers a single deferred render when the response is complete.

**Phase 7 — First Avalonia test coverage**

| Suite | Tests | What it covers |
|---|---|---|
| `MarkdownViewTests` | 12 | Block/inline parse → control tree; streaming deferred-render guard |
| `PanelConstructionTests` | 10 | Every migrated panel constructs headlessly (AXAML, compiled bindings, resources) |
| `T20 AvaloniaSmokeTests` | 1 | FlaUI: launches the Avalonia exe, asserts the main window appears via UIA |

142 automated tests green across WPF unit, Avalonia headless, and FlaUI smoke.

**Pit Boss hardening** — 10+ rounds: Hermes-3-Llama-3.2-3B default (2 GB, VRAM-safe), two-phase JSON synthesis, shell injection prevention (`ArgumentList` not shell string), cross-platform gen subprocess, concurrent log-write lock, path-traversal guard.

**ORC ACADEMY v2 dataset finalized** — 1,784 train / 200 eval, registered as `train_v2gold`/`eval_v2gold` for Forge. Training run pending.

---

## What's new in v1.7

### v1.7.0 — Avalonia cross-platform UI (Phases 0–5)

v1.7 is the biggest architectural shift since launch: TheOrc now ships **two UIs on one codebase**.

The new Avalonia shell (`net10.0`, no `-windows` suffix) runs on Windows, macOS, and Linux. The WPF shell remains the primary shipping app; Avalonia runs side-by-side against the same Ollama backend, the same HIVE mesh, and the same workspace.

| Phase | What landed |
|---|---|
| **Phase 0** | Blank Avalonia 12 shell — AppBuilder, dark theme, brand colours |
| **Phase 1** | Service layer decoupled — `OllamaClient`, `SwarmOrchestrator`, `HiveService` etc. extracted to shared project; both UIs wire against the same interfaces |
| **Phase 2** | Code editor + tool editor ported to `AvaloniaEdit` |
| **Phase 3A** | Simple panels: File Explorer, Settings, Checkpoint Browser, Session Browser |
| **Phase 3B** | Agent panel, Chat panel, Update panel, Warm-up editor |
| **Phase 3C** | HIVE panel, Pit Boss panel, Swarm Board, Training Pit — full dark-theme DataTemplates |
| **Phase 4** | DiffViewer, ShellApprovalCard, UnknownToolCard — approval flow wired end-to-end |
| **Phase 5** | Full IDE layout in `MainWindow.axaml` — ribbon nav, pill switcher, panel host, status bar |

121/121 unit tests green. Grok review: CLEAN.

---

## What's new in v1.6

### v1.6.1 — HIVE security audit & hardening

A full adversarial review of the HIVE cryptographic layer — independent passes via Codex, Cerebras, and a local multi-angle review — with every confirmed finding fixed and covered by tests.

| Fix | What changed |
|---|---|
| **Election forgery** | Election messages (suspect / claim / recover / stepdown) now verify the sender's ECDSA signature. Previously the signature was generated but never checked, letting any LAN peer forge an election and seize the Warchief crown |
| **Fail-closed auth** | `GracePeriodActive` defaults to false; every authenticated endpoint rejects unsigned requests — no anonymous fall-through |
| **Canonical injection** | Request paths are sanitised before HMAC signing, closing a newline field-boundary forgery |
| **Replay after restart** | The nonce replay cache is persisted and restored across restarts (zero replay window on graceful restart, ~5s on hard kill), and recorded only after HMAC verification so it can't be flooded |
| **Revocation race** | Trust check and shared-secret lookup are now a single atomic operation (TOCTOU closed) |
| **Liveness integrity** | Heartbeats are never sent unsigned; a peer that rejects our credentials (401/403) is treated as offline, not healthy |
| **Task-queue races** | Claim / heartbeat / complete / fail and the timeout watchdog are serialised, closing a data race on task ownership |
| **Licensing** | AGPL-3.0 + commercial dual license, a SECURITY.md disclosure policy, and SPDX headers across the source tree |

51/51 HIVE security tests green, including new coverage that rejects forged election messages.

---

Security-hardened HIVE MIND, Update Center with fleet deploy, and a solid headless test foundation.

| Feature | What it is |
|---|---|
| **HIVE security overhaul** | Full cryptographic identity layer: P-256 ECDSA signing, ECDH key exchange, HMAC-SHA256 per-request auth, nonce replay cache, Bully election, DPAPI secret storage — replaces the honor-system prototype |
| **Port 7079 HMAC enforcement** | Task queue endpoints are now fail-closed; workers sign every request; unsigned = 401 |
| **Warchief crown badge** | Gold border and `👑` marker on the correct constellation node card, resolved live via peer store |
| **Update Center** | New `⬆ Update` tab: version card, inline build log, downloads pre-built exe from GitHub releases (falls back to build-from-source), gold dot on mode button when update available |
| **Fleet deploy** | Warchief can push an update to all outdated worker nodes from the Update Center — each node updates and restarts autonomously |
| **Headless unit tests** | `OrchestratorIDE.UnitTests` project: 112 pure-logic tests pass in ~1 s over SSH with no display required |

---

## What's new in v1.5

The biggest release since the swarm itself. v1.5 closes the training loop — from a human describing what they want the swarm to learn, all the way to a deployed adapter, without touching a script.

| Feature | What it is |
|---|---|
| **Pit Boss** | AI training wizard — 8 questions, then TheOrc writes its own training plan and kicks off dataset generation |
| **SQLite metadata layer** | Every capture, plan, run, and dataset now has a queryable database behind it — shipped a full release early |
| **Plan history** | Pit Boss landing page showing every training run with status, target count, model, and timestamp |
| **Worktree isolation** | Each swarm task gets its own git worktree — parallel runs are conflict-free by construction |
| **Reviewer Quality Gate** | Swarm output isn't authoritative until a Reviewer passes it, formalized at the merge step |
| **ORC ACADEMY v1** | Fine-tuned boss adapter trained, evaluated, and deployed — `theorc-boss:gemma4-ft` is live |

---

## Quick Start

### One-click installer

1. Grab `OrchestratorSetup.exe` from [Releases](https://github.com/hardcoreerik/TheOrc/releases)
2. The installer detects your GPU and walks through Ollama setup — it's pretty painless
3. Launch TheOrc, open a workspace folder, describe something you want built

### Build from source

```powershell
git clone https://github.com/hardcoreerik/TheOrc.git
cd TheOrc
dotnet run --project OrchestratorIDE.Avalonia/OrchestratorIDE.Avalonia.csproj
```

**Requirements:** Windows 10/11 · .NET 10 · [Ollama](https://ollama.com) · 8 GB VRAM minimum (16 GB recommended for running a full swarm)

Ollama is the easiest path to get running, which is why it's the default here — it is not the only one. Native in-process inference and a standalone llama.cpp server are both real, supported lanes with zero Ollama dependency; see [docs/RUNTIME_SUPPORT_MATRIX.md](docs/RUNTIME_SUPPORT_MATRIX.md) for what's default, what's opt-in, and how fallback between them actually works.

### Grab a model and go

```powershell
# Recommended starting stack
ollama pull theorc-boss:gemma4-ft   # fine-tuned boss — 125 MB LoRA over Gemma 4 12B QAT
ollama pull qwen2.5-coder:14b       # coder workers — great speed/quality balance
```

> **No dedicated GPU?** TheOrc works with CPU-only Ollama, just slower. 7B coder models run fine at CPU speeds for most tasks. Give it a shot.

---

## Documentation

| | |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | How the shell, swarm, GOBLIN MIND, and Training Pit all connect |
| [The Orc Context Fabric.md](docs/The%20Orc%20Context%20Fabric.md) | The technical design and current implementation path for source-grounded large-corpus memory |
| [CONTEXT_FABRIC_BENCHMARK_CORPUS.md](docs/CONTEXT_FABRIC_BENCHMARK_CORPUS.md) | The Independent Mind Corpus, benchmark shelf, private-corpus rules, and phase mapping |
| [CONTEXT_FABRIC_BENCHMARK_MANIFEST.md](docs/CONTEXT_FABRIC_BENCHMARK_MANIFEST.md) | Fixture manifest fields, pinned-fixture schema, and sample JSON for reproducible CF benchmark imports |
| [CONTEXT_FABRIC_PUBLIC_COPY.md](docs/CONTEXT_FABRIC_PUBLIC_COPY.md) | Short public-facing Context Fabric copy for README, website, and launch posts |
| [USER_GUIDE.md](docs/USER_GUIDE.md) | Best place to start on day one — modes, approvals, workspaces |
| [SWARM_GUIDE.md](docs/SWARM_GUIDE.md) | How goals become plans and how to steer the swarm mid-run |
| [TRAINING_PIT_GUIDE.md](docs/TRAINING_PIT_GUIDE.md) | Capture → review → ORC ACADEMY training, step by step |
| [GLOSSARY.md](docs/GLOSSARY.md) | Every TheOrc term in one place — goblins, captures, manifests, all of it |
| [ROADMAP.md](docs/ROADMAP.md) | What's shipped, what's cooking, what's next |

---

<div align="center">

![Build Complete](Assets/release.png)

</div>

---

## License

TheOrc is licensed under the **GNU Affero General Public License v3.0 (AGPL-3.0)** — see [LICENSE](LICENSE).

In plain terms: you are free to use, study, modify, and share TheOrc. If you run a **modified** version as a network service, AGPL requires you to make your changes available to the people using it. Running it locally for yourself or your team imposes no such obligation.

**Need different terms?** If AGPL doesn't fit your situation — for example, you want to build a closed-source product on top of TheOrc, or embed it in a commercial offering without the copyleft obligations — a **commercial license** is available. See [LICENSING.md](LICENSING.md).

Contributions are welcome under the [Contributor License Agreement](CLA.md), which keeps the dual-license model possible.

---

## Support the project + what's coming

TheOrc is free, open source, and always will be. If it saves you a subscription or two, consider throwing something in the jar:

<div align="center">

[![Ko-fi](https://img.shields.io/badge/Ko--fi-buy_a_coffee-FF5E5B?style=for-the-badge&logo=ko-fi)](https://ko-fi.com/hardcoreerik)
[![PayPal](https://img.shields.io/badge/PayPal-donate-003087?style=for-the-badge&logo=paypal)](https://paypal.me/hardcoreerik)
[![GitHub Sponsors](https://img.shields.io/badge/GitHub-sponsor-EA4AAA?style=for-the-badge&logo=githubsponsors)](https://github.com/sponsors/hardcoreerik)

</div>

Here's what's on the workbench — this is where support goes:

### 🧠 ORC ACADEMY v2 — smarter boss, broader goals
The v1 adapter was trained on 900 plans, almost all C# feature work. v2 fixes that: the Pit Boss pipeline is already generating ~1,200 synthetic examples covering bugfixes, refactors, tests, integrations, and docs across a dozen languages — using Cerebras cloud inference at no cost. Once reviewed, v2 trains a boss that handles real-world requests, not just TheOrc building itself.

### 🌐 HIVE MIND — distributed swarm across your whole network
HIVE MIND lets multiple TheOrc machines coordinate over your local network. One machine runs the boss and hands off worker tasks to others. Your gaming rig does the planning, your NAS runs a coder, the old workstation in the corner finally earns its keep. Phase A is shipped (LAN discovery, queue, worker polling). Phase B is full distributed task execution and remote harvest.

### 🎓 On-platform self-improvement
The long game: TheOrc writes its own training goals, runs them through the swarm, and feeds the results back into ORC ACADEMY — closing the loop with minimal human input. The Pit Boss pipeline makes the dataset generation side of this almost free. The remaining work is getting the swarm to generate and judge its own goals.

### 💻 Cross-platform
TheOrc's desktop shell is Avalonia (.NET), built to run on Windows, macOS, and Linux from one codebase — not yet verified on real Mac/Linux hardware. Ollama already runs everywhere; the daemon (Warband) is already cross-platform too.

---

### 🖥️ We want your hardware

Seriously. HIVE MIND needs real multi-machine testing and TheOrc needs to prove it runs well on hardware beyond the dev rig. If you have any of the following gathering dust, get in touch — you'd be doing the warband a real favour:

| Hardware | What we'd test |
|---|---|
| **Multi-GPU Windows rig** | Distributed swarm with workers on separate GPUs |
| **AMD GPU (RX 7000 / RX 9000)** | ROCm + Ollama compatibility, full swarm on AMD |
| **High VRAM card (24 GB+)** | Larger model support, bigger context, faster worker throughput |
| **Low-spec machine (4–8 GB VRAM / CPU-only)** | Minimum viable swarm, small model combinations |
| **Second Windows machine (any spec)** | HIVE MIND Phase B — multi-node job routing |
| **Mac (Apple Silicon)** | Groundwork for the cross-platform path |

Drop a note in [Issues](https://github.com/hardcoreerik/TheOrc/issues) with the tag `test-lab` or reach out directly. Hardware contributors are credited in [docs/SPONSOR_TEST_LAB.md](docs/SPONSOR_TEST_LAB.md).

The goblins are grateful. They work for free but they do appreciate the compute.
