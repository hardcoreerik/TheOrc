// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
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
        _parsers = (parsers ?? [new TextMarkdownFabricParser(), new PdfTextFabricParser()]).ToArray();
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
