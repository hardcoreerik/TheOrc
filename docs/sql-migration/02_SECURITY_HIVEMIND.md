# JSON → SQL Migration — Security & HIVEMIND Vulnerability Vectors

> The part we cannot get wrong. Two separate threat classes: **(A) SQL injection** and
> **(B) durable storage of untrusted remote input** via the HIVEMIND HTTP surface.
> They are different problems with different fixes. Both must be closed before Phase 4 ships.

---

## A. SQL injection — fully closeable, one rule

### The rule

**Every value passed to SQL goes through a `SqliteParameter`. We never build SQL by
string concatenation or interpolation with a value in it.** That's the whole defense.
Parameterized commands send SQL text and data over separate channels, so a value can
never be parsed as SQL — regardless of what a remote node puts in it.

### Right vs wrong

```csharp
// WRONG — never do this, even for "internal" values:
var sql = $"INSERT INTO hive_tasks(claimed_by) VALUES('{result.WorkerId}')";

// RIGHT — the only pattern allowed in the codebase:
using var cmd = conn.CreateCommand();
cmd.CommandText = "INSERT INTO hive_tasks(claimed_by) VALUES($w)";
cmd.Parameters.AddWithValue("$w", workerId);   // value is data, never SQL
```

### Why this matters *here specifically*

The data that will land in `captures.goal`, `hive_tasks.claimed_by`,
`hive_events.msg`, and `hive_tasks.result_blob` is attacker-influenceable:

- `goal` comes from user/swarm input.
- `WorkerId`, `ClaimToken`, `Result`, event `Msg` come **straight off the wire** from a
  remote worker — see `HiveTaskQueue.ReadJsonAsync<HiveTaskResult>` and
  `HiveTaskBundle.cs`. A hostile node controls every byte of these.

A `WorkerId` of `'); DROP TABLE hive_tasks;--` is harmless with parameters and
catastrophic with concatenation. We make it structurally impossible.

### Enforcement (mechanical, not "remember to")

1. **One choke point.** All `SqliteCommand` construction lives in a single repository
   base helper. No `CommandText` is built anywhere else. Review rejects any `CreateCommand`
   outside that file.
2. **Grep gate in CI/codex-review.** Flag any SQL string containing `+`, `$"…{`, or
   `string.Format` adjacent to `CommandText`/`CommandText =`. Manual approval required.
3. **No dynamic identifiers from input.** Table/column names are never taken from input.
   If we ever need a dynamic column, it's chosen from a hard-coded allow-list — parameters
   only bind *values*, not identifiers.

---

## B. HIVEMIND vulnerability vectors — the real risk

SQL injection is the *easy* problem (one rule closes it). The harder problem is that
**the HIVEMIND HTTP endpoints have no authentication today**, and Phase 4 makes their
input *durable*.

### Current state (verified in code, 2026-06-14)

- `HiveNodeServer` (port **7078**, `GET /hive/info`) and `HiveTaskQueue` (port **7079**,
  the full task lifecycle) bind via `HttpListener` to `http://+:<port>/` — **wildcard, all
  interfaces** — with **no auth check of any kind**.
- Any host that can reach the port can:
  - `GET /hive/tasks/next` — read task specs, project goals, upstream artifacts (info disclosure).
  - `POST /hive/tasks/{id}/claim` — claim a task. The claim token is then **handed to
    whoever asked first**. It is a *consistency* guard against stale re-queued workers,
    **not** an *authentication* secret.
  - `POST /hive/tasks/{id}/complete` — inject an arbitrary result into the swarm, which
    the Warchief "integrates exactly as if the task ran locally." This is the worst one:
    a hostile node can feed the coordinator poisoned code/research.
  - `POST /hive/events` — push arbitrary lifecycle events / log spam.
- Today this is bounded by ephemerality (5-minute eviction, memory only) and by the
  network being Tailscale. **Phase 4 removes the ephemerality.** Poisoned results and
  event spam become durable history.

### Threat model for Phase 4

| Vector | Without mitigation | Mitigation |
|--------|--------------------|------------|
| **Result poisoning** — hostile node POSTs `/complete` with malicious output | Bad code/research persisted as "completed", trusted by Warchief | Shared-secret auth on all mutating endpoints; persist worker identity + auth result; mark provenance |
| **Unauthenticated claim** — any node drains the queue | Tasks claimed by attacker, real workers starved | Auth on `/claim`; only enrolled/trusted nodes (see `HiveHosts`/`HiveEnroller` trust list) may claim |
| **Storage flooding** — node spams huge results/events | DB bloat, disk exhaustion | Per-worker row quota + byte cap per row (truncate at write); retention sweep |
| **Oversized payload** — multi-MB `Result`/`Msg` strings | Memory + disk blowup | Length caps enforced in repository write path, not trusted from wire |
| **Info disclosure** — `/info`, `/tasks/next`, `/status` read project internals | Goal/spec/artifact leak to any LAN/Tailscale peer | Auth on read endpoints too, or bind to Tailscale interface only |
| **SQL injection via wire strings** | (See section A) | Parameterized queries — structurally closed |
| **Replay** — re-POST a captured valid `/complete` | Duplicate/altered result | Claim-token already rotates per claim; add nonce/once-only completion check on persist |

### Required controls before Phase 4 persists anything

These gate Phase 4. None are optional.

1. **Shared-secret auth on every mutating endpoint** (`/claim`, `/heartbeat`,
   `/complete`, `/fail`, `/events`). Simplest viable: an `X-Hive-Key` header carrying a
   per-session secret distributed at enrollment via the existing `HiveEnroller` trust flow.
   Reject (`401`) anything without the current key. (mTLS or Tailscale-ACL-only are
   stronger alternatives — decide in the roadmap's open decisions.)
2. **Bind scope review.** Confirm whether wildcard bind is required or whether binding to
   the Tailscale interface only is sufficient. Narrower bind = smaller attack surface.
3. **Input validation at the persistence boundary.** Before any row is written:
   - length caps (`title`≤512, `worker_id`/`claimed_by`≤128, `msg`≤1024, `result_blob`≤
     a configured max — truncate + log, never trust wire-declared length);
   - charset validation on identifier-ish fields (`worker_id`, `claim_token`,
     `session_id` — alphanumeric + `-_.` only; reject control chars / newlines used for
     log forging);
   - enum validation on `status`/`type`/`role` against the known set.
4. **Per-node write quota.** Cap rows per worker per session; over-quota → reject + flag.
5. **Provenance columns.** Persist *which* node submitted, whether it was authenticated,
   and the claim token, so poisoned data is traceable and revocable after the fact.
6. **Retention sweep.** `retain_until` column + a periodic delete so the durable hive
   tables can never grow unbounded (mirrors the current 5-min in-memory eviction intent).

### Defense in depth — the trust list already exists

`HiveEnroller` / `HiveHosts` already maintain a trusted-node list with a "Trust & Add"
flow. Phase 4 auth should *reuse* that: only enrolled nodes get the session key; the key
gates the endpoints; the persistence layer records the enrolled identity. We are not
inventing a new trust system — we are connecting the one that exists to the wire and the DB.

---

## Pre-flight checklist (copy into the Phase 4 PR)

**SQL injection (applies from Phase 0):**
- [ ] All `SqliteCommand`s built in the single repository helper; none elsewhere.
- [ ] Zero string-concatenated SQL; every value is a `SqliteParameter`.
- [ ] No input-derived table/column identifiers (allow-list only).
- [ ] codex-review grep gate for SQL-concat patterns is active.

**HIVEMIND durable-input (gates Phase 4):**
- [ ] Shared-secret (or stronger) auth on `/claim`,`/heartbeat`,`/complete`,`/fail`,`/events`; unauth → 401.
- [ ] Bind scope reviewed (Tailscale-only vs wildcard justified).
- [ ] Length caps enforced in the write path for every wire-sourced string.
- [ ] Charset/enum validation on identifier and status fields.
- [ ] Per-worker row quota enforced.
- [ ] Provenance (node id, auth result, claim token) persisted on every hive row.
- [ ] Retention sweep deletes rows past `retain_until`.
- [ ] Completion replay guard (once-only per claim token) on persist.
