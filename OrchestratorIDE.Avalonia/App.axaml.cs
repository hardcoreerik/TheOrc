// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE;

public partial class App : Application
{
    public override void Initialize()
    {
        // Boot secret protection before any HIVE store access.
        // DPAPI on Windows, AES-256-GCM (machine-key file) on Linux/macOS.
#if WINDOWS
        if (OperatingSystem.IsWindows())
            SecretProtection.Initialize(new DpapiSecretProtector());
        else
            SecretProtection.Initialize(new AesGcmSecretProtector(OrchestratorIDE.Daemon.MachineKey.Load()));
#else
        SecretProtection.Initialize(new AesGcmSecretProtector(OrchestratorIDE.Daemon.MachineKey.Load()));
#endif
        AvaloniaXamlLoader.Load(this);

        // macOS builds the app menu ("About …", "Hide …", "Quit") from Application.Name, which
        // otherwise reads "Avalonia Application". macOS-only so nothing changes elsewhere.
        if (OperatingSystem.IsMacOS())
        {
            Name = "TheOrc";

            // An Application-level NativeMenu becomes the app menu's items, replacing Avalonia's
            // default "About Avalonia" (Services / Hide / Quit are still added by macOS).
            var about = new NativeMenuItem("About TheOrc");
            about.Click += (_, _) => UI.Windows.AboutWindow.ShowFor(
                (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow);
            NativeMenu.SetMenu(this, new NativeMenu { about });
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();
        base.OnFrameworkInitializationCompleted();
    }
}
