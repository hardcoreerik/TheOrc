// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later

namespace OrchestratorIDE.Services.ContextFabric;

public sealed class FabricCitationVerifier(FabricLibraryRepository libraryRepository)
{
    public FabricCitationVerificationResult VerifyClaim(
        string claimText,
        IReadOnlyList<FabricCitation> citations,
        bool allowRepair = false)
    {
        if (string.IsNullOrWhiteSpace(claimText))
            throw new ArgumentException("Claim text is required.", nameof(claimText));
        ArgumentNullException.ThrowIfNull(citations);

        var repaired = false;
        var items = new List<FabricCitationVerificationItem>(citations.Count);
        var effective = new List<FabricCitation>(citations.Count);
        var matchedQuotes = new List<string>(citations.Count);

        foreach (var citation in citations)
        {
            if (string.IsNullOrWhiteSpace(citation.SegmentId))
            {
                items.Add(new FabricCitationVerificationItem("", FabricCitationVerificationLabel.Unverifiable, citation.Quote ?? "", citation.CharStart, citation.CharEnd, "segment missing"));
                continue;
            }

            var segment = libraryRepository.GetSegment(citation.SegmentId);
            if (segment is null)
            {
                items.Add(new FabricCitationVerificationItem(citation.SegmentId, FabricCitationVerificationLabel.Unverifiable, citation.Quote ?? "", citation.CharStart, citation.CharEnd, "segment not found"));
                continue;
            }

            if (TryMatchExact(segment.Text, citation, out var exactQuote))
            {
                matchedQuotes.Add(exactQuote);
                effective.Add(citation);
                items.Add(new FabricCitationVerificationItem(citation.SegmentId, FabricCitationVerificationLabel.Supported, exactQuote, citation.CharStart, citation.CharEnd, "exact source match"));
                continue;
            }

            if (allowRepair && TryRepair(segment.Text, citation, out var repairedCitation))
            {
                repaired = true;
                matchedQuotes.Add(repairedCitation.Quote);
                effective.Add(repairedCitation);
                items.Add(new FabricCitationVerificationItem(repairedCitation.SegmentId, FabricCitationVerificationLabel.Supported, repairedCitation.Quote, repairedCitation.CharStart, repairedCitation.CharEnd, "repaired to exact source match"));
                continue;
            }

            items.Add(new FabricCitationVerificationItem(citation.SegmentId, FabricCitationVerificationLabel.CitationMismatch, citation.Quote ?? "", citation.CharStart, citation.CharEnd, "quote/range mismatch"));
        }

        var label = ResolveLabel(claimText, items, matchedQuotes);
        return new FabricCitationVerificationResult(claimText, label, items, repaired, effective);
    }

    private static string ResolveLabel(
        string claimText,
        IReadOnlyList<FabricCitationVerificationItem> items,
        IReadOnlyList<string> matchedQuotes)
    {
        if (items.Count == 0)
            return FabricCitationVerificationLabel.Unverifiable;
        if (items.Any(item => item.Label == FabricCitationVerificationLabel.CitationMismatch))
            return FabricCitationVerificationLabel.CitationMismatch;
        if (items.All(item => item.Label == FabricCitationVerificationLabel.Unverifiable))
            return FabricCitationVerificationLabel.Unverifiable;

        var claimTokens = Tokenize(claimText);
        var sourceTokens = Tokenize(string.Join(" ", matchedQuotes));
        if (claimTokens.Count == 0 || sourceTokens.Count == 0)
            return FabricCitationVerificationLabel.Interpretive;
        if (claimTokens.Contains("not") != sourceTokens.Contains("not"))
            return FabricCitationVerificationLabel.Contradicted;

        var overlap = claimTokens.Count(sourceTokens.Contains) / (double)claimTokens.Count;
        if (overlap >= 0.95)
            return FabricCitationVerificationLabel.Supported;
        if (overlap >= 0.50)
            return FabricCitationVerificationLabel.PartiallySupported;
        return FabricCitationVerificationLabel.Interpretive;
    }

    private static bool TryMatchExact(string sourceText, FabricCitation citation, out string exactQuote)
    {
        exactQuote = "";
        if (citation.CharStart < 0 || citation.CharEnd <= citation.CharStart || citation.CharEnd > sourceText.Length)
            return false;

        exactQuote = sourceText[citation.CharStart..citation.CharEnd];
        if (!string.Equals(citation.Quote, exactQuote, StringComparison.Ordinal))
            return false;

        var digest = FabricHashing.Sha256(exactQuote);
        return string.Equals(citation.QuoteDigest, digest, StringComparison.Ordinal);
    }

    private static bool TryRepair(string sourceText, FabricCitation citation, out FabricCitation repaired)
    {
        repaired = citation;
        if (string.IsNullOrWhiteSpace(citation.Quote))
            return false;

        var start = sourceText.IndexOf(citation.Quote, StringComparison.Ordinal);
        if (start < 0 || sourceText.IndexOf(citation.Quote, start + 1, StringComparison.Ordinal) >= 0)
            return false;

        var digest = FabricHashing.Sha256(citation.Quote);
        repaired = citation with
        {
            CharStart = start,
            CharEnd = start + citation.Quote.Length,
            QuoteDigest = digest,
        };
        return true;
    }

    private static HashSet<string> Tokenize(string text) => text
        .Split([' ', '\t', '\r', '\n', ',', '.', ';', ':', '?', '!'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(item => item.ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);
}
