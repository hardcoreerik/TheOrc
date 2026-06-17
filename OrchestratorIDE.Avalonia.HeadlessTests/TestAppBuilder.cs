// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia;
using Avalonia.Headless;
using OrchestratorIDE;
using OrchestratorIDE.Avalonia.HeadlessTests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Boots the real <see cref="App"/> under Avalonia's headless platform so tests
/// run with the full application resource dictionary (brand colours, styles) but
/// no display. <see cref="App.OnFrameworkInitializationCompleted"/> only creates
/// MainWindow under a classic-desktop lifetime, so the headless session does not
/// spin up the IDE shell — controls are exercised in isolation.
/// </summary>
public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
                     .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
