# Warband Module Spec

> Status: research draft
> Purpose: define a drop-in distributed-computing module that can live inside TheOrc today and later stand alone as a marketable API/service.
> Direction: daemon-first. The GUI becomes a client of the local daemon; the daemon becomes the canonical host for networked work.

---

## 1. What We Are Building

The current repo already has the ingredients for distributed work:

- a headless daemon host (`OrchestratorIDE.Daemon`)
- HIVE networking, queueing, worker execution, and peer discovery services
- a GUI that already talks to the same HIVE services in-process

The missing piece is a stable module boundary.

The target is a reusable **Warband** module that:

1. Runs as part of TheOrc today.
2. Can be packaged as a standalone API/service later.
3. Keeps the GUI thin and local.
4. Makes networked compute a daemon concern, not a window concern.

---

## 2. Design Goals

- **Drop-in for TheOrc**: existing HIVE behavior should remain available with minimal call-site churn.
- **Standalone API**: external consumers should be able to run the service without the desktop app.
- **Daemon-first ownership**: the daemon owns node membership, trust, scheduling, queue state, and worker lifecycle.
- **Thin client GUI**: the desktop app should only inspect, configure, and control the local daemon.
- **Shared core, multiple hosts**: the same compute core should work in the GUI host, daemon host, and test host.
- **No bespoke transport magic**: reuse the current HTTP-based HIVE networking for mesh traffic; keep local control separate.

---

## 3. What The Current Repo Already Proves

Current repo state already supports the daemon-first direction:

- `OrchestratorIDE.Daemon/HiveService.cs` boots the full HIVE stack in a headless host.
- `OrchestratorIDE.Daemon/OrchestratorIDE.Daemon.csproj` already compiles shared HIVE source files directly.
- `OrchestratorIDE.Avalonia/MainWindow.axaml.cs` and `OrchestratorIDE.Avalonia/UI/Panels/HivePanel.axaml.cs` still host HIVE behavior in the GUI.
- `docs/ROADMAP.md` explicitly describes the daemon as the future canonical HIVE node.

That means the next step is not to invent a new system. It is to extract a stable service boundary around what already exists.

---

## 4. Proposed Module Boundary

### 4.1 Core library

Create a shared service layer for distributed compute concerns:

- node identity
- membership and trust
- task/job queue semantics
- worker selection and capability matching
- task execution contract
- health / heartbeat / liveness
- persistence abstractions
- telemetry and event stream

Suggested namespace shape:

- `OrchestratorIDE.Distributed`
- `OrchestratorIDE.Distributed.Hosting`
- `OrchestratorIDE.Distributed.Contracts`
- `OrchestratorIDE.Distributed.Persistence`

### 4.2 Daemon host

The daemon owns the service runtime.

Responsibilities:

- start the network API
- expose health and membership endpoints
- run worker/queue services
- coordinate local execution runtime
- persist durable state
- publish events for external clients
- manage local-only admin/control operations

### 4.3 GUI host

The GUI should become a client of the local daemon.

Responsibilities:

- display node status
- edit settings
- trigger joins, pairing, and administrative actions
- launch jobs or inspect queues through the daemon
- remain usable even when remote mesh peers are unavailable

The GUI should not own the canonical distributed state once the daemon path is established.

---

## 5. Transport Split

Use two distinct channels:

### Mesh channel

This is the network-facing distributed compute path.

- peer discovery
- pairing
- job claims
- heartbeats
- completion/failure reports
- membership and trust propagation

This remains HTTP-based and is safe to expose only on the intended mesh surface.

### Local control channel

This is for the GUI talking to its own daemon.

- local settings
- start/stop
- worker enablement
- model/runtime selection
- admin actions that should never be reachable from a remote peer

Preferred options:

1. localhost-only HTTP with strict binding and auth
2. named pipe / domain socket on supported platforms
3. loopback + ephemeral token if the first two are too invasive for the initial slice

Recommendation: start with localhost-only control and keep the interface narrow.

---

## 6. Public API Shape

This is the standalone surface the daemon can eventually market and sell.

### 6.1 Node and health

- `GET /api/v1/health`
- `GET /api/v1/node`
- `GET /api/v1/peers`
- `GET /api/v1/capabilities`

### 6.2 Membership and pairing

- `POST /api/v1/pair`
- `GET /api/v1/pair/{sessionId}`
- `POST /api/v1/pair/{sessionId}/approve`
- `POST /api/v1/pair/{sessionId}/reject`

### 6.3 Jobs

- `GET /api/v1/jobs/next`
- `POST /api/v1/jobs/{jobId}/claim`
- `POST /api/v1/jobs/{jobId}/heartbeat`
- `POST /api/v1/jobs/{jobId}/complete`
- `POST /api/v1/jobs/{jobId}/fail`

### 6.4 Administration

- `POST /api/v1/admin/start`
- `POST /api/v1/admin/stop`
- `POST /api/v1/admin/role`
- `POST /api/v1/admin/runtime`
- `POST /api/v1/admin/reload`

### 6.5 Events

- `GET /api/v1/events`
- `POST /api/v1/events`

The first implementation does not need every endpoint on day one. It does need a stable shape so the daemon and GUI can target the same contract.

---

## 7. Suggested Implementation Order

### Phase 1: Define the contracts

- introduce shared request/response records
- define a host-agnostic service interface
- define the runtime interface for execution
- define the event stream contract

### Phase 2: Extract the daemon core

- move queue, worker, identity, peer, and health orchestration behind the shared interface
- keep the existing behavior intact
- make the daemon the canonical host for the service

### Phase 3: Add local control

- add a local-only control endpoint or pipe
- update the GUI to use that control path for local daemon operations
- stop the GUI from depending on in-process ownership of the distributed state

### Phase 4: Package as standalone

- publish the daemon as a separate product surface
- add config/docs for non-TheOrc consumers
- define the minimal external API and auth story

---

## 8. Integration Principles

- **Do not break the mesh** while extracting the module.
- **Do not make the GUI a second source of truth.**
- **Do not mix local admin calls with remote peer traffic.**
- **Keep existing HIVE endpoints compatible** until the new API is fully proven.
- **Prefer adapter layers over rewrites** when moving current code into the new boundary.

---

## 9. What This Enables Later

Once the module is extracted cleanly, TheOrc can evolve into:

- a front end for a local daemon
- a controller for a cluster of warband nodes
- a standalone API/service for distributed compute workflows
- a product that can be sold outside the desktop app without carrying the whole UI stack

---

## 10. Immediate Next Work

The next implementation pass should do three things:

1. identify the exact shared interfaces to pull out of the current HIVE services
2. create the daemon control boundary
3. wire the GUI to the daemon instead of to in-process HIVE ownership where possible

That will turn this from a conceptual direction into a real module boundary.
