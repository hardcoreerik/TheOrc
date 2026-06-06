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

    /// <summary>
    /// Fired when the user clicks "Check Now" — MainWindow wires this to
    /// <see cref="OrchestratorIDE.Core.UpdateChecker.CheckAsync"/> and shows
    /// the result badge / dialog.
    /// </summary>
    public event Func<Task>? CheckUpdatesRequested;

    private readonly OllamaClient _ollama;

    public SettingsPanel(OllamaClient ollama)
    {
        InitializeComponent();
        _ollama = ollama;
    }

    // ── Load existing settings into controls ──────────────────────────────

    public void LoadSettings(AppSettings s)
    {
        TbOllamaHost.Text           = s.OllamaHost;
        TbDefaultModel.Text         = s.DefaultModel;
        TbMaxSteps.Text             = s.MaxStepsOverride.ToString();
        TglAutoVerify.IsChecked     = s.AutoVerify;
        TglAutoCheckpoint.IsChecked = s.AutoCheckpoint;
        TglCheckUpdates.IsChecked   = s.CheckForUpdates;
        TbDefaultWorkspace.Text     = s.DefaultWorkspace;
        TbStatus.Text               = "";

        // Show current version + last known latest
        var current = UpdateChecker.CurrentVersion();
        var known   = s.LastKnownLatestVersion;
        TbVersionInfo.Text = string.IsNullOrEmpty(known)
            ? $"v{current} installed"
            : $"v{current} installed  •  latest: v{known}";
    }

    // ── Read controls → AppSettings ──────────────────────────────────────

    private AppSettings ReadSettings() => new AppSettings
    {
        OllamaHost            = TbOllamaHost.Text.Trim().TrimEnd('/'),
        DefaultModel          = TbDefaultModel.Text.Trim(),
        MaxStepsOverride      = int.TryParse(TbMaxSteps.Text, out var n) ? Math.Max(0, n) : 0,
        AutoVerify            = TglAutoVerify.IsChecked    == true,
        AutoCheckpoint        = TglAutoCheckpoint.IsChecked == true,
        CheckForUpdates       = TglCheckUpdates.IsChecked   == true,
        DefaultWorkspace      = TbDefaultWorkspace.Text.Trim(),
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

    // ── Check for updates ─────────────────────────────────────────────────

    private async void BtnCheckNow_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckNow.IsEnabled = false;
        SetStatus("Checking for updates…", "#CCA700");

        if (CheckUpdatesRequested != null)
            await CheckUpdatesRequested.Invoke();

        BtnCheckNow.IsEnabled = true;
        SetStatus("", "#4EC9B0");
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
