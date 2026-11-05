// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Input;
using NUnit.Framework;
using OrchestratorIDE.UI;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// ⌘ instead of Ctrl on macOS; byte-for-byte unchanged everywhere else.
/// </summary>
[TestFixture]
public class PlatformShortcutsTests
{
    [TestCase("Ctrl+K", "⌘K")]
    [TestCase("Ctrl+Shift+E", "⇧⌘E")]
    [TestCase("Ctrl+Shift+R", "⇧⌘R")]
    [TestCase("Alt+Shift+Ctrl+F", "⌥⇧⌘F")]
    [TestCase("F1", "F1")]
    [TestCase("", "")]
    [TestCase("Hyper+X", "Hyper+X")]
    public void ToMacLabel_uses_apple_symbols_in_apple_order(string windows, string mac) =>
        Assert.That(PlatformShortcuts.ToMacLabel(windows), Is.EqualTo(mac));

    [Test]
    public void Command_modifier_is_meta_only_on_macOS() =>
        Assert.That(PlatformShortcuts.Command,
            Is.EqualTo(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control));

    [Test]
    public void Label_is_unchanged_off_macOS_and_symbolic_on_macOS() =>
        Assert.That(PlatformShortcuts.Label("Ctrl+Shift+E"),
            Is.EqualTo(OperatingSystem.IsMacOS() ? "⇧⌘E" : "Ctrl+Shift+E"));
}
