// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace OrchestratorIDE.Services.Hive;

public sealed record ChunkWriteResult(bool Complete, long BytesStored, string Path);

/// <summary>Resumable, quota-bounded SHA-256 object store for models and campaign artifacts.</summary>
public sealed class ContentAddressedStore
{
    public const int MaxChunkBytes = 1024 * 1024;
    private static readonly Regex DigestPattern = new("^[a-f0-9]{64}$", RegexOptions.Compiled);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly long _maxObjectBytes;
    private readonly long _maxStoreBytes;
    private readonly string _extension;

    public ContentAddressedStore(string root, long maxObjectBytes = 16L * 1024 * 1024 * 1024,
        long maxStoreBytes = 100L * 1024 * 1024 * 1024, string fileExtension = ".blob")
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        _maxObjectBytes = maxObjectBytes;
        _maxStoreBytes = maxStoreBytes;
        _extension = fileExtension.StartsWith('.') ? fileExtension : "." + fileExtension;
    }

    public string Root { get; }

    public bool Has(string digest) => File.Exists(CompletePath(ValidateDigest(digest)));

    public long GetResumeOffset(string digest)
    {
        digest = ValidateDigest(digest);
        var complete = CompletePath(digest);
        if (File.Exists(complete)) return new FileInfo(complete).Length;
        var partial = PartialPath(digest);
        return File.Exists(partial) ? new FileInfo(partial).Length : 0;
    }

    public string GetPath(string digest)
    {
        var path = CompletePath(ValidateDigest(digest));
        if (!File.Exists(path)) throw new FileNotFoundException("Digest is not present.", digest);
        return path;
    }

    public IReadOnlyList<string> GetDigests(int limit = 4096) => Directory
        .EnumerateFiles(Root, "*" + _extension, SearchOption.AllDirectories)
        .Select(Path.GetFileNameWithoutExtension)
        .Where(d => d is not null && DigestPattern.IsMatch(d))
        .Take(Math.Clamp(limit, 1, 100_000))
        .Cast<string>()
        .ToArray();

    public async Task<ChunkWriteResult> WriteChunkAsync(string digest, long offset, long totalBytes,
        ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        digest = ValidateDigest(digest);
        if (offset < 0 || totalBytes <= 0 || totalBytes > _maxObjectBytes || offset + data.Length > totalBytes)
            throw new InvalidDataException("Invalid object size or chunk range.");
        if (data.Length > MaxChunkBytes || data.Length == 0 && offset != totalBytes)
            throw new InvalidDataException($"Chunk must be between 1 and {MaxChunkBytes} bytes unless finalizing a complete partial object.");

        var gate = _gates.GetOrAdd(digest, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var complete = CompletePath(digest);
            if (File.Exists(complete))
                return new ChunkWriteResult(true, new FileInfo(complete).Length, complete);

            var partial = PartialPath(digest);
            Directory.CreateDirectory(Path.GetDirectoryName(partial)!);
            var existing = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            if (existing != offset)
                throw new InvalidDataException($"Resume offset mismatch: store has {existing}, request supplied {offset}.");

            if (data.Length > 0)
            {
                EnsureCapacity(data.Length);
                await using var stream = new FileStream(partial, FileMode.Append, FileAccess.Write,
                    FileShare.None, MaxChunkBytes, FileOptions.Asynchronous | FileOptions.WriteThrough);
                await stream.WriteAsync(data, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            var stored = existing + data.Length;
            if (stored != totalBytes)
                return new ChunkWriteResult(false, stored, partial);

            var actual = await ComputeSha256Async(partial, ct).ConfigureAwait(false);
            if (!actual.Equals(digest, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                throw new InvalidDataException($"SHA-256 mismatch: expected {digest}, received {actual}.");
            }

            File.Move(partial, complete, overwrite: false);
            return new ChunkWriteResult(true, stored, complete);
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private void EnsureCapacity(long incoming)
    {
        var used = Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)
            .Sum(path => { try { return new FileInfo(path).Length; } catch { return 0; } });
        if (used + incoming > _maxStoreBytes)
            throw new IOException("Content store quota exceeded.");

        var drive = new DriveInfo(Path.GetPathRoot(Root)!);
        if (drive.AvailableFreeSpace < incoming + 64L * 1024 * 1024)
            throw new IOException("Insufficient disk space for content chunk.");
    }

    private string CompletePath(string digest) => Path.Combine(Root, digest[..2], digest + _extension);
    private string PartialPath(string digest) => Path.Combine(Root, digest[..2], digest + ".part");

    private static string ValidateDigest(string digest)
    {
        digest = (digest ?? "").Trim().ToLowerInvariant();
        if (!DigestPattern.IsMatch(digest)) throw new ArgumentException("A lowercase SHA-256 digest is required.");
        return digest;
    }
}
