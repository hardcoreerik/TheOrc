// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Headless.NUnit;
using NUnit.Framework;
using OrchestratorIDE.UI.Windows;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>"About TheOrc" window opened from the macOS app menu.</summary>
[TestFixture]
public class AboutWindowTests
{
    [AvaloniaTest]
    public void AboutWindow_constructs_with_the_app_title() =>
        Assert.That(new AboutWindow().Title, Is.EqualTo("About TheOrc"));

    [Test]
    public void ReadVersion_splits_version_and_build_like_the_status_bar()
    {
        var (version, build) = AboutWindow.ReadVersion(typeof(AboutWindow).Assembly);

        Assert.That(version, Does.Match(@"^\d+\.\d+\.\d+"), "the app's <Version> from the csproj");
        Assert.That(build, Is.Not.Empty);
        Assert.That(version, Does.Not.Contain("+"));
    }
}
