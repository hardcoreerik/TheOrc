// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Avalonia.Input;

namespace OrchestratorIDE.UI;

/// <summary>
/// The app's keyboard shortcuts are written Windows-style ("Ctrl+K"). On macOS the command key
/// is ⌘ (<see cref="KeyModifiers.Meta"/>), not Control, and shortcuts are shown as symbols
/// ("⌘K"). Everywhere else both members return exactly what the code used before.
/// </summary>
internal static class PlatformShortcuts
{
    /// <summary>⌘ on macOS, Ctrl elsewhere.</summary>
    public static KeyModifiers Command =>
        OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>
    /// A shortcut label for display: "Ctrl+Shift+E" becomes "⇧⌘E" on macOS; unchanged elsewhere.
    /// Empty stays empty.
    /// </summary>
    public static string Label(string windowsLabel) =>
        OperatingSystem.IsMacOS() ? ToMacLabel(windowsLabel) : windowsLabel;

    /// <summary>
    /// "Ctrl+Shift+E" → "⇧⌘E", in Apple's modifier order (⌃ ⌥ ⇧ ⌘), the way macOS menus show
    /// shortcuts. "Ctrl" means the command key here, matching <see cref="Command"/>.
    /// </summary>
    internal static string ToMacLabel(string windowsLabel)
    {
        if (string.IsNullOrWhiteSpace(windowsLabel))
            return windowsLabel;

        var parts = windowsLabel.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return windowsLabel;

        bool alt = false, shift = false, command = false;
        foreach (var modifier in parts[..^1])
        {
            switch (modifier.ToLowerInvariant())
            {
                case "ctrl" or "control" or "cmd": command = true; break;
                case "shift": shift = true; break;
                case "alt" or "option": alt = true; break;
                default: return windowsLabel; // not a shortcut we understand; show it as written
            }
        }

        return (alt ? "⌥" : "") + (shift ? "⇧" : "") + (command ? "⌘" : "") + parts[^1];
    }
}
