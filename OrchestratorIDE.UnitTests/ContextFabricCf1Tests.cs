// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using NUnit.Framework;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricCf1Tests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best effort for pooled SQLite and antivirus handles on Windows.
            }
        }
        _tempRoots.Clear();
    }

    [Test]
    public void MigrationV8_Creates_ContextFabric_Tables_And_Fts()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        using var connection = store.Open();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 8"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_corpora'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'fabric_segment_fts'"), Is.EqualTo(1));
        });
    }

    [Test]
    public void TextMarkdownParser_Normalizes_Newlines_And_Tracks_Heading_Path()
    {
        var parser = new TextMarkdownFabricParser();
        var source = Encoding.UTF8.GetBytes("\uFEFF# Alpha\r\n\r\nFirst paragraph.  \r\n\r\n## Beta\r\n\r\nSecond paragraph.\r\n");

        var parsed = parser.Parse(source, "text/markdown");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.NormalizedText, Is.EqualTo("# Alpha\n\nFirst paragraph.\n\n## Beta\n\nSecond paragraph.\n"));
            Assert.That(parsed.Blocks, Has.Count.EqualTo(4));
            Assert.That(parsed.Blocks[1].HeadingPath, Is.EqualTo("Alpha"));
            Assert.That(parsed.Blocks[3].HeadingPath, Is.EqualTo("Alpha / Beta"));
            Assert.That(parsed.NormalizedText[parsed.Blocks[3].CharStart..parsed.Blocks[3].CharEnd], Is.EqualTo("Second paragraph."));
        });
    }

    [Test]
    public void TextMarkdownParser_Rejects_Invalid_Utf8_And_Nul()
    {
        var parser = new TextMarkdownFabricParser();

        Assert.Multiple(() =>
        {
            Assert.That(
                () => parser.Parse(new byte[] { 0xC3, 0x28 }, "text/plain"),
                Throws.TypeOf<InvalidDataException>());
            Assert.That(
                () => parser.Parse(Encoding.UTF8.GetBytes("hello\0world"), "text/plain"),
                Throws.TypeOf<InvalidDataException>());
        });
    }

    [Test]
    public void TextMarkdownParser_Treats_Adjacent_Headings_As_Boundaries()
    {
        var parsed = new TextMarkdownFabricParser().Parse(
            Encoding.UTF8.GetBytes("# Alpha\nIntro\n## Beta\nBody\n"),
            "text/markdown");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Blocks.Select(block => block.Text),
                Is.EqualTo(new[] { "# Alpha", "Intro", "## Beta", "Body" }));
            Assert.That(parsed.Blocks.Select(block => block.HeadingPath),
                Is.EqualTo(new[] { "Alpha", "Alpha", "Alpha / Beta", "Alpha / Beta" }));
            Assert.That(parsed.Blocks, Has.All.Matches<FabricParsedBlock>(block =>
                parsed.NormalizedText[block.CharStart..block.CharEnd] == block.Text));
        });
    }

    [Test]
    public void Segmenter_Is_Deterministic_Bounded_And_Wires_Neighbors()
    {
        var parser = new TextMarkdownFabricParser();
        var text = string.Join("\n\n", Enumerable.Range(1, 12)
            .Select(index => $"Paragraph {index}. " + new string((char)('a' + (index % 20)), 180)));
        var parsed = parser.Parse(Encoding.UTF8.GetBytes(text), "text/plain");
        var segmenter = new FabricSegmenter(new FabricSegmenterOptions(64, 96, 16));

        var first = segmenter.Segment("doc-stable", parsed);
        var second = segmenter.Segment("doc-stable", parsed);

        Assert.Multiple(() =>
        {
            Assert.That(first.Select(segment => segment.SegmentId), Is.EqualTo(second.Select(segment => segment.SegmentId)));
            Assert.That(first, Has.Count.GreaterThan(1));
            Assert.That(first, Has.All.Matches<FabricSegmentDraft>(segment => segment.TokenCount <= 96));
            Assert.That(first[0].PreviousSegmentId, Is.Null);
            Assert.That(first[^1].NextSegmentId, Is.Null);
            Assert.That(first[1].PreviousSegmentId, Is.EqualTo(first[0].SegmentId));
        });
    }

    [Test]
    public void Segmenter_Does_Not_Split_Utf16_Surrogate_Pairs()
    {
        var parsed = new TextMarkdownFabricParser().Parse(
            Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\U0001F600", 400))),
            "text/plain");
        var segments = new FabricSegmenter(new FabricSegmenterOptions(64, 64, 0))
            .Segment("doc-unicode", parsed);

        Assert.Multiple(() =>
        {
            Assert.That(segments, Has.Count.GreaterThan(1));
            Assert.That(segments, Has.All.Matches<FabricSegmentDraft>(segment =>
                !char.IsLowSurrogate(segment.Text[0]) && !char.IsHighSurrogate(segment.Text[^1])));
        });
    }

    [Test]
    public async Task Library_Import_Rebuild_Search_And_Delete_Are_Deterministic()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Field Manual");
        var sourcePath = Path.Combine(harness.Root, "manual.md");
        await File.WriteAllTextAsync(sourcePath,
            "# Signals\n\nThe assigned call sign is LANTERN.\n\n## Channels\n\nEmergency frequency is 17.4 MHz.\n");

        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var rebuilt = await harness.Service.RebuildDocumentAsync(imported.Document.DocumentId);
        var search = harness.Service.Search("LANTERN!!!", corpus.CorpusId);

        Assert.Multiple(() =>
        {
            Assert.That(imported.Rebuilt, Is.False);
            Assert.That(rebuilt.Rebuilt, Is.True);
            Assert.That(rebuilt.Document.DocumentId, Is.EqualTo(imported.Document.DocumentId));
            Assert.That(rebuilt.Document.NormalizedDigest, Is.EqualTo(imported.Document.NormalizedDigest));
            Assert.That(rebuilt.Segments.Select(segment => segment.SegmentId),
                Is.EqualTo(imported.Segments.Select(segment => segment.SegmentId)));
            Assert.That(harness.Artifacts.Has(imported.Document.SourceDigest), Is.True);
            Assert.That(harness.Artifacts.Has(imported.Document.NormalizedDigest), Is.True);
            Assert.That(search, Has.Count.GreaterThanOrEqualTo(1));
            Assert.That(search[0].Text, Does.Contain("LANTERN"));
        });

        Assert.That(harness.Service.DeleteCorpus(corpus.CorpusId), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(harness.Repository.GetCorpus(corpus.CorpusId), Is.Null);
            Assert.That(harness.Repository.GetDocument(imported.Document.DocumentId), Is.Null);
            Assert.That(harness.Repository.GetSegments(imported.Document.DocumentId), Is.Empty);
            Assert.That(harness.Service.Search("LANTERN", corpus.CorpusId), Is.Empty);
        });
    }

    [Test]
    public async Task Library_Rejects_Oversized_Source_Without_Database_Rows()
    {
        var harness = NewHarness(maximumSourceBytes: 16);
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Bounded");
        var sourcePath = Path.Combine(harness.Root, "large.txt");
        await File.WriteAllTextAsync(sourcePath, new string('x', 64));

        Assert.That(
            async () => await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath),
            Throws.TypeOf<InvalidDataException>());
        Assert.That(harness.Service.ListDocuments(corpus.CorpusId), Is.Empty);
    }

    [Test]
    public async Task Library_Distinguishes_Parsing_Media_Type_In_Document_Identity()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Media identity");
        var sourcePath = Path.Combine(harness.Root, "ambiguous.txt");
        await File.WriteAllTextAsync(sourcePath, "# Heading\n\nBody text.\n");

        var plain = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath, "text/plain");
        var markdown = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath, "text/markdown");

        Assert.Multiple(() =>
        {
            Assert.That(markdown.Document.DocumentId, Is.Not.EqualTo(plain.Document.DocumentId));
            Assert.That(harness.Service.ListDocuments(corpus.CorpusId), Has.Count.EqualTo(2));
            Assert.That(markdown.Segments[0].HeadingPath, Is.EqualTo("Heading"));
            Assert.That(plain.Segments[0].HeadingPath, Is.Null);
        });
    }

    [Test]
    public async Task Library_Rebuild_Fails_Closed_When_Source_Artifact_Is_Missing()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Recovery");
        var sourcePath = Path.Combine(harness.Root, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "One durable paragraph.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var originalSegments = harness.Repository.GetSegments(imported.Document.DocumentId)
            .Select(segment => segment.SegmentId)
            .ToArray();
        File.Delete(harness.Artifacts.GetPath(imported.Document.SourceDigest));

        Assert.That(
            async () => await harness.Service.RebuildDocumentAsync(imported.Document.DocumentId),
            Throws.TypeOf<FileNotFoundException>());
        Assert.That(
            harness.Repository.GetSegments(imported.Document.DocumentId).Select(segment => segment.SegmentId),
            Is.EqualTo(originalSegments));
    }

    [Test]
    public async Task Library_Resumes_Partial_Source_Artifact()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Resume partial");
        var source = Encoding.UTF8.GetBytes("Resumable source content.\n");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
        await harness.Artifacts.WriteChunkAsync(digest, 0, source.Length, source.AsMemory(0, 7));
        var sourcePath = Path.Combine(harness.Root, "resume.txt");
        await File.WriteAllBytesAsync(sourcePath, source);

        await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);

        Assert.That(harness.Artifacts.Has(digest), Is.True);
    }

    [Test]
    public async Task Library_Finalizes_Full_Length_Partial_Source_Artifact()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Finalize partial");
        var source = Encoding.UTF8.GetBytes("Fully written source content.\n");
        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(source)).ToLowerInvariant();
        var partialPath = Path.Combine(harness.Artifacts.Root, digest[..2], digest + ".part");
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath)!);
        await File.WriteAllBytesAsync(partialPath, source);
        var sourcePath = Path.Combine(harness.Root, "finalize.txt");
        await File.WriteAllBytesAsync(sourcePath, source);

        await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);

        Assert.Multiple(() =>
        {
            Assert.That(harness.Artifacts.Has(digest), Is.True);
            Assert.That(File.Exists(partialPath), Is.False);
        });
    }

    [Test]
    public async Task Repository_ReplaceDocument_Rolls_Back_On_Invalid_Segment_Set()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Atomic replacement");
        var sourcePath = Path.Combine(harness.Root, "atomic.txt");
        await File.WriteAllTextAsync(sourcePath, "Original searchable content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var originalIds = imported.Segments.Select(segment => segment.SegmentId).ToArray();
        var invalid = new[]
        {
            Draft("seg-invalid-a", 1, "first"),
            Draft("seg-invalid-b", 1, "second"),
        };

        Assert.That(
            () => harness.Repository.ReplaceDocument(imported.Document, invalid),
            Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        Assert.Multiple(() =>
        {
            Assert.That(
                harness.Repository.GetSegments(imported.Document.DocumentId).Select(segment => segment.SegmentId),
                Is.EqualTo(originalIds));
            Assert.That(harness.Service.Search("searchable", corpus.CorpusId), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task Repository_ReplaceDocument_Updates_Identity_Metadata()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Identity replacement");
        var sourcePath = Path.Combine(harness.Root, "identity.txt");
        await File.WriteAllTextAsync(sourcePath, "Original content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var replacement = imported.Document with
        {
            SourceDigest = new string('a', 64),
            ParserId = "replacement-parser",
            ParserVersion = "2",
        };

        harness.Repository.ReplaceDocument(replacement, [Draft("seg-replacement", 0, "replacement")]);
        var persisted = harness.Repository.GetDocument(replacement.DocumentId)!;

        Assert.Multiple(() =>
        {
            Assert.That(persisted.SourceDigest, Is.EqualTo(replacement.SourceDigest));
            Assert.That(persisted.ParserId, Is.EqualTo(replacement.ParserId));
            Assert.That(persisted.ParserVersion, Is.EqualTo(replacement.ParserVersion));
        });
    }

    [Test]
    public async Task MigrationV8_Rejects_Invalid_Segment_Ranges()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Segment constraints");
        var sourcePath = Path.Combine(harness.Root, "constraints.txt");
        await File.WriteAllTextAsync(sourcePath, "Valid content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE fabric_segments SET char_start = -1 WHERE document_id = $document";
        command.Parameters.AddWithValue("$document", imported.Document.DocumentId);

        Assert.That(() => command.ExecuteNonQuery(),
            Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
    }

    private Harness NewHarness(long maximumSourceBytes = 1024 * 1024)
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-cf1-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var repository = new FabricLibraryRepository(store);
        var artifacts = new ContentAddressedStore(
            Path.Combine(root, "objects"),
            maxObjectBytes: maximumSourceBytes,
            maxStoreBytes: maximumSourceBytes * 8);
        var service = new FabricLibraryService(
            repository,
            artifacts,
            options: new FabricLibraryOptions(
                maximumSourceBytes,
                new FabricSegmenterOptions(64, 128, 16)));
        return new Harness(root, store, repository, artifacts, service);
    }

    private static long Scalar(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static FabricSegmentDraft Draft(string id, int ordinal, string text) => new(
        id,
        ordinal,
        null,
        0,
        text.Length,
        1,
        FabricHashing.Sha256(text),
        text,
        null,
        null,
        FabricIngestionVersions.Segmenter);

    private sealed record Harness(
        string Root,
        SqliteStore Store,
        FabricLibraryRepository Repository,
        ContentAddressedStore Artifacts,
        FabricLibraryService Service);
}
