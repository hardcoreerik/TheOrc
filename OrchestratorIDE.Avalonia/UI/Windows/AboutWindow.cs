// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace OrchestratorIDE.UI.Windows;

/// <summary>
/// Small "About TheOrc" window for the macOS app menu, which otherwise only offers Avalonia's
/// own "About Avalonia". Code-only (no XAML): name, version, build, license, repository.
/// </summary>
internal sealed class AboutWindow : Window
{
    internal const string RepositoryUrl = "https://github.com/hardcoreerik/TheOrc";

    public AboutWindow()
    {
        var (version, build) = ReadVersion(typeof(AboutWindow).Assembly);

        Title = "About TheOrc";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Center, MinWidth = 80, IsDefault = true };
        ok.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 6,
            Children =
            {
                new TextBlock { Text = "TheOrc", FontSize = 22, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Orchestrator IDE", HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 },
                new TextBlock { Text = $"Version {version}", HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                new TextBlock { Text = $"Build {build}", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Opacity = 0.7 },
                new TextBlock { Text = "AGPL-3.0-or-later", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11, Opacity = 0.7 },
                new SelectableTextBlock { Text = RepositoryUrl, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 11 },
                new Border { Height = 8 },
                ok,
            },
        };
    }

    /// <summary>
    /// Splits the informational version ("1.13.0+abc1234") into the version and the build
    /// suffix, the same way the status bar's build stamp does.
    /// </summary>
    internal static (string Version, string Build) ReadVersion(Assembly assembly)
    {
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        var parts = info.Split('+', 2);
        var version = parts[0].Length > 0 ? parts[0] : "?";
        var build = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "no-git";
        return (version, build);
    }

    /// <summary>Shows the window, centred on <paramref name="owner"/> when there is one.</summary>
    public static void ShowFor(Window? owner)
    {
        var window = new AboutWindow();
        if (owner is not null)
            window.ShowDialog(owner);
        else
            window.Show();
    }
}
