// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;

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
        _parsers = (parsers ?? [new TextMarkdownFabricParser()]).ToArray();
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

        var normalized = Normalize(decoded);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidDataException("Document contains no text.");

        var blocks = BuildBlocks(normalized, mediaType.Equals("text/markdown", StringComparison.OrdinalIgnoreCase));
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

    private static string Normalize(string value)
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

    private static IReadOnlyList<FabricParsedBlock> BuildBlocks(string text, bool markdown)
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

            var blockText = text[cursor..boundary];
            var headingPath = string.Join(" / ", headings.Where(value => !string.IsNullOrWhiteSpace(value))!);
            blocks.Add(new FabricParsedBlock(
                cursor,
                boundary,
                headingPath.Length == 0 ? null : headingPath,
                blockText));
            cursor = boundary;
        }

        return blocks;
    }
}
