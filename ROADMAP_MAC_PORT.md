# TheOrc — Cross-Platform Roadmap (Mac / Linux)

> **Status:** Parked / Planning
> **Last updated:** 2026-06-07
> **Decision (revised):** Docker + ASP.NET Core API backend + Blazor Server UI — replaces earlier Avalonia plan.
> **Constraint:** No Mac hardware available. Docker approach removes this constraint — any OS with Docker can run TheOrc.

---

## Architecture Decision: Docker + Blazor Server

| Option | Decision | Reason |
|--------|----------|--------|
| **Docker + Blazor Server** | ✅ **Chosen** | Backend already mostly platform-agnostic; Blazor keeps C#; browser UI is free cross-platform |
| Avalonia UI migration | ❌ Superseded | Heavy XAML port; `FlowDocument` has no Avalonia equivalent; still Windows-first dev |
| WPF stays + Swift/AppKit Mac app | ❌ | Two codebases diverge forever |
| Electron / Tauri | ❌ | Full JS rewrite of UI |

**Rationale:** The agent loop, chat engine, web search tools, and Ollama client are already pure .NET with no WPF dependency. Extracting them into an ASP.NET Core API service is lower effort than porting 15+ WPF panels to Avalonia. Mac (and Linux) users run `docker compose up` and access TheOrc in a browser tab. The WPF Windows app continues shipping unchanged.

---

## Target Architecture

```
┌─────────────────────────────────┐
│  Docker Container (Linux)       │
│  ASP.NET Core API + SignalR     │
│  - AgentLoop                    │
│  - ChatEngine + tools           │
│  - OllamaClient proxy           │
│  - File system / workspace      │
│  - Streaming via WebSocket      │
└────────────┬────────────────────┘
             │ HTTP / WebSocket (localhost)
┌────────────▼────────────────────┐
│  Blazor Server UI  (browser)    │
│  - Swarm board                  │
│  - Chat panel                   │
│  - File explorer                │
│  - Settings / first-run         │
└─────────────────────────────────┘
```

Mac/Linux user experience: `docker compose up`, open `http://localhost:5000`. Done.
Windows users continue using the native WPF app — no change.

---

## What Moves Where

| Current Component | Docker Fate |
|---|---|
| `Core/AgentLoop.cs` | ✅ Moves to API — zero changes |
| `Research/ChatEngine.cs` | ✅ Moves to API — zero changes |
| `Research/WebSearchTool.cs` | ✅ Moves to API — zero changes |
| `Research/FetchPageTool.cs` | ✅ Moves to API — zero changes |
| `Core/OllamaClient.cs` | ✅ Moves to API — zero changes |
| `Core/AppSettings.cs` | ✅ Moves to API — minor path abstraction |
| `UI/Panels/*.xaml` | 🔄 Replaced by Blazor components |
| `Research/MarkdownFlowDocument.cs` | 🗑 Deleted — browser renders markdown natively |
| WMI hardware detection | 🔄 Replace with `/proc` or `system_profiler` in container |
| WScript.Shell shortcuts | 🗑 Not needed — no desktop shortcuts in a web app |

---

## Previous Avalonia Plan

The detailed Avalonia migration plan (phases, XAML migration notes, CI runners) is preserved below for reference in case the Docker approach is ever reconsidered.

---

---

## Feasibility Snapshot

| Area | Lines affected | Effort |
|------|---------------|--------|
| Core agent loop, Ollama client, LlamaServer manager | 0 — ports cleanly | — |
| XAML / UI (29 files) | ~90% Avalonia-compatible | Low |
| Hardware detection (WMI → `system_profiler`) | ~170 lines | Medium |
| Shortcut creation (WScript.Shell → `.command`) | ~85 lines | Low |
| Platform paths (`%APPDATA%` → `~/Library`) | ~20 lines | Low |
| Shell execution (`powershell.exe` → `/bin/bash`) | ~30 lines | Low |
| Installer wizard (WPF exe → Avalonia + DMG) | ~95 lines | Medium |
| **Total platform-specific code** | **~400 lines** | **~3 weeks** |
| **Portable code (no changes needed)** | **~2500 lines** | — |

---

## Phase Plan

### Phase 1 — Avalonia Migration (Windows-testable, no Mac needed)

All work here is validated on Windows. Avalonia runs natively on Windows — the migrated app is fully testable before touching any Mac-specific code.

- [ ] Add Avalonia NuGet packages to solution; confirm side-by-side build
- [ ] Migrate `OrchestratorSetup` wizard pages (9 pages — simpler, isolated)
  - XAML namespace: `xmlns="https://github.com/avaloniaui"` replaces WPF namespace
  - `pack://application` image URIs → `avares://` scheme
  - `ControlTemplate.Triggers` → Avalonia `ControlTheme` + `Trigger` syntax
  - `ComboBoxItem.Tag` → bind to `Tag` property (works the same)
- [ ] Migrate `OrchestratorIDE` main shell (MainWindow, panels, dialogs)
- [ ] Confirm dark theme (`InstallerTheme.xaml` / `App.xaml`) renders identically on Avalonia
- [ ] Run full Windows regression — all features working before touching platform code

**Exit criterion:** App builds and runs on Windows as Avalonia. All installer wizard pages display correctly.

---

### Phase 2 — Platform Abstraction Layer (Windows-testable, CI-validated on Mac)

Introduce interfaces so Windows and Mac implementations live side-by-side cleanly.

#### Interfaces to create

```csharp
// src/Platform/IHardwarePlatform.cs
public interface IHardwarePlatform
{
    Task<HardwareInfo> DetectAsync();
}

// src/Platform/IPlatformPaths.cs
public interface IPlatformPaths
{
    string AppData { get; }          // %APPDATA% vs ~/Library/Application Support
    string UserProfile { get; }
}

// src/Platform/IShortcutService.cs
public interface IShortcutService
{
    void CreateDesktopShortcut(string targetExe, string workingDir);
    void CreateStartMenuShortcut(string targetExe, string workingDir);
}

// src/Platform/IShellLauncher.cs
public interface IShellLauncher
{
    Task<string> RunAsync(string command, string args);
}
```

#### Windows implementations (extract from existing code)

- `WindowsHardwarePlatform` — current WMI + Registry code moved here
- `WindowsPlatformPaths` — `Environment.SpecialFolder.ApplicationData`
- `WindowsShortcutService` — current `WScript.Shell` COM code
- `WindowsShellLauncher` — `powershell.exe`

#### Mac implementations (new)

- `MacHardwarePlatform` — `system_profiler SPDisplaysDataType` JSON parse
  - Apple Silicon → variant: `mac_arm64` (Metal GPU)
  - Intel Mac → variant: `mac_x86_64` (CPU/AVX)
  - VRAM: parse from `system_profiler` output
- `MacPlatformPaths` — `~/Library/Application Support/OrchestratorIDE`
- `MacShortcutService` — write a `.command` script to Desktop / Applications
- `MacShellLauncher` — `/bin/bash`

#### Registration (startup)

```csharp
if (OperatingSystem.IsWindows())
    services.AddSingleton<IHardwarePlatform, WindowsHardwarePlatform>();
else if (OperatingSystem.IsMacOS())
    services.AddSingleton<IHardwarePlatform, MacHardwarePlatform>();
```

**Exit criterion:** CI macOS runner builds cleanly; smoke tests pass (app starts, reads config, finds Ollama).

---

### Phase 3 — llama.cpp Mac Variants

The GitHub API resolver (`LlamaCppResolver.cs`) already handles dynamic URL resolution. Add Mac patterns.

#### Manifest additions needed

```json
"runtimes": {
  "llama_cpp": {
    "variants": {
      "cuda12":    "cudart-llama-bin-win-cuda-12.4-x64.zip",
      "cuda11":    "cudart-llama-bin-win-cuda-11.7-x64.zip",
      "vulkan":    "llama-bin-win-vulkan-x64.zip",
      "avx2":      "llama-bin-win-avx2-x64.zip",
      "cpu":       "llama-bin-win-x64.zip",
      "mac_arm64": "llama-bin-macos-arm64.zip",
      "mac_x86":   "llama-bin-macos-x86_64.zip"
    }
  }
}
```

#### `LlamaCppResolver.cs` pattern additions

```csharp
["mac_arm64"] = ["macos", "arm64", ".zip"],
["mac_x86"]   = ["macos", "x86_64", ".zip"],
```

#### Apple Silicon Metal note

The `mac_arm64` llama.cpp build uses Metal GPU acceleration automatically when available. No extra CUDA-like driver install needed — Metal is part of macOS. VRAM detection uses `system_profiler` unified memory reporting.

**Exit criterion:** Installer correctly downloads and extracts the right llama.cpp binary on CI macOS runner.

---

### Phase 4 — Mac Installer (DMG)

Replace `OrchestratorSetup.exe` with a macOS-native install experience.

#### Approach: Avalonia wizard + DMG output

- The same 9-page wizard UI (Avalonia) runs as a macOS `.app` bundle
- GitHub Actions builds the `.app` and packages it into a `.dmg` via `create-dmg` or `hdiutil`
- User drags `.app` to `/Applications`
- First launch detects no runtime → offers to run setup wizard (same logic as Windows portable zip)

#### CI DMG build step

```yaml
- name: Build macOS DMG
  runs-on: macos-latest
  steps:
    - run: dotnet publish OrchestratorSetup/OrchestratorSetup.csproj
            -r osx-arm64 --self-contained -p:PublishSingleFile=true
            -o publish/mac-arm64
    - run: create-dmg TheOrc.dmg publish/mac-arm64/TheOrc.app
```

**Exit criterion:** `.dmg` downloads, mounts, and the wizard completes successfully on a real Mac.

---

### Phase 5 — Hardware Validation (requires Mac access)

- [ ] Metal GPU acceleration confirmed working on Apple Silicon
- [ ] `system_profiler` GPU detection returns correct VRAM
- [ ] App feels native (window chrome, fonts, scrolling momentum)
- [ ] Ollama install/pull flow works end-to-end
- [ ] Shortcuts created correctly in Applications folder and Dock

**Options for Mac access without buying one:**
- MacStadium cloud Mac — $1 trial month
- MacinCloud — pay-per-hour
- Friend/family Mac for a day
- Apple Silicon Mac Mini M4 — ~$600 (eventual purchase if shipping Mac builds)

---

## Testing Strategy (No Mac Required)

### On Windows (during Phase 1 & 2)

The Avalonia app runs on Windows. Test everything there.

```
dotnet run --project OrchestratorIDE -- --platform mock-mac
```

Add a `--platform` flag that forces the Mac platform implementations even on Windows. This lets you exercise `MacHardwarePlatform`, `MacPlatformPaths`, etc. with a mock `system_profiler` JSON response.

### On GitHub Actions (during Phase 2 & 3)

Every push triggers a macOS build. Capture the UI as an artifact:

```yaml
- name: Screenshot smoke test
  run: |
    open -a OrchestratorIDE.app &
    sleep 3
    screencapture -x /tmp/orc-mac-screenshot.png
- uses: actions/upload-artifact@v4
  with:
    name: mac-ui-screenshot
    path: /tmp/orc-mac-screenshot.png
```

Download the artifact from the Actions run to see how it renders on macOS without owning a Mac.

---

## Best Practices for the Migration

### 1. Never touch the Windows build during Mac work

All Mac platform code lives in `OrchestratorSetup/Platform/Mac/` and `OrchestratorIDE/Platform/Mac/`. The Windows path is `Platform/Windows/`. `#if` directives are forbidden — use DI registration instead. The Windows build must stay green on every commit.

### 2. XAML migration order: setup wizard first, main app second

The installer wizard (9 pages, self-contained) is simpler than the main IDE. Migrate it first, ship it, then use the confidence gained to tackle the full IDE.

### 3. One XAML file at a time, run after each

Don't batch-migrate all XAML. Port one file, build, run visually, check it looks right, commit. Avalonia's XAML parser errors are clear but migration bugs are subtle (a misnamed style key shows nothing, not an error).

### 4. Keep WPF projects until Avalonia is fully validated

Run both WPF and Avalonia builds in CI simultaneously during the migration. Only delete the WPF projects after Phase 1 is complete and signed off. The solution file can reference both.

### 5. `avares://` for images, not `pack://`

```xml
<!-- WPF -->
<Image Source="pack://application:,,,/Assets/icon.png"/>

<!-- Avalonia -->
<Image Source="avares://OrchestratorIDE/Assets/icon.png"/>
```

Mark images as `AvaloniaResource` in the .csproj, not `Resource`.

### 6. Hardware detection must never crash

On Mac, `system_profiler` can be slow (2–3 seconds) or return unexpected formats. Always wrap in try/catch, return a safe default (`HardwareInfo { GpuName = "Unknown", VramGb = 8 }`), and let the user override via the dropdown. Same pattern as the existing Windows implementation.

### 7. `OperatingSystem.IsXxx()` not `#if`

```csharp
// Good
if (OperatingSystem.IsMacOS())
    return new MacHardwarePlatform();

// Bad
#if __MACOS__
    return new MacHardwarePlatform();
#endif
```

Runtime checks keep a single binary that works everywhere. Compile-time `#if` requires separate build configurations and makes CI harder.

### 8. Settings path migration

When a Windows user's machine also gets a Mac (or vice versa), settings shouldn't collide. Use the platform path but keep the same JSON format so `settings.json` is portable across OS if copied manually.

### 9. Ollama is the easy path on Mac — lean on it

On Mac, Ollama is the polished experience. `brew install ollama` is one command and it handles Metal GPU automatically. llama.cpp on Mac works but requires more hand-holding. The Mac installer should recommend Ollama first and offer llama.cpp as the power-user option — opposite priority from Windows.

### 10. Test with both Apple Silicon and Intel Mac in CI

```yaml
strategy:
  matrix:
    os: [macos-latest, macos-13]  # macos-latest = arm64, macos-13 = x86_64
```

`macos-latest` on GitHub Actions is Apple Silicon (M1). `macos-13` is the last Intel runner. Run both.

---

## File Map: What Changes Where

```
OrchestratorIDE/
├── Platform/
│   ├── IHardwarePlatform.cs       NEW — interface
│   ├── IPlatformPaths.cs          NEW — interface
│   ├── IShortcutService.cs        NEW — interface
│   ├── IShellLauncher.cs          NEW — interface
│   ├── Windows/
│   │   ├── WindowsHardwarePlatform.cs   MOVED from HardwareDetector.cs
│   │   ├── WindowsPlatformPaths.cs      NEW (wraps SpecialFolder)
│   │   ├── WindowsShortcutService.cs    MOVED from ProfileMerger.cs
│   │   └── WindowsShellLauncher.cs      NEW (powershell.exe)
│   └── Mac/
│       ├── MacHardwarePlatform.cs       NEW (system_profiler)
│       ├── MacPlatformPaths.cs          NEW (~Library/Application Support)
│       ├── MacShortcutService.cs        NEW (.command file)
│       └── MacShellLauncher.cs          NEW (/bin/bash)
│
OrchestratorSetup/
├── Platform/                      (same structure as above, for installer)
│
Setup/
└── model-manifest.json            ADD mac_arm64, mac_x86 variants
│
.github/workflows/
└── release.yml                    ADD osx-arm64 + osx-x86_64 publish steps
```

---

## Quick Reference: Key Differences

| Concern | Windows | Mac |
|---------|---------|-----|
| GPU detection | WMI `Win32_VideoController` | `system_profiler SPDisplaysDataType` |
| VRAM query | Registry + WMI | `system_profiler` unified memory |
| GPU acceleration | CUDA (NVIDIA) / Vulkan (AMD) | Metal (Apple Silicon) |
| App data | `%APPDATA%\OrchestratorIDE` | `~/Library/Application Support/OrchestratorIDE` |
| Shortcuts | `.lnk` via WScript.Shell | `.command` script / Dock via AppleScript |
| Shell | `powershell.exe` | `/bin/bash` or `/bin/zsh` |
| Ollama install | `OllamaSetup.exe` (silent) | `brew install ollama` or `.pkg` |
| llama.cpp variant | cuda12 / vulkan / avx2 | mac_arm64 (Metal) / mac_x86 |
| Installer format | `.exe` wizard | `.dmg` → drag to Applications |
| Runtime elevation | UAC `runas` | `sudo` / macOS Authorization Services |
| Find executable | `where.exe` | `which` |

---

*Roadmap owner: hardcoreerik — revisit when Mac hardware or CI access is available for Phase 4.*
