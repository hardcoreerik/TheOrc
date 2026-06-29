# CF-5 OrcChat Library — Implementation Prompt for Claude Code

Paste this prompt into Claude Code (desktop or CLI) from the root of the TheOrc repo.
The design_handoff_cf5_orcchat_library/ folder contains all supporting files.

---

You are implementing CF-5 for the TheOrc repo (hardcoreerik/TheOrc, branch master, HEAD d7a6af70c67d).
Read design_handoff_cf5_orcchat_library/README.md fully before writing any code.

## Your task

Implement the CF-5 OrcChat Library experience as a **single PR** on branch `feat/cf5-orcchat-library`.

1. Drop the stub files from design_handoff_cf5_orcchat_library/src/ into their target locations:
   - FabricAskService.cs → OrchestratorIDE/Services/ContextFabric/
   - FabricIndexingOrchestrator.cs → OrchestratorIDE/Services/ContextFabric/
   - FabricWebImporter.cs → OrchestratorIDE/Services/ContextFabric/
   - ViewModels/*.cs → OrchestratorIDE.Avalonia/UI/ViewModels/
   - ConversationNotebookStore.cs → OrchestratorIDE/Services/

2. Fill in the stub TODOs. Key ones:
   - FabricAskService.GenerateAsync: check how ContextFabricFeasibilityRunner generates answers internally and follow the same IRoleRuntime streaming pattern.
   - FabricWebImporter.SearchAsync: implement via a one-shot ChatEngine call using the existing web_search tool (OrcChatToolCatalog). Return WebImportCandidate list.
   - LibraryViewModel.CorpusCardViewModel.TotalSegments: sum from FabricLibraryRepository.GetSegments per document.
   - FabricIndexingOrchestrator.RetryFailedAsync: add ReadSegmentsAsync overload to FabricNativeReaderService, or call ReadDocumentAsync and filter results.

3. Apply ChatPanel_CF5_changes.md — targeted edits only. Do NOT rewrite ChatPanel.axaml.cs.

4. Create the two new Avalonia UserControls:
   - OrchestratorIDE.Avalonia/UI/Controls/LibraryDrawerControl.axaml(.cs)
   - OrchestratorIDE.Avalonia/UI/Controls/SourcePreviewPanel.axaml(.cs)
   Follow the design spec in README.md exactly (colors, layout, fonts). Use the design token values listed there. Follow the existing code-behind-only pattern (no MVVM framework — look at HiveConstellationView.cs and ChatPanel.axaml.cs for style).

5. Wire services in MainWindow.axaml.cs per the instructions in ChatPanel_CF5_changes.md § 8.

6. Write tests:
   OrchestratorIDE.UnitTests/:
   - ContextFabricAskServiceTests.cs (use ContextFabricScriptedRuntime from existing test infra)
   - ContextFabricIndexingOrchestratorTests.cs
   OrchestratorIDE.Avalonia.HeadlessTests/:
   - OrcChatLibraryTests.cs (follow TestAppBuilder + ChatPanelModeToggleTests.cs pattern)
   - OrcChatCitationNavigationTests.cs
   - OrcChatContextFabricQueryTests.cs

7. STOP conditions — do NOT do any of these:
   - Add Exhaustive mode or lift FabricQueryPlanner's mode restriction
   - Add new SQLite migrations (the CF-1..CF-4 schema is already in place)
   - Add new parser formats (EPUB, DOCX, OCR)
   - Add library_* model tools to OrcChatToolCatalog
   - Touch the HIVE campaign engine
   - Rewrite ChatPanel from scratch

## Key existing files to read first
- OrchestratorIDE/Services/ContextFabric/FabricLibraryService.cs
- OrchestratorIDE/Services/ContextFabric/FabricQueryPlanner.cs
- OrchestratorIDE/Services/ContextFabric/EvidencePackBuilder.cs
- OrchestratorIDE/Services/ContextFabric/FabricCitationVerifier.cs
- OrchestratorIDE/Services/ContextFabric/FabricNativeReaderService.cs
- OrchestratorIDE/Services/ContextFabric/ContextFabricContracts.cs
- OrchestratorIDE.Avalonia/UI/Panels/ChatPanel.axaml.cs
- OrchestratorIDE.UnitTests/ContextFabricScriptedRuntime.cs

## One-PR definition
Branch: feat/cf5-orcchat-library
Exit gate: user can add darwin-origin-species-primary.pdf, wait for indexing,
start a fresh chat, attach the corpus, ask a cross-chapter question,
and receive a cited, verified answer — with no manual context management.
