// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using OrchestratorIDE.Models;
using OrchestratorIDE.Trust;

namespace OrchestratorIDE.UI.Panels;

public partial class SessionBrowserPanel : UserControl
{
    public event Action<ProjectSession>? SessionSelected;

    /// <summary>
    /// Wired by MainWindow to show a yes/no confirmation dialog.
    /// Returns false (safe default) if not wired.
    /// </summary>
    public Func<string, string, Task<bool>>? ConfirmAsync { get; set; }

    private readonly SessionStore _store;
    private readonly ObservableCollection<SessionVm> _items = [];

    public SessionBrowserPanel(SessionStore store)
    {
        InitializeComponent();
        _store = store;
        SessionList.ItemsSource = _items;
        Refresh();
    }

    public void Refresh() => LoadSessions();

    private void LoadSessions()
    {
        _items.Clear();
        var sessions = _store.ListSessions();

        if (sessions.Count == 0)
        {
            TbEmpty.IsVisible = true;
            TbStatus.Text     = "No saved sessions.";
            return;
        }

        TbEmpty.IsVisible = false;
        foreach (var (id, modified, root) in sessions)
            _items.Add(new SessionVm(id, modified, root));

        TbStatus.Text = $"{sessions.Count} saved session{(sessions.Count == 1 ? "" : "s")} — click to resume";
    }

    private void BtnRefresh_Click(object? sender, RoutedEventArgs e) => Refresh();

    private async void SessionItem_Click(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        if ((sender as Control)?.DataContext is not SessionVm vm) return;

        TbStatus.Text  = "Loading session…";
        var session    = await _store.LoadAsync(vm.Id);
        if (session == null)
        {
            TbStatus.Text = "Could not load session.";
            return;
        }

        SessionSelected?.Invoke(session);
    }

    private async void BtnDeleteSession_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SessionVm vm) return;

        var confirmed = ConfirmAsync != null
            && await ConfirmAsync(
                $"Delete session for:\n{vm.WorkspaceName}\n\nThis cannot be undone.",
                "Delete Session");

        if (!confirmed) return;

        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrchestratorIDE", "sessions", $"{vm.Id}.json");
            if (File.Exists(path)) File.Delete(path);
        }
        catch { /* non-fatal */ }

        Refresh();
    }
}

// ── View model ────────────────────────────────────────────────────────────────

internal sealed class SessionVm
{
    public Guid   Id            { get; }
    public string WorkspaceName { get; }
    public string WhenLabel     { get; }
    public string Detail        { get; }

    public SessionVm(Guid id, DateTime modified, string workspaceRoot)
    {
        Id = id;
        WorkspaceName = string.IsNullOrEmpty(workspaceRoot)
            ? "(no workspace)"
            : Path.GetFileName(workspaceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
              is string n && !string.IsNullOrEmpty(n) ? n : workspaceRoot;
        WhenLabel = FormatWhen(modified);
        Detail    = string.IsNullOrEmpty(workspaceRoot) ? "" : workspaceRoot;
    }

    private static string FormatWhen(DateTime when)
    {
        var now = DateTime.Now;
        if (when.Date == now.Date)             return $"Today {when:HH:mm}";
        if (when.Date == now.Date.AddDays(-1)) return $"Yesterday {when:HH:mm}";
        return when.ToString("MMM d, yyyy  HH:mm");
    }
}
