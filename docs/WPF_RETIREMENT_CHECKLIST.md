<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# WPF Retirement Checklist

Repo-grounded execution plan for removing the WPF shell and going full
Avalonia.

Current answer: **sunset WPF in controlled slices, starting now**.

Avalonia is the primary shell for new work, but WPF still anchors a few
operator flows and the main Windows UI automation lane. Retirement should be a
deliberate final slice, not a side effect of Native Runtime work.

---

## Recently Closed

These are no longer retirement blockers:

- `SandboxBypassDialog` has an Avalonia implementation and is wired into both
  single-agent and SwarmBoard sandbox-bypass paths.
- `HelpWindow` has an Avalonia embedded-docs viewer path.
- `SelfUpdateDialog` has an Avalonia `SelfUpdateWindow`/`UpdatePanel` path.
- `ModelDownloaderWindow` and `ModelLibraryWindow` have Avalonia windows.
- `GlobalAgentDialog` / `WorkspaceRulesDialog` are replaced by the Avalonia
  `AgentRulesWindow` flow rather than one-for-one dialog ports.

---

## Exit Rule

We can retire `OrchestratorIDE/OrchestratorIDE.csproj` as the primary desktop
shell only after all of the following are true:

1. No operator-facing menu flow in `MainWindow.axaml.cs` falls back to
   `UnavailableFeatureRouter`.
2. No trust/approval/user-input path silently degrades because a WPF-only dialog
   is missing.
3. The main Windows UI automation lane no longer assumes `OrchestratorIDE.exe`
   from the WPF project is the canonical desktop binary.
4. `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `.grok/PROJECT_TRUTH.md`,
   `.agents.md`, and `.claude/AGENTS.md` all describe Avalonia as primary
   without claiming WPF is already deleted.

---

## Retirement Policy

Starting now, WPF enters maintenance-only mode:

1. No new operator-facing features should land only in WPF.
2. Any new desktop feature must ship in Avalonia first, or in shared
   runtime/service code with no WPF-only dependency.
3. WPF can still receive narrow parity or shutdown fixes while retirement is in
   progress, but those fixes should reduce the eventual cutover cost.
4. The release message stays truthful: **Avalonia-primary**, **WPF being
   retired**, not "WPF already removed."

---

## Remaining Blockers

### 1. `ask_user` Still Lacks An Avalonia Dialog

WPF wires `ask_user` to `UserInputDialog`. Avalonia still routes it through
`UnavailableFeatureRouter.BlockAskUser(...)`, returning an error string instead
of asking the operator.

This is now the most trust-critical remaining parity gap. It affects agent
behavior, not just menu polish.

### 2. Model Utility Windows Still Stubbed

The high-use model library/downloader path is ported, but three model/test
utility flows still report unavailable in Avalonia:

- `ModelWikiWindow`
- `ModelCapabilityTestDialog`
- `ToolCallTestWindow`

These do not block Native Runtime Phase 3, but they do block a truthful
Avalonia-only desktop release.

### 3. First-Run / Agent File Regeneration Still Stubbed

Avalonia still logs unavailable messages for the first-run wizard and agent-file
regeneration flow. The manual `.agent.md` / Agent Rules path exists, but the
original guided flow is not yet restored.

### 4. UI Automation Still Treats WPF As Canonical

`OrchestratorIDE.UITests` still launches the WPF desktop binary for the main
FlaUI lane. Avalonia has headless/window smoke coverage, but WPF cannot be
removed until the black-box UI lane moves to Avalonia or is deliberately
retired.

---

## Sunset Phases

### Phase 1. Trust Parity

Goal: remove the last trust-critical reason to keep WPF around.

- Port/replace `ask_user` with an Avalonia-native modal.
- Verify agent, swarm, and approval paths no longer fall back to
  `UnavailableFeatureRouter` for user-input pauses.
- Add at least one headless/UI test that proves the Avalonia `ask_user` path
  can render, accept input, and resume.

Exit criterion:
Avalonia no longer silently degrades a trust/approval/user-input flow.

### Phase 2. Model Utility Parity

Goal: eliminate the most visible WPF-only utility windows.

- Port or retire `ModelCapabilityTestDialog`.
- Port or retire `ToolCallTestWindow`.
- Port `ModelWikiWindow`, or fold its remaining value into the Avalonia model
  management surface instead of cloning the old WPF shape exactly.

Exit criterion:
Models menu and model-management workflows no longer route operators back to
 WPF-only windows.

### Phase 3. Guided Setup Cleanup

Goal: close the onboarding gaps that still justify keeping the old shell.

- Restore or explicitly retire the first-run wizard.
- Restore or explicitly retire the regenerate-agent-file flow.
- Update docs/help so the supported Avalonia path is clear.

Exit criterion:
Fresh-user and rules-file setup stories are supported without WPF.

### Phase 4. Automation Lane Cutover

Goal: stop treating WPF as the canonical Windows desktop binary.

- Move the main UI automation lane from WPF FlaUI-first assumptions to Avalonia
  coverage.
- Keep the current Avalonia headless tests, then add one black-box desktop lane
  that launches the Avalonia shell for high-value workflows.
- Update any packaging/test scripts that still hardcode the WPF executable.

Exit criterion:
Release verification no longer depends on launching the WPF shell.

### Phase 5. Project Removal

Goal: remove the WPF shell without leaving truth drift behind.

- Archive or delete `OrchestratorIDE/OrchestratorIDE.csproj` and remaining
  WPF-only windows/panels once Avalonia is the verified desktop path.
- Remove WPF-only test assets and launcher assumptions.
- Update `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `.grok/PROJECT_TRUTH.md`,
  `.agents.md`, and `.claude/AGENTS.md`.

Exit criterion:
No release-critical workflow, automation lane, or truth doc still depends on
 WPF existing.

---

## Immediate Execution Order

This is the order we should actually work in next:

1. `ask_user` Avalonia modal and resume flow.
2. `ModelCapabilityTestDialog` and `ToolCallTestWindow` decision: port or
   retire into a new Avalonia diagnostics surface.
3. `ModelWikiWindow` fold-in or direct port.
4. First-run/regenerate-agent cleanup.
5. Avalonia desktop automation lane.
6. WPF project removal and doc truth sync.

If we want the fastest route to real WPF sunset, the first slice is not
"delete the old project." It is removing the remaining reasons operators or
tests still need it.

---

## Native Runtime Boundary

WPF retirement is separate from Native Runtime. Native Runtime should continue
on Avalonia-primary surfaces, but the next release should still be framed as
**Experimental Native Runtime opt-in** with **Ollama default/fallback**, not as
"WPF removed" or "Ollama removed."
