# 🐝 HIVE MIND — Distributed TheOrc (Product & Engineering Spec)

> Status: **scoping approved 2026-06-11** · Owner track: v1.3 headline
> One sentence: every PC on your network running TheOrc automatically joins a
> roster, and any task — inference, research, farming, judging, training —
> can run on whichever node's hardware actually fits it.

---

## 1. Product requirements (agreed)

| # | Requirement | Acceptance test |
|---|---|---|
| R1 | **Auto-discovery** on LAN and Tailscale, zero manual setup beyond "install + run TheOrc" | Two fresh installs see each other's names within 15 s |
| R2 | **Capability-aware scheduling** — GPU, VRAM, models, live load detected; only valid tasks offered per node | A 6 GB node never shows "Academy 12B" as a target; it does show farm/judge/scout |
| R3 | **Clean UX** — every dispatchable surface gets "Run on: This PC ▾" | One picker, same place, every job type |
| R4 | **Unified progress** — remote work feels local | Academy bar/metrics identical whether local or remote |
| R5 | **Artifact return** — adapters, captures, logs sync back automatically | Adapter trained remotely appears in local `training_pit/outputs/` unprompted |
| R6 | **Basic trust** — random machines cannot join | First contact requires a one-click confirm on BOTH machines; thereafter silent |

**Honest hardware reality (design input, not afterthought):** an RTX 3050 6 GB
cannot run the current 12B Academy flow (~11.5 GB step peak). The hive must
*know* that and instead offer what fits: small-model inference, judge triage
(7B), researcher lanes, NIGHT HARVEST with a small boss, GOBLIN MIND probes,
and a **Scout lane** — training a ~4B boss adapter (≈4 GB peak) as the
"next model" track.

## 2. Delivery phases

### Phase A — Remote Ollama routing (fast win)
Ollama is already HTTP; TheOrc already takes a host URL. Scope:
- `OllamaHosts` list in settings (`name + url`), seeded with `localhost`.
- Swarm Board model pickers gain a host dimension: `boss @ BIGRIG`,
  `researcher @ HARDCOREPC`. Capability badges/probes keyed by `(host, model)`.
- Reachability dot per host (poll `/api/tags`).
- **Out of scope:** discovery (manual host entry is acceptable for A only),
  remote file ops, remote jobs.

### Phase H1 — Discovery + HIVE MIND roster panel
- Each TheOrc runs a **hive beacon**: UDP broadcast (port 7077) every 5 s +
  listener. Payload: `{name, machineId, ip, port, version}` (signed, see §4).
  Tailscale subnets don't carry broadcast → also a `StaticPeers` list and
  optional "introduce by IP" (one peer tells you about its peers — gossip).
- Each TheOrc serves a **node API** (embedded HTTP, same port):
  `GET /hive/info` → hostname, GPU name/VRAM (HardwareDetector), RAM,
  installed Ollama models, current load (GPU util via nvidia-smi, busy jobs),
  capability flags (§3).
- **Roster panel** (🐝 pill or Pit-adjacent): live cards per node — name, GPU,
  VRAM bar (reuse meter), models, "what this node can do", last-seen.

### Phase H2 — Capability-aware remote inference
- Phase A's host pickers become roster-driven: pick a *node*, not a URL.
- Scheduling guard: role requirements (SwarmSteering) × node capability →
  invalid picks disabled with the reason ("needs ~9 GB, node has 6").
- Latency/health shown inline; automatic failover stays OUT (explicit user
  choice only, per the steering philosophy).

### Phase H3 — Remote jobs (the full vision)
- Node API grows job endpoints: `POST /hive/jobs` (`type: farm|judge|academy|probe`,
  params), `GET /hive/jobs/{id}` (status = the progress.json heartbeat,
  served over HTTP), `GET /hive/jobs/{id}/artifacts` (zip stream),
  `POST /hive/jobs/{id}/stop`.
- The Academy/Harvest GUIs already poll a heartbeat file; an `IJobChannel`
  abstraction (LocalFileChannel | HttpChannel) makes remote runs render
  identically (R4).
- Artifact return: on `done`, requester pulls the artifact zip (adapter,
  summary, log; captures for farm jobs) into the local tree (R5).
- Dataset/code the job needs travels WITH the job (train/eval JSONL upload,
  ~5 MB) — nodes don't need the repo checked out for training; farm jobs DO
  need a workspace (ship goals file; captures return as artifacts).

## 3. Capability model (what a node advertises)

```json
{
  "machineId": "…", "name": "HARDCOREPC",
  "gpu": {"name": "RTX 3050", "vramGb": 6, "cuda": "12.x"},
  "ramGb": 64, "load": {"gpuPct": 12, "vramUsedGb": 0.8, "jobs": 0},
  "models": ["qwen2.5-coder:7b", "nemotron-mini"],
  "lanes": {
    "inference":  {"ok": true,  "maxModelGb": 5.5},
    "research":   {"ok": true},
    "judge":      {"ok": true,  "note": "7B-class judge"},
    "farm":       {"ok": true,  "note": "small boss only"},
    "probe":      {"ok": true},
    "academy12b": {"ok": false, "reason": "needs ~12 GB peak, 6 GB present"},
    "scout4b":    {"ok": true,  "note": "~4 GB peak QLoRA"}
  }
}
```
Lane rules are computed node-side from VRAM/models (single source of truth);
requesters just render `ok/reason`. Thresholds live in one table next to the
lane definitions, updated as we learn real working sets (the 11.5 GB figure
came from measurement, not guessing — keep that culture).

## 4. Trust model (R6)

- First contact: requester shows "HARDCOREPC wants to join your hive" → user
  confirms on both ends once. Exchange ed25519 public keys; store in
  `%APPDATA%\OrchestratorIDE\hive-peers.json`.
- All node-API calls signed (request HMAC w/ shared pair secret derived at
  pairing). Beacons unsigned (they only invite discovery, never trust).
- Default bind: private subnets + Tailscale range only. No internet exposure,
  ever, by default.

## 5. Three architecture variants for Option B

### V1 — "Simplest shippable" (in-app peer service)
Beacon + node API hosted **inside OrchestratorIDE.exe** (HttpListener).
Jobs run only while the app is open on the node.
- ✦ Smallest code; ships H1+H2 fast; no installer changes
- ✧ Node must have the GUI running; closing the app kills the hive presence
  (jobs themselves survive — they're detached processes — but status serving stops)

### V2 — "Best long-term" (Hive node Windows service + app as client)
A tiny `OrcHiveNode` service (or tray app) owns beacon/API/jobs; the GUI is
just a client of localhost like any other node. Installer registers it.
- ✦ Headless nodes (no GUI needed), survives logoff, one code path for
  local AND remote (the GUI always talks to "a node")
- ✦ Natural place for queueing, multi-job, scheduled NIGHT HARVEST
- ✧ Service install/update complexity; more security surface to do right

### V3 — "Best zero-config for normal users" (piggyback Ollama + repo drop-box)
No new daemon: discovery via probing the subnet/Tailscale peers for Ollama
(port 11434) + a TheOrc marker; jobs via a synced drop-box folder (existing
file-share/Syncthing/OneDrive) using the proven stop-file/heartbeat-file
patterns.
- ✦ Genuinely nothing to install beyond what exists today
- ✧ Inference-only discovery is solid, but file-queue jobs are clunky
  (latency, conflicts, no streaming status); subnet probing is slow and
  Tailscale-unfriendly; weakest trust story

**Recommendation:** ship **V1** as H1–H3 (it is the V2 architecture minus the
service wrapper — design the node API as if it were V2 from day one), then
graduate to **V2** by moving the same node code into a service when remote
NIGHT HARVEST scheduling makes "GUI must be open" annoying. V3's only durable
idea — Ollama-port probing as a discovery *hint* — gets folded into H1.

## 6. Sequencing & estimates

| Step | Scope | Effort |
|---|---|---|
| A | hosts in settings + host-aware pickers + reachability | 1–2 sessions |
| H1 | beacon, node API `/hive/info`, roster panel, pairing | 2–3 sessions |
| H2 | roster-driven pickers + lane gating | 1 session |
| H3 | job endpoints, HttpChannel, artifact return, Academy/Harvest remote | 3–4 sessions |
| Scout lane | 4B base in academy configs + lane wiring | 1 session (after H3) |

## 7. Open questions (decide before H3)

1. Scout base model: Nemotron-Mini-4B vs Qwen2.5-Coder-3B as the small boss?
2. Does remote farm need the full repo on the node (workspace-dependent
   goals), or do we ship a workspace snapshot? (Leaning: nodes that farm must
   have TheOrc's repo configured; nodes that only train/judge need nothing.)
3. Version skew policy: refuse jobs across mismatched TheOrc versions, or
   best-effort with a warning?
