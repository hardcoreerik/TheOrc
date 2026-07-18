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
    public async Task ReadCorpusAsync_OpenExtraction_RepairRecoversFactTheFirstPassMissedEntirely()
    {
        // Regression guard for the CF-7 gate finding (2026-07-17): a compliant open-extraction
        // reader that returns zero claims for a segment previously short-circuited straight to
        // rejection -- the missing-evidence repair pass (which exists for marked mode) never even
        // ran, because it lived after an early return on "claims must contain at least 1 item".
        // A segment whose one real fact sits among filler lines should now get a second, best-
        // effort look before being rejected.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var fact = fixture.Manifest.LocalFacts[0];
        var segment = fixture.Corpus.Segments.Single(s => s.SegmentId == fact.SegmentId);
        var singleSegmentCorpus = fixture.Corpus with { Segments = [segment] };

        var runtime = new OpenExtractionRepairScriptedRuntime(
            initialClaimStatements: [],
            repairClaimStatements: [fact.StatementText]);
        var options = FabricRunOptions.Default with { OpenExtractionReading = true };
        var runner = new ContextFabricFeasibilityRunner(runtime, options);

        var report = await runner.ReadCorpusAsync(singleSegmentCorpus);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RepairCallCount, Is.EqualTo(1), "the empty first pass must trigger exactly one repair attempt");
            Assert.That(report.SegmentResults[0].Accepted, Is.True, string.Join("; ", report.SegmentResults[0].Errors));
            Assert.That(report.SegmentResults[0].Card!.Claims.SelectMany(c => c.Citations).Select(c => c.Quote),
                Has.Some.EqualTo(fact.StatementText));
        });
    }

    [Test]
    public async Task ReadCorpusAsync_OpenExtraction_RepairDecliningEveryCandidate_StillRejectsTheSegment()
    {
        // The repair prompt is allowed to return zero claims (every flagged candidate turns out to
        // be filler) -- the completeness check must not paper over a genuinely fact-free segment by
        // force-accepting it just because a repair call happened.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var fact = fixture.Manifest.LocalFacts[0];
        var segment = fixture.Corpus.Segments.Single(s => s.SegmentId == fact.SegmentId);
        var singleSegmentCorpus = fixture.Corpus with { Segments = [segment] };

        var runtime = new OpenExtractionRepairScriptedRuntime(
            initialClaimStatements: [],
            repairClaimStatements: []);
        var options = FabricRunOptions.Default with { OpenExtractionReading = true };
        var runner = new ContextFabricFeasibilityRunner(runtime, options);

        var report = await runner.ReadCorpusAsync(singleSegmentCorpus);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RepairCallCount, Is.EqualTo(1));
            Assert.That(report.SegmentResults[0].Accepted, Is.False);
        });
    }

    [Test]
    public async Task ReadCorpusAsync_OpenExtraction_AllCandidateCodesAlreadyCovered_SkipsRepairEntirely()
    {
        // A segment whose reader output already cites every candidate-coded line must not pay for
        // a repair call it doesn't need. This corpus densely packs multiple planted facts per
        // segment (verified: xseg-0001 alone carries a local fact, a chain hop, and a ledger row),
        // so the fixture segment must be seeded with ALL of its coded lines, not just one, or the
        // completeness check would (correctly) still flag the others as missing.
        var fixture = DeterministicExpandedFabricCorpus.Create();
        var fact = fixture.Manifest.LocalFacts[0];
        var segment = fixture.Corpus.Segments.Single(s => s.SegmentId == fact.SegmentId);
        var singleSegmentCorpus = fixture.Corpus with { Segments = [segment] };
        var everyCodedLine = segment.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => System.Text.RegularExpressions.Regex.IsMatch(line, @"\b[A-Za-z]{2,10}-[0-9][0-9A-Za-z-]{0,12}\b"))
            .ToArray();
        Assert.That(everyCodedLine, Has.Some.EqualTo(fact.StatementText), "sanity: the fixture fact must be one of the coded lines");

        var runtime = new OpenExtractionRepairScriptedRuntime(
            initialClaimStatements: everyCodedLine,
            repairClaimStatements: []);
        var options = FabricRunOptions.Default with { OpenExtractionReading = true };
        var runner = new ContextFabricFeasibilityRunner(runtime, options);

        var report = await runner.ReadCorpusAsync(singleSegmentCorpus);

        Assert.Multiple(() =>
        {
            Assert.That(runtime.RepairCallCount, Is.EqualTo(0), "every coded line is already covered -- no repair call is needed");
            Assert.That(report.SegmentResults[0].Accepted, Is.True);
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

    /// <summary>Distinguishes the initial [FABRIC_READER_OPEN] call from the follow-up
    /// [FABRIC_READER_OPEN_REPAIR] call by system-message tag, so a test can script each
    /// independently and assert on how many repair calls actually happened.</summary>
    private sealed class OpenExtractionRepairScriptedRuntime(
        IReadOnlyList<string> initialClaimStatements,
        IReadOnlyList<string> repairClaimStatements)
        : IRoleRuntime, IRoleRuntimeDiagnostics
    {
        public string RuntimeName => "scripted-open-extraction-repair";
        public int RepairCallCount { get; private set; }

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
            var isRepair = messages.Any(m => m.Role == MessageRole.System
                && m.Content.Contains("[FABRIC_READER_OPEN_REPAIR]", StringComparison.Ordinal));
            if (isRepair) RepairCallCount++;

            var input = messages.Last(m => m.Role == MessageRole.User).Content;
            using var doc = JsonDocument.Parse(input);
            var root = doc.RootElement;
            var corpusId = root.GetProperty("corpusId").GetString()!;
            var documentId = root.GetProperty("documentId").GetString()!;
            var segmentId = root.GetProperty("segmentId").GetString()!;

            var statements = isRepair ? repairClaimStatements : initialClaimStatements;
            var claims = statements.Count == 0
                ? "[]"
                : "[" + string.Join(",", statements.Select((statement, i) =>
                    $"{{\"claimId\":\"c{i + 1}\",\"type\":\"assertion\",\"text\":{JsonSerializer.Serialize(statement)}," +
                    $"\"confidence\":1.0,\"citations\":[{{\"segmentId\":\"{segmentId}\",\"charStart\":-1,\"charEnd\":-1," +
                    $"\"quote\":{JsonSerializer.Serialize(statement)},\"quoteDigest\":\"\"}}]}}")) + "]";

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
