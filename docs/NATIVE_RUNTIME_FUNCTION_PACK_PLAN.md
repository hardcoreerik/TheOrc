# Native Runtime Function Pack Plan

> Purpose: phase the highest-value local function packs into the shared native
> runtime so OrcChat, AgentLoop, swarm execution, and Phase 3B campaign work all
> use the same capabilities instead of each surface growing its own one-off tools.

---

## Why this exists

The native runtime is already proving it can generate text without Ollama. That
is necessary, but it is not sufficient.

To become the default daily runtime, it needs the local function surface people
actually use in modern AI chat:

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

All function packs should hang off the shared native-runtime core, not a single
panel:

- `IModelRuntime` / `IRoleRuntime`
- shared headless loop
- shared tool and result contracts
- shared capability advertisement
- shared attestation / trace surface

If a function only works from OrcChat but not from the headless runtime, it is
not done.

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
