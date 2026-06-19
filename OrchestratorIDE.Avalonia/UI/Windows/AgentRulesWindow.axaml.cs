// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.Core;
using OrchestratorIDE.Trust;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.UI.Windows;

public enum AgentRulesApplyTarget { None, WorkspaceRules, GlobalAgent }
public sealed record AgentPresetItem(AgentPresets.Preset Preset, string Name, string Description, string Content);

public partial class AgentRulesWindow : Window
{
    private readonly string? _workspaceRoot;
    private readonly RulesLoader _rules;
    private readonly bool _globalMode;
    private AgentPresetItem? _selectedPreset;
    private bool _busy;

    public AgentRulesApplyTarget AppliedTarget { get; private set; } = AgentRulesApplyTarget.None;

    public AgentRulesWindow(string? workspaceRoot, RulesLoader rules, bool globalMode)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _workspaceRoot = workspaceRoot;
        _rules = rules;
        _globalMode = globalMode;

        InitializeComponent();
        Opened += OnOpened;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        Opened -= OnOpened;
        try
        {
            TbTitle.Text = _globalMode ? "Global Agent" : "Workspace Rules";
            TbSubtitle.Text = _globalMode
                ? AgentPresets.GlobalAgentPath
                : (_workspaceRoot ?? "No workspace open");

            BtnClearGlobal.IsVisible = _globalMode;
            LstPresets.ItemsSource = AgentPresets.All
                .Select(p => new AgentPresetItem(p, $"{p.Icon}  {p.Name}", p.Description, p.Content))
                .ToList();
            UpdateStatus();
        }
        catch (Exception ex)
        {
            TbStatus.Text = $"Failed to load agent rules view: {ex.Message}";
            BtnApplyPreset.IsEnabled = false;
            BtnOpenExisting.IsEnabled = false;
            BtnOpenBlank.IsEnabled = false;
            BtnClearGlobal.IsEnabled = false;
        }
    }

    private void UpdateStatus()
    {
        if (!_globalMode && string.IsNullOrWhiteSpace(_workspaceRoot))
        {
            TbStatus.Text = "No workspace open.";
            BtnApplyPreset.IsEnabled = false;
            BtnOpenExisting.IsEnabled = false;
            BtnOpenBlank.IsEnabled = false;
            return;
        }

        if (_globalMode)
        {
            var path = AgentPresets.GlobalAgentPath;
            if (File.Exists(path))
            {
                try
                {
                    var firstLine = File.ReadAllText(path).TrimStart().Split('\n').FirstOrDefault() ?? "";
                    var name = firstLine.TrimStart('#').Trim();
                    TbStatus.Text = $"Active: {(string.IsNullOrEmpty(name) ? "Custom" : name)}";
                    BtnOpenExisting.IsEnabled = true;
                }
                catch (Exception ex)
                {
                    TbStatus.Text = $"Global agent unreadable: {ex.Message}";
                    BtnOpenExisting.IsEnabled = false;
                }
            }
            else
            {
                TbStatus.Text = "No global agent set.";
                BtnOpenExisting.IsEnabled = false;
            }

            BtnOpenBlank.Content = "Create Global Agent";
            return;
        }

        if (!string.IsNullOrEmpty(_workspaceRoot))
        {
            var existing = _rules.FindRulesFile(_workspaceRoot);
            if (existing is not null)
            {
                TbStatus.Text = $"Active: {Path.GetFileName(existing)}";
                BtnOpenExisting.IsEnabled = true;
            }
            else
            {
                TbStatus.Text = "No workspace rules file found.";
                BtnOpenExisting.IsEnabled = false;
            }
        }
        else
        {
            TbStatus.Text = "No workspace open.";
            BtnOpenExisting.IsEnabled = false;
        }

        BtnOpenBlank.Content = "Create Blank Rules";
    }

    private string TargetRoot => _globalMode
        ? Path.GetDirectoryName(AgentPresets.GlobalAgentPath) ?? AppContext.BaseDirectory
        : _workspaceRoot!;

    private string TargetPath => _globalMode
        ? AgentPresets.GlobalAgentPath
        : Path.Combine(TargetRoot, ".agent.md");

    private AgentRulesApplyTarget CurrentTarget => _globalMode
        ? AgentRulesApplyTarget.GlobalAgent
        : AgentRulesApplyTarget.WorkspaceRules;

    private bool CanUseWorkspaceTarget()
        => _globalMode || (!string.IsNullOrWhiteSpace(_workspaceRoot) && Directory.Exists(_workspaceRoot));

    private void LstPresets_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _selectedPreset = LstPresets.SelectedItem as AgentPresetItem;
        TbPreview.Text = _selectedPreset?.Content ?? "";
        BtnApplyPreset.IsEnabled = _selectedPreset is not null && CanUseWorkspaceTarget();
        TbPreviewTitle.Text = _selectedPreset is null
            ? "Preview"
            : $"Preview — {_selectedPreset.Name}";
    }

    private async void BtnApplyPreset_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (_selectedPreset is null)
                return;
            if (!CanUseWorkspaceTarget())
            {
                await SafeShowInfoAsync("No Workspace", "Open a workspace before editing workspace rules.");
                return;
            }

            if (File.Exists(TargetPath))
            {
                var confirmed = await DialogHelper.ShowYesNoAsync(
                    this,
                    "Replace Existing Rules?",
                    $"A rules file already exists:\n{TargetPath}\n\nReplace it with the \"{_selectedPreset.Name}\" preset?");
                if (!confirmed)
                    return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
            File.WriteAllText(TargetPath, _selectedPreset.Content, Encoding.UTF8);
            AppliedTarget = CurrentTarget;
            Close();
        }
        catch (Exception ex)
        {
            await SafeShowInfoAsync("Write Error", $"Failed to write rules file:\n{ex.Message}");
        }
        finally { _busy = false; }
    }

    private async void BtnOpenExisting_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!CanUseWorkspaceTarget())
            {
                await SafeShowInfoAsync("No Workspace", "Open a workspace before editing workspace rules.");
                return;
            }

            var existing = _globalMode ? TargetPath : _rules.FindRulesFile(_workspaceRoot ?? "");
            if (string.IsNullOrEmpty(existing))
                return;

            if (!File.Exists(existing))
            {
                await SafeShowInfoAsync("No Rules File", "No existing rules file was found.");
                return;
            }

            AppliedTarget = CurrentTarget;
            Close();
        }
        catch (Exception ex)
        {
            await SafeShowInfoAsync("Open Error", $"Failed to open rules file:\n{ex.Message}");
        }
        finally { _busy = false; }
    }

    private async void BtnOpenBlank_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!CanUseWorkspaceTarget())
            {
                await SafeShowInfoAsync("No Workspace", "Open a workspace before creating workspace rules.");
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(TargetPath)!);
            if (!File.Exists(TargetPath))
            {
                var content = _globalMode
                    ? "# Global Agent\n\nYou are a capable assistant.\n"
                    : RulesLoader.DefaultTemplate(Path.GetFileName(TargetRoot) ?? "project");
                File.WriteAllText(TargetPath, content, Encoding.UTF8);
            }

            AppliedTarget = CurrentTarget;
            Close();
        }
        catch (Exception ex)
        {
            await SafeShowInfoAsync("Write Error", $"Failed to create rules file:\n{ex.Message}");
        }
        finally { _busy = false; }
    }

    private async void BtnClearGlobal_Click(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_globalMode)
                return;

            var confirmed = await DialogHelper.ShowYesNoAsync(
                this,
                "Clear Global Agent",
                "Remove the global agent file?\n\nWorkspace rules will still apply.");
            if (!confirmed)
                return;

            if (File.Exists(TargetPath))
                File.Delete(TargetPath);

            AppliedTarget = CurrentTarget;
            Close();
        }
        catch (Exception ex)
        {
            await SafeShowInfoAsync("Delete Error", $"Failed to remove global agent:\n{ex.Message}");
        }
        finally { _busy = false; }
    }

    private async Task SafeShowInfoAsync(string title, string message)
    {
        try
        {
            await DialogHelper.ShowInfoAsync(this, title, message);
        }
        catch { }
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e) => Close();
}
