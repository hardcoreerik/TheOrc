// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Panels;

/// <summary>
/// Sidebar panel listing all saved sessions from %APPDATA%\OrchestratorIDE\sessions\.
/// Click any session to resume it; ✕ to delete.
/// </summary>
public partial class SessionBrowserPanel : UserControl
{
    // Fires when the user picks a session to resume
    public event Action<ProjectSession>? SessionSelected;

    private readonly SessionStore _store;
    private readonly ObservableCollection<SessionVm> _items = [];

    public SessionBrowserPanel(SessionStore store)
    {
        InitializeComponent();
        _store = store;
        SessionList.ItemsSource = _items;
        Refresh();
    }

    // ── Public ────────────────────────────────────────────────────────────

    public void Refresh() => LoadSessions();

    // ── Load ──────────────────────────────────────────────────────────────

    private void LoadSessions()
    {
        _items.Clear();
        var sessions = _store.ListSessions();

        if (sessions.Count == 0)
        {
            TbEmpty.Visibility = Visibility.Visible;
            TbStatus.Text      = "No saved sessions.";
            return;
        }

        TbEmpty.Visibility = Visibility.Collapsed;
        foreach (var (id, modified, root) in sessions)
            _items.Add(new SessionVm(id, modified, root));

        TbStatus.Text = $"{sessions.Count} saved session{(sessions.Count == 1 ? "" : "s")} — click to resume";
    }

    // ── Handlers ──────────────────────────────────────────────────────────

    private void BtnRefresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void SessionItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border border || border.Tag is not Guid id) return;

        TbStatus.Text = "Loading session…";
        var session = await _store.LoadAsync(id);
        if (session == null)
        {
            TbStatus.Text = "Could not load session.";
            return;
        }

        SessionSelected?.Invoke(session);
    }

    private void BtnDeleteSession_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not Guid id) return;

        var vm = _items.FirstOrDefault(i => i.Id == id);
        var label = vm?.WorkspaceName ?? id.ToString()[..8];

        var confirm = MessageBox.Show(
            $"Delete session for:\n{label}\n\nThis cannot be undone.",
            "Delete Session",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", "sessions", $"{id}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* non-fatal */ }

        Refresh();
    }
}

// ── View model ────────────────────────────────────────────────────────────

internal sealed class SessionVm
{
    public Guid   Id            { get; }
    public string WorkspaceName { get; }
    public string WhenLabel     { get; }
    public string Detail        { get; }

    public SessionVm(Guid id, DateTime modified, string workspaceRoot)
    {
        Id            = id;
        WorkspaceName = string.IsNullOrEmpty(workspaceRoot)
            ? "(no workspace)"
            : Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
              is string n && !string.IsNullOrEmpty(n) ? n : workspaceRoot;
        WhenLabel     = FormatWhen(modified);
        Detail        = string.IsNullOrEmpty(workspaceRoot) ? "" : workspaceRoot;
    }

    private static string FormatWhen(DateTime when)
    {
        var now = DateTime.Now;
        if (when.Date == now.Date)
            return $"Today {when:HH:mm}";
        if (when.Date == now.Date.AddDays(-1))
            return $"Yesterday {when:HH:mm}";
        return when.ToString("MMM d, yyyy  HH:mm");
    }
}
