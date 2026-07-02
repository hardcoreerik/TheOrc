// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json;
using System.IO.Compression;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
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
    public void MigrationV9_Creates_ContextFabric_Tables_And_Fts()
    {
        using var store = new SqliteStore(":memory:");
        store.Initialize();
        using var connection = store.Open();

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 9"), Is.EqualTo(1));
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
            Assert.That(parsed.Blocks[0].BlockKind, Is.EqualTo("heading"));
            Assert.That(parsed.Blocks[1].BlockKind, Is.EqualTo("text"));
            Assert.That(parsed.NormalizedText[parsed.Blocks[3].CharStart..parsed.Blocks[3].CharEnd], Is.EqualTo("Second paragraph."));
        });
    }

    [Test]
    public void DocxParser_Extracts_Text_Table_And_Figure_Blocks()
    {
        var parser = new DocxFabricParser();

        var parsed = parser.Parse(BuildDocxPackage(), "application/vnd.openxmlformats-officedocument.wordprocessingml.document");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.ParserVersion, Is.EqualTo(FabricIngestionVersions.DocxParser));
            Assert.That(parsed.Blocks.Select(block => block.BlockKind), Does.Contain("table"));
            Assert.That(parsed.Blocks.Select(block => block.BlockKind), Does.Contain("figure"));
            Assert.That(parsed.Blocks.Single(block => block.BlockKind == "table").Text, Is.EqualTo("Key | Value"));
            Assert.That(parsed.Blocks, Has.All.Matches<FabricParsedBlock>(block =>
                !string.IsNullOrWhiteSpace(block.SourceLocator) &&
                parsed.NormalizedText[block.CharStart..block.CharEnd] == block.Text));
        });
    }

    [Test]
    public void EpubParser_Extracts_Spine_Text_Table_And_Figure_Blocks()
    {
        var parser = new EpubFabricParser();

        var parsed = parser.Parse(BuildEpubPackage(encrypted: false), "application/epub+zip");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.ParserVersion, Is.EqualTo(FabricIngestionVersions.EpubParser));
            Assert.That(parsed.Blocks.Select(block => block.BlockKind), Does.Contain("heading"));
            Assert.That(parsed.Blocks.Select(block => block.BlockKind), Does.Contain("table"));
            Assert.That(parsed.Blocks.Select(block => block.BlockKind), Does.Contain("figure"));
            Assert.That(parsed.Blocks.Single(block => block.BlockKind == "table").Text, Is.EqualTo("Key | Value"));
            Assert.That(parsed.Blocks, Has.All.Matches<FabricParsedBlock>(block =>
                block.SourceLocator == "OEBPS/chapter.xhtml" &&
                parsed.NormalizedText[block.CharStart..block.CharEnd] == block.Text));
        });
    }

    [Test]
    public void EpubParser_Rejects_Encrypted_Packages()
    {
        var parser = new EpubFabricParser();

        Assert.That(
            () => parser.Parse(BuildEpubPackage(encrypted: true), "application/epub+zip"),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void EpubParser_Rejects_Duplicate_Manifest_Ids()
    {
        var parser = new EpubFabricParser();

        Assert.That(
            () => parser.Parse(BuildEpubPackage(encrypted: false, duplicateManifestId: true), "application/epub+zip"),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public async Task PdfTextParser_Attaches_Page_Locators_To_Blocks()
    {
        var parser = new PdfTextFabricParser();
        var source = await File.ReadAllBytesAsync(GetDarwinPdfFixturePath());

        var parsed = parser.Parse(source, "application/pdf");

        Assert.Multiple(() =>
        {
            Assert.That(parsed.Blocks, Has.Count.GreaterThan(0));
            Assert.That(parsed.Blocks, Has.All.Matches<FabricParsedBlock>(block =>
                block.PageNumber >= 1 && !string.IsNullOrWhiteSpace(block.SourceLocator)));
            Assert.That(parsed.Blocks, Has.All.Matches<FabricParsedBlock>(block =>
                parsed.NormalizedText[block.CharStart..block.CharEnd] == block.Text));
        });
    }

    [Test]
    public void PdfTextParser_Preserves_Page_Number_After_Blank_Page()
    {
        var parser = new PdfTextFabricParser();

        var parsed = parser.Parse(BuildTwoPagePdfWithBlankFirstPage(), "application/pdf");
        var sentinel = parsed.Blocks.Single(block => block.Text.Contains("Second page sentinel", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(sentinel.PageNumber, Is.EqualTo(2));
            Assert.That(sentinel.SourceLocator, Is.EqualTo("page 2"));
            Assert.That(parsed.NormalizedText[sentinel.CharStart..sentinel.CharEnd], Is.EqualTo(sentinel.Text));
        });
    }

    [Test]
    public void PdfTextParser_Rejects_ImageOnly_Pdf_Without_Ocr()
    {
        var parser = new PdfTextFabricParser();

        Assert.That(
            () => parser.Parse(BuildBlankPdf(), "application/pdf"),
            Throws.TypeOf<InvalidDataException>());
    }

    [Test]
    public void PdfTextParser_Uses_Configured_Ocr_For_ImageOnly_Pdf()
    {
        var parser = new PdfTextFabricParser(new FakeOcrEngine("OCR sentinel text.", 0.88, "synthetic warning"));

        var parsed = parser.Parse(BuildBlankPdf(), "application/pdf");
        var block = parsed.Blocks.Single();

        Assert.Multiple(() =>
        {
            Assert.That(block.BlockKind, Is.EqualTo("ocr"));
            Assert.That(block.PageNumber, Is.EqualTo(1));
            Assert.That(block.SourceLocator, Is.EqualTo("page 1"));
            Assert.That(block.Confidence, Is.EqualTo(0.88));
            Assert.That(block.Text, Is.EqualTo("OCR sentinel text."));
            Assert.That(parsed.Warnings, Is.EqualTo(new[] { "page 1: synthetic warning" }));
        });
    }

    [Test]
    public void PdfTextParser_Uses_Ocr_For_Blank_Pages_In_Mixed_Pdf()
    {
        var ocr = new FakeOcrEngine("OCR cover page.", 0.75, null);
        var parser = new PdfTextFabricParser(ocr);

        var parsed = parser.Parse(BuildTwoPagePdfWithBlankFirstPage(), "application/pdf");
        var ocrBlock = parsed.Blocks.Single(block => block.Text.Contains("OCR cover page", StringComparison.Ordinal));
        var textBlock = parsed.Blocks.Single(block => block.Text.Contains("Second page sentinel", StringComparison.Ordinal));

        Assert.Multiple(() =>
        {
            Assert.That(ocrBlock.BlockKind, Is.EqualTo("ocr"));
            Assert.That(ocrBlock.PageNumber, Is.EqualTo(1));
            Assert.That(ocrBlock.SourceLocator, Is.EqualTo("page 1"));
            Assert.That(ocrBlock.Confidence, Is.EqualTo(0.75));
            Assert.That(textBlock.BlockKind, Is.EqualTo("text"));
            Assert.That(textBlock.PageNumber, Is.EqualTo(2));
            Assert.That(textBlock.SourceLocator, Is.EqualTo("page 2"));
            Assert.That(textBlock.Confidence, Is.Null);
            Assert.That(parsed.Warnings, Is.Empty);
            Assert.That(ocr.PageNumbers, Is.EqualTo(new[] { 1 }));
        });
    }

    [Test]
    public void PdfPageLocators_Mark_Blocks_Spanning_Multiple_Pages()
    {
        const string normalized = "Page one starts\n\ncontinues on page two\n";
        var blocks = new[]
        {
            new FabricParsedBlock(0, normalized.Length, null, normalized.TrimEnd('\n')),
        };
        var pageTexts = new (int PageNumber, string Text)[]
        {
            (1, "Page one starts"),
            (2, "continues on page two"),
        };

        var located = FabricTextParsing.AddPageLocators(blocks, normalized, pageTexts);

        Assert.Multiple(() =>
        {
            Assert.That(located[0].PageNumber, Is.EqualTo(1));
            Assert.That(located[0].SourceLocator, Is.EqualTo("pages 1-2"));
        });
    }

    [Test]
    public void Segmenter_Preserves_Block_Metadata_When_Splitting_Oversized_Blocks()
    {
        var text = string.Join(' ', Enumerable.Repeat("metadata-token", 180)) + "\n";
        var parsed = new FabricParsedDocument(
            "parser",
            "version",
            "application/pdf",
            text,
            [
                new FabricParsedBlock(
                    0,
                    text.Length,
                    "Manual",
                    text.TrimEnd('\n'),
                    "table",
                    7,
                    "pages 7-8",
                    0.82),
            ],
            []);

        var segments = new FabricSegmenter(new FabricSegmenterOptions(64, 96, 0))
            .Segment("doc-metadata", parsed);

        Assert.Multiple(() =>
        {
            Assert.That(segments, Has.Count.GreaterThan(1));
            Assert.That(segments, Has.All.Matches<FabricSegmentDraft>(segment =>
                segment.BlockKind == "table" &&
                segment.PageNumber == 7 &&
                segment.SourceLocator == "pages 7-8" &&
                segment.Confidence == 0.82));
        });
    }

    [Test]
    public void Segmenter_Aggregates_Page_Metadata_When_Segment_Spans_Multiple_Blocks()
    {
        var first = new string('a', 150);
        var second = new string('b', 150);
        var text = $"{first}\n\n{second}\n";
        var parsed = new FabricParsedDocument(
            "parser",
            "version",
            "application/pdf",
            text,
            [
                new FabricParsedBlock(0, first.Length, "Manual", first, "text", 2, "page 2", 0.91),
                new FabricParsedBlock(first.Length + 2, first.Length + 2 + second.Length, "Manual", second, "text", 3, "page 3", 0.73),
            ],
            []);

        var segments = new FabricSegmenter(new FabricSegmenterOptions(64, 96, 0))
            .Segment("doc-merged-pages", parsed);

        Assert.Multiple(() =>
        {
            Assert.That(segments[0].PageNumber, Is.EqualTo(2));
            Assert.That(segments[0].SourceLocator, Is.EqualTo("pages 2-3"));
            Assert.That(segments[0].Confidence, Is.EqualTo(0.73));
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
    public async Task ImportFileAsync_Creates_Immutable_Versions_And_Supersedes_Prior_Source()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Versioned source");
        var sourcePath = Path.Combine(harness.Root, "versioned.txt");
        await File.WriteAllTextAsync(sourcePath, "alpha old token.\n");
        var first = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        await File.WriteAllTextAsync(sourcePath, "beta new token.\n");

        var second = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);

        var firstPersisted = harness.Repository.GetDocument(first.Document.DocumentId)!;
        var secondPersisted = harness.Repository.GetDocument(second.Document.DocumentId)!;
        Assert.Multiple(() =>
        {
            Assert.That(second.Document.DocumentId, Is.Not.EqualTo(first.Document.DocumentId));
            Assert.That(firstPersisted.Status, Is.EqualTo("superseded"));
            Assert.That(firstPersisted.SupersededByDocumentId, Is.EqualTo(second.Document.DocumentId));
            Assert.That(firstPersisted.SupersededAt, Is.Not.Null);
            Assert.That(firstPersisted.VersionOrdinal, Is.EqualTo(1));
            Assert.That(secondPersisted.Status, Is.EqualTo("ready"));
            Assert.That(secondPersisted.VersionOrdinal, Is.EqualTo(2));
            Assert.That(secondPersisted.SupersededByDocumentId, Is.Null);
            Assert.That(harness.Service.ListDocuments(corpus.CorpusId), Has.Count.EqualTo(2));
            Assert.That(harness.Service.Search("alpha", corpus.CorpusId), Is.Empty);
            Assert.That(harness.Service.Search("beta", corpus.CorpusId), Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task ImportFileAsync_Reverted_Source_Creates_New_Visible_Version()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Reverted source");
        var sourcePath = Path.Combine(harness.Root, "reverted.txt");
        await File.WriteAllTextAsync(sourcePath, "alpha restored token.\n");
        var first = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        await File.WriteAllTextAsync(sourcePath, "beta middle token.\n");
        var second = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        await File.WriteAllTextAsync(sourcePath, "alpha restored token.\n");

        var third = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var rebuiltThird = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);

        Assert.Multiple(() =>
        {
            Assert.That(third.Document.DocumentId, Is.Not.EqualTo(first.Document.DocumentId));
            Assert.That(third.Document.DocumentId, Is.Not.EqualTo(second.Document.DocumentId));
            Assert.That(third.Document.VersionOrdinal, Is.EqualTo(3));
            Assert.That(third.Document.Status, Is.EqualTo("ready"));
            Assert.That(harness.Repository.GetDocument(first.Document.DocumentId)!.Status, Is.EqualTo("superseded"));
            Assert.That(harness.Repository.GetDocument(second.Document.DocumentId)!.Status, Is.EqualTo("superseded"));
            Assert.That(harness.Repository.GetDocument(first.Document.DocumentId)!.SupersededByDocumentId, Is.EqualTo(second.Document.DocumentId));
            Assert.That(harness.Repository.GetDocument(second.Document.DocumentId)!.SupersededByDocumentId, Is.EqualTo(third.Document.DocumentId));
            Assert.That(harness.Service.Search("alpha", corpus.CorpusId).Select(hit => hit.DocumentId), Is.EqualTo(new[] { third.Document.DocumentId }));
            Assert.That(harness.Service.Search("beta", corpus.CorpusId), Is.Empty);
            Assert.That(rebuiltThird.Rebuilt, Is.True);
            Assert.That(rebuiltThird.Document.DocumentId, Is.EqualTo(third.Document.DocumentId));
            Assert.That(rebuiltThird.Document.VersionOrdinal, Is.EqualTo(third.Document.VersionOrdinal));
            Assert.That(harness.Service.ListDocuments(corpus.CorpusId), Has.Count.EqualTo(3));
        });
    }

    [Test]
    public async Task ImportFileAsync_Same_Bytes_With_Different_Names_Create_Distinct_Documents()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Same bytes");
        var firstPath = Path.Combine(harness.Root, "first.txt");
        var secondPath = Path.Combine(harness.Root, "second.txt");
        await File.WriteAllTextAsync(firstPath, "shared source token.\n");
        await File.WriteAllTextAsync(secondPath, "shared source token.\n");

        var first = await harness.Service.ImportFileAsync(corpus.CorpusId, firstPath);
        var second = await harness.Service.ImportFileAsync(corpus.CorpusId, secondPath);

        Assert.Multiple(() =>
        {
            Assert.That(second.Document.DocumentId, Is.Not.EqualTo(first.Document.DocumentId));
            Assert.That(first.Document.SourceDigest, Is.EqualTo(second.Document.SourceDigest));
            Assert.That(first.Document.Status, Is.EqualTo("ready"));
            Assert.That(second.Document.Status, Is.EqualTo("ready"));
            Assert.That(harness.Service.Search("shared", corpus.CorpusId).Select(hit => hit.DocumentId).Distinct(),
                Is.EquivalentTo(new[] { first.Document.DocumentId, second.Document.DocumentId }));
        });
    }

    [Test]
    public async Task RebuildDocumentAsync_Resets_Active_NeedsRebuild_Document_To_Ready()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Rebuild status");
        var sourcePath = Path.Combine(harness.Root, "rebuild.txt");
        await File.WriteAllTextAsync(sourcePath, "recoverable active document.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        using (var connection = store.Open())
        {
            Execute(connection, $"""
                UPDATE fabric_documents
                SET status = 'needs_rebuild'
                WHERE document_id = '{imported.Document.DocumentId}';
                """);
        }

        var rebuilt = await harness.Service.RebuildDocumentAsync(imported.Document.DocumentId);

        Assert.Multiple(() =>
        {
            Assert.That(rebuilt.Rebuilt, Is.True);
            Assert.That(rebuilt.Document.Status, Is.EqualTo("ready"));
            Assert.That(rebuilt.Document.SupersededByDocumentId, Is.Null);
            Assert.That(harness.Repository.GetDocument(imported.Document.DocumentId)!.Status, Is.EqualTo("ready"));
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
    public async Task Library_Garbage_Collects_Unreferenced_Artifacts_After_Delete()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("GC single");
        var sourcePath = Path.Combine(harness.Root, "gc-single.txt");
        await File.WriteAllTextAsync(sourcePath, "Garbage collection candidate.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var expectedDeletes = new[] { imported.Document.SourceDigest, imported.Document.NormalizedDigest }
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.That(harness.Service.DeleteCorpus(corpus.CorpusId), Is.True);

        var deleted = harness.Service.DeleteUnreferencedArtifacts();

        Assert.Multiple(() =>
        {
            Assert.That(deleted, Is.EqualTo(expectedDeletes));
            Assert.That(harness.Artifacts.Has(imported.Document.SourceDigest), Is.False);
            Assert.That(harness.Artifacts.Has(imported.Document.NormalizedDigest), Is.False);
        });
    }

    [Test]
    public async Task Library_Garbage_Collection_Keeps_Shared_Artifacts_Until_Last_Reference_Is_Gone()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var firstCorpus = harness.Service.CreateCorpus("GC first");
        var secondCorpus = harness.Service.CreateCorpus("GC second");
        var sourcePath = Path.Combine(harness.Root, "gc-shared.txt");
        await File.WriteAllTextAsync(sourcePath, "Shared content survives one delete.\n");
        var first = await harness.Service.ImportFileAsync(firstCorpus.CorpusId, sourcePath);
        var second = await harness.Service.ImportFileAsync(secondCorpus.CorpusId, sourcePath);
        var expectedDeletes = new[] { second.Document.SourceDigest, second.Document.NormalizedDigest }
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.That(first.Document.SourceDigest, Is.EqualTo(second.Document.SourceDigest));
        Assert.That(first.Document.NormalizedDigest, Is.EqualTo(second.Document.NormalizedDigest));

        Assert.That(harness.Service.DeleteCorpus(firstCorpus.CorpusId), Is.True);
        Assert.That(harness.Service.DeleteUnreferencedArtifacts(), Is.EqualTo(0));
        Assert.That(harness.Artifacts.Has(second.Document.SourceDigest), Is.True);
        Assert.That(harness.Artifacts.Has(second.Document.NormalizedDigest), Is.True);

        Assert.That(harness.Service.DeleteCorpus(secondCorpus.CorpusId), Is.True);
        Assert.That(harness.Service.DeleteUnreferencedArtifacts(), Is.EqualTo(expectedDeletes));
        Assert.That(harness.Artifacts.Has(second.Document.SourceDigest), Is.False);
        Assert.That(harness.Artifacts.Has(second.Document.NormalizedDigest), Is.False);
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
    public async Task Repository_ReplaceDocument_Rejects_Identity_Changes()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Identity replacement");
        var otherCorpus = harness.Service.CreateCorpus("Other corpus");
        var sourcePath = Path.Combine(harness.Root, "identity.txt");
        await File.WriteAllTextAsync(sourcePath, "Original content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var replacements = new[]
        {
            imported.Document with { CorpusId = otherCorpus.CorpusId },
            imported.Document with { SourceDigest = new string('a', 64) },
            imported.Document with { MediaType = "text/markdown" },
            imported.Document with { ParserId = "replacement-parser" },
            imported.Document with { ParserVersion = "2" },
        };

        foreach (var replacement in replacements)
        {
            Assert.That(
                () => harness.Repository.ReplaceDocument(replacement, [Draft("seg-replacement", 0, "replacement")]),
                Throws.TypeOf<InvalidDataException>());
        }
        var persisted = harness.Repository.GetDocument(imported.Document.DocumentId)!;

        Assert.Multiple(() =>
        {
            Assert.That(persisted.CorpusId, Is.EqualTo(imported.Document.CorpusId));
            Assert.That(persisted.SourceDigest, Is.EqualTo(imported.Document.SourceDigest));
            Assert.That(persisted.MediaType, Is.EqualTo(imported.Document.MediaType));
            Assert.That(persisted.ParserId, Is.EqualTo(imported.Document.ParserId));
            Assert.That(persisted.ParserVersion, Is.EqualTo(imported.Document.ParserVersion));
            Assert.That(harness.Repository.GetSegments(imported.Document.DocumentId).Select(segment => segment.SegmentId),
                Is.EqualTo(imported.Segments.Select(segment => segment.SegmentId)));
        });
    }

    [Test]
    public async Task Repository_ReplaceDocument_Touches_Owning_Corpus_Timestamp()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Timestamp owner");
        var sourcePath = Path.Combine(harness.Root, "timestamp.txt");
        await File.WriteAllTextAsync(sourcePath, "Original content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var updatedDocument = imported.Document with { UpdatedAt = imported.Document.UpdatedAt.AddMinutes(5) };

        harness.Repository.ReplaceDocument(updatedDocument, [Draft("seg-replacement", 0, "replacement")]);

        Assert.That(harness.Repository.GetCorpus(corpus.CorpusId)!.UpdatedAt, Is.EqualTo(updatedDocument.UpdatedAt));
    }

    [Test]
    public async Task Repository_ReplaceDocument_Persists_And_Searches_Segment_Provenance()
    {
        var harness = NewHarness();
        using var store = harness.Store;
        var corpus = harness.Service.CreateCorpus("Provenance persistence");
        var sourcePath = Path.Combine(harness.Root, "provenance.txt");
        await File.WriteAllTextAsync(sourcePath, "Original content.\n");
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var replacement = Draft("seg-provenance", 0, "needle provenance")
            with
            {
                BlockKind = "table",
                PageNumber = 4,
                SourceLocator = "pages 4-5",
                Confidence = 0.67,
            };

        harness.Repository.ReplaceDocument(imported.Document, [replacement]);
        var persisted = harness.Repository.GetSegment(replacement.SegmentId)!;
        var searchHit = harness.Repository.Search("needle", corpus.CorpusId).Single();

        Assert.Multiple(() =>
        {
            Assert.That(persisted.BlockKind, Is.EqualTo("table"));
            Assert.That(persisted.PageNumber, Is.EqualTo(4));
            Assert.That(persisted.SourceLocator, Is.EqualTo("pages 4-5"));
            Assert.That(persisted.Confidence, Is.EqualTo(0.67));
            Assert.That(searchHit.BlockKind, Is.EqualTo("table"));
            Assert.That(searchHit.PageNumber, Is.EqualTo(4));
            Assert.That(searchHit.SourceLocator, Is.EqualTo("pages 4-5"));
            Assert.That(searchHit.Confidence, Is.EqualTo(0.67));
        });
    }

    [Test]
    public async Task DarwinFixture_Imports_And_Rebuilds_Reproducibly()
    {
        await AssertFixtureImportsAndRebuildsReproducibly(
            LoadDarwinFixtureManifest(),
            GetDarwinFixturePath(),
            "Darwin fixture");
    }

    [Test]
    public async Task DarwinPdfFixture_Imports_And_Rebuilds_Reproducibly()
    {
        await AssertFixtureImportsAndRebuildsReproducibly(
            LoadDarwinPdfFixtureManifest(),
            GetDarwinPdfFixturePath(),
            "Darwin PDF fixture");
    }

    [Test]
    public async Task ConstitutionFixture_Imports_And_Rebuilds_Reproducibly()
    {
        await AssertFixtureImportsAndRebuildsReproducibly(
            LoadFixtureManifest("united-states-constitution-full.manifest.json"),
            GetFixturePath("united-states-constitution-full.txt"),
            "Constitution fixture");
    }

    [Test]
    public async Task FederalistFixture_Imports_And_Rebuilds_Reproducibly()
    {
        await AssertFixtureImportsAndRebuildsReproducibly(
            LoadFixtureManifest("the-federalist-papers.manifest.json"),
            GetFixturePath("the-federalist-papers.txt"),
            "Federalist fixture");
    }

    [Test]
    public void MigrationV9_Retrofits_Segment_Constraints_And_Preserves_Search_Text()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        Execute(connection, """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT
            );
            """);
        Execute(connection, Migrations.All.Single(migration => migration.Version == 8).Sql);
        for (var version = 1; version <= 8; version++)
            Execute(connection, $"INSERT INTO schema_migrations VALUES ({version}, 'now', 'test')");
        Execute(connection, """
            INSERT INTO fabric_corpora VALUES ('corpus', 'Corpus', NULL, 'default', 'ready', 'now', 'now');
            INSERT INTO fabric_documents VALUES (
                'document', 'corpus', 'source', 'normalized', 'Document', 'text/plain',
                'parser', '1', 'ready', '[]', 'now', 'now');
            INSERT INTO fabric_segments VALUES (
                'segment', 'document', 0, NULL, 0, 4, 1, 'digest', NULL, NULL, '1');
            INSERT INTO fabric_segment_text VALUES ('segment', NULL, 'kept');
            INSERT INTO fabric_documents VALUES (
                'invalid-document', 'corpus', 'invalid-source', 'invalid-normalized',
                'Invalid document', 'text/plain', 'parser', '1', 'ready', '[]', 'now', 'now');
            INSERT INTO fabric_segments VALUES (
                'invalid-segment', 'invalid-document', 0, NULL, -1, 4, 1,
                'invalid-digest', NULL, NULL, '1');
            INSERT INTO fabric_segment_text VALUES ('invalid-segment', NULL, 'discarded-derived-text');
            """);

        MigrationRunner.Apply(connection);

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 9"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM pragma_foreign_key_check"), Is.Zero);
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segment_text WHERE normalized_text = 'kept'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segment_fts WHERE fabric_segment_fts MATCH 'kept'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segments WHERE document_id = 'invalid-document'"), Is.Zero);
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segment_fts WHERE fabric_segment_fts MATCH 'discarded'"), Is.Zero);
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_documents WHERE document_id = 'invalid-document' AND status = 'needs_rebuild'"), Is.EqualTo(1));
            Assert.That(
                () => Execute(connection, "UPDATE fabric_segments SET char_start = -1 WHERE segment_id = 'segment'"),
                Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        });
    }

    [Test]
    public void MigrationV13_Adds_Segment_Provenance_Columns_With_Defaults()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        Execute(connection, """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT
            );
            """);
        foreach (var migration in Migrations.All.Where(migration => migration.Version <= 12))
        {
            Execute(connection, migration.Sql);
            Execute(connection, $"INSERT INTO schema_migrations VALUES ({migration.Version}, 'now', 'test')");
        }

        Execute(connection, """
            INSERT INTO fabric_corpora VALUES ('corpus', 'Corpus', NULL, 'default', 'ready', 'now', 'now');
            INSERT INTO fabric_documents VALUES (
                'document', 'corpus', 'source', 'normalized', 'Document', 'text/plain',
                'parser', '1', 'ready', '[]', 'now', 'now');
            INSERT INTO fabric_segments
                (segment_id, document_id, ordinal, heading_path, char_start, char_end,
                 token_count, text_digest, previous_segment_id, next_segment_id, chunker_version)
            VALUES
                ('segment', 'document', 0, NULL, 0, 4, 1, 'digest', NULL, NULL, '1');
            INSERT INTO fabric_segment_text VALUES ('segment', NULL, 'kept');
            """);

        MigrationRunner.Apply(connection);

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 13"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segments WHERE segment_id = 'segment' AND block_kind = 'text'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_segments WHERE segment_id = 'segment' AND page_number IS NULL"), Is.EqualTo(1));
            Assert.That(
                () => Execute(connection, "UPDATE fabric_segments SET confidence = 2.0 WHERE segment_id = 'segment'"),
                Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        });
    }

    [Test]
    public void MigrationV14_Adds_Document_Version_Columns_With_Defaults()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            "Data Source=:memory:;Foreign Keys=True");
        connection.Open();
        Execute(connection, """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                applied_at TEXT NOT NULL,
                description TEXT
            );
            """);
        foreach (var migration in Migrations.All.Where(migration => migration.Version <= 13))
        {
            Execute(connection, migration.Sql);
            Execute(connection, $"INSERT INTO schema_migrations VALUES ({migration.Version}, 'now', 'test')");
        }

        Execute(connection, """
            INSERT INTO fabric_corpora VALUES ('corpus', 'Corpus', NULL, 'default', 'ready', 'now', 'now');
            INSERT INTO fabric_documents
                (document_id, corpus_id, source_digest, normalized_digest, display_name,
                 media_type, parser_id, parser_version, status, warnings_json, created_at, updated_at)
            VALUES
                ('document-v1', 'corpus', 'source-1', 'normalized-1', 'Document', 'text/plain',
                 'parser', '1', 'ready', '[]', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z'),
                ('document-v2', 'corpus', 'source-2', 'normalized-2', 'Document', 'text/plain',
                 'parser', '1', 'ready', '[]', '2026-01-02T00:00:00.0000000Z', '2026-01-02T00:00:00.0000000Z'),
                ('other-document', 'corpus', 'source-3', 'normalized-3', 'Other', 'text/plain',
                 'parser', '1', 'ready', '[]', '2026-01-01T00:00:00.0000000Z', '2026-01-01T00:00:00.0000000Z');
            """);

        MigrationRunner.Apply(connection);

        Assert.Multiple(() =>
        {
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM schema_migrations WHERE version = 14"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT version_ordinal FROM fabric_documents WHERE document_id = 'document-v1'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT version_ordinal FROM fabric_documents WHERE document_id = 'document-v2'"), Is.EqualTo(2));
            Assert.That(Scalar(connection, "SELECT version_ordinal FROM fabric_documents WHERE document_id = 'other-document'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_documents WHERE document_id = 'document-v1' AND status = 'superseded' AND superseded_by_document_id = 'document-v2'"), Is.EqualTo(1));
            Assert.That(Scalar(connection, "SELECT COUNT(*) FROM fabric_documents WHERE document_id = 'document-v2' AND status = 'ready' AND superseded_by_document_id IS NULL"), Is.EqualTo(1));
            Assert.That(
                () => Execute(connection, "UPDATE fabric_documents SET version_ordinal = 0 WHERE document_id = 'document-v2'"),
                Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
            Assert.That(
                () => Execute(connection, "UPDATE fabric_documents SET version_ordinal = 2 WHERE document_id = 'document-v1'"),
                Throws.TypeOf<Microsoft.Data.Sqlite.SqliteException>());
        });
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

    private static void Execute(Microsoft.Data.Sqlite.SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
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

    private static string GetFixturePath(string fileName) => Path.Combine(
        TestContext.CurrentContext.TestDirectory,
        "TestData",
        "ContextFabric",
        fileName);

    private static string GetDarwinFixturePath() => GetFixturePath("darwin-origin-species-2009.txt");

    private static string GetDarwinPdfFixturePath() => GetFixturePath("darwin-origin-species-primary.pdf");

    private static DarwinFixtureManifest LoadDarwinFixtureManifest() =>
        LoadFixtureManifest("darwin-origin-species-2009.manifest.json");

    private static DarwinFixtureManifest LoadDarwinPdfFixtureManifest() =>
        LoadFixtureManifest("darwin-origin-species-primary-pdf.manifest.json");

    private static DarwinFixtureManifest LoadFixtureManifest(string fileName)
    {
        var path = GetFixturePath(fileName);
        return JsonSerializer.Deserialize<DarwinFixtureManifest>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Fixture manifest '{fileName}' did not deserialize.");
    }

    private static string SegmentIdsDigest(IReadOnlyList<FabricSegmentEntry> segments)
    {
        var joined = string.Join('\n', segments.Select(segment => segment.SegmentId));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }

    private static string BuildDarwinActualMessage(FabricImportResult result) =>
        JsonSerializer.Serialize(new
        {
            result.Document.DocumentId,
            result.Document.SourceDigest,
            NormalizedSha256 = result.Document.NormalizedDigest,
            result.Document.ParserId,
            result.Document.ParserVersion,
            SegmentCount = result.Segments.Count,
            SegmentIdsSha256 = SegmentIdsDigest(result.Segments),
            FirstSegmentId = result.Segments[0].SegmentId,
            LastSegmentId = result.Segments[^1].SegmentId,
        });

    private static byte[] BuildTwoPagePdfWithBlankFirstPage()
    {
        var body = "BT /F1 12 Tf 72 720 Td (Second page sentinel) Tj ET";
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            $"<< /Length {body.Length} >>\nstream\n{body}\nendstream",
        };
        return BuildPdf(objects);
    }

    private static byte[] BuildBlankPdf()
    {
        var objects = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << >> >>",
        };
        return BuildPdf(objects);
    }

    private static byte[] BuildPdf(IReadOnlyList<string> objects)
    {
        var bytes = new List<byte>();
        void Append(string text) => bytes.AddRange(Encoding.ASCII.GetBytes(text));

        Append("%PDF-1.4\n");
        var offsets = new List<int> { 0 };
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(bytes.Count);
            Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = bytes.Count;
        Append($"xref\n0 {offsets.Count}\n");
        Append("0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
            Append($"{offset:0000000000} 00000 n \n");
        Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return bytes.ToArray();
    }

    private static byte[] BuildDocxPackage()
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, DocumentFormat.OpenXml.WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(
                new Paragraph(new Run(new Text("DOCX introduction"))),
                new Table(
                    new TableRow(
                        new TableCell(new Paragraph(new Run(new Text("Key")))),
                        new TableCell(new Paragraph(new Run(new Text("Value")))))),
                new Paragraph(
                    new Run(new Drawing()),
                    new Run(new Text("Figure 1. Diagram")))));
        }

        return stream.ToArray();
    }

    private static byte[] BuildEpubPackage(bool encrypted, bool duplicateManifestId = false)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntry("OEBPS/");
            WriteZipEntry(archive, "META-INF/container.xml", """
                <?xml version="1.0" encoding="utf-8"?>
                <container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
                  <rootfiles>
                    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml" />
                  </rootfiles>
                </container>
                """);
            if (encrypted)
                WriteZipEntry(archive, "META-INF/encryption.xml", "<encryption />");
            WriteZipEntry(archive, "OEBPS/content.opf", """
                <?xml version="1.0" encoding="utf-8"?>
                <package version="3.0" xmlns="http://www.idpf.org/2007/opf">
                  <manifest>
                    <item id="chapter" href="chapter.xhtml" media-type="application/xhtml+xml" />
                    DUPLICATE_MANIFEST_ITEM
                  </manifest>
                  <spine>
                    <itemref idref="chapter" />
                  </spine>
                </package>
                """.Replace(
                    "DUPLICATE_MANIFEST_ITEM",
                    duplicateManifestId
                        ? """<item id="chapter" href="cover.png" media-type="image/png" />"""
                        : ""));
            WriteZipEntry(archive, "OEBPS/chapter.xhtml", """
                <?xml version="1.0" encoding="utf-8"?>
                <html xmlns="http://www.w3.org/1999/xhtml">
                  <body>
                    <h1>EPUB Heading</h1>
                    <p>EPUB introduction.</p>
                    <table><tr><td>Key</td><td>Value</td></tr></table>
                    <figure><figcaption>Figure 1. Diagram</figcaption></figure>
                  </body>
                </html>
                """);
        }

        return stream.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private async Task AssertFixtureImportsAndRebuildsReproducibly(
        DarwinFixtureManifest manifest,
        string sourcePath,
        string corpusName)
    {
        var harness = NewHarness(maximumSourceBytes: 4L * 1024 * 1024);
        using var store = harness.Store;
        var corpus = harness.Repository.CreateCorpus(manifest.FixtureId, corpusName);
        var imported = await harness.Service.ImportFileAsync(corpus.CorpusId, sourcePath);
        var rebuilt = await harness.Service.RebuildDocumentAsync(imported.Document.DocumentId);
        var segmentIdsDigest = SegmentIdsDigest(imported.Segments);

        Assert.Multiple(() =>
        {
            Assert.That(imported.Rebuilt, Is.False);
            Assert.That(rebuilt.Rebuilt, Is.True);
            Assert.That(imported.Document.SourceDigest, Is.EqualTo(manifest.SourceSha256), BuildDarwinActualMessage(imported));
            Assert.That(imported.Document.DocumentId, Is.EqualTo(manifest.ExpectedDocumentId), BuildDarwinActualMessage(imported));
            Assert.That(imported.Document.NormalizedDigest, Is.EqualTo(manifest.ExpectedNormalizedSha256), BuildDarwinActualMessage(imported));
            Assert.That(imported.Document.ParserId, Is.EqualTo(manifest.ParserId));
            Assert.That(imported.Document.ParserVersion, Is.EqualTo(manifest.ParserVersion));
            Assert.That(imported.Segments, Has.Count.EqualTo(manifest.ExpectedSegmentCount), BuildDarwinActualMessage(imported));
            Assert.That(segmentIdsDigest, Is.EqualTo(manifest.ExpectedSegmentIdsSha256), BuildDarwinActualMessage(imported));
            Assert.That(imported.Segments[0].SegmentId, Is.EqualTo(manifest.ExpectedFirstSegmentId), BuildDarwinActualMessage(imported));
            Assert.That(imported.Segments[^1].SegmentId, Is.EqualTo(manifest.ExpectedLastSegmentId), BuildDarwinActualMessage(imported));
            Assert.That(rebuilt.Document.DocumentId, Is.EqualTo(imported.Document.DocumentId));
            Assert.That(rebuilt.Document.NormalizedDigest, Is.EqualTo(imported.Document.NormalizedDigest));
            Assert.That(rebuilt.Segments.Select(segment => segment.SegmentId), Is.EqualTo(imported.Segments.Select(segment => segment.SegmentId)));
        });
    }

    private sealed record Harness(
        string Root,
        SqliteStore Store,
        FabricLibraryRepository Repository,
        ContentAddressedStore Artifacts,
        FabricLibraryService Service);

    private sealed class FakeOcrEngine(string text, double? confidence, string? warning) : IFabricOcrEngine
    {
        public List<int> PageNumbers { get; } = [];

        public FabricOcrPageResult RecognizePdfPage(int pageNumber, ReadOnlyMemory<byte> pdfSource)
        {
            PageNumbers.Add(pageNumber);
            return new(text, confidence, warning is null ? [] : [warning]);
        }
    }

    private sealed record DarwinFixtureManifest(
        string FixtureId,
        string SourceUrl,
        DateTimeOffset DownloadedAtUtc,
        string Edition,
        string MediaType,
        string SourceSha256,
        string ParserId,
        string ParserVersion,
        string SegmenterVersion,
        string ExpectedDocumentId,
        string ExpectedNormalizedSha256,
        int ExpectedSegmentCount,
        string ExpectedSegmentIdsSha256,
        string ExpectedFirstSegmentId,
        string ExpectedLastSegmentId);
}
