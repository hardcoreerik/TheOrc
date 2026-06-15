// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OrchestratorSetup.Models;

/// <summary>
/// View-model wrapper around <see cref="ModelEntry"/> that adds per-row selection state
/// and an optional role label for display in the multi-select model list.
/// </summary>
public class SelectableModelEntry : INotifyPropertyChanged
{
    public ModelEntry Model { get; }

    public SelectableModelEntry(ModelEntry model) => Model = model;

    // ── Selection ─────────────────────────────────────────────────────────────

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; OnPropertyChanged(); }
    }

    // ── Role label (set by the bundle recommendation logic) ───────────────────

    private string? _roleLabel;

    /// <summary>
    /// Human-readable role assigned by the bundle recommender,
    /// e.g. "Worker · Coder" or "Boss · Orchestrator".
    /// Null / empty when this model is user-selected from the full list without a role.
    /// </summary>
    public string? RoleLabel
    {
        get => _roleLabel;
        set { _roleLabel = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRoleLabel)); }
    }

    public bool HasRoleLabel => !string.IsNullOrEmpty(_roleLabel);

    // ── Forwarded ModelEntry properties for XAML binding ─────────────────────

    public string  Id           => Model.Id;
    public string  Name         => Model.Name;
    public string  Description  => Model.Description;
    public string  VramDisplay  => Model.VramDisplay;
    public string  SizeDisplay  => Model.SizeDisplay;
    public string  StarsDisplay => Model.StarsDisplay;
    public bool    HasPartnerBadge => Model.HasPartnerBadge;
    public string? PartnerBadge   => Model.PartnerBadge;
    public string  PartnerBadgeBg => Model.PartnerBadgeBg;
    public string  PartnerBadgeFg => Model.PartnerBadgeFg;
    public bool    OllamaOnly  => Model.OllamaOnly;
    public bool    SwarmCapable => Model.SwarmCapable;

    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
