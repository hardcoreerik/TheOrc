// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Controls;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Layer 2: shown when the agent calls an unregistered tool.
/// The user chooses how to handle it — the choice resolves a Task&lt;string&gt;
/// that ToolRegistry feeds back to the model as the tool result.
///
/// Three outcomes
/// ──────────────
/// Auto-translate  →  rich Layer-1 error message; model self-corrects next step
/// Skip            →  "(tool skipped by user)"; model continues past the call
/// Implement it…   →  stub for Phase 6 (Roslyn hot-load tool editor)
/// </summary>
public partial class UnknownToolCard : UserControl
{
    // Fires when the user makes a choice; string = result to feed back to model
    public event Action<string>? Resolved;

    private readonly ToolCall         _call;
    private readonly IEnumerable<string> _registered;

    public UnknownToolCard(ToolCall call, IEnumerable<string> registeredTools)
    {
        InitializeComponent();
        _call       = call;
        _registered = registeredTools;
        Populate();
    }

    // ── Populate UI ───────────────────────────────────────────────────────

    private void Populate()
    {
        TbToolName.Text = _call.Name;

        // Call preview line  e.g.  create_project(project_type, project_name)
        var paramNames = string.Join(", ", _call.Arguments.Keys);
        TbCallPreview.Text = $"{_call.Name}({paramNames})";

        // Arguments block
        if (_call.Arguments.Count == 0)
        {
            ArgsBox.Visibility      = Visibility.Collapsed;
            TbArgsHeader.Visibility = Visibility.Collapsed;
        }
        else
        {
            TbArgs.Text = string.Join("\n", _call.Arguments.Select(kv =>
            {
                var val = kv.Value?.ToString() ?? "(null)";
                if (val.Length > 120) val = val[..120] + "…";
                return $"{kv.Key} = \"{val}\"";
            }));
        }
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void BtnAutoTranslate_Click(object sender, RoutedEventArgs e)
    {
        // Layer 1 message — model reads it and uses a real tool on next step
        var msg = ToolRegistry.BuildRichNotFoundMessage(_call, _registered);
        Resolved?.Invoke(msg);
    }

    private void BtnSkip_Click(object sender, RoutedEventArgs e)
        => Resolved?.Invoke($"(tool '{_call.Name}' was skipped by the user — continue with available tools)");

    private void BtnImplement_Click(object sender, RoutedEventArgs e)
    {
        // Phase 6 stub — show a placeholder and fall back to auto-translate
        MessageBox.Show(
            "The tool editor (Roslyn hot-load) is coming in Phase 6.\n\n" +
            "For now the agent will receive the auto-translate message and self-correct.",
            "Coming in Phase 6",
            MessageBoxButton.OK,
            MessageBoxImage.Information);

        var msg = ToolRegistry.BuildRichNotFoundMessage(_call, _registered);
        Resolved?.Invoke(msg);
    }
}
