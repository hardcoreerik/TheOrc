// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class ModelPage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;

    // The three bundle slots (null if model not available for that role)
    private SelectableModelEntry? _workerEntry;
    private SelectableModelEntry? _bossEntry;
    private SelectableModelEntry? _researcherEntry;

    public ModelPage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    // ── Page init ──────────────────────────────────────────────────────────────

    private void Refresh()
    {
        _vm.UpdateRecommendedModel();   // ensures hardware-matched recommendation + bundle defaults

        PopulateBundleRows();
        PopulateFullList();
        UpdateFooter();
    }

    // ── Bundle rows ───────────────────────────────────────────────────────────

    private void PopulateBundleRows()
    {
        // Find the three bundle slots by role label
        _workerEntry     = _vm.AllSelectableModels.FirstOrDefault(
                               e => e.RoleLabel != null && e.RoleLabel.Contains("Worker"));
        _bossEntry       = _vm.AllSelectableModels.FirstOrDefault(
                               e => e.RoleLabel != null && e.RoleLabel.Contains("Boss"));
        _researcherEntry = _vm.AllSelectableModels.FirstOrDefault(
                               e => e.RoleLabel != null && e.RoleLabel.Contains("Researcher"));

        // Hardware summary chip next to "SWARM BUNDLE"
        var vram = _vm.State.DetectedVramGb;
        TxtBundleHardwareSummary.Text =
            vram > 0 ? $"Matched for your {_vm.State.DetectedGpuName}  ({vram} GB VRAM)"
                     : "Hardware-matched recommendation";

        // Worker row
        if (_workerEntry is { } w)
        {
            ChkWorker.IsChecked = w.IsSelected;
            TxtWorkerName.Text  = w.Name;
            TxtWorkerVram.Text  = w.VramDisplay;
            TxtWorkerSize.Text  = w.SizeDisplay;
            TxtWorkerStars.Text = w.StarsDisplay;

            if (w.HasPartnerBadge)
            {
                BdrWorkerPartner.Visibility  = Visibility.Visible;
                TxtWorkerPartner.Text        = w.PartnerBadge;
                BdrWorkerPartner.Background  = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(w.PartnerBadgeBg));
                TxtWorkerPartner.Foreground  = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(w.PartnerBadgeFg));
            }
            else
            {
                BdrWorkerPartner.Visibility = Visibility.Collapsed;
            }

            RowWorker.Visibility = Visibility.Visible;
        }
        else
        {
            RowWorker.Visibility = Visibility.Collapsed;
        }

        // Boss row
        if (_bossEntry is { } b)
        {
            ChkBoss.IsChecked = b.IsSelected;
            TxtBossName.Text  = b.Name;
            TxtBossVram.Text  = b.VramDisplay;
            TxtBossSize.Text  = b.SizeDisplay;
            TxtBossStars.Text = b.StarsDisplay;
            RowBoss.Visibility = Visibility.Visible;
        }
        else
        {
            RowBoss.Visibility = Visibility.Collapsed;
        }

        // Researcher row (optional — show only when available, starts unchecked)
        if (_researcherEntry is { } r)
        {
            ChkResearcher.IsChecked     = r.IsSelected;
            TxtResearcherName.Text      = r.Name;
            TxtResearcherVram.Text      = r.VramDisplay;
            TxtResearcherSize.Text      = r.SizeDisplay;
            TxtResearcherStars.Text     = r.StarsDisplay;
            RowResearcher.Visibility    = Visibility.Visible;
        }
        else
        {
            RowResearcher.Visibility = Visibility.Collapsed;
        }
    }

    private void PopulateFullList()
    {
        ModelList.ItemsSource = _vm.AllSelectableModels;
    }

    // ── Bundle checkbox handler ───────────────────────────────────────────────

    private void BundleCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var isChecked = cb.IsChecked == true;

        if (ReferenceEquals(cb, ChkWorker)     && _workerEntry     is not null)
            _vm.ToggleModel(_workerEntry.Id,     isChecked);
        else if (ReferenceEquals(cb, ChkBoss)  && _bossEntry       is not null)
            _vm.ToggleModel(_bossEntry.Id,       isChecked);
        else if (ReferenceEquals(cb, ChkResearcher) && _researcherEntry is not null)
            _vm.ToggleModel(_researcherEntry.Id, isChecked);

        UpdateFooter();
    }

    // ── Full-list checkbox handler (bubbled up from DataTemplate items) ───────

    private void ModelListCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is CheckBox cb && cb.Tag is string id)
        {
            _vm.ToggleModel(id, cb.IsChecked == true);

            // Keep bundle checkboxes in sync if this model is one of the bundle slots
            if (id == _workerEntry?.Id)     ChkWorker.IsChecked     = cb.IsChecked;
            if (id == _bossEntry?.Id)       ChkBoss.IsChecked       = cb.IsChecked;
            if (id == _researcherEntry?.Id) ChkResearcher.IsChecked = cb.IsChecked;

            UpdateFooter();
        }
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void UpdateFooter()
    {
        var count = _vm.SelectedModelCount;
        TxtFooterCount.Text = count == 0 ? "No models selected — select at least one to continue."
                            : count == 1 ? "1 model selected"
                            :              $"{count} models selected";

        TxtFooterSize.Text = count > 0 ? $"Total download: {_vm.SelectedTotalSizeDisplay}" : "";
    }

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave()
    {
        _vm.SyncSelectionToState();   // make sure state is current before leaving

        if (_vm.SelectedModelCount == 0)
        {
            MessageBox.Show(
                "Please select at least one model before continuing.",
                "Model Required",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }
}
