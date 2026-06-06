using System.Text;
using System.Windows;
using System.Windows.Controls;
using OrchestratorSetup.Services;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Hardware detection wizard page.
/// Runs <see cref="HardwareDetector.DetectAsync"/> on a background thread,
/// streams log messages into the collapsible log box, then populates the
/// result grid and writes detected values into <see cref="InstallerViewModel"/>.
/// </summary>
public partial class HardwareDetectPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private bool _detected = false;

    public HardwareDetectPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += async (_, _) => await RunDetectionAsync();
    }

    // ── Detection ─────────────────────────────────────────────────────────────

    private async Task RunDetectionAsync()
    {
        if (_detected) return; // Don't re-run if the user navigates back

        SetScanningState(true);

        var logBuilder = new StringBuilder();

        var progress = new Progress<string>(msg =>
        {
            logBuilder.AppendLine(msg);
            // Append to log box on the UI thread (Progress<T> marshals automatically)
            TxtLog.Text = logBuilder.ToString();
            TxtLog.ScrollToEnd();
        });

        HardwareDetector.HardwareInfo info;
        try
        {
            info = await HardwareDetector.DetectAsync(progress);
        }
        catch (Exception ex)
        {
            logBuilder.AppendLine($"Detection threw: {ex.Message}");
            TxtLog.Text = logBuilder.ToString();

            // Populate with safe defaults so the user can still proceed
            info = new HardwareDetector.HardwareInfo();
        }

        // ── Write results into ViewModel ──────────────────────────────────────
        _vm.ApplyHardwareInfo(info);
        _detected = true;

        // ── Populate UI ───────────────────────────────────────────────────────
        PopulateResultCard(info);
        SyncVariantCombo(_vm.State.SelectedRuntimeVariant);
        SetScanningState(false);
    }

    private void PopulateResultCard(HardwareDetector.HardwareInfo info)
    {
        TxtGpuName.Text = info.GpuName;

        TxtVram.Text = info.VramGb > 0
            ? $"{info.VramGb} GB"
            : "Not detected (assuming CPU-only)";

        TxtCuda.Text = string.IsNullOrEmpty(info.CudaVersion)
            ? (info.Vendor == "nvidia" ? "Not detected" : "N/A")
            : $"CUDA {info.CudaVersion}";

        TxtRam.Text = info.SystemRamGb > 0 ? $"{info.SystemRamGb} GB" : "Unknown";

        string variant = info.RuntimeVariant;
        TxtVariant.Text = VariantFriendlyName(variant);

        // Show the badge with the raw key so developers see what's happening
        TxtVariantBadge.Text        = variant;
        BdrVariantBadge.Visibility  = Visibility.Visible;
    }

    private void SetScanningState(bool scanning)
    {
        BdrScanning.Visibility    = scanning ? Visibility.Visible : Visibility.Collapsed;
        PbScanning.IsIndeterminate = scanning;

        if (!scanning)
            TxtScanStatus.Text = "Detection complete";
    }

    // ── Variant ComboBox ──────────────────────────────────────────────────────

    private void SyncVariantCombo(string variant)
    {
        foreach (ComboBoxItem item in CmbVariant.Items)
        {
            if (item.Tag?.ToString() == variant)
            {
                CmbVariant.SelectedItem = item;
                return;
            }
        }
        CmbVariant.SelectedIndex = 4; // fallback: cpu baseline
    }

    private void CmbVariant_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbVariant.SelectedItem is ComboBoxItem item)
        {
            string v = item.Tag?.ToString() ?? "cpu";
            _vm.State.SelectedRuntimeVariant = v;
            TxtVariant.Text      = VariantFriendlyName(v);
            TxtVariantBadge.Text = v;
        }
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    /// <summary>
    /// Always allow leaving — detection is best-effort and the user can
    /// override the variant manually with the ComboBox.
    /// </summary>
    public bool CanLeave() => true;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string VariantFriendlyName(string variant) => variant switch
    {
        "cuda12" => "CUDA 12.x (NVIDIA RTX)",
        "cuda11" => "CUDA 11.x (NVIDIA)",
        "vulkan" => "Vulkan (AMD / Intel Arc)",
        "avx2"   => "CPU with AVX2",
        "cpu"    => "CPU baseline",
        _        => variant,
    };
}
