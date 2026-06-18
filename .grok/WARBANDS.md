# WARBANDS — TheOrc Cloud & Headless Deployment

> **Status:** Named and designed 2026-06-17. Binary rename pending (theorc-daemon → theorc-warband).
> Implementation is complete — this is a naming and documentation formalization, not a new feature.

---

## What is a Warband?

A **Warband** is a deployed headless HIVE node. It is the `OrchestratorIDE.Daemon` binary —
renamed `theorc-warband` — running on any machine that is not the user's main desktop.

The HIVE is the home base: the Warchief (GUI app) plus its local worker, running on your machine.
A Warband is an expeditionary force — headless, no GUI, no display — operating in the cloud or
on a remote machine, pulling tasks from the Warchief's queue and executing them against its local
Ollama (or, post-Native-Runtime, directly via LlamaSharp in-process).

You can have many Warbands. Each is independent. Together they form the extended fleet.

---

## Naming map

| Concept | Name |
|---|---|
| Home node (GUI + local worker) | HIVE / Warchief |
| Deployed headless node | **Warband** |
| The fleet of deployed nodes | **WARBANDS** |
| Binary | `theorc-warband` |
| Docker image | `theorc-warband` |
| Compose file | `warband.compose.yml` |
| Deploy action | "Spin up a Warband" |
| UI section | "Your Warbands" |

The agent-role team (Boss, Researcher, Coder, etc.) is also called "the warband" informally
in the codebase. These are distinct: the *agent warband* is the team of AI roles in a swarm run;
a *Warband* (capital W) is a deployed HIVE node. Context makes them unambiguous.

---

## What a Warband runs

The Warband boots six services on startup:

```
HiveNodeServer   (port 7078)  — peer identity, election, pairing, remote-deploy API
HiveTaskQueue    (port 7079)  — Warchief task queue + durable SQLite history
HiveMeshHeartbeat             — 15s/30s liveness pulses between peers
HiveElectionService           — Bully-style Warchief election if the home node drops
HiveBeacon       (UDP)        — multicast broadcast so peers find each other on LAN
HiveWorkerAgent  (optional)   — polls the Warchief queue, claims tasks, runs them via Ollama
```

`WorkerMode: false` turns it into a coordinator-only node (Warchief role) with no local execution.
`WorkerLanes` restricts which task types this node will accept (empty = all lanes).

---

## Docker — current shape (Ollama sidecar required)

```yaml
services:
  warband:
    image: theorc-warband
    environment:
      - HIVE_NODE_NAME=cloud-worker-1
    depends_on: [ollama]

  ollama:
    image: ollama/ollama
    volumes: [ollama_models:/root/.ollama]
    deploy:
      resources:
        reservations:
          devices:
            - capabilities: [gpu]
```

Two containers. The Warband talks to the Ollama sidecar at `http://ollama:11434`.
Models are pulled into the Ollama volume on first run.

---

## Docker — post-Native-Runtime shape (no sidecar)

Once `LLamaSharpRuntime` (Native Runtime Phase 2) ships, the Warband loads GGUF files
directly in-process. The Ollama sidecar is eliminated:

```yaml
services:
  warband:
    image: theorc-warband
    volumes: [./models:/models]
    environment:
      - HIVE_BACKEND=LlamaCpp
      - HIVE_MODEL=/models/gemma-4-12b.gguf
      - HIVE_NODE_NAME=cloud-worker-1
    deploy:
      resources:
        reservations:
          devices:
            - capabilities: [gpu]
```

One container. Mount a folder of GGUFs. No Ollama install. No registry. No `ollama create` step.
ORCISH TONGUE's GBNF grammar constraints work in-process — tool calls are valid by construction
on any model you drop in, even ones never trained for tool use. See `RUNTIME_PHASE0_SPEC.md` §11.

---

## Use cases

| Setup | What runs |
|---|---|
| Solo user, one machine | GUI app only. No Warband needed. |
| Home lab, 2+ machines | GUI on the main PC, Warband on each extra machine. |
| Cloud burst | GUI locally, Warbands on rented GPU VMs (Vast.ai, Lambda, RunPod). |
| Headless server farm | No GUI anywhere — Warbands only, one elected Warchief. |
| CI/CD worker | Warband runs on a build server, executes CODER / TESTER tasks on PR. |

---

## Mac/Linux release plan

`OrchestratorIDE.Daemon` is already `net10.0` (no `-windows` suffix) and uses AES-256-GCM
secrets (no DPAPI). A Warband binary builds for any platform today:

```bash
dotnet publish -r linux-x64   -c Release --self-contained
dotnet publish -r osx-arm64   -c Release --self-contained
dotnet publish -r win-x64     -c Release --self-contained
```

What's missing: CI publish matrix and GitHub release artifacts for the non-Windows targets.
This is one GitHub Actions job — a v1.9 or v2.0 addition depending on prioritization.

---

## Pending changes

- [ ] `OrchestratorIDE.Daemon.csproj`: rename `AssemblyName` from `theorc-daemon` → `theorc-warband`; update description
- [ ] `docs/ROADMAP.md` + `.grok/PROJECT_TRUTH.md`: reflect WARBANDS as a named feature
- [ ] GitHub Actions: add `linux-x64` and `osx-arm64` Warband publish targets to CI
- [ ] Write `warband.compose.yml` (template for cloud deploy)
- [ ] Docker Hub or GHCR: publish `theorc-warband` image on release

---

## Relationship to other systems

- **Native Runtime** (`RUNTIME_PHASE0_SPEC.md`): WARBANDS is the primary beneficiary. Phase 2
  (LLamaSharpRuntime) eliminates the Ollama sidecar and makes each Warband self-contained.
- **ORCISH TONGUE** (`RENAME_GOBLIN_MIND.md`): GBNF grammar-constrained decoding in the native
  runtime makes ORCISH TONGUE work on any model inside a Warband, not just models probed and
  profiled against Ollama's API.
- **OS expansion** (memory: os-expansion): Phases 1-3 shipped the DPAPI abstraction and Tailscale
  cross-platform path detection that make Warbands work on Linux/macOS at all.
