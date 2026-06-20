<!-- Copyright (C) 2025-present hardcoreerik / TheOrc contributors | SPDX-License-Identifier: AGPL-3.0-or-later -->
# WPF Retirement Checklist

Repo-grounded punch list for removing the WPF shell and going full Avalonia.

Current answer: **do not drop WPF yet**.

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

## Recommended Order

1. Port/replace `ask_user` with an Avalonia-native modal.
2. Port or retire `ModelCapabilityTestDialog` and `ToolCallTestWindow`.
3. Port `ModelWikiWindow` or fold it into the Avalonia model-management surface.
4. Restore or explicitly retire the first-run/regenerate-agent guided flow.
5. Move the main UI automation lane to Avalonia.
6. Only then archive/delete the WPF shell project and update all truth docs.

---

## Native Runtime Boundary

WPF retirement is separate from Native Runtime. Native Runtime should continue
on Avalonia-primary surfaces, but the next release should still be framed as
**Experimental Native Runtime opt-in** with **Ollama default/fallback**, not as
"WPF removed" or "Ollama removed."
