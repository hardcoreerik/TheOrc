// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;

namespace OrchestratorSetup.Services;

/// <summary>
/// Reads files that were compiled into the installer exe as EmbeddedResource items.
/// This is required for the standalone single-file publish — there is no Resources/
/// folder next to the exe when the user downloads it from GitHub Releases.
/// </summary>
internal static class EmbeddedResources
{
    private static readonly Assembly _asm = Assembly.GetExecutingAssembly();

    /// <summary>
    /// Returns the text content of an embedded resource whose name ends with
    /// <paramref name="suffix"/> (e.g. "model-manifest.json" or
    /// "Profiles.general.agent.md").  Returns null if not found.
    /// </summary>
    public static string? ReadText(string suffix)
    {
        var name = _asm.GetManifestResourceNames()
                       .FirstOrDefault(n => n.EndsWith(suffix,
                           StringComparison.OrdinalIgnoreCase));
        if (name is null) return null;

        using var stream = _asm.GetManifestResourceStream(name);
        if (stream is null) return null;

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Returns the manifest JSON string, checking the file system first
    /// (useful during development when the file exists next to the exe)
    /// and falling back to the embedded resource.
    /// </summary>
    public static string? ReadManifestJson()
    {
        // Dev-mode: file next to exe takes priority so edits don't require a rebuild
        foreach (var p in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "model-manifest.json"),
            Path.Combine(AppContext.BaseDirectory, "model-manifest.json"),
        })
        {
            if (File.Exists(p)) return File.ReadAllText(p);
        }

        // Production single-file mode: read from embedded resource
        return ReadText("model-manifest.json");
    }
}
