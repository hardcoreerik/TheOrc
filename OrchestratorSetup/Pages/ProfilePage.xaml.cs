// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OrchestratorSetup.Models;
using OrchestratorSetup.ViewModels;

namespace OrchestratorSetup.Pages;

public partial class ProfilePage : UserControl, IInstallerPage
{
    private readonly InstallerViewModel _vm;
    private string _selectedId = "web";

    public ProfilePage(InstallerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        Loaded += (_, _) => BuildCards();
    }

    // ── Card builder ──────────────────────────────────────────────────────────

    private void BuildCards()
    {
        ProfileGrid.Children.Clear();
        _selectedId = _vm.State.SelectedProfileId;

        foreach (var profile in CodingProfile.All)
        {
            var card = MakeCard(profile);
            ProfileGrid.Children.Add(card);
        }

        UpdateSelectionVisuals();
        UpdateSelectedLabel();
    }

    private Border MakeCard(CodingProfile profile)
    {
        var card = new Border
        {
            Tag   = profile.Id,
            Style = (Style)FindResource("ProfileCardBorder"),
            Margin = new Thickness(0, 0, 8, 8),
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(card, $"Profile.{profile.Id}");

        var sp = new StackPanel();

        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        header.Children.Add(new TextBlock { Text = profile.Emoji, FontSize = 22, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(new TextBlock
        {
            Text             = profile.Name,
            FontFamily       = new FontFamily("Segoe UI"),
            FontSize         = 13,
            FontWeight       = FontWeights.SemiBold,
            Foreground       = (SolidColorBrush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        sp.Children.Add(header);
        sp.Children.Add(new TextBlock
        {
            Text         = profile.Description,
            FontFamily   = new FontFamily("Segoe UI"),
            FontSize      = 11,
            Foreground   = (SolidColorBrush)FindResource("TextSecondary"),
            TextWrapping = TextWrapping.Wrap,
        });

        card.Child = sp;

        card.MouseEnter  += (_, _) => { if ((string)card.Tag != _selectedId) card.Background = (SolidColorBrush)FindResource("BgCardHover"); };
        card.MouseLeave  += (_, _) => { if ((string)card.Tag != _selectedId) card.Background = (SolidColorBrush)FindResource("BgCard"); };
        card.MouseLeftButtonUp += (_, _) => SelectProfile((string)card.Tag);

        return card;
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    private void SelectProfile(string id)
    {
        _selectedId = id;
        _vm.State.SelectedProfileId = id;
        _vm.UpdateRecommendedModel();
        UpdateSelectionVisuals();
        UpdateSelectedLabel();
    }

    private void UpdateSelectionVisuals()
    {
        foreach (Border card in ProfileGrid.Children.OfType<Border>())
        {
            var isSelected = (string)card.Tag == _selectedId;
            card.Background   = (SolidColorBrush)FindResource(isSelected ? "BgCardSelected" : "BgCard");
            card.BorderBrush  = (SolidColorBrush)FindResource(isSelected ? "BorderAccent" : "BorderSubtle");
        }
    }

    private void UpdateSelectedLabel()
    {
        var profile = CodingProfile.All.FirstOrDefault(p => p.Id == _selectedId);
        TxtSelected.Text = profile is not null
            ? $"Selected: {profile.Emoji} {profile.Name}"
            : "";
    }

    // ── Validation ────────────────────────────────────────────────────────────

    public bool CanLeave() => true;
}
