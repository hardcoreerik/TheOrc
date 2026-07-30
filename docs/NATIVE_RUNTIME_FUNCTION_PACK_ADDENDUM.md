# Native Runtime Function Pack — Addendum

> Cutting-edge capabilities that exceed current frontier-model function, backed by 2026 research and shipping technology.

> **Integration note (2026-07-30):** wired into
> [`docs/NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md`](NATIVE_RUNTIME_FUNCTION_PACK_PLAN.md) as ranks 8-14
> and interleaved `Phase N.5` entries, and linked from
> [`docs/ROADMAP.md`](ROADMAP.md)'s Native Runtime section, so this content doesn't get lost. Marked
> there as a **different confidence tier** than the existing ranked 1-7/Phase 0-6 plan: this
> content is AI-researched, and a review pass found real citation-quality issues worth knowing
> before treating the numbers below as settled fact — one malformed arXiv ID (`arXiv 2502.0` under
> Phase 7, not a valid ID format), several stats sourced from content-marketing blogs
> (`getknit.dev`, `synvestable.com`, `buildmvpfast.com`, `zylos.ai`) rather than primary papers or
> benchmarks, and some arXiv IDs (`2605.18535`) implying dates after this project's assistant's
> knowledge cutoff and therefore unverifiable from here. The underlying technologies and
> directions (MCP, XGrammar-style constrained decoding, Graphiti-style temporal graphs, Rocq/Lean,
> UFO²/OSWorld-style GUI agents, Reflexion/Self-Refine, edge-model routing) are real and worth
> planning around — re-verify the specific numbers against primary sources before using them to
> justify a scoping or resourcing decision.

---

## Additions to the Priority List

| Rank | Function pack | Why it exceeds frontier models | Primary references |
|---|---|---|---|
| **8** | **MCP-native server runtime** | Frontier models bolt tools on via bespoke function calling. A native runtime that speaks MCP can discover, negotiate, and compose tools from an ecosystem of 10,000+ servers without per-tool custom code. | [MCP Linux Foundation roadmap](https://www.getknit.dev/blog/the-future-of-mcp-roadmap-enhancements-and-whats-next), [MCP enterprise adoption](https://www.synvestable.com/model-context-protocol.html) |
| **9** | **Structured generation / constrained decoding engine** | Frontier models still hallucinate JSON schema violations. A local runtime with grammar-constrained decoding (XGrammar, Outlines) guarantees 100% schema compliance, speeds up generation by ~50%, and improves downstream task accuracy by up to 4%. | [JSONSchemaBench / Microsoft Research](https://arxiv.org/html/2501.10868v1) |
| **10** | **Temporal knowledge-graph memory** | Frontier models are stateless between sessions. A local temporal knowledge graph (entity-relationship-time) lets the runtime remember "who committed to what, when," track belief revision, and answer "what was true then?" queries that vector RAG cannot. | [Awesome-GraphMemory survey](https://github.com/DEEP-PolyU/Awesome-GraphMemory), [Rowboat / Potpie case studies](https://akkonrad.medium.com/knowledge-graphs-arent-databases-anymore-they-re-the-memory-layer-for-ai-agents-d090c03eb58c) |
| **11** | **Formal verification / proof-assistant bridge** | No frontier model natively produces machine-checkable correctness proofs. Integrating Rocq/Lean lets the runtime generate code *and* a verifiable proof certificate, closing the trust gap for safety-critical or crypto code. | [AutoRocq agentic verification](https://arxiv.org/html/2511.17330v3) |
| **12** | **Native OS GUI automation (beyond browser)** | Browser automation is table stakes. OS-level GUI agents (UFO², CUA) can cross application boundaries—e.g., "pull data from the email client, paste into Excel, export to PDF"—but SOTA on open desktop tasks is still only ~20% vs 72% human. A local runtime with sandboxed OS control can iterate on this frontier privately. | [Computer Use state of the art 2026](https://zylos.ai/research/2026-02-08-computer-use-gui-agents/) |
| **13** | **Self-improvement loop with external anchors** | Frontier models cannot self-correct reasoning without external signal. A local runtime can run a bounded reflexion loop where the agent grades its own output against test results, linter output, or user rubrics—research shows this lifts HumanEval pass@1 from 80% to 91%. | [Reflexion / Self-Refine research](https://www.buildmvpfast.com/blog/ai-agent-self-improvement-recursive-accuracy-production-2026) |
| **14** | **Adaptive model routing (edge↔cloud)** | Frontier models are monolithic. A local runtime can route structured tool-use and summarization to a 4B local SLM (matching 671B-parameter performance on those tasks) while escalating open-ended reasoning to cloud—achieving functional parity at a fraction of the cost and latency. | [Beyond Scaling: Agents at the Edge](https://arxiv.org/html/2605.18535v1) |

---

## Where They Plug Into the Phase Plan

---

### Phase 1.5 — MCP-native tool layer (parallel to Browser pack)

**Scope:** Implement an MCP host inside the native runtime so that browser, workspace, shell, and image tools are exposed as MCP servers, not hardcoded functions.

**Why it exceeds frontier:** ChatGPT and Claude have function calling, but each integration is bespoke. MCP turns the runtime into a universal client that discovers capabilities at runtime. As of early 2026, 28% of Fortune 500 have deployed MCP servers, and the protocol is under Linux Foundation governance with AWS, Google, and Cloudflare committed.

**References:**
- [The Future of MCP: Roadmap, Enhancements, and What's Next](https://www.getknit.dev/blog/the-future-of-mcp-roadmap-enhancements-and-whats-next)
- [Model Context Protocol (MCP) Enterprise Adoption](https://www.synvestable.com/model-context-protocol.html)

**Exit criteria:** OrcChat can discover and call a third-party MCP server (e.g., a local SQLite or Git server) without a code change to the client.

---

### Phase 2.5 — Structured generation core (parallel to Image pack)

**Scope:** Integrate a constrained decoding backend (e.g., XGrammar or llama.cpp grammar mode) so that every tool contract—browser extraction, workspace outline, shell result—can be emitted as guaranteed-schema JSON, Pydantic, or even domain-specific grammar.

**Why it exceeds frontier:** Frontier APIs offer "JSON mode," but it is probabilistic and still violates complex schemas. Research on 10K real-world schemas shows constrained decoding frameworks not only guarantee compliance but can speed up generation by 50% because the engine skips boilerplate tokens.

**References:**
- [JSONSchemaBench: A Benchmark for Complex JSON Schema Generation](https://arxiv.org/html/2501.10868v1)
- [XGrammar: Flexible and Efficient Structured Generation Engine for Large Language Models](https://github.com/mlc-ai/xgrammar)

**Exit criteria:** 100% schema compliance on tool outputs; zero post-hoc JSON repair.

---

### Phase 3.5 — Temporal knowledge-graph memory (parallel to Workspace pack)

**Scope:** Replace or augment vector-only RAG with a local temporal knowledge graph (e.g., Graphiti, FalkorDB, or a lightweight embedded option). Extract entities, relationships, and timestamps from chat history, workspace files, and browser sessions.

**Why it exceeds frontier:** Frontier models have context windows, not memory. A temporal graph lets the runtime answer "What did we decide about the auth flow three weeks ago?" and track that a dependency was removed in commit `abc123` but still appears in the design doc. Production case studies show this reduces root-cause analysis from a week to 30 minutes on large codebases.

**References:**
- [Awesome-GraphMemory: A Survey of Graph-Based Memory for LLM Agents](https://github.com/DEEP-PolyU/Awesome-GraphMemory)
- [Knowledge Graphs Aren't Databases Anymore — They're the Memory Layer for AI Agents](https://akkonrad.medium.com/knowledge-graphs-arent-databases-anymore-they-re-the-memory-layer-for-ai-agents-d090c03eb58c)
- [Graphiti: Temporal Knowledge Graphs for AI Agents](https://github.com/getzep/graphiti)

**Exit criteria:** Cross-session continuity; provenance tracking for every extracted fact; human-review gate before the graph is updated.

---

### Phase 4.5 — Formal verification bridge (parallel to Shell/Test pack)

**Scope:** Add a Rocq/Lean proof-assistant server as an MCP tool. When the runtime generates sensitive code (crypto, concurrency, protocol parsers), it can spawn a proof agent that iteratively constructs a machine-checkable correctness proof.

**Why it exceeds frontier:** No shipping frontier model produces verifiable proofs for arbitrary code. Research on AutoRocq demonstrates an LLM agent that learns on-the-fly from the prover's feedback, achieving push-button verification for C programs and Linux kernel modules without human proof engineering.

**References:**
- [AutoRocq: LLM Agent for Automatic Software Verification in Rocq](https://arxiv.org/html/2511.17330v3)
- [The Lean Theorem Prover](https://lean-lang.org/)

**Exit criteria:** Runtime can generate a function and a Rocq proof certificate; failed proofs surface as structured diagnostics, not silent success.

---

### Phase 5.5 — Native OS GUI automation (parallel to Artifact export)

**Scope:** Beyond Playwright, add a sandboxed OS GUI control layer (informed by UFO² / CUA research) for cross-application workflows: "Open the Figma desktop app, export the frame, convert it in a local CLI tool, embed it in the markdown artifact."

**Why it exceeds frontier:** WebArena success is ~70% for web agents, but OSWorld (desktop) is still ~20% for SOTA systems. A local runtime can iterate on this gap with human supervision, starting with low-risk, repo-local desktop workflows.

**References:**
- [Computer Use GUI Agents: State of the Art 2026](https://zylos.ai/research/2026-02-08-computer-use-gui-agents/)
- [OSWorld: Benchmarking Multimodal Agents for Open-Ended Tasks in Real Computer Environments](https://os-world.github.io/)
- [UFO²: The Desktop Agent](https://github.com/microsoft/UFO)

**Exit criteria:** Deterministic cross-app workflow on a sandboxed VM; human approval gate for any input outside the sandbox.

---

### Phase 6.5 — Reflexion loop runtime (extends Typed results)

**Scope:** After any tool execution (shell test, browser extraction, proof attempt), the runtime can enter a bounded critique loop: compare output against rubric, test result, or schema, then request a revision. Hard cap at 3 iterations.

**Why it exceeds frontier:** Intrinsic self-correction without external signal often degrades performance. But with external anchors (exit codes, test failures, schema violations), reflexion loops show 20-point absolute gains on reasoning tasks and lift code generation from 80% to 91% on HumanEval.

**References:**
- [AI Agent Self-Improvement: Recursive Accuracy in Production (2026)](https://www.buildmvpfast.com/blog/ai-agent-self-improvement-recursive-accuracy-production-2026)
- [Reflexion: Self-Reflective Agents with Verbal Reinforcement Learning](https://arxiv.org/abs/2303.11366)
- [Self-Refine: Iterative Self-Improvement with Self-Feedback](https://arxiv.org/abs/2303.17651)

**Exit criteria:** Configurable reflexion on any typed result channel; audit log captures each draft, critique, and final output.

---

### Phase 7 — Adaptive model routing (capability-aware routing, evolved)

**Scope:** The runtime maintains a capability registry of local SLMs (e.g., Qwen3-4B, Phi-4-mini) and cloud endpoints. Structured tool-use, summarization, and regex extraction route to local 4B–7B models; open-ended reasoning, creative writing, and deep debugging route to cloud.

**Why it exceeds frontier:** Research on edge agents shows 4B-parameter models already match 671B-parameter DeepSeek-R1 on structured tool-use and API orchestration benchmarks. A native runtime that owns the inference stack can make this routing transparent, cutting cost and latency for 80% of daily tasks.

**References:**
- [Beyond Scaling: Agents at the Edge](https://arxiv.org/html/2605.18535v1)
- [WideSeek-R1-4B: Efficient Tool Use at the Edge](https://arxiv.org/abs/2502.0) *(see arXiv 2605.18535v1 for comparative analysis)*
- [Qwen3-4B Technical Report](https://qwenlm.github.io/blog/qwen3/)

**Exit criteria:** Sub-100ms local inference for structured tasks; automatic fallback to cloud with user disclosure; telemetry comparing accuracy per route.

---

## Summary

Frontier models are generalists that forget between sessions, hallucinate structure, and cannot verify their own code. A native runtime that adds **MCP-native tool discovery, grammar-constrained decoding, temporal knowledge graphs, formal proof bridges, and adaptive edge↔cloud routing** becomes a specialist operator that exceeds the frontier on reliability, memory, and verifiability—while still using those frontier models as one of many routed backends.

---

*Generated 2026-07-30. Research references current as of July 2026.*
