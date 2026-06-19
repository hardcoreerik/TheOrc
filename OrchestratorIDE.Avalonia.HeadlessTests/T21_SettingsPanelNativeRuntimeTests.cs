// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.UI.Panels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

[TestFixture]
public sealed class T21_SettingsPanelNativeRuntimeTests
{
    [AvaloniaTest]
    public void Native_runtime_test_surface_starts_disabled_until_scan_populates_bindings()
    {
        var panel = new SettingsPanel(new OllamaClient());

        var combo = panel.FindControl<ComboBox>("CbNativeBinding");
        var button = panel.FindControl<Button>("BtnRunNativeRuntimeTest");
        var result = panel.FindControl<TextBlock>("TbNativeRuntimeTestResult");
        var liveOutput = panel.FindControl<TextBlock>("TbNativeRuntimeLiveOutput");

        Assert.Multiple(() =>
        {
            Assert.That(combo, Is.Not.Null);
            Assert.That(button, Is.Not.Null);
            Assert.That(button!.IsEnabled, Is.False);
            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Text, Is.EqualTo("Not run yet."));
            Assert.That(liveOutput, Is.Not.Null);
            Assert.That(liveOutput!.Text, Is.EqualTo("(none)"));
        });
    }
}
