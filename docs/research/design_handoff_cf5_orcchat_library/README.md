# Handoff: CF-5 OrcChat Library Experience
> Repo: hardcoreerik/TheOrc · Branch: master · HEAD: d7a6af70c67de402c593e0933998f49ec686d058

## Overview
CF-5 brings a **Context Fabric Library** into OrcChat — TheOrc's chat panel. A user adds a document (Darwin, a manual, a reference), waits for it to be indexed by local native models, attaches it to a conversation, and asks cross-document questions that come back with cited, verifiable answers. No manual context management.

This is a **C# / Avalonia desktop app** (see ARCHITECTURE.md). Everything in this package targets that codebase. Do not ship the HTML design reference files directly — they are prototypes illustrating intended layout and behavior. Recreate each surface as Avalonia controls following the existing ChatPanel / SwarmBoardPanel patterns.

## Fidelity
**High-fidelity.** The HTML mockup (`OrcChat Library.dc.html`) shows exact colors, typography, spacing, and interactions. Match them using inline-style-equivalent Avalonia property setters. The color palette and sizing are specified in Design Tokens below. Use JetBrains Mono for technical labels (segment IDs, token counts, digests), Segoe UI / system-ui for all other text.

---

## Confirmed Scope for This Slice

| Decision | Choice |
|---|---|
| Ask routing | **Direct in-ChatPanel bridge** (no library_* model tools, no tool-call probing) |
| Indexing depth | **Read + Reduce** (FabricNativeReaderService + FabricReducer), with a read-only checkbox |
| Extras in this slice | Web-find import · Library search (FTS) · Storage-path setting |

### Explicitly OUT of this slice
- HIVE / distributed campaign engine
- Exhaustive query mode (FabricQueryPlanner already throws on it — do not lift that restriction)
- New DB migrations (all CF-5 schema tables — fabric_corpora through fabric_query_runs — are already in theorc.db via CF-1..CF-4 migrations)
- New parser formats (EPUB / DOCX / OCR) — current registry handles .txt, .md, .pdf only
- Model-facing library_* tool family (may follow in CF-5.1)
- Full cross-conversation notebook knowledge base

---

## Screens / Views

### 1 · Library Drawer (left rail inside ChatPanel)
A 312 px collapsible left rail. Always shown when the Chat mode button is active; toggled by a Library icon button in the chat header.

**Header row:**
- "📚 Library" label (Segoe UI 13 px, weight 600, #C8D4C8)
- "+ Add source ▾" dropdown button: bg #162616, border #2A4A2A, text #C4E89A, 11.5 px, 600 weight, corner-radius 6. Click opens a flyout with three items: From file… / From folder… / Find on the web…
- Search box below the header: bg #0B0F0B, border #1E2E1E, radius 7, icon 🔎 #5A6B5A, placeholder "Search the library…" #5E6E5A 11.5 px, FTS badge at right (JetBrains Mono 9 px, #5A6B5A)

**Corpus cards (scrollable list):**
Each card is a Border: bg #101A10 (Ready) / #12100A (Indexing) / #0E120E (Stale), border-radius 9, padding 11×12.

Ready card:
- Name: Segoe UI 12.5 px, weight 600, #D8E4D0
- Edition/type: JetBrains Mono 10 px, #6E8068
- "● Attached" pill: bg #132513, border #2F4F2F, text #9FD37F, radius 999, 10 px mono 600
- Progress bar: 5 px, radius 3, bg #162016, fill gradient #3F6E33→#7FB069, width 100%
- Coverage / chapter / claim chips: mono 9.5 px, bg #132513 (green), #0E160E (grey)

Indexing card:
- "Indexing" pill: bg #221B0C, border #4A3C18, text #D6A85A, dot animates orcPulse (@keyframes 0%/100% opacity .55, 50% opacity 1, 1.1s infinite)
- Progress bar: 5 px, fill gradient #7A5E22→#D6A85A at reported %, shimmer overlay @keyframes orcShimmer translateX(-100%→280%) 1.6s infinite
- Stage label: "Reading evidence cards · ~2m left" JetBrains Mono 10 px, #8A7E60
- Failed segment count chip + Repair button (border #3A3017, text #D6A85A)
- Read-only checkbox below the progress bar: "Read-only (skip reduce)" — stores a bool `IndexReadOnly` on CorpusAttachmentState. When checked, FabricIndexingOrchestrator skips the FabricReducer stage. Reducer runs best-effort when unchecked; failure does not block completion.

Stale card:
- "↻ Stale" pill: bg #1A140B, border #443714, text #C98A4B
- Explanation text: mono 10 px, #9A845E
- "Re-index corpus" full-width button: bg #1A140B, border #443714, text #C98A4B

**Notebook section** (below corpus list, same scroll area):
- Section label: NOTEBOOK · THIS CHAT, 10 px, 600 weight, 1.2 px letter-spacing, uppercase, #5A6B5A
- Dashed border container #0E140E/#243424 showing up to 5 most recent cited conclusions, each with border-left 2 px #2F4F2F and monospace citation tag.

**Storage footer** (below scroll area, flex:0):
- Border-top #162016, bg #0A0D0A, padding 10×13
- 🗄 label + current path (JetBrains Mono 10 px, #8A9A84, overflow ellipsis) + "Change…" button
- "Change…" opens a SaveFolderPickerAsync dialog; calls FabricLibraryOptions setter then restarts the store at the new path.

### 2 · Chat Header Bar
Inside ChatPanel, a row above the conversation scroll area.

Corpus badge (shown when corpus attached):
- bg #101A10, border #2A4A2A, radius 8, padding 6×11
- 📖 icon + corpus name (12 px 600 #D8E4D0) + edition (JetBrains Mono 10 px #6E8068) + status line ("source-bound · coverage complete" 9.5 px mono #7FB069)
- ✕ detach button (#5A6B5A → hover #C0614F)

Quick / Study toggle:
- Outer: bg #0E140E, border #1E2E1E, radius 8, padding 3
- Active button: bg #1A2A1A, text #C4E89A, border #3A5A3A, radius 6, padding 5×14, 11.5 px 600
- Idle button: bg transparent, text #7E8C7E, border transparent
- Mode hint text right of toggle: JetBrains Mono 10 px #6E8068, max-width 170 px
  - Quick: "hybrid retrieval over segments + summaries"
  - Study: "iterative retrieval + targeted rereading"

Context usage badge: right-aligned, existing pattern, unchanged.

### 3 · Cited Answer Bubble
The assistant bubble (bg #111611, border #1E2E1E, radius 10/10/10/2, padding 15×17) renders:

- Answer prose with inline citation superscripts: `[1]` `[2]` etc. as clickable spans. Green chip (bg #132513, text #9FD37F) for supported citations; blue chip (bg #10161C, text #7F94A8) for interpretive.
- **Coverage / verification line** (border-top #1A241A, JetBrains Mono 10 px):
  - Mode badge: "Study mode" or "Quick mode" — bg #132513, border #2F4F2F, text #9FD37F
  - Segments considered: "considered 38 / 384 segments · reopened 12 sources" (or "considered 9 / 384 segments")
  - "✓ citations verified" #7FB069 or "⚠ coverage incomplete" #D6A85A
  - "✎ N interpretive" #7F94A8 if any
- **Citation footnotes**: one row per citation, bg #0E140E border #1A241A radius 6, clickable (opens source preview). Shows: index pill + "Ch. N · Heading" + verification label + seg-id + char range.

**Stale/incomplete banner** (only when CoverageStatus ≠ Complete OR generation keys stale):
- Full-width, bg #221B0C, border-bottom #4A3C18, text #D6A85A, 11 px
- "⚠ Index incomplete — answers may miss sections. Segment coverage: k/N."

**Medical/sensitive policy banner** (when policyProfile ≠ "default"):
- bg #10161C, border-bottom #2A4256, text #A8C4D8
- "Educational interpretation of the cited source, not professional advice."

### 4 · Source-Preview Pane (right rail)
372 px right panel. Opens on citation click; closes on ✕ or on corpus detach.

- Header: "📄 Source" + citation label (JetBrains Mono 10 px #6E8068)
- Meta card: bg #0E140E, border #1A241A, radius 8
  - Document name (12.5 px 600 #D8E4D0)
  - heading · seg-id · char-range (mono 10 px #6E8068, line-height 1.6)
  - Verification badge: "✓ supported — exact source match" (#7FB069/#132513) or "✎ interpretive" (#7F94A8/#10161C)
- Source text: JetBrains Mono 12.5 px, line-height 1.72, #8A9A84 for context; highlighted quote with bg #1B3018 / text #D8E4D0 / border-bottom 2 px #7FB069 (supported) or bg #16222C / text #D4E0E8 / border-bottom #7F94A8 (interpretive)
- Footer buttons: "Open full segment" + "Save to notebook", bg #0E160E, border #1E2E1E, #9FB890

### 5 · Add-Source Menu / Web-Find Flow

**From file… / From folder…**: file/folder picker → FabricLibraryService.ImportFileAsync → triggers indexing.

**Find on the web…**: Opens a small inline panel in the drawer (replaces corpus list temporarily):
- Text input: "Book title, author, or URL…"
- "Search" button → calls FabricWebImporter.SearchAndDownloadAsync (web_search tool internally via ChatEngine one-shot, or direct HTTP fetch for known URLs like Project Gutenberg)
- Results list: URL + title + format tag (PDF / TXT) + estimated size, each with "Add to library" button
- On "Add to library": download → verify digest → save to content store → call ImportFileAsync → trigger indexing

Policy gate displayed below the input: "Only add content you are licensed to use. OrcChat does not redistribute corpus content outside this machine."

### 6 · Library Search
Activating the drawer search box (click or Tab) filters the corpus list in real time via FabricLibraryService.Search(query). Highlights matching corpus/document names. A "search results" section expands above corpora showing FabricSearchHit items (segment text snippets, claim text) with a "Jump to corpus" link.

---

## Interactions & Behavior

- **Quick/Study toggle**: updates CorpusAttachmentState.Mode; next send uses the new mode. No re-run of previous answers.
- **Citation click**: sets SourcePreviewState.OpenCitationIndex; pane animates in (slide from right or simple fade). Pane closes on X or on corpus detach.
- **Attach corpus**: sets CorpusAttachmentState; next send routes through FabricAskService instead of plain ChatEngine.Send.
- **Detach corpus**: clears CorpusAttachmentState; pane closes; plain chat resumes. Does NOT clear conversation history.
- **Index → complete**: IndexProgressViewModel raises PropertyChanged; corpus card transitions from Indexing → Ready state; corpus badge updates.
- **Read-only checkbox**: persisted per corpus (not per conversation). Calls FabricIndexingOrchestrator with readOnly:true, skips FabricReducer stage.
- **Stale detection**: on chat activate or corpus select, compare corpus generation keys (parser/chunker/schema/model/prompt versions from fabric_derivation_runs) against current FabricRunOptions values. Show stale banner if mismatch.
- **Budget overflow**: if FabricEvidencePack.FitsBudget == false, prepend "Evidence truncated — ask a narrower question or use Study" to the answer bubble.
- **Save to notebook**: appends a ConversationNotebookEntry to ConversationNotebookStore; notebook section in drawer live-updates.

---

## State Management

New state objects — follow existing ChatPanel field patterns (`private X _x;`):

```
CorpusAttachmentState         // null = no corpus attached
  string CorpusId
  string DisplayName
  string Edition
  string Mode                 // "Quick" | "Study"
  string CoverageStatus       // "complete" | "partial" | "none"
  bool IsStale
  bool IsReadOnly             // from read-only checkbox
  string PolicyProfile        // "default" | "medical" | ...

IndexProgressViewModel        // per-document, raised via events
  string DocumentId
  string Stage                // "parsing" | "reading" | "reducing" | "ready" | "failed"
  int CompletedSegments
  int TotalSegments
  IReadOnlyList<string> FailedSegmentIds
  string? ErrorMessage

CitationViewModel             // per inline citation chip
  int Index
  string SegmentId
  string DocumentId
  string HeadingPath
  int CharStart
  int CharEnd
  string Quote
  string VerificationLabel    // "Supported" | "PartiallySupported" | "Interpretive" | "CitationMismatch" | "Unverifiable"

ConversationNotebookEntry
  string ClaimText
  List<CitationViewModel> Citations
  DateTimeOffset CreatedAt
  string QueryRunId
```

---

## Design Tokens

```
App bg:             #0B0F0B
Drawer/preview bg:  #0A0D0A
Mode/status bar bg: #0D120D
Raised surface:     #101A10   (corpus card ready)
                    #12100A   (indexing card)
                    #0E120E   (stale card)
                    #111611   (assistant bubble)
                    #0E140E   (insets, footnote rows)
Borders:
  subtle:           #1A241A
  standard:         #1E2E1E
  emphasis:         #2A4A2A
  strong:           #3A5A3A
User bubble:        bg #1A2A1A   border #2A4A2A
Text primary:       #D4D4D4  #C8D4C8  #D8E4D0
Text muted:         #8A9A84  #7E8C7E  #6E8068  #5A6B5A
Green accents:      #C4E89A  #A8CC80  #9FD37F  #7FB069  #3F6E33
Status amber:       #D6A85A  bg #221B0C  border #4A3C18
Status orange:      #C98A4B  bg #1A140B  border #443714
Status red:         #C0614F  bg #1E110E  border #3A1E18
Status blue (interpretive): #7F94A8  bg #10161C  border #2A4256
Font body:          "Segoe UI", system-ui, -apple-system, sans-serif
Font mono:          "JetBrains Mono", ui-monospace, monospace
```

---

## Files

| File | Purpose |
|---|---|
| `OrcChat Library.dc.html` | High-fidelity interactive mockup — reference only |
| `src/FabricAskService.cs` | Core orchestration: planner → pack → answer → verify |
| `src/FabricIndexingOrchestrator.cs` | Read + reduce pipeline with IProgress<> + retry |
| `src/FabricWebImporter.cs` | Web-find: search → download → digest → import |
| `src/ViewModels/LibraryViewModel.cs` | Corpus/document list, search, attach/detach |
| `src/ViewModels/IndexProgressViewModel.cs` | Per-document progress state |
| `src/ViewModels/CitationViewModel.cs` | Per-citation data for chip + pane |
| `src/ConversationNotebookStore.cs` | Per-conversation cited-conclusion JSON store |
| `ChatPanel_CF5_changes.md` | Exact instructions for ChatPanel.axaml.cs edits |
| `CLAUDE_CODE_PROMPT.md` | Ready-to-paste prompt for Claude Code / CLI |
