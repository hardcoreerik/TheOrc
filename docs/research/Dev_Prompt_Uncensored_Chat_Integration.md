# Dev Prompt — Add Uncensored Chat Models to TheOrc (app + installer) + RLHF Research

> Paste everything inside the code fence below into a fresh Claude Code / Grok session
> opened on the TheOrc dev tree. It is self-contained and grounded in the real repo
> (verified 2026-06-21 against dev HEAD `8c4cceb`, release v1.9.4).

---

```
# TheOrc: Add Uncensored Chat Models + RLHF Research

## ROLE & MODE
You are working in the TheOrc C# codebase. Work methodically. Verify every external
fact (Hugging Face repo IDs, GGUF filenames, file sizes, SHA-256) against the live
source before writing it into code. NEVER assume a path, field name, repo id, or schema —
if you cannot verify something required to proceed, STOP and ask rather than inventing it.

## CRITICAL — WORK IN THE RIGHT DIRECTORY
The canonical dev tree is:  F:\Ai\OrchestratorIDE-dev
  - It is a git repo (remote: https://github.com/hardcoreerik/TheOrc.git), branch `master`,
    in sync with GitHub at the time this prompt was written. Latest release: v1.9.4.
DO NOT work in F:\Ai\TheOrchestrator — that is a DIFFERENT, older Python project, not a
git repo, and NOT the app you are changing. If your working directory is
F:\Ai\TheOrchestrator, switch to F:\Ai\OrchestratorIDE-dev first. (Other checkouts like
OrchestratorIDE, OrchestratorIDE-dev, TheOrc, *-sync* exist on disk — only
OrchestratorIDE-dev is canonical. Confirm with `git remote -v` and `git log -1`.)

The reference research lives in F:\Ai\TheOrchestrator\ (read-only inputs):
  - SillyTavern_Research.md          — how SillyTavern does uncensored chat
  - Uncensored_Models_Tier_Guide.md  — the three target models + VRAM tiers + quants
Read both before coding. Skim the SillyTavern repo
(https://github.com/SillyTavern/SillyTavern) only if those docs leave a gap.

## BACKGROUND THE CHANGE MUST RESPECT (verified facts about this repo)
- The desktop app is **Avalonia-only** (WPF was deleted 2026-06-20). App project:
  OrchestratorIDE.Avalonia. Core library: OrchestratorIDE.
- There are TWO separate model catalogs, with DIFFERENT schemas and DIFFERENT JSON files.
  You must update BOTH.

  (A) APP catalog — a metadata OVERLAY, NOT a download source:
      Data:   OrchestratorIDE/Resources/curated-models.json   (embedded resource;
              JS-style // comments are allowed and stripped at load)
      Loader: OrchestratorIDE/Services/Models/CuratedModelCatalog.cs
      Model:  CuratedModelEntry  (see OrchestratorIDE/Models/)
      Fields (snake_case in JSON): id, name, huggingface_id, ollama_name, publisher,
        architecture, parameters_b, context_k, description, intended_use, tool_use,
        swarm_roles[], swarm_capable, vram_min_gb, vram_recommended_gb, cpu_ok,
        recommended_quant, quality_stars, tags[]
      NOTE: this catalog deliberately carries NO download URL. Do not add one here.

  (B) INSTALLER manifest — the ACTUAL download source (url + sha256):
      Data:   Setup/model-manifest.json
      Model:  OrchestratorSetup/Models/ModelEntry.cs
      Display:OrchestratorSetup/Pages/ModelPage.axaml(.cs)
      Download:OrchestratorSetup/Services/DownloadService.cs
      Hardware:OrchestratorSetup/Services/HardwareDetector.cs (+ HardwareDetectPage)
      Fields: id, name, description, publisher, quantization, parameters_b, vram_min_gb,
        vram_recommended_gb, size_bytes, url, sha256, quality_stars, cpu_ok, context_k,
        profiles[], tags[], ollama_name, swarm_capable
      Helpers already present: ModelEntry.FitsInVram(availableVramGb),
        ModelEntry.PartnerBadge/HasPartnerBadge (coloured chip),
        ModelEntry.OllamaOnly (true when no direct GGUF Url).
      Recommendation already exists: the ModelPage view-model calls
        UpdateRecommendedModel() ("hardware-matched recommendation"). REUSE it —
        do not write a new recommender.

- The INSTALLER IS MID-REVAMP. Phases 1-2 of INSTALLER_REVAMP_SPEC.md (docs/ in repo)
  shipped 2026-06-21: WPF→Avalonia port + IPlatformInstaller abstraction. There is a
  native-first RuntimeSetupPage. Do NOT undo or fight that in-flight work; your change is
  additive (new model entries + badge/recommendation wiring), nothing structural.

## STEP 1 — Map before you write
Use the `codegraph-query` skill (graph DB at .orc/theorc.db) and/or Grep to confirm the
exact current shape of an existing entry in BOTH curated-models.json and
model-manifest.json. Read one full existing entry from each and mirror its formatting,
field order, and conventions exactly. Identify the existing badge/tag rendering path in
ModelPage and in the app's model UI so you reuse it instead of inventing a new control.

## STEP 2 — Verify the three models on Hugging Face (do NOT trust the tier guide blindly)
The repo IDs/sizes in Uncensored_Models_Tier_Guide.md came from web search and MUST be
re-confirmed live. For the installer manifest you need a real **GGUF quant file** with a
working download URL, exact size_bytes, and (if published) sha256 — the base
`cognitivecomputations/*` repos are usually safetensors, so the GGUF almost certainly
lives in a quant repo (e.g. bartowski / mradermacher). Confirm the exact file for the
specified quant exists and resolves. If a repo/quant is gone, pick the closest current
equivalent from the same Dolphin line and FLAG the substitution in your report — never
silently swap.

Target models (tier → quant):
  1. Dolphin-Yi-34B           — Q3_K_M  — tier: 16GB VRAM
  2. Dolphin-Mistral-Nemo-12B — Q4_K_M  — tier: 6GB VRAM
  3. Dolphin-Qwen2.5-3B       — Q3_K_M  — tier: CPU / low-VRAM

## STEP 3 — Idempotency
Before adding each model, search BOTH catalogs for it (by name / huggingface_id /
ollama_name). If present, do NOT duplicate — just ensure it carries the badge + the
recommendation wiring from STEP 4, and report it as "already present, updated".
(At time of writing, neither catalog contains any Dolphin/uncensored entry — but verify.)

## STEP 4 — Add and wire the three models
For EACH (that is not already present):
  - APP: add a curated-models.json entry matching schema (A), populated with verified
    values. Set vram_min_gb / vram_recommended_gb / cpu_ok to match the tier
    (Yi-34B: ~16/24, GPU only; Nemo-12B: ~6/8, GPU; Qwen-3B: cpu_ok=true, low vram).
    Set recommended_quant to the tier quant. Add a tag that the UI can surface as the
    badge text "Best for uncensored chat" (reuse the existing tag/badge mechanism you
    found in STEP 1; if there is genuinely no badge/tag surface to carry that label,
    STOP and ask before adding a new UI element).
  - INSTALLER: add a model-manifest.json entry matching schema (B), with the verified
    GGUF url + size_bytes + sha256 (or ollama_name + leave Url empty so OllamaOnly=true,
    if you deliberately choose the Ollama path — state which you chose and why). Carry the
    "Best for uncensored chat" badge via the existing badge/tag path.
  - RECOMMENDATION: ensure the installer's existing UpdateRecommendedModel() /
    FitsInVram() hardware logic naturally surfaces the right model per detected VRAM:
        ≥16 GB  -> Dolphin-Yi-34B
        ≥6 & <16 GB -> Dolphin-Mistral-Nemo-12B
        otherwise (low/no VRAM, cpu_ok) -> Dolphin-Qwen2.5-3B
    If the existing recommender keys off a single "recommended" pick rather than tiers,
    integrate so these three are correctly ranked/recommended by hardware — do not bolt on
    a parallel system. Mirror the same hardware-aware highlighting in the app catalog UI
    if it has an equivalent surface.

## STEP 5 — Spawn a Haiku research subagent on RLHF (run in parallel with coding)
Per the standing model-routing rule (pure read/report work → Haiku), spawn ONE subagent
on the Haiku model with a self-contained brief (it has no memory of this session). Tell it
to research and write `RLHF_Research.md` in F:\Ai\OrchestratorIDE-dev\docs\research\ :
  - What RLHF is and the precise mechanism by which it produces refusals.
  - Published theory/techniques for REVERSING or neutralizing safety alignment in
    open-weight models — specifically abliteration / refusal-direction ablation,
    refusal-removal fine-tuning, and activation steering / representation engineering.
    Cite papers + repos. THEORY AND FEASIBILITY ONLY — no operational harm instructions.
  - Feasibility of a built-in "de-alignment / abliteration" capability inside TheOrc,
    explicitly tied to ORC ACADEMY (which already does local LoRA fine-tuning) and the
    Native Runtime / LLamaSharp layer. Frame toward whether it's a viable differentiator.
Relay a summary of its findings in your final report.

## STEP 6 — Verify
- Build the affected projects locally: OrchestratorIDE.Avalonia (or OrchestratorIDE) and
  OrchestratorSetup. (Note: some HIVE nodes lack the dotnet SDK — build on the local
  machine. If you cannot build, say so explicitly; do NOT claim success you didn't verify.)
- Confirm both JSON files still parse (the app loader strips // comments; the installer
  manifest is strict JSON — no comments there).
- Sanity-check the recommendation for representative VRAM values (e.g. 24, 8, 0 GB).
- Do NOT fabricate test results. "Not run" with a reason is acceptable; a false "passed"
  is not.

## GROUND RULES (hard constraints)
- LOCAL-ONLY at runtime: no cloud inference, no telemetry, no secrets in the repo
  (Pit Boss / project hard rules). Download URLs pointing at Hugging Face for the
  installer are fine — that's a user-initiated model download, not a runtime call.
- New .cs files get the AGPL header used across the repo:
    // Copyright (C) 2025-present hardcoreerik / TheOrc contributors
    // SPDX-License-Identifier: AGPL-3.0-or-later
  (You are mostly editing JSON + existing files; only add headers if you create .cs files.)
- Match surrounding conventions, field order, comment density. No drive-by refactors.
- Scope = the 3 model entries in both catalogs + badge + hardware recommendation, plus the
  Haiku RLHF doc. Nothing else.
- Git: you are on `master`. Create a feature branch before committing
  (e.g. `feat/uncensored-chat-models`). Do NOT commit, push, or tag unless I explicitly
  ask — make the changes in the working tree and report.
- If any required path/field/url/schema is ambiguous or missing, STOP and ask a specific
  question instead of guessing.

## FINAL REPORT (required)
1. Confirmed working directory + `git log -1` line you saw.
2. The existing entry shape you mirrored, with file:line refs for both catalogs.
3. Per model: NEW vs ALREADY-PRESENT; verified HF repo id + GGUF filename + size + sha256
   (or chosen Ollama path); any substitution made and why.
4. Every file changed (app vs installer).
5. How hardware-based recommendation surfaces each model, and the VRAM values you checked.
6. Build/verify result (or honest "not run" + reason).
7. Summary of the Haiku RLHF findings + path to the new RLHF_Research.md.
```

---

## Why this version is hardened (notes for Erik, not part of the prompt)

- **Right repo, stated first.** The launch dir `F:\Ai\TheOrchestrator` is a stale Python
  project and not even a git repo; the real tree is `F:\Ai\OrchestratorIDE-dev` (master,
  synced to GitHub `8c4cceb`, release v1.9.4). The prompt forces the agent there.
- **Two catalogs, two schemas, named exactly.** App overlay `curated-models.json`
  (`CuratedModelCatalog.cs`, no URLs) vs installer `Setup/model-manifest.json`
  (`ModelEntry.cs`, real `url`+`sha256`). The agent can't conflate them.
- **Reuses what exists.** `UpdateRecommendedModel()` + `FitsInVram()` already do
  hardware-matched recommendation; `PartnerBadge` already renders chips. The prompt says
  reuse, not reinvent.
- **Installer-in-flight guard.** The installer just got rewritten today (Phases 1-2 of
  `INSTALLER_REVAMP_SPEC.md`); the prompt keeps the change additive so it won't collide.
- **GGUF reality check.** Base `cognitivecomputations` repos are safetensors; real GGUF
  quants live in `bartowski`/`mradermacher`. The prompt makes the agent find the actual
  downloadable file + size + sha256, and flag substitutions.
- **Anti-hallucination + STOP-and-ask** at every external-fact and ambiguity point;
  honest build reporting (accounts for the no-SDK HIVE-node gotcha); branch-don't-push.
