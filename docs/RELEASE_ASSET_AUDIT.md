# TheOrc — Release Asset Audit

> Built in response to the 2026-07-12 external release review's P0 finding:
> historical Windows release builds silently omitted CUDA runtime DLLs
> (forcing NVIDIA users to CPU fallback) and a v1.11.0 workflow concurrency
> bug could omit Windows/Linux Warband assets — with no audit trail proving
> which shipped binaries were actually affected.

---

## The tool

`scripts/audit_release.ps1` — for any GitHub release tag, downloads the real
Windows assets, computes SHA-256/size, and runs the review's requested checks
with **honest, safety-scoped answers** rather than fabricated or guessed
results. See the script's own header comment for the full safety reasoning;
summary:

| Review's requested check | What this tool actually does |
|---|---|
| Expected vs. actual assets | Real: compares against the fixed Windows asset pattern list |
| File size, SHA-256 | Real: computed from the actual downloaded file |
| Platform/RID | Real: from the asset filename |
| Launch result | Real **only for the current release** (freshly built, known-safe to execute). Historical binaries are never executed unattended. |
| Installer launch result | **Never executed** — an installer can modify system state (registry, files) with no human in the loop; recorded as `not_executed` always |
| Native backend load result | Real static proxy: does the extracted archive contain the CUDA runtime DLLs (`cudart64_*.dll`, `cublas64_*.dll`, `cublasLt64_*.dll`)? A live `NativeBackendBootstrap`/GPU test only runs for the current build via `Tools/NativeProbe` |
| GPU/CPU backend selected | Not tested live for historical tags (would require executing old code) |
| Update-path result | Not tested (would require triggering the self-updater against a running older version) |
| Superseded/supported status | Real: derived from the CUDA-DLL check result + documented commit history (concurrency-bug tag list) |

**A genuinely important nuance the tool gets right, verified against git log
rather than assumed**: two different CUDA fixes landed the same day
(2026-07-04), ten minutes apart — one that bundled the DLLs directly into the
release build, immediately superseded by one that fetches them via the
**installer** into `%LOCALAPPDATA%\TheOrc\CudaRedist` instead (to avoid
bloating downloads for non-NVIDIA users). No tagged release ever shipped the
"bundled in the portable zip" mechanism. This means **CUDA DLLs being absent
from the portable zip is the correct, intended state for every release from
v1.11.3 onward** — not a defect. The tool only flags this as a real bug for
releases published before 2026-07-04, and only after confirming it live via
an actual download, not a date-based assumption alone.

## Results (first run, 2026-07-13)

Audited: `v1.12.0` (current), `v1.11.2` and `v1.11.0` (pre-CUDA-fix era, one
of them also the documented concurrency-bug release). Full machine-readable
results: [`release_audit/`](../release_audit/) — `SUMMARY.md` plus one JSON
per audited tag.

| Tag | CUDA DLLs (portable zip) | Launch (live) | Verdict |
|---|---|---|---|
| v1.12.0 | Absent — **expected**, installer fetches at install time | Started and stayed up | No known issues |
| v1.11.2 | **Absent — confirmed real bug** (pre-2026-07-04) | Not executed (historical) | Flagged: pre-CUDA-fix |
| v1.11.0 | **Absent — confirmed real bug** (pre-2026-07-04) | Not executed (historical) | Flagged: pre-CUDA-fix + documented concurrency-bug release |

## What this audit does NOT yet cover

- **Only 3 of 28 tagged releases have been audited so far.** The tool
  supports auditing any/all of them (`-All` flag), but running it against
  every historical tag — downloading and static-checking ~28 releases —
  hasn't been done in this pass. The two audited pre-fix releases were
  chosen specifically to confirm the documented incident live; extending to
  the full history is straightforward with the existing tool but is
  additional real work, not done here.
- **macOS and Linux assets are not audited** — the tool only downloads and
  checks Windows assets today. The review's own scope was specifically the
  Windows CUDA-DLL incident; macOS/Linux asset verification would need
  platform-appropriate checks (code signing/notarization status, `.tar.gz`
  extraction, executable bit) this pass didn't build.
- **No SHA-256 checksum manifest, SBOM, build provenance/attestation, or
  code signing exists for any release yet** — the review's "every future
  release should publish" list. This audit tool computes SHA-256 for its own
  verification purposes; it does not yet publish a manifest alongside each
  GitHub release, and no signing/notarization/SBOM pipeline exists. Real,
  separate, unstarted work.

## Recommended GitHub release warnings (NOT YET POSTED)

The review recommends adding a warning to affected historical releases and
marking them superseded. **This document proposes the text; it has not been
posted to the live GitHub release pages** — editing a public-facing release
page is a distinct, visible action from a repo commit and needs an explicit
decision, not something a docs/tooling pass should do unilaterally.

Proposed warning for every release tagged before 2026-07-04
(v1.0.3 through v1.11.2, pending a full audit run to confirm exact boundary
tags — the two spot-checked above are confirmed, the rest are inferred from
the same date cutoff and not yet individually re-verified):

> ⚠️ **Known issue**: this release's Windows build does not include the CUDA
> runtime DLLs required for GPU-accelerated native inference on NVIDIA
> hardware. Affected users fall back to CPU inference silently (no error
> shown). Fixed in [v1.11.3](https://github.com/hardcoreerik/TheOrc/releases/tag/v1.11.3)
> and later — please upgrade. See
> [RELEASE_ASSET_AUDIT.md](https://github.com/hardcoreerik/TheOrc/blob/master/docs/RELEASE_ASSET_AUDIT.md)
> for the full audit.

Proposed additional warning for v1.11.0 specifically:

> ⚠️ **Known issue**: this release's build workflow had a matrix-concurrency
> race that could cause Windows and/or Linux Warband assets to be silently
> omitted from the release. Verify you have the assets you expect, or
> upgrade to a later release.

## How to extend this audit

```powershell
# Audit one more historical tag
pwsh scripts/audit_release.ps1 -Tags v1.10.0

# Audit every tag (~28 releases -- downloads real assets for each, takes a while)
pwsh scripts/audit_release.ps1 -All

# Just rebuild SUMMARY.md from whatever's already been audited
pwsh scripts/audit_release.ps1 -RebuildSummary
```
