# TheOrc — Quick Start

This is the fastest path from a fresh install to your first successful run. Follow these steps in order and you'll have TheOrc running a real task in about 10 minutes.

For project vocabulary, see [GLOSSARY.md](GLOSSARY.md). For the bigger picture, see [ARCHITECTURE.md](ARCHITECTURE.md).

---

## 1. Start Your Inference Backend

TheOrc needs an AI model to talk to. If you're using Ollama (the most common local option), start it first:

```powershell
ollama serve
```

Then confirm it's running:

```powershell
ollama list
```

TheOrc connects to Ollama at `http://localhost:11434` by default.

---

## 2. Pull a Model

You need at least one AI model installed. A coding-capable model is the best choice for your first run. How much VRAM (video memory) your GPU has determines which model fits:

- **6 to 8 GB VRAM** — `qwen2.5-coder:7b`
- **10 to 16 GB VRAM** — `qwen2.5-coder:14b`
- **More VRAM** — see [MODEL_GUIDE.md](MODEL_GUIDE.md) for better options

To pull a model:

```powershell
ollama pull qwen2.5-coder:14b
```

Wait for the download to finish before moving on.

---

## 3. Launch TheOrc

Open the app. Before doing anything else, check these four things in the status bar at the bottom:

- The workspace badge shows a folder path
- The build stamp (version number) is visible
- The model name is filled in
- No Ollama connection error is shown

If something looks wrong, see [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## 4. Open a Workspace

A workspace is the project folder TheOrc will work inside. Point it at a real folder on your computer — a code project, a folder of files, anything.

Click the workspace badge in the status bar and select your folder.

Why this matters: file reads and writes are rooted here, the file explorer shows this folder, and git checkpoints only work when the workspace is a git repository.

---

## 5. Open the In-App Help

Press `F1` to open the Help window.

This confirms the docs viewer is working and you can read guides inside the app without switching windows.

---

## 6. Run a Small Single-Agent Task

Switch to **Single** mode using the mode bar at the top. Give TheOrc a safe, read-only task to start:

```text
Read the README and summarize the build steps without modifying any files.
```

TheOrc should produce a plan first. Nothing runs yet — you're still in the planning step.

---

## 7. Approve an Execution

Now try a low-risk task that actually does something, like reading a file or making a tiny change.

Watch what happens:

- Shell commands show an approval card before running — you click to allow
- File writes show a diff before applying — you approve the change
- The status bar stays live and responsive
- Activity streams in as it happens, not just at the end

This is the approval flow that keeps you in control.

---

## 8. Try Swarm Mode

Switch to **Swarm** mode. Check that:

- The Boss, Coder, and Researcher model pickers are populated
- Capability badges appear under each picker showing what that model can do
- The Launch button only becomes active once you have a valid workspace and model setup

If you want to test a model's tool-call ability, click the **Probe Now** button.

---

## 9. Model Wiki (retired UI, data still tracked)

The Model Wiki / Lab window (`Models → Model Wiki / Lab...`) was retired when WPF was
deleted and has not been rebuilt for Avalonia — there is no menu item to click yet.
The underlying data (model details, catalog scores, local observations, capability-test
results) is still tracked in the background and drives runtime behavior; it's just not
exposed in a window today. See [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) for what
the rebuilt surface is planned to look like.

---

## 10. Check for Updates

Click the **⬆ Update** button in the mode bar.

This opens the Update Center, where you can see if a newer version of TheOrc is available and install it with one click. If you see a gold dot on the button, an update is already waiting.

---

## 11. Optional: Set Up HIVE MIND

If you have more than one PC running TheOrc, switch to the **Hive** mode. Your other machines should appear automatically within about 15 seconds if they're on the same network.

Approve the pairing on both machines and you're connected. See [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) for the full setup guide.

---

## What to Read Next

- [USER_GUIDE.md](USER_GUIDE.md) — the full guide to the app, modes, and approval system
- [SWARM_GUIDE.md](SWARM_GUIDE.md) — how multi-agent swarm runs work
- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) — how to turn swarm runs into a trained AI adapter
