// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class NativeWithFallbackRuntimeTests
{
    private static readonly AgentMessage[] _history =
    [
        new() { Role = MessageRole.User, Content = "hello", Status = MessageStatus.Complete },
    ];

    [Test]
    public async Task StreamCompletionAsync_Uses_Native_Output_When_Native_Succeeds()
    {
        var native = new FakeRoleRuntime("native-a", "native-b");
        var fallback = new FakeModelRuntime("fallback-a");
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, fallback);

        var tokens = await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history));

        Assert.Multiple(() =>
        {
            Assert.That(tokens, Is.EqualTo(new[] { "native-a", "native-b" }));
            Assert.That(fallback.CallCount, Is.EqualTo(0));
            Assert.That(runtime.GetHealth().RuntimeName, Is.EqualTo("FakeRoleRuntime"));
        });
    }

    [Test]
    public async Task StreamCompletionAsync_FallsBack_When_Native_Fails_Before_First_Token()
    {
        var native = FakeRoleRuntime.ThrowingBeforeFirstToken(new InvalidOperationException("no model loaded"));
        var fallback = new FakeModelRuntime("fallback-a", "fallback-b");
        var fallbackReasons = new List<string>();
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Worker, fallback, onFallback: fallbackReasons.Add);

        var tokens = await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history));

        Assert.Multiple(() =>
        {
            Assert.That(tokens, Is.EqualTo(new[] { "fallback-a", "fallback-b" }));
            Assert.That(fallback.CallCount, Is.EqualTo(1));
            Assert.That(fallbackReasons, Is.EqualTo(new[] { "no model loaded" }));
            Assert.That(runtime.GetHealth().RuntimeName, Is.EqualTo("FakeModelRuntime"));
            // Native Runtime v2.0 §5.4: fallback must be visible to telemetry, not just the
            // _onFallback Activity Log callback above.
            Assert.That(runtime.FallbackCount, Is.EqualTo(1));
            Assert.That(runtime.LastFallbackReason, Is.EqualTo("no model loaded"));
        });
    }

    [Test]
    public async Task FallbackCount_Accumulates_And_LastFallbackReason_Reflects_The_Most_Recent_Call()
    {
        var fallback = new FakeModelRuntime("fallback-a");
        var runtime = new NativeWithFallbackRuntime(
            FakeRoleRuntime.ThrowingBeforeFirstToken(new InvalidOperationException("first failure")),
            RuntimeRole.Worker,
            fallback);

        await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history));

        // Swap in a runtime that fails for a different reason to prove LastFallbackReason
        // tracks the most recent call, not the first.
        var runtime2 = new NativeWithFallbackRuntime(
            FakeRoleRuntime.ThrowingBeforeFirstToken(new TimeoutException("second failure")),
            RuntimeRole.Worker,
            fallback);
        await CollectAsync(runtime2.StreamCompletionAsync("ignored-model", _history));

        Assert.Multiple(() =>
        {
            Assert.That(runtime.FallbackCount, Is.EqualTo(1));
            Assert.That(runtime.LastFallbackReason, Is.EqualTo("first failure"));
            Assert.That(runtime2.FallbackCount, Is.EqualTo(1));
            Assert.That(runtime2.LastFallbackReason, Is.EqualTo("second failure"));
        });
    }

    [Test]
    public async Task FallbackCount_Stays_Zero_When_Native_Succeeds()
    {
        var native = new FakeRoleRuntime("native-a");
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, new FakeModelRuntime());

        await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history));

        Assert.Multiple(() =>
        {
            Assert.That(runtime.FallbackCount, Is.EqualTo(0));
            Assert.That(runtime.LastFallbackReason, Is.Null);
        });
    }

    [Test]
    public void StreamCompletionAsync_Propagates_When_Native_Fails_After_First_Token()
    {
        var native = FakeRoleRuntime.ThrowingAfterFirstToken("native-a", new InvalidOperationException("connection dropped"));
        var fallback = new FakeModelRuntime("fallback-a");
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Researcher, fallback);

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history)));

        // No fallback attempted — a partial native turn must not be spliced with fallback output.
        Assert.That(fallback.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void StreamCompletionAsync_Propagates_When_Native_Invoked_OnUsage_Before_Failing()
    {
        var native = FakeRoleRuntime.InvokingUsageThenThrowingBeforeFirstToken(
            new InvalidOperationException("no model loaded"));
        var fallback = new FakeModelRuntime("fallback-a");
        var usageCalls = new List<(int, int)>();
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, fallback);

        Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectAsync(runtime.StreamCompletionAsync(
                "ignored-model", _history, onUsage: (p, c) => usageCalls.Add((p, c)))));

        // onUsage already reached the caller once — a fallback retry would invoke the fallback's
        // own onUsage for the same logical turn on top of it, double-reporting usage.
        Assert.Multiple(() =>
        {
            Assert.That(usageCalls, Has.Count.EqualTo(1));
            Assert.That(fallback.CallCount, Is.EqualTo(0));
        });
    }

    [Test]
    public void StreamCompletionAsync_Propagates_When_Native_Denies_Admission()
    {
        var binding = new RuntimeRoleBinding(
            RuntimeRole.Boss,
            new RuntimeModelAsset(
                "base", RuntimeAssetKind.BaseModelGguf, "base.gguf", "base", SizeBytes: 1,
                LastModifiedUtc: DateTimeOffset.UtcNow, SuggestedRoles: []),
            null);
        var denial = new RuntimeAdmissionDeniedException(
            binding,
            new VramBudget(TotalBytes: 1, ReservedBytes: 1),
            new SchedulingDecision(Admitted: false, Lane: SchedulingLane.Interactive, Reason: "no room"));
        var native = FakeRoleRuntime.ThrowingBeforeFirstToken(denial);
        var fallback = new FakeModelRuntime("fallback-a");
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, fallback);

        // Admission denial is a deliberate capacity decision, not a transient load failure —
        // silently rerouting to the fallback every time would mask a VRAM problem indefinitely.
        Assert.ThrowsAsync<RuntimeAdmissionDeniedException>(
            async () => await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history)));

        Assert.That(fallback.CallCount, Is.EqualTo(0));
    }

    [Test]
    public void StreamCompletionAsync_Propagates_When_Native_Failure_Is_Not_Fallback_Eligible()
    {
        var native = FakeRoleRuntime.ThrowingBeforeFirstToken(new ArgumentException("bad role binding"));
        var fallback = new FakeModelRuntime("fallback-a");
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, fallback);

        Assert.ThrowsAsync<ArgumentException>(
            async () => await CollectAsync(runtime.StreamCompletionAsync("ignored-model", _history)));

        Assert.That(fallback.CallCount, Is.EqualTo(0));
    }

    [Test]
    public async Task DisposeAsync_Disposes_Native_When_It_Implements_IAsyncDisposable()
    {
        var native = new DisposableFakeRoleRuntime();
        var runtime = new NativeWithFallbackRuntime(native, RuntimeRole.Boss, new FakeModelRuntime());

        await runtime.DisposeAsync();

        Assert.That(native.Disposed, Is.True);
    }

    [Test]
    public void Constructor_Throws_When_Native_Is_Null() =>
        Assert.Throws<ArgumentNullException>(
            () => new NativeWithFallbackRuntime(null!, RuntimeRole.Boss, new FakeModelRuntime()));

    [Test]
    public void Constructor_Throws_When_Fallback_Is_Null() =>
        Assert.Throws<ArgumentNullException>(
            () => new NativeWithFallbackRuntime(new FakeRoleRuntime(), RuntimeRole.Boss, null!));

    // ── modelNameFilter threading (docs/NATIVE_RUNTIME_V2_SPEC.md follow-up, found live
    // 2026-07-30): NativeWithFallbackRuntime previously ignored `model` entirely for the native
    // path -- switching models in a caller's UI had no effect on which native GGUF actually ran.
    // These use a REAL NativeRoleRuntime (not a fake) specifically to exercise the type-check in
    // NativeWithFallbackRuntime.StreamCompletionAsync that only fires for the concrete class.

    [Test]
    public async Task StreamCompletionAsync_FallsBack_WhenSelectedModelMatchesNoLocalAsset()
    {
        var tempDir = Directory.CreateTempSubdirectory("orctest_depot_");
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir.FullName, "totally-unrelated-model.gguf"), []);
            var depot = ModelDepot.Scan(tempDir.FullName);
            await using var native = new NativeRoleRuntime(depot);
            var fallback = new FakeModelRuntime("fallback-a");
            var fallbackReasons = new List<string>();
            await using var runtime = new NativeWithFallbackRuntime(
                native, RuntimeRole.Boss, fallback, onFallback: fallbackReasons.Add);

            var tokens = await CollectAsync(runtime.StreamCompletionAsync("qwen2.5-coder:14b", _history));

            Assert.Multiple(() =>
            {
                Assert.That(tokens, Is.EqualTo(new[] { "fallback-a" }));
                Assert.That(fallback.CallCount, Is.EqualTo(1));
                Assert.That(fallbackReasons, Has.Count.EqualTo(1));
                Assert.That(fallbackReasons[0], Does.Contain("No local GGUF matches requested model 'qwen2.5-coder:14b'"));
            });
        }
        finally
        {
            try { Directory.Delete(tempDir.FullName, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Test]
    public async Task StreamCompletionAsync_DoesNotRejectOnFilter_WhenSelectedModelMatchesALocalAsset()
    {
        var tempDir = Directory.CreateTempSubdirectory("orctest_depot_");
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir.FullName, "quirky-test-model.gguf"), []);
            var depot = ModelDepot.Scan(tempDir.FullName);
            // allowUnbudgetedExecution: this test only cares about resolution (did the filter
            // let binding succeed), not admission -- without a scheduler/budget, admission would
            // fail closed with RuntimeAdmissionDeniedException, which is deliberately NOT
            // fallback-eligible (IsFallbackEligible) and would propagate instead of falling back.
            await using var native = new NativeRoleRuntime(depot, allowUnbudgetedExecution: true);
            var fallback = new FakeModelRuntime("fallback-a");
            var fallbackReasons = new List<string>();
            await using var runtime = new NativeWithFallbackRuntime(
                native, RuntimeRole.Boss, fallback, onFallback: fallbackReasons.Add);

            // The filter matches, so binding resolution succeeds -- it still falls back (a
            // zero-byte file is not a real GGUF LlamaSharp can load), but for a DIFFERENT reason
            // than "no local GGUF matches," proving the filter itself let this one through.
            await CollectAsync(runtime.StreamCompletionAsync("quirky-test-model", _history));

            Assert.That(fallbackReasons, Has.Count.EqualTo(1));
            Assert.That(fallbackReasons[0], Does.Not.Contain("No local GGUF matches requested model"));
        }
        finally
        {
            try { Directory.Delete(tempDir.FullName, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Test]
    public async Task OnNativeModelChanged_FiresOnResolution_OnceUntilTheResolvedModelActuallyChanges()
    {
        // Real NativeRoleRuntime, not a fake -- onModelResolved is only wired through the
        // concrete-type branch (see NativeWithFallbackRuntime.StreamCompletionAsync), and fires
        // right after binding resolution succeeds, before generation is even attempted. So it's
        // fine that these zero-byte "GGUFs" can never actually load -- whatever fails afterward
        // (admission, load) is irrelevant to this test and simply discarded.
        var tempDir = Directory.CreateTempSubdirectory("orctest_depot_");
        try
        {
            File.WriteAllBytes(Path.Combine(tempDir.FullName, "model-a.gguf"), []);
            File.WriteAllBytes(Path.Combine(tempDir.FullName, "model-b.gguf"), []);
            var depot = ModelDepot.Scan(tempDir.FullName);
            await using var native = new NativeRoleRuntime(depot, allowUnbudgetedExecution: true);
            var fallback = new FakeModelRuntime();
            var changes = new List<string>();
            await using var runtime = new NativeWithFallbackRuntime(
                native, RuntimeRole.Boss, fallback, onNativeModelChanged: changes.Add);

            async Task Attempt(string modelFilter)
            {
                try { await CollectAsync(runtime.StreamCompletionAsync(modelFilter, _history)); }
                catch { /* only onModelResolved's side effect matters to this test */ }
            }

            await Attempt("model-a");
            await Attempt("model-a");
            Assert.That(changes, Is.EqualTo(new[] { "model-a.gguf" }), "repeat calls resolving the same model must not re-log");

            await Attempt("model-b");
            await Attempt("model-b");
            Assert.That(changes, Is.EqualTo(new[] { "model-a.gguf", "model-b.gguf" }), "a real change logs once, then stays quiet again");
        }
        finally
        {
            try { Directory.Delete(tempDir.FullName, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Test]
    public async Task GetContextLengthAsync_Falls_Back_When_Native_Returns_Null()
    {
        var runtime = new NativeWithFallbackRuntime(
            new FakeRoleRuntimeWithContextLength(null),
            RuntimeRole.Boss,
            new FakeModelRuntimeWithContextLength(4096));

        var length = await runtime.GetContextLengthAsync("ignored-model");

        Assert.That(length, Is.EqualTo(4096));
    }

    private static async Task<List<string>> CollectAsync(IAsyncEnumerable<string> stream)
    {
        var tokens = new List<string>();
        await foreach (var token in stream)
            tokens.Add(token);
        return tokens;
    }

    private sealed class FakeRoleRuntime : IRoleRuntime
    {
        private readonly string[] _tokens;
        private readonly Exception? _throwBefore;
        private readonly Exception? _throwAfterFirst;
        private readonly bool _invokeUsageBeforeThrow;

        public FakeRoleRuntime(params string[] tokens) => _tokens = tokens;

        private FakeRoleRuntime(
            string[] tokens, Exception? throwBefore, Exception? throwAfterFirst, bool invokeUsageBeforeThrow = false)
        {
            _tokens = tokens;
            _throwBefore = throwBefore;
            _throwAfterFirst = throwAfterFirst;
            _invokeUsageBeforeThrow = invokeUsageBeforeThrow;
        }

        public static FakeRoleRuntime ThrowingBeforeFirstToken(Exception ex) => new([], ex, null);

        public static FakeRoleRuntime ThrowingAfterFirstToken(string firstToken, Exception ex) =>
            new([firstToken], null, ex);

        public static FakeRoleRuntime InvokingUsageThenThrowingBeforeFirstToken(Exception ex) =>
            new([], ex, null, invokeUsageBeforeThrow: true);

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
            if (_invokeUsageBeforeThrow)
                onUsage?.Invoke(10, 0);

            if (_throwBefore is not null)
                throw _throwBefore;

            foreach (var token in _tokens)
            {
                await Task.Yield();
                yield return token;
            }

            if (_throwAfterFirst is not null)
                throw _throwAfterFirst;
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName);

        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName);
    }

    private sealed class FakeRoleRuntimeWithContextLength(int? contextLength) : IRoleRuntime, IContextLengthProvider
    {
        public string RuntimeName => "FakeRoleRuntimeWithContextLength";

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult(contextLength);

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
            yield return "token";
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName);

        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName);
    }

    private sealed class DisposableFakeRoleRuntime : IRoleRuntime, IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public string RuntimeName => "DisposableFakeRoleRuntime";

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
            yield return "token";
        }

        public RuntimeHealth GetHealth(RuntimeRole? role = null) => new(true, RuntimeName);

        public RuntimeStats GetStats(RuntimeRole? role = null) => new(RuntimeName);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeModelRuntime(params string[] tokens) : IModelRuntime
    {
        public int CallCount { get; private set; }

        public string RuntimeName => "FakeModelRuntime";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

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
            CallCount++;
            foreach (var token in tokens)
            {
                await Task.Yield();
                yield return token;
            }
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);

        public RuntimeStats GetStats() => new(RuntimeName);
    }

    private sealed class FakeModelRuntimeWithContextLength(int? contextLength) : IModelRuntime
    {
        public string RuntimeName => "FakeModelRuntimeWithContextLength";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(new List<string>());

        public Task<int?> GetContextLengthAsync(string model, CancellationToken ct = default) =>
            Task.FromResult(contextLength);

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
            await Task.Yield();
            yield return "token";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);

        public RuntimeStats GetStats() => new(RuntimeName);
    }
}
