// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Modal warning shown when a tool tries to access a path outside the workspace sandbox.
/// </summary>
public partial class SandboxBypassDialog : Window
{
    public SandboxBypassDialog(
        string toolName,
        string escapedPath,
        string sandboxRoot,
        string agentLabel)
    {
        InitializeComponent();

        TbSubtitle.Text    = PathSandbox.EscapeLabel(toolName, escapedPath);
        TbTool.Text        = toolName;
        TbAgent.Text       = agentLabel;
        TbSandboxRoot.Text = sandboxRoot;
        TbTargetPath.Text  = escapedPath;
    }

    public static async Task<bool> ShowAsync(
        Window owner,
        string toolName,
        string escapedPath,
        string sandboxRoot,
        string agentLabel,
        CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return false;

        var dlg = new SandboxBypassDialog(toolName, escapedPath, sandboxRoot, agentLabel);
        using var _ = ct.Register(() => Dispatcher.UIThread.Post(() => dlg.Close(false)));
        return await dlg.ShowDialog<bool>(owner);
    }

    private void BtnAllow_Click(object? sender, RoutedEventArgs e) => Close(true);

    private void BtnDeny_Click(object? sender, RoutedEventArgs e) => Close(false);
}
