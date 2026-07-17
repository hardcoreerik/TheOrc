// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Core;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class NativeRuntimeTestSupportTests
{
    private readonly List<string> _tempRoots = [];

    [TearDown]
    public void TearDown()
    {
        foreach (var root in _tempRoots)
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Best-effort temp cleanup on Windows.
            }
        }

        _tempRoots.Clear();
    }

    [Test]
    public async Task FallbackCoordinator_Returns_NativeSuccess_Without_Invoking_Fallback()
    {
        var nativeCalled = 0;
        var confirmCalled = 0;
        var fallbackCalled = 0;

        var outcome = await NativeRuntimeFallbackCoordinator.ExecuteAsync(
            _ =>
            {
                nativeCalled++;
                return Task.FromResult(Attempt("LLamaSharp", "boss.gguf", success: true, output: "native runtime ok"));
            },
            (_, _) =>
            {
                confirmCalled++;
                return Task.FromResult(true);
            },
            _ =>
            {
                fallbackCalled++;
                return Task.FromResult(Attempt("Ollama", "qwen", success: true, output: "fallback ok"));
            });

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(NativeRuntimeTestOutcomeKind.NativeSuccess));
            Assert.That(nativeCalled, Is.EqualTo(1));
            Assert.That(confirmCalled, Is.EqualTo(0));
            Assert.That(fallbackCalled, Is.EqualTo(0));
        });
    }

    [Test]
    public async Task FallbackCoordinator_Returns_Declined_When_User_Declines_Fallback()
    {
        var outcome = await NativeRuntimeFallbackCoordinator.ExecuteAsync(
            _ => Task.FromResult(Attempt("LLamaSharp", "boss.gguf", success: false, errorType: "LoadFailed")),
            (_, _) => Task.FromResult(false),
            _ => Task.FromResult(Attempt("Ollama", "qwen", success: true, output: "unused")));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(NativeRuntimeTestOutcomeKind.NativeFailedFallbackDeclined));
            Assert.That(outcome.FallbackAttempt, Is.Null);
        });
    }

    [Test]
    public async Task FallbackCoordinator_Returns_FallbackSuccess_When_Ollama_Succeeds()
    {
        var outcome = await NativeRuntimeFallbackCoordinator.ExecuteAsync(
            _ => Task.FromResult(Attempt("LLamaSharp", "boss.gguf", success: false, errorType: "LoadFailed")),
            (_, _) => Task.FromResult(true),
            _ => Task.FromResult(Attempt("Ollama", "qwen", success: true, output: "native runtime ok")));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaSuccess));
            Assert.That(outcome.FallbackAttempt, Is.Not.Null);
            Assert.That(outcome.FallbackAttempt!.Success, Is.True);
        });
    }

    [Test]
    public async Task FallbackCoordinator_Returns_DoubleFailure_When_Ollama_Fails_Too()
    {
        var outcome = await NativeRuntimeFallbackCoordinator.ExecuteAsync(
            _ => Task.FromResult(Attempt("LLamaSharp", "boss.gguf", success: false, errorType: "LoadFailed")),
            (_, _) => Task.FromResult(true),
            _ => Task.FromResult(Attempt("Ollama", "qwen", success: false, errorType: "ConnectionFailed", errorMessage: "offline")));

        Assert.Multiple(() =>
        {
            Assert.That(outcome.Kind, Is.EqualTo(NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaFailed));
            Assert.That(outcome.FallbackAttempt, Is.Not.Null);
            Assert.That(outcome.FallbackAttempt!.Success, Is.False);
        });
    }

    [Test]
    public async Task EvidenceStore_Writes_Sanitized_Truncated_Json_For_Native_Failure()
    {
        var root = NewTempRoot();
        var prompt = "ok" + new string('\u0001', 2) + new string('p', 5000);
        var output = new string('o', 9000);
        var error = "bad" + '\u0002' + "news";
        var outcome = new NativeRuntimeTestOutcome(
            NativeRuntimeTestOutcomeKind.NativeFailedFallbackAcceptedOllamaSuccess,
            Attempt("LLamaSharp", "boss.gguf", success: false, promptText: prompt, errorType: "LoadFailed", errorMessage: error),
            Attempt("Ollama", "qwen", success: true, promptText: prompt, output: output));

        var path = await NativeRuntimeFallbackEvidenceStore.WriteAsync(outcome, root);

        Assert.That(path, Is.Not.Null);
        Assert.That(File.Exists(path!), Is.True);

        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path!));
        var promptText = doc.RootElement.GetProperty("prompt_text").GetString();
        var nativeError = doc.RootElement.GetProperty("native").GetProperty("error_message").GetString();
        var fallbackOutput = doc.RootElement.GetProperty("fallback").GetProperty("output").GetString();

        Assert.Multiple(() =>
        {
            Assert.That(promptText, Does.Not.Contain('\u0001'));
            Assert.That(promptText, Does.EndWith("…[truncated]"));
            Assert.That(promptText!.Length, Is.EqualTo(4108));
            Assert.That(nativeError, Does.Not.Contain('\u0002'));
            Assert.That(fallbackOutput, Does.EndWith("…[truncated]"));
            Assert.That(fallbackOutput!.Length, Is.EqualTo(8204));
        });
    }

    [Test]
    public async Task EvidenceStore_Does_Not_Write_For_NativeSuccess()
    {
        var root = NewTempRoot();
        var path = await NativeRuntimeFallbackEvidenceStore.WriteAsync(
            new NativeRuntimeTestOutcome(
                NativeRuntimeTestOutcomeKind.NativeSuccess,
                Attempt("LLamaSharp", "boss.gguf", success: true, output: "native runtime ok")),
            root);

        Assert.That(path, Is.Null);
        Assert.That(Directory.Exists(Path.Combine(root, ".orc", "runtime-fallback")), Is.False);
    }

    [Test]
    public void ComparisonRunner_Evaluate_ExactText_Normalizes_Whitespace()
    {
        var testCase = new NativeRuntimeComparisonCase(
            "exact",
            "format",
            "unused",
            NativeRuntimeComparisonExpectationKind.ExactText,
            "alpha beta");
        var attempt = Attempt("LLamaSharp", "boss.gguf", success: true, output: " alpha   beta \r\n");

        var evaluation = NativeRuntimeComparisonRunner.Evaluate(testCase, attempt);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.ExpectationMatched, Is.True);
            Assert.That(evaluation.CanonicalOutput, Is.EqualTo("alpha beta"));
            Assert.That(evaluation.ValidationErrors, Is.Empty);
        });
    }

    [Test]
    public void ComparisonRunner_Evaluate_JsonExact_Canonicalizes_KeySpacing()
    {
        var testCase = new NativeRuntimeComparisonCase(
            "json",
            "json",
            "unused",
            NativeRuntimeComparisonExpectationKind.JsonExact,
            "{\"status\":\"ok\",\"count\":3}");
        var attempt = Attempt("Ollama", "qwen", success: true, output: "{ \"status\" : \"ok\", \"count\" : 3 }");

        var evaluation = NativeRuntimeComparisonRunner.Evaluate(testCase, attempt);

        Assert.Multiple(() =>
        {
            Assert.That(evaluation.ExpectationMatched, Is.True);
            Assert.That(evaluation.CanonicalOutput, Is.EqualTo("{\"status\":\"ok\",\"count\":3}"));
            Assert.That(evaluation.ValidationErrors, Is.Empty);
        });
    }

    [Test]
    public async Task ComparisonRunner_RunAsync_Builds_Summary_And_Match_Details()
    {
        var cases = new[]
        {
            new NativeRuntimeComparisonCase(
                "literal",
                "format",
                "Reply with exactly one word: yes",
                NativeRuntimeComparisonExpectationKind.ExactText,
                "yes",
                MaxTokens: 8),
            new NativeRuntimeComparisonCase(
                "math",
                "reasoning",
                "Compute 12 + 30 and answer with digits only.",
                NativeRuntimeComparisonExpectationKind.Regex,
                "^42$",
                MaxTokens: 8),
        };

        var native = new PromptMappedRuntime(new Dictionary<string, string>
        {
            [cases[0].PromptText] = "yes",
            [cases[1].PromptText] = "42",
        });
        var ollama = new PromptMappedRuntime(new Dictionary<string, string>
        {
            [cases[0].PromptText] = "yes",
            [cases[1].PromptText] = "41",
        });

        var report = await NativeRuntimeComparisonRunner.RunAsync(
            native,
            "boss.gguf",
            ollama,
            "qwen",
            cases,
            corpusName: "test-corpus");

        Assert.Multiple(() =>
        {
            Assert.That(report.CorpusName, Is.EqualTo("test-corpus"));
            Assert.That(report.Results, Has.Count.EqualTo(2));
            Assert.That(report.Summary.TotalCases, Is.EqualTo(2));
            Assert.That(report.Summary.NativePassedCases, Is.EqualTo(2));
            Assert.That(report.Summary.OllamaPassedCases, Is.EqualTo(1));
            Assert.That(report.Summary.BothPassedCases, Is.EqualTo(1));
            Assert.That(report.Results[0].CanonicalOutputsMatch, Is.True);
            Assert.That(report.Results[1].OllamaEvaluation.ExpectationMatched, Is.False);
        });
    }

    [Test]
    public void ComparisonCaseResult_DoesNotTreatDualFailuresAsCanonicalMatch()
    {
        var testCase = new NativeRuntimeComparisonCase(
            "failure",
            "format",
            "unused",
            NativeRuntimeComparisonExpectationKind.ExactText,
            "yes");
        var failedAttempt = Attempt("LLamaSharp", "boss.gguf", success: false, output: null, errorType: "Boom");
        var result = new NativeRuntimeComparisonCaseResult(
            testCase,
            failedAttempt,
            new NativeRuntimeComparisonEvaluation(false, false, "", ["native failed"]),
            failedAttempt with { RuntimeName = "Ollama", ModelRef = "qwen" },
            new NativeRuntimeComparisonEvaluation(false, false, "", ["fallback failed"]));

        Assert.That(result.CanonicalOutputsMatch, Is.False);
    }

    [Test]
    public async Task ComparisonReportStore_Writes_Json_Report()
    {
        var root = NewTempRoot();
        var report = new NativeRuntimeComparisonReport(
            NativeRuntimeComparisonCorpus.DefaultCorpusName,
            "LLamaSharp",
            "boss.gguf",
            "Ollama",
            "qwen",
            DateTimeOffset.UtcNow,
            [
                new NativeRuntimeComparisonCaseResult(
                    new NativeRuntimeComparisonCase(
                        "literal",
                        "format",
                        "unused",
                        NativeRuntimeComparisonExpectationKind.ExactText,
                        "yes"),
                    Attempt("LLamaSharp", "boss.gguf", success: true, output: "yes"),
                    new NativeRuntimeComparisonEvaluation(true, true, "yes", []),
                    Attempt("Ollama", "qwen", success: true, output: "yes"),
                    new NativeRuntimeComparisonEvaluation(true, true, "yes", []))
            ],
            new NativeRuntimeComparisonSummary(1, 1, 1, 1, 1, 0, 0));

        var path = await NativeRuntimeComparisonReportStore.WriteAsync(report, root);

        Assert.That(File.Exists(path), Is.True);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(path));
        Assert.Multiple(() =>
        {
            Assert.That(doc.RootElement.GetProperty("schema_version").GetString(), Is.EqualTo("1"));
            Assert.That(doc.RootElement.GetProperty("CorpusName").GetString(), Is.EqualTo(NativeRuntimeComparisonCorpus.DefaultCorpusName));
            Assert.That(doc.RootElement.GetProperty("Summary").GetProperty("BothPassedCases").GetInt32(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task NativeRuntimeSmoke_WithConfiguredGguf_Loads_Generates_And_Repeats()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the native smoke lane.");

        var first = await NativeRuntimeTestRunner.RunLocalAsync(ggufPath!);
        var second = await NativeRuntimeTestRunner.RunLocalAsync(ggufPath!);

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True, first.ErrorMessage ?? "first native smoke failed");
            Assert.That(second.Success, Is.True, second.ErrorMessage ?? "second native smoke failed");
            Assert.That(first.Output, Is.Not.Null.And.Not.Empty);
            Assert.That(second.Output, Is.Not.Null.And.Not.Empty);
            Assert.That(first.Stats.LastTimeToFirstToken, Is.Not.Null);
            Assert.That(second.Stats.LastTimeToFirstToken, Is.Not.Null);
        });
    }

    [Test]
    public async Task NativeRuntimeParity_WithConfiguredNativeAndOllama_Runs_Default_Corpus()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the native parity lane.");

        var ollamaModel = Environment.GetEnvironmentVariable("THEORC_TEST_OLLAMA_MODEL");
        if (string.IsNullOrWhiteSpace(ollamaModel))
            Assert.Ignore("Set THEORC_TEST_OLLAMA_MODEL to run the native parity lane.");

        var ollamaHost = Environment.GetEnvironmentVariable("THEORC_TEST_OLLAMA_HOST") ?? "http://localhost:11434";

        await using var native = new LLamaSharpRuntime();
        var load = await native.LoadModelAsync(ggufPath!);
        if (!load.Success)
            Assert.Fail(load.Message ?? "Native model load failed.");

        var report = await NativeRuntimeComparisonRunner.RunAsync(
            native,
            load.ModelRef,
            new OllamaRuntime(new OllamaClient(ollamaHost)),
            ollamaModel!);

        var reportPath = await NativeRuntimeComparisonReportStore.WriteAsync(
            report,
            Path.GetDirectoryName(Path.GetFullPath(ggufPath!)));

        Assert.Multiple(() =>
        {
            Assert.That(report.Results, Has.Count.EqualTo(NativeRuntimeComparisonCorpus.DefaultCases.Count));
            Assert.That(report.Summary.NativeExecutionFailures, Is.EqualTo(0));
            Assert.That(report.Summary.OllamaExecutionFailures, Is.EqualTo(0));
            Assert.That(report.Summary.NativePassedCases, Is.EqualTo(report.Summary.TotalCases));
            Assert.That(report.Summary.OllamaPassedCases, Is.EqualTo(report.Summary.TotalCases));
            Assert.That(report.Summary.CanonicalMatches, Is.EqualTo(report.Summary.TotalCases));
            Assert.That(File.Exists(reportPath), Is.True);
        });
    }

    [Test]
    public async Task RunRuntimeAsync_Streams_Output_And_Reports_Stats()
    {
        var runtime = new FakeModelRuntime("chunk-a", "chunk-b");
        var seenTokens = new List<string>();

        var attempt = await NativeRuntimeTestRunner.RunRuntimeAsync(
            runtime,
            "boss.gguf",
            onToken: seenTokens.Add);

        Assert.Multiple(() =>
        {
            Assert.That(attempt.Success, Is.True);
            Assert.That(attempt.Output, Is.EqualTo("chunk-achunk-b"));
            Assert.That(seenTokens, Is.EqualTo(new[] { "chunk-a", "chunk-b" }));
            Assert.That(attempt.Stats.LastTimeToFirstToken, Is.EqualTo(TimeSpan.FromMilliseconds(25)));
            Assert.That(attempt.Stats.TokensPerSecond, Is.EqualTo(77.7));
        });
    }

    [Test]
    public async Task RunRoleAsync_Streams_Output_And_Reports_Role_Stats()
    {
        var runtime = new FakeRoleRuntime("role-a", "role-b");
        var seenTokens = new List<string>();

        var attempt = await NativeRuntimeTestRunner.RunRoleAsync(
            runtime,
            RuntimeRole.Worker,
            "worker.gguf",
            onToken: seenTokens.Add);

        Assert.Multiple(() =>
        {
            Assert.That(attempt.Success, Is.True);
            Assert.That(attempt.Output, Is.EqualTo("role-arole-b"));
            Assert.That(seenTokens, Is.EqualTo(new[] { "role-a", "role-b" }));
            Assert.That(attempt.Health.ActiveModel, Is.EqualTo("Worker:worker.gguf"));
            Assert.That(attempt.Stats.LastTimeToFirstToken, Is.EqualTo(TimeSpan.FromMilliseconds(33)));
            Assert.That(attempt.Stats.TokensPerSecond, Is.EqualTo(88.8));
        });
    }

    [Test]
    public void NativeRoleRuntime_CompletionLimit_UsesOnlyRemainingContextCells()
    {
        Assert.Multiple(() =>
        {
            Assert.That(NativeRoleRuntime.GetCompletionTokenLimit(1_000, 2_048, 8_192), Is.EqualTo(2_048));
            Assert.That(NativeRoleRuntime.GetCompletionTokenLimit(7_000, 2_048, 8_192), Is.EqualTo(1_192));
            Assert.That(NativeRoleRuntime.GetCompletionTokenLimit(8_192, 2_048, 8_192), Is.Zero);
            Assert.That(
                () => NativeRoleRuntime.GetCompletionTokenLimit(8_193, 1, 8_192),
                Throws.InvalidOperationException.With.Message.Contains("exceeding the native context size"));
        });
    }

    [Test]
    public async Task NativeRoleRuntime_EmptyDepot_Returns_ClearFailure_Before_ModelLoad()
    {
        var root = NewTempRoot();
        await using var runtime = new NativeRoleRuntime(ModelDepot.Scan(root));

        var attempt = await NativeRuntimeTestRunner.RunRoleAsync(
            runtime,
            RuntimeRole.Worker,
            "missing-worker.gguf");

        Assert.Multiple(() =>
        {
            Assert.That(attempt.Success, Is.False);
            Assert.That(attempt.ErrorType, Is.EqualTo("InvalidOperationException"));
            Assert.That(attempt.ErrorMessage, Does.Contain("No base GGUF resolved"));
            Assert.That(attempt.Health.IsAvailable, Is.False);
        });
    }

    [Test]
    public async Task NativeRoleRuntime_SchedulerDenial_Returns_ClearFailure_Before_ModelLoad()
    {
        var root = NewTempRoot();
        var ggufPath = Path.Combine(root, "worker-base.gguf");
        await File.WriteAllTextAsync(ggufPath, "not-a-real-gguf");

        await using var runtime = new NativeRoleRuntime(
            ModelDepot.Scan(root),
            scheduler: new OrcScheduler(),
            budgetProvider: () => new VramBudget(TotalBytes: 1, ReservedBytes: 0));

        var attempt = await NativeRuntimeTestRunner.RunRoleAsync(
            runtime,
            RuntimeRole.Worker,
            ggufPath);

        Assert.Multiple(() =>
        {
            Assert.That(attempt.Success, Is.False);
            Assert.That(attempt.ErrorType, Is.EqualTo(nameof(RuntimeAdmissionDeniedException)));
            Assert.That(attempt.ErrorMessage, Does.Contain("Runtime admission denied"));
            Assert.That(attempt.Health.IsAvailable, Is.False);
            Assert.That(attempt.Health.Message, Does.Contain("Runtime admission denied"));
            Assert.That(attempt.Health.ActiveModel, Does.Contain("worker-base.gguf"));
            Assert.That(attempt.Stats.EstimatedVramBytes, Is.Not.Null.And.GreaterThan(0));
        });
    }

    [Test]
    public async Task NativeRoleRuntime_WithConfiguredGguf_ResolvesRole_Generates_AndReportsStats()
    {
        var ggufPath = Environment.GetEnvironmentVariable("THEORC_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(ggufPath))
            Assert.Ignore("Set THEORC_TEST_GGUF to run the native role-runtime smoke lane.");

        var root = Path.GetDirectoryName(Path.GetFullPath(ggufPath!));
        if (string.IsNullOrWhiteSpace(root))
            Assert.Fail("THEORC_TEST_GGUF must point to a GGUF file.");

        await using var runtime = new NativeRoleRuntime(
            ModelDepot.Scan(root!),
            new RuntimeOptions(ContextLength: 2048, GpuLayers: -1));

        var attempt = await NativeRuntimeTestRunner.RunRoleAsync(
            runtime,
            RuntimeRole.Worker,
            ggufPath!);

        Assert.Multiple(() =>
        {
            Assert.That(attempt.Success, Is.True, attempt.ErrorMessage ?? "role-runtime smoke failed");
            Assert.That(attempt.Output, Is.Not.Null.And.Not.Empty);
            Assert.That(attempt.Stats.LastTimeToFirstToken, Is.Not.Null);
            Assert.That(attempt.Stats.TokensPerSecond, Is.Not.Null);
        });
    }

    [Test]
    public void NativePromptBuilder_PrepareMessages_Appends_Tools_To_System_Message()
    {
        var messages = NativePromptBuilder.PrepareMessages(
            [
                new AgentMessage
                {
                    Role = MessageRole.System,
                    Content = "system",
                },
                new AgentMessage
                {
                    Role = MessageRole.User,
                    Content = "user",
                },
            ],
            [new { name = "search", description = "lookup" }]);

        Assert.Multiple(() =>
        {
            Assert.That(messages, Has.Count.EqualTo(2));
            Assert.That(messages[0].Content, Does.Contain("Available tools (call as JSON):"));
            Assert.That(messages[0].Content, Does.Contain("\"name\":\"search\""));
            Assert.That(messages[1].Content, Is.EqualTo("user"));
        });
    }

    [Test]
    public void NativePromptBuilder_BuildChatMLPrompt_Ends_With_Assistant_Cue()
    {
        var prompt = NativePromptBuilder.BuildChatMLPrompt(
            [
                new AgentMessage
                {
                    Role = MessageRole.System,
                    Content = "system",
                },
                new AgentMessage
                {
                    Role = MessageRole.User,
                    Content = "what time is it?",
                },
            ]);

        Assert.Multiple(() =>
        {
            Assert.That(prompt, Does.Contain("<|im_start|>system\nsystem<|im_end|>"));
            Assert.That(prompt, Does.Contain("<|im_start|>user\nwhat time is it?<|im_end|>"));
            Assert.That(prompt, Does.EndWith("<|im_start|>assistant\n"));
        });
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-native-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static NativeRuntimeTestAttempt Attempt(
        string runtimeName,
        string modelRef,
        bool success,
        string? output = null,
        string promptText = NativeRuntimeTestPrompt.PromptText,
        string? errorType = null,
        string? errorMessage = null) =>
        new(
            RuntimeName: runtimeName,
            ModelRef: modelRef,
            PromptCategory: NativeRuntimeTestPrompt.PromptCategory,
            PromptText: promptText,
            Success: success,
            Output: output,
            Health: new RuntimeHealth(success, runtimeName, ActiveModel: modelRef),
            Stats: new RuntimeStats(runtimeName, ActiveModel: modelRef),
            ErrorType: errorType,
            ErrorMessage: errorMessage);

    private sealed class FakeModelRuntime(params string[] tokens) : IModelRuntime
    {
        public string RuntimeName => "FakeRuntime";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "boss.gguf" });

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(null);

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            double? topP = null,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }

            onUsage?.Invoke(12, 2);
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName, ActiveModel: "boss.gguf");

        public RuntimeStats GetStats() => new(
            RuntimeName,
            ActiveModel: "boss.gguf",
            TokensPerSecond: 77.7,
            LastTimeToFirstToken: TimeSpan.FromMilliseconds(25),
            EstimatedVramBytes: null);
    }

    private sealed class FakeRoleRuntime(params string[] tokens) : IRoleRuntime
    {
        public string RuntimeName => "FakeRoleRuntime";

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
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }

            onUsage?.Invoke(10, 2);
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) =>
            new(true, RuntimeName, ActiveModel: $"{role ?? RuntimeRole.Worker}:worker.gguf");

        public RuntimeStats GetStats(RuntimeRole? role = null) =>
            new(
                RuntimeName,
                ActiveModel: $"{role ?? RuntimeRole.Worker}:worker.gguf",
                TokensPerSecond: 88.8,
                LastTimeToFirstToken: TimeSpan.FromMilliseconds(33),
                EstimatedVramBytes: 123);
    }

    private sealed class PromptMappedRuntime(IReadOnlyDictionary<string, string> outputs) : IModelRuntime
    {
        public string RuntimeName => "PromptMapped";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string> { "mapped" });

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult<int?>(2048);

        public async IAsyncEnumerable<string> StreamCompletionAsync(
            string model,
            IEnumerable<AgentMessage> history,
            IReadOnlyList<object>? tools = null,
            double temperature = 0.1,
            double? topP = null,
            int maxTokens = 4096,
            Action<ToolCall>? onToolCall = null,
            Action<int, int>? onUsage = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var prompt = history.LastOrDefault(m => m.Role == MessageRole.User)?.Content ?? string.Empty;
            var output = outputs.TryGetValue(prompt, out var mapped)
                ? mapped
                : "UNMAPPED";

            await Task.Yield();
            yield return output;
            onUsage?.Invoke(prompt.Length, output.Length);
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName, ActiveModel: "mapped");

        public RuntimeStats GetStats() => new(RuntimeName, ActiveModel: "mapped");
    }
}
