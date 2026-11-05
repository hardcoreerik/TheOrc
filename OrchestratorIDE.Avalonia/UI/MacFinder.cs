// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics;

namespace OrchestratorIDE.UI;

/// <summary>
/// macOS equivalent of the app's "Show in Explorer" actions, which call <c>explorer.exe</c>
/// and so did nothing on a Mac. Uses <c>/usr/bin/open</c> directly (no shell): a file is
/// revealed and selected in Finder (<c>open -R</c>); a folder is opened. No-op elsewhere.
/// </summary>
internal static class MacFinder
{
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(path))
            return;
        try
        {
            var full = Path.GetFullPath(path);
            var args = ArgsFor(full, isFile: File.Exists(full), isDirectory: Directory.Exists(full));
            if (args is null)
                return;
            var psi = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            foreach (var a in args)
                psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
        }
        catch
        {
            // Non-fatal, like the Windows explorer.exe calls it mirrors.
        }
    }

    /// <summary>Arguments for <c>open</c>; null when the path doesn't exist.</summary>
    internal static string[]? ArgsFor(string fullPath, bool isFile, bool isDirectory) =>
        isFile ? ["-R", fullPath]
        : isDirectory ? [fullPath]
        : null;
}
