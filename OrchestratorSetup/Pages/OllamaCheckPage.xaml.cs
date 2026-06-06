using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class OllamaCheckPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private bool _useLlamaCpp = true; // default

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
            TxtOllamaIcon.Text    = "🟢";
            TxtOllamaStatus.Text  = "Ollama is running on this machine";
            TxtOllamaDetail.Text  = "Detected a running Ollama service at http://localhost:11434. " +
                                    "You can use it instead of downloading llama.cpp.";
            TxtOllamaOptionDetail.Text =
                "Ollama is already running — The Orc can connect to it directly. " +
                "You will still need to pull a compatible model via Ollama.";
        }
        else if (_vm.State.OllamaDetected)
        {
            TxtOllamaIcon.Text    = "🟡";
            TxtOllamaStatus.Text  = "Ollama is installed but not running";
            TxtOllamaDetail.Text  = "Ollama was found but its service isn't active. " +
                                    "We recommend using llama.cpp (bundled) instead.";
        }
        else
        {
            TxtOllamaIcon.Text    = "⚪";
            TxtOllamaStatus.Text  = "Ollama not detected";
            TxtOllamaDetail.Text  = "No Ollama service was found. The Orc will install " +
                                    "llama.cpp as its local inference engine.";
            CardOllama.IsEnabled  = false;
            CardOllama.Opacity    = 0.4;
        }

        SelectOption(useLlamaCpp: true);
    }

    // ── Option selection ──────────────────────────────────────────────────────

    private void CardLlamaCpp_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => SelectOption(useLlamaCpp: true);

    private void CardOllama_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CardOllama.IsEnabled)
            SelectOption(useLlamaCpp: false);
    }

    private void SelectOption(bool useLlamaCpp)
    {
        _useLlamaCpp = useLlamaCpp;
        _vm.State.UseExistingOllama = !useLlamaCpp;

        // llama.cpp card
        CardLlamaCpp.BorderBrush  = (SolidColorBrush)FindResource(useLlamaCpp ? "BorderAccent" : "BorderSubtle");
        RadioLlamaCpp.Fill        = (SolidColorBrush)FindResource(useLlamaCpp ? "Accent" : "BgPanel");
        RadioLlamaCpp.Stroke      = (SolidColorBrush)FindResource(useLlamaCpp ? "Accent" : "TextMuted");

        // Ollama card
        CardOllama.BorderBrush    = (SolidColorBrush)FindResource(!useLlamaCpp ? "BorderAccent" : "BorderSubtle");
        RadioOllama.Fill          = (SolidColorBrush)FindResource(!useLlamaCpp ? "Accent" : "BgPanel");
        RadioOllama.Stroke        = (SolidColorBrush)FindResource(!useLlamaCpp ? "Accent" : "TextMuted");
    }

    public bool CanLeave() => true;
}
