using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class OllamaCheckPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    private enum BackendChoice { LlamaCpp, InstallOllama, ExistingOllama }
    private BackendChoice _choice = BackendChoice.LlamaCpp; // default

    public OllamaCheckPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await DetectOllamaAsync();
    }

    // ── Ollama detection ──────────────────────────────────────────────────────

    private async Task DetectOllamaAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var resp = await http.GetAsync("http://localhost:11434/api/tags");
            _vm.State.OllamaDetected = true;
            _vm.State.OllamaRunning  = resp.IsSuccessStatusCode;
        }
        catch
        {
            _vm.State.OllamaDetected = false;
            _vm.State.OllamaRunning  = false;
        }

        UpdateStatusDisplay();
    }

    private void UpdateStatusDisplay()
    {
        if (_vm.State.OllamaRunning)
        {
            TxtOllamaIcon.Text   = "🟢";
            TxtOllamaStatus.Text = "Ollama is running on this machine";
            TxtOllamaDetail.Text = "Detected a running Ollama service at http://localhost:11434. " +
                                   "You can use it directly or switch to llama.cpp.";

            TxtOllamaOptionDetail.Text =
                "Ollama is already running — The Orc connects to it directly. " +
                "You will need to have a compatible model available in Ollama.";

            // Enable "Use existing Ollama" since it's running
            CardOllama.IsEnabled = true;
            CardOllama.Opacity   = 1.0;
        }
        else if (_vm.State.OllamaDetected)
        {
            TxtOllamaIcon.Text   = "🟡";
            TxtOllamaStatus.Text = "Ollama is installed but not running";
            TxtOllamaDetail.Text = "Ollama was found but its service is not active. " +
                                   "llama.cpp (bundled) is recommended, or install a fresh Ollama.";

            CardOllama.IsEnabled = false;
            CardOllama.Opacity   = 0.4;
        }
        else
        {
            TxtOllamaIcon.Text   = "⚪";
            TxtOllamaStatus.Text = "Ollama not detected";
            TxtOllamaDetail.Text = "No Ollama service found. " +
                                   "Use llama.cpp (bundled) or let The Orc install Ollama for you.";

            CardOllama.IsEnabled = false;
            CardOllama.Opacity   = 0.4;
        }

        // Default to llama.cpp on first load
        SelectOption(BackendChoice.LlamaCpp);
    }

    // ── Option selection ──────────────────────────────────────────────────────

    private void CardLlamaCpp_Click(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
        => SelectOption(BackendChoice.LlamaCpp);

    private void CardInstallOllama_Click(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
        => SelectOption(BackendChoice.InstallOllama);

    private void CardOllama_Click(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CardOllama.IsEnabled)
            SelectOption(BackendChoice.ExistingOllama);
    }

    private void SelectOption(BackendChoice choice)
    {
        _choice = choice;

        // Write into shared state
        _vm.State.UseExistingOllama = choice != BackendChoice.LlamaCpp;
        _vm.State.InstallOllama     = choice == BackendChoice.InstallOllama;

        // Visual update for all three cards
        ApplyCardStyle(CardLlamaCpp,      RadioLlamaCpp,      choice == BackendChoice.LlamaCpp);
        ApplyCardStyle(CardInstallOllama, RadioInstallOllama, choice == BackendChoice.InstallOllama);
        ApplyCardStyle(CardOllama,        RadioOllama,        choice == BackendChoice.ExistingOllama);
    }

    private void ApplyCardStyle(System.Windows.Controls.Border card,
                                System.Windows.Shapes.Ellipse  radio,
                                bool selected)
    {
        if (!card.IsEnabled) return;

        card.BorderBrush  = (SolidColorBrush)FindResource(
            selected ? "BorderAccent" : "BorderSubtle");
        radio.Fill        = (SolidColorBrush)FindResource(
            selected ? "Accent"       : "BgPanel");
        radio.Stroke      = (SolidColorBrush)FindResource(
            selected ? "Accent"       : "TextMuted");
    }

    public bool CanLeave() => true;
}
