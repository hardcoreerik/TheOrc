# TheOrc — HIVE MIND Guide

HIVE MIND lets multiple PCs running TheOrc work as a team. Instead of one computer doing everything, your machines share the load — one handles a research task, another runs a training job, a third handles code generation. They find each other automatically on your home or office network.

HIVE MIND is fully shipped as of v1.6. All phases (A, H1, H2, H3) are live.

For terminology, see [GLOSSARY.md](GLOSSARY.md).

---

## What HIVE MIND Is

Think of it like a crew. Each PC running TheOrc is a node in your hive. Nodes announce themselves on your local network, share what they're capable of (based on their GPU, RAM, and installed models), and accept tasks from the rest of the hive.

You don't configure IP addresses or dig into network settings. You install TheOrc, opt in during setup, and your machines find each other.

---

## How It Works

### Discovery

When TheOrc starts, it broadcasts a small signal on your local network every few seconds. Other TheOrc nodes on the same network hear it and add you to their roster. This all happens in the background — you just see new nodes appear in the HIVE panel.

### Pairing

Discovery doesn't mean trust. When a new node appears for the first time, both machines show a confirmation prompt. You click to approve on both ends — once. After that, the two nodes remember each other and connect silently.

### Task Routing

Once nodes are paired, any job that can run on a remote node shows a "Run on: [node name]" picker. TheOrc knows what each node can handle — it reads each machine's GPU, available VRAM, and installed models. If a node doesn't have enough power for a task, that option is greyed out with an explanation.

---

## Setting Up HIVE MIND

### Option 1: During Installation

When you install TheOrc, the installer includes a "Join HIVE MIND" step. It's checked by default. It sets up the right firewall rules for your private network and enables your PC to be discovered by other TheOrc installs.

### Option 2: On an Existing Install

If you already have TheOrc installed, open the HIVE panel. If your PC isn't yet sharing itself to the hive, you'll see a one-click "Enable hive serving on this PC" button. Click it — no reinstall needed.

### Steps to Get Two PCs Talking

1. Install TheOrc on both PCs (or enable hive serving if already installed).
2. Make sure both PCs are on the same local network (same Wi-Fi or wired network).
3. Open the HIVE panel on either machine — you should see the other PC appear within about 15 seconds.
4. Click to approve the pairing on both machines.
5. Both nodes are now in your hive.

---

## The HIVE Panel

The HIVE panel shows your network as a constellation — each node is a card, arranged visually on screen.

### Node Cards

Each card shows:

- The machine name
- Its GPU and how much VRAM is available
- Which AI models are installed
- What kinds of tasks it can handle
- A live status dot (green = reachable, grey = offline)

### The Warchief

One node in the hive is elected the Warchief — the leader. The Warchief coordinates scheduling across the hive. Its card gets a gold border and a crown badge so you always know which machine is in charge.

If the Warchief goes offline, the remaining nodes elect a new one automatically.

---

## Security — Why It's Safe

TheOrc's hive only works on your private local network. It never opens connections to the internet.

Every request between nodes is signed with a secret that's set up during pairing (using HMAC-SHA256, a standard signing method). If a request doesn't have the right signature, it gets rejected with a 401 error — so a random device on your network can't just join your hive or send it tasks.

Your identity as a node is based on a P-256 ECDSA key pair (a type of cryptographic ID), stored securely using Windows DPAPI (the same system Windows uses to protect passwords). Each request includes a one-time nonce to prevent replay attacks.

The short version: only paired machines can talk to each other, and every message is verified.

---

## Fleet Deploy — Updating All Nodes at Once

If you're the Warchief, you can push a TheOrc update to every node in your hive at the same time. You don't have to walk to each PC.

Here's how:

1. Click the **⬆ Update** button in the mode bar (or look for the gold dot if an update is ready).
2. In the Update Center, you'll see your hive nodes listed.
3. Click **Deploy to all nodes**.
4. TheOrc pushes the update to each worker node and shows a 5-step progress bar per node.

Each node updates itself and reports back when done.

---

## Troubleshooting HIVE MIND

### A node doesn't appear in the HIVE panel

- Make sure both PCs are on the same private network (not a guest network or separate VLAN).
- Check that TheOrc is running on the other PC and HIVE is enabled.
- Windows Firewall may be blocking the discovery signal. The installer sets this up automatically, but if you installed manually, you may need to allow TheOrc through the firewall for Private networks.

### Pairing was approved but the node shows as offline

- Restart TheOrc on the offline node.
- Check that the other machine's IP address hasn't changed (this can happen with DHCP). Reconnecting usually fixes it.

### A task option is greyed out for a node

- The node doesn't have enough VRAM or the right models for that task. Hover over the greyed option to see the reason.
- Try pulling a smaller model on that node, or run the task locally instead.

### The Warchief crown moved to a different node

- This is normal. If the previous Warchief went offline or became unreachable, the hive elects a new leader automatically.

### Fleet deploy fails on one node

- Check that the offline or failing node is reachable in the HIVE panel first.
- You can manually update that node by opening TheOrc on it and using the Update Center directly.
