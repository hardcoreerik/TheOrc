// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Windows;
using OrchestratorIDE.Tests;

namespace OrchestratorIDE;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--autotest"))
        {
            // Headless integration test mode — show test window, exit when done
            new AutoTestWindow().Show();
        }
        else if (e.Args.Contains("--tool-probe"))
        {
            // Tool call capability probe mode.
            // Usage: OrchestratorIDE.exe --tool-probe [--model <name>]
            //
            // Shows the ToolCallTestWindow which runs probes and saves profiles,
            // then auto-closes after completion.
            var modelArg = GetArgValue(e.Args, "--model");
            var settings = Core.AppSettings.Load();
            var win      = new ToolCallTestWindow(settings);
            win.Show();
            _ = win.RunHeadlessAsync(modelArg);
        }
        else
        {
            // Normal launch
            new MainWindow().Show();
        }
    }

    private static string? GetArgValue(string[] args, string key)
    {
        var idx = Array.IndexOf(args, key);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
