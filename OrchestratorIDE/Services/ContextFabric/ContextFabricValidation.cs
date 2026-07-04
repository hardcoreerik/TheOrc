// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using System.Text;

namespace OrchestratorIDE.Services.ContextFabric;

public static class FabricJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
    };

    // Recovery-only options: let trailing commas and inline comments through without
    // rejecting the whole response. Only used when the strict parse fails.
    private static readonly JsonSerializerOptions s_lenientOptions = new(Options)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    // JSON keyword tokens that models sometimes emit with garbage word-characters appended,
    // e.g. "falseC" (false + first char of the next word) or "trueX". Listed longest-first
    // so that an exact match is not accidentally trimmed by a shorter prefix.
    private static readonly string[] s_jsonKeywords = ["false", "true", "null"];

    /// <summary>
    /// Extracts and deserializes the first JSON object from raw model output. Applies recovery
    /// passes before giving up: (1) lenient parse that tolerates trailing commas and inline
    /// comments; (2) keyword-suffix sanitization that fixes token-boundary artifacts such as
    /// "falseC" or "trueX" that models produce when the literal token runs into the next word;
    /// (3) unescaped-quote sanitization that fixes models quoting a term (e.g. "Chapter Alpha")
    /// inside a string value without escaping it, which otherwise truncates the string at the
    /// first embedded quote; (4) combinations of the above. Any remaining failure throws
    /// JsonException.
    /// </summary>
    public static T ParseModelObject<T>(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            throw new JsonException("Model returned an empty response.");

        var json = ExtractFirstObject(output);

        // Fast path: strict parse
        if (TryDeserialize<T>(json, Options, out var result))
            return result;

        // Allow trailing commas and inline comments that models sometimes emit
        if (TryDeserialize<T>(json, s_lenientOptions, out result))
            return result;

        // Strip keyword-suffix artifacts ("falseC" → "false", "trueX" → "true") and/or escape
        // unescaped inner quotes ("Chapter Alpha" written without \"), and retry. A single
        // response can carry either artifact alone or both at once, and each repair pass can only
        // recognize its own artifact correctly when the other has already been fixed (e.g. the
        // quote repair's string-boundary tracking gets confused by a stray keyword token, and vice
        // versa), so both orders are attempted on the raw (unvalidated) intermediate text before
        // the final deserialize attempt actually validates it.
        var (keywordFixed, keywordChanged) = SanitizeLiteralSuffixesRaw(json);
        var (quoteFixed, quoteChanged) = SanitizeUnescapedQuotesRaw(json);

        if (keywordChanged && TryDeserializeEither<T>(keywordFixed, out result))
            return result;
        if (quoteChanged && TryDeserializeEither<T>(quoteFixed, out result))
            return result;

        if (keywordChanged)
        {
            var (keywordThenQuote, changedFurther) = SanitizeUnescapedQuotesRaw(keywordFixed);
            if (changedFurther && TryDeserializeEither<T>(keywordThenQuote, out result))
                return result;
        }

        if (quoteChanged)
        {
            var (quoteThenKeyword, changedFurther) = SanitizeLiteralSuffixesRaw(quoteFixed);
            if (changedFurther && TryDeserializeEither<T>(quoteThenKeyword, out result))
                return result;
        }

        throw new JsonException(
            $"Model response could not be parsed as {typeof(T).Name}. " +
            $"Extracted: {json[..Math.Min(json.Length, 200)]}");
    }

    private static bool TryDeserializeEither<T>(string json, out T result)
    {
        if (TryDeserialize(json, Options, out result))
            return true;
        return TryDeserialize(json, s_lenientOptions, out result);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    internal static string ExtractFirstObject(string output)
    {
        var start = output.IndexOf('{');
        if (start < 0)
            throw new JsonException("Model response did not contain a JSON object.");

        if (TryExtractBalancedObject(output, start, out var json))
            return json;

        var repaired = TryRepairUnterminatedObject(output[start..]);
        if (repaired is not null)
            return repaired;

        throw new JsonException("Model response contained an unterminated JSON object.");
    }

    private static bool TryExtractBalancedObject(string output, int start, out string json)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < output.Length; index++)
        {
            var ch = output[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{') depth++;
            if (ch != '}') continue;
            depth--;
            if (depth == 0)
            {
                json = output[start..(index + 1)];
                return true;
            }
        }

        json = "";
        return false;
    }

    private static bool TryDeserialize<T>(string json, JsonSerializerOptions opts, out T result)
    {
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, opts);
            if (value is not null)
            {
                result = value;
                return true;
            }
        }
        catch (JsonException) { }
        result = default!;
        return false;
    }

    /// <summary>
    /// Walks the JSON character-by-character (correctly skipping over string values) and strips
    /// word-character suffixes from JSON keyword tokens. This repairs the token-boundary artifact
    /// where autoregressive models emit e.g. "falseC" (the literal token "false" immediately
    /// followed by a partial next token starting with "C"). String contents are never modified.
    /// Returns null if no change was made or if the result still does not parse as JSON.
    /// </summary>
    internal static string? TrySanitizeLiteralSuffixes(string json)
    {
        var (result, changed) = SanitizeLiteralSuffixesRaw(json);
        if (!changed) return null;

        try
        {
            using var _ = JsonDocument.Parse(result);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Core scan for TrySanitizeLiteralSuffixes, without the standalone-validity check, so callers
    // composing it with another repair pass (see ParseModelObject) can chain both passes before
    // validating the final result instead of being blocked by an intermediate invalid state.
    private static (string Text, bool Changed) SanitizeLiteralSuffixesRaw(string json)
    {
        var sb = new StringBuilder(json.Length);
        var inString = false;
        var escaped = false;
        var changed = false;

        for (var i = 0; i < json.Length; i++)
        {
            var ch = json[i];

            if (inString)
            {
                sb.Append(ch);
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') { inString = true; sb.Append(ch); continue; }

            // Outside strings: scan the full word token and strip any garbage suffix that
            // starts after a known JSON keyword (false/true/null).
            if (char.IsLetter(ch))
            {
                var wordStart = i;
                while (i < json.Length && (char.IsLetterOrDigit(json[i]) || json[i] == '_'))
                    i++;
                var word = json[wordStart..i];
                i--; // for-loop will increment

                var clipped = false;
                foreach (var keyword in s_jsonKeywords)
                {
                    if (word.Length > keyword.Length && word.StartsWith(keyword, StringComparison.Ordinal))
                    {
                        sb.Append(keyword);
                        changed = true;
                        clipped = true;
                        break;
                    }
                }
                if (!clipped) sb.Append(word);
                continue;
            }

            sb.Append(ch);
        }

        return (sb.ToString(), changed);
    }

    /// <summary>
    /// Walks the JSON and escapes quote characters found inside a string value that are not
    /// genuine terminators — the artifact where a model quotes a term (e.g. "Chapter Alpha")
    /// inline without escaping it, which otherwise ends the JSON string at the first embedded
    /// quote and corrupts everything after it. A quote is treated as a real terminator only when
    /// the next non-whitespace character confirms it: <c>:</c> for an object-key string (keys are
    /// always followed by a colon), or <c>,</c> <c>}</c> <c>]</c> or end of input for a value
    /// string — <c>:</c> is never a value-string terminator, since a value can legitimately
    /// contain a quoted term immediately followed by a colon. Any other in-string quote is escaped
    /// instead. Returns null if no change was made or if the result still does not parse as JSON.
    /// </summary>
    internal static string? TrySanitizeUnescapedQuotes(string json)
    {
        var (result, changed) = SanitizeUnescapedQuotesRaw(json);
        if (!changed) return null;

        try
        {
            using var _ = JsonDocument.Parse(result);
            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Core scan for TrySanitizeUnescapedQuotes, without the standalone-validity check, so callers
    // composing it with another repair pass (see ParseModelObject) can chain both passes before
    // validating the final result instead of being blocked by an intermediate invalid state.
    private static (string Text, bool Changed) SanitizeUnescapedQuotesRaw(string json)
    {
        var sb = new StringBuilder(json.Length + 16);
        var inString = false;
        var escaped = false;
        var changed = false;
        var isKeyString = false;

        // Tracks, per open container, whether the container is an object ('{') or array ('[')
        // and — for objects — whether the next string is expected to be a key or a value. Array
        // elements are always values; object members alternate key, value, key, value, ...
        var containers = new List<(char Kind, bool ExpectingKey)>();

        for (var i = 0; i < json.Length; i++)
        {
            var ch = json[i];

            if (!inString)
            {
                if (ch == '"')
                {
                    inString = true;
                    isKeyString = containers.Count > 0
                        && containers[^1].Kind == '{'
                        && containers[^1].ExpectingKey;
                    sb.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '{':
                        containers.Add(('{', true));
                        break;
                    case '[':
                        containers.Add(('[', false));
                        break;
                    case '}':
                    case ']':
                        if (containers.Count > 0)
                            containers.RemoveAt(containers.Count - 1);
                        break;
                    case ':' when containers.Count > 0 && containers[^1].Kind == '{':
                        containers[^1] = ('{', false);
                        break;
                    case ',' when containers.Count > 0 && containers[^1].Kind == '{':
                        containers[^1] = ('{', true);
                        break;
                }

                sb.Append(ch);
                continue;
            }

            if (escaped)
            {
                sb.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                sb.Append(ch);
                escaped = true;
                continue;
            }

            if (ch != '"')
            {
                sb.Append(ch);
                continue;
            }

            var next = i + 1;
            while (next < json.Length && char.IsWhiteSpace(json[next]))
                next++;
            var nextChar = next < json.Length ? json[next] : '\0';
            var isTerminator = next >= json.Length
                || (isKeyString && nextChar == ':')
                || (!isKeyString && nextChar is ',' or '}' or ']');

            if (isTerminator)
            {
                sb.Append(ch);
                inString = false;
            }
            else
            {
                sb.Append('\\').Append(ch);
                changed = true;
            }
        }

        return (sb.ToString(), changed);
    }

    private static string? TryRepairUnterminatedObject(string fragment)
    {
        var closers = new Stack<char>();
        var sb = new StringBuilder(fragment.Length + 8);
        var inString = false;
        var escaped = false;

        foreach (var ch in fragment)
        {
            sb.Append(ch);
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (ch == '"')
                    inString = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '{':
                    closers.Push('}');
                    break;
                case '[':
                    closers.Push(']');
                    break;
                case '}':
                case ']':
                    if (closers.Count == 0 || closers.Pop() != ch)
                        return null;
                    break;
            }
        }

        if (escaped)
            return null;

        if (inString)
            sb.Append('"');

        while (closers.Count > 0)
            sb.Append(closers.Pop());

        var repaired = sb.ToString();
        try
        {
            using var _ = JsonDocument.Parse(repaired);
            return repaired;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static class FabricEvidenceProcessor
{
    private const int MaxSummaryChars = 2_000;
    private const int MaxClaimChars = 2_000;
    private const int MaxQuoteChars = 1_500;
    private const int MaxClaims = 64;

    public static FabricEvidenceValidationResult NormalizeAndValidate(
        FabricCorpus corpus,
        FabricSegment segment,
        FabricEvidenceCard draft,
        bool requireCompleteCoverage = false)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<string>();
        CheckIdentity(draft.SchemaVersion, FabricSchemaVersions.EvidenceCard, "schemaVersion", errors);
        CheckIdentity(draft.CorpusId, corpus.CorpusId, "corpusId", errors);
        CheckIdentity(draft.DocumentId, corpus.DocumentId, "documentId", errors);
        CheckIdentity(draft.SegmentId, segment.SegmentId, "segmentId", errors);
        CheckIdentity(draft.PromptVersion, FabricSchemaVersions.ReaderPrompt, "promptVersion", errors);

        if (string.IsNullOrWhiteSpace(draft.Summary))
            errors.Add("summary is required");
        else if (draft.Summary.Length > MaxSummaryChars)
            errors.Add($"summary exceeds {MaxSummaryChars} characters");

        var draftClaims = draft.Claims ?? [];
        if (draftClaims.Count is 0 or > MaxClaims)
            errors.Add($"claims must contain between 1 and {MaxClaims} items");

        var claimIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedClaimIds = new HashSet<string>(StringComparer.Ordinal);
        var normalizedClaims = new List<FabricClaim>(draftClaims.Count);
        foreach (var claim in draftClaims)
        {
            if (claim is null)
            {
                errors.Add("claims must not contain null items");
                continue;
            }

            if (string.IsNullOrWhiteSpace(claim.ClaimId) || !claimIds.Add(claim.ClaimId))
                errors.Add($"claimId '{claim.ClaimId}' is missing or duplicated");
            if (string.IsNullOrWhiteSpace(claim.Text) || claim.Text.Length > MaxClaimChars)
                errors.Add($"claim '{claim.ClaimId}' text is missing or exceeds {MaxClaimChars} characters");
            if (claim.Confidence is < 0 or > 1)
                errors.Add($"claim '{claim.ClaimId}' confidence must be between 0 and 1");
            var draftCitations = claim.Citations ?? [];
            if (draftCitations.Count == 0)
                errors.Add($"claim '{claim.ClaimId}' has no citations");

            var citations = new List<FabricCitation>(draftCitations.Count);
            foreach (var citation in draftCitations)
            {
                if (citation is null)
                {
                    errors.Add($"claim '{claim.ClaimId}' citations must not contain null items");
                    continue;
                }

                var normalized = NormalizeCitation(segment, citation, errors, $"claim '{claim.ClaimId}'");
                if (normalized is not null)
                    citations.Add(normalized);
            }

            var canonicalClaimId = citations.Count == 0
                ? claim.ClaimId
                : $"claim-{segment.SegmentId}-{FabricHashing.DigestOrdered(
                    citations.Select(item => item.QuoteDigest).Prepend(claim.Text ?? ""))[..12]}";
            if (!normalizedClaimIds.Add(canonicalClaimId))
            {
                errors.Add($"normalized claimId '{canonicalClaimId}' is duplicated");
                continue;
            }
            normalizedClaims.Add(claim with { ClaimId = canonicalClaimId, Citations = citations });
        }

        if (requireCompleteCoverage)
        {
            var expectedEvidence = segment.Text
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.StartsWith("EVIDENCE:", StringComparison.Ordinal))
                .Select(line => line["EVIDENCE:".Length..].Trim())
                .ToArray();
            var anchoredQuotes = normalizedClaims
                .SelectMany(claim => claim.Citations)
                .Select(citation => citation.Quote.Trim())
                .ToHashSet(StringComparer.Ordinal);
            foreach (var evidence in expectedEvidence)
                if (!anchoredQuotes.Contains(evidence))
                    errors.Add($"missing evidence line '{evidence}'");
        }

        if (errors.Count > 0)
            return new FabricEvidenceValidationResult(false, null, errors);

        var normalizedCard = draft with
        {
            SchemaVersion = FabricSchemaVersions.EvidenceCard,
            CorpusId = corpus.CorpusId,
            DocumentId = corpus.DocumentId,
            SegmentId = segment.SegmentId,
            PromptVersion = FabricSchemaVersions.ReaderPrompt,
            Summary = draft.Summary.Trim(),
            Claims = normalizedClaims,
            Entities = CleanStrings(draft.Entities ?? [], 64, 256),
            Conflicts = CleanStrings(draft.Conflicts ?? [], 32, 1_000),
            OpenQuestions = CleanStrings(draft.OpenQuestions ?? [], 32, 1_000),
        };
        return new FabricEvidenceValidationResult(true, normalizedCard, []);
    }

    internal static FabricCitation? NormalizeCitation(
        FabricSegment segment,
        FabricCitation citation,
        List<string> errors,
        string owner)
    {
        if (!string.IsNullOrWhiteSpace(citation.SegmentId) &&
            !citation.SegmentId.Equals(segment.SegmentId, StringComparison.Ordinal))
        {
            errors.Add($"{owner} citation points to unexpected segment '{citation.SegmentId}'");
            return null;
        }

        if (string.IsNullOrWhiteSpace(citation.Quote) || citation.Quote.Length > MaxQuoteChars)
        {
            errors.Add($"{owner} citation quote is missing or exceeds {MaxQuoteChars} characters");
            return null;
        }

        var start = citation.CharStart;
        var end = citation.CharEnd;
        var offsetsMatch = start >= 0 && end > start && end <= segment.Text.Length &&
            segment.Text.AsSpan(start, end - start).SequenceEqual(citation.Quote.AsSpan());

        if (!offsetsMatch)
        {
            var anchor = AnalyzeQuoteAnchor(segment, citation.Quote);
            if (anchor.Mode is not (FabricAnchorMode.Exact or FabricAnchorMode.NormalizedExact) ||
                anchor.CharStart is null ||
                anchor.CharEnd is null)
            {
                errors.Add(anchor.Mode == FabricAnchorMode.SoftCandidate
                    ? $"{owner} citation quote only produced a soft candidate; exact or normalized-exact anchoring is required"
                    : $"{owner} {(anchor.Errors.FirstOrDefault() ?? "citation quote was not found in source segment")}");
                return null;
            }
            start = anchor.CharStart.Value;
            end = anchor.CharEnd.Value;
        }

        return citation with
        {
            SegmentId = segment.SegmentId,
            CharStart = start,
            CharEnd = end,
            QuoteDigest = FabricHashing.Sha256(citation.Quote),
        };
    }

    internal static FabricQuoteAnchorResult AnalyzeQuoteAnchor(FabricSegment segment, string quote)
    {
        ArgumentNullException.ThrowIfNull(segment);
        if (string.IsNullOrWhiteSpace(quote))
            return new FabricQuoteAnchorResult("", segment.SegmentId, quote, FabricAnchorMode.None, false, null, null, 0, ["quote is empty"]);

        if (TryFindUniqueExact(segment.Text, quote, out var exactStart, out var exactEnd, out var exactError))
        {
            return new FabricQuoteAnchorResult("", segment.SegmentId, quote, FabricAnchorMode.Exact, true, exactStart, exactEnd, 1.0, []);
        }
        if (string.Equals(exactError, "exact quote is ambiguous", StringComparison.Ordinal))
        {
            return new FabricQuoteAnchorResult("", segment.SegmentId, quote, FabricAnchorMode.None, false, null, null, 0, [exactError!]);
        }

        if (TryFindUniqueNormalized(segment.Text, quote, out var normalizedStart, out var normalizedEnd, out var normalizedError))
        {
            return new FabricQuoteAnchorResult("", segment.SegmentId, quote, FabricAnchorMode.NormalizedExact, true, normalizedStart, normalizedEnd, 1.0, []);
        }
        if (string.Equals(normalizedError, "normalized quote is ambiguous", StringComparison.Ordinal))
        {
            return new FabricQuoteAnchorResult("", segment.SegmentId, quote, FabricAnchorMode.None, false, null, null, 0, [normalizedError!]);
        }

        var soft = FindSoftAnchorCandidate(segment.Text, quote);
        if (soft is not null)
        {
            return new FabricQuoteAnchorResult(
                "",
                segment.SegmentId,
                quote,
                FabricAnchorMode.SoftCandidate,
                false,
                soft.Value.Start,
                soft.Value.End,
                soft.Value.TokenOverlap,
                [exactError ?? normalizedError ?? "soft candidate only"]);
        }

        return new FabricQuoteAnchorResult(
            "",
            segment.SegmentId,
            quote,
            FabricAnchorMode.None,
            false,
            null,
            null,
            0,
            [exactError ?? normalizedError ?? "quote was not found"]);
    }

    private static void CheckIdentity(string actual, string expected, string field, List<string> errors)
    {
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            errors.Add($"{field} must be '{expected}'");
    }

    private static List<string> CleanStrings(IEnumerable<string> values, int maxItems, int maxChars) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value.Trim())
        .Where(value => value.Length <= maxChars)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(maxItems)
        .ToList();

    private static bool TryFindUniqueExact(string source, string quote, out int start, out int end, out string? error)
    {
        start = source.IndexOf(quote, StringComparison.Ordinal);
        if (start < 0)
        {
            end = 0;
            error = "exact quote was not found";
            return false;
        }

        var duplicate = source.IndexOf(quote, start + 1, StringComparison.Ordinal);
        if (duplicate >= 0)
        {
            end = 0;
            error = "exact quote is ambiguous";
            return false;
        }

        end = start + quote.Length;
        error = null;
        return true;
    }

    private static bool TryFindUniqueNormalized(string source, string quote, out int start, out int end, out string? error)
    {
        var normalizedSource = NormalizeForAnchor(source, out var indexMap);
        var normalizedQuote = NormalizeForAnchor(quote, out _);
        if (string.IsNullOrWhiteSpace(normalizedQuote))
        {
            start = end = 0;
            error = "normalized quote is empty";
            return false;
        }

        var normalizedStart = normalizedSource.IndexOf(normalizedQuote, StringComparison.Ordinal);
        if (normalizedStart < 0)
        {
            start = end = 0;
            error = "normalized quote was not found";
            return false;
        }

        var duplicate = normalizedSource.IndexOf(normalizedQuote, normalizedStart + 1, StringComparison.Ordinal);
        if (duplicate >= 0)
        {
            start = end = 0;
            error = "normalized quote is ambiguous";
            return false;
        }

        start = indexMap[normalizedStart];
        end = indexMap[normalizedStart + normalizedQuote.Length - 1] + 1;
        error = null;
        return true;
    }

    private static (int Start, int End, double TokenOverlap)? FindSoftAnchorCandidate(string source, string quote)
    {
        var sourceLines = source.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();
        if (sourceLines.Length == 0)
            return null;

        var quoteTokens = TokenizeForOverlap(NormalizeForAnchor(quote, out _));
        if (quoteTokens.Count == 0)
            return null;

        (int Start, int End, double Score)? best = null;
        (int Start, int End, double Score)? second = null;
        foreach (var line in sourceLines)
        {
            var tokens = TokenizeForOverlap(NormalizeForAnchor(line, out _));
            if (tokens.Count == 0)
                continue;

            var overlap = quoteTokens.Intersect(tokens, StringComparer.Ordinal).Count();
            var score = overlap / (double)Math.Max(quoteTokens.Count, tokens.Count);
            if (score < 0.6)
                continue;

            var start = source.IndexOf(line, StringComparison.Ordinal);
            var candidate = (start, start + line.Length, score);
            if (best is null || candidate.score > best.Value.Score)
            {
                second = best;
                best = candidate;
            }
            else if (second is null || candidate.score > second.Value.Score)
            {
                second = candidate;
            }
        }

        if (best is null)
            return null;
        if (second is not null && Math.Abs(best.Value.Score - second.Value.Score) < 0.05)
            return null;
        return (best.Value.Start, best.Value.End, best.Value.Score);
    }

    private static string NormalizeForAnchor(string value, out List<int> indexMap)
    {
        indexMap = [];
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        var pendingSpaceIndex = -1;
        for (var index = 0; index < value.Length; index++)
        {
            var ch = value[index] switch
            {
                '\u2018' or '\u2019' => '\'',
                '\u201C' or '\u201D' => '"',
                '\u2013' or '\u2014' => '-',
                '\u00A0' => ' ',
                _ => value[index],
            };

            if (char.IsWhiteSpace(ch))
            {
                if (sb.Length > 0)
                {
                    pendingSpace = true;
                    pendingSpaceIndex = index;
                }
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                indexMap.Add(pendingSpaceIndex);
                pendingSpace = false;
            }

            sb.Append(char.ToLowerInvariant(ch));
            indexMap.Add(index);
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
            indexMap.RemoveAt(indexMap.Count - 1);
        }

        return sb.ToString();
    }

    private static HashSet<string> TokenizeForOverlap(string value) => value
        .Split([' ', '\t', '\r', '\n', '.', ',', ':', ';', '?', '!', '\'', '"', '(', ')', '-', '/'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(token => token.Length >= 3)
        .ToHashSet(StringComparer.Ordinal);
}

public static class FabricAnswerVerifier
{
    public static (FabricAnswerDraft Answer, FabricVerificationResult Verification) NormalizeAndVerify(
        FabricCorpus corpus,
        FabricBenchmarkQuestion question,
        FabricAnswerDraft draft)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(draft);

        var errors = new List<string>();
        if (!string.Equals(draft.SchemaVersion, FabricSchemaVersions.Answer, StringComparison.Ordinal))
            errors.Add($"schemaVersion must be '{FabricSchemaVersions.Answer}'");

        // Sanity bounds reject model garbage, but a legitimate exhaustive enumeration scales with
        // the question's own ground truth: one fact and one citation per expected occurrence. At
        // benchmark-corpus size (640 archive tokens over 1.8M source tokens) the host-aggregated
        // answer is ~40K characters carrying one citation per segment, so the caps grow with the
        // expected terms/segments while staying fixed for every small question.
        var maxAnswerChars = Math.Max(12_000, 80 * question.ExpectedTerms.Count);
        var maxCitationsPerClaim = Math.Max(32, question.ExpectedSegmentIds.Count);
        if ((draft.Answer?.Length ?? 0) > maxAnswerChars)
            errors.Add($"answer exceeds {maxAnswerChars} characters");

        var segments = corpus.Segments.ToDictionary(segment => segment.SegmentId, StringComparer.Ordinal);
        var draftClaims = draft.Claims ?? [];
        var answerText = draft.Answer ?? "";
        if (draftClaims.Count > 64)
            errors.Add("answer contains more than 64 claims");
        var normalizedClaims = new List<FabricAnswerClaim>(draftClaims.Count);
        var totalCitations = 0;
        var validCitations = 0;
        var verifiedSegments = new HashSet<string>(StringComparer.Ordinal);

        foreach (var claim in draftClaims)
        {
            if (claim is null)
            {
                errors.Add("answer claims must not contain null items");
                continue;
            }

            if (string.IsNullOrWhiteSpace(claim.Text))
                errors.Add("answer claim text is required");

            var draftCitations = claim.Citations ?? [];
            if (draftCitations.Count > maxCitationsPerClaim)
                errors.Add($"answer claim '{claim.Text}' contains more than {maxCitationsPerClaim} citations");
            var citations = new List<FabricCitation>(draftCitations.Count);
            foreach (var citation in draftCitations)
            {
                if (citation is null)
                {
                    errors.Add($"answer claim '{claim.Text}' citations must not contain null items");
                    continue;
                }

                totalCitations++;
                if (string.IsNullOrWhiteSpace(citation.SegmentId))
                {
                    errors.Add($"answer claim '{claim.Text}' citation segmentId is required");
                    continue;
                }
                if (!segments.TryGetValue(citation.SegmentId, out var segment))
                {
                    errors.Add($"answer citation references unknown segment '{citation.SegmentId}'");
                    continue;
                }

                var before = errors.Count;
                var normalized = FabricEvidenceProcessor.NormalizeCitation(segment, citation, errors, "answer claim");
                if (normalized is null || errors.Count != before)
                    continue;

                validCitations++;
                verifiedSegments.Add(normalized.SegmentId);
                citations.Add(normalized);
            }

            if (!draft.Abstained && citations.Count == 0)
                errors.Add($"answer claim '{claim.Text}' has no valid citation");
            normalizedClaims.Add(claim with { Citations = citations });
        }

        if (question.ExpectAbstention)
        {
            if (!draft.Abstained)
                errors.Add("question is unanswerable but the model did not abstain");
            if (draft.Abstained && !answerText.Contains("does not establish", StringComparison.OrdinalIgnoreCase))
                errors.Add("an abstained answer must explicitly say the corpus does not establish the answer");
            if (draftClaims.Count > 0)
                errors.Add("an abstained answer must not contain factual claims");
        }
        else
        {
            if (draft.Abstained)
                errors.Add("answer unexpectedly abstained");
            var groundedTrace = string.Join(' ', normalizedClaims
                .SelectMany(claim => claim.Citations.Select(citation => citation.Quote).Prepend(claim.Text))
                .Prepend(answerText));
            foreach (var term in question.ExpectedTerms)
                if (!groundedTrace.Contains(term, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"answer is missing expected term '{term}'");
            foreach (var segmentId in question.ExpectedSegmentIds)
                if (!verifiedSegments.Contains(segmentId))
                    errors.Add($"answer is missing required evidence from '{segmentId}'");
        }

        var normalizedAnswer = draft with
        {
            SchemaVersion = FabricSchemaVersions.Answer,
            Answer = answerText.Trim(),
            Claims = normalizedClaims,
        };
        var precision = totalCitations == 0
            ? question.ExpectAbstention ? 1.0 : 0.0
            : validCitations / (double)totalCitations;
        return (normalizedAnswer, new FabricVerificationResult(
            errors.Count == 0,
            precision,
            validCitations,
            totalCitations,
            verifiedSegments.Order(StringComparer.Ordinal).ToArray(),
            errors));
    }
}
