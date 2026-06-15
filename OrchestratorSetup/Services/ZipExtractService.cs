// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;

namespace OrchestratorSetup.Services;

/// <summary>
/// Extracts a zip archive to a destination directory with per-entry progress callbacks.
/// Used to unpack the llama.cpp runtime zip (llama-server.exe + CUDA/Vulkan DLLs).
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
    /// Scans the extracted runtime directory and returns the path to llama-server.exe
    /// (or server.exe for older builds). Returns null if not found.
    /// </summary>
    public static string? FindServerExe(string extractDir)
    {
        foreach (var name in new[] { "llama-server.exe", "server.exe" })
        {
            // Check root and one level of subdirectory (some zips add a version folder)
            var direct = Path.Combine(extractDir, name);
            if (File.Exists(direct)) return direct;

            var nested = Directory.GetFiles(extractDir, name, SearchOption.AllDirectories)
                                   .FirstOrDefault();
            if (nested is not null) return nested;
        }
        return null;
    }
}
