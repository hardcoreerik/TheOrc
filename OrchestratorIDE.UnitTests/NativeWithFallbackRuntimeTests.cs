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
}
