// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class AppSettingsTests
{
    [Test]
    public void NativeRuntimeHiveWorker_Settings_Default_To_Enabled_And_ModelStorageRoot()
    {
        var settings = new AppSettings();

        Assert.Multiple(() =>
        {
            // §6 default-runtime flip, 2026-07-29 — see AppSettings.cs doc comment.
            Assert.That(settings.ExperimentalNativeHiveWorkerEnabled, Is.True);
            Assert.That(settings.ExperimentalNativeMainChatEnabled, Is.True);
            Assert.That(settings.NativeRuntimeModelRoot, Is.Empty);
            Assert.That(settings.ResolvedNativeRuntimeModelRoot, Is.EqualTo(settings.ResolvedModelStoragePath));
            Assert.That(settings.NativeRuntimeContextSize, Is.EqualTo(8192));
            Assert.That(settings.NativeRuntimeGpuLayers, Is.EqualTo(-1));
        });
    }
}
