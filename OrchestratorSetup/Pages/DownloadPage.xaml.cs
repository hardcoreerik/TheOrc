using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorSetup.Services;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Download + install page. Drives <see cref="InstallOrchestrator"/> and reflects
/// its progress in the UI. Navigation buttons are hidden — this page advances itself.
/// </summary>
public partial class DownloadPage : UserControl, IInstallerPage
{
    // ── Refs to dynamically-built item rows ────────────────────────────────────

    private sealed class RowRef
    {
        public string       Key          { get; init; } = "";
        public ProgressBar  Pb           { get; init; } = null!;
        public TextBlock    StatusLabel  { get; init; } = null!;
        public TextBlock    SpeedLabel   { get; init; } = null!;
        public Border       Card         { get; init; } = null!;
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly InstallerViewModel _vm;
    private readonly List<RowRef>       _rows = [];
    private CancellationTokenSource?    _cts;
    private bool                        _started;

    public DownloadPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await BeginIfNotStarted();
    }

    // ── Entry ─────────────────────────────────────────────────────────────────

    private async Task BeginIfNotStarted()
    {
        if (_started) return;
        _started = true;

        BuildItemRows();
        await RunOrchestratorAsync();
    }

    // ── Row builder ───────────────────────────────────────────────────────────

    private void BuildItemRows()
    {
        DownloadItems.Children.Clear();
        _rows.Clear();

        // App exe always first (unless the manifest had no URL — unlikely in prod)
        if (!string.IsNullOrEmpty(_vm.State.AppDownloadUrl))
        {
            _rows.Add(AddRow("OrchestratorIDE", "OrchestratorIDE",
                             "The Orc application (self-contained, no .NET install required)"));
        }

        if (!_vm.State.UseExistingOllama)
        {
            _rows.Add(AddRow("runtime", "llama.cpp runtime",
                             "Inference engine (llama-server.exe + GPU libraries)"));
        }

        // One progress row per selected model; fall back to single-model legacy path
        var modelsToShow = _vm.State.SelectedModels.Any()
            ? _vm.State.SelectedModels
            : _vm.AllModels.Where(m => m.Id == _vm.State.SelectedModelId).ToList();

        foreach (var model in modelsToShow)
        {
            var role = _vm.State.ModelRoles.TryGetValue(model.Id, out var r) ? r : "Model";
            _rows.Add(AddRow(model.Name, model.Name, $"{role}  ·  GGUF model file"));
        }

        _rows.Add(AddRow("config", "Configuration", "settings.json + .agent.md profile"));
    }

    private RowRef AddRow(string key, string title, string subtitle)
    {
        var pb = new ProgressBar
        {
            Value  = 0,
            Maximum = 100,
            Style  = (Style)FindResource("ThemedProgressBar"),
            Margin = new Thickness(0, 0, 0, 4),
        };
        var statusLbl = new TextBlock
        {
            Text       = "Waiting…",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Foreground = (SolidColorBrush)FindResource("TextSecondary"),
        };
        var speedLbl = new TextBlock
        {
            Text       = "",
            FontFamily = new FontFamily("Consolas"),
            FontSize   = 10,
            Foreground = (SolidColorBrush)FindResource("Accent"),
            Margin     = new Thickness(0, 2, 0, 0),
        };

        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };
        sp.Children.Add(new TextBlock
        {
            Text       = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)FindResource("TextPrimary"),
            Margin     = new Thickness(0, 0, 0, 2),
        });
        sp.Children.Add(new TextBlock
        {
            Text       = subtitle,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Foreground = (SolidColorBrush)FindResource("TextSecondary"),
            Margin     = new Thickness(0, 0, 0, 8),
        });
        sp.Children.Add(pb);
        sp.Children.Add(statusLbl);
        sp.Children.Add(speedLbl);

        var card = new Border
        {
            Style   = (Style)FindResource("CardBorder"),
            Padding = new Thickness(14, 10, 14, 10),
            Margin  = new Thickness(0, 0, 0, 8),
            Child   = sp,
        };

        DownloadItems.Children.Add(card);

        var row = new RowRef { Key = key, Pb = pb, StatusLabel = statusLbl, SpeedLabel = speedLbl, Card = card };
        return row;
    }

    // ── Orchestrator wiring ───────────────────────────────────────────────────

    private async Task RunOrchestratorAsync()
    {
        _cts = new CancellationTokenSource();

        using var orchestrator = new InstallOrchestrator(_vm);

        // Map item progress → matching row
        orchestrator.OnItemProgress += p =>
        {
            Dispatcher.Invoke(() => UpdateItemRow(p));
        };

        // Overall progress → header bar
        orchestrator.OnOverallProgress += (step, total, name, pct) =>
        {
            Dispatcher.Invoke(() =>
            {
                PbOverall.Value      = pct;
                TxtOverallPct.Text   = $"{pct:F0}%";
                TxtOverallStatus.Text = name;
            });
        };

        // Log → scrolling text box
        orchestrator.OnLog += msg =>
        {
            Dispatcher.Invoke(() => AppendLog(msg));
        };

        try
        {
            await orchestrator.RunAsync(_cts.Token);

            // Success — update header and advance to Complete page
            Dispatcher.Invoke(() =>
            {
                TxtPageTitle.Text    = "Installation Complete ✓";
                TxtPageSubtitle.Text = "All components installed. Proceeding…";
                PbOverall.Value      = 100;
                TxtOverallPct.Text   = "100%";
                TxtOverallStatus.Text = "Done.";
            });

            await Task.Delay(1200); // brief pause so the user sees the 100%
            Dispatcher.Invoke(() => _vm.NavigateTo(InstallerViewModel.Page.Complete));
        }
        catch (OperationCanceledException)
        {
            Dispatcher.Invoke(() =>
            {
                TxtPageTitle.Text    = "Installation Cancelled";
                TxtOverallStatus.Text = "Cancelled by user.";
            });
        }
        catch (Exception ex)
        {
            Dispatcher.Invoke(() =>
            {
                TxtPageTitle.Text    = "Installation Failed";
                TxtPageSubtitle.Text = ex.Message;
                TxtOverallStatus.Text = "See log below for details.";
                AppendLog($"\n❌ {ex}");
            });
        }
    }

    // ── Row update ────────────────────────────────────────────────────────────

    private void UpdateItemRow(DownloadProgress p)
    {
        // Match progress item to the correct row.
        // Row keys: exact model name (for model rows) or fixed strings ("runtime", "config", "OrchestratorIDE").
        // InstallOrchestrator fires progress with ItemName = the model's Name or step name.
        RowRef? row = _rows.FirstOrDefault(r =>
            string.Equals(r.Key, p.ItemName, StringComparison.OrdinalIgnoreCase) ||
            p.ItemName.Contains(r.Key, StringComparison.OrdinalIgnoreCase) ||
            r.Key.Contains(p.ItemName, StringComparison.OrdinalIgnoreCase));

        if (row is null) return;

        if (p.Error is not null)
        {
            row.StatusLabel.Text       = $"⚠ {p.Error}";
            row.StatusLabel.Foreground = (SolidColorBrush)FindResource("Error");
            return;
        }

        row.Pb.Value        = p.Percent;
        row.StatusLabel.Text = p.StatusLine;

        if (p.IsComplete)
        {
            row.StatusLabel.Foreground = (SolidColorBrush)FindResource("Success");
            row.SpeedLabel.Text        = "";
            row.Card.BorderBrush       = (SolidColorBrush)FindResource("Success");
        }
        else if (p.SpeedBytesPerSec > 0)
        {
            row.SpeedLabel.Text = $"{p.SpeedDisplay}  {p.EtaDisplay}";
        }
    }

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void AppendLog(string msg)
    {
        TxtLog.Text += msg + "\n";
        LogScroller.ScrollToBottom();
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave() => _vm.State.InstallationComplete;
}
