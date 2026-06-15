// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Dialogs;

/// <summary>
/// Modal warning dialog shown when a tool attempts to access a path outside
/// the workspace sandbox.  The user can allow the single operation or deny it.
///
/// Returns <c>true</c> (DialogResult) when "Allow Once" is clicked.
/// Returns <c>false</c> / null when "Deny" or Escape is pressed.
/// </summary>
public partial class SandboxBypassDialog : Window
{
    /// <param name="toolName">Name of the tool that triggered the escape (e.g. "write_file").</param>
    /// <param name="escapedPath">The fully-resolved target path outside the sandbox.</param>
    /// <param name="sandboxRoot">The workspace root (safe zone).</param>
    /// <param name="agentLabel">Human-readable agent identifier shown to the user.</param>
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

    private void BtnAllow_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnDeny_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
