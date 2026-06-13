using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class CompletePage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    public CompletePage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => PopulateSummary();
    }

    private void PopulateSummary()
    {
        var profile = CodingProfile.All.FirstOrDefault(p => p.Id == _vm.State.SelectedProfileId);
        var model   = _vm.AllModels.FirstOrDefault(m  => m.Id  == _vm.State.SelectedModelId);

        TxtSummaryProfile.Text = profile is not null
            ? $"{profile.Emoji} {profile.Name}"
            : _vm.State.SelectedProfileId;

        TxtSummaryModel.Text = model?.Name ?? _vm.State.SelectedModelId;
        TxtSummaryPath.Text  = _vm.State.AppInstallPath;

        // Sync checkboxes to state defaults
        ChkLaunch.IsChecked         = _vm.State.LaunchAfterInstall;
        ChkDesktopShortcut.IsChecked= _vm.State.CreateDesktopShortcut;
        ChkStartMenu.IsChecked      = _vm.State.CreateStartMenuShortcut;
    }

    // ── Checkbox handlers ─────────────────────────────────────────────────────

    private void ChkLaunch_Changed(object sender, RoutedEventArgs e)
        => _vm.State.LaunchAfterInstall = ChkLaunch.IsChecked == true;

    private void ChkDesktop_Changed(object sender, RoutedEventArgs e)
        => _vm.State.CreateDesktopShortcut = ChkDesktopShortcut.IsChecked == true;

    private void ChkStartMenu_Changed(object sender, RoutedEventArgs e)
        => _vm.State.CreateStartMenuShortcut = ChkStartMenu.IsChecked == true;

    // ── Finish ────────────────────────────────────────────────────────────────

    private void BtnFinish_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.State.LaunchAfterInstall)
        {
            var exePath = Path.Combine(_vm.State.AppInstallPath, "OrchestratorIDE.exe");
            if (File.Exists(exePath))
            {
                try { Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show($"Could not launch The Orc:\n{ex.Message}",
                        "Launch Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            else
            {
                MessageBox.Show(
                    $"OrchestratorIDE.exe was not found at:\n{exePath}\n\n" +
                    "This is expected in Phase D — download logic arrives in Phase E.",
                    "Launch Skipped (Phase D)",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        Application.Current.Shutdown(0);
    }

    public bool CanLeave() => true;
}
