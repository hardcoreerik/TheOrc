// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

[TestFixture]
public sealed class T22_ModelManagementWindowTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void Model_downloader_window_opens_with_search_surface()
    {
        var settings = new AppSettings
        {
            ModelStoragePath = Path.Combine(Path.GetTempPath(), "theorc-headless-models"),
        };
        var window = new ModelDownloaderWindow(
            settings,
            probeHardwareAsync: () => Task.FromResult<(string Summary, int VramGb)>(("GPU: Headless Test (8 GB VRAM)", 8)),
            verifyCuratedAsync: () => Task.FromResult(new List<string>()));

        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(window.IsVisible, Is.True);
                Assert.That(Required<TextBox>(window, "TxtSearch").IsVisible, Is.True);
                Assert.That(Required<Button>(window, "BtnSearch").IsVisible, Is.True);
                Assert.That(Required<TextBlock>(window, "TxtWindowStatus").Text, Does.Contain("Ready"));
                Assert.That(Required<TextBlock>(window, "TxtHardwareSummary").Text, Does.Contain("Headless Test"));
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void Model_library_open_downloader_button_launches_modal_child()
    {
        var settings = new AppSettings
        {
            ModelStoragePath = Path.Combine(Path.GetTempPath(), "theorc-headless-models"),
        };

        var downloaderOpened = false;
        var library = new ModelLibraryWindow(settings, () =>
        {
            var child = new Window
            {
                Width = 320,
                Height = 180,
                Content = new TextBlock { Text = "fake downloader" },
            };
            child.Opened += (_, _) =>
            {
                downloaderOpened = true;
                Dispatcher.UIThread.Post(child.Close);
            };
            return child;
        });

        try
        {
            library.Show();
            Dispatcher.UIThread.RunJobs();

            Click(Required<Button>(library, "BtnOpenDownloader"));
            Dispatcher.UIThread.RunJobs();
            Dispatcher.UIThread.RunJobs();

            Assert.That(downloaderOpened, Is.True);
        }
        finally
        {
            library.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
