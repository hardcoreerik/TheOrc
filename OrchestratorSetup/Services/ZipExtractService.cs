// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Formats.Tar;
using System.IO.Compression;

namespace OrchestratorSetup.Services;

/// <summary>
/// Extracts the llama.cpp runtime archive to a destination directory with per-entry progress
/// callbacks. Windows assets are .zip (<see cref="ExtractAsync"/>); Linux/macOS assets are
/// .tar.gz (<see cref="ExtractTarGzAsync"/>) -- added MULTI_OS_RELEASE_SPEC.md Phase D (grok
/// review BLOCKER, 2026-06-21: the original zip-only extractor threw "not a valid zip file"
/// the moment a real macOS install tried to extract its actual .tar.gz runtime download --
/// every macOS-specific resolver/manifest change before this was reachable code that had
/// never been exercised end-to-end against the real extraction step).
/// </summary>
public sealed class ZipExtractService
{
    /// <summary>
    /// Fired for each entry as extraction proceeds.
    /// (entryIndex, totalEntries, currentEntryName)
    /// </summary>
    public event Action<int, int, string>? OnEntryExtracted;

    /// <summary>
    /// Fired when all entries have been extracted.
    /// </summary>
    public event Action? OnComplete;

    /// <summary>
    /// Extracts <paramref name="zipPath"/> to <paramref name="destDir"/>.
    /// Existing files are overwritten. The destination directory is created if it does not exist.
    /// </summary>
    public async Task ExtractAsync(
        string            zipPath,
        string            destDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        using var archive = ZipFile.OpenRead(zipPath);
        int total   = archive.Entries.Count;
        int current = 0;

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Skip directory-only entries (entries that end with /)
            if (string.IsNullOrEmpty(entry.Name))
            {
                current++;
                continue;
            }

            // Resolve the destination path, strip any leading path from the zip
            // (some llama.cpp zips wrap everything in a subfolder, some don't)
            var relativeName = entry.FullName.Replace('\\', '/');
            var destPath     = Path.GetFullPath(Path.Combine(destDir, relativeName));

            // Zip-slip guard: ensure the resolved path is within destDir
            if (!destPath.StartsWith(Path.GetFullPath(destDir) + Path.DirectorySeparatorChar,
                                      StringComparison.OrdinalIgnoreCase))
            {
                current++;
                continue; // skip malicious entries
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

            // Extract asynchronously by reading from the zip entry stream
            await using var entryStream = entry.Open();
            await using var fileStream  = new FileStream(
                destPath, FileMode.Create, FileAccess.Write,
                FileShare.None, 65_536, useAsync: true);

            await entryStream.CopyToAsync(fileStream, ct);

            current++;
            OnEntryExtracted?.Invoke(current, total, entry.Name);
        }

        OnComplete?.Invoke();
    }

    /// <summary>
    /// Extracts a .tar.gz archive (Linux/macOS llama.cpp release shape) to
    /// <paramref name="destDir"/>. Unlike <see cref="ExtractAsync"/>, this delegates entirely
    /// to System.Formats.Tar.TarFile rather than a hand-rolled per-entry loop -- it already
    /// preserves each entry's Unix file mode (including the executable bit) when extracting on
    /// a Unix-like OS, which a byte-for-byte custom copy loop would silently drop, leaving
    /// llama-server non-executable after every extraction.
    /// </summary>
    public async Task ExtractTarGzAsync(
        string            tarGzPath,
        string            destDir,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(destDir);

        await using var fileStream = File.OpenRead(tarGzPath);
        await using var gzipStream = new GZipStream(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzipStream, destDir, overwriteFiles: true, ct);

        OnComplete?.Invoke();
        // No per-entry progress here -- TarFile's extraction API doesn't expose entry-by-entry
        // callbacks the way the hand-rolled zip loop above does. These archives are ~10-25MB
        // (see Setup/model-manifest.json's llama_cpp.size_mb), so the loss of granular progress
        // during what's typically a sub-second extraction isn't worth a second hand-rolled
        // tar-parsing loop just to recover it.
    }

    /// <summary>
    /// Returns the total uncompressed size in bytes of all entries in the zip.
    /// Use this to estimate extraction progress by file size rather than entry count.
    /// </summary>
    public static long GetUncompressedSize(string zipPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            return archive.Entries.Sum(e => e.Length);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Scans the extracted runtime directory and returns the path to the llama.cpp server
    /// binary -- "llama-server.exe"/"server.exe" on Windows, "llama-server"/"server" (no
    /// extension) on Linux/macOS. Returns null if not found.
    /// </summary>
    public static string? FindServerExe(string extractDir)
    {
        var names = OperatingSystem.IsWindows()
            ? new[] { "llama-server.exe", "server.exe" }
            : new[] { "llama-server", "server" };

        foreach (var name in names)
        {
            // Check root and one level of subdirectory (some archives add a version folder)
            var direct = Path.Combine(extractDir, name);
            if (File.Exists(direct)) return direct;

            var nested = Directory.GetFiles(extractDir, name, SearchOption.AllDirectories)
                                   .FirstOrDefault();
            if (nested is not null) return nested;
        }
        return null;
    }
}
