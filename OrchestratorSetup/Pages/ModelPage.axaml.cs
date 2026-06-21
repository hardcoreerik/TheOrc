// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
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

    // Tracks which full-list entries we've already subscribed to, so Refresh() (called again
    // on re-navigation) doesn't double-subscribe and double-fire UpdateFooter per click.
    private readonly HashSet<SelectableModelEntry> _subscribed = new();

    private bool _validationFailed;

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
                BdrWorkerPartner.IsVisible  = true;
                TxtWorkerPartner.Text       = w.PartnerBadge;
                BdrWorkerPartner.Background = ParseColorOrDefault(w.PartnerBadgeBg);
                TxtWorkerPartner.Foreground = ParseColorOrDefault(w.PartnerBadgeFg);
            }
            else
            {
                BdrWorkerPartner.IsVisible = false;
            }

            RowWorker.IsVisible = true;
        }
        else
        {
            RowWorker.IsVisible = false;
        }

        // Boss row
        if (_bossEntry is { } b)
        {
            ChkBoss.IsChecked   = b.IsSelected;
            TxtBossName.Text    = b.Name;
            TxtBossVram.Text    = b.VramDisplay;
            TxtBossSize.Text    = b.SizeDisplay;
            TxtBossStars.Text   = b.StarsDisplay;
            RowBoss.IsVisible   = true;
        }
        else
        {
            RowBoss.IsVisible = false;
        }

        // Researcher row (optional — show only when available, starts unchecked)
        if (_researcherEntry is { } r)
        {
            ChkResearcher.IsChecked = r.IsSelected;
            TxtResearcherName.Text  = r.Name;
            TxtResearcherVram.Text  = r.VramDisplay;
            TxtResearcherSize.Text  = r.SizeDisplay;
            TxtResearcherStars.Text = r.StarsDisplay;
            RowResearcher.IsVisible = true;
        }
        else
        {
            RowResearcher.IsVisible = false;
        }
    }

    private static IBrush ParseColorOrDefault(string hex)
    {
        try { return new SolidColorBrush(Color.Parse(hex)); }
        catch { return Brushes.Gray; }
    }

    private void PopulateFullList()
    {
        ModelList.ItemsSource = _vm.AllSelectableModels;

        // Avalonia has no WPF-style "CheckBox.Click bubbles to a named ancestor" wiring used
        // here; subscribing to each entry's own PropertyChanged for IsSelected is simpler and
        // works the same regardless of Avalonia's exact routed-event bubbling rules for
        // ToggleButton.Click, which the original WPF page depended on.
        foreach (var entry in _vm.AllSelectableModels)
        {
            if (!_subscribed.Add(entry)) continue;
            entry.PropertyChanged += ModelEntry_PropertyChanged;
        }
    }

    private void ModelEntry_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SelectableModelEntry.IsSelected)) return;
        if (sender is not SelectableModelEntry entry) return;

        _vm.ToggleModel(entry.Id, entry.IsSelected);

        // Keep bundle checkboxes in sync if this model is one of the bundle slots
        if (entry.Id == _workerEntry?.Id)     ChkWorker.IsChecked     = entry.IsSelected;
        if (entry.Id == _bossEntry?.Id)       ChkBoss.IsChecked       = entry.IsSelected;
        if (entry.Id == _researcherEntry?.Id) ChkResearcher.IsChecked = entry.IsSelected;

        UpdateFooter();
    }

    // ── Bundle checkbox handler ───────────────────────────────────────────────

    private void BundleCheckBox_Changed(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var isChecked = cb.IsChecked == true;

        if (ReferenceEquals(cb, ChkWorker)          && _workerEntry     is not null)
            _vm.ToggleModel(_workerEntry.Id,         isChecked);
        else if (ReferenceEquals(cb, ChkBoss)       && _bossEntry       is not null)
            _vm.ToggleModel(_bossEntry.Id,            isChecked);
        else if (ReferenceEquals(cb, ChkResearcher) && _researcherEntry is not null)
            _vm.ToggleModel(_researcherEntry.Id,      isChecked);

        UpdateFooter();
    }

    // ── Footer ────────────────────────────────────────────────────────────────

    private void UpdateFooter()
    {
        var count = _vm.SelectedModelCount;
        TxtFooterCount.Text = count == 0 ? "No models selected — select at least one to continue."
                            : count == 1 ? "1 model selected"
                            :              $"{count} models selected";

        TxtFooterSize.Text = count > 0 ? $"Total download: {_vm.SelectedTotalSizeDisplay}" : "";

        if (_validationFailed && count > 0)
        {
            _validationFailed = false;
            TxtFooterCount.Foreground = ResourceBrush("TextSecondary");
        }
    }

    private IBrush ResourceBrush(string key)
        => this.TryFindResource(key, out var res) && res is IBrush brush ? brush : Brushes.Gray;

    // ── IInstallerPage ────────────────────────────────────────────────────────

    public bool CanLeave()
    {
        _vm.SyncSelectionToState();   // make sure state is current before leaving

        if (_vm.SelectedModelCount == 0)
        {
            // No WPF MessageBox in Avalonia -- flash the footer count message red instead
            // of a dialog, consistent with the inline-validation pattern used on the other
            // pages that need it (WelcomePage, InstallPathPage).
            _validationFailed = true;
            TxtFooterCount.Foreground = ResourceBrush("Error");
            TxtFooterCount.Text = "Please select at least one model before continuing.";
            return false;
        }
        return true;
    }
}
