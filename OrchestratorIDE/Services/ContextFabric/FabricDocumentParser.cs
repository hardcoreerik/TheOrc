// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OrchestratorIDE.Services.ContextFabric;

public interface IFabricDocumentParser
{
    string ParserId { get; }
    string ParserVersion { get; }
    bool Supports(string mediaType);
    FabricParsedDocument Parse(ReadOnlyMemory<byte> source, string mediaType);
}

public sealed class FabricDocumentParserRegistry
{
    private readonly IReadOnlyList<IFabricDocumentParser> _parsers;

    public FabricDocumentParserRegistry(IEnumerable<IFabricDocumentParser>? parsers = null)
    {
        _parsers = (parsers ?? [
            new TextMarkdownFabricParser(),
            new PdfTextFabricParser(),
            new DocxFabricParser(),
            new EpubFabricParser(),
        ]).ToArray();
    }

    public IFabricDocumentParser Resolve(string mediaType) => _parsers
        .FirstOrDefault(parser => parser.Supports(mediaType))
        ?? throw new NotSupportedException($"No Context Fabric parser supports media type '{mediaType}'.");

    public IFabricDocumentParser Resolve(string parserId, string parserVersion) => _parsers
        .FirstOrDefault(parser =>
            parser.ParserId.Equals(parserId, StringComparison.Ordinal) &&
            parser.ParserVersion.Equals(parserVersion, StringComparison.Ordinal))
        ?? throw new NotSupportedException($"Context Fabric parser '{parserId}' version '{parserVersion}' is unavailable.");
}

public sealed class TextMarkdownFabricParser : IFabricDocumentParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex MarkdownHeading = new(
        @"^(?<level>#{1,6})[ \t]+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string ParserId => "fabric-text-markdown";
    public string ParserVersion => FabricIngestionVersions.TextMarkdownParser;

    public bool Supports(string mediaType) =>
        mediaType.Equals("text/plain", StringComparison.OrdinalIgnoreCase) ||
        mediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase);

    public FabricParsedDocument Parse(ReadOnlyMemory<byte> source, string mediaType)
    {
        if (!Supports(mediaType))
            throw new NotSupportedException($"Parser does not support '{mediaType}'.");
        if (source.IsEmpty)
            throw new InvalidDataException("Document is empty.");

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(source.Span);
        }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidDataException("Document is not valid UTF-8.", ex);
        }

        var normalized = FabricTextParsing.Normalize(decoded);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("Document contains no text.");

        var blocks = FabricTextParsing.BuildBlocks(normalized, mediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase));
        if (blocks.Count == 0)
            throw new InvalidDataException("Document contains no parseable blocks.");

        return new FabricParsedDocument(
            ParserId,
            FabricIngestionVersions.TextMarkdownParser,
            mediaType.ToLowerInvariant(),
            normalized,
            blocks,
            []);
    }
}

public sealed class PdfTextFabricParser : IFabricDocumentParser
{
    public string ParserId => "fabric-pdf-text";
    public string ParserVersion => FabricIngestionVersions.PdfTextParser;

    public bool Supports(string mediaType) =>
        mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase);

    public FabricParsedDocument Parse(ReadOnlyMemory<byte> source, string mediaType)
    {
        if (!Supports(mediaType))
            throw new NotSupportedException($"Parser does not support '{mediaType}'.");
        if (source.IsEmpty)
            throw new InvalidDataException("Document is empty.");

        using var document = PdfDocument.Open(source.ToArray());
        var pages = document.GetPages().ToArray();
        if (pages.Length == 0)
            throw new InvalidDataException("PDF contains no pages.");

        var pageTexts = pages
            .Select((page, index) => (PageNumber: index + 1, Text: ExtractPageText(page)))
            .Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .ToArray();
        if (pageTexts.Length == 0)
            throw new InvalidDataException("PDF contains no extractable text.");

        var normalized = FabricTextParsing.Normalize(string.Join("\n\n", pageTexts.Select(page => page.Text)));
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("PDF contains no parseable text.");

        var blocks = FabricTextParsing.AddPageLocators(
            FabricTextParsing.BuildBlocks(normalized, markdown: false),
            normalized,
            pageTexts);
        if (blocks.Count == 0)
            throw new InvalidDataException("PDF contains no parseable blocks.");

        return new FabricParsedDocument(
            ParserId,
            ParserVersion,
            "application/pdf",
            normalized,
            blocks,
            []);
    }

    private static string ExtractPageText(Page page)
    {
        var lines = page.Text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0);
        return string.Join('\n', lines);
    }
}

public sealed class DocxFabricParser : IFabricDocumentParser
{
    public string ParserId => "fabric-docx";
    public string ParserVersion => FabricIngestionVersions.DocxParser;

    public bool Supports(string mediaType) =>
        mediaType.Equals("application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase);

    public FabricParsedDocument Parse(ReadOnlyMemory<byte> source, string mediaType)
    {
        if (!Supports(mediaType))
            throw new NotSupportedException($"Parser does not support '{mediaType}'.");
        if (source.IsEmpty)
            throw new InvalidDataException("Document is empty.");

        try
        {
            using var stream = new MemoryStream(source.ToArray(), writable: false);
            using var document = WordprocessingDocument.Open(stream, isEditable: false);
            var body = document.MainDocumentPart?.Document?.Body
                ?? throw new InvalidDataException("DOCX contains no document body.");
            var structured = BuildStructuredBlocks(body.Elements(), "docx block").ToArray();
            if (structured.Length == 0)
                throw new InvalidDataException("DOCX contains no parseable text.");

            var (normalized, blocks) = FabricTextParsing.BuildStructuredBlocks(structured);
            return new FabricParsedDocument(
                ParserId,
                ParserVersion,
                "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                normalized,
                blocks,
                []);
        }
        catch (OpenXmlPackageException ex)
        {
            throw new InvalidDataException("DOCX package is malformed or unsupported.", ex);
        }
        catch (FileFormatException ex)
        {
            throw new InvalidDataException("DOCX package is malformed or unsupported.", ex);
        }
    }

    private static IEnumerable<FabricStructuredBlock> BuildStructuredBlocks(
        IEnumerable<OpenXmlElement> elements,
        string locatorPrefix)
    {
        var ordinal = 1;
        foreach (var element in elements)
        {
            FabricStructuredBlock? block = element switch
            {
                Table table => BuildTable(table, $"{locatorPrefix} {ordinal}"),
                Paragraph paragraph => BuildParagraph(paragraph, $"{locatorPrefix} {ordinal}"),
                _ => null,
            };

            if (block is not null)
            {
                yield return block.Value;
                ordinal++;
            }
        }
    }

    private static FabricStructuredBlock? BuildParagraph(Paragraph paragraph, string sourceLocator)
    {
        var text = CollapseText(paragraph.Descendants<Text>().Select(node => node.Text));
        var hasDrawing = paragraph.Descendants<Drawing>().Any();
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "";
        var kind = hasDrawing || style.Equals("Caption", StringComparison.OrdinalIgnoreCase)
            ? "figure"
            : style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase) ? "heading" : "text";

        if (string.IsNullOrWhiteSpace(text))
        {
            if (!hasDrawing)
                return null;
            text = "[figure]";
        }

        return new FabricStructuredBlock(text, kind, null, sourceLocator);
    }

    private static FabricStructuredBlock? BuildTable(Table table, string sourceLocator)
    {
        var rows = table.Elements<TableRow>()
            .Select(row => string.Join(" | ", row.Elements<TableCell>()
                .Select(cell => CollapseText(cell.Descendants<Text>().Select(node => node.Text)))
                .Where(text => text.Length > 0)))
            .Where(row => row.Length > 0)
            .ToArray();
        if (rows.Length == 0)
            return null;

        return new FabricStructuredBlock(string.Join('\n', rows), "table", null, sourceLocator);
    }

    private static string CollapseText(IEnumerable<string> parts) => string.Join(" ", parts
            .Select(part => part.Trim())
            .Where(part => part.Length > 0))
        .Trim();
}

public sealed class EpubFabricParser : IFabricDocumentParser
{
    private const long MaxXmlEntryBytes = 16L * 1024 * 1024;
    private const long MaxTotalXmlBytes = 32L * 1024 * 1024;

    public string ParserId => "fabric-epub";
    public string ParserVersion => FabricIngestionVersions.EpubParser;

    public bool Supports(string mediaType) =>
        mediaType.Equals("application/epub+zip", StringComparison.OrdinalIgnoreCase);

    public FabricParsedDocument Parse(ReadOnlyMemory<byte> source, string mediaType)
    {
        if (!Supports(mediaType))
            throw new NotSupportedException($"Parser does not support '{mediaType}'.");
        if (source.IsEmpty)
            throw new InvalidDataException("Document is empty.");

        try
        {
            using var stream = new MemoryStream(source.ToArray(), writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.GetEntry("META-INF/encryption.xml") is not null ||
                archive.GetEntry("META-INF/rights.xml") is not null)
            {
                throw new InvalidDataException("EPUB encryption or rights management is not supported.");
            }

            var budget = new EpubXmlBudget();
            var packagePath = GetPackagePath(archive, budget);
            var packageEntry = GetSafeEntry(archive, packagePath)
                ?? throw new InvalidDataException("EPUB package document is missing.");
            XDocument package;
            using (var packageStream = OpenXmlEntry(packageEntry, budget))
                package = LoadXml(packageStream);

            var allManifestItems = package.Descendants()
                .Where(element => element.Name.LocalName == "item")
                .Select(element => new
                {
                    Id = (string?)element.Attribute("id"),
                    Href = (string?)element.Attribute("href"),
                    MediaType = (string?)element.Attribute("media-type"),
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToArray();
            var duplicateManifestId = allManifestItems
                .GroupBy(item => item.Id!, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicateManifestId is not null)
                throw new InvalidDataException($"EPUB manifest contains duplicate item id '{duplicateManifestId.Key}'.");

            var manifestItems = allManifestItems
                .Where(item =>
                    !string.IsNullOrWhiteSpace(item.Href) &&
                    item.MediaType is "application/xhtml+xml" or "text/html")
                .ToArray();
            var manifest = manifestItems.ToDictionary(item => item.Id!, item => item.Href!, StringComparer.Ordinal);

            var packageDirectory = Path.GetDirectoryName(packagePath)?.Replace('\\', '/') ?? "";
            var blocks = new List<FabricStructuredBlock>();
            foreach (var idRef in package.Descendants()
                         .Where(element => element.Name.LocalName == "itemref")
                         .Select(element => (string?)element.Attribute("idref"))
                         .Where(id => !string.IsNullOrWhiteSpace(id)))
            {
                if (!manifest.TryGetValue(idRef!, out var href))
                    continue;

                var contentPath = ResolveZipPath(packageDirectory, href);
                var contentEntry = GetSafeEntry(archive, contentPath)
                    ?? throw new InvalidDataException($"EPUB spine item '{contentPath}' is missing.");
                XDocument content;
                using (var contentStream = OpenXmlEntry(contentEntry, budget))
                    content = LoadXml(contentStream);

                blocks.AddRange(BuildXhtmlBlocks(content, contentEntry.FullName));
            }

            if (blocks.Count == 0)
                throw new InvalidDataException("EPUB contains no parseable text.");

            var (normalized, parsedBlocks) = FabricTextParsing.BuildStructuredBlocks(blocks);
            return new FabricParsedDocument(
                ParserId,
                ParserVersion,
                "application/epub+zip",
                normalized,
                parsedBlocks,
                []);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("EPUB XML is malformed or unsupported.", ex);
        }
    }

    private static string GetPackagePath(ZipArchive archive, EpubXmlBudget budget)
    {
        var container = archive.GetEntry("META-INF/container.xml")
            ?? throw new InvalidDataException("EPUB container.xml is missing.");
        XDocument document;
        using (var stream = OpenXmlEntry(container, budget))
            document = LoadXml(stream);

        var path = document.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "rootfile")
            ?.Attribute("full-path")
            ?.Value;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("EPUB container.xml does not identify a package document.");

        return NormalizeZipPath(path);
    }

    private static IEnumerable<FabricStructuredBlock> BuildXhtmlBlocks(XDocument document, string sourceLocator)
    {
        var body = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "body")
            ?? document.Root
            ?? throw new InvalidDataException("EPUB XHTML content has no root element.");

        foreach (var element in body.Descendants())
        {
            var localName = element.Name.LocalName;
            if (localName is "table")
            {
                if (IsInsideStructuredContainer(element))
                    continue;

                var rows = element.Descendants()
                    .Where(row => row.Name.LocalName == "tr")
                    .Select(row => string.Join(" | ", row.Elements()
                        .Where(cell => cell.Name.LocalName is "td" or "th")
                        .Select(TextOf)
                        .Where(text => text.Length > 0)))
                    .Where(row => row.Length > 0)
                    .ToArray();
                if (rows.Length > 0)
                    yield return new FabricStructuredBlock(string.Join('\n', rows), "table", null, sourceLocator);
                continue;
            }

            if (localName is "figure")
            {
                if (IsInsideStructuredContainer(element))
                    continue;

                var text = TextOf(element);
                yield return new FabricStructuredBlock(text.Length == 0 ? "[figure]" : text, "figure", null, sourceLocator);
                continue;
            }

            if (localName is "p" or "li" or "blockquote" or "figcaption" ||
                localName.Length == 2 && localName[0] == 'h' && char.IsAsciiDigit(localName[1]))
            {
                if (IsInsideStructuredContainer(element))
                    continue;

                var text = TextOf(element);
                if (text.Length == 0)
                    continue;
                var kind = localName == "figcaption"
                    ? "figure"
                    : localName.Length == 2 && localName[0] == 'h' ? "heading" : "text";
                yield return new FabricStructuredBlock(text, kind, null, sourceLocator);
            }
        }
    }

    private static bool IsInsideStructuredContainer(XElement element) =>
        element.Ancestors().Any(ancestor => ancestor.Name.LocalName is "table" or "figure");

    private static string TextOf(XElement element) => string.Join(" ", element
            .DescendantNodes()
            .OfType<XText>()
            .Select(text => text.Value.Trim())
            .Where(text => text.Length > 0))
        .Trim();

    private static XDocument LoadXml(Stream stream)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
        };
        using var reader = XmlReader.Create(stream, settings);
        return XDocument.Load(reader, LoadOptions.None);
    }

    private static ZipArchiveEntry? GetSafeEntry(ZipArchive archive, string path)
    {
        path = NormalizeZipPath(path);
        foreach (var entry in archive.Entries)
        {
            string normalizedEntryPath;
            try
            {
                normalizedEntryPath = NormalizeZipPath(entry.FullName);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (normalizedEntryPath.Equals(path, StringComparison.Ordinal))
                return entry;
        }

        return null;
    }

    private static string ResolveZipPath(string directory, string href)
    {
        var combined = string.IsNullOrWhiteSpace(directory) ? href : $"{directory}/{href}";
        return NormalizeZipPath(combined);
    }

    private static string NormalizeZipPath(string path)
    {
        path = Uri.UnescapeDataString(path)
            .Replace('\\', '/')
            .TrimStart('/');
        if (path.Split('/').Any(part => part is "" or "." or "..") || path.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("EPUB contains an unsafe ZIP path.");
        return path;
    }

    private static Stream OpenXmlEntry(ZipArchiveEntry entry, EpubXmlBudget budget)
    {
        if (entry.Length > MaxXmlEntryBytes)
            throw new InvalidDataException($"EPUB XML entry '{entry.FullName}' exceeds the supported size limit.");
        budget.Charge(entry);
        return entry.Open();
    }

    private sealed class EpubXmlBudget
    {
        private long _used;

        public void Charge(ZipArchiveEntry entry)
        {
            if (_used > MaxTotalXmlBytes - entry.Length)
                throw new InvalidDataException("EPUB expanded XML exceeds the supported size limit.");
            _used += entry.Length;
        }
    }
}

internal readonly record struct FabricStructuredBlock(
    string Text,
    string BlockKind,
    string? HeadingPath,
    string? SourceLocator,
    double? Confidence = null);

internal static class FabricTextParsing
{
    private static readonly Regex MarkdownHeading = new(
        @"^(?<level>#{1,6})[ \t]+(?<title>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Normalize(string value)
    {
        value = value.TrimStart('\uFEFF')
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Normalize(NormalizationForm.FormC);
        if (value.Contains('\0'))
            throw new InvalidDataException("Document contains NUL characters.");

        var lines = value.Split('\n').Select(line => line.TrimEnd()).ToArray();
        return string.Join('\n', lines).Trim('\n') + "\n";
    }

    public static IReadOnlyList<FabricParsedBlock> BuildBlocks(string text, bool markdown)
    {
        var blocks = new List<FabricParsedBlock>();
        var headings = new string?[6];
        var cursor = 0;
        while (cursor < text.Length)
        {
            while (cursor < text.Length && text[cursor] == '\n') cursor++;
            if (cursor >= text.Length) break;

            var lineEnd = text.IndexOf('\n', cursor);
            if (lineEnd < 0) lineEnd = text.Length;
            var heading = markdown ? MarkdownHeading.Match(text[cursor..lineEnd]) : Match.Empty;
            int boundary;
            if (heading.Success)
            {
                var level = heading.Groups["level"].Value.Length;
                headings[level - 1] = heading.Groups["title"].Value.Trim();
                for (var index = level; index < headings.Length; index++) headings[index] = null;
                boundary = lineEnd;
            }
            else
            {
                boundary = text.Length;
                var scan = cursor;
                while (scan < text.Length)
                {
                    var newline = text.IndexOf('\n', scan);
                    if (newline < 0 || newline == text.Length - 1)
                    {
                        boundary = newline < 0 ? text.Length : newline;
                        break;
                    }

                    var nextLineEnd = text.IndexOf('\n', newline + 1);
                    if (nextLineEnd < 0) nextLineEnd = text.Length;
                    if (text[newline + 1] == '\n' ||
                        markdown && MarkdownHeading.IsMatch(text[(newline + 1)..nextLineEnd]))
                    {
                        boundary = newline;
                        break;
                    }

                    scan = newline + 1;
                }
            }

            var headingPath = string.Join(" / ", headings.Where(value => !string.IsNullOrWhiteSpace(value))!);
            var blockText = text[cursor..boundary];
            var kind = heading.Success ? "heading" : "text";
            blocks.Add(new FabricParsedBlock(
                cursor,
                boundary,
                headingPath.Length == 0 ? null : headingPath,
                blockText,
                kind));
            cursor = boundary;
        }

        return blocks;
    }

    public static (string Normalized, IReadOnlyList<FabricParsedBlock> Blocks) BuildStructuredBlocks(
        IEnumerable<FabricStructuredBlock> sourceBlocks)
    {
        var normalizedParts = sourceBlocks
            .Select(block => block with { Text = Normalize(block.Text).Trim('\n') })
            .Where(block => !string.IsNullOrWhiteSpace(block.Text))
            .ToArray();
        if (normalizedParts.Length == 0)
            throw new InvalidDataException("Document contains no parseable blocks.");

        var normalized = string.Join("\n\n", normalizedParts.Select(block => block.Text)) + "\n";
        var blocks = new List<FabricParsedBlock>(normalizedParts.Length);
        var cursor = 0;
        for (var index = 0; index < normalizedParts.Length; index++)
        {
            var block = normalizedParts[index];
            var charStart = cursor;
            var charEnd = charStart + block.Text.Length;
            blocks.Add(new FabricParsedBlock(
                charStart,
                charEnd,
                block.HeadingPath,
                block.Text,
                block.BlockKind,
                null,
                block.SourceLocator,
                block.Confidence));
            cursor = charEnd + (index == normalizedParts.Length - 1 ? 1 : 2);
        }

        return (normalized, blocks);
    }

    public static IReadOnlyList<FabricParsedBlock> AddPageLocators(
        IReadOnlyList<FabricParsedBlock> blocks,
        string normalized,
        IReadOnlyList<(int PageNumber, string Text)> pageTexts)
    {
        if (blocks.Count == 0 || pageTexts.Count == 0)
            return blocks;

        var ranges = BuildPageRanges(normalized, pageTexts);
        return blocks.Select(block =>
        {
            var pages = ranges
                .Where(range => block.CharStart < range.End && block.CharEnd > range.Start)
                .Select(range => range.PageNumber)
                .Distinct()
                .ToArray();
            if (pages.Length == 0)
                return block;

            return pages.Length == 1
                ? block with { PageNumber = pages[0], SourceLocator = $"page {pages[0]}" }
                : block with { PageNumber = pages[0], SourceLocator = $"pages {pages[0]}-{pages[^1]}" };
        }).ToArray();
    }

    private static IReadOnlyList<(int PageNumber, int Start, int End)> BuildPageRanges(
        string normalized,
        IReadOnlyList<(int PageNumber, string Text)> pageTexts)
    {
        var ranges = new List<(int PageNumber, int Start, int End)>();
        var cursor = 0;
        for (var index = 0; index < pageTexts.Count; index++)
        {
            var page = Normalize(pageTexts[index].Text).Trim('\n');
            if (page.Length == 0)
                continue;

            var start = normalized.IndexOf(page, cursor, StringComparison.Ordinal);
            if (start < 0)
                continue;

            var end = start + page.Length;
            ranges.Add((pageTexts[index].PageNumber, start, end));
            cursor = end;
        }

        return ranges;
    }
}
