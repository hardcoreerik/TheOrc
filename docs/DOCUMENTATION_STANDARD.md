# TheOrc — Documentation Standard

> This document defines the standards for all files in `docs/`. Apply these rules
> when creating new docs or updating existing ones.

---

## File Naming

- All doc files use `UPPER_SNAKE_CASE.md`
- File names describe the feature or topic, not the audience
- Examples: `SWARM_GUIDE.md`, `HARDWARE_GUIDE.md`, `TROUBLESHOOTING.md`

---

## Doc File Structure

Every doc file follows this pattern:

```markdown
# TheOrc — <Title>

> Optional callout: status note, disclaimer, or phase flag.
> Use this for: "Phase 2 ACTIVE", "Windows only", "Planned feature".

---

## Section Heading

Content...

---

## Another Section

Content...
```

Rules:
- First heading is always `# TheOrc — <Title>`
- Top-level sections use `##` with horizontal rules (`---`) between them
- Subsections use `###`
- No deeper nesting than `####`

---

## Accuracy First

Every claim in the docs must reflect current implementation.

**Do not document planned behavior as current behavior.** If something is planned but not
implemented, mark it explicitly:

- `Planned 🔲` in tables
- `> **Planned:** ...` in callout blocks
- `*(not yet implemented)*` inline

**Do not document behavior that has been observed to fail differently.** If the model guide
says a model passes something, that should be backed by an actual test result.

---

## Implementation vs Planned

| Marking | Meaning |
|---|---|
| ✅ | Implemented and stable |
| ⚠️ | Implemented but Beta — may have known rough edges |
| 🔬 | Experimental — present but not production-ready |
| 🔲 | Planned — not yet implemented |

Use these consistently in feature lists and roadmap sections.

---

## Model-Specific Claims

Before documenting behavior for a specific model:

1. Check `OrchestratorIDE/Resources/model-wiki-observations.json` for recorded observations
2. Check `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl` for capability test results
3. Do not assert a model "passes" or "fails" without citing a test result or observation

When citing a test result, include:
- The test ID (e.g., `T06`, `FileWriteLarge`)
- The date of the observation
- The evidence (e.g., `opens=2, closes=0, len=2000`)

Example:
> **T06 result (2026-06-09):** `nemotron-3-nano:4b-q8_0` — FAIL. Zero files written across
> 3 passes. Pass 1: `opens=2, closes=0, len=2000`. Pass 2: `opens=2, closes=0, len=85`.
> Pass 3: empty response.

---

## Training Pit Constraints

The following must never appear in any doc file:

- Instructions to start Phase 3 training
- New swarm execution roles beyond RESEARCHER, CODER, UIDEVELOPER, TESTER
- Claims that `theorc-boss:gemma4` is a LoRA-trained model (it is a Modelfile wrapper)
- Instructions to recreate the `theorc-boss:gemma4` Ollama model (it is already live)

The `validate_dataset.py` quality values are `gold`, `silver`, `draft`, `rejected`.
Do not document `good`, `edge`, or `bad` as valid quality labels — they are not supported.

---

## AutomationIds

When documenting UI automation IDs, use the exact `AutomationId` values from the XAML.
Do not invent or paraphrase them.

Verified IDs (from `MainWindow.xaml` and component XAML):

```
MainWindow                    Root application window
Mode.Single / Mode.Swarm / Mode.Chat
StatusBar.Workspace
Menu.Models
Menu.ModelWiki
Menu.ModelCapabilityTest
Panel.SwarmBoard
Swarm.GoalInput / Swarm.Launch
ModelWiki.Root
ModelWiki.Search / ModelWiki.ModelList / ModelWiki.Detail
ModelWiki.RunCapabilityTest
ModelCapTest.Root
ModelCapTest.Cancel / ModelCapTest.Close
```

---

## Code Blocks

Use triple-backtick code blocks with language tags:

```powershell
ollama serve
```

```bash
python training_pit/scripts/validate_dataset.py train_v1.jsonl
```

```csharp
// C# code
```

Use inline backticks for: file names, paths, AutomationIds, model tags, field names,
tool names, and short code fragments.

---

## Path and Environment Variables

Use the standard Windows environment variable notation:
- `%APPDATA%\OrchestratorIDE\` — not `$env:APPDATA\OrchestratorIDE\`
- `%TEMP%\TheOrc\` — not `$TEMP/TheOrc/`

When a path appears in both Windows and WSL2 contexts, document both.

---

## Links Between Docs

Use relative Markdown links:

```markdown
See [MODEL_GUIDE.md](MODEL_GUIDE.md) for profile scores.
See [TROUBLESHOOTING.md#agent-runs-but-writes-no-files](TROUBLESHOOTING.md)
```

All docs link back to `README.md` implicitly via the docs index.

---

## Tables

Prefer tables over bullet lists for: comparison data, schema fields, keyboard shortcuts,
file paths, and any enumeration with 3+ attributes.

| Column | Content |
|---|---|
| Short name | Brief description |

Use `---` separator row immediately after the header row (no spaces).

---

## What Not to Include

- Do not create empty placeholder docs — every file must have real content
- Do not include installation instructions for tools not used by TheOrc
- Do not duplicate content across files — link to the authoritative source instead
- Do not document Windows-only behavior as if it applies cross-platform
- Do not include personal machine paths, credentials, or identifiable information

---

## Adding a New Doc File

1. Name it `UPPER_SNAKE_CASE.md` in `docs/`
2. Follow the structure template above
3. Add it to `docs/README.md` in the correct section
4. Link to it from related doc files

---

## Updating Existing Docs

When a feature changes:
1. Update the doc in the same PR as the code change
2. If a planned item ships, change `🔲 Planned` to `✅` or `⚠️ Beta`
3. If a test result changes, update the evidence with the new date and result
4. Increment the Version History table if the file has one

---

## Version History Tables

Some docs include a version history table at the bottom:

```markdown
| Version | Date | Notes |
|---|---|---|
| 1.0 | 2026-06-09 | Initial version |
| 1.1 | 2026-06-09 | Added X section |
```

Include this only in docs that change frequently (CONTRIBUTING.md, schema docs).
Omit from guides and reference docs — git history is sufficient version tracking there.
