# TheOrc — Multi-OS Release Pipeline Specification

> Status: Phases A-D implemented 2026-06-21 (macOS only; Linux still has no release.yml
>         publish job — Phase A's matrix has one macOS leg added, no Linux leg yet). Phase E
>         (Linux/macOS Ollama install) and Phase F (real-hardware verification) not started —
>         this has only been cross-compiled and grok-reviewed, never run on an actual Mac.
>         Took five review rounds; see commit 659784a for the full list of what each one
>         caught (a release-creation race between matrix legs, a nested-vs-flat archive
>         mismatch, no tar.gz extraction support at all, no Unix executable bit anywhere in
>         the download/copy/extract paths, and three separate spots in the RUNNING app —
>         UpdateChecker, SelfUpdater, LlamaServerManager — that only ever recognized Windows
>         binary names, which would have produced a Mac install that completes and then can't
>         update itself or find its own runtime). Target: **v1.9.5**.
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

### Phase A — `release.yml`: publish Linux + macOS artifacts — **macOS shipped 2026-06-21, Linux not started.**
- `release.yml` is now a build matrix with a `windows-latest`/`win-x64` leg (unchanged) and a
  `macos-latest`/`osx-arm64` leg (new). No `linux-x64` leg yet — `osx-x64` (Intel Mac) also
  deferred, same "get ONE real artifact shipping and tested first" reasoning.
- Both legs publish `OrchestratorIDE.Avalonia` and `OrchestratorSetup`, same
  `--self-contained -p:PublishSingleFile=true` shape. macOS binaries get `chmod +x` right
  after publish (the runner does this natively, not cross-compiled); portable bundle is
  `tar.gz` with a flat staged layout (copy both binaries into one staging dir first) matching
  the Windows zip's flat root — tar's default `-C dir file1 file2` preserves each path's
  parent directory as an archive member, which would have silently contradicted the release
  notes' "extract and run" instructions.
- The real, found-in-review risk this shape creates: two matrix legs both calling
  `action-gh-release` against the same tag in parallel can both reach "create release" at the
  same instant — one wins, the other 422s and silently drops that OS's assets. Fixed with a
  job-level `concurrency: group: release-${{ github.ref }}` (no `matrix.*` in the group key,
  so it serializes every leg of this job, not just deduplicates retries of the same leg).
- **Not yet tested**: this has only been validated via `python -c "import yaml..."` syntax
  checking and local cross-compiles -- the actual GitHub Actions run (real `macos-latest`
  runner, real matrix-concurrency behavior, real `action-gh-release` against a real tag) has
  never executed. First real test happens whenever the next tag gets pushed.

### Phase B — OS-keyed manifest schema — **Shipped 2026-06-21 (windows/macos; no linux key yet).**
- `Setup/model-manifest.json`'s `app` key is OS-keyed (`windows`/`macos`); `InstallOrchestrator.
  ResolveAppUrl` picks the right one via `OperatingSystem.IsWindows()`/`IsMacOS()`, same pattern
  `PlatformInstaller.Resolve()` uses. No `linux` key — `ResolveAppUrl` returns early (empty
  `AppDownloadUrl`) there until Phase A gets a Linux leg, rather than guessing at a URL with no
  real asset behind it.

### Phase C — Wire up `AppExePath`/`PortableAppExePath`/`LaunchCommand` — **Shipped 2026-06-21.**
- `InstallerState.AppInstallPath`/`ModelStoragePath`/`AppExePath`/`PortableAppExePath` all now
  delegate to `PlatformInstaller.Current` instead of hardcoding Windows-shaped paths — this
  closes the "`DefaultAppDir`/`LaunchCommand` implemented but never wired" gap flagged back in
  Phase 2's grok review, AND a second, previously-unnoticed instance of the exact same
  folder-name-drift bug already found once in `LinuxPlatformInstaller`'s `removeUserData` step
  (`AppInstallPath`'s old default resolved to a DIFFERENT folder name than
  `MacPlatformInstaller.DefaultAppDir` on macOS). No behavior change for Windows — every
  default resolves to the identical value as before.
- The app binary itself needs no archive extraction (GitHub release uploads the raw single-file
  binary directly per OS, not zipped) — only the llama.cpp runtime download does. See Phase D.
- Added `ZipExtractService.ExtractTarGzAsync` (`System.Formats.Tar`, preserves Unix file mode
  including the executable bit) for the runtime archive on Linux/macOS, and
  `InstallOrchestrator.EnsureExecutable` (`File.SetUnixFileMode`) after every place a binary
  gets written via raw HTTP download or `File.Copy`/`File.Move` — those carry zero file-mode
  metadata, so without this, the downloaded app binary would be non-executable on macOS even
  though the install "succeeded."

### Phase D — `LlamaCppResolver` + manifest llama.cpp variants — **Shipped 2026-06-21.**
- `MustContain`/`MustNotContain` are now OS+arch-aware (`ResolveOsTagAndExtension`/
  `ResolveArchTag`), verified against the actual current llama.cpp release via
  `gh api repos/ggml-org/llama.cpp/releases/latest`, not guessed. Found and fixed a latent
  Windows-only bug along the way: llama.cpp renamed its CPU build's filename label from
  "avx2" to "cpu" at some point — our `avx2` variant's match terms had quietly stopped
  matching anything on Windows too, not just macOS, before this fix.
- macOS has one unified "metal" variant (no CUDA/Vulkan-equivalent split — Metal is always
  available), matching `MacPlatformInstaller.DetectHardwareAsync`'s own `RuntimeVariant`.
  `Setup/model-manifest.json`'s static fallback tables got the matching entry.
- **Three more reachable paths found and fixed in the RUNNING app, not just the installer**
  (`UpdateChecker.GetReleaseAssetUrlAsync`, `SelfUpdater.DownloadReleaseAsync`,
  `LlamaServerManager.LocateServerExe`) — all three only ever recognized Windows binary names.
  Fixing only the installer would have produced a Mac install that completes successfully and
  then can't update itself or find its own extracted runtime to launch.

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
