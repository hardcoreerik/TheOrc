// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using System.Windows.Media;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Tests;

public partial class AutoTestWindow : Window
{
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OrchestratorIDE", "autotestlogs");

    private const int MaxLogs = 20;   // prune when over this

    private readonly CancellationTokenSource _cts = new();
    private string _workspace = "";

    // Thread-safe log buffer — avoids dispatcher priority race when saving
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logLines = new();

    public AutoTestWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(LogDir);
        Loaded += async (_, _) => await RunTestsAsync();
    }

    private async Task RunTestsAsync()
    {
        var runner = new AutoTestRunner(AppendLine, OnWorkspaceCreated);

        bool passed;
        try
        {
            passed = await runner.RunAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            AppendLine($"[FATAL] {ex.Message}");
            passed = false;
        }

        // Save log to disk
        var logPath = await SaveLogAsync(passed);

        Dispatcher.Invoke(() =>
        {
            PbProgress.IsIndeterminate = false;
            PbProgress.Value           = 100;
            PbProgress.Foreground      = passed
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));

            TbSubtitle.Text       = passed ? "All tests passed ✓" : "Tests failed ✗";
            TbSubtitle.Foreground = passed
                ? new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0xB0))
                : new SolidColorBrush(Color.FromRgb(0xF4, 0x47, 0x47));

            BtnClose.IsEnabled         = true;
            BtnClose.Content           = passed ? "Close  ✓" : "Close  ✗";
            BtnOpenWorkspace.IsEnabled = Directory.Exists(_workspace);

            if (logPath != null)
                AppendLine($"\n[log saved] {logPath}");
        });

        // CI mode — exit after short delay
        if (!Environment.UserInteractive)
        {
            await Task.Delay(2000);
            Application.Current.Dispatcher.Invoke(() =>
                Application.Current.Shutdown(passed ? 0 : 1));
        }
    }

    // ── Log persistence ────────────────────────────────────────────────────

    private async Task<string?> SaveLogAsync(bool passed)
    {
        try
        {
            var stamp    = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var status   = passed ? "PASS" : "FAIL";
            var fileName = $"autotest_{stamp}_{status}.log";
            var path     = Path.Combine(LogDir, fileName);

            // Use the concurrent queue (not TbLog.Text) to avoid the dispatcher
            // priority race where Invoke(Send) jumps ahead of pending InvokeAsync(Normal)
            var text = string.Join("\n", _logLines);
            await File.WriteAllTextAsync(path, text);

            PruneOldLogs();
            return path;
        }
        catch { return null; }
    }

    private static void PruneOldLogs()
    {
        try
        {
            var logs = Directory.GetFiles(LogDir, "autotest_*.log")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            foreach (var old in logs.Skip(MaxLogs))
                File.Delete(old);
        }
        catch { /* non-fatal */ }
    }

    // Called by the runner as soon as the temp workspace is created
    private void OnWorkspaceCreated(string path)
    {
        _workspace = path;
        Dispatcher.InvokeAsync(() =>
        {
            TbWorkspacePath.Text       = path;
            BtnOpenWorkspace.IsEnabled = true;
        });
    }

    private void AppendLine(string msg)
    {
        _logLines.Enqueue(msg);   // always captured before UI dispatch
        Dispatcher.InvokeAsync(() =>
        {
            TbLog.Text += msg + "\n";
            LogScroll.ScrollToBottom();
        });
    }

    private void BtnOpenWorkspace_Click(object sender, RoutedEventArgs e)
        => FileExplorerPanel.RevealInExplorer(_workspace);

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
        => Clipboard.SetText(TbLog.Text);

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();

    protected override void OnClosed(System.EventArgs e)
    {
        _cts.Cancel();

        // Clean up temp workspace when window is closed (not before — user may inspect it)
        if (!string.IsNullOrEmpty(_workspace) && Directory.Exists(_workspace))
        {
            try { Directory.Delete(_workspace, recursive: true); }
            catch { /* non-fatal */ }
        }

        base.OnClosed(e);
    }
}
