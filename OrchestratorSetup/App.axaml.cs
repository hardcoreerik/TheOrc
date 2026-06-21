// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace OrchestratorSetup;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Catastrophic startup-path failures (before any window can show its own dialog)
            // go to stderr + a non-zero exit rather than a WPF-style MessageBox -- Avalonia has
            // no built-in equivalent, and per-page validation already covers normal error UX
            // (LicensePage's "must accept" message, InstallPathPage's path validation, etc.).
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                Console.Error.WriteLine(
                    $"OrchestratorSetup crashed: {(e.ExceptionObject as Exception)?.Message}\n" +
                    "Please report this at https://github.com/hardcoreerik/TheOrc/issues");

                // AppDomain.UnhandledException is observational, not preventable -- the CLR
                // terminates the process after this handler returns regardless. Explicit exit
                // code 1 restores parity with the old WPF path's Shutdown(1) (grok review
                // MINOR, 2026-06-21) rather than relying on whatever default exit code the
                // runtime happens to use for an unhandled exception.
                Environment.Exit(1);
            };

            // Explicit (matches Avalonia's own default for this lifetime, but stated rather
            // than implied): closing MainWindow exits the app. Old WPF's --uninstall path was
            // win.ShowDialog() (blocking) then an explicit Shutdown(0) -- ShowDialog has no
            // Avalonia equivalent for a desktop main window, so this relies on that default
            // close-triggers-exit behavior instead (grok review MINOR, 2026-06-21).
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            bool isUninstall = Array.IndexOf(desktop.Args ?? [], "--uninstall") >= 0;
            desktop.MainWindow = isUninstall ? new UninstallWindow() : new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
