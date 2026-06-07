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

    /// <summary>
    /// Fired when the user clicks "Regenerate Agent File" — MainWindow opens
    /// the FirstRunWindow wizard and handles the write + rules reload.
    /// </summary>
    public event Func<Task>? RegenerateAgentFileRequested;

    private readonly OllamaClient _ollama;
    private AppSettings _current = new();  // snapshot from last LoadSettings call

    public SettingsPanel(OllamaClient ollama)
    {
        InitializeComponent();
        _ollama = ollama;
    }

    // ── Load existing settings into controls ──────────────────────────────

    public void LoadSettings(AppSettings s)
    {
        _current = s;  // keep reference so ReadSettings can preserve unknown fields

        TbOllamaHost.Text            = s.OllamaHost;
        TbDefaultModel.Text          = s.DefaultModel;
        TbMaxSteps.Text              = s.MaxStepsOverride.ToString();
        TglAutoVerify.IsChecked        = s.AutoVerify;
        TglAutoCheckpoint.IsChecked    = s.AutoCheckpoint;
        TglRestoreLastModel.IsChecked  = s.RestoreLastModel;
        TglAutoModelSwitch.IsChecked   = s.AutoModelSwitch;
        TglCheckUpdates.IsChecked    = s.CheckForUpdates;
        TbDefaultWorkspace.Text      = s.DefaultWorkspace;
        TbStatus.Text                = "";

        // Show current version + last known latest
        var current = UpdateChecker.CurrentVersion();
        var known   = s.LastKnownLatestVersion;
        TbVersionInfo.Text = string.IsNullOrEmpty(known)
            ? $"v{current} installed"
            : $"v{current} installed  •  latest: v{known}";

        // ── Multi-agent / parallel ─────────────────────────────────────
        RefreshParallelStatus();
        SetComboToSlots(s.OllamaParallelSlots > 1 ? s.OllamaParallelSlots
                        : OllamaParallelHelper.DetectCurrentSlots());
        // Show VRAM-based recommendation hint
        var recommended = OllamaParallelHelper.RecommendedSlots(s.DetectedVramGb);
        TbSlotHint.Text = s.DetectedVramGb > 0
            ? $"← recommended for your GPU ({s.DetectedVramGb:F0} GB VRAM): {recommended}"
            : "(select based on available VRAM)";
    }

    private void RefreshParallelStatus()
    {
        var slots = OllamaParallelHelper.DetectCurrentSlots();
        TbParallelStatus.Text = OllamaParallelHelper.StatusText(slots);
        TbParallelStatus.Foreground = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(OllamaParallelHelper.StatusColor(slots)));
        TbParallelExplain.Text = OllamaParallelHelper.GetExplanation(slots);
    }

    private void SetComboToSlots(int slots)
    {
        foreach (ComboBoxItem item in CbParallelSlots.Items)
        {
            if (item.Tag?.ToString() == slots.ToString())
            {
                CbParallelSlots.SelectedItem = item;
                return;
            }
        }
        CbParallelSlots.SelectedIndex = 0; // fallback to 1
    }

    private int SelectedSlots()
    {
        if (CbParallelSlots.SelectedItem is ComboBoxItem item
            && int.TryParse(item.Tag?.ToString(), out var n))
            return n;
        return 1;
    }

    // ── Read controls → AppSettings ──────────────────────────────────────
    // Start from _current so fields this panel doesn't control are preserved.

    private AppSettings ReadSettings()
    {
        // Clone current settings to preserve fields we don't surface in the UI
        // (ActivityVerbosity, FirstRunComplete, RecentWorkspaces, detected hardware, etc.)
        var s = _current;
        s.OllamaHost          = TbOllamaHost.Text.Trim().TrimEnd('/');
        s.DefaultModel        = TbDefaultModel.Text.Trim();
        s.MaxStepsOverride    = int.TryParse(TbMaxSteps.Text, out var n) ? Math.Max(0, n) : 0;
        s.AutoVerify          = TglAutoVerify.IsChecked         == true;
        s.AutoCheckpoint      = TglAutoCheckpoint.IsChecked     == true;
        s.RestoreLastModel    = TglRestoreLastModel.IsChecked   == true;
        s.AutoModelSwitch     = TglAutoModelSwitch.IsChecked    == true;
        s.CheckForUpdates     = TglCheckUpdates.IsChecked     == true;
        s.DefaultWorkspace    = TbDefaultWorkspace.Text.Trim();
        s.OllamaParallelSlots = SelectedSlots();
        return s;
    }

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
                SetStatus($"✓  Connected — {models.Count} models found", "#76B900");
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
        SetStatus("✓  Saved", "#76B900");
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
        SetStatus("", "#76B900");
    }

    // ── Regenerate agent file ─────────────────────────────────────────────

    private async void BtnRegenerateAgentFile_Click(object sender, RoutedEventArgs e)
    {
        BtnRegenerateAgentFile.IsEnabled = false;
        if (RegenerateAgentFileRequested != null)
            await RegenerateAgentFileRequested.Invoke();
        BtnRegenerateAgentFile.IsEnabled = true;
    }

    // ── Multi-agent parallel slots ───────────────────────────────────────

    private void BtnSetPermanent_Click(object sender, RoutedEventArgs e)
    {
        var slots = SelectedSlots();
        try
        {
            OllamaParallelHelper.SetPermanently(slots);
            SetStatus(
                $"✓  OLLAMA_NUM_PARALLEL={slots} written to Windows user environment. " +
                "Restart Ollama for it to take effect.",
                "#76B900");
            RefreshParallelStatus();
        }
        catch (Exception ex)
        {
            SetStatus($"✗  Failed to write env var: {ex.Message}", "#F44747");
        }
    }

    private void BtnCopyRestartCmd_Click(object sender, RoutedEventArgs e)
    {
        var slots = SelectedSlots();
        var cmd   = OllamaParallelHelper.GetRestartCommand(slots);
        try
        {
            System.Windows.Clipboard.SetText(cmd);
            SetStatus(
                "✓  Restart command copied — paste into a PowerShell window to apply immediately.",
                "#76B900");
        }
        catch
        {
            SetStatus($"Command: {cmd}", "#CCA700");
        }
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
