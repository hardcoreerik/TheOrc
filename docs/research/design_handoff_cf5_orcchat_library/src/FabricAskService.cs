// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: OrcChat Library ask service — chains planner → pack → native answer → verifier.
// Drop this file into OrchestratorIDE/Services/ContextFabric/

using System.Text.Json;
using OrchestratorIDE.Core.Runtime;

namespace OrchestratorIDE.Services.ContextFabric;

public sealed record FabricAskResult(
    string Answer,
    bool Abstained,
    string Mode,
    int SegmentsConsidered,
    int SegmentsTotal,
    bool FitsBudget,
    bool TriggeredSourceReopen,
    IReadOnlyList<FabricAnswerClaimResult> Claims);

public sealed record FabricAnswerClaimResult(
    string ClaimText,
    string VerificationLabel,      // Supported | PartiallySupported | Interpretive | CitationMismatch | Unverifiable
    IReadOnlyList<FabricCitationDetail> Citations);

public sealed record FabricCitationDetail(
    int Index,
    string SegmentId,
    string DocumentId,
    string HeadingPath,
    int CharStart,
    int CharEnd,
    string Quote,
    string VerificationLabel);

public sealed class FabricAskService(
    FabricQueryPlanner queryPlanner,
    EvidencePackBuilder packBuilder,
    FabricCitationVerifier citationVerifier,
    FabricLibraryRepository libraryRepository,
    IRoleRuntime runtime,
    FabricRunOptions? options = null)
{
    private static readonly FabricRunOptions _defaultOptions = FabricRunOptions.Default;
    private readonly FabricRunOptions _options = options ?? _defaultOptions;

    // System prompt template — answer contract. Adjust cf0-answer-1.2 prompt text here.
    private const string AnswerSystemPrompt = """
        You are a source-bound research assistant. Answer ONLY from the provided evidence.
        Respond with valid JSON matching this schema exactly:
        {"schemaVersion":"cf0-answer-1.0","answer":"<prose>","abstained":<bool>,"claims":[{"text":"<claim>","citations":[{"segmentId":"<id>","charStart":<n>,"charEnd":<n>,"quote":"<exact substring>","quoteDigest":""}]}]}
        Rules:
        - Every non-trivial claim must carry at least one citation with an exact quote from the evidence.
        - If the evidence does not support the question, set abstained:true and answer:"The provided sources do not contain sufficient evidence to answer this question."
        - Do not invent facts. Do not use knowledge outside the provided evidence.
        - quoteDigest is left empty — the host computes it.
        """;

    public async Task<FabricAskResult> AskAsync(
        string question,
        string corpusId,
        string mode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);

        // 1. Plan
        var plan = queryPlanner.BuildPlan(question, corpusId, mode);

        // 2. Pack evidence into token budget
        var pack = packBuilder.Build(plan);

        // 3. Build prompt and generate answer
        var evidenceText = BuildEvidenceText(pack);
        var prompt = $"""
            Evidence from the corpus:

            {evidenceText}

            ---
            Question: {question}

            Respond with the JSON answer schema.
            """;

        var rawAnswer = await GenerateAsync(prompt, ct).ConfigureAwait(false);

        // 4. Parse model output
        var draft = ParseAnswerDraft(rawAnswer);

        // 5. Verify citations and build claim results
        var claimResults = new List<FabricAnswerClaimResult>(draft.Claims.Count);
        var citIndex = 0;
        foreach (var claim in draft.Claims)
        {
            var verificationResult = citationVerifier.VerifyClaim(
                claim.Text, claim.Citations, allowRepair: true);

            var citDetails = verificationResult.Items
                .Select(item => BuildCitationDetail(ref citIndex, item))
                .ToList();

            claimResults.Add(new FabricAnswerClaimResult(
                claim.Text,
                verificationResult.OverallLabel,
                citDetails));
        }

        // 6. Resolve total segment count for coverage display
        var totalSegments = libraryRepository
            .ListDocuments(corpusId)
            .Sum(d => libraryRepository.GetSegments(d.DocumentId).Count);

        return new FabricAskResult(
            draft.Answer,
            draft.Abstained,
            plan.Mode,
            plan.SeedHits.Count + plan.ReopenedSegmentIds.Count,
            totalSegments,
            pack.FitsBudget,
            plan.TriggeredSourceReopen,
            claimResults);
    }

    private async Task<string> GenerateAsync(string prompt, CancellationToken ct)
    {
        // IRoleRuntime streaming — collect all tokens into a string.
        // Mirrors ContextFabricFeasibilityRunner's internal generation pattern.
        var sb = new System.Text.StringBuilder();
        await foreach (var token in runtime.GenerateStreamingAsync(
            AnswerSystemPrompt, prompt,
            maxTokens: _options.AnswerMaxTokens,
            temperature: _options.Temperature,
            ct: ct).ConfigureAwait(false))
        {
            sb.Append(token);
        }
        return sb.ToString();
    }

    private static FabricAnswerDraft ParseAnswerDraft(string raw)
    {
        // Strip markdown fences if model wrapped the JSON
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{');
            var end   = json.LastIndexOf('}');
            if (start >= 0 && end > start)
                json = json[start..(end + 1)];
        }

        try
        {
            return JsonSerializer.Deserialize<FabricAnswerDraft>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? new FabricAnswerDraft { Abstained = true, Answer = "Could not parse answer." };
        }
        catch
        {
            return new FabricAnswerDraft { Abstained = true, Answer = "Answer schema parse failed — raw: " + raw[..Math.Min(200, raw.Length)] };
        }
    }

    private static string BuildEvidenceText(FabricEvidencePack pack)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var item in pack.Included)
        {
            sb.AppendLine(item.IsSource
                ? $"[SOURCE {item.Id} | {item.Provenance}]"
                : $"[SUMMARY {item.Id} | {item.Provenance}]");
            sb.AppendLine(item.Text);
            sb.AppendLine();
        }
        if (pack.Excluded.Count > 0)
            sb.AppendLine($"// {pack.Excluded.Count} additional evidence items excluded by token budget.");
        return sb.ToString();
    }

    private static FabricCitationDetail BuildCitationDetail(
        ref int index, FabricCitationVerificationItem item)
    {
        index++;
        // Segment heading resolved from repo at render time in the UI layer.
        return new FabricCitationDetail(
            index, item.SegmentId, "", "", item.CharStart, item.CharEnd,
            item.MatchedQuote, item.Label);
    }
}
