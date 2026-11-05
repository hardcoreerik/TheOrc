// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;

namespace OrchestratorIDE;

internal static class Program
{
    // Avalonia entry point — must not use any Avalonia types before AppBuilder runs.
    [STAThread]
    public static void Main(string[] args)
    {
        // --tool-probe (ToolCallTestWindow's retired CLI mode) and --autotest
        // (AutoTestWindow's headless test mode) both lost their implementation
        // when WPF was deleted (2026-06-20) — see docs/SPONSOR_TEST_LAB.md. Fail
        // closed instead of falling through to a normal GUI launch, in case any
        // external script still passes either flag.
        if (Array.IndexOf(args, "--tool-probe") >= 0)
        {
            Console.Error.WriteLine("--tool-probe was retired with ToolCallTestWindow (see docs/SPONSOR_TEST_LAB.md). It is no longer supported.");
            Environment.Exit(1);
            return;
        }

        if (Array.IndexOf(args, "--autotest") >= 0)
        {
            Console.Error.WriteLine("--autotest was retired with AutoTestWindow (WPF deletion, 2026-06-20). It is no longer supported.");
            Environment.Exit(1);
            return;
        }

        // Persistent log file (see AppLog): activity, crashes and llama.cpp errors.
        Core.Diagnostics.AppLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Core.Diagnostics.AppLog.Exception("Crash", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()),
                $"Unhandled exception (terminating: {e.IsTerminating})");
        TaskScheduler.UnobservedTaskException += (_, e) =>
            Core.Diagnostics.AppLog.Exception("Background task", e.Exception, "Unobserved task exception");

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Core.Diagnostics.AppLog.Shutdown();
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
