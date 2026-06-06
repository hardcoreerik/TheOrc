using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UI.Controls;

/// <summary>
/// Inline approval card for shell commands and other non-write_file tool calls.
/// Shown in the diff-panel slot — the same location as DiffViewer and UnknownToolCard.
///
/// The Resolved event fires with true (approved) or false (rejected).
/// MainWindow wires this to the ApprovalQueue to resume the agent loop.
/// </summary>
public partial class ShellApprovalCard : UserControl
{
    /// Fires with true=Approved, false=Rejected
    public event Action<bool>? Resolved;

    public ShellApprovalCard(ToolCall call)
    {
        InitializeComponent();
        Populate(call);
    }

    // ── Populate UI ───────────────────────────────────────────────────────

    private void Populate(ToolCall call)
    {
        TbToolName.Text = call.Name;

        // Build argument rows — each becomes a labeled code block
        var rows = new ObservableCollection<ArgRow>();

        // Special-case: "command" argument gets a prominent label
        var argOrder = call.Arguments.Keys
            .OrderBy(k => k == "command" ? 0 : k == "content" ? 99 : 1)
            .ToList();

        foreach (var key in argOrder)
        {
            var val = call.Arguments[key]?.ToString() ?? "(null)";
            // Truncate very long content (e.g. file writes) — shouldn't reach here but be safe
            if (val.Length > 2000) val = val[..2000] + "\n…(truncated)";

            rows.Add(new ArgRow
            {
                Label = key == "command" ? "Command:" :
                        key == "path"    ? "Path:"    :
                        $"{key}:",
                Value = val
            });
        }

        ArgsList.ItemsSource = rows;

        // Reason / ExplainWhy
        var reason = call.ExplainWhy
            ?? (call.Arguments.TryGetValue("reason", out var r) ? r?.ToString() : null);

        if (!string.IsNullOrWhiteSpace(reason))
        {
            TbReason.Text         = reason;
            ReasonPanel.Visibility = Visibility.Visible;
        }
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void BtnApprove_Click(object sender, RoutedEventArgs e)
        => Resolved?.Invoke(true);

    private void BtnReject_Click(object sender, RoutedEventArgs e)
        => Resolved?.Invoke(false);

    // ── Row model ─────────────────────────────────────────────────────────

    private sealed class ArgRow
    {
        public required string Label { get; init; }
        public required string Value { get; init; }
    }
}
