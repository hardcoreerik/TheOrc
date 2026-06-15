# Security Policy

## Reporting a Vulnerability

If you believe you've found a security vulnerability in TheOrc, please **do not open a public GitHub issue**. Public disclosure before a fix is ready puts everyone using the software at risk.

Instead, send a report to **hardcoreerik@gmail.com** with:

- A clear description of the vulnerability
- Steps to reproduce it (a minimal proof-of-concept is ideal)
- The version of TheOrc you're using (check the status bar in the app)
- Any relevant logs or screenshots

You'll get an acknowledgment within **72 hours**. If the issue is confirmed, I'll work on a fix and let you know when a patched version is available before any public disclosure.

---

## Supported Versions

Only the **latest release** receives security fixes. Older versions are not patched.

| Version | Supported |
|---|---|
| Latest (v1.6.x) | ✅ |
| Older versions | ❌ |

---

## Scope

### In scope

- **HIVE MIND network security** — request signing (HMAC-SHA256), node authentication, pairing flow, replay protection
- **Approval flow bypasses** — anything that allows a file write, shell command, or git operation to execute without explicit user approval
- **Privilege escalation** — a swarm agent escaping its tool access restrictions (e.g., TESTER gaining write access)
- **Local data exposure** — credentials, DPAPI-wrapped secrets, or node identity keys leaking in logs or artifacts
- **Installer security** — firewall rule scoping, OLLAMA_HOST binding, or setup steps that expose services beyond the intended private-network scope

### Out of scope

- Security issues in Ollama, llama.cpp, or third-party model weights — report those to their respective projects
- Vulnerabilities that require physical access to the machine running TheOrc
- Social engineering attacks
- Denial-of-service against a single local instance

---

## A note on the HIVE cryptographic layer

The HIVE MIND security layer (P-256 ECDSA node identity, HMAC-SHA256 per-request signing, DPAPI secret storage, nonce replay cache) was designed and implemented in-house and shipped in v1.6.

In v1.6.1 it went through an adversarial review of the implementation (multiple independent automated passes plus a manual multi-angle review), which surfaced and fixed 11 findings — including election-message signature verification, fail-closed authentication, a canonical-form injection, a trust/secret TOCTOU, and replay protection that now survives a restart. This was a code-level review, **not a formal independent third-party cryptographic audit**, and the design has not been externally certified.

If you have cryptography or network security expertise and spot a flaw — especially around the signing protocol, key derivation, or pairing flow — that is exactly the kind of report I most want to receive.

---

## Disclosure Policy

- Reports are handled confidentially until a fix is released.
- Credit is given in the release notes to anyone who responsibly discloses a confirmed vulnerability (unless you prefer to remain anonymous).
- There is no bug bounty program at this time.
