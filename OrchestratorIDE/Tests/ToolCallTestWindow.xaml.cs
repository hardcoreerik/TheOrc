using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using OrchestratorIDE.Core;
using OrchestratorIDE.Services.ToolCalls;

namespace OrchestratorIDE.Tests;

// ── View-model row (one row per model × mode) ─────────────────────────────────

public class ProbeRow : INotifyPropertyChanged
{
    private static string Icon(ProbeResult? r) => r switch
    {
        ProbeResult.Pass    => "✓",
        ProbeResult.Fail    => "✗",
        ProbeResult.Timeout => "⏱",
        ProbeResult.Error   => "⚠",
        null                => "·",
        _                   => "?"
    };
    private static string IconColor(ProbeResult? r) => r switch
    {
        ProbeResult.Pass    => "#4EC94E",
        ProbeResult.Fail    => "#F44747",
        ProbeResult.Timeout => "#F0C060",
        ProbeResult.Error   => "#F0C060",
        null                => "#444444",
        _                   => "#888888"
    };

    public string  ModelId    { get; set; } = "";
    public string  ShortName  { get; set; } = "";
    public ProbeMode Mode     { get; set; }
    public string  ModeDisplay => Mode == ProbeMode.NativeApi ? "Native" : "TextJson";
    public string  ModeColor   => Mode == ProbeMode.NativeApi ? "#4A9FD9" : "#C586C0";

    // Per-test results
    private ProbeResult? _t1, _t2, _t3, _t4, _t5;
    public ProbeResult? R1 { get => _t1; set { _t1 = value; OnChanged(nameof(T1)); OnChanged(nameof(T1Color)); OnChanged(nameof(PassScore)); OnChanged(nameof(ScoreColor)); } }
    public ProbeResult? R2 { get => _t2; set { _t2 = value; OnChanged(nameof(T2)); OnChanged(nameof(T2Color)); OnChanged(nameof(PassScore)); OnChanged(nameof(ScoreColor)); } }
    public ProbeResult? R3 { get => _t3; set { _t3 = value; OnChanged(nameof(T3)); OnChanged(nameof(T3Color)); OnChanged(nameof(PassScore)); OnChanged(nameof(ScoreColor)); } }
    public ProbeResult? R4 { get => _t4; set { _t4 = value; OnChanged(nameof(T4)); OnChanged(nameof(T4Color)); OnChanged(nameof(PassScore)); OnChanged(nameof(ScoreColor)); } }
    public ProbeResult? R5 { get => _t5; set { _t5 = value; OnChanged(nameof(T5)); OnChanged(nameof(T5Color)); OnChanged(nameof(PassScore)); OnChanged(nameof(ScoreColor)); } }

    public string T1 => Icon(R1); public string T1Color => IconColor(R1);
    public string T2 => Icon(R2); public string T2Color => IconColor(R2);
    public string T3 => Icon(R3); public string T3Color => IconColor(R3);
    public string T4 => Icon(R4); public string T4Color => IconColor(R4);
    public string T5 => Icon(R5); public string T5Color => IconColor(R5);

    public int  Passes     => new[] { R1, R2, R3, R4, R5 }.Count(r => r == ProbeResult.Pass);
    public string PassScore => $"{Passes}/5";
    public string ScoreColor => Passes switch { >= 4 => "#4EC94E", >= 2 => "#F0C060", _ => "#F44747" };

    private ToolCallMode _recMode = ToolCallMode.Unknown;
    public ToolCallMode RecModeValue { get => _recMode; set { _recMode = value; OnChanged(nameof(RecMode)); OnChanged(nameof(RecColor)); } }
    public string RecMode => _recMode == ToolCallMode.Unknown ? "—" : _recMode.ToString();
    public string RecColor => _recMode switch
    {
        ToolCallMode.Native   => "#4EC94E",
        ToolCallMode.TextJson => "#C586C0",
        ToolCallMode.Both     => "#76B900",
        ToolCallMode.None     => "#F44747",
        _                     => "#666666",
    };

    // ── GOBLIN MIND Phase 1: Format Fingerprint ───────────────────────────────

    private FormatVariant? _fmt;
    public FormatVariant? FormatValue
    {
        get => _fmt;
        set { _fmt = value; OnChanged(nameof(FormatDisplay)); OnChanged(nameof(FormatColor)); }
    }
    // Only show on NativeApi row to avoid duplication (format is per-model not per-mode)
    public string FormatDisplay => Mode == ProbeMode.NativeApi
        ? (_fmt?.ToString() ?? "—")
        : "";
    public string FormatColor => _fmt == null ? "#444444" : "#F0C060";

    // ── GOBLIN MIND Phase 2: Category Boundary Map ────────────────────────────

    private string? _cats;
    public string? CatsSummary
    {
        get => _cats;
        set { _cats = value; OnChanged(nameof(CatsDisplay)); OnChanged(nameof(CatsColor)); }
    }
    // Only show on NativeApi row
    public string CatsDisplay => Mode == ProbeMode.NativeApi ? (_cats ?? "—") : "";
    public string CatsColor
    {
        get
        {
            if (_cats == null || Mode != ProbeMode.NativeApi) return "#444444";
            if (_cats.Contains('/'))
            {
                var parts = _cats.Split('/');
                if (int.TryParse(parts[0], out var pass) && int.TryParse(parts[1], out var total))
                    return pass >= total - 1 ? "#4EC94E" : pass >= total / 2 ? "#F0C060" : "#F44747";
            }
            return "#888888";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

// ── Window ────────────────────────────────────────────────────────────────────

public partial class ToolCallTestWindow : Window
{
    private CancellationTokenSource? _cts;
    private readonly ObservableCollection<ProbeRow> _rows = [];
    private readonly AppSettings _settings;

    // Called by MainWindow to set dependencies
    public OllamaClient? Ollama { get; set; }

    public ToolCallTestWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        DgResults.ItemsSource = _rows;
        TbProfilePath.Text = ToolCallProfileStore.ProfilesPath;
    }

    // ── Start / stop ──────────────────────────────────────────────────────────

    private async void BtnRunAll_Click(object sender, RoutedEventArgs e)
    {
        var models = await GetInstalledModelsAsync();
        if (models.Count == 0) { Log("No models found."); return; }
        await RunAsync(models);
    }

    private async void BtnRunSelected_Click(object sender, RoutedEventArgs e)
    {
        // Run only the model that is selected in the grid (either row's ModelId)
        if (DgResults.SelectedItem is ProbeRow row)
        {
            await RunAsync([row.ModelId]);
        }
        else
        {
            Log("Select a model row first, then click Test Selected.");
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Log("Stopped by user.");
    }

    private async void BtnRunGoblinMind_Click(object sender, RoutedEventArgs e)
    {
        // Run dispatch probe first, then format + category probes for same models
        var models = DgResults.SelectedItem is ProbeRow sel
            ? (IReadOnlyList<string>)[sel.ModelId]
            : await GetInstalledModelsAsync();
        if (models.Count == 0) { Log("No models found."); return; }

        // Dispatch probe
        await RunAsync(models);

        // Format + category probes
        await RunGoblinMindAsync(models);
    }

    // ── Goblin Mind runner (Format + Category) ────────────────────────────────

    private async Task RunGoblinMindAsync(IReadOnlyList<string> models)
    {
        _cts = new CancellationTokenSource();
        SetRunning(true);

        var host          = _settings.OllamaHost ?? "http://localhost:11434";
        var formatEngine  = new FormatProbeEngine(host);
        var categoryEngine = new CategoryProbeEngine(host);

        var done  = 0;
        var total = models.Count * 2;   // format + category per model
        PbProgress.Value = 0;

        foreach (var model in models)
        {
            if (_cts.Token.IsCancellationRequested) break;

            // ── Phase 1: Format Fingerprinting ───────────────────────────────
            Log($"\n🎨 [Format] {model}");
            TbStatus.Text = $"Format probe: {ShortName(model)}… ({done * 2 + 1}/{total})";
            FormatFingerprint? fingerprint = null;
            try
            {
                fingerprint = await formatEngine.RunAsync(model, Log, _cts.Token);
                await ToolCallProfileStore.SaveFormatFingerprintAsync(model, fingerprint);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"  Format probe ERROR: {ex.Message}"); }

            // Update rows
            Dispatcher.Invoke(() =>
            {
                var nRow = _rows.FirstOrDefault(r => r.ModelId == model && r.Mode == ProbeMode.NativeApi);
                if (nRow != null && fingerprint != null)
                    nRow.FormatValue = fingerprint.PreferredFormat;
            });
            done++;
            PbProgress.Value = (double)done * 2 / total * 100;

            // ── Phase 2: Category Boundary Mapping ───────────────────────────
            Log($"\n🗂  [Category] {model}");
            TbStatus.Text = $"Category probe: {ShortName(model)}… ({done * 2}/{total})";
            CategoryBoundaryMap? catMap = null;
            try
            {
                catMap = await categoryEngine.RunAsync(model, Log, _cts.Token);
                await ToolCallProfileStore.SaveCategoryMapAsync(model, catMap);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"  Category probe ERROR: {ex.Message}"); }

            // Update rows
            Dispatcher.Invoke(() =>
            {
                var nRow = _rows.FirstOrDefault(r => r.ModelId == model && r.Mode == ProbeMode.NativeApi);
                if (nRow != null && catMap != null)
                    nRow.CatsSummary = catMap.ShortSummary;
            });
            done++;
            PbProgress.Value = (double)done * 2 / total * 100;
        }

        SetRunning(false);
        TbStatus.Text = _cts.IsCancellationRequested ? "Stopped" : $"Goblin Mind profile complete — {done / 2} model(s)";
        PbProgress.Value = 100;
    }

    // ── Core runner ───────────────────────────────────────────────────────────

    private async Task RunAsync(IReadOnlyList<string> models)
    {
        _cts = new CancellationTokenSource();
        SetRunning(true);
        _rows.Clear();

        var host   = _settings.OllamaHost ?? "http://localhost:11434";
        var engine = new ToolCallProbeEngine(host);

        var done  = 0;
        var total = models.Count;

        PbProgress.Value = 0;

        foreach (var model in models)
        {
            if (_cts.Token.IsCancellationRequested) break;

            Log($"\n── {model}");
            TbStatus.Text = $"Testing {ShortName(model)}… ({done + 1}/{total})";

            // Pre-add placeholder rows
            var nativeRow = AddOrGetRow(model, ProbeMode.NativeApi);
            var textRow   = AddOrGetRow(model, ProbeMode.TextJson);

            ModelProbeResult result;
            try
            {
                result = await engine.RunAsync(model,
                    msg =>
                    {
                        Log(msg);
                        // Update the appropriate row as results arrive
                        Dispatcher.InvokeAsync(() => UpdateRowFromLog(nativeRow, textRow, msg));
                    },
                    _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Log($"  ERROR: {ex.Message}");
                done++;
                continue;
            }

            // Apply completed outcomes to view-model rows
            Dispatcher.Invoke(() =>
            {
                ApplyOutcomes(nativeRow, result, ProbeMode.NativeApi);
                ApplyOutcomes(textRow,   result, ProbeMode.TextJson);

                // Set recommended mode on BOTH rows
                nativeRow.RecModeValue = result.RecommendedMode;
                textRow.RecModeValue   = result.RecommendedMode;
            });

            // Persist
            await ToolCallProfileStore.SaveFromProbeResultAsync(result);
            Log($"  → {result.SummaryLine}");

            // Backfill FORMAT / CATS columns from any previously stored Goblin Mind profile
            var stored = ToolCallProfileStore.Load(model);
            if (stored != null)
            {
                Dispatcher.Invoke(() =>
                {
                    if (stored.FormatProfile   != null) nativeRow.FormatValue = stored.FormatProfile.PreferredFormat;
                    if (stored.CategoryProfile != null) nativeRow.CatsSummary = stored.CategoryProfile.ShortSummary;
                });
            }

            done++;
            PbProgress.Value = (double)done / total * 100;
        }

        SetRunning(false);
        TbStatus.Text = _cts.IsCancellationRequested ? "Stopped" : $"Done — {done} model(s) tested";
        PbProgress.Value = 100;

        // Reload any pre-existing profiles for models we didn't test
        LoadExistingProfiles(models);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private ProbeRow AddOrGetRow(string model, ProbeMode mode)
    {
        var existing = _rows.FirstOrDefault(r => r.ModelId == model && r.Mode == mode);
        if (existing != null) return existing;

        var row = new ProbeRow
        {
            ModelId   = model,
            ShortName = ShortName(model),
            Mode      = mode,
        };
        _rows.Add(row);
        return row;
    }

    private static void ApplyOutcomes(ProbeRow row, ModelProbeResult result, ProbeMode mode)
    {
        foreach (var o in result.Outcomes.Where(o => o.Mode == mode))
        {
            switch (o.TestId)
            {
                case ProbeTestId.BasicCall:         row.R1 = o.Result; break;
                case ProbeTestId.IntArgs:           row.R2 = o.Result; break;
                case ProbeTestId.MultilineContent:  row.R3 = o.Result; break;
                case ProbeTestId.ToolSelection:     row.R4 = o.Result; break;
                case ProbeTestId.StructuredOutput:  row.R5 = o.Result; break;
            }
        }
    }

    // Lightweight parse of the progress strings to update icons in real time
    // before the full result arrives
    private static void UpdateRowFromLog(ProbeRow native, ProbeRow text, string msg)
    {
        // Not needed for correctness — rows update fully via ApplyOutcomes
        // This is just for visual responsiveness. Skip complex parsing.
    }

    private void LoadExistingProfiles(IReadOnlyList<string> justTested)
    {
        var all = ToolCallProfileStore.LoadAll();
        foreach (var profile in all)
        {
            if (justTested.Contains(profile.ModelId)) continue; // already shown

            var nativeRow = AddOrGetRow(profile.ModelId, ProbeMode.NativeApi);
            var textRow   = AddOrGetRow(profile.ModelId, ProbeMode.TextJson);

            foreach (var (key, passed) in profile.TestPassMap)
            {
                var parts = key.Split('_');
                if (parts.Length < 2) continue;
                if (!Enum.TryParse<ProbeTestId>(parts[0], out var testId)) continue;
                if (!Enum.TryParse<ProbeMode>(parts[1], out var mode))     continue;

                var row     = mode == ProbeMode.NativeApi ? nativeRow : textRow;
                var outcome = passed ? ProbeResult.Pass : ProbeResult.Fail;

                switch (testId)
                {
                    case ProbeTestId.BasicCall:         row.R1 = outcome; break;
                    case ProbeTestId.IntArgs:           row.R2 = outcome; break;
                    case ProbeTestId.MultilineContent:  row.R3 = outcome; break;
                    case ProbeTestId.ToolSelection:     row.R4 = outcome; break;
                    case ProbeTestId.StructuredOutput:  row.R5 = outcome; break;
                }
            }

            nativeRow.RecModeValue = profile.RecommendedMode;
            textRow.RecModeValue   = profile.RecommendedMode;

            // ── GOBLIN MIND: populate FORMAT / CATS columns from stored profile ──
            if (profile.FormatProfile != null)
                nativeRow.FormatValue = profile.FormatProfile.PreferredFormat;
            if (profile.CategoryProfile != null)
                nativeRow.CatsSummary = profile.CategoryProfile.ShortSummary;
        }
    }

    private async Task<List<string>> GetInstalledModelsAsync()
    {
        try
        {
            if (Ollama != null)
                return await Ollama.GetInstalledModelsAsync();

            // Fallback: direct HTTP call
            using var http = new System.Net.Http.HttpClient();
            var resp = await http.GetAsync($"{_settings.OllamaHost}/api/tags");
            resp.EnsureSuccessStatusCode();
            var json = System.Text.Json.Nodes.JsonNode.Parse(
                await resp.Content.ReadAsStringAsync());
            return json?["models"]?.AsArray()
                .Select(m => m?["name"]?.GetValue<string>() ?? "")
                .Where(n => n.Length > 0)
                .ToList() ?? [];
        }
        catch (Exception ex)
        {
            Log($"Cannot fetch models: {ex.Message}");
            return [];
        }
    }

    private void SetRunning(bool running)
    {
        BtnRunAll.IsEnabled      = !running;
        BtnRunSelected.IsEnabled = !running;
        BtnStop.IsEnabled        =  running;
        PbProgress.IsIndeterminate = running && _rows.Count == 0;
    }

    private void Log(string msg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            TbLog.Text += msg + "\n";
            LogScroll.ScrollToBottom();
        });
    }

    private static string ShortName(string id)
    {
        if (id.Contains('/'))
        {
            var seg = id.Split('/').Last();
            return seg.Length > 34 ? seg[..34] + "…" : seg;
        }
        return id.Length > 34 ? id[..34] + "…" : id;
    }

    // ── Footer buttons ────────────────────────────────────────────────────────

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText(TbLog.Text);

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        base.OnClosed(e);
    }

    // ── Public entry for --tool-probe CLI mode ────────────────────────────────

    /// <summary>
    /// Run tests immediately on startup (used in --tool-probe headless mode).
    /// Pass null to test all installed models.
    /// </summary>
    public async Task RunHeadlessAsync(string? specificModel = null)
    {
        Loaded += async (_, _) =>
        {
            var models = specificModel != null
                ? new List<string> { specificModel }
                : await GetInstalledModelsAsync();
            await RunAsync(models);
            // Brief delay so the user can see the final state
            await Task.Delay(3000);
            Close();
        };
        await Task.CompletedTask;
    }
}
