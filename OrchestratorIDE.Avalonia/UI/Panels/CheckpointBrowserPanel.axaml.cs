// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Panels;

public partial class CheckpointBrowserPanel : UserControl
{
    public event Action<string>? CheckpointRestored;

    /// <summary>
    /// Wired by MainWindow to show a yes/no confirmation dialog.
    /// Returns false (safe default) if not wired — nothing destructive runs.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    private readonly GitCheckpoint _git;
    private string _workspaceRoot = "";
    private readonly ObservableCollection<CheckpointVm> _items = [];

    public CheckpointBrowserPanel(GitCheckpoint git)
    {
        InitializeComponent();
        _git = git;
        CheckpointList.ItemsSource = _items;
    }

    public void SetWorkspace(string root)
    {
        _workspaceRoot = root;
        _ = LoadAsync();
    }

    public void Refresh() => _ = LoadAsync();

    private async Task LoadAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            TbLoading.IsVisible = true;
            TbEmpty.IsVisible   = false;
            _items.Clear();
        });

        var checkpoints = await _git.GetCheckpointsAsync(_workspaceRoot, max: 20);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            TbLoading.IsVisible = false;

            if (checkpoints.Count == 0)
            {
                TbEmpty.IsVisible = true;
                TbStatus.Text     = "No agent checkpoints in this workspace.";
            }
            else
            {
                TbEmpty.IsVisible = false;
                foreach (var cp in checkpoints)
                    _items.Add(new CheckpointVm(cp));
                TbStatus.Text = $"Showing {checkpoints.Count} most recent agent checkpoint{(checkpoints.Count == 1 ? "" : "s")}";
            }
        });
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e) => _ = LoadAsync();

    private async void BtnRestore_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not CheckpointVm vm) return;
        if (string.IsNullOrEmpty(_workspaceRoot)) return;

        var confirmed = ConfirmAsync != null
            && await ConfirmAsync(
                $"Restore to checkpoint:\n{vm.ShortMessage}\n\n" +
                "This will HARD RESET the workspace to this commit.\n" +
                "All uncommitted changes and any later commits will be lost.\n\nContinue?",
                "Restore Checkpoint");

        if (!confirmed) return;

        TbStatus.Text = $"Restoring to {vm.ShortSha}…";
        var ok = await _git.RollbackAsync(_workspaceRoot, vm.Sha);

        if (ok)
        {
            TbStatus.Text = $"✓ Restored to {vm.ShortSha}";
            CheckpointRestored?.Invoke(vm.Sha);
            _ = LoadAsync();
        }
        else
        {
            TbStatus.Text = "✗ Restore failed — check the activity log.";
        }
    }
}

// ── View model ────────────────────────────────────────────────────────────────

internal sealed class CheckpointVm
{
    public string Sha           { get; }
    public string ShortSha      { get; }
    public string ShortMessage  { get; }
    public string WhenLabel     { get; }
    public string RestoreTooltip { get; }

    public CheckpointVm(CheckpointInfo info)
    {
        Sha            = info.Sha;
        ShortSha       = info.Sha[..8];
        ShortMessage   = StripAgentPrefix(info.Message);
        WhenLabel      = FormatWhen(info.When);
        RestoreTooltip = $"Hard-reset workspace to {info.Sha[..8]}\n{info.When:yyyy-MM-dd HH:mm:ss}";
    }

    private static string StripAgentPrefix(string msg)
    {
        if (msg.StartsWith("[agent] ")) msg = msg[8..];
        var dashIdx = msg.LastIndexOf(" — ");
        if (dashIdx > 0) msg = msg[..dashIdx];
        return msg;
    }

    private static string FormatWhen(DateTime when)
    {
        var now = DateTime.Now;
        if (when.Date == now.Date)             return $"Today {when:HH:mm}";
        if (when.Date == now.Date.AddDays(-1)) return $"Yesterday {when:HH:mm}";
        return when.ToString("MMM d, HH:mm");
    }
}
