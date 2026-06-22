# TheOrc — Multi-OS Release Pipeline Specification

> Status: Scoping only, 2026-06-21. No code written yet. Target: **v1.9.5**.
> Depends on: `INSTALLER_REVAMP_SPEC.md` Phases 1/2/4/5 (all shipped 2026-06-21) — this spec
>             closes the gap those phases explicitly left open: the platform-installer classes
>             are correct, but nothing upstream of them can hand a Linux or macOS install an
>             actual binary to install.
> Author: Claude Sonnet 4.6 + Erik.

---

## Section 1 — Why This Spec Exists

`OrchestratorSetup` now has a working `IPlatformInstaller` for Windows, Linux, and macOS
(hardware detection, firewall, launchers, uninstall — see `INSTALLER_REVAMP_SPEC.md`). None
of that matters yet, because every step that *acquires a binary* — the main app, the llama.cpp
runtime, optionally Ollama — only knows how to fetch a Windows artifact, and GitHub Releases
only contains a Windows artifact to fetch. A real install on Linux or macOS today fails at the
download step, before any of the new platform-installer code ever runs.

This spec scopes exactly what has to change to close that gap. It does NOT cover packaging
follow-ups (AppImage, `.dmg`, macOS notarization) — those are downstream of "can we produce a
working raw binary for that OS at all," which is what this spec is about.

---

## Section 2 — Current-State Findings (confirmed by reading the code, 2026-06-21)

| Component | File | Problem |
|---|---|---|
| Release workflow | `.github/workflows/release.yml` | `runs-on: windows-latest`; every publish step is `--runtime win-x64`; only `OrchestratorIDE.exe`/`OrchestratorSetup.exe`/one `.zip` are verified+uploaded. No Linux/macOS job at all. |
| App download manifest | `Setup/model-manifest.json` → `app.download_url` | Single OS-unaware string, hardcoded to `.../OrchestratorIDE.exe`. |
| App exe paths | `OrchestratorSetup/Models/InstallerState.cs` → `AppExePath`/`PortableAppExePath` | Both hardcode `"OrchestratorIDE.exe"` regardless of OS. |
| App acquisition logic | `OrchestratorSetup/Services/InstallOrchestrator.cs` (`ResolveAppUrl`, download/copy steps) | Reads the single manifest key above; copies/moves to the hardcoded `.exe` path. |
| llama.cpp runtime resolver | `OrchestratorSetup/Services/LlamaCppResolver.cs` (`MustContain`/`MustNotContain`) | Every variant entry (`cuda12`/`cuda11`/`vulkan`/`avx2`/`cpu`) requires the literal term `"win"` in the matched GitHub-release asset filename — can never match llama.cpp's own `ubuntu-x64`/`macos-arm64` release assets, even though llama.cpp publishes them. |
| llama.cpp static fallback | `Setup/model-manifest.json` → `runtimes.llama_cpp.variants` | Same problem, statically: every filename is `...-win-...zip`. No `metal` variant exists at all (macOS has no CUDA; needs its own variant key). |
| Ollama installer | `OrchestratorSetup/Services/OllamaInstaller.cs` | Hardcoded to download+silently-run `OllamaSetup.exe` (NSIS `/S` flag). Linux/macOS Ollama installation is fundamentally different (`curl \| sh` script on Linux; `.dmg`/Homebrew on macOS) — not just a different URL. |
| GGUF model downloads | `Setup/model-manifest.json` → `models[].url` | **Already fine** — plain HuggingFace HTTPS URLs, no OS dependency. Nothing to change here. |

---

## Section 3 — Phased Build Plan

Each phase is independently shippable and reviewable, same convention as the installer spec.
Phases are ordered so each one is testable before the next depends on it.

### Phase A — `release.yml`: publish Linux + macOS artifacts
- Add a build matrix (`win-x64`, `linux-x64`, `osx-arm64`; `osx-x64` optional/deferred —
  Apple Silicon is the realistic majority of new Mac hardware) publishing both
  `OrchestratorIDE.Avalonia` and `OrchestratorSetup` per RID, same
  `--self-contained -p:PublishSingleFile=true` shape already used for Windows.
- Non-Windows artifacts need `chmod +x` before archiving — `Compress-Archive`/`zip` does not
  reliably preserve the Unix executable bit; use `tar -czf` for the Linux/macOS portable
  bundles instead of `.zip` (matches the ecosystem convention for those OSes anyway).
- Upload all per-OS artifacts to the same GitHub Release alongside the existing Windows ones.
  No existing Windows asset name or behavior changes — purely additive.
- **Testable in isolation**: a manual `workflow_dispatch` run produces a release with 3x the
  current artifact count; verify each binary actually runs on its target OS (a real Linux box
  or VM, a real Mac) before moving to Phase B.

### Phase B — OS-keyed manifest schema
- `Setup/model-manifest.json`'s `app` key becomes OS-keyed:
  ```json
  "app": {
    "windows": { "download_url": "...-latest.../OrchestratorIDE.exe", "size_mb": 647 },
    "linux":   { "download_url": "...-latest.../OrchestratorIDE-linux-x64.tar.gz", "size_mb": ... },
    "macos":   { "download_url": "...-latest.../OrchestratorIDE-osx-arm64.tar.gz", "size_mb": ... }
  }
  ```
  (Exact asset-naming convention finalized alongside Phase A, once real release artifacts
  exist to name.)
- `InstallOrchestrator.ResolveAppUrl` picks the right top-level key via
  `OperatingSystem.IsWindows()`/`IsLinux()`/`IsMacOS()`, same pattern `PlatformInstaller.Resolve()`
  already uses.
- **Testable in isolation**: `ResolveAppUrl` unit-testable independent of Phase A actually
  having real assets yet (point it at a manifest with placeholder URLs first).

### Phase C — Wire up `AppExePath`/`PortableAppExePath`/`LaunchCommand`
- `InstallerState.AppExePath`/`PortableAppExePath` stop hardcoding `"OrchestratorIDE.exe"` and
  instead delegate to `PlatformInstaller.Current.LaunchCommand(AppInstallPath)` — this finally
  closes the "`DefaultAppDir`/`LaunchCommand` implemented but never wired" gap flagged as a
  MINOR finding back in Phase 2's grok review and left deferred ever since.
- Downstream archive-extraction logic (wherever the downloaded `.tar.gz`/`.zip` gets unpacked)
  needs the right unpack call per OS — `ZipFile.ExtractToDirectory` for the Windows `.zip`,
  `tar -xzf` (via `Process`, same pattern `LinuxPlatformInstaller`/`MacPlatformInstaller`
  already establish for shelling out to OS tools) for the `.tar.gz` on Linux/macOS.
- **Testable in isolation**: with Phase A+B real, a full install attempt on Linux/macOS should
  get exactly as far as a working `OrchestratorIDE` binary in `AppInstallPath`, runnable
  manually, even before Phase D's runtime resolver is fixed.

### Phase D — `LlamaCppResolver` + manifest llama.cpp variants
- Add an OS axis to `MustContain`/`MustNotContain` (e.g. key by `$"{os}-{variant}"` instead of
  just `variant`), with real term-matching against llama.cpp's actual current release asset
  names — must be verified against the live GitHub Releases API at implementation time, not
  guessed here (the existing class's own comment already warns this naming drifts; that's
  doubly true extending into two more OSes for the first time).
- macOS has no CUDA variants at all; needs a `metal` variant key (`MacPlatformInstaller.
  DetectHardwareAsync` already emits `"metal"` as `RuntimeVariant` for exactly this — Phase D
  is what finally gives that value somewhere to resolve to).
- `Setup/model-manifest.json`'s `runtimes.llama_cpp.variants`/`size_mb`/`hardware_selection`
  static-fallback tables get the equivalent per-OS entries, mirroring Phase B's `app` key shape.
- **Testable in isolation**: with Phases A-C done, a Linux/macOS install can now also fetch a
  real, runnable llama.cpp build — the full chain works end-to-end for the native-runtime path
  (the default/recommended path per `INSTALLER_REVAMP_SPEC.md`).

### Phase E — Ollama installer (Linux/macOS) — lower priority, may defer past v1.9.5
- Ollama is the secondary/optional runtime path (native llama.cpp is the default per the
  installer spec), so this can ship later than Phases A-D without blocking "Linux/macOS
  installs work."
- Linux: `curl -fsSL https://ollama.com/install.sh | sh` (Ollama's own documented install
  method) instead of a silent `.exe`. macOS: Ollama ships a `.dmg`/`.zip` containing an `.app`,
  no NSIS-style silent flag — needs its own install/verify logic, not just a different URL.
- **Testable in isolation**: independent of every other phase here — `OllamaInstaller`'s
  Windows path is untouched either way.

### Phase F — Real-hardware verification
- Everything above is reviewable and buildable on Windows (cross-compiling `-r linux-x64`/
  `-r osx-arm64` already proven out during `INSTALLER_REVAMP_SPEC.md` Phases 4-5), but none of
  it has actually *run* on real Linux/macOS hardware or a VM. Phase F is explicitly: get a
  Linux box and a Mac, run the installer end-to-end on each, fix whatever the cross-compile
  build couldn't have caught (file permissions, path quoting under a real shell, actual
  firewall/elevation prompts firing correctly).

---

## Section 4 — Explicitly Out of Scope (separate, later work)

- **AppImage / `.dmg` packaging** — once a raw binary install works (this spec), wrapping it
  in a self-mounting/portable package format is a UX polish layer on top, not a prerequisite.
- **macOS code-signing / notarization** — required for Gatekeeper to not block an unsigned
  `.app`/binary by default; irrelevant until Phase A-F produce something to sign in the first
  place. Already flagged as an open question in `INSTALLER_REVAMP_SPEC.md` §8.
- **`.deb`/`.rpm`/Homebrew/AUR distribution** — real OS-native package-manager integration,
  a different (larger) scope than "the portable installer works."
