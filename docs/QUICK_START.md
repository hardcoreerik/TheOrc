# TheOrc — Quick Start

> This is the fastest verified path from a fresh install to a successful local run. For the bigger picture, see [ARCHITECTURE.md](ARCHITECTURE.md). For project vocabulary, see [GLOSSARY.md](GLOSSARY.md).

---

## What You Are Doing

You are proving four things:

1. TheOrc can reach your inference backend.
2. A usable model is installed.
3. The app is pointed at a real workspace.
4. A simple plan can become an approved execution run.

---

## 1. Start Your Inference Backend

If you are using Ollama, start it first:

```powershell
ollama serve
```

Check that it responds:

```powershell
ollama list
```

The default host used by TheOrc is `http://localhost:11434`.

---

## 2. Pull A First Model

A safe first-run choice is a coding-capable model that fits your VRAM. The docs avoid pretending one model is perfect everywhere because local behavior depends on quantization, VRAM headroom, and tool-call reliability.

Practical starting points:

- 6 to 8 GB VRAM: `qwen2.5-coder:7b`
- 10 to 16 GB VRAM: `qwen2.5-coder:14b`
- higher VRAM: use [MODEL_GUIDE.md](MODEL_GUIDE.md) and [MODEL_WIKI_AND_LAB.md](MODEL_WIKI_AND_LAB.md) for a better fit

Example:

```powershell
ollama pull qwen2.5-coder:14b
```

---

## 3. Launch TheOrc

Open the app and confirm these shell signals:

- the workspace badge is visible in the status bar
- the build stamp appears in the status bar
- the model label is populated
- the app does not show an Ollama connectivity problem

If something is off already, jump to [TROUBLESHOOTING.md](TROUBLESHOOTING.md).

---

## 4. Open A Workspace

Use the workspace badge or the file-opening flow to point TheOrc at a real project folder.

Why this matters:

- file tools are rooted to the workspace
- the file explorer uses that root
- git checkpoints only happen when the workspace is a git repo
- swarm run artifacts and dataset captures are written relative to that root

---

## 5. Read The In-App Help Once

Press `F1` to open the Help window.

This confirms two things:

- the embedded docs viewer is working
- you can navigate guides in-app without leaving the shell

---

## 6. Run A Small Single-Agent Plan

Switch to `Single` mode and ask for a tiny change or inspection task, for example:

```text
Read the README and summarize the build steps without modifying files.
```

This should produce a plan first. Nothing should execute yet.

---

## 7. Approve A Safe Execute Run

After the plan looks sane, execute a low-risk task that can complete with reads or a tiny write.

Watch for these behaviors:

- shell commands show an approval card before running
- file writes show a diff before applying
- the status bar remains responsive
- activity events stream live instead of only appearing at the end

---

## 8. Try Swarm Mode

Switch to `Swarm` mode and confirm:

- the Boss, Coder, and Researcher model pickers are populated
- capability badges appear under each picker
- the Launch button is enabled only when the workspace and slot/model gate are valid

If needed, use the `Probe Now` button to open the tool-call probe window.

---

## 9. Confirm Model Intelligence Surfaces

Open `Models -> Model Wiki / Lab...` and check that you can see:

- model detail
- local observations
- trends strip
- capability test entry point
- comparison entry point

This is the fastest way to verify that TheOrc is not treating model choice as a blind string.

---

## 10. Know The Next Three Guides

After this quick start, the best next reads are:

- [USER_GUIDE.md](USER_GUIDE.md) for the shell and trust model
- [SWARM_GUIDE.md](SWARM_GUIDE.md) for multi-agent orchestration
- [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for the capture-to-adapter loop
