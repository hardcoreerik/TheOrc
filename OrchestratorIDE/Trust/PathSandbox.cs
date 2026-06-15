// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
namespace OrchestratorIDE.Trust;

/// <summary>
/// Validates that file-system paths stay within the declared workspace sandbox.
///
/// A "sandbox escape" occurs when a tool produces a fully-resolved path that
/// is not a descendant of the workspace root.  This catches both:
///   • Relative traversals  — "../../Windows/System32/evil.exe"
///   • Absolute paths       — "/etc/passwd", "C:\Users\user\.ssh\id_rsa"
/// </summary>
public static class PathSandbox
{
    // ── Core check ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when <paramref name="resolvedPath"/> is inside (or equal to)
    /// <paramref name="sandboxRoot"/>.  Both paths are fully normalised before comparison.
    /// Comparison is case-insensitive on Windows.
    /// </summary>
    public static bool IsInsideSandbox(string resolvedPath, string sandboxRoot)
    {
        // Normalise — GetFullPath collapses "..", symlinks, and alternate separators
        var root = Path.GetFullPath(sandboxRoot)
                       .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;

        var path = Path.GetFullPath(resolvedPath);

        // Exact match (the root directory itself) or proper descendant
        return path.Equals(root.TrimEnd(Path.DirectorySeparatorChar),
                           StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    // ── Display helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Short summary label for the bypass dialog header line.
    /// </summary>
    public static string EscapeLabel(string toolName, string escapedPath)
        => $"'{toolName}' → {escapedPath}";

    /// <summary>
    /// Returns the relative portion of the escape path for compact display,
    /// or the full path if it shares no common root with the sandbox.
    /// </summary>
    public static string RelativeEscape(string escapedPath, string sandboxRoot)
    {
        try   { return Path.GetRelativePath(sandboxRoot, escapedPath); }
        catch { return escapedPath; }
    }
}
