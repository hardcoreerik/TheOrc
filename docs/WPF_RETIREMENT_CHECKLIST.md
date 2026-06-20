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
- **`ask_user` (2026-06-19 — this doc was stale, verified against code, not assumed):**
  `OrchestratorIDE.Avalonia/MainWindow.axaml.cs` registers `ask_user` against a
  real `UserInputDialog.ShowAsync(...)` call (`RegisterAskUserTool`, ~line 829),
  not `UnavailableFeatureRouter`. `UserInputDialog.axaml.cs` is a complete modal
  (text input, OK/Cancel, Escape-to-cancel, focus management, hint extraction
  from parenthetical question text). A dedicated headless test already exists:
  `OrchestratorIDE.Avalonia.HeadlessTests/T23_UserInputDialogTests.cs`. Phase 1's
  exit criterion is met. The "Remaining Blockers #1" and "Phase 1" sections below
  are kept as a record of what this doc *used to* claim — do not action them.

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

### 1. `ask_user` — CLOSED, see "Recently Closed" above

~~WPF wires `ask_user` to `UserInputDialog`. Avalonia still routes it through
`UnavailableFeatureRouter.BlockAskUser(...)`...~~ — stale as of 2026-06-19;
verified closed against actual code, not assumed from this doc.

### 2. Model Utility Windows — PARTIALLY CLOSED (2026-06-19)

The high-use model library/downloader path is ported. Of the three remaining
model/test utility flows:

- **`ModelCapabilityTestDialog` — RETIRED.** Deleted (WPF window + Avalonia stub
  + the SwarmBoardPanel/ModelWikiWindow launch points + its UI test coverage).
  Was part of the Sponsor Test Lab community workflow — that program is paused
  for this section, not silently broken; see `docs/SPONSOR_TEST_LAB.md`.
- **`ToolCallTestWindow` — RETIRED.** Same treatment, including the `--tool-probe`
  CLI mode in `App.xaml.cs`.
- **`ModelWikiWindow` — still stubbed, deliberately deferred, not retired.** This
  one is a real user-facing feature (browseable model catalogue), not a pure
  diagnostic tool. It needs a proper "fold into the existing Avalonia model
  management surface" design (per Phase 2's own original suggestion below),
  not a rushed 1:1 port or a retirement decision made under time pressure.

Remaining blocker for WPF deletion: `ModelWikiWindow` only.

### 3. First-Run / Agent File Regeneration — ✅ CLOSED (2026-06-20)

`FirstRunWindow` ported to Avalonia (`OrchestratorIDE.Avalonia/UI/Windows/FirstRunWindow.axaml`).
Both call sites updated: the first-run-on-startup check in `MainWindow.axaml.cs`
(via `ShowFirstRunWizardAsync`) and `RegenerateAgentFileAsync()` for Settings'
"Regenerate Agent File" button — same dual-purpose window as the WPF original,
so one port closed both. 4 new headless tests (`T24_FirstRunWindowTests.cs`)
plus a permanent regression test (`T26_DispatcherInvokeAsyncTests.cs`, see its
doc comment for why). Found and fixed a real Avalonia 12.0.4 headless layout
bug along the way (wrapped read-only TextBox hangs `RunJobs()` — switched to
TextBlock) and two real bugs in the port's save-ordering/exception-safety via
Codex CLI review.

### 4. UI Automation Still Treats WPF As Canonical

`OrchestratorIDE.UITests` still launches the WPF desktop binary for the main
FlaUI lane. Avalonia has headless/window smoke coverage, but WPF cannot be
removed until the black-box UI lane moves to Avalonia or is deliberately
retired.

---

## Sunset Phases

### Phase 1. Trust Parity — ✅ DONE (verified 2026-06-19, see "Recently Closed")

### Phase 2. Model Utility Parity — PARTIALLY DONE (2026-06-19)

Goal: eliminate the most visible WPF-only utility windows.

- ~~Port or retire `ModelCapabilityTestDialog`.~~ ✅ Retired.
- ~~Port or retire `ToolCallTestWindow`.~~ ✅ Retired.
- Port `ModelWikiWindow`, or fold its remaining value into the Avalonia model
  management surface instead of cloning the old WPF shape exactly. **Not done —
  deliberately deferred, this is the one remaining real WPF-deletion blocker
  from this phase.**

Exit criterion:
Models menu and model-management workflows no longer route operators back to
 WPF-only windows. (Not yet met — `ModelWikiWindow` still does.)

### Phase 3. Guided Setup Cleanup — ✅ DONE (2026-06-20, see "Remaining Blockers" #3)

- ~~Restore or explicitly retire the first-run wizard.~~ Restored (ported).
- ~~Restore or explicitly retire the regenerate-agent-file flow.~~ Restored (same window).
- Update docs/help so the supported Avalonia path is clear. **Not done** — no
  user-facing docs change made yet; low priority since behavior now matches WPF.

Exit criterion:
Fresh-user and rules-file setup stories are supported without WPF. ✅ Met.

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

1. ~~`ask_user` Avalonia modal and resume flow.~~ DONE — see "Recently Closed".
2. ~~`ModelCapabilityTestDialog` and `ToolCallTestWindow` decision: port or
   retire into a new Avalonia diagnostics surface.~~ DONE — both retired
   2026-06-19, Sponsor Test Lab paused for that section.
3. `ModelWikiWindow` fold-in or direct port. **Next real blocker.**
4. ~~First-run/regenerate-agent cleanup.~~ DONE 2026-06-20 — see "Remaining Blockers" #3.
5. Avalonia desktop automation lane. **Other remaining real blocker, likely the bigger of the two.**
6. WPF project removal and doc truth sync.

**Project-level decision (2026-06-19):** WPF retirement is the gate for the
v1.9 release specifically — v1.9 should ship Avalonia-only with WPF deleted,
once items 2-5 above are closed and verified (not before). v2.0 (Native
Runtime default, Ollama dropped) follows v1.9 and is gated on multi-machine
HIVE MIND validation of v1.9's native opt-in path, not on this checklist.

If we want the fastest route to real WPF sunset, the first slice is not
"delete the old project." It is removing the remaining reasons operators or
tests still need it.

---

## Native Runtime Boundary

WPF retirement is separate from Native Runtime. Native Runtime should continue
on Avalonia-primary surfaces, but the next release should still be framed as
**Experimental Native Runtime opt-in** with **Ollama default/fallback**, not as
"WPF removed" or "Ollama removed."
