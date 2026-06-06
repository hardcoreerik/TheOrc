using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Sidebar panel showing all [agent] git checkpoints in the workspace repo.
/// Each entry has a Restore button that hard-resets to that SHA.
/// MainWindow sets WorkspaceRoot before making this panel visible.
/// </summary>
public partial class CheckpointBrowserPanel : UserControl
{
    // Fires when the user restores a checkpoint — MainWindow can refresh the editor
    public event Action<string>? CheckpointRestored;

    private readonly GitCheckpoint _git;
    private string _workspaceRoot = "";
    private readonly ObservableCollection<CheckpointVm> _items = [];

    public CheckpointBrowserPanel(GitCheckpoint git)
    {
        InitializeComponent();
        _git = git;
        CheckpointList.ItemsSource = _items;
    }

    // ── Public API ────────────────────────────────────────────────────────

    public void SetWorkspace(string root)
    {
        _workspaceRoot = root;
        _ = LoadAsync();
    }

    public void Refresh() => _ = LoadAsync();

    // ── Load ──────────────────────────────────────────────────────────────

    private async Task LoadAsync()
    {
        Dispatcher.Invoke(() =>
        {
            TbLoading.Visibility = Visibility.Visible;
            TbEmpty.Visibility   = Visibility.Collapsed;
            _items.Clear();
        });

        var checkpoints = await _git.GetCheckpointsAsync(_workspaceRoot, max: 20);

        Dispatcher.Invoke(() =>
        {
            TbLoading.Visibility = Visibility.Collapsed;

            if (checkpoints.Count == 0)
            {
                TbEmpty.Visibility = Visibility.Visible;
                TbStatus.Text = "No agent checkpoints in this workspace.";
            }
            else
            {
                TbEmpty.Visibility = Visibility.Collapsed;
                foreach (var cp in checkpoints)
                    _items.Add(new CheckpointVm(cp));
                TbStatus.Text = $"Showing {checkpoints.Count} most recent agent checkpoint{(checkpoints.Count == 1 ? "" : "s")}";
            }
        });
    }

    // ── Handlers ──────────────────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
        => _ = LoadAsync();

    private async void BtnRestore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string sha) return;
        if (string.IsNullOrEmpty(_workspaceRoot)) return;

        var vm = _items.FirstOrDefault(i => i.Sha == sha);
        var label = vm?.ShortMessage ?? sha[..8];

        var confirm = MessageBox.Show(
            $"Restore to checkpoint:\n{label}\n\n" +
            "This will HARD RESET the workspace to this commit.\n" +
            "All uncommitted changes and any later commits will be lost.\n\n" +
            "Continue?",
            "Restore Checkpoint",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        TbStatus.Text = $"Restoring to {sha[..8]}…";
        var ok = await _git.RollbackAsync(_workspaceRoot, sha);

        if (ok)
        {
            TbStatus.Text = $"✓ Restored to {sha[..8]}";
            CheckpointRestored?.Invoke(sha);
            await LoadAsync();  // Refresh list (restored commit is now HEAD)
        }
        else
        {
            TbStatus.Text = "✗ Restore failed — check the activity log.";
            MessageBox.Show(
                $"Could not restore to SHA {sha[..8]}.\n\nThe commit may no longer exist, or the repository may be locked.",
                "Restore Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}

// ── View model ────────────────────────────────────────────────────────────

internal sealed class CheckpointVm
{
    public string Sha          { get; }
    public string ShortSha     { get; }
    public string ShortMessage { get; }
    public string WhenLabel    { get; }
    public string RestoreTooltip { get; }

    public CheckpointVm(CheckpointInfo info)
    {
        Sha           = info.Sha;
        ShortSha      = info.Sha[..8];
        ShortMessage  = StripAgentPrefix(info.Message);
        WhenLabel     = FormatWhen(info.When);
        RestoreTooltip = $"Hard-reset workspace to {info.Sha[..8]}\n{info.When:yyyy-MM-dd HH:mm:ss}";
    }

    private static string StripAgentPrefix(string msg)
    {
        // "[agent] Pre-agent checkpoint — 09:14:22" → "Pre-agent checkpoint — 09:14"
        if (msg.StartsWith("[agent] ")) msg = msg[8..];
        // Trim the trailing timestamp (— HH:mm:ss) if present
        var dashIdx = msg.LastIndexOf(" — ");
        if (dashIdx > 0) msg = msg[..dashIdx];
        return msg;
    }

    private static string FormatWhen(DateTime when)
    {
        var now = DateTime.Now;
        if (when.Date == now.Date)          return $"Today {when:HH:mm}";
        if (when.Date == now.Date.AddDays(-1)) return $"Yesterday {when:HH:mm}";
        return when.ToString("MMM d, HH:mm");
    }
}
