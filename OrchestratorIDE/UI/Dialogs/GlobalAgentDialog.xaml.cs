// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Lets the user pick a global agent preset that applies to all workspaces,
/// or edit the global_agent.md file directly.
/// </summary>
public partial class GlobalAgentDialog : System.Windows.Window
{
    private AgentPresets.Preset? _selectedPreset;

    /// <summary>Name of the preset that was applied (or "Custom" if edited manually).</summary>
    public string SelectedPresetName { get; private set; } = "";

    public GlobalAgentDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        // Show current global agent status
        var path = AgentPresets.GlobalAgentPath;
        if (File.Exists(path))
        {
            var firstLine = File.ReadAllText(path).TrimStart().Split('\n').FirstOrDefault() ?? "";
            var name      = firstLine.TrimStart('#').Trim();
            TbCurrentAgent.Text = $"Active: {(string.IsNullOrEmpty(name) ? "Custom" : name)}";
        }
        else
        {
            TbCurrentAgent.Text     = "No global agent set";
            TbCurrentAgent.Foreground = System.Windows.Media.Brushes.Gray;
        }

        // Populate presets
        foreach (var preset in AgentPresets.All)
        {
            PresetList.Items.Add(new System.Windows.Controls.ListBoxItem
            {
                Content = $"{preset.Icon}  {preset.Name}",
                ToolTip = preset.Description,
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
            _selectedPreset     = preset;
            PreviewBox.Text     = preset.Content;
            BtnApply.IsEnabled  = true;
        }
    }

    private void BtnApply_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_selectedPreset is null) return;

        try
        {
            var dir = Path.GetDirectoryName(AgentPresets.GlobalAgentPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(AgentPresets.GlobalAgentPath, _selectedPreset.Content);
            SelectedPresetName = _selectedPreset.Name;
            DialogResult       = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to write global agent:\n{ex.Message}",
                "Write Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
    }

    private void BtnEdit_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // Ensure the file exists with a blank template
        var path = AgentPresets.GlobalAgentPath;
        if (!File.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "# Global Agent\n\nYou are a capable assistant.\n");
            }
            catch { /* ignore */ }
        }

        // Open in default editor (Notepad as fallback)
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName        = path,
                UseShellExecute = true
            });
        }
        catch { /* ignore — user can find the file */ }

        SelectedPresetName = "Custom";
        DialogResult       = true;
    }

    private void BtnClear_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var answer = System.Windows.MessageBox.Show(
            "Remove the global agent file?\n\nWorkspace rules will still apply.",
            "Clear Global Agent",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Question);

        if (answer != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            if (File.Exists(AgentPresets.GlobalAgentPath))
                File.Delete(AgentPresets.GlobalAgentPath);
            SelectedPresetName = "";
            DialogResult       = true;
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Failed to remove file:\n{ex.Message}",
                "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private void BtnCancel_Click(object sender, System.Windows.RoutedEventArgs e)
        => DialogResult = false;
}
