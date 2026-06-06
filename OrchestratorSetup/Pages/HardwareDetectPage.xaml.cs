using System.Windows.Controls;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class HardwareDetectPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    public HardwareDetectPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => RefreshDetectionResults();
    }

    private void RefreshDetectionResults()
    {
        // Phase F will replace these stubs with real WMI calls.
        // For now, display whatever InstallerState already holds.
        var s = _vm.State;

        TxtGpuName.Text = string.IsNullOrEmpty(s.DetectedGpuName) || s.DetectedGpuName == "Unknown"
            ? "Not detected — detection runs in Phase F"
            : s.DetectedGpuName;

        TxtVram.Text    = s.DetectedVramGb == 0
            ? "Unknown (assuming CPU-only)"
            : $"{s.DetectedVramGb} GB";

        TxtCuda.Text    = string.IsNullOrEmpty(s.CudaVersion)
            ? "Not detected"
            : $"CUDA {s.CudaVersion}";

        TxtVariant.Text = s.SelectedRuntimeVariant;

        // Pre-select the matching ComboBox item
        foreach (ComboBoxItem item in CmbVariant.Items)
        {
            if (item.Tag?.ToString() == s.SelectedRuntimeVariant)
            {
                CmbVariant.SelectedItem = item;
                break;
            }
        }

        // Default to CPU if nothing selected
        if (CmbVariant.SelectedItem is null)
            CmbVariant.SelectedIndex = 4;
    }

    private void CmbVariant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbVariant.SelectedItem is ComboBoxItem item)
        {
            _vm.State.SelectedRuntimeVariant = item.Tag?.ToString() ?? "cpu";
            TxtVariant.Text = _vm.State.SelectedRuntimeVariant;
        }
    }

    public bool CanLeave() => true;
}
