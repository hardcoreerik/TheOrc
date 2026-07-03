# TheOrc — Cross-Platform Installer Revamp Specification

> Status: Phases 1, 2, 4, 5 (§7) implemented 2026-06-21 — WPF→Avalonia UI port (Phase 3's
>         page restructure folded in), the `IPlatformInstaller` extraction, and Windows/
>         Linux/macOS implementations all exist now. What's NOT done: the actual
>         cross-platform release pipeline -- `InstallOrchestrator`'s download/copy logic,
>         the model manifest's single OS-unaware `app.download_url` key, `release.yml`
>         (win-x64-only), and `LlamaCppResolver`'s filename-matching tables (hardcode "win"
>         in every variant) are all still Windows-only, so neither the main app binary nor
>         the llama.cpp runtime can actually be acquired on Linux or macOS yet regardless of
>         how correct the three platform-installer classes are. That's release-engineering
>         work on the main app and its dependencies, not something any `IPlatformInstaller`
>         implementation can close alone -- explicitly scoped as separate, not-yet-started
>         follow-up work. Page-by-page direction confirmed with the user 2026-06-21 via a
>         structured walk-through of all 10 then-current pages.
> Original scope: Rewrite `OrchestratorSetup` (then a Windows-only WPF wizard) as a cross-platform
>        Avalonia GUI installer; abstract every Windows-coupled action (hardware detection,
>        firewall, shortcuts, registry/uninstall) behind a per-OS layer; pivot runtime
>        provisioning toward the native runtime (llama.cpp + GGUF via TheOrc's own internal
>        downloaders) with Ollama demoted to an optional path.
> Target release: not yet assigned (post-v1.9.4; larger than a point release).
> Builds on: the Avalonia migration (the app + `OrchestratorIDE.Daemon` are already
>        cross-platform `net10.0`); this brings the *installer* to parity.
> Author: Claude Sonnet 4.6 + Erik, based on codebase audit 2026-06-21.

---

## Section 1 — Why This Spec Exists

`OrchestratorSetup` was built before the WPF→Avalonia migration. Before this revamp, the
installer was still `net10.0-windows` with `<UseWPF>true</UseWPF>` and deeply coupled to
Windows-only facilities. It could only ever produce a Windows install,
which capped the entire product's reach regardless of the app being portable.

This spec defines a from-the-studs rewrite that (a) runs on Windows, Linux, and macOS, and
(b) modernizes the install flow to match the project's current reality — the native runtime
direction, the app's own first-run personalization, and the app's internal model-download
stack — rather than the pre-migration assumptions baked into the current pages.

---

## Section 2 — Current-State Findings

### 2.1 Tech stack (all Windows-locked)

`OrchestratorSetup/OrchestratorSetup.csproj`:
- `<OutputType>WinExe</OutputType>`, `<TargetFramework>net10.0-windows</TargetFramework>`,
  `<UseWPF>true</UseWPF>` — WPF is Windows-only.
- `PackageReference System.Management` — WMI, Windows-only.
- Branding via `pack://application:,,,/` URIs (WPF resource scheme) and `Resource`/
  `EmbeddedResource` items.

### 2.2 The 10-page WPF wizard

Page order from `InstallerViewModel.Page` enum:

| # | Page | Windows coupling |
|---|------|------------------|
| 0 | Welcome | none (branding + "what gets installed" list) |
| 1 | License | none |
| 2 | HardwareDetect | **WMI** (`Win32_VideoController`, `ManagementObjectSearcher`) for GPU/VRAM/RAM |
| 3 | DotNetCheck | `dotnet-install.ps1`; page is optional (SDK only needed for "update from source") |
| 4 | InstallPath | app + model folders; Windows-style defaults |
| 5 | Profile | writes `.agent.md` — **duplicates** the app's `FirstRunWindow` |
| 6 | ModelSelect | picks/downloads a coding model — **overlaps** the app's `ModelDownloaderWindow` |
| 7 | OllamaCheck | detects/installs `ollama.exe` |
| 8 | Download | **the install itself** (see 2.3) |
| 9 | Complete | launch button |

### 2.3 What the install (Download step) actually does — `InstallOrchestrator.cs`

- `HiveEnroller.Enroll(Log)` — opens HIVE ports 7077-7079 via **`netsh advfirewall`** + URL ACLs
  via `netsh http` (Windows-only, Private profile).
- `ProfileMerger.CreateShortcuts(_state)` — Desktop + Start Menu **`.lnk`** shortcuts (Windows-only).
- `UninstallService.Register(_state)` — **registry** Apps & Features entry under
  `HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OrchestratorIDE`.

Uninstall (`UninstallService.Uninstall`) deletes the `.lnk` files, optional `%APPDATA%` user
data, and the registry key. All three are Windows-native mechanisms with no portable equivalent
in the current code.

### 2.4 Assets the rewrite can reuse as-is (already portable)

- `LlamaCppResolver.TryResolveLatestAsync(variant, …)` — resolves the correct llama.cpp build
  per GPU variant; pure HTTP + zip, no Windows coupling. **This is the native-runtime resolver
  the revamp's Runtime page builds on.**
- The app's internal model-download stack (`OrchestratorIDE/Services/Models/`):
  `ModelDownloadService`, `HuggingFaceClient`, `CuratedModelCatalog`, `ModelSearchService`,
  `ModelStatusService`. **This is "our own download methods from the appropriate sources"** —
  the installer should call into these rather than shelling out to `ollama pull`.
- `DownloadService` / `ZipExtractService` / `EmbeddedResources` / `ProfileMerger`
  (the `.agent.md` generation half, minus shortcut creation) — mostly portable logic.
- The model manifest + profile templates are already `EmbeddedResource`s read via
  `Assembly.GetManifestResourceStream()` — that mechanism is cross-platform; only the WPF
  `pack://` *image* URIs need to change (Avalonia uses `avares://`).

---

## Section 3 — Confirmed Decisions (user walk-through, 2026-06-21)

| Decision point | Choice |
|---|---|
| **Overall approach** | **Port the wizard to Avalonia** — one cross-platform GUI installer, same guided UX on every OS. |
| **Delivery** | **Self-contained per-OS binary** — publish per RID (win-x64 `.exe`, linux-x64 binary, osx `.app`), download-and-run with no prerequisites, mirroring how the app ships. |
| **Welcome (#0)** | **Rework content** — the "what gets installed" list is stale post-Avalonia/native-runtime. |
| **License (#1)** | **Fold into Welcome** — single accept checkbox on the welcome screen. |
| **HardwareDetect (#2)** | **Per-OS detection backends** behind an interface (WMI on Windows; `nvidia-smi` / `/proc` / `/sys` on Linux; `system_profiler` on macOS). |
| **DotNetCheck (#3)** | **Keep, optional** — cross-platform via `dotnet-install.sh` on Linux/Mac; still skippable. |
| **InstallPath (#4)** | **Per-OS XDG-aware defaults** with overrides; keep the app/model folder split. |
| **Profile (#5)** | **Drop from installer** — defer to the app's `FirstRunWindow` (single source of truth for `.agent.md`). |
| **ModelSelect (#6)** | **Offer both** (download-now optional, or defer to app), using **TheOrc's internal GGUF downloaders** toward the native runtime, not `ollama pull`. |
| **OllamaCheck (#7)** | **Replace with a "Runtime Setup" page** — native runtime by default (llama.cpp + GGUF), Ollama as a collapsible "advanced/alternative" option. |
| **Runtime strategy** | **Native-first, Ollama optional** — default path provisions llama.cpp + GGUF; Ollama becomes an opt-in checkbox. |
| **Install actions (#8)** | **Per-OS integration layer** — interface with Windows / Linux / macOS implementations. |
| **Firewall** | **Automatic with elevation** (UAC on Windows, `sudo` on Linux/Mac) — users must NOT have to open ports manually; manual instructions are a true last-resort fallback only. |
| **Complete (#9)** | **Keep launch + add a HIVE button** (configure/verify HIVE networking); OS-correct launch command. |
| **Uninstall** | **Per-OS uninstall layer** — registry/Apps&Features (Windows), removal script + `.desktop` cleanup (Linux), delete `.app` + support files (macOS). |

---

## Section 4 — Cross-Platform Architecture

### 4.1 The core abstraction: `IPlatformInstaller`

Every Windows-coupled action moves behind a single interface, with three implementations
selected once at startup via `OperatingSystem.IsWindows()/IsLinux()/IsMacOS()`:

```csharp
public interface IPlatformInstaller
{
    string DefaultAppDir   { get; }   // §4.2 per-OS paths
    string DefaultModelDir { get; }

    Task<HardwareInfo> DetectHardwareAsync(IProgress<string>? log, CancellationToken ct);

    // Returns the exact manual command(s) if elevation was declined/unavailable, else null.
    Task<string?> ConfigureFirewallAsync(int[] ports, IProgress<string>? log, CancellationToken ct);

    void CreateLaunchers(InstallerState state);   // .lnk / .desktop / .app or symlink
    void RegisterUninstall(InstallerState state); // registry / script / (noop+manifest)
    void Uninstall(string installPath, bool removeUserData, IProgress<string>? log);

    string LaunchCommand(string installPath);     // exe / ./binary / open .app
}
```

`HardwareDetector` (currently a static WMI class) becomes the Windows implementation of
`DetectHardwareAsync`; its `HardwareInfo` record is already OS-neutral and is kept as the
shared result type. `HiveEnroller`'s `netsh` logic becomes the Windows
`ConfigureFirewallAsync`. `ProfileMerger.CreateShortcuts` and `UninstallService` likewise
split into per-OS implementations of `CreateLaunchers` / `RegisterUninstall` / `Uninstall`.

### 4.2 Per-OS default paths (§InstallPath)

| | App dir | Model dir |
|---|---|---|
| **Windows** | `%LOCALAPPDATA%\OrchestratorIDE` (current behavior) | `%LOCALAPPDATA%\OrchestratorIDE\Models` |
| **Linux** | `$XDG_DATA_HOME/theorc` (default `~/.local/share/theorc`) | `$XDG_DATA_HOME/theorc/models` |
| **macOS** | `~/Library/Application Support/TheOrc` (binary under `~/Applications` if `.app`) | `~/Library/Application Support/TheOrc/Models` |

Model dir is independently overridable (GGUF files are large and users often want a separate
disk). The app already resolves its own model root from settings, so the installer only seeds
the default; nothing in the app hardcodes the Windows path.

### 4.3 Firewall: automatic-with-elevation, never manual-first

Per the confirmed firewall decision, `ConfigureFirewallAsync` always *attempts* to open the
ports itself, escalating privileges as needed, and only surfaces manual instructions if that
genuinely cannot proceed:

- **Windows** — run `netsh advfirewall firewall add rule …` (current `HiveEnroller` logic).
  Elevation via a UAC prompt is expected and acceptable; the installer relaunches the firewall
  step elevated rather than asking the user to run commands.
- **Linux** — detect `ufw` then `firewalld`; run the matching `ufw allow` / `firewall-cmd`
  through `pkexec`/`sudo` (the UAC equivalent). If neither firewall is present, treat the host
  as having no active firewall (common on desktop Linux) and succeed silently.
- **macOS** — the application firewall is per-app, not per-port; add the app binary to the
  allowlist via `socketfilterfw` under `sudo` if the firewall is on, else succeed silently.
- **True fallback only**: if elevation is declined or the platform firewall is unknown, return
  the exact command string so the Complete page can show it — but this is the exception, not
  the default path. The Complete page's HIVE button re-invokes `ConfigureFirewallAsync` so a
  user who declined elevation the first time can retry without reinstalling.

---

## Section 5 — New Page Flow

Net result: 10 pages → **8** (License folded into Welcome; Profile dropped; OllamaCheck
becomes Runtime Setup).

```
0. Welcome          (+ license acceptance, reworked "what gets installed")
1. HardwareDetect   (per-OS detection backend; manual override always available)
2. DotNetCheck      (optional, skippable; dotnet-install.sh off-Windows)
3. InstallPath      (per-OS XDG-aware defaults, app + model split)
4. Runtime Setup    (was OllamaCheck — native-first: llama.cpp + GGUF; Ollama = advanced opt-in)
5. ModelSelect      (internal GGUF downloader; download-now optional or defer to app)
6. Install          (was Download — per-OS IPlatformInstaller actions; auto firewall)
7. Complete         (launch + HIVE button; OS-correct launch command)
```

### 5.0 Welcome (+ License)

Reworked bullets reflecting current reality: "OrchestratorIDE — the cross-platform AI coding
workspace", "Native local AI runtime (llama.cpp) — no external dependencies", "GPU-matched
coding models pulled from Hugging Face", "(optional) Ollama if you prefer it". License text
shown inline (or via a "view license" expander) with one "I accept the AGPL-3.0 license"
checkbox gating Next.

### 5.1 HardwareDetect

Calls `IPlatformInstaller.DetectHardwareAsync`. Same collapsible-log + result-grid UX as today.
Detection is best-effort everywhere; the manual GPU-variant override (already present) is the
guaranteed fallback when a backend can't read the hardware.

### 5.2 DotNetCheck

Unchanged in purpose (optional SDK for update-from-source). Off Windows, swap the
`dotnet-install.ps1` invocation for `dotnet-install.sh`. Always `CanLeave() == true`.

### 5.3 InstallPath

Two folder pickers seeded from `IPlatformInstaller.DefaultAppDir` / `DefaultModelDir` (§4.2).

### 5.4 Runtime Setup (replaces OllamaCheck)

Native runtime is the default and selected path: resolve the correct llama.cpp build via
`LlamaCppResolver.TryResolveLatestAsync` for the detected GPU variant, download + extract it
into the install dir. A collapsible "Advanced: use Ollama instead" section preserves the old
detect/install-Ollama flow for users who explicitly choose it; choosing it sets a flag the
Install step branches on. Ollama is never installed unless explicitly selected.

### 5.5 ModelSelect

Recommends a coding model matched to detected VRAM (existing logic), but the **download is
performed by the app's internal stack** — `ModelDownloadService` + `HuggingFaceClient` +
`CuratedModelCatalog` pulling GGUF from Hugging Face — not `ollama pull`. "Offer both": a
"download now" checkbox (default on) that fetches the starter model during install, OR leave it
unchecked and let the app's `ModelDownloaderWindow` pull on first launch (the app already
handles the empty-model state). Sharing the download stack means installer and app pull
identically from the same sources, with no second implementation to drift.

### 5.6 Install (replaces Download)

Drives `InstallOrchestrator`, which now calls `IPlatformInstaller` for every OS-specific step:
copy/extract the app, provision the runtime (§5.4), optionally pull the model (§5.5),
`ConfigureFirewallAsync` (auto, elevated), `CreateLaunchers`, `RegisterUninstall`. Self-advancing
page as today.

### 5.7 Complete

Success screen + "Launch TheOrc" (via `IPlatformInstaller.LaunchCommand`). **New HIVE button**:
re-invokes `ConfigureFirewallAsync` / verifies the HIVE ports are open, so a user who declined
elevation during install can enable HIVE networking after the fact without a reinstall. A short
line notes that first launch personalizes the agent (the dropped Profile page's job, now the
app's).

---

## Section 6 — Packaging & Delivery

Per the "self-contained per-OS binary" decision, the installer publishes the same shape as the
app's release pipeline, per RID:

- **win-x64** — self-contained single-file `.exe` (as today).
- **linux-x64** — self-contained single-file binary (chmod +x); a `.desktop` launcher is what
  `CreateLaunchers` writes *post-install* for the app, not for the installer itself.
- **osx-x64 / osx-arm64** — self-contained binary; `.app` bundling is a packaging follow-up
  (see Open Questions), not a blocker for a runnable installer.

The Avalonia installer must not depend on a system .NET (the chicken-and-egg the current WPF
installer avoids by being self-contained) — same `--self-contained -p:PublishSingleFile=true`
shape the app and the sync-fleet skill already use.

---

## Section 7 — Phased Build Plan

Each phase independently builds and is reviewable; the installer stays shippable on Windows
throughout (no big-bang cutover).

### Phase 1 — Project + UI port — **Shipped 2026-06-21.**
- `OrchestratorSetup` is now Avalonia (`net10.0`, no `UseWPF`); all pages ported `.xaml` →
  `.axaml`, `pack://` image URIs → `avares://`. Ported directly to the §5 8-page flow (not the
  old 10) since rebuilding pages slated for deletion would be wasted work. Windows-only service
  calls (`HardwareDetector`/WMI, `HiveEnroller`/netsh, `UninstallService`/registry) are plain
  C# and carry over completely unchanged — no `IPlatformInstaller` shim needed for Phase 1 to
  be behavior-identical on Windows. `HardwareDetector.Detect` gained a runtime
  `OperatingSystem.IsWindows()` guard (not `#if WINDOWS` — no different type needed on the
  other branch, just skip the call) so a future non-Windows build degrades to a safe CPU
  default instead of crashing.

### Phase 2 — Extract `IPlatformInstaller` + Windows impl — **Shipped 2026-06-21.**
- `IPlatformInstaller` defined (§4.1, with two documented deviations from the original sketch:
  `ConfigureFirewallAsync` takes no `ports` param since `HiveEnroller.Enroll` doesn't vary it,
  and returns `Task<bool>` not `Task<string?>` since there's no Linux/macOS manual-fallback
  string yet to return). `WindowsPlatformInstaller` is pure delegation to the existing
  `HardwareDetector`/`HiveEnroller`/`ProfileMerger.CreateShortcuts`/`UninstallService` — no
  rewrite. `PlatformInstaller.Current` (`Lazy<T>`-backed) resolves it on Windows, throws
  `PlatformNotSupportedException` elsewhere until Phases 4-5. Took three review rounds to
  actually land "no behavior change": the subtle one was a swallow-and-return-false wrapper
  around `HiveEnroller.Enroll` that looked like it preserved the retry button's old try/catch
  but silently changed `InstallOrchestrator`'s install-time HIVE step from "exception fails
  the install" to "swallowed, proceeds as if it succeeded" — that caller never had a
  try/catch before. Exceptions now propagate from the shared implementation; only the two
  callers that always handled them locally still do, in their own code.

### Phase 3 — Page restructure — **folded into Phase 1, shipped 2026-06-21.**
- Ported directly to the final 8-page flow during the WPF→Avalonia port itself, rather than
  porting the old 10 pages first and restructuring them in a separate pass — rebuilding pages
  slated for deletion (License as separate, Profile, OllamaCheck) would have been wasted work.
  See Phase 1's entry above; nothing left to do here.

### Phase 4 — Linux implementation — **Shipped 2026-06-21.**
- `LinuxPlatformInstaller`: `/proc`+`nvidia-smi`/`lspci` detection, XDG paths, `ufw`/`firewalld`
  via `pkexec`/`sudo`, `.desktop` launchers, manifest-file uninstall registration. First
  non-Windows `IPlatformInstaller`. Took three review rounds: a stdout/stderr pipe deadlock
  (redirected stderr was never read, so any tool output past the pipe buffer hung the await)
  plus no timeout on calls invoked with `CancellationToken.None`; then an uncaught
  `Process.Start` throw when a probed command (`which`/`ufw`/`pkexec`) doesn't exist; then a
  `.desktop` quoting bug and an `XDG_CONFIG_HOME` miss.
- **Does NOT yet make an end-to-end Linux install possible** — confirmed by reading the
  surrounding pipeline, not just this diff. `InstallerState.AppExePath`/`PortableAppExePath`,
  `InstallOrchestrator`'s download/copy logic, the single OS-unaware `app.download_url` key in
  `Setup/model-manifest.json`, and `.github/workflows/release.yml` (win-x64-only, no Linux
  publish job or release asset) are all untouched and Windows-only. A real Linux install today
  fails at the download step before reaching `LinuxPlatformInstaller` at all. This is exactly
  the gap Phase 5 below already names ("Per-OS publish RIDs + delivery wrappers") — closing it
  is release-engineering work on the main app, not something Phase 4 alone can finish, and is
  deliberately left as its own task rather than scope-crept into this one.

### Phase 5 — macOS implementation — **Shipped 2026-06-21** (the `IPlatformInstaller` half).
- `MacPlatformInstaller`: `system_profiler`+`uname`+`sysctl` detection (Apple Silicon vs Intel
  Mac, Metal variant selection), `~/Library/Application Support` paths, the per-app
  Application Firewall via `osascript`-elevated `socketfilterfw` (raw `sudo` has no TTY to
  prompt in from a GUI app -- `osascript ... with administrator privileges` is the actual
  macOS-native equivalent of UAC/`pkexec`), a `~/Applications` symlink launcher standing in
  for a real `.app` bundle (deferred, see below), manifest-file uninstall registration.
  `IPlatformInstaller.ConfigureFirewallAsync` gained an `appExePath` parameter for this --
  macOS's firewall is per-app, not per-port, so it's the one implementation that actually
  needs the binary's real path; Windows/Linux ignore it. Took one review round on two bugs
  specific to this platform (resolving the app path via `DefaultAppDir` instead of the real
  install path; an `osascript -e <script-with-embedded-quotes>` call corrupted by .NET's
  flat-Arguments re-tokenization, fixed with an `ArgumentList`-based `RunAsync` variant).
  Also fixed in the same commit, found while researching macOS's settings-folder convention:
  `LinuxPlatformInstaller`'s `removeUserData` step was deleting `~/.config/theorc`, which the
  main app never writes to (its real folder is `~/.config/OrchestratorIDE`) -- that uninstall
  checkbox silently did nothing on every real Linux install since Phase 4 shipped.
- **NOT shipped**: "Per-OS publish RIDs + delivery wrappers" -- the actual cross-platform
  release pipeline. `InstallOrchestrator`'s download/copy logic, the model manifest's single
  OS-unaware `app.download_url` key, `release.yml` (win-x64-only, no Linux/macOS publish job
  or release asset), and `LlamaCppResolver`'s filename-matching tables (hardcode `"win"` in
  every variant: cuda12/cuda11/vulkan/avx2/cpu) are all untouched. Neither the main app binary
  nor the llama.cpp runtime can actually be downloaded on Linux or macOS today -- a real
  install on either OS fails at the download step before reaching any of the
  `IPlatformInstaller` code Phases 4-5 shipped. AppImage/`.dmg` packaging and macOS
  notarization (Section 8's Open Questions) are downstream of solving this first. This is
  release-engineering work on the main app and its dependencies, not something either
  platform-installer class could close alone -- intentionally a separate, not-yet-scoped task
  rather than folded into "the installer."

---

## Section 8 — Open Questions (not blocking the spec)

- **macOS code-signing / notarization** — an unsigned `.app` is gatekeeper-blocked. The user
  already accepted Windows Smart App Control re-blocking for now (deferred code-signing);
  macOS notarization is the same class of deferred concern, surfaced here so it isn't mistaken
  for an oversight. Likely needs an Apple Developer account before a frictionless Mac install.
- **Elevation UX on Linux** — `pkexec` (graphical) vs `sudo` (terminal); `pkexec` is the better
  GUI fit but isn't installed everywhere. Detection + fallback ordering to be finalized in
  Phase 4.
- **Installer self-delivery wrappers** — AppImage (Linux) / `.dmg` (macOS) packaging of the
  installer binary itself is deferred (the "Bootstrap: self-contained per-OS binary" decision
  explicitly scoped wrapper packaging as a follow-up; a bare runnable binary ships first).
- **Daemon-only installs** — the v2.5 daemon-centric direction may eventually want a headless,
  no-GUI install path (a server with no display). Out of scope here (this spec is the GUI
  installer), but the `IPlatformInstaller` layer is exactly what a future `swarmcli --install`
  or daemon installer would reuse, so it's built to not preclude that.
