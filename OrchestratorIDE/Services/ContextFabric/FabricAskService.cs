// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
// CF-5: OrcChat Library ask service — chains planner -> pack -> native answer -> verifier.

using System.Text;
using System.Text.Json;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

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
    string VerificationLabel,
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

    private const string AnswerSystemPrompt = """
        [FABRIC_ASK] You are a source-bound research assistant. Answer ONLY from the provided evidence.
        Respond with valid JSON matching this schema exactly:
        {"schemaVersion":"cf0-answer-1.0","answer":"<prose>","abstained":<bool>,"claims":[{"text":"<claim>","citations":[{"segmentId":"<id>","charStart":<n>,"charEnd":<n>,"quote":"<exact substring>","quoteDigest":""}]}]}
        Rules:
        - Every non-trivial claim must carry at least one citation with an exact quote from the evidence.
        - If the evidence does not support the question, set abstained:true and answer:"The provided sources do not contain sufficient evidence to answer this question."
        - Do not invent facts. Do not use knowledge outside the provided evidence.
        - quoteDigest is left empty - the host computes it.
        """;

    public async Task<FabricAskResult> AskAsync(
        string question,
        string corpusId,
        string mode,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(corpusId);

        var plan = queryPlanner.BuildPlan(question, corpusId, mode);
        var pack = packBuilder.Build(plan);

        var evidenceText = BuildEvidenceText(pack);
        var userPrompt = $"""
            Evidence from the corpus:

            {evidenceText}

            ---
            Question: {question}

            Respond with the JSON answer schema.
            """;

        var rawAnswer = await GenerateAsync(userPrompt, ct).ConfigureAwait(false);
        var draft = ParseAnswerDraft(rawAnswer);

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
                verificationResult.Label,
                citDetails));
        }

        var totalSegments = libraryRepository
            .ListDocuments(corpusId)
            .Sum(d => libraryRepository.GetSegments(d.DocumentId).Count);

        return new FabricAskResult(
            draft.Answer,
            draft.Abstained,
            plan.Mode,
            plan.SeedHits.Count + plan.ReopenedSegmentIds.Count,
            totalSegments,
            pack.WithinBudget,
            plan.TriggeredSourceReopen,
            claimResults);
    }

    private async Task<string> GenerateAsync(string userPrompt, CancellationToken ct)
    {
        var messages = new AgentMessage[]
        {
            new() { Role = MessageRole.System, Content = AnswerSystemPrompt },
            new() { Role = MessageRole.User, Content = userPrompt },
        };

        var sb = new StringBuilder();
        await foreach (var token in runtime.StreamRoleCompletionAsync(
            RuntimeRole.Reviewer,
            messages,
            temperature: _options.Temperature,
            maxTokens: _options.AnswerMaxTokens,
            ct: ct).ConfigureAwait(false))
        {
            sb.Append(token);
        }
        return sb.ToString();
    }

    private static FabricAnswerDraft ParseAnswerDraft(string raw)
    {
        var json = raw.Trim();
        if (json.StartsWith("```", StringComparison.Ordinal))
        {
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
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
            return new FabricAnswerDraft
            {
                Abstained = true,
                Answer = "Answer schema parse failed - raw: " + raw[..Math.Min(200, raw.Length)],
            };
        }
    }

    private static string BuildEvidenceText(FabricEvidencePack pack)
    {
        var sb = new StringBuilder();
        foreach (var item in pack.Included)
        {
            sb.AppendLine(item.FromSource
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
        return new FabricCitationDetail(
            index, item.SegmentId, "", "", item.CharStart, item.CharEnd,
            item.QuoteText, item.Label);
    }
}
