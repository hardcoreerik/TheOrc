# Handoff: continuing the uncensored/Open Chat work on this machine

Written 2026-06-22 from NEWCOREPC, for a fresh Claude Code session starting on
hardcorelaptopmsi. The user is moving development to this machine for a while because
running real (windowed) UI tests from NEWCOREPC was interrupting their own use of that
PC — mouse/screen control kept getting pulled away mid-game. This machine has a real
interactive session, so UI tests here won't have that problem; do them here as needed
(see "UI testing" below).

You're working in the same shared repo (`F:\Ai\OrchestratorIDE-dev` on NEWCOREPC,
reachable from here via the Windows share you already have access to) -- so paths in
this doc and in the codebase are the same paths you'll see locally. Branch:
**`feat/uncensored-chat-models`**. Latest commit: **`12e5815`**. Don't push without
asking the user first -- nothing in this branch has been pushed yet, commit locally.

Delete this file once you've read it and started working, so it doesn't go stale and
mislead a later session.

## Where this branch came from

The user wants TheOrc to support "uncensored" chat models (Dolphin-line, RLHF-refusal
removed) and a SillyTavern-pattern multi-backend chat experience -- built ourselves in
C#, not by reusing SillyTavern's code, improving on the pattern rather than cloning it.
Full original plan (Phase A model-catalog work, Phase B from-scratch chat work) is at
`C:\Users\hardc\.claude\plans\snoopy-forging-wren.md` on NEWCOREPC if you need the full
history -- the short version of what's already shipped:

- **Phase A** (separate, already-released work): 3 uncensored Dolphin models added to
  the model catalogs with an UNCENSORED badge, opt-in only, never auto-recommended.
- **Phase B1-B3** (this branch, shipped): `ChatEngine` (`OrchestratorIDE/Research/ChatEngine.cs`)
  generalized to carry two modes instead of forking a second engine -- Research mode
  (unchanged: web tools, research system prompt, temp 0.2) and Open mode (no injected
  prompt, no tools by default, user-controlled temperature/top-p). One panel,
  `OrchestratorIDE.Avalonia/UI/Panels/ChatPanel.axaml(.cs)`, serves both via a mode
  toggle. HIVE node-routing lets Open Chat target a paired machine's Ollama instead of
  local. Models tagged `"uncensored"` show a badge in the model picker.
- **Latest batch** (commit `12e5815`, just landed): real user testing surfaced that
  Open Chat didn't know the date/time, didn't remember a chosen persona/name across app
  restarts, and had no way to see context-window usage. Fixed via an opt-in
  `IncludeDateTimeContext` flag on `ChatEngine`, a new `OpenChatMemory` persistence
  service (`OrchestratorIDE/Services/OpenChatMemory.cs`), and
  `OllamaClient.GetContextLengthAsync` + `ChatEngine.OnUsage` feeding a context-usage
  indicator in the panel header.
- **Phase B4** (ORC ACADEMY tie-in -- fine-tuning a persona from chat history) was
  explicitly deferred in the original plan as its own follow-up; still not started,
  not part of this handoff's scope unless the user brings it up.

## What the user wants next (this handoff's actual task list)

Said verbatim just now: *"Get us back on track for continuing to develop out our
uncensored chat... tell it to do full ui tests as needed. Also work in the some basic
tool use abilities for the uncensored chat too. webcrawling/searching/scraping. Image
and file ingestion with the ability to 'see' an image. ability to show graphics/image
in chat."*

Four real, separate pieces of work. Checked the codebase before writing this so you're
not guessing at scope:

### 1. Tool-use for Open Chat (web search / fetch / scrape)

Open mode currently passes `tools: []` deliberately
(`OrchestratorIDE.Avalonia/UI/Panels/ChatPanel.axaml.cs`, `CreateEngine` method) -- the
original design rationale was "no tools by default, pure passthrough." The user now
wants this changed: Open Chat should be able to use tools, specifically web
search/fetch/scrape.

The tools already exist and are proven -- `WebSearchTool` and `FetchPageTool`
(`OrchestratorIDE/Research/WebSearchTool.cs`, `FetchPageTool.cs`), wired into Research
mode today via `ResearchToolset.GetTools(new WebSearchTool(), new FetchPageTool())`
(see `ChatEngine.cs` line ~111). The likely right move is exposing the same tools to
Open mode -- but **don't just flip Open mode to use the Research toolset wholesale**;
think about whether Open mode should get its own un-prefixed/un-prompted tool
availability (no injected research system prompt telling it how/when to search, since
that's exactly the kind of injected instruction Open mode exists to avoid) versus
reusing the tool *definitions* while leaving the system prompt empty. Check what
`ResearchToolset.GetTools` actually returns (tool schemas only, or does it bundle
prompt text too?) before deciding.

"Scraping" specifically -- check whether `FetchPageTool.FetchAsync` (returns
`PageResult { Url, Title, Text, Links }`) is sufficient as-is, or whether the user's
specific ask implies something more aggressive (following links, structured
extraction). If unsure, that's worth a clarifying question rather than guessing.

### 2. Image/file ingestion with vision ("see" an image)

**This does not exist at all today** -- checked `AgentMessage`
(`OrchestratorIDE/Models/AgentMessage.cs`): it's pure text (`Content` is a `string`,
no image/attachment field whatsoever). Checked `OllamaClient.cs`'s payload builder:
no `images` key anywhere in the request. This is real, new work, not a wiring task:

- `AgentMessage` needs an image/attachment field (Ollama's chat API takes a
  `images: [base64...]` array per message for vision-capable models like
  `llava`/`qwen2-vl`/`qwen2.5-vl` -- confirm the exact field name against Ollama's
  current docs before assuming, API surfaces drift).
- The chat UI needs a way to attach an image/file to a message (drag-drop, paste, or a
  file picker button next to `TbInput` in `ChatPanel.axaml`).
- Worth checking early whether the currently-selected/default model is even
  vision-capable -- sending an `images` array to a text-only model either gets ignored
  or errors, so the UI probably needs to communicate "this model can't see images" if
  the user attaches one to a non-vision model.

### 3. Show graphics/images in chat output

**Also doesn't exist today** -- checked `MarkdownView.cs`
(`OrchestratorIDE.Avalonia/UI/Controls/MarkdownView.cs`): no image rendering at all,
no handling of markdown image syntax (`![alt](url)`). This needs an `Image`/`Bitmap`
control wired into the markdown rendering pipeline for image blocks, plus thought
about whether this is for: (a) the model linking to a web image (fetched via
`FetchPageTool` or a direct URL), (b) tool-returned images (e.g. a future screenshot
tool), or (c) just rendering whatever the user attached in #2 back into their own
chat bubble so they can see what they sent. Probably all three eventually, but start
with whichever the user actually asked for first if you get a chance to ask.

### 4. UI testing -- now actually viable on this machine

The reason development moved here: `OrchestratorIDE.UITests`
(`OrchestratorIDE.UITests/Tests/T01_LaunchTests.cs` through `T20_AvaloniaSmokeTests.cs`,
20 test classes) uses FlaUI to drive the real, rendered Avalonia app via Windows UI
Automation (`AppFixture.cs` launches the actual `.exe` and finds its real window). This
needs a genuine interactive desktop session to work -- confirmed during testing earlier
today that this fails over a plain SSH connection (the launched process never gets a
real window handle, `MainWindowHandle` reads `0`), which is exactly why the user is now
working interactively on this machine instead. Since you're running interactively here,
this should just work normally: `dotnet test OrchestratorIDE.UITests`.

Run the full suite as needed while building out items 1-3 above, not just at the end --
catch regressions early. Save results to `test-results/` (e.g.
`test-results/uitests-<date>.log`), matching the existing
`test-results/automated-test-report-*.md` convention already in this repo.

## Standing rules from this session (apply throughout, not just for this handoff)

- **`Tools/grok-review.ps1 -Staged` must come back CLEAN before every commit.** This
  was followed strictly all session -- multiple real concurrency/exception-handling
  bugs were caught this way (UI mutations off the UI thread after an `await`, a plain
  `Dictionary` instead of `ConcurrentDictionary` under concurrent access, fire-and-forget
  `InvokeAsync` calls with no exception handling). Don't skip a round even if a fix
  looks obviously correct -- run it again after every fix until clean.
- **Write/extend headless tests** (`OrchestratorIDE.Avalonia.HeadlessTests`) for new
  ChatPanel behavior where you can -- this project's test discipline this session was:
  after writing a regression test, deliberately revert the fix and confirm the test
  goes red, then restore it, to prove the test actually has teeth.
- **Tooltips and context-aware right-click menus are a standing, project-wide UX
  principle now**, not a one-off for the chat panel -- per the user's explicit
  instruction earlier this session. Apply it to any new UI surface you add for items
  1-3 above (e.g. a tooltip explaining what an attached image will be used for, a
  right-click option on an image bubble to save/copy it).
- **Ask before destructive or hard-to-reverse actions** -- stopping/restarting the
  running app, force-killing processes, anything affecting shared/committed state.
  This was a recurring, explicit, firmly-held expectation all session.
- **Commit locally, don't push** -- no commit on this branch has been pushed anywhere;
  keep it that way unless the user explicitly says otherwise.

## Quick orientation if you need it

- `OrchestratorIDE/Research/ChatEngine.cs` -- the dual-mode chat engine.
- `OrchestratorIDE.Avalonia/UI/Panels/ChatPanel.axaml(.cs)` -- the chat UI, both modes.
- `OrchestratorIDE/Services/OpenChatMemory.cs` -- system-prompt persistence pattern to
  follow if you need similar persistence for anything new.
- `OrchestratorIDE/Core/OllamaClient.cs` -- the HTTP client to Ollama; this is where an
  `images` field would need to be added to the request payload for vision support.
- `OrchestratorIDE.Avalonia/OrchestratorIDE.Avalonia.csproj` -- **no wildcard globbing**,
  every new `.cs` file needs its own explicit `<Compile Include>` line or it silently
  won't compile into the Avalonia project. Easy to forget, bit several people this
  session.
