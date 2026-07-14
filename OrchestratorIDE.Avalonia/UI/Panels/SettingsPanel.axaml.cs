// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using CoreActivityEvent = OrchestratorIDE.Core.ActivityEvent;
using CoreActivityKind = OrchestratorIDE.Core.ActivityKind;

namespace OrchestratorIDE.UI.Panels;

public partial class SettingsPanel : UserControl
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string RepoOwner  = "hardcoreerik";
    private const string RepoName   = "TheOrc";
    private const string RepoUrl    = $"https://github.com/{RepoOwner}/{RepoName}";
    private const string IssuesApi  = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/issues?state=open&per_page=20";
    private const string CommitsApi = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/commits?per_page=10";

    private static readonly string DefaultSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "source");

    // ── Events ────────────────────────────────────────────────────────────────

    public event Action<AppSettings>? SettingsSaved;
    public event Func<Task>?          CheckUpdatesRequested;
    public event Func<Task>?          RegenerateAgentFileRequested;
    public event Action<string>?      OpenFolderAsWorkspaceRequested;
    public event Action<string>?      ScanAnalysisReady;
    public event Action<CoreActivityEvent>? ActivityRequested;
    public event Func<Task>?          StartHiveWorkerRequested;
    public event Func<Task>?          StopHiveWorkerRequested;
    /// <summary>Warchief side: open the time-boxed re-sync auto-approve window.</summary>
    public event Action?              AcceptHiveResyncRequested;
    /// <summary>Worker side: rediscover the Warchief identity and re-pair now.</summary>
    public event Func<Task>?          ResyncWorkerNowRequested;

    /// <summary>Shows a one-line status under the re-sync buttons (countdown, result, etc.).</summary>
    public void SetHiveResyncStatus(string text) => TbHiveResyncStatus.Text = text;

    /// <summary>Called by MainWindow whenever the worker's running state changes (start,
    /// stop, or a poll-loop crash) so the button row reflects reality instead of just
    /// whatever was last clicked.</summary>
    public void SetHiveWorkerRunning(bool running)
    {
        BtnStartHiveWorker.IsEnabled = !running;
        BtnStopHiveWorker.IsEnabled  = running;
        TbHiveWorkerStatus.Text      = running ? "Running" : "Stopped";
        TbHiveWorkerStatus.Foreground = running
            ? new SolidColorBrush(Color.Parse("#76B900"))
            : new SolidColorBrush(Color.Parse("#5A6A4A"));
    }

    // ── State ─────────────────────────────────────────────────────────────────

    private readonly OllamaClient _ollama;
    private AppSettings _current = new();
    private static readonly HttpClient _ghHttp = BuildGitHubClient();
    private ModelDepot? _scannedDepot;
    private readonly List<NativeBindingOption> _nativeBindingOptions = [];

    // Native Runtime telemetry — wraps the existing OllamaClient in the IModelRuntime
    // abstraction (Phase 0) so this surface costs nothing new: no model-folder config,
    // no adapter hot-swap, no SessionManager (that's scoped to ILocalModelRuntime /
    // in-process GGUF sessions, which Ollama is not — it's a thin passthrough client).
    private OllamaRuntime? _runtimeProbe;

    private sealed record NativeBindingOption(RuntimeRoleBinding Binding)
    {
        public override string ToString()
        {
            var adapterText = Binding.Adapter is null ? "base only" : Binding.Adapter.DisplayName;
            return $"{Binding.Role}: {Binding.BaseModel.DisplayName} ({adapterText})";
        }
    }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsPanel(OllamaClient ollama)
    {
        InitializeComponent();
        _ollama = ollama;
        TbInstallPath.Text = Path.GetDirectoryName(
            Assembly.GetExecutingAssembly().Location) ?? "(unknown)";
    }

    // ── Load / Read ───────────────────────────────────────────────────────────

    public void LoadSettings(AppSettings s)
    {
        _current = s;

        TbOllamaHost.Text             = s.OllamaHost;
        RbBackendOllama.IsChecked     = s.Backend == InferenceBackend.Ollama;
        RbBackendLlamaCpp.IsChecked   = s.Backend == InferenceBackend.LlamaCpp;
        PnlLlamaCppSettings.IsVisible = s.Backend == InferenceBackend.LlamaCpp;
        TbLlamaCppRuntimePath.Text    = s.LlamaCppRuntimePath;
        TbLlamaCppModelPath.Text      = s.LlamaCppModelPath;
        TbLlamaCppPort.Text           = s.LlamaCppPort.ToString();
        TbLlamaCppContextSize.Text    = s.LlamaCppContextSize.ToString();
        TbLlamaCppGpuLayers.Text      = s.LlamaCppGpuLayers.ToString();
        TbLlamaCppTestResult.Text     = "";
        TbDefaultModel.Text           = s.DefaultModel;
        TbMaxSteps.Text               = s.MaxStepsOverride.ToString();
        TglAutoVerify.IsChecked       = s.AutoVerify;
        TglAutoCheckpoint.IsChecked   = s.AutoCheckpoint;
        TglRestoreLastModel.IsChecked = s.RestoreLastModel;
        TglAutoModelSwitch.IsChecked  = s.AutoModelSwitch;
        TglCheckUpdates.IsChecked     = s.CheckForUpdates;
        TbDefaultWorkspace.Text       = s.DefaultWorkspace;
        TglHiveMindEnabled.IsChecked  = s.HiveMindEnabled;
        TglHiveLiteMode.IsChecked     = s.HiveLiteMode;
        // Matched by Tag, not index -- avoids a fragile magic-number mapping to the XAML
        // item order (grok review MINOR, 2026-06-21). Falls back to "Ask each time" (index 1)
        // for an unrecognized/missing value.
        CmbHiveAcceptControlFrom.SelectedItem =
            CmbHiveAcceptControlFrom.Items.OfType<ComboBoxItem>()
                .FirstOrDefault(i => (string?)i.Tag == s.HiveDefaultAcceptControlFrom)
            ?? CmbHiveAcceptControlFrom.Items.OfType<ComboBoxItem>().ElementAt(1);
        TglNativeHiveWorker.IsChecked = s.ExperimentalNativeHiveWorkerEnabled;
        TglAutoResync.IsChecked       = s.HiveDevAutoResyncEnabled;
        TglNativeMainChat.IsChecked   = s.ExperimentalNativeMainChatEnabled;
        TglToolcallerDatasetCapture.IsChecked = s.ToolcallerDatasetCaptureEnabled;
        TglToolcallerRepair.IsChecked         = s.ToolcallerRepairEnabled;
        RowModelStorage.PathText  = s.ModelStoragePath;
        RowModelStorage.DefaultDisplay = s.ResolvedModelStoragePath;
        RowTempFallback.PathText  = s.TempFallbackPath;
        RowTempFallback.DefaultDisplay = s.ResolvedTempFallbackPath;
        TbNativeRuntimeModelRoot.Text = s.NativeRuntimeModelRoot;
        TbNativeRuntimeContextSize.Text = s.NativeRuntimeContextSize.ToString();
        TbNativeRuntimeGpuLayers.Text = s.NativeRuntimeGpuLayers.ToString();
        TbDepotScanFolder.Text        = s.ResolvedNativeRuntimeModelRoot;
        TbStatus.Text                 = "";

        TbSourceFolder.Text = string.IsNullOrEmpty(s.SourceFolderPath)
            ? DefaultSourceFolder
            : s.SourceFolderPath;

        var current = UpdateChecker.CurrentVersion();
        var known   = s.LastKnownLatestVersion;
        TbVersionInfo.Text = string.IsNullOrEmpty(known)
            ? $"v{current} installed"
            : $"v{current} installed  •  latest: v{known}";

        RefreshParallelStatus();
        _ = RefreshRuntimeStatusAsync();
        SetComboToSlots(s.OllamaParallelSlots > 1
            ? s.OllamaParallelSlots
            : OllamaParallelHelper.DetectCurrentSlots());

        var recommended = OllamaParallelHelper.RecommendedSlots(s.DetectedVramGb);
        TbSlotHint.Text = s.DetectedVramGb > 0
            ? $"← {recommended} recommended ({s.DetectedVramGb:F0} GB VRAM)"
            : "(select based on available VRAM)";

        RefreshSourceButtons();
    }

    private void RefreshParallelStatus()
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        TbParallelStatus.Text       = OllamaParallelHelper.StatusText(slots);
        TbParallelStatus.Foreground = new SolidColorBrush(Color.Parse(OllamaParallelHelper.StatusColor(slots)));
        TbParallelExplain.Text      = OllamaParallelHelper.GetExplanation(slots);
    }

    private void RefreshSourceButtons()
    {
        var folder  = TbSourceFolder.Text?.Trim() ?? "";
        var hasRepo = Directory.Exists(Path.Combine(folder, ".git"));
        BtnGrabSource.Content                = hasRepo ? "↺  Pull Latest" : "⬇  Grab Source";
        BtnOpenSourceAsWorkspace.IsEnabled   = Directory.Exists(folder);
    }

    /// <summary>
    /// Native Runtime telemetry surface. Wraps the existing OllamaClient in OllamaRuntime
    /// and reads IModelRuntime.GetHealth()/GetStats() directly — no model loading, no
    /// adapter hot-swap, no new config. Read-only proof-of-life for the IModelRuntime
    /// abstraction landed in Phase 0; will generalize automatically once the active
    /// backend can be swapped to LlamaCppServerRuntime/LLamaSharpRuntime.
    /// </summary>
    private async Task RefreshRuntimeStatusAsync()
    {
        try
        {
            _runtimeProbe ??= new OllamaRuntime(_ollama);

            // Must call the runtime wrapper's IsReachableAsync, not the raw client's —
            // OllamaRuntime.GetHealth() reads _lastKnownReachable, which only this call updates.
            await _runtimeProbe.IsReachableAsync().ConfigureAwait(true);

            var health = _runtimeProbe.GetHealth();
            var stats  = _runtimeProbe.GetStats();

            TbRuntimeStatus.Text       = health.IsAvailable ? "Runtime reachable" : "Runtime unavailable";
            TbRuntimeStatus.Foreground = new SolidColorBrush(Color.Parse(health.IsAvailable ? "#76B900" : "#CC4444"));

            var statsLine = stats.TokensPerSecond is { } tps
                ? $" · {tps:F1} tok/s"
                : "";
            TbRuntimeExplain.Text =
                $"{health.RuntimeName} · {health.Message ?? "no detail"}{statsLine}";
        }
        catch (Exception ex)
        {
            TbRuntimeStatus.Text       = "Runtime check failed";
            TbRuntimeStatus.Foreground = new SolidColorBrush(Color.Parse("#CC4444"));
            TbRuntimeExplain.Text      = ex.Message;
        }
    }

    private async void BtnRefreshRuntimeStatus_Click(object? sender, RoutedEventArgs e) =>
        await RefreshRuntimeStatusAsync();

    // async void UI handlers must never let an exception escape onto the Avalonia UI thread
    // (worker start runs capability detection / native-runtime build, which can throw) — an
    // unhandled fault here crashes the app. Each catch surfaces the error instead of dying
    // (Codex review BLOCKER, 2026-06-30).
    private async void BtnStartHiveWorker_Click(object? sender, RoutedEventArgs e)
    {
        if (StartHiveWorkerRequested is not { } handler) return;
        try { await handler(); }
        catch (Exception ex)
        {
            ActivityRequested?.Invoke(new CoreActivityEvent(
                CoreActivityKind.Warning, "HIVE Worker", $"Start failed: {ex.Message}", DateTime.Now));
        }
    }

    private async void BtnStopHiveWorker_Click(object? sender, RoutedEventArgs e)
    {
        if (StopHiveWorkerRequested is not { } handler) return;
        try { await handler(); }
        catch (Exception ex)
        {
            ActivityRequested?.Invoke(new CoreActivityEvent(
                CoreActivityKind.Warning, "HIVE Worker", $"Stop failed: {ex.Message}", DateTime.Now));
        }
    }

    private void BtnAcceptHiveResync_Click(object? sender, RoutedEventArgs e)
        => AcceptHiveResyncRequested?.Invoke();

    private async void BtnResyncWorkerNow_Click(object? sender, RoutedEventArgs e)
    {
        if (ResyncWorkerNowRequested is not { } handler) return;
        BtnResyncWorkerNow.IsEnabled = false;
        SetHiveResyncStatus("Re-syncing…");
        try { await handler(); }
        catch (Exception ex) { SetHiveResyncStatus($"Re-sync error: {ex.Message}"); }
        finally { BtnResyncWorkerNow.IsEnabled = true; }
    }

    private void SetComboToSlots(int slots)
    {
        foreach (ComboBoxItem? item in CbParallelSlots.Items)
        {
            if (item?.Content?.ToString() == slots.ToString())
            {
                CbParallelSlots.SelectedItem = item;
                return;
            }
        }
        CbParallelSlots.SelectedIndex = 0;
    }

    private int SelectedSlots()
    {
        if (CbParallelSlots.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Content?.ToString(), out var n))
            return n;
        return 1;
    }

    private AppSettings ReadSettings()
    {
        var s = _current;
        s.OllamaHost          = TbOllamaHost.Text?.Trim().TrimEnd('/') ?? "";
        s.Backend             = RbBackendLlamaCpp.IsChecked == true
            ? InferenceBackend.LlamaCpp
            : InferenceBackend.Ollama;
        s.LlamaCppRuntimePath = TbLlamaCppRuntimePath.Text?.Trim() ?? "";
        s.LlamaCppModelPath   = TbLlamaCppModelPath.Text?.Trim() ?? "";
        s.LlamaCppPort        = int.TryParse(TbLlamaCppPort.Text, out var llamaPort) ? llamaPort : 8080;
        s.LlamaCppContextSize = int.TryParse(TbLlamaCppContextSize.Text, out var llamaCtx)
            ? Math.Max(512, llamaCtx)
            : 8192;
        s.LlamaCppGpuLayers   = int.TryParse(TbLlamaCppGpuLayers.Text, out var llamaGpu) ? llamaGpu : -1;
        s.DefaultModel        = TbDefaultModel.Text?.Trim() ?? "";
        s.MaxStepsOverride    = int.TryParse(TbMaxSteps.Text, out var n) ? Math.Max(0, n) : 0;
        s.AutoVerify          = TglAutoVerify.IsChecked       == true;
        s.AutoCheckpoint      = TglAutoCheckpoint.IsChecked   == true;
        s.RestoreLastModel    = TglRestoreLastModel.IsChecked  == true;
        s.AutoModelSwitch     = TglAutoModelSwitch.IsChecked   == true;
        s.CheckForUpdates     = TglCheckUpdates.IsChecked      == true;
        s.DefaultWorkspace    = TbDefaultWorkspace.Text?.Trim() ?? "";
        s.OllamaParallelSlots = SelectedSlots();
        s.SourceFolderPath    = TbSourceFolder.Text?.Trim() ?? "";
        s.HiveMindEnabled = TglHiveMindEnabled.IsChecked == true;
        s.HiveLiteMode    = TglHiveLiteMode.IsChecked == true;
        s.HiveDefaultAcceptControlFrom =
            (CmbHiveAcceptControlFrom.SelectedItem as ComboBoxItem)?.Tag as string ?? "Ask";
        s.ExperimentalNativeHiveWorkerEnabled = TglNativeHiveWorker.IsChecked == true;
        s.HiveDevAutoResyncEnabled            = TglAutoResync.IsChecked == true;
        s.ExperimentalNativeMainChatEnabled   = TglNativeMainChat.IsChecked == true;
        s.ToolcallerDatasetCaptureEnabled     = TglToolcallerDatasetCapture.IsChecked == true;
        s.ToolcallerRepairEnabled             = TglToolcallerRepair.IsChecked == true;
        s.ModelStoragePath  = RowModelStorage.PathText.Trim();
        s.TempFallbackPath  = RowTempFallback.PathText.Trim();
        s.NativeRuntimeModelRoot = TbNativeRuntimeModelRoot.Text?.Trim() ?? "";
        s.NativeRuntimeContextSize = int.TryParse(TbNativeRuntimeContextSize.Text, out var nativeCtx)
            ? Math.Max(512, nativeCtx)
            : 8192;
        s.NativeRuntimeGpuLayers = int.TryParse(TbNativeRuntimeGpuLayers.Text, out var nativeGpu)
            ? nativeGpu
            : -1;
        return s;
    }

    // ── Test connection ───────────────────────────────────────────────────────

    private async void BtnTestConn_Click(object? sender, RoutedEventArgs e)
    {
        BtnTestConn.IsEnabled = false;
        SetStatus("Testing…", "#CCA700");

        var host     = TbOllamaHost.Text?.Trim().TrimEnd('/') ?? "";
        var original = _ollama.Host;
        _ollama.Host = host;

        try
        {
            var models = await _ollama.GetInstalledModelsAsync();
            SetStatus(models.Count > 0
                ? $"✓  Connected — {models.Count} models found"
                : "⚠  Connected but no models returned",
                models.Count > 0 ? "#76B900" : "#CCA700");
        }
        catch (Exception ex)
        {
            _ollama.Host = original;
            SetStatus($"✗  {ex.Message}", "#F44747");
        }
        finally { BtnTestConn.IsEnabled = true; }
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    private void BtnSave_Click(object? sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();
        if (string.IsNullOrWhiteSpace(settings.OllamaHost))
        {
            SetStatus("✗  Ollama host cannot be empty", "#F44747");
            return;
        }
        if (settings.Backend == InferenceBackend.LlamaCpp &&
            (string.IsNullOrWhiteSpace(settings.LlamaCppRuntimePath) ||
             string.IsNullOrWhiteSpace(settings.LlamaCppModelPath)))
        {
            SetStatus("✗  llama.cpp backend needs both a runtime folder and a model file", "#F44747");
            return;
        }
        settings.Save();
        SetStatus("✓  Saved", "#76B900");
        SettingsSaved?.Invoke(settings);
    }

    // ── Check for updates ─────────────────────────────────────────────────────

    private async void BtnCheckNow_Click(object? sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        SetStatus("Checking for updates…", "#CCA700");
        if (CheckUpdatesRequested != null)
            await CheckUpdatesRequested.Invoke();
        BtnCheckNow.IsEnabled = true;
        SetStatus("", "#76B900");
    }

    // ── Regenerate agent file ─────────────────────────────────────────────────

    private async void BtnRegenerateAgentFile_Click(object? sender, RoutedEventArgs e)
    {
        BtnRegenerateAgentFile.IsEnabled = false;
        if (RegenerateAgentFileRequested != null)
            await RegenerateAgentFileRequested.Invoke();
        BtnRegenerateAgentFile.IsEnabled = true;
    }

    // ── Multi-agent parallel ──────────────────────────────────────────────────

    private void BtnSetPermanent_Click(object? sender, RoutedEventArgs e)
    {
        var slots = SelectedSlots();
        try
        {
            OllamaParallelHelper.SetPermanently(slots);
            SetStatus($"✓  OLLAMA_NUM_PARALLEL={slots} written. Restart Ollama to apply.", "#76B900");
            RefreshParallelStatus();
        }
        catch (Exception ex) { SetStatus($"✗  {ex.Message}", "#F44747"); }
    }

    private async void BtnCopyRestartCmd_Click(object? sender, RoutedEventArgs e)
    {
        var cmd = OllamaParallelHelper.GetRestartCommand(SelectedSlots());
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is IClipboard cb)
            {
                await cb.SetTextAsync(cmd);
                SetStatus("✓  Restart command copied — paste into a PowerShell window.", "#76B900");
            }
            else
            {
                SetStatus($"Command: {cmd}", "#CCA700");
            }
        }
        catch { SetStatus($"Command: {cmd}", "#CCA700"); }
    }

    // ── Workspace browse ──────────────────────────────────────────────────────

    private async void BtnBrowseWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose default workspace folder", AllowMultiple = false });
        if (folders.Count > 0)
            TbDefaultWorkspace.Text = folders[0].Path.LocalPath;
    }

    // ── Model Depot scan (Phase 3 — local discovery only, no model loading) ──────

    private void RbBackend_Checked(object? sender, RoutedEventArgs e)
    {
        if (PnlLlamaCppSettings is null) return; // fires during InitializeComponent, before the field is set
        PnlLlamaCppSettings.IsVisible = RbBackendLlamaCpp.IsChecked == true;
    }

    private async void BtnBrowseLlamaCppRuntimePath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose folder containing llama-server.exe", AllowMultiple = false });
        if (folders.Count > 0)
            TbLlamaCppRuntimePath.Text = folders[0].Path.LocalPath;
    }

    private async void BtnBrowseLlamaCppModelPath_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a GGUF model file",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("GGUF model") { Patterns = new[] { "*.gguf" } } },
        });
        if (files.Count > 0)
            TbLlamaCppModelPath.Text = files[0].Path.LocalPath;
    }

    private async void BtnTestLlamaCppConn_Click(object? sender, RoutedEventArgs e)
    {
        BtnTestLlamaCppConn.IsEnabled = false;
        TbLlamaCppTestResult.Text = "Checking…";

        var runtimePath = TbLlamaCppRuntimePath.Text?.Trim() ?? "";
        var modelPath   = TbLlamaCppModelPath.Text?.Trim() ?? "";
        var port        = int.TryParse(TbLlamaCppPort.Text, out var p) ? p : 8080;

        var exeFound =
            File.Exists(Path.Combine(runtimePath, "llama-server.exe")) ||
            File.Exists(Path.Combine(runtimePath, "server.exe")) ||
            File.Exists(Path.Combine(runtimePath, "llama-server")) ||
            File.Exists(Path.Combine(runtimePath, "server"));
        var modelFound = File.Exists(modelPath);

        if (!exeFound || !modelFound)
        {
            var missing = !exeFound && !modelFound ? "exe and model file"
                : !exeFound ? "llama-server exe"
                : "model file";
            TbLlamaCppTestResult.Text = $"✗  {missing} not found";
            BtnTestLlamaCppConn.IsEnabled = true;
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync($"http://127.0.0.1:{port}/health");
            TbLlamaCppTestResult.Text = resp.IsSuccessStatusCode
                ? "✓  Files OK — server already running and healthy"
                : "✓  Files OK — server not running yet (Save to start it)";
        }
        catch
        {
            TbLlamaCppTestResult.Text = "✓  Files OK — server not running yet (Save to start it)";
        }
        finally { BtnTestLlamaCppConn.IsEnabled = true; }
    }

    private async void BtnBrowseNativeRuntimeModelRoot_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose native runtime model root", AllowMultiple = false });
        if (folders.Count > 0)
        {
            TbNativeRuntimeModelRoot.Text = folders[0].Path.LocalPath;
            TbDepotScanFolder.Text = folders[0].Path.LocalPath;
        }
    }

    private async void BtnBrowseDepotScanFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose folder to scan for GGUF/LoRA files", AllowMultiple = false });
        if (folders.Count > 0)
            TbDepotScanFolder.Text = folders[0].Path.LocalPath;
    }

    private async void BtnScanDepot_Click(object? sender, RoutedEventArgs e)
    {
        var folder = TbDepotScanFolder.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder))
        {
            TbDepotResults.Text = "Enter or browse to a folder first.";
            return;
        }

        BtnScanDepot.IsEnabled = false;
        TbDepotResults.Text = "Scanning…";
        try
        {
            // ModelDepot.Scan recursively walks the directory tree and hashes every path found —
            // can be slow on large folders. Off the UI thread, matching the async pattern every
            // other long-running action in this panel already uses (BtnTestConn, BtnGrabSource, etc).
            _scannedDepot = await Task.Run(() => ModelDepot.Scan(folder));
            PopulateNativeBindingOptions(_scannedDepot);
            TbDepotResults.Text = FormatDepotResults(_scannedDepot);
        }
        catch (Exception ex)
        {
            _scannedDepot = null;
            PopulateNativeBindingOptions(null);
            TbDepotResults.Text = $"Scan failed: {ex.Message}";
        }
        finally
        {
            BtnScanDepot.IsEnabled = true;
        }
    }

    private void PopulateNativeBindingOptions(ModelDepot? depot)
    {
        _nativeBindingOptions.Clear();

        if (depot is not null)
        {
            foreach (var role in Enum.GetValues<RuntimeRole>())
            {
                var binding = depot.ResolveRole(role);
                if (binding is not null)
                    _nativeBindingOptions.Add(new NativeBindingOption(binding));
            }
        }

        CbNativeBinding.ItemsSource = null;
        CbNativeBinding.ItemsSource = _nativeBindingOptions;
        CbNativeBinding.SelectedIndex = _nativeBindingOptions.Count > 0 ? 0 : -1;
        BtnRunNativeRuntimeTest.IsEnabled = _nativeBindingOptions.Count > 0;
    }

    private static string FormatDepotResults(ModelDepot depot)
    {
        var sb = new StringBuilder();

        if (depot.Assets.Count == 0)
        {
            sb.AppendLine("No GGUF files or PEFT adapter directories found under this folder.");
        }
        else
        {
            var byKind = depot.Assets
                .GroupBy(a => a.Kind)
                .OrderBy(g => g.Key);
            foreach (var group in byKind)
                sb.AppendLine($"{group.Key}: {group.Count()}");

            sb.AppendLine();
            foreach (var role in Enum.GetValues<RuntimeRole>())
            {
                var binding = depot.ResolveRole(role);
                if (binding is null)
                {
                    sb.AppendLine($"{role}: no base model resolved");
                }
                else
                {
                    var adapterText = binding.Adapter is null ? "(no adapter)" : binding.Adapter.DisplayName;
                    sb.AppendLine($"{role}: {binding.BaseModel.DisplayName} + {adapterText}");
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    private void BtnRunNativeRuntimeTest_Click(object? sender, RoutedEventArgs e) =>
        _ = RunNativeRuntimeTestAsync();

    private async Task RunNativeRuntimeTestAsync()
    {
        if (CbNativeBinding.SelectedItem is not NativeBindingOption option)
        {
            TbNativeRuntimeTestResult.Text = "Scan a model folder and choose a resolved binding first.";
            return;
        }

        var fallbackModel = TbDefaultModel.Text?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(fallbackModel))
        {
            TbNativeRuntimeTestResult.Text = "Default Model is empty, so there is no Ollama fallback target.";
            return;
        }

        BtnRunNativeRuntimeTest.IsEnabled = false;
        TbNativeRuntimeTestResult.Text = $"Running native test for {option.Binding.Role}...";
        TbNativeRuntimeLiveOutput.Text = "(starting)";

        try
        {
            var binding = option.Binding;
            var promptText = NativeRuntimeTestPrompt.PromptText;
            var outcome = await NativeRuntimeFallbackCoordinator.ExecuteAsync(
                async ct =>
                {
                    TbNativeRuntimeTestResult.Text =
                        $"Native runtime test\nBinding: {option}\nBackend: LLamaSharpRuntime";
                    TbNativeRuntimeLiveOutput.Text = string.Empty;

                    return await NativeRuntimeTestRunner.RunLocalAsync(
                        binding.BaseModel.Path,
                        promptText: promptText,
                        onToken: AppendNativeRuntimeLiveOutput,
                        ct: ct);
                },
                async (nativeAttempt, ct) =>
                {
                    var topLevel = TopLevel.GetTopLevel(this) as Window;
                    if (topLevel is null)
                        return false;

                    TbNativeRuntimeTestResult.Text =
                        $"Native runtime failed: {nativeAttempt.ErrorType ?? "UnknownError"} - {nativeAttempt.ErrorMessage ?? "no detail"}";

                    return await DialogHelper.ShowYesNoAsync(
                        topLevel,
                        "Native Runtime Failed",
                        $"Native runtime failed for {binding.BaseModel.DisplayName}.\n\n" +
                        $"{nativeAttempt.ErrorType ?? "Error"}: {nativeAttempt.ErrorMessage ?? "No detail"}\n\n" +
                        $"Retry the same Settings test with Ollama model '{fallbackModel}'?");
                },
                async ct =>
                {
                    TbNativeRuntimeTestResult.Text =
                        $"Retrying with Ollama fallback ({fallbackModel})...";
                    TbNativeRuntimeLiveOutput.Text +=
                        $"{Environment.NewLine}{Environment.NewLine}--- Ollama fallback ---{Environment.NewLine}";

                    return await NativeRuntimeTestRunner.RunRuntimeAsync(
                        new OllamaRuntime(_ollama),
                        fallbackModel,
                        promptText: promptText,
                        onToken: AppendNativeRuntimeLiveOutput,
                        ct: ct);
                });

            var evidencePath = await NativeRuntimeFallbackEvidenceStore.WriteAsync(
                outcome,
                workspaceRoot: !string.IsNullOrWhiteSpace(_current.DefaultWorkspace) ? _current.DefaultWorkspace : null);

            TbNativeRuntimeTestResult.Text = FormatNativeRuntimeOutcome(option.Binding, fallbackModel, outcome, evidencePath);
            RaiseActivity(outcome);
        }
        catch (OperationCanceledException)
        {
            TbNativeRuntimeTestResult.Text += "\nCancelled.";
        }
        catch (Exception ex)
        {
            TbNativeRuntimeTestResult.Text = $"Native runtime test failed: {ex.Message}";
        }
        finally
        {
            BtnRunNativeRuntimeTest.IsEnabled = _nativeBindingOptions.Count > 0;
        }
    }

    private static string FormatNativeRuntimeOutcome(
        RuntimeRoleBinding binding,
        string fallbackModel,
        NativeRuntimeTestOutcome outcome,
        string? evidencePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Native runtime Settings test");
        sb.AppendLine($"State: {outcome.Kind}");
        sb.AppendLine($"Binding: {binding.Role} -> {binding.BaseModel.DisplayName}");
        sb.AppendLine($"Adapter: {(binding.Adapter?.DisplayName ?? "(none in this slice)")}");
        sb.AppendLine($"Fallback model: {fallbackModel}");
        sb.AppendLine();
        AppendAttempt(sb, "Native", outcome.NativeAttempt);

        if (outcome.FallbackAttempt is not null)
        {
            sb.AppendLine();
            AppendAttempt(sb, "Fallback", outcome.FallbackAttempt);
        }

        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            sb.AppendLine();
            sb.AppendLine($"Evidence: {evidencePath}");
        }

        return sb.ToString().TrimEnd();
    }

    private static void AppendAttempt(StringBuilder sb, string label, NativeRuntimeTestAttempt attempt)
    {
        sb.AppendLine($"{label}: {(attempt.Success ? "PASS" : "FAIL")} via {attempt.RuntimeName}");
        sb.AppendLine($"Model: {attempt.ModelRef}");
        sb.AppendLine($"Availability: {(attempt.Health.IsAvailable ? "available" : "unavailable")}");
        sb.AppendLine($"TTFT: {FormatTtft(attempt.Stats.LastTimeToFirstToken)}");
        sb.AppendLine($"tok/s: {FormatRate(attempt.Stats.TokensPerSecond)}");

        if (!string.IsNullOrWhiteSpace(attempt.ErrorType) || !string.IsNullOrWhiteSpace(attempt.ErrorMessage))
            sb.AppendLine($"Error: {attempt.ErrorType ?? "Error"} - {attempt.ErrorMessage ?? "no detail"}");

    }

    private static string FormatRate(double? tokensPerSecond) =>
        tokensPerSecond is { } rate ? $"{rate:F1}" : "n/a";

    private static string FormatTtft(TimeSpan? ttft) =>
        ttft is { } value ? $"{value.TotalMilliseconds:F0} ms" : "n/a";

    private void RaiseActivity(NativeRuntimeTestOutcome outcome)
    {
        var summary = outcome.Kind switch
        {
            NativeRuntimeTestOutcomeKind.NativeSuccess =>
                $"Native Settings test passed ({outcome.NativeAttempt.ModelRef})",
            NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaSuccess =>
                $"Native Settings test failed; Ollama fallback passed ({outcome.FallbackAttempt?.ModelRef})",
            NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaFailed =>
                $"Native Settings test failed; Ollama fallback also failed ({outcome.FallbackAttempt?.ModelRef})",
            _ =>
                $"Native Settings test failed and fallback was declined ({outcome.NativeAttempt.ModelRef})",
        };

        var kind = outcome.Kind is NativeRuntimeTestOutcomeKind.NativeSuccess
            ? CoreActivityKind.Info
            : CoreActivityKind.Warning;

        ActivityRequested?.Invoke(new CoreActivityEvent(kind, "Native Runtime", summary, DateTime.Now));
    }

    private void AppendNativeRuntimeLiveOutput(string token) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (TbNativeRuntimeLiveOutput.Text == "(starting)" || TbNativeRuntimeLiveOutput.Text == "(none)")
                TbNativeRuntimeLiveOutput.Text = string.Empty;

            TbNativeRuntimeLiveOutput.Text += token;
        });

    // ── Install folder links ──────────────────────────────────────────────────

    private void BtnOpenInstallFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            OpenInExplorer(path);
        else
            SetStatus("✗  Install folder not found", "#F44747");
    }

    private void BtnOpenDataFolder_Click(object? sender, RoutedEventArgs e)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OrchestratorIDE");
        Directory.CreateDirectory(path);
        OpenInExplorer(path);
    }

    // ── Source folder browse ──────────────────────────────────────────────────

    private async void BtnBrowseSourceFolder_Click(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = "Choose folder for TheOrc source code", AllowMultiple = false });
        if (folders.Count > 0)
        {
            TbSourceFolder.Text = folders[0].Path.LocalPath;
            RefreshSourceButtons();
        }
    }

    // ── Grab Source ───────────────────────────────────────────────────────────

    private async void BtnGrabSource_Click(object? sender, RoutedEventArgs e)
    {
        var folder = TbSourceFolder.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(folder))
        {
            SetSelfStatus("✗  Set a source folder first.", "#F44747");
            return;
        }

        BtnGrabSource.IsEnabled = false;
        var isExisting = Directory.Exists(Path.Combine(folder, ".git"));

        if (isExisting)
        {
            SetSelfStatus("Pulling latest from GitHub/main…", "#CCA700");
            await RunGitAsync("pull", folder);
        }
        else
        {
            SetSelfStatus($"Cloning {RepoUrl} into {folder}…", "#CCA700");
            Directory.CreateDirectory(folder);
            await RunGitAsync($"clone {RepoUrl} .", folder);
        }

        BtnGrabSource.IsEnabled = true;
        RefreshSourceButtons();
    }

    private async Task RunGitAsync(string arguments, string workingDir)
    {
        try
        {
            var psi = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory       = workingDir,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };

            using var proc = Process.Start(psi)!;
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            if (proc.ExitCode == 0)
                SetSelfStatus($"✓  Done: {(stdout + stderr).Trim().Split('\n').LastOrDefault() ?? "OK"}", "#76B900");
            else
                SetSelfStatus($"✗  git exited {proc.ExitCode}: {stderr.Trim().Split('\n').FirstOrDefault()}", "#F44747");
        }
        catch (Exception ex)
        {
            SetSelfStatus($"✗  {ex.Message} (is git installed?)", "#F44747");
        }
    }

    // ── Open source as workspace ──────────────────────────────────────────────

    private void BtnOpenSourceAsWorkspace_Click(object? sender, RoutedEventArgs e)
    {
        var folder = TbSourceFolder.Text?.Trim() ?? "";
        if (!Directory.Exists(folder))
        {
            SetSelfStatus("✗  Source folder not found. Grab Source first.", "#F44747");
            return;
        }

        _current.SourceFolderPath = folder;
        _current.Save();

        OpenFolderAsWorkspaceRequested?.Invoke(folder);
        SetSelfStatus($"✓  Opened {Path.GetFileName(folder)} as workspace.", "#76B900");
    }

    // ── Scan GitHub for improvements ──────────────────────────────────────────

    private async void BtnScanImprovements_Click(object? sender, RoutedEventArgs e)
    {
        BtnScanImprovements.IsEnabled = false;
        SetSelfStatus("Fetching GitHub issues + commits…", "#CCA700");

        try
        {
            var (issues, commits) = await FetchGitHubDataAsync();

            if (issues == null && commits == null)
            {
                SetSelfStatus("✗  Could not reach GitHub API. Check your network.", "#F44747");
                return;
            }

            var prompt = BuildScanPrompt(issues, commits);
            SetSelfStatus($"✓  Fetched {issues?.Count ?? 0} issues, {commits?.Count ?? 0} commits — sending to agent…", "#76B900");
            ScanAnalysisReady?.Invoke(prompt);
        }
        catch (Exception ex)
        {
            SetSelfStatus($"✗  {ex.Message}", "#F44747");
        }
        finally
        {
            BtnScanImprovements.IsEnabled = true;
        }
    }

    private async Task<(List<GitHubIssue>? issues, List<GitHubCommit>? commits)>
        FetchGitHubDataAsync()
    {
        List<GitHubIssue>?  issues  = null;
        List<GitHubCommit>? commits = null;

        try
        {
            var issueJson  = await _ghHttp.GetStringAsync(IssuesApi);
            var issueArray = JsonNode.Parse(issueJson)?.AsArray();
            if (issueArray != null)
            {
                issues = issueArray
                    .Select(n => new GitHubIssue(
                        Number:  n?["number"]?.GetValue<int>() ?? 0,
                        Title:   n?["title"]?.GetValue<string>()   ?? "",
                        Body:    n?["body"]?.GetValue<string>()    ?? "",
                        Labels:  n?["labels"]?.AsArray()
                                   .Select(l => l?["name"]?.GetValue<string>() ?? "")
                                   .Where(s => s.Length > 0)
                                   .ToList() ?? [],
                        HtmlUrl: n?["html_url"]?.GetValue<string>() ?? ""))
                    .ToList();
            }
        }
        catch { /* non-fatal — partial data is fine */ }

        try
        {
            var commitJson  = await _ghHttp.GetStringAsync(CommitsApi);
            var commitArray = JsonNode.Parse(commitJson)?.AsArray();
            if (commitArray != null)
            {
                commits = commitArray
                    .Select(n => new GitHubCommit(
                        Sha:     (n?["sha"]?.GetValue<string>() ?? "")[..Math.Min(7, n?["sha"]?.GetValue<string>()?.Length ?? 0)],
                        Message: n?["commit"]?["message"]?.GetValue<string>()?.Split('\n')[0] ?? "",
                        Author:  n?["commit"]?["author"]?["name"]?.GetValue<string>() ?? ""))
                    .ToList();
            }
        }
        catch { /* non-fatal */ }

        return (issues, commits);
    }

    private static string BuildScanPrompt(
        List<GitHubIssue>?  issues,
        List<GitHubCommit>? commits)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# TheOrc Self-Improvement Scan");
        sb.AppendLine();
        sb.AppendLine($"You are TheOrc — the Orchestrator IDE — reviewing your own GitHub repository ({RepoUrl}).");
        sb.AppendLine("Analyze the open issues and recent commits below, then:");
        sb.AppendLine("1. **Prioritize** the top 3 bugs or regressions that should be fixed first.");
        sb.AppendLine("2. **Identify** the most impactful improvement or feature request.");
        sb.AppendLine("3. **Suggest** one specific code change (file, function, what to change and why).");
        sb.AppendLine("4. **Flag** any issue that is stale, duplicate, or out-of-scope.");
        sb.AppendLine();

        if (issues?.Count > 0)
        {
            sb.AppendLine($"## Open Issues ({issues.Count})");
            foreach (var iss in issues)
            {
                var labels = iss.Labels.Count > 0 ? $" [{string.Join(", ", iss.Labels)}]" : "";
                sb.AppendLine($"- #{iss.Number}{labels}: **{iss.Title}**");
                if (!string.IsNullOrWhiteSpace(iss.Body))
                {
                    var body = iss.Body.Replace("\r", "").Replace("\n", " ").Trim();
                    if (body.Length > 150) body = body[..150] + "…";
                    sb.AppendLine($"  > {body}");
                }
            }
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## Open Issues\n_(none fetched)_\n");
        }

        if (commits?.Count > 0)
        {
            sb.AppendLine($"## Recent Commits ({commits.Count})");
            foreach (var c in commits)
                sb.AppendLine($"- `{c.Sha}` {c.Message} — {c.Author}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine("Respond with a prioritized action plan. Be specific and concise. Reference issue numbers.");
        return sb.ToString();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private void SetStatus(string msg, string hex)
    {
        TbStatus.Text       = msg;
        TbStatus.Foreground = new SolidColorBrush(Color.Parse(hex));
    }

    private void SetSelfStatus(string msg, string hex)
    {
        TbSelfImproveStatus.Text       = msg;
        TbSelfImproveStatus.Foreground = new SolidColorBrush(Color.Parse(hex));
    }

    private static HttpClient BuildGitHubClient()
    {
        var v      = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("TheOrc", $"{v.Major}.{v.Minor}.{v.Build}"));
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private record GitHubIssue(int Number, string Title, string Body, List<string> Labels, string HtmlUrl);
    private record GitHubCommit(string Sha, string Message, string Author);
}
