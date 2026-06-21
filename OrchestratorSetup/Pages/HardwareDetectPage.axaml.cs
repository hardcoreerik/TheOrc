// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Avalonia.Controls;
using OrchestratorSetup.Services;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

/// <summary>
/// Hardware detection wizard page.
/// Runs <see cref="PlatformInstaller.Current"/>'s <c>DetectHardwareAsync</c> (the Windows
/// implementation delegates straight through to <see cref="HardwareDetector.DetectAsync"/>;
/// see INSTALLER_REVAMP_SPEC.md §7 Phase 2) on a background thread, streams log messages
/// into the collapsible log box, then populates the result grid and writes detected values
/// into <see cref="InstallerViewModel"/>.
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
            // Append to log box on the UI thread (Progress<T> marshals automatically).
            // Avalonia's TextBox has no ScrollToEnd (WPF-only) -- setting CaretIndex to the
            // end is the standard Avalonia idiom that drives the same auto-scroll behavior.
            TxtLog.Text = logBuilder.ToString();
            TxtLog.CaretIndex = TxtLog.Text.Length;
        });

        HardwareDetector.HardwareInfo info;
        try
        {
            // INSTALLER_REVAMP_SPEC.md §7 Phase 2 -- goes through IPlatformInstaller now,
            // not the static HardwareDetector class directly, though the Windows
            // implementation just delegates straight through to it unchanged.
            info = await PlatformInstaller.Current.DetectHardwareAsync(progress, CancellationToken.None);
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
        TxtVariantBadge.Text     = variant;
        BdrVariantBadge.IsVisible = true;
    }

    private void SetScanningState(bool scanning)
    {
        BdrScanning.IsVisible      = scanning;
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

    private void CmbVariant_SelectionChanged(object? sender, SelectionChangedEventArgs e)
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
