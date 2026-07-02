// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using OrchestratorIDE.Core;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricSegmenter
{
    private readonly FabricSegmenterOptions _options;

    public FabricSegmenter(FabricSegmenterOptions? options = null)
    {
        _options = options ?? new FabricSegmenterOptions();
        _options.Validate();
    }

    public IReadOnlyList<FabricSegmentDraft> Segment(string documentId, FabricParsedDocument document)
    {
        if (string.IsNullOrWhiteSpace(documentId))
            throw new ArgumentException("Document ID is required.", nameof(documentId));
        ArgumentNullException.ThrowIfNull(document);

        var blocks = document.Blocks.SelectMany(SplitOversizedBlock).ToArray();
        var ranges = new List<(int Start, int End)>();
        var start = 0;
        while (start < blocks.Length)
        {
            var end = start;
            var tokens = 0;
            while (end < blocks.Length)
            {
                var projected = ContextManager.EstimateTokens(
                    document.NormalizedText[blocks[start].CharStart..blocks[end].CharEnd]);
                if (end > start && projected > _options.MaximumTokens)
                    break;
                tokens = projected;
                end++;
                if (tokens >= _options.TargetTokens)
                    break;
            }

            if (end == start) end++;
            ranges.Add((start, end));
            if (end >= blocks.Length) break;

            var overlapStart = end;
            while (overlapStart > start + 1)
            {
                var candidate = ContextManager.EstimateTokens(
                    document.NormalizedText[blocks[overlapStart - 1].CharStart..blocks[end - 1].CharEnd]);
                if (candidate > _options.OverlapTokens) break;
                overlapStart--;
            }
            start = Math.Max(start + 1, overlapStart);
        }

        var drafts = ranges.Select((range, index) =>
        {
            var charStart = blocks[range.Start].CharStart;
            var charEnd = blocks[range.End - 1].CharEnd;
            var text = document.NormalizedText[charStart..charEnd];
            var digest = FabricHashing.Sha256(text);
            var segmentId = $"seg-{FabricHashing.Sha256($"{documentId}|{FabricIngestionVersions.Segmenter}|{charStart}|{charEnd}|{digest}")[..24]}";
            var metadata = AggregateSourceMetadata(blocks[range.Start..range.End]);
            return new FabricSegmentDraft(
                segmentId,
                index + 1,
                blocks[range.Start].HeadingPath,
                charStart,
                charEnd,
                ContextManager.EstimateTokens(text),
                digest,
                text,
                null,
                null,
                FabricIngestionVersions.Segmenter,
                blocks[range.Start].BlockKind,
                metadata.PageNumber,
                metadata.SourceLocator,
                metadata.Confidence);
        }).ToArray();

        return drafts.Select((draft, index) => draft with
        {
            PreviousSegmentId = index == 0 ? null : drafts[index - 1].SegmentId,
            NextSegmentId = index == drafts.Length - 1 ? null : drafts[index + 1].SegmentId,
        }).ToArray();
    }

    private IEnumerable<FabricParsedBlock> SplitOversizedBlock(FabricParsedBlock block)
    {
        if (ContextManager.EstimateTokens(block.Text) <= _options.MaximumTokens)
        {
            yield return block;
            yield break;
        }

        var offset = 0;
        while (offset < block.Text.Length)
        {
            var low = 1;
            var high = block.Text.Length - offset;
            while (low < high)
            {
                var mid = low + ((high - low + 1) / 2);
                if (ContextManager.EstimateTokens(block.Text.Substring(offset, mid)) <= _options.MaximumTokens)
                    low = mid;
                else
                    high = mid - 1;
            }

            var length = low;
            if (offset + length < block.Text.Length)
            {
                var whitespace = block.Text.LastIndexOfAny([' ', '\t', '\n'], offset + length - 1, length);
                if (whitespace > offset + (length / 2))
                    length = whitespace - offset + 1;

                var splitAt = offset + length;
                if (splitAt > offset &&
                    char.IsHighSurrogate(block.Text[splitAt - 1]) &&
                    char.IsLowSurrogate(block.Text[splitAt]))
                {
                    length--;
                    if (length == 0)
                        throw new InvalidDataException("Unable to split block at a Unicode scalar boundary.");
                }
            }

            var text = block.Text.Substring(offset, length);
            yield return new FabricParsedBlock(
                block.CharStart + offset,
                block.CharStart + offset + length,
                block.HeadingPath,
                text,
                block.BlockKind,
                block.PageNumber,
                block.SourceLocator,
                block.Confidence);
            offset += length;
        }
    }

    private static (int? PageNumber, string? SourceLocator, double? Confidence) AggregateSourceMetadata(
        IReadOnlyList<FabricParsedBlock> blocks)
    {
        var pages = blocks.SelectMany(EnumeratePages).Distinct().Order().ToArray();
        var confidences = blocks
            .Where(block => block.Confidence.HasValue)
            .Select(block => block.Confidence!.Value)
            .ToArray();
        var confidence = confidences.Length == 0 ? (double?)null : confidences.Min();

        if (pages.Length == 0)
        {
            var locator = blocks.Select(block => block.SourceLocator).FirstOrDefault(locator => !string.IsNullOrWhiteSpace(locator));
            return (blocks.Select(block => block.PageNumber).FirstOrDefault(page => page.HasValue), locator, confidence);
        }

        return pages.Length == 1
            ? (pages[0], $"page {pages[0]}", confidence)
            : (pages[0], $"pages {pages[0]}-{pages[^1]}", confidence);
    }

    private static IEnumerable<int> EnumeratePages(FabricParsedBlock block)
    {
        if (TryParsePageRange(block.SourceLocator, out var start, out var end))
        {
            for (var page = start; page <= end; page++)
                yield return page;
            yield break;
        }

        if (block.PageNumber.HasValue)
            yield return block.PageNumber.Value;
    }

    private static bool TryParsePageRange(string? sourceLocator, out int start, out int end)
    {
        start = 0;
        end = 0;
        if (string.IsNullOrWhiteSpace(sourceLocator))
            return false;

        const string singlePrefix = "page ";
        const string rangePrefix = "pages ";
        if (sourceLocator.StartsWith(singlePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(sourceLocator[singlePrefix.Length..], out start))
                return false;
            end = start;
            return start > 0;
        }

        if (!sourceLocator.StartsWith(rangePrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = sourceLocator[rangePrefix.Length..].Split('-', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], out start) ||
            !int.TryParse(parts[1], out end))
        {
            return false;
        }

        return start > 0 && end >= start;
    }
}
