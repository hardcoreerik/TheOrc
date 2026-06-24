// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using System.Text.Json;
using NUnit.Framework;
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
}
