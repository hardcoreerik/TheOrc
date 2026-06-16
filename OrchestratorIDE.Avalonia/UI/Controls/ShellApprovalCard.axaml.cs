// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Controls;

public partial class ShellApprovalCard : UserControl
{
    public event Action<bool>? Resolved;

    public ShellApprovalCard(ToolCall call)
    {
        InitializeComponent();
        Populate(call);
    }

    private void Populate(ToolCall call)
    {
        TbToolName.Text = call.Name;

        var rows = new ObservableCollection<ArgRow>();

        var argOrder = call.Arguments.Keys
            .OrderBy(k => k == "command" ? 0 : k == "content" ? 99 : 1)
            .ToList();

        foreach (var key in argOrder)
        {
            var raw = call.Arguments[key]?.ToString() ?? "(null)";
            var val = raw.Length > 5000
                ? raw[..5000] + $"\n…[{raw.Length - 5000:N0} more chars — see agent log for full value]"
                : raw;

            rows.Add(new ArgRow
            {
                Label = key == "command" ? "Command:" :
                        key == "path"    ? "Path:"    :
                        $"{key}:",
                Value = val
            });
        }

        ArgsList.ItemsSource = rows;

        var reason = call.ExplainWhy
            ?? (call.Arguments.TryGetValue("reason", out var r) ? r?.ToString() : null);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            TbReason.Text         = reason;
            ReasonPanel.IsVisible = true;
        }
    }

    private void BtnApprove_Click(object? sender, RoutedEventArgs e)
        => Resolved?.Invoke(true);

    private void BtnReject_Click(object? sender, RoutedEventArgs e)
        => Resolved?.Invoke(false);
}

internal sealed class ArgRow
{
    public required string Label { get; init; }
    public required string Value { get; init; }
}
