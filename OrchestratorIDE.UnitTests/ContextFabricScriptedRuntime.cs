// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

internal sealed class ScriptedFabricRuntime : IRoleRuntime, IRoleRuntimeDiagnostics
{
    public string RuntimeName => "scripted-native-cf0";
    public Func<IReadOnlyList<AgentMessage>, int?>? PromptTokenCounter { get; init; }

    public int? CountPromptTokens(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null) =>
        PromptTokenCounter?.Invoke(history.ToArray());

    public async IAsyncEnumerable<string> StreamRoleCompletionAsync(
        RuntimeRole role,
        IEnumerable<AgentMessage> history,
        IReadOnlyList<object>? tools = null,
        double temperature = 0.1,
        int maxTokens = 4096,
        Action<ToolCall>? onToolCall = null,
        Action<int, int>? onUsage = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
        var messages = history.ToArray();
        var system = messages.First(message => message.Role == MessageRole.System).Content;
        var input = messages.Last(message => message.Role == MessageRole.User).Content;
        string output;
        if (system.Contains("[FABRIC_READER]", StringComparison.Ordinal))
            output = BuildEvidenceCard(input);
        else if (system.Contains("[FABRIC_REDUCER]", StringComparison.Ordinal))
            output = BuildReduction(input);
        else if (system.Contains("[FABRIC_ANSWER]", StringComparison.Ordinal))
            output = BuildAnswer(input);
        else if (system.Contains("[FABRIC_STITCHER]", StringComparison.Ordinal))
            output = BuildStitch(input);
        else if (system.Contains("[FABRIC_QUERY]", StringComparison.Ordinal))
            output = BuildQueryFinding(input);
        else
            throw new InvalidOperationException("Unexpected CF-0 prompt.");

        yield return output;
    }

    public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName, "scripted.gguf");
    public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName, "scripted.gguf");
    public string? GetLastPromptPath(RuntimeRole role) => "Scripted";

    private static string BuildEvidenceCard(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        var corpusId = root.GetProperty("corpusId").GetString()!;
        var documentId = root.GetProperty("documentId").GetString()!;
        var segmentId = root.GetProperty("segmentId").GetString()!;
        var source = root.GetProperty("sourceText").GetString()!;
        var facts = source.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("EVIDENCE: ", StringComparison.Ordinal))
            .Select(line => line["EVIDENCE: ".Length..])
            .ToArray();
        var claims = facts.Select((fact, index) => new FabricClaim
        {
            ClaimId = $"{segmentId}-c{index + 1}",
            Text = fact,
            Confidence = 1,
            Citations =
            [
                new FabricCitation
                {
                    SegmentId = segmentId,
                    CharStart = -1,
                    CharEnd = -1,
                    Quote = fact,
                },
            ],
        }).ToList();
        return FabricJson.Serialize(new FabricEvidenceCard
        {
            CorpusId = corpusId,
            DocumentId = documentId,
            SegmentId = segmentId,
            Summary = string.Join(' ', facts),
            Claims = claims,
        });
    }

    private static string BuildReduction(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var children = doc.RootElement.GetProperty("children").EnumerateArray().ToArray();
        var claimIds = children
            .SelectMany(child => child.GetProperty("claimIds").EnumerateArray())
            .Select(id => id.GetString()!)
            .ToList();
        var summaries = children.Select(child => child.GetProperty("summary").GetString()!).ToArray();
        var conflicts = children
            .SelectMany(child => child.GetProperty("conflicts").EnumerateArray())
            .Select(value => value.GetString()!)
            .ToList();
        return FabricJson.Serialize(new FabricReductionDraft
        {
            Summary = string.Join(' ', summaries),
            ClaimIds = claimIds,
            Conflicts = conflicts,
        });
    }

    private static string BuildAnswer(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        var questionId = root.GetProperty("questionId").GetString()!;
        var evidence = root.GetProperty("evidence").EnumerateArray()
            .SelectMany(segment => segment.GetProperty("claims").EnumerateArray())
            .Select(claim => new EvidenceClaim(
                claim.GetProperty("text").GetString()!,
                claim.GetProperty("citations")[0].GetProperty("segmentId").GetString()!,
                claim.GetProperty("citations")[0].GetProperty("quote").GetString()!))
            .ToArray();

        if (questionId == "unanswerable-lunar-latitude")
        {
            return FabricJson.Serialize(new FabricAnswerDraft
            {
                Answer = "The corpus does not establish a lunar latitude for Station Merrow.",
                Abstained = true,
            });
        }

        var selected = questionId switch
        {
            "local-call-sign" => evidence.Where(item =>
                item.Text.Contains("call sign", StringComparison.OrdinalIgnoreCase)),
            "multihop-northstar-checksum" => evidence.Where(item =>
                item.Text.Contains("Northstar", StringComparison.OrdinalIgnoreCase) ||
                item.Text.Contains("checksum word", StringComparison.OrdinalIgnoreCase)),
            "contradiction-arden-material" => evidence.Where(item =>
                item.Text.Contains("hull material", StringComparison.OrdinalIgnoreCase)),
            "exhaustive-archive-tokens" => evidence.Where(item =>
                item.Text.Contains("archive token", StringComparison.OrdinalIgnoreCase)),
            _ => throw new InvalidOperationException($"Unsupported scripted questionId '{questionId}'."),
        };
        var selectedArray = selected.ToArray();
        if (selectedArray.Length == 0)
            throw new InvalidOperationException($"No scripted evidence matched questionId '{questionId}'.");

        return FabricJson.Serialize(new FabricAnswerDraft
        {
            Answer = string.Join(' ', selectedArray.Select(item => item.Text)),
            Claims = selectedArray.Select(item => new FabricAnswerClaim
            {
                Text = item.Text,
                Citations =
                [
                    new FabricCitation
                    {
                        SegmentId = item.SegmentId,
                        CharStart = -1,
                        CharEnd = -1,
                        Quote = item.Quote,
                    },
                ],
            }).ToList(),
        });
    }

    private static string BuildStitch(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var caseId = doc.RootElement.GetProperty("caseId").GetString()!;
        return caseId switch
        {
            "cross-clause-result" => FabricJson.Serialize(new FabricBoundaryStitchDraft
            {
                CaseId = caseId,
                Summary = "The navigation council approved the delta route, resulting in a forty percent reduction in spring travel time during the field trials.",
                LinkedFacts =
                [
                    "The navigation council approved the delta route.",
                    "The result was a forty percent reduction in spring travel time during the field trials.",
                ],
            }),
            "cross-pronoun-reference" => FabricJson.Serialize(new FabricBoundaryStitchDraft
            {
                CaseId = caseId,
                Summary = "The archive crew sealed the blue ledger in cabinet forty-two and documented the transfer at 19:40 UTC before leaving the records office.",
                LinkedFacts =
                [
                    "The archive crew sealed the blue ledger in cabinet forty-two.",
                    "They documented the transfer at 19:40 UTC before leaving the records office.",
                ],
            }),
            _ => FabricJson.Serialize(new FabricBoundaryStitchDraft
            {
                CaseId = caseId,
                Summary = $"Stitched boundary for case '{caseId}'.",
                LinkedFacts = [],
            }),
        };
    }

    private static string BuildQueryFinding(string input)
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        var questionId = root.GetProperty("questionId").GetString()!;
        var segmentId = root.GetProperty("segmentId").GetString()!;
        var source = root.GetProperty("sourceText").GetString()!;

        var evidenceFacts = source.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("EVIDENCE: ", StringComparison.Ordinal))
            .Select(line => line["EVIDENCE: ".Length..])
            .ToArray();

        if (evidenceFacts.Length == 0)
            return FabricJson.Serialize(new FabricQueryFindingDraft { Relevant = false });

        var claims = evidenceFacts.Select((fact, index) => new FabricClaim
        {
            ClaimId = $"{segmentId}-q{index + 1}",
            Text = fact,
            Confidence = 1,
            Citations =
            [
                new FabricCitation
                {
                    SegmentId = segmentId,
                    CharStart = -1,
                    CharEnd = -1,
                    Quote = fact,
                },
            ],
        }).ToList();

        return FabricJson.Serialize(new FabricQueryFindingDraft
        {
            Relevant = true,
            FindingText = string.Join(' ', evidenceFacts),
            Claims = claims,
        });
    }

    private sealed record EvidenceClaim(string Text, string SegmentId, string Quote);
}
