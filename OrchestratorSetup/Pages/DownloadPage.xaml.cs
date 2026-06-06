using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Phase D stub: the UI scaffolding for the download/install step.
/// Phase E (DownloadService) will plug real HTTP range-download logic
/// into <see cref="StartInstallAsync"/>.
/// </summary>
public partial class DownloadPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private bool _started;

    public DownloadPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await BeginIfNotStarted();
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    private async Task BeginIfNotStarted()
    {
        if (_started) return;
        _started = true;
        BuildItemList();
        await StartInstallAsync();
    }

    // ── Build the per-item progress rows ──────────────────────────────────────

    private void BuildItemList()
    {
        DownloadItems.Children.Clear();

        if (!_vm.State.UseExistingOllama)
        {
            DownloadItems.Children.Add(MakeItemRow("llama.cpp runtime",
                "llama-server.exe and GPU libraries", out _, out _));
        }

        var modelName = _vm.AllModels.FirstOrDefault(m => m.Id == _vm.State.SelectedModelId)?.Name
                        ?? _vm.State.SelectedModelId;
        DownloadItems.Children.Add(MakeItemRow(modelName,
            "GGUF model file", out _, out _));

        DownloadItems.Children.Add(MakeItemRow("OrchestratorIDE",
            "Application files", out _, out _));
    }

    private static Border MakeItemRow(string title, string subtitle,
        out ProgressBar pb, out TextBlock statusLabel)
    {
        var outerPb     = new ProgressBar { Value = 0, Maximum = 100 };
        var outerStatus = new TextBlock   { Text  = "Waiting…" };
        pb          = outerPb;
        statusLabel = outerStatus;

        outerPb.Style     = (Style)Application.Current.FindResource("ThemedProgressBar");
        outerStatus.Style = (Style)Application.Current.FindResource("TextSubtitle");
        outerStatus.FontSize  = 11;
        outerStatus.Margin    = new Thickness(0, 4, 0, 0);

        var card = new Border
        {
            Style   = (Style)Application.Current.FindResource("CardBorder"),
            Padding = new Thickness(14, 10, 14, 10),
        };

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text       = title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextPrimary"),
            Margin     = new Thickness(0, 0, 0, 4),
        });
        sp.Children.Add(new TextBlock
        {
            Text       = subtitle,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Foreground = (SolidColorBrush)Application.Current.FindResource("TextSecondary"),
            Margin     = new Thickness(0, 0, 0, 8),
        });
        sp.Children.Add(outerPb);
        sp.Children.Add(outerStatus);

        card.Child = sp;
        return card;
    }

    // ── Install logic (Phase D stub — Phase E replaces this body) ────────────

    private async Task StartInstallAsync()
    {
        Log("Installation starting…");
        AppendLog($"App path  : {_vm.State.AppInstallPath}");
        AppendLog($"Model path: {_vm.State.ModelStoragePath}");
        AppendLog($"Profile   : {_vm.State.SelectedProfileId}");
        AppendLog($"Model     : {_vm.State.SelectedModelId}");
        AppendLog($"Runtime   : {_vm.State.SelectedRuntimeVariant}");
        AppendLog($"Ollama    : {(_vm.State.UseExistingOllama ? "use existing" : "llama.cpp")}");

        // ── Phase D: simulate progress for UI review ──────────────────────────
        // Phase E will replace this with real DownloadService calls.
        AppendLog("\n[Phase D] Download logic not yet implemented.");
        AppendLog("Phase E will add: HTTP range-resume, SHA-256 verify, extract.");

        await SimulateProgressAsync();

        // Mark complete so MainWindow can enable the Finish button
        _vm.State.InstallationComplete = true;
        TxtPageTitle.Text    = "Installation Complete ✓";
        TxtPageSubtitle.Text = "All components have been installed. Click Finish to launch The Orc.";
        PbOverall.Value      = 100;
        TxtOverallPct.Text   = "100%";
        TxtOverallStatus.Text = "Done.";
        AppendLog("\nInstallation complete (simulation).");

        // Advance to the Complete page
        _vm.NavigateTo(InstallerViewModel.Page.Complete);
    }

    private async Task SimulateProgressAsync()
    {
        for (int i = 0; i <= 100; i += 2)
        {
            await Task.Delay(40);
            PbOverall.Value   = i;
            TxtOverallPct.Text = $"{i}%";
            TxtOverallStatus.Text = i < 30  ? "Downloading llama.cpp runtime…" :
                                    i < 75  ? "Downloading model…" :
                                    i < 95  ? "Installing application…" : "Finalising…";
        }
    }

    // ── Log helpers ───────────────────────────────────────────────────────────

    private void Log(string msg)
    {
        TxtLog.Text = msg;
        LogScroller.ScrollToBottom();
    }

    private void AppendLog(string msg)
    {
        TxtLog.Text += "\n" + msg;
        LogScroller.ScrollToBottom();
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave() => _vm.State.InstallationComplete;
}
