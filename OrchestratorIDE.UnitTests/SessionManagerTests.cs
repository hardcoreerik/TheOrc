// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Runtime.CompilerServices;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class SessionManagerTests
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
                // Best effort cleanup for Windows file handles held briefly by test hosts.
            }
        }

        _tempRoots.Clear();
    }

    [Test]
    public async Task LoadRoleAsync_Loads_Base_Without_Applying_Adapter()
    {
        var root = NewTempRoot();
        var basePath = WriteFile(root, "models", "theorc-base.gguf");
        WriteFile(root, "adapters", "reviewer-lora.gguf");
        var runtime = new FakeLocalModelRuntime();
        var manager = new SessionManager(runtime);

        var result = await manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Reviewer);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(result.ReusedExistingSession, Is.False);
            Assert.That(runtime.LoadCount, Is.EqualTo(1));
            Assert.That(runtime.LastBasePath, Is.EqualTo(Path.GetFullPath(basePath)));
            Assert.That(runtime.LastAdapterPath, Is.Null);
            Assert.That(manager.GetSnapshot().HasPendingAdapter, Is.True);
            Assert.That(result.Message, Is.EqualTo("Base model loaded; adapter is pending AdapterManager support."));
        });
    }

    [Test]
    public async Task LoadRoleAsync_Reuses_Already_Loaded_Base_For_Same_Role()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "boss-base.gguf");
        var runtime = new FakeLocalModelRuntime();
        var manager = new SessionManager(runtime);
        var depot = ModelDepot.Scan(root);

        var first = await manager.LoadRoleAsync(depot, RuntimeRole.Boss);
        var second = await manager.LoadRoleAsync(depot, RuntimeRole.Boss);

        Assert.Multiple(() =>
        {
            Assert.That(first.Success, Is.True);
            Assert.That(second.Success, Is.True);
            Assert.That(second.ReusedExistingSession, Is.True);
            Assert.That(runtime.LoadCount, Is.EqualTo(1));
        });
    }

    [Test]
    public async Task LoadRoleAsync_Reuses_Base_When_Role_Changes_But_Base_Is_Same()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "shared-base.gguf");
        WriteFile(root, "adapters", "boss-lora.gguf");
        WriteFile(root, "adapters", "worker-lora.gguf");
        var runtime = new FakeLocalModelRuntime();
        var manager = new SessionManager(runtime);
        var depot = ModelDepot.Scan(root);

        var boss = await manager.LoadRoleAsync(depot, RuntimeRole.Boss);
        var worker = await manager.LoadRoleAsync(depot, RuntimeRole.Worker);

        Assert.Multiple(() =>
        {
            Assert.That(boss.Success, Is.True);
            Assert.That(worker.Success, Is.True);
            Assert.That(worker.ReusedExistingSession, Is.True);
            Assert.That(runtime.LoadCount, Is.EqualTo(1));
            Assert.That(manager.CurrentRole, Is.EqualTo(RuntimeRole.Worker));
            Assert.That(manager.CurrentBinding!.Adapter!.DisplayName, Is.EqualTo("worker-lora.gguf"));
        });
    }

    [Test]
    public async Task LoadRoleAsync_Returns_Failure_When_No_Base_Model_Resolved()
    {
        var root = NewTempRoot();
        WriteFile(root, "adapters", "worker-lora.gguf");
        var runtime = new FakeLocalModelRuntime();
        var manager = new SessionManager(runtime);

        var result = await manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Worker);

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(runtime.LoadCount, Is.EqualTo(0));
            Assert.That(result.Message, Does.Contain("No base GGUF resolved"));
            Assert.That(manager.CurrentBinding, Is.Null);
        });
    }

    [Test]
    public async Task LoadRoleAsync_NoBaseFailure_Preserves_Previous_Binding()
    {
        var goodRoot = NewTempRoot();
        WriteFile(goodRoot, "models", "boss-base.gguf");
        var missingRoot = NewTempRoot();
        WriteFile(missingRoot, "adapters", "worker-lora.gguf");
        var runtime = new FakeLocalModelRuntime();
        var manager = new SessionManager(runtime);

        var loaded = await manager.LoadRoleAsync(ModelDepot.Scan(goodRoot), RuntimeRole.Boss);
        var failed = await manager.LoadRoleAsync(ModelDepot.Scan(missingRoot), RuntimeRole.Worker);

        Assert.Multiple(() =>
        {
            Assert.That(loaded.Success, Is.True);
            Assert.That(failed.Success, Is.False);
            Assert.That(manager.CurrentRole, Is.EqualTo(RuntimeRole.Boss));
            Assert.That(manager.CurrentBinding, Is.Not.Null);
            Assert.That(manager.CurrentBinding!.Role, Is.EqualTo(RuntimeRole.Boss));
        });
    }

    [Test]
    public async Task GetSnapshot_Returns_Runtime_Health_And_Stats()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "boss-base.gguf");
        var runtime = new FakeLocalModelRuntime
        {
            Stats = new RuntimeStats(
                RuntimeName: "FakeLocal",
                ActiveModel: "boss-base.gguf",
                TokensPerSecond: 42.5,
                LastTimeToFirstToken: TimeSpan.FromMilliseconds(123),
                EstimatedVramBytes: null)
        };
        var manager = new SessionManager(runtime);

        await manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Boss);
        var snapshot = manager.GetSnapshot();

        Assert.Multiple(() =>
        {
            Assert.That(snapshot.Health.IsAvailable, Is.True);
            Assert.That(snapshot.Health.RuntimeName, Is.EqualTo("FakeLocal"));
            Assert.That(snapshot.Stats.TokensPerSecond, Is.EqualTo(42.5));
            Assert.That(snapshot.Stats.LastTimeToFirstToken, Is.EqualTo(TimeSpan.FromMilliseconds(123)));
            Assert.That(snapshot.Stats.EstimatedVramBytes, Is.Null);
        });
    }

    [Test]
    public async Task DisposeAsync_Waits_For_InFlight_Load()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "boss-base.gguf");
        var runtime = new FakeLocalModelRuntime
        {
            LoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var manager = new SessionManager(runtime);

        var loadTask = manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Boss);
        await runtime.LoadStarted.Task;

        var disposeTask = manager.DisposeAsync().AsTask();
        Assert.That(disposeTask.IsCompleted, Is.False);

        runtime.ContinueLoad.SetResult();
        var result = await loadTask;
        await disposeTask;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.True);
            Assert.That(disposeTask.IsCompletedSuccessfully, Is.True);
        });
    }

    [Test]
    public async Task DisposeAsync_Returns_When_InFlight_Load_Does_Not_Finish_Before_Timeout()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "boss-base.gguf");
        var runtime = new FakeLocalModelRuntime
        {
            LoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            ContinueLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var manager = new SessionManager(runtime, disposeWaitTimeout: TimeSpan.FromMilliseconds(25));

        var loadTask = manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Boss);
        await runtime.LoadStarted.Task;

        await manager.DisposeAsync();

        Assert.That(loadTask.IsCompleted, Is.False);

        runtime.ContinueLoad.SetResult();
        var result = await loadTask;

        Assert.Multiple(() =>
        {
            Assert.That(result.Success, Is.False);
            Assert.That(result.Message, Is.EqualTo("Session manager disposed during model load."));
        });
    }

    [Test]
    public async Task Public_Methods_Throw_After_Dispose()
    {
        var root = NewTempRoot();
        WriteFile(root, "models", "boss-base.gguf");
        var manager = new SessionManager(new FakeLocalModelRuntime());

        await manager.DisposeAsync();

        Assert.Multiple(() =>
        {
            Assert.Throws<ObjectDisposedException>(() => manager.GetHealth());
            Assert.Throws<ObjectDisposedException>(() => manager.GetStats());
            Assert.Throws<ObjectDisposedException>(() => manager.GetSnapshot());
            Assert.Throws<ObjectDisposedException>(() => _ = manager.CurrentRole);
            Assert.Throws<ObjectDisposedException>(() => _ = manager.CurrentBinding);
            Assert.Throws<ObjectDisposedException>(() => _ = manager.LastLoad);
        });

        Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await manager.LoadRoleAsync(ModelDepot.Scan(root), RuntimeRole.Boss));
    }

    private string NewTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "orc-session-manager-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempRoots.Add(root);
        return root;
    }

    private static string WriteFile(string root, params string[] segments)
    {
        var path = Path.Combine([root, .. segments]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fake model bytes");
        return path;
    }

    private sealed class FakeLocalModelRuntime : ILocalModelRuntime
    {
        public int LoadCount { get; private set; }

        public string? LastBasePath { get; private set; }

        public string? LastAdapterPath { get; private set; }

        public RuntimeStats Stats { get; set; } = new("FakeLocal");

        public TaskCompletionSource? LoadStarted { get; set; }

        public TaskCompletionSource? ContinueLoad { get; set; }

        public string RuntimeName => "FakeLocal";

        public Task<bool> IsReachableAsync(CancellationToken ct = default) =>
            Task.FromResult(LastBasePath is not null);

        public Task<List<string>> GetInstalledModelsAsync(CancellationToken ct = default) =>
            Task.FromResult(LastBasePath is null
                ? []
                : new List<string> { Path.GetFileName(LastBasePath) });

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
            await Task.CompletedTask;
            yield break;
        }

        public RuntimeHealth GetHealth() => new(
            IsAvailable: LastBasePath is not null,
            RuntimeName: RuntimeName,
            ActiveModel: LastBasePath is null ? null : Path.GetFileName(LastBasePath));

        public RuntimeStats GetStats() => Stats;

        public async Task<ModelLoadResult> LoadModelAsync(
            string baseGgufPath,
            string? adapterPath = null,
            RuntimeOptions? options = null,
            CancellationToken ct = default)
        {
            LoadCount++;
            LoadStarted?.SetResult();
            if (ContinueLoad is not null)
                await ContinueLoad.Task.WaitAsync(ct);
            LastBasePath = baseGgufPath;
            LastAdapterPath = adapterPath;
            return new ModelLoadResult(
                true,
                RuntimeName,
                Path.GetFileName(baseGgufPath),
                "loaded");
        }

        public Task SwapAdapterAsync(string? adapterName, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
