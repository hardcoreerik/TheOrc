// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using NUnit.Framework;
using OrchestratorIDE.Core;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class AppSettingsTests
{
    [Test]
    public void NativeRuntimeHiveWorker_Settings_Default_To_OptIn_And_ModelStorageRoot()
    {
        var settings = new AppSettings();

        Assert.Multiple(() =>
        {
            Assert.That(settings.ExperimentalNativeHiveWorkerEnabled, Is.False);
            Assert.That(settings.NativeRuntimeModelRoot, Is.Empty);
            Assert.That(settings.ResolvedNativeRuntimeModelRoot, Is.EqualTo(settings.ResolvedModelStoragePath));
            Assert.That(settings.NativeRuntimeContextSize, Is.EqualTo(8192));
            Assert.That(settings.NativeRuntimeGpuLayers, Is.EqualTo(-1));
        });
    }
}
