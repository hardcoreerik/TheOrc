// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Text;

namespace OrchestratorIDE.Core.Runtime;

/// <summary>
/// The architecture facts a VRAM cost estimate needs, read from a GGUF file's header.
/// All fields come straight from the header's own KV pairs — nothing inferred.
/// </summary>
public sealed record GgufModelHeader(
    string Architecture,
    int BlockCount,
    int HeadCountKv,
    int KeyLength,
    int ValueLength);

/// <summary>
/// Header-only GGUF metadata reader — Native Runtime v2.0, Phase B addendum implementation
/// (docs/NATIVE_RUNTIME_V2_SPEC.md "Phase B addendum"). Reads the handful of KV pairs a
/// KV-cache cost formula needs WITHOUT loading the model: LLamaSharp 0.27 exposes no
/// header-only metadata API (verified by reflection over the package — only load-time
/// MetadataOverride types exist), and <c>LLamaWeights.Metadata</c> requires a full weights
/// load, which is useless at admission time.
///
/// Format: GGUF v2/v3 — magic "GGUF", u32 version, u64 tensor_count, u64 kv_count, then KV
/// pairs (string key, u32 type, value). The 2026-07-19 spike validated this parser's approach
/// and the formula it feeds byte-exactly against llama.cpp's own allocator logs
/// (224.00/896.00/1792.00 MiB at n_ctx 2048/8192/16384 for Llama-3.2-3B).
///
/// Walks the full KV section with sized skips (no early exit — GGUF key order is not
/// guaranteed, and exiting early could skip an explicit value_length behind key_length,
/// mis-sizing asymmetric-head models). One remaining ordering assumption: `general.architecture`
/// must precede the `{arch}.*` keys — true of every llama.cpp-written file (its writer emits
/// general.* first); a file violating it just yields null and the legacy-estimate fallback.
///
/// Never throws to callers: any I/O or parse failure returns null, and the caller
/// (<see cref="OrcScheduler"/>) falls back to the legacy file-size-only estimate — estimation
/// must never be the thing that breaks admission.
/// </summary>
public static class GgufMetadataReader
{
    // Memo keyed by path; entry invalidated when size or mtime changes. Reads are cheap (~KB)
    // but admission can be called per-role per-conversation — no reason to re-open the file.
    private static readonly ConcurrentDictionary<string, (long Size, DateTime MtimeUtc, GgufModelHeader? Header)> s_cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static GgufModelHeader? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return null;

            if (s_cache.TryGetValue(path, out var hit) &&
                hit.Size == info.Length && hit.MtimeUtc == info.LastWriteTimeUtc)
                return hit.Header;

            var header = ReadCore(path);
            s_cache[path] = (info.Length, info.LastWriteTimeUtc, header);
            return header;
        }
        catch
        {
            return null;
        }
    }

    private static GgufModelHeader? ReadCore(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != 0x46554747) // "GGUF" little-endian
            return null;

        var version = reader.ReadUInt32();
        if (version is not (2 or 3))
            return null; // v1 used u32 counts — different layout, not worth supporting

        _ = reader.ReadUInt64(); // tensor_count
        var kvCount = reader.ReadUInt64();

        string? arch = null;
        long? blockCount = null, headCountKv = null, keyLength = null, valueLength = null;
        long? embeddingLength = null, headCount = null;

        for (ulong i = 0; i < kvCount; i++)
        {
            var key = ReadString(reader);
            var type = reader.ReadUInt32();

            // Only general.architecture and {arch}.* keys matter. Everything else — most
            // importantly the huge tokenizer arrays — is skipped by size, never materialized.
            var wanted =
                key == "general.architecture" ||
                (arch is not null && key.StartsWith(arch + ".", StringComparison.Ordinal));

            if (!wanted)
            {
                SkipValue(reader, type);
                continue;
            }

            if (key == "general.architecture")
            {
                if (type != 8) return null; // must be a string per spec
                arch = ReadString(reader);
                continue;
            }

            var suffix = key[(arch!.Length + 1)..];
            switch (suffix)
            {
                case "block_count": blockCount = ReadIntegral(reader, type); break;
                case "attention.head_count_kv": headCountKv = ReadIntegral(reader, type); break;
                case "attention.head_count": headCount = ReadIntegral(reader, type); break;
                case "attention.key_length": keyLength = ReadIntegral(reader, type); break;
                case "attention.value_length": valueLength = ReadIntegral(reader, type); break;
                case "embedding_length": embeddingLength = ReadIntegral(reader, type); break;
                default: SkipValue(reader, type); break;
            }

            // No early exit (CodeRabbit finding on the first cut): GGUF key order is not
            // guaranteed, and llama.cpp's writer emits key_length/value_length adjacently — an
            // exit that fires once key_length is in hand would skip an explicit value_length
            // right behind it, silently mis-sizing asymmetric-head (MLA-style) models via the
            // vl ?? kl fallback. A full walk of the KV section with sized skips is milliseconds
            // even past the tokenizer arrays, runs once per (path, size, mtime) thanks to the
            // memo, and removes the ordering assumption entirely for the {arch}.* keys.
        }

        if (arch is null || blockCount is null or <= 0 || headCountKv is null or <= 0)
            return null;

        // key_length is absent on many models — llama.cpp's own convention: head_dim =
        // embedding_length / head_count. value_length defaults to key_length (symmetric heads).
        var kl = keyLength ?? (embeddingLength is > 0 && headCount is > 0
            ? embeddingLength.Value / headCount.Value
            : 0);
        if (kl <= 0)
            return null;
        var vl = valueLength ?? kl;

        return new GgufModelHeader(
            Architecture: arch,
            BlockCount: (int)blockCount.Value,
            HeadCountKv: (int)headCountKv.Value,
            KeyLength: (int)kl,
            ValueLength: (int)vl);
    }

    private static string ReadString(BinaryReader r)
    {
        var len = checked((int)r.ReadUInt64());
        // Keys/arch names are short; a huge "string" here means a corrupt file — bail via the
        // outer catch rather than allocating gigabytes.
        if (len is < 0 or > 1_048_576)
            throw new InvalidDataException($"GGUF string length {len} out of range.");
        return Encoding.UTF8.GetString(r.ReadBytes(len));
    }

    private static long ReadIntegral(BinaryReader r, uint type) => type switch
    {
        0 => r.ReadByte(),      // u8
        1 => r.ReadSByte(),     // i8
        2 => r.ReadUInt16(),    // u16
        3 => r.ReadInt16(),     // i16
        4 => r.ReadUInt32(),    // u32
        5 => r.ReadInt32(),     // i32
        10 => checked((long)r.ReadUInt64()), // u64
        11 => r.ReadInt64(),    // i64
        _ => throw new InvalidDataException($"GGUF type {type} is not integral."),
    };

    /// <summary>Advances past a value without materializing it. Fixed-size types seek;
    /// string arrays must walk per-element lengths (each element is length-prefixed).</summary>
    private static void SkipValue(BinaryReader r, uint type)
    {
        switch (type)
        {
            case 0 or 1 or 7: r.BaseStream.Seek(1, SeekOrigin.Current); break;  // u8/i8/bool
            case 2 or 3: r.BaseStream.Seek(2, SeekOrigin.Current); break;       // u16/i16
            case 4 or 5 or 6: r.BaseStream.Seek(4, SeekOrigin.Current); break;  // u32/i32/f32
            case 10 or 11 or 12: r.BaseStream.Seek(8, SeekOrigin.Current); break; // u64/i64/f64
            case 8: // string
                var slen = checked((long)r.ReadUInt64());
                r.BaseStream.Seek(slen, SeekOrigin.Current);
                break;
            case 9: // array: elem type u32, count u64, then elements
                var elemType = r.ReadUInt32();
                var count = checked((long)r.ReadUInt64());
                if (elemType == 8)
                {
                    for (long i = 0; i < count; i++)
                    {
                        var len = checked((long)r.ReadUInt64());
                        r.BaseStream.Seek(len, SeekOrigin.Current);
                    }
                }
                else if (elemType == 9)
                {
                    for (long i = 0; i < count; i++)
                        SkipValue(r, 9);
                }
                else
                {
                    long elemSize = elemType switch
                    {
                        0 or 1 or 7 => 1,
                        2 or 3 => 2,
                        4 or 5 or 6 => 4,
                        10 or 11 or 12 => 8,
                        _ => throw new InvalidDataException($"GGUF array element type {elemType} unknown."),
                    };
                    r.BaseStream.Seek(count * elemSize, SeekOrigin.Current);
                }
                break;
            default:
                throw new InvalidDataException($"GGUF value type {type} unknown.");
        }
    }
}
