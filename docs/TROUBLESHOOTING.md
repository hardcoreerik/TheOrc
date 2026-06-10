# TheOrc — Troubleshooting

---

## Startup / Connection

### Red Ollama indicator in the status bar

TheOrc cannot reach the Ollama host.

1. Verify Ollama is running: `ollama list` in a terminal
2. If Ollama isn't running, start it: `ollama serve`
3. Check **Settings → Ollama Host** — default is `http://localhost:11434`
4. If Ollama is on another machine, check Windows Firewall and verify the address
5. Restart TheOrc after fixing the connection

### Model list is empty

- The Ollama host must be reachable before TheOrc starts
- Pull at least one model: `ollama pull qwen2.5-coder:7b`
- Restart TheOrc after pulling if the model list was already showing empty

### "OrchestratorIDE.exe not found" or build error

Build the project first:
```powershell
dotnet build OrchestratorIDE/OrchestratorIDE.csproj
```
Or set the `ORCHESTRATOR_EXE` environment variable to the full path of the binary.

### "SmartScreen prevented an unrecognized app"

The executable is unsigned. Click **More info → Run anyway**.
This is expected for open-source apps without code-signing. The source is available for inspection.

### dotnet build fails: "SDK not found"

Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).
Verify with `dotnet --version` — should show `10.x.x`.

---

## Agent Runs But Writes No Files

This is the most common issue when testing with small models.

### Diagnosis checklist

1. **Check the activity log** — does it show the model "thinking" but no tool calls appear?
2. **Check for truncation messages** — the log shows `[RESULT:fail]` or truncation warnings
3. **Run the Model Capability Test** — `Models → Run Model Capability Test…`
   - If FileWriteSmall passes but FileWriteLarge fails → payload size ceiling
   - If FileWriteSmall fails → the model cannot produce valid tool-call JSON at all

### Confirmed small-model failures

- `nemotron-3-nano:4b-q8_0` — starts `write_file` JSON but truncates before closing braces
  on passes 1 and 2; returns empty response on pass 3. Zero files written across 3 T06 passes.
- `nemotron-3-nano:4b` — same or worse than Q8 (lower quantization precision)

### Fix

Switch to a model ≥ 7B parameters for tasks that require file writing.

Recommended minimum for autonomous coding:
- `qwen2.5-coder:7b` (6 GB VRAM)
- `qwen2.5-coder:14b` (10–12 GB VRAM)

The 4B models are suitable for: short chat, lightweight tester role, log summarization.
They are not suitable for: primary single-agent coding, multi-file autonomous generation.

---

## Model Generates a Plan but Execution Fails

### Plan looks good, but Execute produces no output

- The model may be hallucinating tool calls that are not in the registered toolset
- Check the activity log for `UnknownToolCard` entries — these are tool calls TheOrc couldn't parse
- Try a model with GOBLIN MIND probe results — run `Models → Run Tool Call Tests…`

### Plan is a single task ("Execute goal" with empty description)

This is planning collapse. The boss model is outputting a degenerate single-task plan.

- Switch to `theorc-boss:gemma4` (Modelfile-calibrated, proven in swarm benchmarks)
- Do not use raw `gemma4:12b` without the Modelfile as a boss model

---

## Swarm-Specific Issues

### Workers queue instead of running in parallel

`OLLAMA_NUM_PARALLEL` is not set or is too low.

```powershell
$env:OLLAMA_NUM_PARALLEL = "4"
ollama serve
```

Workers will run sequentially without this. Performance degrades significantly.

### Swarm hangs — one worker stuck at RUNNING

- The model may be generating a very long response (large file)
- Check if VRAM is exhausted (Task Manager → Performance → GPU)
- Use **STOP** to cancel the run
- Try a faster worker model or reduce task complexity

### Boss assigns impossible tasks to wrong roles

- Check the task type — TESTER tasks should not have `write_file` requirements
- The boss model may be mis-routing; steering the run with the steering bar may help
- For persistent mis-routing, switch to a higher-quality boss model

---

## File Write and Diff Viewer Issues

### DiffViewer shows empty diff

- The model may be writing identical content to an existing file (no diff to show)
- Or the write_file path is wrong — check the path in the tool call

### File was approved but not written

- Check Windows file permissions on the workspace folder
- The workspace path may contain special characters — try a simpler path

### "Reject" closed the diff but the agent keeps trying the same write

- The model received the rejection notice and is retrying with the same content
- Reject again or provide a clarification in the chat input

---

## Git Checkpoint Issues

### No checkpoint appears before Execute

- The workspace folder must contain a `.git` folder (initialized git repo)
- Run `git init` in your workspace if there is no git repo
- Check the activity log — checkpoint failures are logged but do not block execution

### Checkpoint commit appears on the wrong branch

- TheOrc commits on whatever branch is currently checked out
- Switch to the desired branch before running Execute

---

## Model Wiki / Lab Issues

### Model Wiki window doesn't open

- Check that the app is fully loaded (status bar shows connected Ollama indicator)
- Try `Models → Model Wiki / Lab…` again — if already open, it will be activated
- The window is single-instance — it cannot be opened twice

### Capability test shows FAIL for a capable model

- The test runs in an isolated temp workspace (`%TEMP%\TheOrc\ModelTests\`)
- Ensure Ollama is not under heavy load from other processes
- Check the brace counts in the activity feed — `opens≠closes` means truncation
- Try running the test again; a single run can be affected by thermal throttling

### Model observations not showing in detail pane

- Built-in observations are loaded from `OrchestratorIDE/Resources/model-wiki-observations.json`
- User test results are loaded from `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl`
- Both files are read at window open time — close and reopen the window after running tests

---

## FlaUI UI Test Issues

### Tests fail to find the application window

- The app must be built first: `dotnet build OrchestratorIDE/OrchestratorIDE.csproj`
- The app must NOT already be running — FlaUI tests launch their own instance
- Do not move the mouse or type during FlaUI test runs

### T07/T08 fail sporadically

- Another application's window may be overlapping TheOrc during the test
- Close unnecessary applications before running UI tests
- Run tests from the command line, not from the IDE Test Explorer (it requires an interactive desktop session)

### T06 fails with "model not capable"

T06 is the autonomous file-writing test. It requires a model ≥ 7B parameters.
Do not run T06 with Nemotron Nano 4B or similar small models.

See [TESTING_GUIDE.md](TESTING_GUIDE.md) for full test descriptions and requirements.

---

## Logs and Diagnostic Files

| Item | Location |
|---|---|
| Settings | `%APPDATA%\OrchestratorIDE\settings.json` |
| Model Wiki test results | `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl` |
| GOBLIN MIND probe results | `%APPDATA%\OrchestratorIDE\tool-call-profiles.json` |
| Swarm metrics | `swarm-metrics.json` in the solution root |
| Dataset staging | `.orc/swarm/dataset-staging/` in the workspace |
| FlaUI test recordings | `%APPDATA%\OrchestratorIDE\Recordings\` |
| Git checkpoints | `.git/` in your workspace |
