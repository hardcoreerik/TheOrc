# TheOrc — Quick Start

> **Windows only.** .NET 10 required. Ollama required (or llama.cpp — see [INSTALLATION.md](INSTALLATION.md)).
>
> **Honest local-model disclaimer:** model performance varies by model size, quantization, backend,
> VRAM, prompt format, and tool-call reliability. A model that passes short tool calls may still
> fail large file writes. See [MODEL_GUIDE.md](MODEL_GUIDE.md) for details.

---

## Step 1 — Install and start Ollama

Download Ollama from [ollama.ai](https://ollama.ai) and start it:

```powershell
ollama serve
```

Verify it's running:
```powershell
ollama list
```

By default Ollama listens at `http://localhost:11434`. If you run Ollama on a separate machine,
note the host address — you'll configure it in TheOrc Settings.

---

## Step 2 — Install a recommended model

For a first run, pull a model that fits your VRAM:

| Your VRAM | Recommended first model | Command |
|---|---|---|
| 6 GB | Qwen 2.5 Coder 7B Q4 | `ollama pull qwen2.5-coder:7b` |
| 8 GB | Qwen 2.5 Coder 7B Q5 | `ollama pull qwen2.5-coder:7b-instruct-q5_k_m` |
| 10–12 GB | Qwen 2.5 Coder 14B Q4 | `ollama pull qwen2.5-coder:14b` |
| 16 GB | Qwen 2.5 Coder 14B Q5 | `ollama pull qwen2.5-coder:14b-instruct-q5_k_m` |

> ⚠️ **Nemotron Nano 4B note:** This model starts `write_file` JSON but truncates before
> closing braces on large payloads. It will NOT complete T06-style autonomous file writing.
> Use it for short tasks, lightweight testing, or chat only. See [MODEL_GUIDE.md](MODEL_GUIDE.md).

---

## Step 3 — Launch TheOrc

**From a release build:**
Run `OrchestratorIDE.exe` from the portable zip or installer output.

**From source:**
```powershell
dotnet run --project OrchestratorIDE/OrchestratorIDE.csproj
```

---

## Step 4 — Point TheOrc at your Ollama instance

On first launch, open **Settings** (gear icon in the activity bar, bottom left).

- Set **Ollama Host** to your Ollama address (default: `http://localhost:11434`)
- The model dropdown will populate once the host is reachable

If you see a red Ollama status indicator in the status bar, Ollama is not reachable.
Check that `ollama serve` is running and the host address is correct.

---

## Step 5 — Open a workspace

Click the **📁 workspace badge** in the top bar (shows the current workspace path, or
"No workspace" if none is open).

Select a folder you want to work in. TheOrc will use this as the root for all file operations.

> **Git auto-checkpoint:** If the workspace has a git repo, TheOrc commits a checkpoint
> before every Execute run. Nothing is lost.

---

## Step 6 — Try Single Agent

Make sure the mode selector (top left) shows **Single** (not Swarm or Chat).

Type a request in the chat input and press **Enter** or click **Plan**.

TheOrc will:
1. Call the model with your goal and produce a **Plan** — a list of steps it intends to take
2. Show you the plan for review
3. On **Execute** approval: carry out the steps, showing diffs before any file write

> **Important:** The first time you click Execute, you may see a trust prompt. Start in
> **Guarded** mode (default) — every file write and shell command requires approval.

---

## Step 7 — Try Swarm mode

Switch the mode selector to **Swarm**.

Open the **Swarm Board** tab (or it may open automatically in Swarm mode).

On the Swarm Board:
- Select a **Boss model** (needs planning capability — see [SWARM_GUIDE.md](SWARM_GUIDE.md))
- Select a **Coder model** and **Researcher model**
- Enter a goal in the Goal input
- Click **Launch Swarm**

> **OLLAMA_NUM_PARALLEL requirement:** Swarm mode runs multiple models concurrently.
> Set `OLLAMA_NUM_PARALLEL=3` (or higher) in your environment before starting Ollama.
> Without this, workers queue and performance degrades significantly.

---

## Step 8 — Open Model Wiki / Lab

In the menu bar: **Models → Model Wiki / Lab…**

This opens a non-modal window with:
- A searchable, filterable catalogue of all models TheOrc knows about
- Per-model scores (Boss, Coder, Researcher, Tester)
- Built-in observations from local tests (e.g. T06 Nemotron Nano results)
- GOBLIN MIND probe results if you've run them

Browse models and use the **Run Capability Test** button to open the capability test dialog.

---

## Step 9 — Run a Model Capability Test

In the menu bar: **Models → Run Model Capability Test…**

Or click **Run Capability Test** inside the Model Wiki window.

The dialog lets you:
- Select a model and test level (Small / Medium / Large / All three)
- Click **▶ Run** to start
- Watch the phase strip: Idle → Sending → Waiting → Received → Analyzing → Done
- See live test cards showing each test's state and result
- Read the colored activity feed for detailed output

Results are saved to `%APPDATA%\OrchestratorIDE\model-wiki\results.jsonl` and
appear in the Model Wiki detail pane on next open.

> Tests run in an isolated temp workspace (`%TEMP%\TheOrc\ModelTests\`).
> They do NOT touch your active project workspace.

---

## Step 10 — Understand T06-style autonomous file writing

**T06** is the internal benchmark test for single-agent autonomous code generation:
the agent is given a coding goal and must write working files without human approval of each step.

T06 requires a capable model. It **will fail** with:
- Models under ~7B parameters
- Models that truncate long JSON payloads (confirmed: Nemotron Nano 4B)
- Models with poor tool-call reliability

If you're trying T06-style work and "the agent runs but writes no files," see
[TROUBLESHOOTING.md](TROUBLESHOOTING.md#agent-runs-but-writes-no-files).

---

## What's Next

| Goal | Doc |
|---|---|
| Understand all the mode options | [USER_GUIDE.md](USER_GUIDE.md) |
| Pick the right model for your GPU | [MODEL_GUIDE.md](MODEL_GUIDE.md) |
| Configure Swarm mode properly | [SWARM_GUIDE.md](SWARM_GUIDE.md) |
| Understand The Training Pit | [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) |
| Diagnose a problem | [TROUBLESHOOTING.md](TROUBLESHOOTING.md) |
