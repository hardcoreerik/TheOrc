// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.UnitTests;

/// <summary>
/// GgufMetadataReader parses a simple documented binary format, so unlike the native-object
/// classes it is fully unit-testable: these tests WRITE tiny valid (and deliberately invalid)
/// GGUF headers to temp files and read them back. The format knowledge here mirrors the
/// 2026-07-19 spike parser that was validated against a real Llama-3.2-3B GGUF (see the
/// Phase B addendum in docs/NATIVE_RUNTIME_V2_SPEC.md).
/// </summary>
[TestFixture]
public sealed class GgufMetadataReaderTests
{
    private readonly List<string> _tempFiles = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
        _tempFiles.Clear();
    }

    [Test]
    public void TryRead_Parses_A_Valid_Header_With_Explicit_Key_Length()
    {
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            WriteKvU32(w, "llama.block_count", 28);
            WriteKvU32(w, "llama.attention.head_count_kv", 8);
            WriteKvU32(w, "llama.attention.head_count", 24);
            WriteKvU32(w, "llama.attention.key_length", 128);
            WriteKvU32(w, "llama.attention.value_length", 128);
        });

        var header = GgufMetadataReader.TryRead(path);

        Assert.That(header, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(header!.Architecture, Is.EqualTo("llama"));
            Assert.That(header.BlockCount, Is.EqualTo(28));
            Assert.That(header.HeadCountKv, Is.EqualTo(8));
            Assert.That(header.KeyLength, Is.EqualTo(128));
            Assert.That(header.ValueLength, Is.EqualTo(128));
        });
    }

    [Test]
    public void TryRead_Derives_Key_Length_From_Embedding_And_Head_Count_When_Absent()
    {
        // Same convention llama.cpp itself uses: head_dim = embedding_length / head_count.
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            WriteKvU32(w, "llama.block_count", 28);
            WriteKvU32(w, "llama.attention.head_count_kv", 8);
            WriteKvU32(w, "llama.attention.head_count", 24);
            WriteKvU32(w, "llama.embedding_length", 3072);
        });

        var header = GgufMetadataReader.TryRead(path);

        Assert.That(header, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(header!.KeyLength, Is.EqualTo(128)); // 3072 / 24
            Assert.That(header.ValueLength, Is.EqualTo(128)); // defaults to KeyLength
        });
    }

    [Test]
    public void TryRead_Skips_Large_String_Arrays_Between_Wanted_Keys()
    {
        // Tokenizer-style array placed BETWEEN the arch key and the llama.* keys, so the parser
        // must skip it correctly (per-element length walk) to reach what it needs.
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            WriteKvStringArray(w, "tokenizer.ggml.tokens", Enumerable.Range(0, 5000).Select(i => $"tok{i}").ToArray());
            WriteKvU32(w, "llama.block_count", 16);
            WriteKvU32(w, "llama.attention.head_count_kv", 4);
            WriteKvU32(w, "llama.attention.key_length", 64);
        });

        var header = GgufMetadataReader.TryRead(path);

        Assert.That(header, Is.Not.Null);
        Assert.That(header!.BlockCount, Is.EqualTo(16));
    }

    [Test]
    public void TryRead_Honors_Asymmetric_Value_Length_Written_After_Key_Length()
    {
        // Regression for a CodeRabbit finding on the first cut: an early-exit fired once
        // key_length was in hand, skipping an explicit value_length written right behind it —
        // silently mis-sizing asymmetric-head (MLA-style) models via the vl ?? kl fallback.
        // llama.cpp's writer emits these keys adjacently in exactly this order.
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "deepseek2");
            WriteKvU32(w, "deepseek2.block_count", 27);
            WriteKvU32(w, "deepseek2.attention.head_count_kv", 16);
            WriteKvU32(w, "deepseek2.attention.key_length", 192);
            WriteKvU32(w, "deepseek2.attention.value_length", 128); // asymmetric: dv != dk
        });

        var header = GgufMetadataReader.TryRead(path);

        Assert.That(header, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(header!.KeyLength, Is.EqualTo(192));
            Assert.That(header.ValueLength, Is.EqualTo(128), "must not fall back to KeyLength");
        });
    }

    [Test]
    public void TryRead_Returns_Null_For_Wrong_Magic()
    {
        var path = NewTempFile();
        File.WriteAllBytes(path, "NOTGGUF-lots-of-other-bytes-here"u8.ToArray());

        Assert.That(GgufMetadataReader.TryRead(path), Is.Null);
    }

    [Test]
    public void TryRead_Returns_Null_For_Missing_File() =>
        Assert.That(GgufMetadataReader.TryRead(Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".gguf")), Is.Null);

    [Test]
    public void TryRead_Returns_Null_For_Truncated_Header()
    {
        var path = NewTempFile();
        File.WriteAllBytes(path, [0x47, 0x47, 0x55, 0x46, 3, 0]); // "GGUF" + partial version

        Assert.That(GgufMetadataReader.TryRead(path), Is.Null);
    }

    [Test]
    public void TryRead_Returns_Null_When_Required_Keys_Missing()
    {
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            // no block_count / heads at all
        });

        Assert.That(GgufMetadataReader.TryRead(path), Is.Null);
    }

    [Test]
    public void TryRead_Memo_Invalidates_When_File_Changes()
    {
        var path = WriteGguf(w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            WriteKvU32(w, "llama.block_count", 28);
            WriteKvU32(w, "llama.attention.head_count_kv", 8);
            WriteKvU32(w, "llama.attention.key_length", 128);
        });
        var first = GgufMetadataReader.TryRead(path);
        Assert.That(first!.BlockCount, Is.EqualTo(28));

        // Rewrite in place with a different block count (and ensure mtime moves).
        WriteGgufTo(path, w =>
        {
            WriteKvString(w, "general.architecture", "llama");
            WriteKvU32(w, "llama.block_count", 99);
            WriteKvU32(w, "llama.attention.head_count_kv", 8);
            WriteKvU32(w, "llama.attention.key_length", 128);
        });
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var second = GgufMetadataReader.TryRead(path);
        Assert.That(second!.BlockCount, Is.EqualTo(99));
    }

    [Test]
    public void TryRead_Parses_A_Real_Model_File_When_Configured()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the real-file header-parse lane.");

        var header = GgufMetadataReader.TryRead(ggufPath!);

        // Field values are model-specific, so assert structure/sanity rather than exact dims —
        // the exact-math cases above cover the formula against known synthetic fixtures, and
        // the 2026-07-19 spike validated the real-file numbers byte-exactly by hand.
        Assert.That(header, Is.Not.Null, "a real GGUF's header must parse");
        Assert.Multiple(() =>
        {
            Assert.That(header!.Architecture, Is.Not.Empty);
            Assert.That(header.BlockCount, Is.InRange(1, 1000));
            Assert.That(header.HeadCountKv, Is.InRange(1, 1024));
            Assert.That(header.KeyLength, Is.InRange(8, 4096));
        });
    }

    // ── GGUF fixture writers (v3 layout: magic, u32 version, u64 tensor_count, u64 kv_count) ──

    private string WriteGguf(Action<BinaryWriter> writeKvs)
    {
        var path = NewTempFile();
        WriteGgufTo(path, writeKvs);
        return path;
    }

    private static void WriteGgufTo(string path, Action<BinaryWriter> writeKvs)
    {
        // Count KVs by writing to a scratch buffer first (kv_count precedes the pairs in the
        // real format, so the count must be known before the pairs are emitted to the file).
        using var kvBuffer = new MemoryStream();
        ulong kvCount;
        using (var kw = new BinaryWriter(kvBuffer, Encoding.UTF8, leaveOpen: true))
        {
            writeKvs(kw);
            kvCount = KvWritesInProgress;
            KvWritesInProgress = 0;
        }

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(stream, Encoding.UTF8);
        w.Write(0x46554747u); // "GGUF"
        w.Write(3u);          // version
        w.Write(0ul);         // tensor_count
        w.Write(kvCount);
        w.Write(kvBuffer.ToArray());
    }

    // Simple static tally: each WriteKv* helper increments it; WriteGgufTo consumes it. Tests
    // are single-threaded per NUnit default here, so a static is safe and keeps helpers terse.
    private static ulong KvWritesInProgress;

    private static void WriteStr(BinaryWriter w, string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        w.Write((ulong)bytes.Length);
        w.Write(bytes);
    }

    private static void WriteKvString(BinaryWriter w, string key, string value)
    {
        WriteStr(w, key);
        w.Write(8u); // string
        WriteStr(w, value);
        KvWritesInProgress++;
    }

    private static void WriteKvU32(BinaryWriter w, string key, uint value)
    {
        WriteStr(w, key);
        w.Write(4u); // u32
        w.Write(value);
        KvWritesInProgress++;
    }

    private static void WriteKvStringArray(BinaryWriter w, string key, string[] values)
    {
        WriteStr(w, key);
        w.Write(9u); // array
        w.Write(8u); // element type: string
        w.Write((ulong)values.Length);
        foreach (var v in values)
            WriteStr(w, v);
        KvWritesInProgress++;
    }

    private string NewTempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), "orc-gguf-test-" + Guid.NewGuid().ToString("N") + ".gguf");
        _tempFiles.Add(path);
        return path;
    }
}
