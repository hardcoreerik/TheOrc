// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json.Nodes;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Services;

/// <summary>
/// Top-level coordinator that sequences all installation steps:
///   0. Create directories
///   1. Download OrchestratorIDE.exe     (GitHub Releases latest asset)
///   2a. [llama.cpp path]  Download runtime zip + extract it
///   2b. [Ollama install]  Download OllamaSetup.exe, run silently, wait, pull model
///   3. Download GGUF model file          (skipped on Ollama install path — pull handles it)
///   4. SHA-256 verify model             (if manifest sha256 != null)
///   5. Write settings.json + .agent.md
///   6. Create shortcuts
/// </summary>
public sealed class InstallOrchestrator : IDisposable
{
    // ── Events ────────────────────────────────────────────────────────────────

    /// <summary>Progress for the current download item.</summary>
    public event Action<DownloadProgress>? OnItemProgress;

    /// <summary>Log line for the scrolling log box.</summary>
    public event Action<string>? OnLog;

    /// <summary>
    /// Overall progress: (stepIndex 0-based, totalSteps, stepName, overallPercent 0-100).
    /// </summary>
    public event Action<int, int, string, double>? OnOverallProgress;

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly InstallerState     _state;
    private readonly InstallerViewModel _vm;
    private readonly DownloadService    _dl;
    private readonly ZipExtractService  _zip;

    private int    _totalSteps;
    private int    _stepsDone;
    private string _currentStep = "";

    public InstallOrchestrator(InstallerViewModel vm)
    {
        _vm    = vm;
        _state = vm.State;
        _dl    = new DownloadService();
        _zip   = new ZipExtractService();

        _dl.OnProgress += p =>
        {
            OnItemProgress?.Invoke(p);
            if (p.IsComplete)
                Log($"  ✓ {p.ItemName} — {p.TotalDisplay}");
        };
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    public async Task RunAsync(CancellationToken ct = default)
    {
        // Resolve app download URL from manifest before computing step count --
        // ComputeTotalSteps reads _state.AppDownloadUrl, which is empty until
        // ResolveAppUrl populates it. The original order ran ComputeTotalSteps
        // first, so its AppDownloadUrl check was always evaluated against ""
        // and never actually counted the download branch's step (Grok CLI MINOR,
        // 2026-06-21 -- a pre-existing ordering bug, not introduced by this diff,
        // but directly affecting the same step-count accuracy this diff touches).
        ResolveAppUrl();

        // Compute step count so overall progress is accurate
        _totalSteps = ComputeTotalSteps();
        _stepsDone  = 0;

        try
        {
            // ── Step 0: Create directories ──────────────────────────────────
            await Step("Creating directories", () => Task.Run(() =>
            {
                Directory.CreateDirectory(_state.AppInstallPath);
                if (!_state.UseExistingOllama)
                {
                    Directory.CreateDirectory(_state.ModelStoragePath);
                    if (!_state.InstallOllama)
                        Directory.CreateDirectory(_state.LlamaRuntimeExtractPath);
                }
                Log($"  App path  : {_state.AppInstallPath}");
                if (!_state.UseExistingOllama)
                    Log($"  Model path: {_state.ModelStoragePath}");
            }, ct), ct);

            // ── Step 1: Download OrchestratorIDE.exe ───────────────────────
            // Skip ONLY if the exe was placed next to OrchestratorSetup.exe itself
            // (portable-zip layout: the user extracted both files together). A
            // stale exe already sitting at the final AppInstallPath from a PRIOR
            // install is not a reason to skip — upgrades must always fetch the
            // current release, or the desktop shortcut keeps launching the old build.
            if (File.Exists(_state.PortableAppExePath))
            {
                await Step("Copying app from portable layout", async () =>
                {
                    var appBinaryName = Path.GetFileName(_state.AppExePath);
                    Log($"✓ Found {appBinaryName} next to the installer (portable layout) — using it instead of downloading.");
                    // The target is locked if a previous install's app binary is still running
                    // from this exact path (e.g. upgrading without closing it first) -- an
                    // unguarded File.Copy would throw a raw IOException and abort the whole
                    // install ungracefully (Grok CLI BLOCKER, 2026-06-21).
                    try
                    {
                        File.Copy(_state.PortableAppExePath, _state.AppExePath, overwrite: true);
                        EnsureExecutable(_state.AppExePath);
                    }
                    // UnauthorizedAccessException does NOT derive from IOException in .NET --
                    // a permissions-denied case (e.g. install dir needs elevation) hit the same
                    // "still running" message before, but would have bypassed this catch
                    // entirely and surfaced as a raw, unfriendly exception (Grok CLI MINOR,
                    // 2026-06-21). FileNotFoundException DOES derive from IOException, so a
                    // source-file-vanished race (after the outer Exists check, before Copy)
                    // also lands here -- the message below covers that too rather than
                    // claiming one specific cause (Grok CLI MINOR, 2026-06-21).
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException(
                            $"Could not copy {appBinaryName} to {_state.AppExePath}. It may " +
                            "still be running, the installer may need to run as administrator, " +
                            "or the source file became unavailable. Please close OrchestratorIDE " +
                            "and run the installer again.", ex);
                    }
                    // The download branch below gets a free "✓ {ItemName} — {TotalDisplay}" log
                    // line and DownloadPage row update via DownloadFileAsync's own OnProgress
                    // events; this branch calls neither, so without this the UI/log never marks
                    // "OrchestratorIDE" complete even though the copy succeeded (Grok CLI MINOR,
                    // 2026-06-21).
                    var copiedBytes = new FileInfo(_state.AppExePath).Length;
                    OnItemProgress?.Invoke(new DownloadProgress
                    {
                        ItemName      = "OrchestratorIDE",
                        BytesReceived = copiedBytes,
                        TotalBytes    = copiedBytes,
                        IsComplete    = true,
                    });
                    Log($"  ✓ OrchestratorIDE — copied from portable layout");
                    await Task.CompletedTask;
                }, ct);
            }
            else if (!string.IsNullOrEmpty(_state.AppDownloadUrl))
            {
                await Step("Downloading OrchestratorIDE", async () =>
                {
                    var appBinaryName = Path.GetFileName(_state.AppExePath);
                    Log($"  URL : {_state.AppDownloadUrl}");
                    Log($"  Dest: {_state.AppExePath}");

                    // Download to a side-by-side temp path and only swap it into AppExePath
                    // after a full, successful download -- DownloadFileAsync treats any
                    // existing file at destPath as "already done" when no hash is given, so
                    // forcing a fresh download means removing the stale exe first, but doing
                    // that directly against AppExePath left the install with NO executable at
                    // all if the download then failed (network error, 404, cancellation) --
                    // strictly worse than the stale-exe bug this was fixing (Grok CLI BLOCKER,
                    // 2026-06-21).
                    var tempPath = _state.AppExePath + ".download";

                    // If tempPath already exists, a PRIOR run already completed this download
                    // and only the subsequent Move failed (e.g. target locked by a still-running
                    // OrchestratorIDE.exe) -- skip re-downloading the whole multi-hundred-MB exe
                    // and go straight to retrying the move. Unconditionally deleting tempPath
                    // here, as an earlier version of this fix did, directly contradicted this
                    // same comment's intent and was flagged twice by review (Grok CLI MINOR,
                    // 2026-06-21) -- this is the actual fix, not another guard around the
                    // contradiction.
                    if (File.Exists(tempPath))
                    {
                        // DownloadFileAsync's own OnProgress firing (including the final
                        // IsComplete event and "✓ {ItemName}" log line) only happens when it's
                        // actually called -- skipping it here means the UI/log would never mark
                        // "OrchestratorIDE" complete on this retry path without firing the same
                        // completion manually, matching the portable-copy branch above
                        // (Grok CLI MINOR, 2026-06-21).
                        Log($"  Found a previously downloaded {appBinaryName} — retrying instead of re-downloading.");
                        var existingBytes = new FileInfo(tempPath).Length;
                        OnItemProgress?.Invoke(new DownloadProgress
                        {
                            ItemName      = "OrchestratorIDE",
                            BytesReceived = existingBytes,
                            TotalBytes    = existingBytes,
                            IsComplete    = true,
                        });
                        Log($"  ✓ OrchestratorIDE — already downloaded");
                    }
                    else
                    {
                        await _dl.DownloadFileAsync(
                            _state.AppDownloadUrl,
                            tempPath,
                            "OrchestratorIDE",
                            _state.AppSizeBytes > 0 ? _state.AppSizeBytes : null,
                            null,
                            ct);
                    }

                    try
                    {
                        File.Move(tempPath, _state.AppExePath, overwrite: true);
                        EnsureExecutable(_state.AppExePath);
                    }
                    // UnauthorizedAccessException does NOT derive from IOException in .NET --
                    // broadened to match the portable-copy branch above. FileNotFoundException
                    // DOES derive from IOException -- if tempPath vanished between the Exists
                    // check and here, the message below covers that too rather than naming one
                    // specific cause (Grok CLI MINOR, 2026-06-21).
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        throw new InvalidOperationException(
                            $"Downloaded {appBinaryName} but could not move it into place at " +
                            $"{_state.AppExePath}. It may still be running, the installer may need " +
                            "to run as administrator, or the downloaded file became unavailable. " +
                            "Please close OrchestratorIDE and run the installer again.", ex);
                    }

                    // Only reached on success -- the temp file has already been moved into
                    // place, so there's nothing left at tempPath to clean up here. A failed
                    // download (network error, 404) never reaches this point either, since
                    // DownloadFileAsync itself doesn't leave a partial file at tempPath on
                    // failure (it writes to an even more nested ".partial" path internally and
                    // only produces tempPath on full success).
                }, ct);
            }
            else
            {
                Log("⚠ App download URL not found in manifest — exe must be placed manually.");
            }

            // ── Backend-specific steps ─────────────────────────────────────

            if (_state.InstallOllama)
            {
                // ── Path B: Install Ollama + pull model ─────────────────────
                await Step("Installing Ollama", async () =>
                {
                    using var ollInst = new OllamaInstaller();
                    ollInst.OnLog      += msg => Log(msg);
                    ollInst.OnProgress += p   => OnItemProgress?.Invoke(p);
                    await ollInst.InstallAsync(_state.SelectedOllamaModel, ct);
                }, ct);
            }
            else if (!_state.UseExistingOllama)
            {
                // ── Path A: llama.cpp runtime + GGUF model ──────────────────

                // Step 2: Resolve runtime URL (prefer live GitHub API, fall back to manifest)
                var runtimeUrl  = await ResolveRuntimeUrlAsync(ct);
                // Windows assets are .zip; Linux/macOS are .tar.gz (LlamaCppResolver/
                // model-manifest.json, MULTI_OS_RELEASE_SPEC.md Phase D) -- this filename
                // must match what's about to be downloaded, or extraction picks the wrong
                // codec for what's actually on disk (grok review BLOCKER, 2026-06-21: this
                // was hardcoded ".zip" regardless of OS).
                var runtimeArchiveExt = OperatingSystem.IsWindows() ? ".zip" : ".tar.gz";
                var runtimeZip  = Path.Combine(_state.AppInstallPath, $"llama-runtime{runtimeArchiveExt}");
                var runtimeSize = GetRuntimeSizeBytes();
                _state.RuntimeDownloadUrl = runtimeUrl;

                // No SHA-256 is known for the runtime zip, so DownloadFileAsync's
                // "already done" check can't verify it — a stale zip left behind by
                // an interrupted prior install would otherwise be treated as current.
                // Unlike the app exe above, this isn't a running executable, so a delete
                // failure here is unlikely (no realistic "still running" lock) but not
                // impossible (permissions, AV scan holding a handle). This must actually
                // throw rather than log-and-continue: DownloadFileAsync's own "already done"
                // check (no SHA known for this file) would otherwise silently reuse the stale
                // zip for extraction instead of fetching the current release -- the exact
                // freshness guarantee this delete exists to enforce. Logging a warning and
                // proceeding anyway (an earlier version of this fix did exactly that) left the
                // behavior fail-open with just a warning attached, not actually fixed
                // (Grok CLI MINOR, 2026-06-21).
                try { if (File.Exists(runtimeZip)) File.Delete(runtimeZip); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    throw new InvalidOperationException(
                        $"Could not remove the previous llama.cpp runtime archive at {runtimeZip} " +
                        "to fetch the current release fresh. Please close any program that might " +
                        "have it open and run the installer again.", ex);
                }

                await Step("Downloading llama.cpp runtime", async () =>
                {
                    Log($"  URL: {runtimeUrl}");
                    await _dl.DownloadFileAsync(
                        runtimeUrl, runtimeZip,
                        "llama.cpp runtime",
                        runtimeSize > 0 ? runtimeSize : null,
                        null, ct);
                }, ct);

                // Step 3: Extract runtime
                await Step("Extracting llama.cpp runtime", async () =>
                {
                    Log($"  Extracting to {_state.LlamaRuntimeExtractPath}");
                    int lastPct = 0;
                    _zip.OnEntryExtracted += (cur, total, name) =>
                    {
                        int pct = (int)((double)cur / total * 100);
                        if (pct / 10 != lastPct / 10)
                        {
                            lastPct = pct;
                            Log($"  Extracting [{pct,3}%] {name}");
                        }
                        OnOverallProgress?.Invoke(_stepsDone, _totalSteps, "Extracting runtime",
                            OverallPercent(pct / 100.0));
                    };

                    if (OperatingSystem.IsWindows())
                        await _zip.ExtractAsync(runtimeZip, _state.LlamaRuntimeExtractPath, ct);
                    else
                        await _zip.ExtractTarGzAsync(runtimeZip, _state.LlamaRuntimeExtractPath, ct);

                    var serverExe = ZipExtractService.FindServerExe(_state.LlamaRuntimeExtractPath);
                    if (serverExe is null)
                        Log("  ⚠ llama-server binary not found in extracted archive.");
                    else
                    {
                        // Belt-and-suspenders: System.Formats.Tar already preserves each
                        // entry's Unix file mode (including +x) when extracting on a Unix-like
                        // OS, but this is the first real install path Mac hardware has ever
                        // exercised end-to-end -- explicitly re-asserting the executable bit
                        // here costs nothing and removes "did the upstream tar's mode bits
                        // survive" as a variable if llama-server still won't run.
                        EnsureExecutable(serverExe);
                        Log($"  ✓ Found: {serverExe}");
                    }

                    try { File.Delete(runtimeZip); } catch { }
                }, ct);

                // Step 4+: Download all selected GGUF models
                // Use SelectedModels (multi-select path) or fall back to legacy single model.
                var modelsToDownload = _state.SelectedModels.Any()
                    ? _state.SelectedModels
                    : _vm.AllModels
                          .Where(m => m.Id == _state.SelectedModelId)
                          .ToList();

                if (modelsToDownload.Count == 0)
                {
                    Log($"⚠ No model selected — skipping model download.");
                }

                foreach (var modelEntry in modelsToDownload)
                {
                    if (string.IsNullOrEmpty(modelEntry.Url))
                    {
                        Log($"⚠ Model '{modelEntry.Name}' has no direct download URL — " +
                            $"use Ollama to pull '{modelEntry.OllamaName ?? modelEntry.Name}'.");
                        OnItemProgress?.Invoke(new DownloadProgress
                        {
                            ItemName = modelEntry.Name,
                            Error    = "No direct URL — use Ollama to pull this model",
                        });
                        continue;
                    }

                    var destPath = _state.GetModelFilePath(modelEntry);
                    var role     = _state.ModelRoles.TryGetValue(modelEntry.Id, out var r) ? $" ({r})" : "";

                    // Model downloads are non-fatal: a 401 (gated model), 404, or
                    // transient network error should skip this model with a warning
                    // rather than aborting the entire installation.
                    try
                    {
                        await Step($"Downloading {modelEntry.Name}{role}", async () =>
                        {
                            Log($"  URL : {modelEntry.Url}");
                            Log($"  Dest: {destPath}");
                            Log($"  Size: {modelEntry.SizeDisplay}");

                            await _dl.DownloadFileAsync(
                                modelEntry.Url,
                                destPath,
                                modelEntry.Name,
                                modelEntry.SizeBytes > 0 ? modelEntry.SizeBytes : null,
                                modelEntry.Sha256,
                                ct);
                        }, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw; // cancellation always propagates
                    }
                    catch (Exception modelEx)
                    {
                        // 401 = gated model (requires HuggingFace login / license accept)
                        bool isAuth = modelEx.Message.Contains("401") ||
                                      modelEx.Message.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase);
                        string hint = isAuth
                            ? $"This model requires a HuggingFace account and license acceptance. " +
                              $"Use Ollama to pull '{modelEntry.OllamaName ?? modelEntry.Name}' instead."
                            : modelEx.Message;

                        Log($"\n⚠ Skipping '{modelEntry.Name}': {hint}");
                        OnItemProgress?.Invoke(new DownloadProgress
                        {
                            ItemName = modelEntry.Name,
                            Error    = hint,
                        });
                    }
                }
            }
            else
            {
                // ── Path C: Existing Ollama — nothing extra to download ─────
                Log("Using existing Ollama service — skipping runtime and model download.");
            }

            // ── HIVE MIND enrollment (spec §8) ──────────────────────────────
            // INSTALLER_REVAMP_SPEC.md §7 Phase 2 -- goes through IPlatformInstaller now;
            // ConfigureFirewallAsync already wraps the call on its own (no need for this
            // step to do its own Task.Run on top of it).
            if (_state.JoinHiveMind)
            {
                await Step("Joining HIVE MIND",
                    () => PlatformInstaller.Current.ConfigureFirewallAsync(_state.AppExePath, Log, ct), ct);
            }
            else
            {
                Log("HIVE MIND enrollment skipped (unchecked).");
            }

            // ── Step N-1: Write settings.json + .agent.md ──────────────────
            await Step("Writing configuration", () => Task.Run(() =>
            {
                Log("  Writing settings.json…");
                ProfileMerger.WriteAppSettings(_state);

                Log("  Writing .agent.md…");
                ProfileMerger.WriteAgentMd(_state);
            }, ct), ct);

            // ── Step N: Create shortcuts ────────────────────────────────────
            await Step("Creating shortcuts", () => Task.Run(() =>
            {
                PlatformInstaller.Current.CreateLaunchers(_state);
                Log("  Desktop/Start Menu shortcuts created (if exe present).");
            }, ct), ct);

            // ── Step N+1: Register uninstaller ──────────────────────────────
            await Step("Registering uninstaller", () => Task.Run(() =>
            {
                PlatformInstaller.Current.RegisterUninstall(_state);
                Log("  Uninstall entry registered in Apps & Features.");
            }, ct), ct);

            // Done
            _state.InstallationComplete = true;
            FireOverall(1.0);
            Log("\n✅ Installation complete.");
        }
        catch (OperationCanceledException)
        {
            Log("\n⛔ Installation cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _state.InstallationError = ex.Message;
            Log($"\n❌ Installation failed: {ex.Message}");
            throw;
        }
    }

    // ── Step helpers ──────────────────────────────────────────────────────────

    private async Task Step(string name, Func<Task> action, CancellationToken ct)
    {
        _currentStep = name;
        Log($"\n▶ {name}");
        FireOverall(0);
        await action();
        _stepsDone++;
        FireOverall(1.0);
    }

    private int ComputeTotalSteps()
    {
        // Base: create dirs + write config + shortcuts + uninstall registration
        int n = 4;

        // App exe step — exactly one Step runs in either the portable-copy or the
        // download branch above (mutually exclusive); only the third, no-op "neither
        // available" case skips a step entirely. Previously this only counted the
        // download branch, so _stepsDone overran _totalSteps under portable layout
        // once that branch was wrapped in its own Step too (Grok CLI MINOR, 2026-06-21).
        if (File.Exists(_state.PortableAppExePath) || !string.IsNullOrEmpty(_state.AppDownloadUrl))
            n += 1;

        if (_state.InstallOllama)
        {
            n += 1;  // Install Ollama (includes model pull)
        }
        else if (!_state.UseExistingOllama)
        {
            n += 2;  // download runtime + extract

            // One step per selected model with a download URL; minimum 1 for the primary
            var modelCount = _state.SelectedModels.Any()
                ? _state.SelectedModels.Count(m => !string.IsNullOrEmpty(m.Url))
                : 1;
            n += Math.Max(1, modelCount);
        }
        // ExistingOllama path: no extra steps

        return n;
    }

    /// <summary>
    /// Reads the OS-keyed "app" section from the bundled manifest and populates
    /// <see cref="InstallerState.AppDownloadUrl"/> and <see cref="InstallerState.AppSizeBytes"/>.
    /// Same OperatingSystem.IsWindows()/IsMacOS()/IsLinux() pattern PlatformInstaller.Resolve()
    /// already uses (docs/MULTI_OS_RELEASE_SPEC.md Phase B). No "linux" key exists in the
    /// manifest yet -- release.yml has no Linux publish job (Phase A) -- so this intentionally
    /// leaves AppDownloadUrl empty there rather than guessing at a URL with nothing behind it.
    /// </summary>
    private void ResolveAppUrl()
    {
        try
        {
            var json = ReadManifest();
            if (json is null) return;

            var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("app", out var app)) return;

            var osKey = OperatingSystem.IsWindows() ? "windows"
                      : OperatingSystem.IsMacOS()   ? "macos"
                      : null; // Linux: no manifest entry yet, see doc comment above.
            if (osKey is null || !app.TryGetProperty(osKey, out var osApp)) return;

            if (osApp.TryGetProperty("download_url", out var urlProp))
                _state.AppDownloadUrl = urlProp.GetString() ?? "";

            if (osApp.TryGetProperty("size_mb", out var sizeProp))
                _state.AppSizeBytes = (long)sizeProp.GetInt32() * 1_048_576;
        }
        catch { /* non-fatal */ }
    }

    // ── llama.cpp URL resolution ──────────────────────────────────────────────

    /// <summary>
    /// Resolves the runtime URL for the selected variant.
    /// Tries the GitHub API first (always current) and falls back to the manifest.
    /// </summary>
    private async Task<string> ResolveRuntimeUrlAsync(CancellationToken ct)
    {
        var variant = _state.SelectedRuntimeVariant;

        Log($"  Resolving llama.cpp URL for variant '{variant}' via GitHub API…");
        var apiUrl = await LlamaCppResolver.TryResolveLatestAsync(variant, ct);

        if (!string.IsNullOrEmpty(apiUrl))
        {
            Log($"  ✓ Resolved: {apiUrl}");
            return apiUrl;
        }

        Log("  ⚠ GitHub API unavailable — using manifest fallback URL.");
        return BuildRuntimeFallbackUrl();
    }

    private string BuildRuntimeFallbackUrl()
    {
        try
        {
            var json = ReadManifest();
            if (json is null) return DefaultFallbackRuntimeUrl();

            var root        = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var releaseBase = root.GetProperty("runtimes").GetProperty("llama_cpp")
                                  .GetProperty("release_base").GetString() ?? "";
            var variant     = _state.SelectedRuntimeVariant;
            var filename    = root.GetProperty("runtimes").GetProperty("llama_cpp")
                                  .GetProperty("variants").GetProperty(variant).GetString() ?? "";
            return $"{releaseBase}/{filename}";
        }
        catch { return DefaultFallbackRuntimeUrl(); }
    }

    private long GetRuntimeSizeBytes()
    {
        try
        {
            var json = ReadManifest();
            if (json is null) return 0;

            var root    = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var variant = _state.SelectedRuntimeVariant;
            var sizeMb  = root.GetProperty("runtimes").GetProperty("llama_cpp")
                               .GetProperty("size_mb").GetProperty(variant).GetInt32();
            return (long)sizeMb * 1_048_576;
        }
        catch { return 0; }
    }

    private static string? ReadManifest()
        => EmbeddedResources.ReadManifestJson();

    private static string DefaultFallbackRuntimeUrl()
        => "https://github.com/ggml-org/llama.cpp/releases/latest/download/" +
           "cudart-llama-bin-win-cuda-12.4-x64.zip";

    /// <summary>
    /// Sets the Unix executable bit on a freshly placed binary. A no-op on Windows -- raw
    /// HTTP downloads and File.Copy/File.Move carry no file-mode metadata at all (unlike
    /// System.Formats.Tar extraction, which preserves it from the archive's own entries), so
    /// without this, the downloaded app binary and the portable-layout copy would both be
    /// non-executable on Linux/macOS, even though the install otherwise "succeeded" (grok
    /// review BLOCKER, 2026-06-21 -- found by reading the actual download/copy code path, not
    /// assumed from the OS-aware path changes alone).
    /// </summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* best-effort -- a failure here surfaces later as "permission denied" when
                   the user tries to launch, which is at least diagnosable, rather than this
                   non-critical step aborting the whole install */ }
    }

    // ── Progress helpers ──────────────────────────────────────────────────────

    private double OverallPercent(double stepFraction = 0)
        => (_stepsDone + stepFraction) / _totalSteps * 100.0;

    private void FireOverall(double stepFraction)
        => OnOverallProgress?.Invoke(_stepsDone, _totalSteps,
                                     _currentStep, OverallPercent(stepFraction));

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _dl.Dispose();
}
