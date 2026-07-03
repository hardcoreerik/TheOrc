// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class ContextFabricAskServiceTests
{
    [Test]
    public async Task AskAsync_Returns_Supported_Citation_For_Exact_Quote()
    {
        using var harness = NewHarness();
        var quote = "LANTERN is the assigned call sign.";
        var runtime = new FakeAskRuntime(BuildAnswerJson(quote, "seg-0"));
        var service = harness.NewAskService(runtime);

        var result = await service.AskAsync("What is the call sign?", harness.Corpus.CorpusId, FabricQueryMode.Quick);

        Assert.Multiple(() =>
        {
            Assert.That(result.Abstained, Is.False);
            Assert.That(result.Mode, Is.EqualTo(FabricQueryMode.Quick));
            Assert.That(result.SegmentsTotal, Is.EqualTo(2));
            Assert.That(result.Claims, Has.Count.EqualTo(1));
            Assert.That(result.Claims[0].VerificationLabel, Is.EqualTo(FabricCitationVerificationLabel.Supported));
            Assert.That(result.Claims[0].Citations.Single().Quote, Is.EqualTo(quote));
        });
    }

    [Test]
    public async Task AskAsync_Marks_Mismatched_Citation()
    {
        using var harness = NewHarness();
        var runtime = new FakeAskRuntime(BuildAnswerJson("LANTERN is the call sign.", "seg-0", quoteDigest: "wrong-digest"));
        var service = harness.NewAskService(runtime);

        var result = await service.AskAsync("What is the call sign?", harness.Corpus.CorpusId, FabricQueryMode.Quick);

        Assert.That(result.Claims[0].VerificationLabel, Is.EqualTo(FabricCitationVerificationLabel.CitationMismatch));
    }

    [Test]
    public async Task AskAsync_Resolves_Source_Label_To_Segment_Id()
    {
        using var harness = NewHarness();
        var quote = "LANTERN is the assigned call sign.";
        var runtime = new FakeAskRuntime($$"""
            {"schemaVersion":"cf0-answer-1.0","answer":"{{quote}}","abstained":false,"claims":[{"text":"{{quote}}","citations":[{"sourceLabel":"S1","charStart":0,"charEnd":{{quote.Length}},"quote":"{{quote}}","quoteDigest":""}]}]}
            """);
        var service = harness.NewAskService(runtime);

        var result = await service.AskAsync("What is the call sign?", harness.Corpus.CorpusId, FabricQueryMode.Quick);

        Assert.Multiple(() =>
        {
            Assert.That(result.Claims[0].VerificationLabel, Is.EqualTo(FabricCitationVerificationLabel.Supported));
            Assert.That(result.Claims[0].Citations[0].SegmentId, Is.EqualTo("seg-0"));
        });
    }

    [Test]
    public async Task AskAsync_Surfaces_Abstention()
    {
        using var harness = NewHarness();
        var runtime = new FakeAskRuntime("""{"schemaVersion":"cf0-answer-1.0","answer":"The provided sources do not contain sufficient evidence to answer this question.","abstained":true,"claims":[]}""");
        var service = harness.NewAskService(runtime);

        var result = await service.AskAsync("What is the orbital period?", harness.Corpus.CorpusId, FabricQueryMode.Quick);

        Assert.Multiple(() =>
        {
            Assert.That(result.Abstained, Is.True);
            Assert.That(result.Claims, Is.Empty);
        });
    }

    [Test]
    public async Task AskAsync_Parses_First_Object_When_Model_Rambles_Past_A_Complete_Answer()
    {
        // Reproduces a live CF-5 failure: the model closed a valid ```json answer, then kept
        // generating -- a "<|channel>thought<channel|>" artifact followed by a near-duplicate
        // JSON block -- until token budget cut it off. The naive first-'{'-to-last-'}' slice
        // used to swallow all of that trailing noise as one (invalid) JSON blob.
        using var harness = NewHarness();
        const string rambling = """
            ```json
            {"schemaVersion":"cf0-answer-1.0","answer":"The provided sources do not contain sufficient evidence.","abstained":true,"claims":[]}
            ```<|channel>thought
            <channel|>```json
            {"schemaVersion":"cf0-answer-1.0","answer":"The provided sources do not contain sufficient
            """;
        var runtime = new FakeAskRuntime(rambling);
        var service = harness.NewAskService(runtime);

        var result = await service.AskAsync("What is the call sign?", harness.Corpus.CorpusId, FabricQueryMode.Quick);

        Assert.Multiple(() =>
        {
            Assert.That(result.Abstained, Is.True);
            Assert.That(result.Answer, Is.EqualTo("The provided sources do not contain sufficient evidence."));
            Assert.That(result.Claims, Is.Empty);
        });
    }

    [Test]
    public void AskAsync_Rejects_Blank_Question()
    {
        using var harness = NewHarness();
        var service = harness.NewAskService(new FakeAskRuntime("{}"));

        Assert.That(
            () => service.AskAsync("   ", harness.Corpus.CorpusId, FabricQueryMode.Quick).GetAwaiter().GetResult(),
            Throws.TypeOf<ArgumentException>());
    }

    private static string BuildAnswerJson(string quote, string segmentId, string? quoteDigest = null) =>
        $$"""
        {"schemaVersion":"cf0-answer-1.0","answer":"{{quote}}","abstained":false,"claims":[{"text":"{{quote}}","citations":[{"segmentId":"{{segmentId}}","charStart":0,"charEnd":{{quote.Length}},"quote":"{{quote}}","quoteDigest":"{{quoteDigest ?? FabricHashing.Sha256(quote)}}"}]}]}
        """;

    private static Harness NewHarness()
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var corpus = library.CreateCorpus("cf5-ask-corpus", "CF-5 ask lane");
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            "cf5-ask-doc",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "CF-5 Ask Notes",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);

        var texts = new[]
        {
            "LANTERN is the assigned call sign.",
            "Emergency frequency is 17.4 MHz.",
        };
        var start = 0;
        library.ReplaceDocument(document, texts.Select((text, index) =>
        {
            var draft = new FabricSegmentDraft(
                $"seg-{index}",
                index,
                $"Section {index}",
                start,
                start + text.Length,
                Math.Max(6, text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length),
                FabricHashing.Sha256(text),
                text,
                index > 0 ? $"seg-{index - 1}" : null,
                index < texts.Length - 1 ? $"seg-{index + 1}" : null,
                FabricIngestionVersions.Segmenter);
            start += text.Length + 1;
            return draft;
        }).ToArray());

        return new Harness(store, library, graph, corpus, document);
    }

    private sealed class Harness(
        SqliteStore store,
        FabricLibraryRepository library,
        DocumentGraphRepository graph,
        FabricCorpusEntry corpus,
        FabricDocumentEntry document) : IDisposable
    {
        public FabricCorpusEntry Corpus { get; } = corpus;
        public FabricDocumentEntry Document { get; } = document;

        public FabricAskService NewAskService(IRoleRuntime runtime)
        {
            var search = new FabricSearchService(library, graph);
            var planner = new FabricQueryPlanner(search, graph);
            var packBuilder = new EvidencePackBuilder(library, graph);
            var verifier = new FabricCitationVerifier(library);
            return new FabricAskService(planner, packBuilder, verifier, library, runtime);
        }

        public void Dispose() => store.Dispose();
    }

    /// <summary>Minimal IRoleRuntime stand-in for FabricAskService — returns one canned JSON
    /// answer regardless of prompt content, since these tests only assert on the
    /// verification/parsing path, not retrieval-driven prompt content.</summary>
    private sealed class FakeAskRuntime(string answerJson) : IRoleRuntime
    {
        public string RuntimeName => "fake-ask-runtime";

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
            yield return answerJson;
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName, "fake.gguf");
        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName, "fake.gguf");
    }
}
