// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricOpenExtractionTests
{
    [Test]
    public async Task ReadCorpusAsync_WithOpenExtractionReading_AcceptsClaimsFromUnMarkedSegment()
    {
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var fact = fixture.Manifest.LocalFacts[0];
        var segment = fixture.Corpus.Segments.Single(s => s.SegmentId == fact.SegmentId);
        var singleSegmentCorpus = fixture.Corpus with { Segments = [segment] };

        var runtime = new OpenExtractionScriptedRuntime(fact.StatementText, claimsToEmit: 1);
        var options = FabricRunOptions.Default with { OpenExtractionReading = true };
        var runner = new ContextFabricFeasibilityRunner(runtime, options);

        var report = await runner.ReadCorpusAsync(singleSegmentCorpus);

        Assert.Multiple(() =>
        {
            Assert.That(report.SegmentResults, Has.Count.EqualTo(1));
            Assert.That(report.SegmentResults[0].Accepted, Is.True, string.Join("; ", report.SegmentResults[0].Errors));
            Assert.That(report.SegmentResults[0].Card!.Claims.SelectMany(c => c.Citations).Select(c => c.Quote),
                Has.Some.EqualTo(fact.StatementText));
        });
    }

    [Test]
    public async Task ReadCorpusAsync_WithDefaultMarkedReading_RejectsUnMarkedSegment_WithZeroClaims()
    {
        // Regression guard for the exact bug this mode fixes: the marked-checklist reader prompt
        // instructs "no claims for other source text" against a segment with zero EVIDENCE: lines,
        // so a compliant model emits zero claims -- which the validator hard-rejects (claims must
        // contain between 1 and 64 items). OpenExtractionReading=false is the pre-existing default.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var fact = fixture.Manifest.LocalFacts[0];
        var segment = fixture.Corpus.Segments.Single(s => s.SegmentId == fact.SegmentId);
        var singleSegmentCorpus = fixture.Corpus with { Segments = [segment] };

        var runtime = new OpenExtractionScriptedRuntime(fact.StatementText, claimsToEmit: 0);
        var runner = new ContextFabricFeasibilityRunner(runtime, FabricRunOptions.Default);

        var report = await runner.ReadCorpusAsync(singleSegmentCorpus);

        Assert.That(report.SegmentResults[0].Accepted, Is.False);
    }

    private sealed class OpenExtractionScriptedRuntime(string statementToCite, int claimsToEmit)
        : IRoleRuntime, IRoleRuntimeDiagnostics
    {
        public string RuntimeName => "scripted-open-extraction";

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
            var input = messages.Last(m => m.Role == MessageRole.User).Content;
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var corpusId = root.GetProperty("corpusId").GetString()!;
            var documentId = root.GetProperty("documentId").GetString()!;
            var segmentId = root.GetProperty("segmentId").GetString()!;

            var claims = claimsToEmit == 0
                ? "[]"
                : $"[{{\"claimId\":\"c1\",\"type\":\"assertion\",\"text\":\"extracted fact\",\"confidence\":1.0," +
                  $"\"citations\":[{{\"segmentId\":\"{segmentId}\",\"charStart\":-1,\"charEnd\":-1," +
                  $"\"quote\":{JsonSerializer.Serialize(statementToCite)},\"quoteDigest\":\"\"}}]}}]";

            yield return "{\"schemaVersion\":\"cf0-evidence-card-1.0\"," +
                $"\"corpusId\":{JsonSerializer.Serialize(corpusId)}," +
                $"\"documentId\":{JsonSerializer.Serialize(documentId)}," +
                $"\"segmentId\":{JsonSerializer.Serialize(segmentId)}," +
                "\"promptVersion\":\"cf0-reader-1.2\",\"summary\":\"open extraction summary\"," +
                $"\"claims\":{claims},\"entities\":[],\"conflicts\":[],\"openQuestions\":[]}}";
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName, "scripted.gguf");
        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName, "scripted.gguf");
        public string? GetLastPromptPath(RuntimeRole role) => "Scripted";
    }
}
