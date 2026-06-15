# TheOrc — FAQ

Quick answers to common questions. For deeper reading, follow the links at the end of each answer.

For terminology, see [GLOSSARY.md](GLOSSARY.md).

---

## What is TheOrc?

TheOrc is a Windows desktop app for running AI coding assistants locally on your own PC. It handles everything from a single AI working on a task, to a full team of specialized AI workers, to training your own custom AI model — all without sending your code to a cloud service by default.

---

## Does it send my code to a cloud service?

Not by default. TheOrc is built around local AI models running on your own hardware. The one exception is if you specifically configure it to use a remote model endpoint — in that case, your prompts go to wherever you pointed it.

---

## What are goblins?

Goblins are the four specialized AI worker roles in Swarm mode: **RESEARCHER**, **CODER**, **UIDEVELOPER**, and **TESTER**. Each one has a different job and different tool access. The boss AI coordinates them but isn't one of the four.

See [SWARM_GUIDE.md](SWARM_GUIDE.md) for how each lane works.

---

## What is GOBLIN MIND?

GOBLIN MIND is the system TheOrc uses to figure out what each AI model is actually capable of. It tests models for tool-call format, category skills (like file operations or code execution), and simplifies schemas for models that need it. The results show up as capability badges in the Swarm Board.

---

## Why does TheOrc ask me to approve things before running them?

That's the approval flow — it's the core safety feature. Before TheOrc runs a shell command or writes to a file, it shows you exactly what it's about to do and waits for your go-ahead. You're in control in real time, not reviewing a log after the fact.

See [USER_GUIDE.md](USER_GUIDE.md) for details.

---

## What is ORC ACADEMY?

ORC ACADEMY is the in-app training system. Once you've collected and reviewed enough training examples (from swarm runs), ORC ACADEMY runs the actual fine-tuning process to create a custom AI adapter tailored to TheOrc's task style.

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for the full pipeline.

---

## Is the Training Pit usable?

Yes. The current dataset has 900 training examples, 87 evaluation examples, and 25 negative examples — well above the minimum thresholds. The v1 adapter (`theorc-boss:gemma4-ft`) is live and can be pulled.

---

## What is HIVE MIND?

HIVE MIND is TheOrc's multi-PC networking feature. If you have more than one PC running TheOrc, they automatically find each other on your local network, pair with a one-click approval, and can share tasks. A research job can run on one machine while a coding job runs on another.

HIVE MIND is fully shipped as of v1.6.

See [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) for the setup guide.

---

## What is the Warchief?

The Warchief is the elected leader of a HIVE MIND network. It's the node that coordinates task scheduling across all connected machines. You can tell which node is the Warchief because its card in the HIVE panel has a gold border and a crown badge.

If the Warchief goes offline, the remaining nodes elect a new one automatically.

---

## What is the Warchief crown?

It's the gold border and crown badge that appear on the Warchief node's card in the HIVE constellation panel. It just means "this machine is currently the leader." It can move to a different machine if the current Warchief goes offline.

---

## What is the Update Center?

The Update Center is the **⬆ Update** button in the mode bar. Click it to check for new TheOrc versions and install them on the current machine. A gold dot on the button means an update is already available.

If you're the Warchief in a HIVE network, the Update Center also lets you push an update to all your connected nodes at once.

---

## How do I update TheOrc?

Click the **⬆ Update** button in the mode bar. If an update is available, you'll see it there. Click to install and watch the 5-step progress display. The gold dot on the button is your signal that something new is waiting.

---

## Can nodes on different networks connect?

Nodes on the same local network (home, office, or Wi-Fi) find each other automatically. Nodes on different networks (like two different homes) need Tailscale — a free VPN tool that makes remote machines appear as if they're on the same local network. Add the Tailscale peer IP in your HIVE settings.

---

## Is my code safe in HIVE MIND?

Yes. HIVE MIND only works on your private local network — it never opens connections to the internet. Every message between nodes is signed using HMAC-SHA256, so unsigned requests are rejected. Only machines you've explicitly paired with can join your hive or send it tasks.

See the Security section in [HIVE_MIND_SPEC.md](HIVE_MIND_SPEC.md) for more detail.

---

## What is Pit Boss?

Pit Boss is a setup wizard inside the Training Pit. It asks you 8 questions about what you want to train, then generates a training plan and creates an initial dataset automatically. It hands the results off to ORC ACADEMY for training. It's the fastest way to start fine-tuning without setting everything up manually.

See [TRAINING_PIT_GUIDE.md](TRAINING_PIT_GUIDE.md) for details.
