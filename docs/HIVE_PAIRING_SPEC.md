# HIVE MIND — Secure Pairing and Node Authentication Specification

> Status: Design — not yet implemented  
> Scope: First-contact pairing, mutual node auth, session establishment, replay resistance,  
>        trusted-peer persistence, request signing, re-pairing / revocation, migration path  
> Author: Claude Sonnet 4.6 + Erik, based on codebase audit 2026-06-14

---

## Section 1 — Current-State Findings

### What the spec promises vs. what exists

`HIVE_MIND_SPEC.md §4` promises:

> *"First contact: requester shows 'HARDCOREPC wants to join your hive' → user confirms on both ends once. Exchange ed25519 public keys; store in `%APPDATA%\OrchestratorIDE\hive-peers.json`. All node-API calls signed (request HMAC w/ shared pair secret derived at pairing)."*

None of that exists in the running code. The gap between spec and implementation is total.

### Actual authentication on every HTTP endpoint

| Port | Endpoint | Method | Auth today |
|---|---|---|---|
| 7079 | `/hive/tasks/next` | GET | **None** |
| 7079 | `/hive/tasks/{id}/claim` | POST | **None** |
| 7079 | `/hive/tasks/{id}/heartbeat` | POST | Claim token only (see below) |
| 7079 | `/hive/tasks/{id}/complete` | POST | Claim token only |
| 7079 | `/hive/tasks/{id}/fail` | POST | Claim token only |
| 7079 | `/hive/tasks/status` | GET | **None** |
| 7079 | `/hive/session/context` | GET | **None** |
| 7079 | `/hive/events` | POST/GET | **None** |
| 7078 | `/hive/info` | GET | **None** |
| 7077 | UDP beacon | — | Unsigned by design (discovery only — correct) |

**Claim tokens are not authentication.** `HiveTaskQueue.cs:276–307` generates a GUID token and hands it to whoever calls `/claim` first. The token rotates on re-queue to prevent stale workers from reasserting a task they lost — it guards consistency, not identity. Any node on the network can call `/claim`, receive the token, and then call `/complete` with a poisoned result.

### Node identity model

`HiveHosts.cs:18–53` defines the peer record:

```csharp
public sealed class HiveHost {
    public string Name     { get; set; } = "";   // user-supplied label
    public string Url      { get; set; } = "";   // IP:port — ephemeral
    public string Hostname { get; set; } = "";   // DNS/MagicDNS
    public string Source   { get; set; } = "";   // "manual" | "lan" | "tailscale"
    // No public key. No fingerprint. No certificate.
}
```

Identity is `(Name, IP:port)`. Both are mutable. There is no cryptographic binding between a name and a machine. `hive-peers.json` does not exist.

### Network binding

`HiveEnroller.cs:52–72` opens ports 7077–7079 and 50052 on the Windows Firewall **Private profile only**. The `HttpListener` wildcard-binds (`http://+:7079/hive/`) — it accepts from any source that reaches the port. Tailscale provides transport encryption and peer authentication at the VPN layer. If a session operates over plain LAN, there is no transport security at all.

### What the existing guard does and does not cover

The heartbeat-timeout watchdog (`HiveTaskQueue.cs:478–496`) re-queues tasks after 45 s without a heartbeat. The pending-timeout (`PendingTimeoutSec = 60`) falls back to local after 60 s. The task eviction sweep (`HiveTaskQueue.cs:499–510`) removes completed/failed entries after 5 minutes. These are **availability and crash-recovery** controls, not security controls. An attacker who sends a heartbeat every 40 s can hold a claimed task indefinitely.

### What `02_SECURITY_HIVEMIND.md` already identified

The security doc in `docs/sql-migration/` correctly gates Phase 4 (durable storage) on seven controls: shared-secret auth, input validation, per-node quotas, provenance columns, retention sweep, replay guard, and SQL parameterization. Those controls remain unimplemented. The current code's implicit security model is: *"the Private firewall profile keeps bad actors off the port, and Tailscale authenticates Tailscale peers."*

That model is correct for its stated assumptions. It is not correct once the assumption breaks.

---

## Section 2 — Threat Model

### Scope

Threat actors with **network access to the ports** — either:
- a device on the same LAN segment (home/office network compromise, rogue device, VLAN leak)
- a Tailscale peer that is enrolled in the tailnet but that the user did not intend to trust with HIVE work (shared corporate tailnet, compromised device in the tailnet)
- a Tailscale peer that was revoked at the Tailscale layer but where the node process still has a cached IP before ACL propagates

Out of scope: OS-level compromise of the Warchief machine itself, supply-chain attacks on the binary, users intentionally pairing with hostile machines.

### Assets

1. **Task specifications** — contain the project goal, upstream research, partial code, model hints. Disclosure leaks IP.
2. **Task results** — written into the live workspace as-if local. Injection means RCE on the Warchief's filesystem.
3. **Session context** — project goal, session ID, language. Disclosure.
4. **Event stream** — real-time task execution timeline. Disclosure + operational intelligence.
5. **Node info** — GPU load, VRAM, installed models, RPC port. Reconnaissance.
6. **Audit trail (Phase 4)** — durable record of who did what. Tampering poisons accountability.

### Threat catalogue

| ID | Threat | Current exposure | Impact |
|---|---|---|---|
| T1 | **Task queue drainage** | Any LAN peer calls `/claim` on every task, holds with heartbeats | Swarm never completes; falls back to local only after timeout |
| T2 | **Code injection via poisoned result** | Any peer calls `/complete` with crafted code after claiming a task | RCE on Warchief — staged files land in workspace |
| T3 | **Research/goal exfiltration** | GET `/tasks/next`, `/status`, `/session/context` from any peer | IP disclosure |
| T4 | **Worker impersonation** | `WorkerId` field is attacker-controlled; no verification | Audit trail poisoned; blame shifted |
| T5 | **Heartbeat abuse** | Attacker holds claimed task indefinitely | DoS; real worker never executes |
| T6 | **Event stream surveillance** | GET `/events?since=-1` polled at 2 s intervals | Real-time intelligence on swarm execution |
| T7 | **Beacon spoofing** | UDP broadcast with any name | Discovery-layer deception; low impact today |
| T8 | **Phase 4 replay attack** | Re-submit an old `/complete` body after durable storage lands | Duplicate/stale results persisted |
| T9 | **Phase 4 DB exhaustion** | Flood `/events` with large payloads; no length cap | Disk exhaustion; query slowdown |
| T10 | **Tailscale revocation lag** | Node removed from tailnet but process holds cached IPs | Grace window where revoked peer still reaches ports |

### Trust boundaries

```
[ Internet ] ← Firewall Private-only blocks this
     |
[ LAN ] ← currently implicit trust once port is reachable
     |
[ Tailscale VPN ] ← WireGuard: encrypted + Tailscale-authed, but not HIVE-authed
     |
[ HIVE HTTP endpoints ] ← application layer: currently zero auth
     |
[ SwarmSession ] ← workspace writes, RCE surface
```

The goal of this spec is to add an **application-layer trust boundary** between "reachable" and "authorized to execute work."

---

## Section 3 — Option Comparison

### Option A: Mutual TLS with app-issued node certificates

Each node generates a certificate signed by a local CA that TheOrc controls. Both sides present and verify certs on every connection. TLS handles channel encryption, authentication, and replay resistance.

| Dimension | Assessment |
|---|---|
| Security properties | Excellent. Channel encrypted; both parties authenticated; cert revocation via CRL or OCSP. |
| .NET implementation cost | **High.** `HttpListener` does not support mTLS natively. Migration to Kestrel (ASP.NET Core) or wrapping in `SslStream` is a significant refactor of `HiveTaskQueue`, `HiveNodeServer`, and `HiveWorkerAgent`. A local CA with cert issuance, rotation, and revocation adds ongoing maintenance. |
| UX for first-run pairing | Complex. User must trust the CA, or verify certificate fingerprints out-of-band. |
| LAN + Tailscale fit | Works on both, but Tailscale already provides transport encryption. Double-encrypting over Tailscale is harmless but wasteful. |
| Replay protection | Covered by TLS session; no additional work needed. |
| Key management burden | Local CA private key must be protected. Certificate expiry must be tracked and renewed. |
| Revocation / rotation | CRL/OCSP for real revocation; or just delete the cert and reissue. Non-trivial to implement correctly in a desktop app. |
| Future headless nodes | Works but requires cert provisioning at node setup. |
| Likely failure modes | Cert expiry causes silent breakage; CA key loss requires re-pairing all nodes; clock skew breaks cert validity. |
| Verdict | **Overkill for v1.** The HttpListener → Kestrel migration alone is a large PR unrelated to security. Transport encryption is redundant over Tailscale. Revocation story is complex. |

---

### Option B: Noise Protocol Framework (NK or XX handshake)

Noise is a framework of handshake patterns built from standard primitives (X25519, ChaCha20-Poly1305, SHA-256). The XX pattern provides mutual authentication with no PKI. NK provides one-way auth (caller authenticates to server). Both produce an encrypted, authenticated channel session.

| Dimension | Assessment |
|---|---|
| Security properties | Excellent. Purpose-built for peer-to-peer mutual auth. No PKI. Session keys forward-secret. |
| .NET implementation cost | **Very high.** No actively maintained .NET Noise library exists. NoiseDotNet is unmaintained (2018). Would require implementing or vending a dependency with no ecosystem support. The binary framing is also at odds with the existing JSON-over-HTTP architecture. |
| UX for first-run pairing | XX pattern handles first-contact elegantly — identity keys are exchanged during the handshake. |
| LAN + Tailscale fit | Excellent on both; transport-agnostic. |
| Replay protection | Built into the Noise session; nonces managed by the protocol. |
| Key management | Static key pair per node; no CA; rotation requires re-pairing. |
| Revocation | Delete peer record. Simple. |
| Future headless nodes | Ideal; designed for this use case. |
| Likely failure modes | Library maintenance risk; binary protocol harder to debug than JSON; migration from HTTP to Noise session layer is a rewrite of the transport. |
| Verdict | **Conceptually ideal but impractical.** The .NET ecosystem gap and the HTTP-to-Noise migration cost make this the wrong choice for a small-team .NET desktop app. The right answer to "what Noise would give us" is achievable with BCL-only primitives at a fraction of the cost. |

---

### Option C: Ed25519 node identity + X25519 ECDH pairing + HKDF session key + HMAC-SHA256 per-request signing

Each node generates a stable Ed25519 key pair on first install (node identity). During pairing, an X25519 ECDH exchange derives a shared signing secret via HKDF. Every subsequent request to an authenticated endpoint carries an HMAC-SHA256 signature over the method, path, nonce, timestamp, and body hash. All primitives are in the .NET 10 BCL.

| Dimension | Assessment |
|---|---|
| Security properties | Good. Node identity is cryptographically stable. Request integrity + authentication per call. Replay resistance via nonce + timestamp window. No channel encryption (relies on Tailscale or private LAN). |
| .NET implementation cost | **Low–Medium.** `System.Security.Cryptography.ECDsa` (Ed25519, .NET 7+), `ECDiffieHellman` (X25519, .NET 5+), `HKDF` (.NET 5+), `HMACSHA256` (always). No external dependencies. The signing middleware is ~150 lines; the pairing flow is ~200 lines. All existing endpoints stay on `HttpListener`. |
| UX for first-run pairing | Moderate. Warchief shows fingerprint when a new node requests pairing. User approves on Warchief. Optional: both machines display the same fingerprint for OOB verification. Subsequent connections are silent. |
| LAN + Tailscale fit | Works on plain LAN (adds application-layer auth without transport encryption) and on Tailscale (adds app-layer auth on top of WireGuard). No double-encryption overhead. |
| Replay protection | Nonce + 30 s timestamp window. Nonce cache per peer (in-memory LRU, 1000 entries, 5 min TTL). |
| Key management | One Ed25519 + one X25519 key pair per node, generated once, persisted to `hive-identity.json` protected by Windows DPAPI. No CA, no cert expiry. |
| Revocation | Delete peer record from `hive-peers.json`, rotate Warchief's own key pair, re-pair all nodes. Immediate effect on the next request. |
| Future headless nodes | Ed25519 public key fingerprint is the stable node ID, independent of IP, hostname, or install. Works for service nodes. |
| Likely failure modes | Clock skew > 30 s breaks auth (NTP required); DPAPI file loss requires re-pairing; nonce cache lost on restart (acceptable — 30 s window prevents replay across restart for nearly all cases). |
| Verdict | **Recommended for v1.** Standard building blocks, BCL-only, minimal migration cost, extensible to future node types. Honest about what it provides and what it doesn't (no transport encryption — document this clearly). |

---

### Option D: MLS (Message Layer Security) or group-keyed approach

MLS (RFC 9420) is designed for large-group encrypted messaging with efficient member add/remove and forward secrecy. A group-keyed approach would give all HIVE nodes a shared symmetric key rotated periodically.

| Dimension | Assessment |
|---|---|
| Relevance | HIVE MIND has at most ~10 nodes. MLS overhead — epoch management, key trees, ratcheting — is designed for thousands of members. For N ≤ 10, the complexity is purely cost with no benefit. |
| .NET availability | No production .NET MLS library exists. |
| Group symmetric key (simplified) | Simpler variant: Warchief distributes a session PSK to all enrolled nodes. Loses per-node identity; any node compromise leaks the group key. |
| Verdict | **Not relevant.** MLS is overkill by two orders of magnitude. Group PSK loses identity guarantees that are useful for audit and revocation. |

---

## Section 4 — Recommended Design

**Use Option C: Ed25519 node identity + X25519 ECDH pairing + HKDF session key + HMAC-SHA256 per-request signing.**

### Why these primitives

- **Ed25519** — RFC 8032. 32-byte public key, 64-byte signatures, ~128-bit security, extremely fast, no parameter choices to get wrong, available in .NET 7+ via `ECDsa` + `ECCurve.Ed25519`. Used for **stable node identity** (not for per-request signing — that uses the HMAC shared secret).
- **X25519** — RFC 7748. Elliptic-curve DH on Curve25519. Used for **one-time key agreement** at pairing to derive the shared signing secret. Available in .NET 5+ via `ECDiffieHellman`. No cofactor clearing errors, no small-subgroup attacks, one correct implementation path.
- **HKDF-SHA256** — RFC 5869. Deterministic key derivation from DH output. Separates the DH output (which has structure) from the key (which must look random). Available in .NET 5+ via `HKDF.DeriveKey`. Used once at pairing to derive the `shared_signing_secret`.
- **HMAC-SHA256** — RFC 2104. Used for per-request authentication. Provides integrity + authentication given a shared secret. Constant-time comparison via `CryptographicOperations.FixedTimeEquals`. Available in every .NET version.
- **CSPRNG** — `RandomNumberGenerator.GetBytes` for nonces and the local private keys. Do not use `Random`, `Guid`, or anything non-cryptographic.

The primitives deliberately use nothing from outside `System.Security.Cryptography`. An adversarial reviewer sees standard constructions from RFCs, composed in a straightforward way, with no novel combinations.

### What this design does and does not claim

**Does:**
- Establish stable cryptographic node identity, independent of IP or hostname
- Prevent unauthenticated nodes from claiming tasks, submitting results, or reading the queue
- Prevent replay of valid requests (nonce + timestamp window)
- Prevent impersonation (only paired nodes know the shared signing secret)
- Give users a clear pairing ceremony with fingerprint verification

**Does not:**
- Encrypt traffic on plain LAN (application-layer encryption is out of scope for v1; Tailscale provides this at the VPN layer; plain LAN users are already trusting their network for all Ollama traffic)
- Provide forward secrecy for the signing secret (rotation on re-pair handles this; forward secrecy would require session-level ephemeral keys — out of scope for v1)
- Protect against OS-level compromise of the Warchief machine

---

## Section 5 — Proposed Protocol and Persistence Format

### 5.1 Node identity lifecycle

On first launch (and on any call to `HiveIdentity.EnsureIdentity()`):

1. Generate an Ed25519 key pair: `node_signing_key` (private) + `node_id` (public)
2. Generate an X25519 key pair: `node_dh_key` (private) + `node_dh_pub` (public)
3. Compute `fingerprint = base16(SHA256(node_id_bytes))[..16]` — 8 bytes, 16 hex chars, formatted as `AB:CD:EF:01:23:45:67:89`
4. Persist to `%APPDATA%\OrchestratorIDE\hive-identity.json`, encrypted with `ProtectedData.Protect(scope=CurrentUser)`

```json
{
  "version": 1,
  "node_id": "<64-hex-char ed25519 pubkey>",
  "node_signing_key_enc": "<base64(DPAPI-encrypted ed25519 privkey)>",
  "node_dh_pub": "<base64(x25519 pubkey)>",
  "node_dh_key_enc": "<base64(DPAPI-encrypted x25519 privkey)>",
  "fingerprint": "AB:CD:EF:01:23:45:67:89",
  "created_at": "2026-06-14T12:00:00Z"
}
```

`node_id` is the stable public identity. It does not change unless the user explicitly re-generates the identity ("Reset node identity" in Settings → HIVE).

### 5.2 Pairing protocol — wire contract

**Prerequisite:** Both nodes are running. Node B appears in Node A's constellation (via beacon or manual add). User clicks "Pair" on Node A's HIVE panel.

#### Step 1 — Pair request (Node A → Node B)

```
POST http://<node_b>:7078/hive/pair
Content-Type: application/json

{
  "protocol":          "theorc-hive/1",
  "requester_node_id": "<64-hex ed25519 pubkey>",
  "requester_dh_pub":  "<base64 x25519 pubkey>",
  "requester_name":    "MAINPC",
  "requester_port":    7079,
  "timestamp_ms":      1718928000000
}
```

No auth on this request — it IS first contact.

#### Step 2 — Node B acknowledges, queues for user approval

```
HTTP 202 Accepted
{
  "status":           "pending_approval",
  "responder_node_id": "<64-hex ed25519 pubkey>",
  "responder_dh_pub":  "<base64 x25519 pubkey>",
  "responder_name":    "BIGRIG",
  "session_id":        "<uuid>",
  "fingerprint_local": "<16-hex fingerprint of Node B>"
}
```

Node B's UI shows: **"MAINPC (fingerprint: AB:CD:EF:...) wants to join your hive. Approve?"**  
The fingerprint displayed on Node B should match what Node A shows. Users who want to verify OOB compare the two displays. Users who don't care click Approve.

#### Step 3 — Node A polls for approval

```
GET http://<node_b>:7078/hive/pair/<session_id>

→ HTTP 202 { "status": "pending" }     (user hasn't clicked yet)
→ HTTP 200 { "status": "approved", "responder_node_id": "...", "responder_dh_pub": "..." }
→ HTTP 403 { "status": "denied" }      (user clicked Deny)
```

Poll interval: 3 s. Timeout: 120 s (after which Node A shows "Pairing timed out").

#### Step 4 — Key agreement (both sides, independently)

On receipt of the peer's DH public key, each side computes:

```
shared_ikm     = X25519(my_dh_priv, peer_dh_pub)
salt           = SHA256(sort_bytes(my_node_id_bytes, peer_node_id_bytes))
                 // sort ensures both sides compute the same salt regardless of who initiated
shared_secret  = HKDF-SHA256(ikm=shared_ikm, salt=salt, info=utf8("theorc-hive-v1"), length=32)
```

The shared secret is **never transmitted**. It is derived independently by both sides from the same inputs. Any observer who intercepts the pairing exchange sees only the X25519 public keys — learning the shared secret requires solving the DH problem.

#### Step 5 — Persist peer record (both sides)

`%APPDATA%\OrchestratorIDE\hive-peers.json`:

```json
{
  "version": 1,
  "peers": [
    {
      "peer_node_id":      "<64-hex>",
      "peer_name":         "BIGRIG",
      "peer_fingerprint":  "AB:CD:EF:01:23:45:67:89",
      "shared_secret_enc": "<base64(DPAPI-encrypted 32-byte shared_secret)>",
      "paired_at":         "2026-06-14T12:00:00Z",
      "last_seen_url":     "http://192.168.1.20:7079",
      "revoked":           false,
      "revoked_at":        null
    }
  ]
}
```

`shared_secret_enc` is DPAPI-protected with `CurrentUser` scope. If the file is copied to another machine, the secrets are unusable (DPAPI is machine+user bound).

### 5.3 Per-request authentication — wire contract

Every request to an authenticated endpoint (all endpoints except `/hive/info`, `/hive/pair`, and `/hive/pair/<id>`) carries four additional headers:

| Header | Value | Purpose |
|---|---|---|
| `X-Hive-Node-Id` | 64-hex ed25519 pubkey | Identifies the sending node; used to look up the shared secret |
| `X-Hive-Nonce` | 32-hex (16 random bytes) | Replay prevention |
| `X-Hive-Ts` | Unix milliseconds (decimal) | Clock-skew check |
| `X-Hive-Sig` | Base64 HMAC-SHA256 | Integrity + authentication |

**Signing input:**

```
signing_input = method + "\n"
              + path   + "\n"
              + nonce  + "\n"
              + ts     + "\n"
              + hex(SHA256(body_bytes))

// body_bytes is the raw request body; empty body → SHA256("") = e3b0c44298...
// method and path are upper-cased and lower-cased respectively, e.g. "POST\n/hive/tasks/task-123/claim"
```

**HMAC computation:**

```
sig = Base64(HMAC-SHA256(key=shared_secret, data=UTF8(signing_input)))
```

**Server-side validation (in `HiveAuthMiddleware`):**

```
1. Extract X-Hive-Node-Id → look up peer in hive-peers store
   → 401 if not found or revoked
2. Check abs(now_ms - X-Hive-Ts) < 30_000
   → 401 "clock_skew" if outside window
3. Check nonce not in per-peer nonce cache
   → 409 "replay" if already seen
4. Recompute signing_input from request; compute expected HMAC
5. CryptographicOperations.FixedTimeEquals(expected, received)
   → 401 "bad_sig" if mismatch
6. Add nonce to per-peer nonce cache (LRU, max 1000 per peer, 5 min TTL)
7. Attach peer context to request for downstream use (audit, per-node quotas)
```

The 30 s timestamp window means nodes must have reasonably synchronized clocks (NTP). This is true of any Windows machine on a home or office network. Document this requirement.

### 5.4 Unauthenticated endpoints (open by design)

| Endpoint | Reason open |
|---|---|
| `GET /hive/info` | Discovery — exposes node_id and dh_pub so a new peer can initiate pairing; no secrets |
| `POST /hive/pair` | First-contact — must be open for pairing to occur; no secrets returned |
| `GET /hive/pair/{session_id}` | Approval polling — returns only status and the approver's public key; no secrets |

`/hive/info` must now include the node's identity material:

```json
{
  "name":        "BIGRIG",
  "node_id":     "<64-hex ed25519 pubkey>",
  "dh_pub":      "<base64 x25519 pubkey>",
  "fingerprint": "AB:CD:EF:01:23:45:67:89",
  "gpu_vram_mb": 24576,
  "models":      ["qwen2.5-coder:14b", "theorc-boss:gemma4-ft"],
  "lanes":       ["coder", "researcher"],
  "hive_port":   7079,
  "rpc_port":    50052,
  "version":     "1.5.0",
  "protocol":    "theorc-hive/1"
}
```

Adding `version` and `protocol` here is the hook for version-skew handling (see Section 5.6).

### 5.5 Revocation and key rotation

**Revoke a peer (remove trust):**
1. Mark `"revoked": true, "revoked_at": "<timestamp>"` in `hive-peers.json`
2. `HiveAuthMiddleware` returns 403 for any request from that `node_id` from this point
3. Optionally: force re-pair by generating a new X25519 key pair and rotating the shared secret with all remaining peers (prevents the revoked node from impersonating using old traffic captures — relevant if the revoked node was hostile, not just decommissioned)

**Rotate all secrets (breach response):**
1. Settings → HIVE → "Rotate node secrets"
2. Generate new X25519 key pair
3. Mark all existing peer records as `"requires_repair": true`
4. Peers attempting to connect get 401 with `{"error": "repair_required"}` instead of "bad_sig"
5. Each peer must re-pair through the normal pairing ceremony
6. The Ed25519 identity key is NOT rotated — the node retains its stable fingerprint (rotation of the identity key is a separate "Reset node identity" action that breaks all existing pairings)

**Re-pair a single node (routine maintenance, IP change, reinstall):**
New node_id detected for the same display name → Warchief shows: "BIGRIG has a new identity (was AB:CD, now EF:01). Re-pair required." User approves, new shared secret derived.

### 5.6 Version skew and protocol evolution

`/hive/info` carries `"protocol": "theorc-hive/1"`. Rules:

- A node that receives a lower protocol version in `/hive/info` may choose to proceed with a compatibility subset or reject with `409 Protocol_Mismatch`
- Protocol version is incremented only when the wire format is incompatible with the prior version
- Adding new optional fields is not a version bump
- The pairing request carries `"protocol": "theorc-hive/1"` — both sides must agree on the same major version to pair

For v1: There is only `theorc-hive/1`. Document it. Future changes that break wire compatibility produce `theorc-hive/2`.

### 5.7 LAN vs. Tailscale trust boundaries

The application-layer authentication in this spec is **transport-independent**. It works identically over:
- Plain LAN (Private firewall profile) — app-layer auth is the only security
- Tailscale (WireGuard tunnel) — Tailscale provides transport encryption + Tailscale-level peer auth; app-layer auth adds identity binding and result integrity on top

The implication: **do not make Tailscale a prerequisite for HIVE security**. The design must stand on its own on a plain LAN. Tailscale nodes get defense-in-depth; LAN nodes get adequate protection.

Tailscale revocation lag (T10 in the threat model): an app-layer revocation (`"revoked": true` in `hive-peers.json`) takes effect immediately regardless of Tailscale ACL propagation delays.

---

## Section 6 — Migration Plan

The existing `hive-hosts.json` nodes are *named* but *unkeyed*. They cannot be migrated to `hive-peers.json` without re-pairing because there is no key material to carry over. The migration path is designed to avoid a hard cutover that breaks existing installations.

### Phase 0 — Identity generation (no breaking changes)

*Target: next commit. Deployed silently.*

- `HiveIdentity.cs` generates Ed25519 + X25519 key pairs on first run, persists to `hive-identity.json` under DPAPI
- `HiveNodeServer.cs`: `/hive/info` response gains `node_id`, `dh_pub`, `fingerprint`, `protocol` fields
- No behaviour change. No endpoint added. No auth enforced. Existing nodes continue working.

### Phase 1 — Pairing ceremony (v1.6 target)

- Add `POST /hive/pair` and `GET /hive/pair/{id}` endpoints to `HiveNodeServer`
- Add pairing UI to `HivePanel`: "Pair" button on undiscovered/unauthenticated nodes; fingerprint display; approve/deny dialog
- Add `hive-peers.json` store (`HivePeerStore.cs`)
- Existing `hive-hosts.json` entries gain a `"paired": false` badge in the UI: "This node is not paired — HIVE features require pairing"
- Paired nodes gain a lock icon and show their fingerprint
- **No auth enforcement yet** — paired and unpaired nodes can still reach task endpoints

### Phase 2 — HMAC enforcement (v1.6, same release as Phase 1)

- Add `HiveAuthMiddleware` to `HiveTaskQueue` and `HiveNodeServer` (excluding the three open endpoints)
- Unpaired nodes receive `401 { "error": "not_paired", "pair_url": "/hive/pair" }`
- Add `X-Hive-*` header injection to `HiveWorkerAgent` (outgoing requests)
- **Grace period flag**: `AppSettings.HiveAuthGracePeriod` (bool, default `true` for 30 days post-upgrade). When `true`, unpaired nodes that provide `X-Hive-Legacy: 1` header are logged as warnings but permitted. When `false` (or after 30 days), the middleware rejects them with 401.
- Log a startup warning when any unpaired node is in `hive-hosts.json`

### Phase 3 — Hard enforcement (v1.7 or when Phase 4 durable storage lands)

- Remove the grace period bypass
- `HiveAuthMiddleware` is unconditional on all task endpoints
- Phase 4 (SQLite persistence for HIVE state) proceeds only after this is in place, as required by `02_SECURITY_HIVEMIND.md`
- `hive-hosts.json` is deprecated; all host data migrated to `hive-peers.json`

---

## Section 7 — Minimal First Implementation Slice

The smallest slice that provides real security improvement, in order of priority:

### 7.1 `HiveIdentity.cs` — generate and persist node identity

```csharp
// OrchestratorIDE/Services/Hive/HiveIdentity.cs
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

public sealed class HiveIdentity
{
    public string NodeId      { get; }  // 64-hex ed25519 pubkey
    public string Fingerprint { get; }  // "AB:CD:EF:01:23:45:67:89"
    public byte[] DhPublicKey { get; }  // x25519 pubkey bytes (32)

    // Kept internal; used only for ECDH at pairing time
    internal ECDiffieHellman DhKey   { get; }
    internal ECDsa           SignKey  { get; }

    private HiveIdentity(ECDsa sign, ECDiffieHellman dh)
    {
        SignKey = sign;
        DhKey   = dh;

        var pubBytes = sign.ExportSubjectPublicKeyInfo(); // DER; trim to raw 32 bytes
        NodeId       = Convert.ToHexString(ExtractRawEd25519Pub(pubBytes)).ToLower();
        Fingerprint  = FormatFingerprint(SHA256.HashData(ExtractRawEd25519Pub(pubBytes)));
        DhPublicKey  = dh.PublicKey.ExportSubjectPublicKeyInfo(); // or raw 32 bytes
    }

    public static HiveIdentity EnsureLoaded()
    {
        var path = IdentityPath();
        if (File.Exists(path))
            return Load(path);
        return Generate(path);
    }

    private static HiveIdentity Generate(string path)
    {
        using var sign = ECDsa.Create(ECCurve.Ed25519);
        using var dh   = ECDiffieHellman.Create(ECCurve.CreateFromFriendlyName("X25519"));

        var dto = new IdentityDto
        {
            Version          = 1,
            NodeId           = Convert.ToHexString(ExtractRawEd25519Pub(
                                   sign.ExportSubjectPublicKeyInfo())).ToLower(),
            SigningKeyEnc    = Protect(sign.ExportPkcs8PrivateKey()),
            DhPub            = Convert.ToBase64String(dh.PublicKey.ExportSubjectPublicKeyInfo()),
            DhKeyEnc         = Protect(dh.ExportPkcs8PrivateKey()),
            CreatedAt        = DateTime.UtcNow,
        };
        dto.Fingerprint = FormatFingerprint(SHA256.HashData(
            Convert.FromHexString(dto.NodeId)));

        File.WriteAllText(path, JsonSerializer.Serialize(dto, JsonOpts));
        return new HiveIdentity(sign, dh);
    }

    private static string IdentityPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE", "hive-identity.json");

    private static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    private static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);

    private static string FormatFingerprint(byte[] hash) =>
        string.Join(":", hash[..8].Select(b => b.ToString("X2")));

    // Ed25519 SubjectPublicKeyInfo wraps the 32-byte raw key;
    // the raw key starts at offset 12 in the DER encoding.
    private static byte[] ExtractRawEd25519Pub(byte[] spki) => spki[12..44];
}
```

> Note on `ECCurve.Ed25519` and `X25519`: both are available in .NET 7+ on all platforms. On Windows, the underlying provider is CNG. On Linux/Mac (future cross-platform), it falls through to OpenSSL. The BCL abstracts this. Do not call `new ECParameters` with raw curve constants — use the named curve accessors.

### 7.2 `HiveNodeServer.cs` — expose identity in `/hive/info`

Add to the `HiveNodeInfo` record:

```csharp
public string NodeId      { get; set; } = "";
public string DhPub       { get; set; } = "";
public string Fingerprint { get; set; } = "";
public string Protocol    { get; set; } = "theorc-hive/1";
public string Version     { get; set; } = AppVersion.Current;
```

Populate from `HiveIdentity.EnsureLoaded()` at `HiveNodeServer` startup. No auth required on `/hive/info`. This is Phase 0 — no breaking change.

### 7.3 `HivePeerStore.cs` — peer persistence layer

```csharp
// OrchestratorIDE/Services/Hive/HivePeerStore.cs
public sealed class HivePeer
{
    public string   PeerNodeId      { get; set; } = "";
    public string   PeerName        { get; set; } = "";
    public string   PeerFingerprint { get; set; } = "";
    public string   SharedSecretEnc { get; set; } = "";  // DPAPI base64
    public DateTime PairedAt        { get; set; }
    public string   LastSeenUrl     { get; set; } = "";
    public bool     Revoked         { get; set; }
    public DateTime? RevokedAt      { get; set; }

    // Decoded on demand; not serialized
    [JsonIgnore] public byte[]? SharedSecret { get; set; }
}

public sealed class HivePeerStore
{
    private static readonly string StorePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "hive-peers.json");

    private readonly List<HivePeer> _peers = [];

    public HivePeer? FindByNodeId(string nodeId) =>
        _peers.FirstOrDefault(p => p.PeerNodeId == nodeId && !p.Revoked);

    public void AddPeer(HivePeer peer) { _peers.Add(peer); Save(); }

    public void RevokePeer(string nodeId)
    {
        var peer = _peers.FirstOrDefault(p => p.PeerNodeId == nodeId);
        if (peer is null) return;
        peer.Revoked = true;
        peer.RevokedAt = DateTime.UtcNow;
        Save();
    }

    public byte[] GetSharedSecret(HivePeer peer)
    {
        if (peer.SharedSecret is not null) return peer.SharedSecret;
        var raw = ProtectedData.Unprotect(
            Convert.FromBase64String(peer.SharedSecretEnc), null,
            DataProtectionScope.CurrentUser);
        peer.SharedSecret = raw;
        return raw;
    }

    public void Load() { /* deserialize from StorePath */ }
    private void Save() { /* serialize to StorePath */ }

    // Derive shared secret after ECDH exchange at pairing
    public static byte[] DeriveSharedSecret(
        ECDiffieHellman localDhKey,
        byte[] peerDhPubSpki,
        byte[] localNodeIdBytes,
        byte[] peerNodeIdBytes)
    {
        using var peerDh = ECDiffieHellman.Create();
        peerDh.ImportSubjectPublicKeyInfo(peerDhPubSpki, out _);
        var sharedIkm = localDhKey.DeriveRawSecretAgreement(peerDh.PublicKey);

        // Salt: SHA256 of sorted node IDs (deterministic regardless of initiator)
        byte[] a = localNodeIdBytes, b = peerNodeIdBytes;
        if (Comparer<byte>.Default.Compare(a[0], b[0]) > 0) (a, b) = (b, a);
        var salt = SHA256.HashData([.. a, .. b]);

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            ikm:    sharedIkm,
            outputLength: 32,
            salt:   salt,
            info:   "theorc-hive-v1"u8.ToArray());
    }
}
```

### 7.4 `HiveAuthMiddleware.cs` — per-request HMAC validation

```csharp
// OrchestratorIDE/Services/Hive/HiveAuthMiddleware.cs
public sealed class HiveAuthMiddleware
{
    private readonly HivePeerStore _peers;
    // Per-peer nonce cache: nodeId → HashSet<nonce> with TTL
    private readonly ConcurrentDictionary<string, NonceCache> _nonces = new();

    public bool TryAuthenticate(HttpListenerRequest req, out HivePeer? peer)
    {
        peer = null;
        var nodeId = req.Headers["X-Hive-Node-Id"];
        var nonce  = req.Headers["X-Hive-Nonce"];
        var tsStr  = req.Headers["X-Hive-Ts"];
        var sigB64 = req.Headers["X-Hive-Sig"];

        if (nodeId is null || nonce is null || tsStr is null || sigB64 is null)
            return Reject(req, 401, "missing_headers");

        // 1. Look up peer
        var found = _peers.FindByNodeId(nodeId);
        if (found is null)
            return Reject(req, 401, "not_paired");

        // 2. Timestamp window ±30 s
        if (!long.TryParse(tsStr, out var ts) ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - ts) > 30_000)
            return Reject(req, 401, "clock_skew");

        // 3. Replay: nonce must be unseen
        var cache = _nonces.GetOrAdd(nodeId, _ => new NonceCache());
        if (!cache.TryAdd(nonce))
            return Reject(req, 409, "replay");

        // 4. Read body and compute body hash
        using var ms = new MemoryStream();
        req.InputStream.CopyTo(ms);
        var bodyBytes = ms.ToArray();
        var bodyHash  = Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLower();

        // 5. Reconstruct signing input
        var path   = req.Url?.AbsolutePath ?? "";
        var method = req.HttpMethod.ToUpper();
        var input  = $"{method}\n{path}\n{nonce}\n{ts}\n{bodyHash}";

        // 6. HMAC verify (constant-time)
        var secret   = _peers.GetSharedSecret(found);
        var expected = HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(input));
        var received = Convert.FromBase64String(sigB64);
        if (!CryptographicOperations.FixedTimeEquals(expected, received))
            return Reject(req, 401, "bad_sig");

        peer = found;
        // Rewind body stream so downstream handlers can read it
        req.InputStream = new MemoryStream(bodyBytes);
        return true;
    }

    private static bool Reject(HttpListenerRequest req, int code, string reason)
    {
        // caller writes the response; this just returns false
        return false;
    }
}

internal sealed class NonceCache
{
    private readonly object _lock = new();
    private readonly Queue<(string nonce, long expiry)> _order = new();
    private readonly HashSet<string> _set = [];
    private const long TtlMs = 5 * 60 * 1000;
    private const int MaxEntries = 1000;

    public bool TryAdd(string nonce)
    {
        lock (_lock)
        {
            Evict();
            if (!_set.Add(nonce)) return false;
            _order.Enqueue((nonce, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + TtlMs));
            if (_set.Count > MaxEntries) { var (old, _) = _order.Dequeue(); _set.Remove(old); }
            return true;
        }
    }

    private void Evict()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        while (_order.TryPeek(out var top) && top.expiry < now)
        {
            _order.Dequeue();
            _set.Remove(top.nonce);
        }
    }
}
```

### 7.5 Integration points in existing code

**`HiveTaskQueue.cs`** — in `HandleAsync`, before dispatching to any route handler:

```csharp
private async Task HandleAsync(HttpListenerContext ctx)
{
    var path = ctx.Request.Url?.AbsolutePath ?? "";

    // Open endpoints — no auth
    if (path is "/hive/pair" || path.StartsWith("/hive/pair/"))
    {
        await HandlePairAsync(ctx);
        return;
    }

    // All other endpoints require auth
    if (!_authMiddleware.TryAuthenticate(ctx.Request, out var peer))
    {
        WriteJson(ctx, new { error = "not_paired" }, 401);
        return;
    }

    // Existing routing ...
    if (path == "/hive/tasks/next") { await HandleGetNextAsync(ctx, peer!); return; }
    // etc.
}
```

**`HiveWorkerAgent.cs`** — inject headers before every outgoing request:

```csharp
private HttpRequestMessage SignRequest(HttpMethod method, string path, object? body)
{
    var nonce    = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLower();
    var ts       = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
    var bodyJson = body is null ? "" : JsonSerializer.Serialize(body);
    var bodyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(bodyJson))).ToLower();
    var input    = $"{method.Method.ToUpper()}\n{path}\n{nonce}\n{ts}\n{bodyHash}";
    var sig      = Convert.ToBase64String(HMACSHA256.HashData(_sharedSecret, Encoding.UTF8.GetBytes(input)));

    var req = new HttpRequestMessage(method, _warchiefUrl + path);
    req.Headers.Add("X-Hive-Node-Id", _identity.NodeId);
    req.Headers.Add("X-Hive-Nonce",   nonce);
    req.Headers.Add("X-Hive-Ts",      ts);
    req.Headers.Add("X-Hive-Sig",     sig);
    if (body is not null)
        req.Content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
    return req;
}
```

---

## Appendix: Checklist before Phase 4 (durable storage) ships

From `02_SECURITY_HIVEMIND.md`, these must all be done before Phase 4:

- [x] Parameterized SQL only — no string concatenation with wire data
- [ ] **Shared-secret auth** — this spec
- [ ] **Input validation** — `WorkerId` max 64 chars, alphanumeric+dash; `Result` max 1 MB; `Msg` max 2 KB; enum validation on `Status`
- [ ] **Per-node quota** — max 100 active tasks per `node_id` per session
- [ ] **Provenance columns** — `authenticated_node_id`, `claim_token` in durable task records
- [ ] **Retention sweep** — `retain_until` column; tasks older than 30 days deleted on startup
- [ ] **Replay guard** — `once_token` unique constraint; duplicate `/complete` on same task → 409

---

*This spec uses no custom cryptographic primitives. All constructions are standard (Ed25519 RFC 8032, X25519 RFC 7748, HKDF RFC 5869, HMAC-SHA256 RFC 2104). All implementations use the .NET 10 BCL (`System.Security.Cryptography`). No external dependencies required.*
