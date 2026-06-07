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
        // Compute step count so overall progress is accurate
        _totalSteps = ComputeTotalSteps();
        _stepsDone  = 0;

        // Resolve app download URL from manifest before starting
        ResolveAppUrl();

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
            // Skip if the exe was already placed next to OrchestratorSetup.exe
            // (portable-zip layout: the user extracted both files together).
            if (File.Exists(_state.AppExePath))
            {
                Log($"✓ OrchestratorIDE.exe already present at {_state.AppExePath} — skipping download.");
            }
            else if (!string.IsNullOrEmpty(_state.AppDownloadUrl))
            {
                await Step("Downloading OrchestratorIDE", async () =>
                {
                    Log($"  URL : {_state.AppDownloadUrl}");
                    Log($"  Dest: {_state.AppExePath}");

                    await _dl.DownloadFileAsync(
                        _state.AppDownloadUrl,
                        _state.AppExePath,
                        "OrchestratorIDE",
                        _state.AppSizeBytes > 0 ? _state.AppSizeBytes : null,
                        null,
                        ct);
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
                var runtimeZip  = Path.Combine(_state.AppInstallPath, "llama-runtime.zip");
                var runtimeSize = GetRuntimeSizeBytes();
                _state.RuntimeDownloadUrl = runtimeUrl;

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

                    await _zip.ExtractAsync(runtimeZip, _state.LlamaRuntimeExtractPath, ct);

                    var serverExe = ZipExtractService.FindServerExe(_state.LlamaRuntimeExtractPath);
                    if (serverExe is null)
                        Log("  ⚠ llama-server.exe not found in extracted archive.");
                    else
                        Log($"  ✓ Found: {serverExe}");

                    try { File.Delete(runtimeZip); } catch { }
                }, ct);

                // Step 4: Download GGUF model
                var modelEntry = _vm.AllModels.FirstOrDefault(m => m.Id == _state.SelectedModelId);
                if (modelEntry is not null)
                {
                    await Step($"Downloading model: {modelEntry.Name}", async () =>
                    {
                        Log($"  URL : {_state.SelectedModelUrl}");
                        Log($"  Dest: {_state.ModelFilePath}");
                        Log($"  Size: {modelEntry.SizeDisplay}");

                        await _dl.DownloadFileAsync(
                            _state.SelectedModelUrl,
                            _state.ModelFilePath,
                            modelEntry.Name,
                            _state.SelectedModelSizeBytes > 0 ? _state.SelectedModelSizeBytes : null,
                            modelEntry.Sha256,
                            ct);
                    }, ct);
                }
                else
                {
                    Log($"⚠ Model entry not found for id '{_state.SelectedModelId}' — skipping download.");
                }
            }
            else
            {
                // ── Path C: Existing Ollama — nothing extra to download ─────
                Log("Using existing Ollama service — skipping runtime and model download.");
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
                ProfileMerger.CreateShortcuts(_state);
                Log("  Desktop/Start Menu shortcuts created (if exe present).");
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
        // Base: create dirs + write config + shortcuts
        int n = 3;

        // App exe download (only when it will actually run)
        if (!File.Exists(_state.AppExePath) && !string.IsNullOrEmpty(_state.AppDownloadUrl))
            n += 1;

        if (_state.InstallOllama)
        {
            n += 1;  // Install Ollama (includes model pull)
        }
        else if (!_state.UseExistingOllama)
        {
            n += 2;  // download runtime + extract
            n += 1;  // download model
        }
        // ExistingOllama path: no extra steps

        return n;
    }

    /// <summary>
    /// Reads the "app" section from the bundled manifest and populates
    /// <see cref="InstallerState.AppDownloadUrl"/> and <see cref="InstallerState.AppSizeBytes"/>.
    /// </summary>
    private void ResolveAppUrl()
    {
        try
        {
            var json = ReadManifest();
            if (json is null) return;

            var root = System.Text.Json.JsonDocument.Parse(json).RootElement;
            if (!root.TryGetProperty("app", out var app)) return;

            if (app.TryGetProperty("download_url", out var urlProp))
                _state.AppDownloadUrl = urlProp.GetString() ?? "";

            if (app.TryGetProperty("size_mb", out var sizeProp))
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
