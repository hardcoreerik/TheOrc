// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Lets the user pick an AgentPreset and write it as the workspace .agent.md,
/// or edit an existing rules file.
/// </summary>
public partial class WorkspaceRulesDialog : System.Windows.Window
{
    private readonly string?      _workspaceRoot;
    private readonly RulesLoader  _rules;
    private AgentPresets.Preset?  _selectedPreset;

    public WorkspaceRulesDialog(string? workspaceRoot, RulesLoader rules)
    {
        InitializeComponent();
        _workspaceRoot = workspaceRoot;
        _rules         = rules;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Workspace path label
        if (!string.IsNullOrEmpty(_workspaceRoot))
        {
            TbWorkspacePath.Text = _workspaceRoot;

            var existing = _rules.FindRulesFile(_workspaceRoot);
            if (existing != null)
            {
                TbCurrentStatus.Text = $"Active: {Path.GetFileName(existing)}";
                BtnEditExisting.Visibility = System.Windows.Visibility.Visible;
            }
            else
            {
                TbCurrentStatus.Text = "No rules file found — pick a preset or start blank";
                TbCurrentStatus.Foreground = System.Windows.Media.Brushes.Gray;
            }
        }
        else
        {
            TbWorkspacePath.Text = "No workspace open — rules will apply globally";
        }

        // Populate preset list
        foreach (var preset in AgentPresets.All)
        {
            PresetList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{preset.Icon}  {preset.Name}",
                Tag     = preset
            });
        }
    }

    private void PresetList_SelectionChanged(object sender,
        System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PresetList.SelectedItem is System.Windows.Controls.ListBoxItem item
            && item.Tag is AgentPresets.Preset preset)
        {
            _selectedPreset   = preset;
            PreviewBox.Text   = preset.Content;
            BtnApply.IsEnabled = true;
        }
    }

    private void BtnApply_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedPreset is null) return;

        var destRoot = _workspaceRoot ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var destPath = Path.Combine(destRoot, ".agent.md");

        // Confirm overwrite if something already exists
        var existing = _rules.FindRulesFile(destRoot);
        if (existing != null)
        {
            var answer = System.Windows.MessageBox.Show(
                $"A rules file already exists:\n{existing}\n\nReplace it with the \"{_selectedPreset.Name}\" preset?",
                "Replace existing rules?",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            if (answer != System.Windows.MessageBoxResult.Yes) return;
        }

        try
        {
            File.WriteAllText(destPath, _selectedPreset.Content);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to write .agent.md:\n{ex.Message}",
                "Write Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void BtnEditExisting_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Signal caller to open the existing file in the editor
        DialogResult = true;
    }

    private void BtnOpenBlank_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var destRoot = _workspaceRoot ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile);
        var destPath = Path.Combine(destRoot, ".agent.md");

        if (!File.Exists(destPath))
        {
            var projectName = Path.GetFileName(destRoot) ?? "project";
            try { File.WriteAllText(destPath, RulesLoader.DefaultTemplate(projectName)); }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to create .agent.md:\n{ex.Message}",
                    "Write Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
                return;
            }
        }
        DialogResult = true;
    }

    private void BtnCancel_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogResult = false;
}
