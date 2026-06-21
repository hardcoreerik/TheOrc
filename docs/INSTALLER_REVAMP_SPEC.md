# TheOrc — Cross-Platform Installer Revamp Specification

> Status: Phase 1 (§7, WPF→Avalonia UI port) implemented 2026-06-21, ported directly to the
>         final 8-page flow below rather than the old 10 pages. Phases 2-5 (IPlatformInstaller
>         extraction, Linux impl, macOS impl + packaging) design only. Page-by-page direction
>         confirmed with the user 2026-06-21 via a structured walk-through of all 10 then-
>         current pages.
> Scope: Rewrite `OrchestratorSetup` (today a Windows-only WPF wizard) as a cross-platform
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

`OrchestratorSetup` was built before the WPF→Avalonia migration. While the application
(`OrchestratorIDE.Avalonia`) and the headless daemon (`OrchestratorIDE.Daemon`) are now
cross-platform `net10.0`, the installer is still `net10.0-windows` with `<UseWPF>true</UseWPF>`
and is deeply coupled to Windows-only facilities. It can only ever produce a Windows install,
which now caps the entire product's reach regardless of the app being portable.

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

### Phase 2 — Extract `IPlatformInstaller` + Windows impl
- Define the interface (§4.1); move `HardwareDetector`/`HiveEnroller`/`ProfileMerger`-shortcuts/
  `UninstallService` behind the Windows implementation. Pure refactor, Windows behavior
  unchanged, now structured for other OSes.

### Phase 3 — Page restructure
- Fold License into Welcome; drop Profile; convert OllamaCheck → Runtime Setup (native-first);
  wire ModelSelect to the app's internal download stack; add the Complete HIVE button. Still
  Windows-only at runtime, but the *flow* is the final flow.

### Phase 4 — Linux implementation
- `LinuxPlatformInstaller`: `/proc`+`nvidia-smi` detection, XDG paths, `ufw`/`firewalld` via
  `pkexec`, `.desktop` launchers, removal-script uninstall. First non-Windows install.

### Phase 5 — macOS implementation + packaging
- `MacPlatformInstaller`: `system_profiler` detection, `~/Library` paths, `socketfilterfw`
  firewall, `.app`/symlink launchers, delete-bundle uninstall. Per-OS publish RIDs + delivery
  wrappers (Open Questions resolved).

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
