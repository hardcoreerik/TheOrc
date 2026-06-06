using System.Text.Json.Nodes;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Services;

/// <summary>
/// Top-level coordinator that sequences all installation steps:
///   1. Create directories
///   2. Download llama.cpp runtime zip   (if UseExistingOllama == false)
///   3. Extract runtime zip
///   4. Download GGUF model file
///   5. SHA-256 verify model              (if manifest sha256 != null)
///   6. Write settings.json + .agent.md
///   7. Create shortcuts                  (Phase G stub)
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

    private readonly InstallerState    _state;
    private readonly InstallerViewModel _vm;
    private readonly DownloadService   _dl;
    private readonly ZipExtractService _zip;

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

        try
        {
            // ── Step 0: Create directories ──────────────────────────────────
            await Step("Creating directories", () => Task.Run(() =>
            {
                Directory.CreateDirectory(_state.AppInstallPath);
                Directory.CreateDirectory(_state.ModelStoragePath);
                if (!_state.UseExistingOllama)
                    Directory.CreateDirectory(_state.LlamaRuntimeExtractPath);
                Log($"  App path  : {_state.AppInstallPath}");
                Log($"  Model path: {_state.ModelStoragePath}");
            }, ct), ct);

            // ── Step 1: Download runtime (optional) ─────────────────────────
            if (!_state.UseExistingOllama)
            {
                var runtimeUrl  = BuildRuntimeUrl();
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

                // ── Step 2: Extract runtime ─────────────────────────────────
                await Step("Extracting llama.cpp runtime", async () =>
                {
                    Log($"  Extracting to {_state.LlamaRuntimeExtractPath}");
                    int lastPct = 0;
                    _zip.OnEntryExtracted += (cur, total, name) =>
                    {
                        int pct = (int)((double)cur / total * 100);
                        if (pct / 10 != lastPct / 10) // log every 10%
                        {
                            lastPct = pct;
                            Log($"  Extracting [{pct,3}%] {name}");
                        }
                        OnOverallProgress?.Invoke(_stepsDone, _totalSteps, "Extracting runtime",
                            OverallPercent(pct / 100.0));
                    };

                    await _zip.ExtractAsync(runtimeZip, _state.LlamaRuntimeExtractPath, ct);

                    // Confirm server exe exists
                    var serverExe = ZipExtractService.FindServerExe(_state.LlamaRuntimeExtractPath);
                    if (serverExe is null)
                        Log("  ⚠ llama-server.exe not found in extracted archive.");
                    else
                        Log($"  ✓ Found: {serverExe}");

                    // Clean up zip to save disk space
                    try { File.Delete(runtimeZip); } catch { }
                }, ct);
            }
            else
            {
                Log("Skipping llama.cpp download — using existing Ollama.");
            }

            // ── Step 3: Download model ──────────────────────────────────────
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

            // ── Step 4: Write settings.json + .agent.md ─────────────────────
            await Step("Writing configuration", () => Task.Run(() =>
            {
                Log("  Writing settings.json…");
                ProfileMerger.WriteAppSettings(_state);

                Log("  Writing .agent.md…");
                ProfileMerger.WriteAgentMd(_state);
            }, ct), ct);

            // ── Step 5: Create shortcuts ────────────────────────────────────
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
        int n = 3; // directories + write config + shortcuts
        if (!_state.UseExistingOllama) n += 2; // download runtime + extract
        n += 1; // download model
        return n;
    }

    private double OverallPercent(double stepFraction = 0)
        => (_stepsDone + stepFraction) / _totalSteps * 100.0;

    private void FireOverall(double stepFraction)
        => OnOverallProgress?.Invoke(_stepsDone, _totalSteps,
                                     _currentStep, OverallPercent(stepFraction));

    private void Log(string msg) => OnLog?.Invoke(msg);

    // ── Manifest helpers ──────────────────────────────────────────────────────

    private string BuildRuntimeUrl()
    {
        // Read the bundled manifest to get release_base and variant filename
        try
        {
            var json = ReadManifest();
            if (json is null) return FallbackRuntimeUrl();

            var root        = System.Text.Json.JsonDocument.Parse(json).RootElement;
            var releaseBase = root.GetProperty("runtimes").GetProperty("llama_cpp")
                                  .GetProperty("release_base").GetString() ?? "";
            var variant     = _state.SelectedRuntimeVariant;
            var filename    = root.GetProperty("runtimes").GetProperty("llama_cpp")
                                  .GetProperty("variants").GetProperty(variant).GetString() ?? "";
            return $"{releaseBase}/{filename}";
        }
        catch { return FallbackRuntimeUrl(); }
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
    {
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "model-manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "model-manifest.json"),
        })
        {
            if (File.Exists(p)) return File.ReadAllText(p);
        }
        return null;
    }

    private static string FallbackRuntimeUrl()
        => "https://github.com/ggml-org/llama.cpp/releases/download/b5200/" +
           "llama-b5200-bin-win-avx2-x64.zip";

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _dl.Dispose();
}
