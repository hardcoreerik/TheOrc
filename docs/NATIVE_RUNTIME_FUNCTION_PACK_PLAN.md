# Native Runtime Function Pack Plan

> Purpose: phase the highest-value local function packs into the shared native
> runtime so OrcChat, AgentLoop, swarm execution, and Phase 3B campaign work all
> use the same capabilities instead of each surface growing its own one-off tools.
>
> **Current status — 2026-07-31:** Phase 0 capability contracts and the Phase 1
> browser mechanism are landed; see `NATIVE_BROWSER_AUTOMATION_SPEC.md` for the
> verified interactive/headless boundary and remaining HIVE origin-grant gap.
> PR #96 adds CaseForge, Art Forge, and KeyHound Atlas tools to OrcChat only;
> those integrations are pending and do not count as complete Function Packs
> until the shared headless/HIVE surface and capability policy exist. Phases 2–6
> remain planned. The N.5 research directions remain uncommitted research.
> The product decision is that studio integrations are TheOrc-wide capabilities,
> not OrcChat features. The current r3 specialist is proof-of-concept quality and
> is not expected to understand these tools; vocabulary training waits for the
> separately planned `theorc-toolcaller-v2` rather than blocking shared plumbing.

---

## Why this exists

The native runtime now generates text without Ollama as the production default.
That foundation is necessary, but it is not sufficient.

To become the complete daily workbench, it needs the local function surface
people actually use in modern AI chat:

- browser interaction
- screenshot and image understanding
- fast workspace search and file operations
- bounded shell/build/test loops
- durable artifact export

These are the capabilities that turn "a local model host" into "a local operator."

---

## Research-backed priority list

| Rank | Function pack | Practical value | Primary references |
|---|---|---|---|
| **1** | Browser automation + screenshots + DOM extraction | Reaches the web, reproduces UI flows, captures evidence, powers testing and research | [Playwright intro](https://playwright.dev/docs/intro), [Playwright screenshots](https://playwright.dev/docs/screenshots) |
| **2** | Image attachments + OCR + multimodal routing | Lets OrcChat reason over screenshots, scans, plots, and mixed documents | [Tesseract manual](https://tesseract-ocr.github.io/tessdoc/), [LLamaSharp](https://github.com/SciSharp/LLamaSharp) |
| **3** | Workspace intelligence | Fast local read/search/outline/diff/edit operations are core to coding tasks | [ripgrep](https://github.com/BurntSushi/ripgrep) |
| **4** | Bounded shell / build / test | Closes the implementation loop with real verification, not just suggestion text | Internal runtime requirement |
| **5** | Artifact generation / export | Converts chat outcomes into markdown, docx, pdf, html, and reusable handoff files | [Pandoc](https://pandoc.org/) |
| **6** | Typed result channels | Makes tools composable, auditable, and easier to verify than prose-only replies | Internal runtime requirement |
| **7** | Capability-aware routing | Avoids silent fallback and lets the runtime choose the right local model/tool/node | Internal runtime requirement |

**Ranks 8-14 — research-directions extension, added 2026-07-30:** see
[`docs/NATIVE_RUNTIME_FUNCTION_PACK_ADDENDUM.md`](NATIVE_RUNTIME_FUNCTION_PACK_ADDENDUM.md) for the
full writeup (MCP-native tool layer, constrained/grammar decoding, temporal knowledge-graph memory,
formal verification bridge, OS-level GUI automation, reflexion loops, adaptive edge↔cloud routing).
**Explicitly a different confidence tier than ranks 1-7 above** — sourced from external 2026
research with some citation-quality issues (one malformed arXiv ID, several stats traced to
content-marketing blogs rather than primary sources, some referenced papers dated after this
project's knowledge cutoff and unverifiable from here). The underlying technologies (MCP, XGrammar-
style constrained decoding, Graphiti-style temporal graphs, Rocq/Lean, UFO²/OSWorld-style GUI
agents, Reflexion/Self-Refine, edge-model routing) are real and worth planning around; the specific
numbers attached to them should be re-verified against primary sources before being used to justify
a scoping or resourcing decision. Interleaved into the phase plan below as `Phase N.5` entries,
matching the addendum's own numbering — none of them have a concrete, code-grounded implementation
spec yet (the kind [`NATIVE_BROWSER_AUTOMATION_SPEC.md`](NATIVE_BROWSER_AUTOMATION_SPEC.md) is for
Phase 0/1), so none are ready to start.

---

## Product goals

1. OrcChat can match the most useful local workflows of leading web chat tools
   without depending on Ollama.
2. The same tool contracts work in GUI chat, headless AgentLoop, swarm lanes,
   and Phase 3B campaign execution.
3. Every function is local-first, bounded, observable, and honest about whether
   the chosen runtime/model can actually satisfy the request.

---

## Non-goals for this rollout

- Recreating a generic remote shell orchestrator
- Allowing arbitrary network-heavy remote code execution by default
- Building a plugin marketplace before the core native tool packs are stable
- Hiding unsupported capabilities behind silent fallbacks

---

## Architecture rule

Function packs belong to TheOrc's shared orchestration/tool layer above inference
runtimes, not inside one model backend or UI panel:

- `IModelRuntime` / `IRoleRuntime`
- shared headless loop
- shared tool and result contracts
- shared capability advertisement
- shared attestation / trace surface

If a function only works from OrcChat but not from the headless runtime, it is
not done.

CaseForge, Art Forge, and KeyHound Atlas therefore register through the same
shared capability/tool profiles used by OrcChat, AgentLoop, swarm, and HIVE.
Surface-specific UI may differ; tool identity, schemas, policy, and execution do
not fork.

`OrcEngine` is a future experimental inference backend, not a tool host or
authorization layer. It may later advertise constrained-decoding capability,
but tool discovery, policy, approval, execution, and traces remain owned by
TheOrc.

## Orcish Tongue and specialist boundary

The Function Pack defines live capabilities, schemas, typed results, and
execution. Orcish Tongue defines how model intent becomes a versioned tool
decision. The learned toolcaller selects the next semantic action; it never
executes, authorizes, or overrides capability state.

The current promoted specialist is still a frozen six-tool, single-next-action
repair model. A future universal revision must be schema-conditioned and tested
against held-out tool names, but each training/evaluation corpus remains frozen
and hash-addressed for reproducibility. Do not silently extend v0 or v1.

---

## Phase plan

### Phase 0 — Contracts and capability model

### Scope

- Add a shared `NativeToolCapability` contract
- Add attachment capability flags:
  - image input
  - OCR available
  - browser automation available
  - shell/test available
  - artifact export available
- Add typed result contracts for:
  - text summary
  - structured rows / JSON payload
  - artifact references
  - screenshot/image refs
  - telemetry / attestation

### Exit criteria

- OrcChat and headless loops both query the same capability model
- Unsupported operations fail explicitly with a user-readable reason
- Tool traces include capability snapshot + runtime backend identity

---

### Phase 1 — Browser automation pack

### Scope

- Adopt Playwright as the browser-control backbone
- Provide runtime-owned tools for:
  - navigate
  - click
  - type
  - wait for selector/text
  - extract DOM/text
  - capture screenshot
  - download file
- Return structured evidence:
  - page title/url
  - extracted text blocks
  - screenshot refs
  - optional trace/log artifact refs

### Why first

This closes the largest gap between native runtime chat and how people actually
use web-based AI tools in practice.

### Exit criteria

- OrcChat can perform a multi-step browse/extract/screenshot loop end-to-end on
  the native runtime
- Headless tests cover at least one deterministic site flow
- Cancellation and timeout behavior are enforced

---

### Phase 1.5 — MCP-native tool layer *(research-directions tier, see addendum)*

Expose browser/workspace/shell/image tools as MCP servers instead of (or alongside) hardcoded
`ToolDefinition`/`HeadlessTool` entries, so the runtime can discover and compose third-party MCP
tools without per-tool custom code. **Directly interacts with Phase 1's design**: if this direction
is pursued, `NATIVE_BROWSER_AUTOMATION_SPEC.md`'s `BrowserTools.cs` approach (direct
`ToolDefinition` registration) would need to be revisited as an MCP-server wrapper instead, or in
addition — flagged as open question 5 in that spec. Full scope/exit-criteria in the addendum.
Not started; sequencing relative to Phase 1 itself is an open decision, not assumed to precede it.

---

### Phase 2 — Image attachment, OCR, and multimodal pack

### Scope

- Add first-class chat attachment records
- Accept local image attachments and screenshots
- Add OCR pipeline using Tesseract
- Add multimodal routing when the selected native model supports image input
- Support combined OCR + reasoning fallback when the model is text-only

### UX rules

- If the current model cannot see images, say so clearly
- Offer OCR-only handling when full vision is unavailable
- Preserve attachment provenance in chat history and artifacts

### Exit criteria

- User can attach a screenshot to OrcChat and receive either:
  - multimodal-native reasoning, or
  - OCR-backed reasoning with a disclosed fallback path
- Output can embed image previews and extracted text snippets

---

### Phase 2.5 — Structured generation core *(research-directions tier, see addendum)*

Grammar-constrained decoding (XGrammar or llama.cpp grammar mode) so every tool contract emits
guaranteed-schema output instead of probabilistic "JSON mode." Independent of the image/OCR pack
it's numbered alongside — could land any time after Phase 0's typed-result contracts exist to
constrain against. Full scope/exit-criteria in the addendum. Not started.

---

### Phase 3 — Workspace intelligence pack

### Scope

- Consolidate shared tools for:
  - browse
  - search
  - read
  - outline
  - diff
  - safe write/apply
- Back search with `rg` where available
- Normalize path safety, truncation, and preview behavior across chat and headless execution

### Why here

Browser and image handling make chat useful. Workspace intelligence makes it
productive for real local development work.

### Exit criteria

- OrcChat and AgentLoop use the same workspace tool contracts
- Search/read/outline behavior is consistent across runtimes
- Large file handling and path safety are covered by tests

---

### Phase 3.5 — Temporal knowledge-graph memory *(research-directions tier, see addendum)*

Replace/augment vector-only RAG with a local temporal knowledge graph (entities, relationships,
timestamps extracted from chat, workspace files, browser sessions) for cross-session continuity and
provenance-tracked recall. The addendum's own exit criteria require a human-review gate before the
graph is updated — a real design constraint, not a detail to skip. Full scope in the addendum.
Not started; would be a substantial new subsystem, not a small tool addition.

---

### Phase 4 — Bounded shell / build / test pack

### Scope

- Standardize one execution surface for:
  - short shell commands
  - build/test commands
  - formatter/linter runs
- Add:
  - time budgets
  - line/output caps
  - cancellation
  - exit code capture
  - streaming log events
  - trust/approval integration

### Security rule

This is not generic unrestricted remote command execution. The initial policy
should favor repo-local builds/tests and routine diagnostics, with explicit
denials for destructive or high-risk operations.

### Exit criteria

- Native runtime can run a bounded test/build loop with streamed output
- Approval/trust gates are enforced in OrcChat and headless mode
- Audit records capture command, cwd, limits, exit code, and cancellation state

---

### Phase 4.5 — Formal verification bridge *(research-directions tier, see addendum)*

A Rocq/Lean proof-assistant server as an MCP tool (depends on Phase 1.5 existing first, or its own
non-MCP integration) so sensitive code (crypto, concurrency, protocol parsers) can ship with a
machine-checked correctness proof alongside the generated code. Full scope/exit-criteria in the
addendum. Not started; genuinely narrow-audience (safety/security-critical code paths only), worth
weighing against broader-value phases before scheduling.

---

### Phase 5 — Artifact generation and export pack

### Scope

- Treat markdown generation as a first-class artifact, not only chat text
- Add export flows for:
  - markdown
  - html
  - docx
  - pdf
- Use artifact refs in chat responses instead of pasting very large content

### Why it matters

Good operator sessions often end in a handoff, report, spec, or release note.
The runtime should produce those directly.

### Exit criteria

- OrcChat can generate a markdown artifact and export it to at least one richer format
- Export failures surface clearly with actionable errors
- Artifacts are linked back into the conversation history

---

### Phase 5.5 — Native OS GUI automation *(research-directions tier, see addendum)*

Beyond Playwright: sandboxed OS-level GUI control for cross-application workflows (desktop apps,
not just browser tabs). The addendum's own comparison (~70% web-agent success vs. ~20% desktop-agent
SOTA) is itself the kind of stat flagged above as worth re-verifying against the primary OSWorld
benchmark rather than the secondary blog cited — but the *direction* (a sandboxed VM, human approval
gate for anything outside it) is a sound starting constraint regardless of the exact number. Full
scope in the addendum. Not started; meaningfully higher-risk than the browser pack (arbitrary
desktop-app surface vs. a single sandboxed browser process) and should not be scheduled ahead of
Phase 1 actually shipping and being trusted in production.

---

### Phase 6 — Typed results, verification, and polish

### Scope

- Unify typed result rendering across browser, OCR, shell, workspace, and export tools
- Add verification helpers:
  - command success/failure summaries
  - extracted table previews
  - artifact digests
  - screenshot thumbnails
- Improve model/tool capability disclosures in settings and chat

### Exit criteria

- Tool outputs render consistently in OrcChat
- Native runtime traces are compact but replayable
- Operators can tell what happened without opening logs for every action

---

### Phase 6.5 — Reflexion loop runtime *(research-directions tier, see addendum)*

After any tool execution, a bounded critique loop (hard-capped iterations) comparing output against
a rubric/test result/schema before requesting a revision — extends this phase's typed-result work
rather than standing alone. Full scope/exit-criteria in the addendum. Not started.

---

### Phase 7 — Adaptive model routing (edge↔cloud) *(research-directions tier, see addendum;
evolves this plan's original Phase 7 "capability-aware routing" rank)*

A capability registry of local SLMs + cloud endpoints, routing structured tool-use/summarization
locally and open-ended reasoning to cloud. Note the tension with this whole plan's own "local-first"
framing and non-goals (§ above) — a cloud-routing feature needs an explicit decision on when/whether
TheOrc calls out to a cloud endpoint at all, not just a technical design. Full scope in the addendum.
Not started.

---

## Acceptance test matrix

1. Browser flow:
   OrcChat opens a page, extracts content, and returns a screenshot artifact.
2. Image flow:
   OrcChat accepts a screenshot, OCRs it, and answers a question about the text.
3. Workspace flow:
   OrcChat finds a symbol, opens the file, and returns a concise outline.
4. Build/test flow:
   OrcChat runs a bounded test command and summarizes pass/fail with logs attached.
5. Artifact flow:
   OrcChat generates a markdown plan and exports it to a second format.
6. Capability failure flow:
   A text-only model receives an image request and responds with an explicit
   unsupported/alternate-path message instead of silently failing.

---

## Suggested delivery order

1. Phase 0 contracts/capabilities
2. Phase 1 browser automation
3. Phase 2 image + OCR + multimodal routing
4. Phase 3 workspace intelligence
5. Phase 4 bounded shell/test execution
6. Phase 5 artifact export
7. Phase 6 typed-result polish

This order favors visible operator value first, while also building the shared
contracts that Phase 3B campaign execution can reuse.

**The `N.5`/research-directions phases (1.5, 2.5, 3.5, 4.5, 5.5, 6.5, evolved-7) are deliberately
NOT inserted into this delivery order.** They're a different confidence tier (see the ranks 8-14
note above the priority table) and none has a code-grounded implementation spec yet. Sequencing
them is a decision for whoever picks each one up, informed by how Phase 0/1 actually land — not
pre-committed here.
