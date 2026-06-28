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
                FabricIngestionVersions.Segmenter);
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

                if (char.IsLowSurrogate(block.Text[offset + length]))
                {
                    length--;
                }
            }

            var text = block.Text.Substring(offset, length);
            yield return new FabricParsedBlock(
                block.CharStart + offset,
                block.CharStart + offset + length,
                block.HeadingPath,
                text);
            offset += length;
        }
    }
}
