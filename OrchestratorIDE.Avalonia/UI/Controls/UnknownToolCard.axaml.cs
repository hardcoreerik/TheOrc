// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.Core;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Controls;

public partial class UnknownToolCard : UserControl
{
    public event Action<string>? Resolved;

    private readonly ToolCall            _call;
    private readonly IEnumerable<string> _registered;

    public UnknownToolCard(ToolCall call, IEnumerable<string> registeredTools)
    {
        InitializeComponent();
        _call       = call;
        _registered = registeredTools;
        Populate();
    }

    private void Populate()
    {
        TbToolName.Text = _call.Name;

        var paramNames = string.Join(", ", _call.Arguments.Keys);
        TbCallPreview.Text = $"{_call.Name}({paramNames})";

        if (_call.Arguments.Count == 0)
        {
            ArgsBox.IsVisible       = false;
            TbArgsHeader.IsVisible  = false;
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

    private void BtnAutoTranslate_Click(object? sender, RoutedEventArgs e)
    {
        var msg = ToolRegistry.BuildRichNotFoundMessage(_call, _registered);
        Resolved?.Invoke(msg);
    }

    private void BtnSkip_Click(object? sender, RoutedEventArgs e)
        => Resolved?.Invoke($"(tool '{_call.Name}' was skipped by the user — continue with available tools)");

    private void BtnImplement_Click(object? sender, RoutedEventArgs e)
    {
        // Phase 6: tool editor (Roslyn hot-load). Fall back to auto-translate.
        var msg = ToolRegistry.BuildRichNotFoundMessage(_call, _registered);
        Resolved?.Invoke(msg);
    }
}
