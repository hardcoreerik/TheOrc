// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.ContextFabric;
using OrchestratorIDE.Services.Data;
using OrchestratorIDE.UI.ViewModels;

namespace OrchestratorIDE.Avalonia.HeadlessTests;

/// <summary>
/// Shared CF-5 wiring for headless ChatPanel tests — builds a real in-memory corpus/document
/// plus the full FabricAskService/FabricIndexingOrchestrator/LibraryViewModel stack against a
/// scripted IRoleRuntime, mirroring OrchestratorIDE.UnitTests' ContextFabricAskServiceTests
/// harness shape (kept local here since HeadlessTests can't reference UnitTests' internal
/// fakes).
/// </summary>
internal sealed class Cf5TestHarness : IDisposable
{
    public SqliteStore Store { get; }
    public FabricLibraryRepository Library { get; }
    public DocumentGraphRepository Graph { get; }
    public FabricCorpusEntry Corpus { get; }
    public FabricDocumentEntry Document { get; }
    public string WorkspaceRoot { get; }

    private Cf5TestHarness(
        SqliteStore store, FabricLibraryRepository library, DocumentGraphRepository graph,
        FabricCorpusEntry corpus, FabricDocumentEntry document, string workspaceRoot)
    {
        Store = store;
        Library = library;
        Graph = graph;
        Corpus = corpus;
        Document = document;
        WorkspaceRoot = workspaceRoot;
    }

    public static Cf5TestHarness Create(string quote = "LANTERN is the assigned call sign.")
    {
        var store = new SqliteStore(":memory:");
        store.Initialize();
        var library = new FabricLibraryRepository(store);
        var graph = new DocumentGraphRepository(store);
        var corpus = library.CreateCorpus("cf5-headless-corpus", "CF-5 headless lane");
        var now = DateTimeOffset.UtcNow;
        var document = new FabricDocumentEntry(
            "cf5-headless-doc",
            corpus.CorpusId,
            "source-digest",
            "normalized-digest",
            "CF-5 Headless Notes",
            "text/plain",
            FabricIngestionVersions.TextMarkdownParser,
            FabricIngestionVersions.TextMarkdownParser,
            "ready",
            [],
            now,
            now);

        var texts = new[] { quote, "Emergency frequency is 17.4 MHz." };
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

        var workspaceRoot = Path.Combine(Path.GetTempPath(), "orc-cf5-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);

        return new Cf5TestHarness(store, library, graph, corpus, document, workspaceRoot);
    }

    public FabricAskService NewAskService(IRoleRuntime runtime)
    {
        var search = new FabricSearchService(Library, Graph);
        var planner = new FabricQueryPlanner(search, Graph);
        var packBuilder = new EvidencePackBuilder(Library, Graph);
        var verifier = new FabricCitationVerifier(Library);
        return new FabricAskService(planner, packBuilder, verifier, Library, runtime);
    }

    public FabricIndexingOrchestrator NewOrchestrator(IRoleRuntime runtime)
    {
        var reader = new FabricNativeReaderService(Library, Graph, runtime);
        var reducer = new FabricReducer(Library, Graph);
        return new FabricIndexingOrchestrator(reader, reducer, Library);
    }

    public LibraryViewModel NewLibraryViewModel()
    {
        var artifacts = new Services.Hive.ContentAddressedStore(Path.Combine(WorkspaceRoot, "objects"));
        var libraryService = new FabricLibraryService(Library, artifacts);
        return new LibraryViewModel(libraryService, Library, Path.Combine(WorkspaceRoot, "fabric"));
    }

    public void Dispose()
    {
        Store.Dispose();
        try { Directory.Delete(WorkspaceRoot, recursive: true); } catch { /* best-effort cleanup */ }
    }
}

/// <summary>Minimal IRoleRuntime stand-in — returns one canned JSON answer regardless of
/// prompt content, for tests that only assert on the ChatPanel UI wiring, not retrieval.</summary>
internal sealed class FakeFabricRuntime(string answerJson) : IRoleRuntime
{
    public string RuntimeName => "fake-fabric-runtime";

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

    public static string BuildAnswerJson(string quote, string segmentId) =>
        $$"""
        {"schemaVersion":"cf0-answer-1.0","answer":"{{quote}}","abstained":false,"claims":[{"text":"{{quote}}","citations":[{"segmentId":"{{segmentId}}","charStart":0,"charEnd":{{quote.Length}},"quote":"{{quote}}","quoteDigest":"{{FabricHashing.Sha256(quote)}}"}]}]}
        """;
}
