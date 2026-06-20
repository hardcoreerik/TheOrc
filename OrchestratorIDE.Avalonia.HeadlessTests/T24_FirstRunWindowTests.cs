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

/// <summary>
/// Avalonia port of OrchestratorIDE/UI/FirstRunWindow.xaml.cs, ported 2026-06-20 as part of
/// the v1.9 WPF-retirement push. Deliberately does NOT exercise BtnSave_Click/BtnSkip_Click —
/// both call AppSettings.Save(), which writes to the real per-user settings.json path
/// (AppSettings.cs's private `_path` field); a test invoking that would mutate a real file on
/// whatever machine runs the suite. Coverage here is everything observable without saving.
/// </summary>
[TestFixture]
public sealed class T24_FirstRunWindowTests
{
    private static T Required<T>(Control root, string name) where T : Control
        => root.FindControl<T>(name)
           ?? throw new AssertionException($"Expected to find control named '{name}'.");

    private static void Click(Button button)
        => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [AvaloniaTest]
    public void Shows_detected_hardware_summary_on_load()
    {
        var settings = new AppSettings
        {
            DetectedGpuName = "RTX 5070 Ti",
            DetectedVramGb = 16,
            DetectedCudaVersion = "12.8",
        };

        var window = new FirstRunWindow(settings, "C:\\work\\repo");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var summary = Required<TextBlock>(window, "TbHardwareSummary").Text ?? "";

            Assert.Multiple(() =>
            {
                Assert.That(summary, Does.Contain("RTX 5070 Ti"));
                Assert.That(summary, Does.Contain("16"));
                Assert.That(summary, Does.Contain("CUDA 12.8"));
                Assert.That(summary, Does.Contain("C:\\work\\repo"));
            });
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void Shows_swarm_hint_only_when_a_nemotron_model_is_installed()
    {
        var settings = new AppSettings();

        var withNemotron = new FirstRunWindow(settings, "C:\\work\\repo", ["llama3", "nemotron-4-340b"]);
        var withoutNemotron = new FirstRunWindow(settings, "C:\\work\\repo", ["llama3", "qwen2.5"]);
        try
        {
            withNemotron.Show();
            withoutNemotron.Show();
            Dispatcher.UIThread.RunJobs();

            Assert.Multiple(() =>
            {
                Assert.That(Required<Border>(withNemotron, "BdrSwarmHint").IsVisible, Is.True);
                Assert.That(Required<Border>(withoutNemotron, "BdrSwarmHint").IsVisible, Is.False);
            });
        }
        finally
        {
            withNemotron.Close();
            withoutNemotron.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void Trust_pill_click_updates_description_text()
    {
        var window = new FirstRunWindow(new AppSettings(), "C:\\work\\repo");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var desc = Required<TextBlock>(window, "TbTrustDesc");
            Click(Required<Button>(window, "BtnTrustFullAuto"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(desc.Text, Does.Contain("Full Auto"));

            Click(Required<Button>(window, "BtnTrustPlan"));
            Dispatcher.UIThread.RunJobs();

            Assert.That(desc.Text, Does.Contain("Plan"));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTest]
    public void Preview_updates_as_name_and_extra_context_are_typed()
    {
        var window = new FirstRunWindow(new AppSettings(), "C:\\work\\repo");
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();

            var preview = Required<TextBlock>(window, "TbPreview");
            var before = preview.Text;

            Required<TextBox>(window, "TbName").Text = "Erik";
            Dispatcher.UIThread.RunJobs();

            Assert.That(preview.Text, Is.Not.EqualTo(before));
        }
        finally
        {
            window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
