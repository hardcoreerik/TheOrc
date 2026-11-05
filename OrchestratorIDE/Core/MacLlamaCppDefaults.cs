// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Core;

/// <summary>
/// macOS: fills in the llama.cpp lane's two settings when they are empty, the way the installer
/// would have. On Windows the installer downloads llama-server and writes
/// <see cref="AppSettings.LlamaCppRuntimePath"/> / <see cref="AppSettings.LlamaCppModelPath"/>;
/// a Mac running from source never runs the installer, so both stay empty and the lane (which
/// OrcChat and Research talk to) could never start. Settings the user filled in always win.
/// Callers apply this on macOS only, so Windows and Linux keep reading their settings as-is.
/// </summary>
public static class MacLlamaCppDefaults
{
    /// <summary>Where the macOS installer extracts llama.cpp (InstallerState.LlamaRuntimeExtractPath).</summary>
    public static string InstallerRuntimeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "Application Support", "TheOrc", "Runtime", "llama");

    /// <summary>Homebrew's bin folders (Apple Silicon first, then Intel-era prefix).</summary>
    public static readonly IReadOnlyList<string> HomebrewBinDirs = ["/opt/homebrew/bin", "/usr/local/bin"];

    private const string ServerName = "llama-server";

    /// <summary>
    /// The folder that holds <c>llama-server</c>: <paramref name="configured"/> when set;
    /// otherwise the installer's folder (newest build if several were extracted), then
    /// Homebrew's. Empty when none has it.
    /// </summary>
    public static string ResolveRuntimePath(string configured,
        string? installerDir = null, IEnumerable<string>? homebrewDirs = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        installerDir ??= InstallerRuntimeDir;
        try
        {
            if (Directory.Exists(installerDir))
            {
                // The release archive unpacks into a versioned folder (llama-b12128/…).
                var newest = Directory.EnumerateFiles(installerDir, ServerName, SearchOption.AllDirectories)
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (newest?.DirectoryName is { } dir)
                    return dir;
            }
        }
        catch { /* unreadable folder: try Homebrew */ }

        foreach (var dir in homebrewDirs ?? HomebrewBinDirs)
            if (File.Exists(Path.Combine(dir, ServerName)))
                return dir;
        return "";
    }

    /// <summary>
    /// The model file: <paramref name="configured"/> when set; otherwise the app's default
    /// model, then the last single-agent model (when either is a .gguf file in the model
    /// folder), then the first base GGUF found there. Empty when there is none.
    /// </summary>
    public static string ResolveModelPath(string configured, AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;

        var root = settings.ResolvedModelStoragePath;
        foreach (var name in new[] { settings.DefaultModel, settings.LastSingleModel })
        {
            // Ollama tags ("qwen2.5-coder:14b") aren't files; only plain .gguf names are.
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                || name.IndexOfAny(['/', '\\']) >= 0)
                continue;
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                return path;
        }

        try
        {
            return ModelDepot.Scan(root).Assets
                .FirstOrDefault(a => a.Kind == RuntimeAssetKind.BaseModelGguf)?.Path ?? "";
        }
        catch
        {
            return "";
        }
    }
}
