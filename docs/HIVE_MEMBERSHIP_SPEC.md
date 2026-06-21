# HIVE MIND — Hive Identity, Membership Certificates, and Auto-Promotion Specification

> Status: Phases 1-4 implemented 2026-06-21 (all four §9 phases landed same day). One
>         deferred remainder: cert-presentation at the request-time auth gate (§5.5
>         implementation note) — needs its own signature scheme, intentionally not bolted on.
>         Spec preceded implementation per explicit instruction: "write a fuller spec for
>         this before any code lands."
> Scope: Hive-wide identity (`HiveId`), membership certificates for non-transitive-pairing
>        trust propagation, an authenticated role-assignment RPC + "declare Warchief and
>        promote all peers" UI action, and a first-run/repair discovery wizard.
> Target release: v1.9.4
> Builds on: `HIVE_PAIRING_SPEC.md` (node identity, pairing ceremony, HMAC request auth,
>            mesh heartbeat, election) — all shipped as of v1.6/v1.7. This spec does not
>            replace any of that; it adds a layer on top.
> Author: Claude Sonnet 4.6 + Erik, based on codebase audit 2026-06-21

---

## Section 1 — Why This Spec Exists

Today, pairing establishes trust **pairwise**: Node A pairs with Node B, and only A and B
recognize each other. There is no concept of "the hive" as a thing with its own identity —
only a graph of individual pairing relationships. This works at the scale tested so far
(2-5 nodes, all manually paired by a human clicking Approve on each side). It does not work
at the scale the user is asking to design for now: "I could see this sprawling to 100's of
computers at some point." At that scale, pairwise-only trust means O(n²) human approval
clicks to fully connect a mesh — a non-starter.

This spec adds three things, building on the existing pairing/identity primitives without
replacing them:

1. **`HiveId`** — a stable identifier for the mesh itself, independent of which node is
   currently elected Warchief (Warchief churns on failover; the hive's identity should not).
2. **Membership certificates** — a way for a node to prove hive membership to a peer it has
   never directly paired with, without that peer needing to run the full fingerprint-confirm
   pairing ceremony.
3. **An authenticated role-assignment RPC + UI action** — "declare this machine the hive's
   Warchief and promote every currently-paired peer to Worker," with no per-machine approval
   click required for peers that have opted into it.

Plus a first-run (and re-callable "repair") discovery wizard that scans for an existing hive
before deciding whether to create one or join one.

---

## Section 2 — Current-State Findings

### 2.1 Node identity is per-node only; no hive-wide identity exists

`HiveIdentity.cs:24` — `NodeId = hex(SHA-256(SigningPublicKeyDer))`. This is the *node's*
stable identity (P-256 ECDSA + ECDH, not Ed25519/X25519 — the implementation diverged from
`HIVE_PAIRING_SPEC.md`'s original proposal during the v1.6 build; P-256 is the BCL-native
choice that shipped). There is no field anywhere — `HiveIdentity`, `HivePeer`, `HiveNodeInfo`,
`AppSettings` — that identifies *which mesh* a node belongs to. A node freshly installed on
an isolated machine and a node that has been part of a 50-machine hive for a year look
identical from this field's perspective.

### 2.2 Trust is pairwise and does not propagate

`HivePeerStore.cs:23-67` (`HivePeer`) stores one record per *directly paired* peer. There is
no peer-of-a-peer concept. `HiveNodeServer.ApprovePairing` (`HiveNodeServer.cs:168-230`)
only ever creates a relationship between the two parties physically exchanging the pairing
handshake. Pairing with the Warchief does not introduce you to the rest of the hive.

### 2.3 `HiveNodeRole` already has a three-tier model, underused

`HivePeerStore.cs:9` — `enum HiveNodeRole { Observer, Worker, Controller }`. `Controller` is
already the model's "elevated trust" tier — `HivePairingClient.cs` (`CompletePairing`)
explicitly comments that promoting a peer to `Controller`-eligible "must be a deliberate,
later action, not a side effect of pairing." Nothing in the shipped code currently performs
that deliberate promotion — there's no UI or RPC for it. This spec is the first real consumer
of that tier.

### 2.4 `HiveAcceptControlPolicy` already models exactly the consent question this spec needs, and is unused

`HivePeerStore.cs:12-18`:

```csharp
public enum HiveAcceptControlPolicy { Never, Ask, Allowlist, AnyPaired }
```

The doc comment says this governs "when a peer may assert Warchief (controller) authority
over this node" — which is precisely the auto-promote question the user is asking about.
**No code path currently reads or enforces this enum.** `ApprovePairing` sets every newly
paired peer's `AcceptControlFrom` to `Ask` (or `Never` for mobile) and nothing ever checks
it afterward. This spec is also the first real consumer of this field.

### 2.5 Naming collision: "Set as Warchief" already exists, means something unrelated

`HivePanel.axaml.cs:477` already has a context-menu item literally labeled **"🎯 Set as
Warchief"**, calling `SetAsWarchiefTarget` (`HivePanel.axaml.cs:559-567`). What it actually
does: points *this machine's* local swarm-task dispatcher at a remote Ollama host
(`HiveTaskQueue.QueuePort`) so "this machine will send all swarm tasks to \[host] for
distribution." This is a **per-machine local routing preference**, unrelated to
`HiveElectionService`'s mesh-authority Warchief (the crown-badge, election-winner concept).

The user's new ask — declare *this* machine the mesh's Warchief and push Worker role to
every peer — is the *other* Warchief concept entirely. Shipping both with the same label is
a real, immediate UX bug, not a future risk. **Resolution: rename the existing item before
adding the new one.** Proposed: rename `"🎯 Set as Warchief"` → `"📤 Route my tasks here"`
(or similar — exact wording is a small follow-up, not blocking this spec). The new action
gets the unambiguous label `"👑 Declare this machine Warchief"`.

### 2.6 Discovery exists; "is there an active hive here" detection does not

`HiveBeacon.cs` already does LAN UDP broadcast/probe (`ScanAsync`, `HiveBeacon.cs:79-130`)
carrying name, URL, models, VRAM. It does not carry any hive-identity field, so a scan today
cannot answer "is there an existing hive on this network, and which one." The installer
(`OrchestratorSetup/`) does zero network scanning of any kind — confirmed via
`WelcomePage.xaml.cs` (minimal, no hive logic) and `HiveEnroller.cs` (enrollment/firewall
only, no discovery).

### 2.7 Existing crypto primitives this spec reuses (no new cryptography)

- `HiveIdentity.Sign(data)` / `HiveIdentity.Verify(pubKeyDer, data, signature)` — already
  static, already used for pairing proof-of-key. Membership certificates reuse this exactly.
- `HivePeer.SigningPublicKeyDer` — already stored per paired peer. Cert verification needs
  no new key-distribution mechanism; the verifier already has what it needs once it has
  directly paired with the cert's issuer.
- `HiveNodeServer`'s existing strict-auth RPC pattern (`_strictAuth.Validate(req, body)` at
  `HiveNodeServer.cs:356`, used identically by `/hive/mesh/heartbeat` and
  `/hive/mesh/election/*`) — the new role-assignment RPC follows this exact pattern.

---

## Section 3 — Design Decisions (confirmed with the user, 2026-06-21)

1. **Trust-propagation model: membership certificates**, not hub-and-spoke vouching and not
   "keep pairwise pairing forever." Chosen over hub-and-spoke specifically because the cert
   is a portable, independently-verifiable artifact rather than a live round-trip to a
   single hub node.
2. **Discovery/join flow lives in the in-app first-run wizard**, reusing existing
   `HiveBeacon.ScanAsync` networking rather than duplicating it into the installer binary —
   *plus* it must be re-callable later as a "repair hive association" action, both manually
   and auto-triggered when the app detects a broken hive association.

---

## Section 4 — `HiveId`: Design

### 4.1 What it is

A random 128-bit identifier (GUID, formatted as standard hyphenated hex), minted **once**,
by whichever node becomes the hive's *founder* — the first node that completes first-run
discovery and finds no existing hive to join. It identifies the mesh, not any one machine.
It does not change when the Warchief changes via election.

### 4.2 Storage

Added to `HiveIdentity`'s persisted file (`hive-identity.json`, already DPAPI-encrypted —
no new encryption surface):

```json
{
  "signingPriv": "<base64, existing field>",
  "exchangePriv": "<base64, existing field>",
  "hiveId": "3f29a7c1-8e44-4b2a-9d31-7a0c4e9b1f06",
  "hiveRole": "Founder"
}
```

`hiveRole` here is informational/local only (`"Founder" | "Member" | "Unset"`) — it records
*how this node came to have this HiveId*, for diagnostics and for the repair wizard's
"why does this say Unset" troubleshooting path. It is not transmitted and carries no
authority by itself (authority comes from `HivePeer.Role == Controller`, Section 5).

A node with `hiveRole: "Unset"` (fresh install, wizard not yet run, or explicitly skipped)
has no `HiveId` and is excluded from all mesh-authority operations in this spec until it
completes the wizard.

### 4.3 Propagation

Added as a new field on the existing pairing wire types:

- `HivePairingRequest` gains `HiveId` (initiator's, may be empty if initiator is itself
  unset/founding for the first time).
- `HivePairingResponse` gains `HiveId` (responder's).

Rule applied in `ApprovePairing` and in `HivePairingClient.CompletePairing`:

```
if both sides have a HiveId and they differ:
    refuse to complete pairing — surface "these are two different hives; pairing
    across separate hives is not supported" rather than silently merging or
    silently picking one. (A future "merge two hives" flow is explicitly out of
    scope for this spec.)
if exactly one side has a HiveId:
    the side without one adopts it as part of completing this pairing
if neither side has one:
    this is two unset nodes pairing directly with each other before either has
    run the wizard — the responder (who is approving, i.e. already has a person
    sitting at it confirming) becomes the founder, mints a fresh HiveId, and it
    flows to the initiator in the approval response.
```

That third case means **the wizard is not strictly required to end up in a valid hive
state** — direct pairing without ever opening the wizard still works and still produces a
valid `HiveId`, exactly as pairing works today. The wizard is the *better* path (it scans
first and avoids accidentally founding a second hive when one already exists on the
network), not the *only* path. This matters: it means this spec adds no hard requirement
that breaks any existing pairing flow.

### 4.4 Beacon broadcast

`HiveBeacon.cs` broadcast payload gains a `hiveId` field (empty string if unset). This is
what makes the wizard's "is there an active hive on this network" scan actually answer the
question — today's beacon can say "a TheOrc node is here" but not "and it belongs to hive
X." No change to the beacon's security model: this field is exactly as sensitive as the
node name/VRAM/model list already broadcast unauthenticated today (Section 11 of
`HIVE_PAIRING_SPEC.md` — beacon is display-only, deliberately open).

---

## Section 5 — Membership Certificates: Design

### 5.1 The problem this solves, precisely

Today: Node A pairs with Controller C. Node A later wants to interact with Worker W, who has
*never* directly paired with A. Without this spec, A and W must run the full
fingerprint-confirm pairing ceremony — a human click on both sides — even though both A and
W already separately trust C.

With this spec: when C approves A's pairing, C also issues A a **membership certificate**.
When A later contacts W, A presents the certificate alongside its normal authenticated
request. W checks: *do I already directly trust the certificate's issuer (C) as a
`Controller`?* If yes, W accepts A as a hive member with the role the certificate states —
no separate pairing ceremony, no human click on W's side.

### 5.2 What this deliberately does NOT do (scope discipline)

This is **not** a general-purpose PKI / certificate-authority chain. Specifically:

- **No delegation chains.** A certificate is only ever issued by a node holding
  `HivePeer.Role == Controller` from the verifier's own direct, pre-existing pairing with
  that Controller. A Worker cannot issue certs. A Controller introduced *transitively* (i.e.
  the verifier has never itself paired with that Controller) is not trusted on the strength
  of someone else's say-so — only a Controller the verifier *personally* paired with counts.
- **No automatic Controller promotion via certificates.** Becoming a `Controller` remains
  exactly what it is today: a deliberate, manual grant at pairing-approval time (or via a
  separate future "promote to Controller" action, explicitly out of scope here). Certificates
  only ever assert `Observer` or `Worker` membership.
- **No revocation propagation.** If a Controller is later revoked by one node, certificates
  it already issued are not automatically invalidated mesh-wide. Mitigated by a short
  validity window (Section 5.4) — a revoked Controller's old certs simply expire rather than
  needing active revocation-list distribution. This is a deliberate simplicity trade-off,
  documented here so it isn't mistaken for an oversight: full revocation propagation would
  require either an online revocation check (defeats the "no live round-trip" point of
  certs) or a distributed revocation list (real complexity for a feature whose entire
  purpose is avoiding that kind of machinery). Re-evaluate only if a real incident shows
  this window is a practical problem.

This keeps the verification rule to one sentence: *trust a certificate if and only if you
already directly trust its issuer as a Controller.* No recursion, no second-hand trust.

### 5.3 Certificate format

```json
{
  "hiveId":        "3f29a7c1-8e44-4b2a-9d31-7a0c4e9b1f06",
  "subjectNodeId": "<64-hex of the node this cert vouches for>",
  "subjectName":   "HARDCORELAPTOPMSI",
  "role":          "Worker",
  "issuerNodeId":  "<64-hex of the Controller who issued this>",
  "issuedAt":      "2026-06-21T18:00:00Z",
  "expiresAt":     "2026-07-21T18:00:00Z",
  "signature":     "<base64 ECDSA-P256 signature over the canonical field bytes above>"
}
```

Signing input (canonical, newline-joined, matches the existing pairing-proof pattern in
`HivePairingClient.PairCoreAsync` rather than inventing a new serialization convention):

```
signingInput = hiveId + "\n" + subjectNodeId + "\n" + role + "\n"
             + issuedAt(ISO8601) + "\n" + expiresAt(ISO8601) + "\n" + issuerNodeId
signature    = issuer.Sign(UTF8(signingInput))      // HiveIdentity.Sign — existing method
```

Default validity: **30 days**. Reissued automatically on each successful mesh heartbeat
exchange between the subject and its issuing Controller (piggybacked on the existing
`HiveMeshHeartbeat` traffic — no new periodic job), so a continuously-connected node's
certificate never actually expires in practice; only a node that's been offline for 30+ days
needs to re-establish via its Controller before its cert is honored by third parties again.

### 5.4 Issuance

Extends `HiveNodeServer.ApprovePairing` (`HiveNodeServer.cs:168-230`): immediately after
persisting the new `HivePeer`, if `identity` (the approving node) currently holds
`Role == Controller` for itself within the hive — which for the founder is implicitly true
from the moment it mints `HiveId` — it also constructs and signs a membership certificate
for the newly paired peer and includes it in the `HivePairingResponse`. The peer stores its
own certificate locally (new field on the persisted identity file, not `HivePeer`, since
it's the *subject's own* credential to present to others — not something the issuer
tracks per-peer beyond the issuance moment).

**Implementation note (2026-06-21):** "holds `Role == Controller` for itself" assumes a field
that didn't actually exist before this phase — `HivePeer.Role` only ever records roles *this*
node has granted to *others*; there was no field anywhere for "what role did my own pairing
approver grant me." Phase 2 added `HiveIdentity.SelfRole` (set from `HivePairingResponse.
AssignedRole` when a pairing completes — that field already existed on the wire, it just
wasn't being captured) and a computed `CanIssueMembershipCerts => HiveRole == Founder ||
SelfRole == Controller`, which is what `ApprovePairing` actually checks.

If the approving node does **not** hold `Controller`, no certificate is issued at pairing
time — pairing still completes exactly as it does today (this spec adds capability, it
doesn't make ordinary Worker-to-Worker pairing require Controller involvement).

### 5.5 Presentation and verification

New optional header on authenticated requests, alongside the existing
`X-Hive-*` HMAC headers already defined in `HIVE_PAIRING_SPEC.md` §5.3:

| Header | Value |
|---|---|
| `X-Hive-Membership-Cert` | Base64 JSON of the certificate (Section 5.3), if the sender has one and the recipient is not already a directly-paired peer |

Verification (new method on `HivePeerStore`, called from `HiveNodeServer.HandleAsync` only
on the path where the sender's `NodeId` is *not* already a known peer):

```csharp
public bool TryAcceptViaMembershipCert(MembershipCert cert, out HivePeer? provisionalPeer)
{
    provisionalPeer = null;

    var issuer = Find(cert.IssuerNodeId);
    if (issuer is null || issuer.Revoked || issuer.Role != HiveNodeRole.Controller)
        return false;                                   // don't trust unknown/non-Controller issuers

    if (cert.ExpiresAt < DateTime.UtcNow) return false;  // expired

    var signingInput = BuildSigningInput(cert);          // same canonical format as issuance
    if (!HiveIdentity.Verify(issuer.SigningPublicKeyDerBytes, UTF8(signingInput),
                              Convert.FromBase64String(cert.Signature)))
        return false;

    if (HiveId != cert.HiveId) return false;             // different hive entirely

    provisionalPeer = new HivePeer
    {
        NodeId  = cert.SubjectNodeId,
        Name    = cert.SubjectName,
        Role    = cert.Role,
        MaxRole = cert.Role,            // a cert-admitted peer is capped at the role the cert grants —
                                         // it cannot become Controller via this path, ever (5.2)
        AcceptControlFrom = HiveAcceptControlPolicy.Ask,  // still defaults safe even though admission was automatic
    };
    return true;
}
```

A `provisionalPeer` accepted this way is **not** written into `hive-peers.json` as a fully
trusted, persistent peer — it's held in-memory for the duration of the interaction (task
claim, RPC call) and re-verified on the next contact rather than persisted. This avoids
silently growing the trust store from automated, non-human-confirmed admissions. If the same
node is later directly paired through the normal ceremony, it becomes a regular persisted
`HivePeer` at that point, same as any other pairing today.

**Implementation note (2026-06-21):** `HivePeerStore.TryAcceptViaMembershipCert` (the
verification logic above) shipped in Phase 2, unit-tested in isolation. Wiring it into
`HiveNodeServer.HandleAsync`'s request-time auth gate is deliberately **not** part of that
same change. The existing `_strictAuth.Validate` HMAC check requires a shared secret from a
completed ECDH exchange — a cert-admitted node has never done that exchange with this
specific verifier, so it cannot pass that check today. Accepting a cert at the wire level
needs the *subject* to instead prove possession of its own signing key on each request (akin
to the pairing-proof signature, not an HMAC it structurally cannot have) — a small but
genuinely new piece of security-critical surface that deserves its own focused design+review
pass rather than being bolted onto an unrelated commit. Tracked as the explicit remainder of
Phase 2 in Section 10.

---

## Section 6 — Role-Assignment RPC and "Declare Warchief" UI Action

### 6.1 New endpoint

`POST /hive/mesh/role-assign`, authenticated via the existing strict-auth path
(`_strictAuth.Validate`, same as `/hive/mesh/heartbeat` and `/hive/mesh/election/*` —
`HiveNodeServer.cs:356-375`). No new auth mechanism.

Request body:

```json
{
  "hiveId":        "3f29a7c1-...",
  "assignerNodeId": "<64-hex, must match the authenticated sender>",
  "newRole":       "Worker",
  "reason":        "warchief-declaration"
}
```

### 6.2 Server-side handling

```
1. authResult = _strictAuth.Validate(req, body)        // existing — rejects unknown senders
2. if authResult.NodeId != body.assignerNodeId → 403   // can't assign on someone else's behalf
3. assignerPeer = _peers.Find(authResult.NodeId)
4. if assignerPeer is null → 403 "not a known peer"    // (no membership-cert path here —
                                                         //  role-assignment is Controller-
                                                         //  weight authority, deliberately
                                                         //  excluded from cert auto-admission,
                                                         //  consistent with §5.2)
5. gate on assignerPeer.AcceptControlFrom (THIS is the field's first real consumer):
     Never      → 403, always
     Ask        → queue an approval card exactly like a pairing request (OnRoleAssignReceived
                   event, mirroring OnPairingRequestReceived) — human clicks once
     Allowlist  → auto-accept only if assignerPeer.NodeId ∈ assignerPeer.ControlAllowlist
     AnyPaired  → auto-accept unconditionally (the user has explicitly opted in to this)
6. on accept: this node's own Role updates locally to body.newRole; ActiveRole updates;
   AddEvent-style log entry written so it's visible in the Activity feed (per the user's
   explicit goal this session: "clearly seeing the pairing functions happening in the
   Activity window" — role changes get the same visibility treatment)
7. response: { "status": "accepted" | "pending_approval" | "rejected" }
```

### 6.3 UI action: "👑 Declare this machine Warchief"

New context-menu item on the **center ("This PC") card only** — promoting yourself doesn't
make sense from a remote peer's card. Confirmation dialog:

> "Declare HARDCOREPC the hive's Warchief? This sends a role-assignment request to all 4
> currently paired peers, asking each to set their role to Worker. Peers configured to
> auto-accept will update immediately; others will show an approval prompt on their end."

On confirm: sends `POST /hive/mesh/role-assign` to every `HivePeerStore.All()` entry that
isn't revoked, using each peer's `LastKnownAddress` (existing field, already used by the
mesh heartbeat for exactly this kind of "where do I send this" lookup). Logs one Activity
line per peer with the outcome (`accepted` / `pending_approval` / `rejected` / unreachable),
not just a single aggregate "done."

This is **not** the same as winning an election (`HiveElectionService`) — it's a manual
override a human explicitly invokes, separate from and not interfering with automatic
failover. If an election is currently underway when this is invoked, refuse with a clear
message ("an election is in progress — wait for it to resolve") rather than racing the two
mechanisms.

### 6.4 New Settings/per-peer surface needed

`AcceptControlFrom` has no UI today (Section 2.4). This spec needs, at minimum, a per-peer
toggle/dropdown in the HIVE panel's node-card detail view (or context menu) exposing the
four `HiveAcceptControlPolicy` values directly, plus a HIVE-wide default in Settings
("Default auto-accept policy for new peers") applied at pairing-approval time instead of the
current hardcoded `Ask`. Without this, the feature technically works but every peer still
requires a one-time manual policy change via... nothing, today, since there's no UI for it.
This is a small, necessary addition, not optional polish.

---

## Section 7 — First-Run Discovery Wizard + Repair Flow

### 7.1 Trigger conditions

- **First run**: no `hive-identity.json` exists, or it exists with `hiveRole: "Unset"`.
- **Manual repair**: a new "Repair HIVE association" action (Settings → HIVE, and/or a
  context-menu item on "This PC").
- **Auto-detected problem** (surfaces the same wizard, framed as a recoverable prompt, not a
  silent auto-fix):
  - This node has a `HiveId` but zero entries in `hive-peers.json` resolve to *any* reachable
    address for longer than a full mesh-heartbeat cycle (peers exist but none are reachable —
    could mean isolated network, could mean stale config).
  - A peer pairing attempt completes but is refused at the `HiveId`-mismatch check (Section
    4.3) — strong signal the user is trying to join a different hive than the one they're
    currently part of.
  - `hive-identity.json` fails to load (corrupt) and had to regenerate — the regenerated
    identity has no `HiveId`, so this falls out of the first-run check naturally, but it's
    worth a distinct message ("Your HIVE identity was reset — rejoin your hive?") rather than
    a generic first-run prompt, so the user understands *why* they're seeing it again.

### 7.2 Flow

```
1. Scan: HiveBeacon.ScanAsync(timeout: 3s) over LAN, plus any configured Tailscale peers
   (reuses existing scan logic — Section 2.6 — extended per §4.4 to surface hiveId per result)
2. Group results by hiveId (results with empty hiveId are "unset" nodes, shown separately)
3a. If one or more hives found:
      "Found N existing hive(s) on this network: [HiveName/short-id, M nodes online]"
      → user picks one → Join
      Join = the normal PairAsync flow (HivePairingClient, existing code, unchanged) against
      one reachable node from that hive's result set, defaulting to whichever result reports
      Role == Controller if any do, else any reachable node. Still requires the human
      fingerprint-confirm step on first contact — this spec does not skip that for the very
      first cross-trust establishment (Section 4.3's third case already covers "what if the
      user skips the wizard entirely and just pairs directly" — the wizard is the discovery
      layer in front of the same unchanged pairing ceremony, not a replacement for it).
3b. If no hive found:
      "No existing hive found. Create a new one?" → on confirm, mint a fresh HiveId locally
      (Section 4.1), set hiveRole: "Founder", Role: Controller for self. No network call
      needed — founding is purely local until a second node ever pairs in.
3c. User may also explicitly choose "Skip for now" → hiveRole stays "Unset"; HIVE features
    that require a HiveId (role-assignment, membership certs) are unavailable until the
    wizard is run again, but ordinary direct pairing (Section 4.3 case 3) still works as an
    escape hatch.
4. Repair-triggered runs of this same flow additionally show *why* it was triggered (the
   specific condition from §7.1) at the top of the wizard, so it doesn't look like an
   unprompted nag.
```

### 7.3 Where this lives

Per the confirmed decision (Section 3): inside the app, building on the in-progress
`FirstRunWindow` Avalonia port rather than the installer binary. The installer's job stays
exactly what it is today (enrollment: firewall rules, URL ACLs) — it does not gain network
scanning. The wizard is just another screen/step shown on first launch after install
completes, and is independently re-invokable later from Settings without needing to be tied
to "first run" at the code level (i.e., implement it as a normal dialog/window the app can
show at any time, with "first run" being only one of several call sites that open it).

---

## Section 8 — Updated Threat Model Deltas

Building on `HIVE_PAIRING_SPEC.md` §2 (unchanged threats T1-T12 still apply as documented
there). New considerations introduced by this spec:

| ID | Threat | Mitigation |
|---|---|---|
| T13 | A compromised Controller issues certs vouching for a malicious node | Bounded by §5.2's no-delegation-chain rule — damage is limited to nodes that *directly* trust that specific Controller, and bounded in time by the 30-day cert expiry (§5.3). Revoking the Controller (existing `HivePeerStore.Revoke`) stops new admissions immediately; already-admitted provisional peers (§5.5, not persisted) are re-verified on next contact and will fail once the issuer shows `Revoked: true`. |
| T14 | Role-assignment RPC used to force-downgrade or force-upgrade roles without consent | Gated entirely by the existing, previously-dormant `AcceptControlFrom` (§6.2) — `Never`/`Ask` defaults mean no silent role change without either a prior explicit opt-in or a per-event human click. The RPC cannot grant `Controller` (§6.1 `newRole` is logically restricted to `Observer`/`Worker` — enforce this server-side, not just by UI convention, since the request body is attacker-shaped input). |
| T15 | Two independently-founded hives on the same network get accidentally bridged | Explicitly refused, not silently merged (§4.3, mismatched-`HiveId` case). A future deliberate "merge two hives" flow is out of scope here. |
| T16 | Membership-cert replay (presenting an old, expired-but-not-yet-checked cert) | `ExpiresAt` is checked server-side on every presentation (§5.5 step 2), not just at issuance — no caching of "this cert was valid once." |

**Server-side enforcement note for T14**: the role-assignment handler must validate
`newRole ∈ {Observer, Worker}` and reject anything else with 400, independent of what the UI
ever sends — the wire format is attacker-controlled input the moment the RPC exists, same
discipline already applied throughout `HiveAuthMiddleware`/`HiveNodeServer` for existing
endpoints (caps on `WorkerId` length, enum validation on `Status`, etc., per
`HIVE_PAIRING_SPEC.md` §9 T9).

---

## Section 9 — Migration / Phased Build Order

Each phase is independently shippable and additive — no phase requires breaking an earlier
one, mirroring the migration discipline `HIVE_PAIRING_SPEC.md` §6 used for the original
pairing rollout.

### Phase 1 — `HiveId` foundation (no behavior change)

- `HiveIdentity`: add `HiveId`, `HiveRole` (local-only enum, Section 4.2) to the persisted
  identity file. Default `HiveRole: Unset`, `HiveId: ""` for all existing installs until
  they either run the wizard or complete a pairing that assigns one (Section 4.3).
- `HivePairingRequest`/`HivePairingResponse`: add `HiveId` field; implement the three-case
  reconciliation logic (Section 4.3) in `ApprovePairing` and `HivePairingClient.
  CompletePairing`.
- `HiveBeacon`: add `hiveId` to the broadcast payload and `ScanAsync` result type.
- No new endpoints. No UI changes required to ship this phase (existing pairing keeps
  working identically for any node that never touches the new field).

### Phase 2 — Membership certificates

- New `HiveMembershipCert` model + canonical signing-input builder (Section 5.3). **Shipped
  2026-06-21.**
- `ApprovePairing`: issue a cert when the approving node holds `CanIssueMembershipCerts`
  (Section 5.4; required adding `HiveIdentity.SelfRole` — see implementation note there, this
  field didn't exist before Phase 2 and the spec's original wording assumed it did). **Shipped.**
- `HivePeerStore.TryAcceptViaMembershipCert` (Section 5.5). **Shipped, unit-tested in isolation.**
- `HiveNodeServer.HandleAsync`: accept the new `X-Hive-Membership-Cert` header on the path
  where the sender is not already a known peer; construct a provisional, non-persisted
  `HivePeer` on success. **Deliberately deferred** — needs a subject-proves-key-possession
  signature scheme that doesn't exist yet (see the implementation note at the end of Section
  5.5); rushing this into the wire-level auth gate without its own review pass is how you get
  a forgeable-identity hole, not a feature.
- Reissuance piggybacked on `HiveMeshHeartbeat` traffic (Section 5.3). **Deferred** — has no
  caller until the item above lands, so reissuance has nothing to refresh yet.

### Phase 3 — Role-assignment RPC + "Declare Warchief" UI action

- `POST /hive/mesh/role-assign` (Section 6.1-6.2), first real consumer of
  `AcceptControlFrom`.
- Rename the existing `"🎯 Set as Warchief"` menu item to remove the naming collision
  (Section 2.5) before adding the new, differently-named action.
- New `"👑 Declare this machine Warchief"` context-menu item on the center card (Section
  6.3).
- New per-peer `AcceptControlFrom` UI surface + Settings-level default (Section 6.4) — this
  is required for Phase 3 to be usable at all, not a follow-up.

### Phase 4 — First-run wizard + repair flow — **Shipped 2026-06-21.**

- `HiveDiscoveryWizard` (`OrchestratorIDE.Avalonia/UI/Windows/`): scans via
  `HiveBeacon.ScanAsync`, groups results by `hiveId`, offers Join (normal pairing ceremony
  with fingerprint confirm) or Create (purely local founding). Flat single-screen `Window`
  shown via `ShowDialog<bool>` (true = HiveId changed), matching `FirstRunWindow` rather than
  building paged-wizard infrastructure for one screen.
- Three trigger sites wired (Section 7.1): first HIVE start with `HiveRole.Unset`
  (`MainWindow.InitializeAsync`, **sequenced after the first-run personalisation wizard**, not
  raced from inside the background `StartHiveAsync` — a concurrent-modal bug caught in review);
  manual "🔧 Repair HIVE association" (HivePanel center-card context menu); and a
  `Result.HiveIdConflict` pairing refusal (HivePanel auto-prompts to open the repair wizard —
  keyed off a dedicated flag, not a substring match on the message).
- On success the node's beacon payload is re-broadcast (`MainWindow.RefreshHiveBeaconHiveId`,
  fired via the new `HivePanel.OnHiveAssociationChanged` event) so the new HiveId propagates
  immediately rather than waiting for the next restart.

---

## Section 10 — Implementation Status

| Component | File | Status |
|---|---|---|
| `HiveId` + `HiveRole` on identity | `HiveIdentity.cs` | ✅ Implemented (`SetHive`, instance-locked + persist-before-mutate) |
| `HiveId` on pairing request/response | `HiveNodeServer.cs`, `HivePairingClient.cs` | ✅ Implemented (§4.3 three-case reconciliation both sides; early `hiveid_mismatch` refusal at request time) |
| `HiveId` on beacon payload | `HiveBeacon.cs` | ✅ Implemented |
| `HiveMembershipCert` model + signing | new file `HiveMembershipCert.cs` | ✅ Implemented |
| Cert issuance at pairing | `HiveNodeServer.ApprovePairing` | ✅ Implemented (gated on `CanIssueMembershipCerts`) |
| Cert verification / provisional peer admission | `HivePeerStore.cs` | ✅ Implemented (`TryAcceptViaMembershipCert`, unit-tested) |
| Cert presentation at request-time auth gate | `HiveNodeServer.HandleAsync` | 🔲 Deferred — needs subject-proves-key signature scheme (§5.5) |
| `POST /hive/mesh/role-assign` | `HiveNodeServer.cs` | ✅ Implemented (server-side Observer/Worker-only, `TryParseAssignableRole` unit-tested) |
| `AcceptControlFrom` enforcement (first real use) | `HiveNodeServer.HandleRoleAssign` | ✅ Implemented |
| "🎯 Set as Warchief" rename → "📤 Route my swarm tasks here" | `HivePanel.axaml.cs` | ✅ Implemented |
| "👑 Declare this machine Warchief" action | `HivePanel.axaml.cs` | ✅ Implemented |
| Per-peer `AcceptControlFrom` UI + Settings default | `HivePanel.axaml.cs` / `SettingsPanel` | ✅ Implemented |
| First-run/repair wizard | `HiveDiscoveryWizard` (new) | ✅ Implemented (3 trigger sites) |
| Beacon scan grouped by `hiveId` | `HiveBeacon.cs` + wizard UI | ✅ Implemented |
| swarmcli parity (`--list-peers`, `--declare-warchief`, `--set-accept-control`, extended `--show-identity`) | `Tools/SwarmCli/Program.cs` | ✅ Implemented |

---

## Section 11 — Open Questions Deliberately Deferred (not blocking v1.9.4)

- **Delegated cert issuance** (Controllers vouching for other Controllers) — explicitly
  scoped out in Section 5.2. Revisit only if the founder-must-be-reachable-ish-often
  constraint (a Controller, not literally the founder, can issue once promoted — so this is
  milder than "founder must always be online," but still concentrates issuance in a small,
  fixed set) proves to be a real-world blocker.
- **Hive merge flow** (T15) — two independently founded hives are refused from bridging, not
  reconciled. No design proposed here for an intentional merge.
- **Cert revocation propagation** — accepted as a known, documented gap (Section 5.2), bounded
  by the 30-day expiry rather than solved actively.
- **Topological layout change reflecting Warchief/Worker role** (carried over from the prior
  session's "next version" backlog) — visual-only, unrelated to the trust-model work in this
  spec; still deferred, not part of v1.9.4 unless explicitly pulled in later.
- **Duplicate node-entry merging across LAN/Tailscale-IP/Tailscale-DNS** (same prior-session
  backlog item) — also unrelated to this spec, still deferred.
