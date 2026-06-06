using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Panels;

public partial class SettingsPanel : UserControl
{
    /// <summary>Fired when the user saves — MainWindow applies changes live.</summary>
    public event Action<AppSettings>? SettingsSaved;

    private readonly OllamaClient _ollama;

    public SettingsPanel(OllamaClient ollama)
    {
        InitializeComponent();
        _ollama = ollama;
    }

    // ── Load existing settings into controls ──────────────────────────────

    public void LoadSettings(AppSettings s)
    {
        TbOllamaHost.Text        = s.OllamaHost;
        TbDefaultModel.Text      = s.DefaultModel;
        TbMaxSteps.Text          = s.MaxStepsOverride.ToString();
        TglAutoVerify.IsChecked  = s.AutoVerify;
        TglAutoCheckpoint.IsChecked = s.AutoCheckpoint;
        TbDefaultWorkspace.Text  = s.DefaultWorkspace;
        TbStatus.Text            = "";
    }

    // ── Read controls → AppSettings ──────────────────────────────────────

    private AppSettings ReadSettings() => new AppSettings
    {
        OllamaHost        = TbOllamaHost.Text.Trim().TrimEnd('/'),
        DefaultModel      = TbDefaultModel.Text.Trim(),
        MaxStepsOverride  = int.TryParse(TbMaxSteps.Text, out var n) ? Math.Max(0, n) : 0,
        AutoVerify        = TglAutoVerify.IsChecked == true,
        AutoCheckpoint    = TglAutoCheckpoint.IsChecked == true,
        DefaultWorkspace  = TbDefaultWorkspace.Text.Trim(),
    };

    // ── Test connection ───────────────────────────────────────────────────

    private async void BtnTestConn_Click(object sender, RoutedEventArgs e)
    {
        BtnTestConn.IsEnabled = false;
        SetStatus("Testing…", "#CCA700");

        var host = TbOllamaHost.Text.Trim().TrimEnd('/');

        // Temporarily point client at whatever's in the text box
        var original = _ollama.Host;
        _ollama.Host = host;

        try
        {
            var models = await _ollama.GetInstalledModelsAsync();
            if (models.Count > 0)
                SetStatus($"✓  Connected — {models.Count} models found", "#4EC9B0");
            else
                SetStatus("⚠  Connected but no models returned", "#CCA700");
        }
        catch (Exception ex)
        {
            _ollama.Host = original;   // Revert on failure
            SetStatus($"✗  {ex.Message}", "#F44747");
        }
        finally
        {
            BtnTestConn.IsEnabled = true;
        }
    }

    // ── Save ─────────────────────────────────────────────────────────────

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var settings = ReadSettings();

        if (string.IsNullOrWhiteSpace(settings.OllamaHost))
        {
            SetStatus("✗  Ollama host cannot be empty", "#F44747");
            return;
        }

        settings.Save();
        SetStatus("✓  Saved", "#4EC9B0");
        SettingsSaved?.Invoke(settings);
    }

    // ── Browse workspace ─────────────────────────────────────────────────

    private void BtnBrowseWorkspace_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose default workspace folder" };
        if (dlg.ShowDialog() == true)
            TbDefaultWorkspace.Text = dlg.FolderName;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void SetStatus(string msg, string hex)
    {
        TbStatus.Text       = msg;
        TbStatus.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
    }
}
