using NUnit.Framework;
using OrchestratorIDE.Agents;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T15 — HIVE MIND Phase 3 pure-logic tests (no Ollama, no network).
/// Covers: HiveScheduler routing, HiveTaskBundle round-trip,
/// and HiveTaskQueue timeout/claim-token state machine.
/// </summary>
[TestFixture]
public class T15_HiveMindPhase3Tests
{
    // ── HiveScheduler ──────────────────────────────────────────────────────────

    [Test]
    public void AssignNodes_NoRemotes_LeavesAllTasksLocal()
    {
        var tasks = new[] { MakeTask(SwarmWorkerRole.Coder), MakeTask(SwarmWorkerRole.Researcher) };
        var hosts = new[] { new HiveHost { Name = "This PC", Url = Local, Reachable = true } };

        HiveScheduler.AssignNodes(tasks, hosts, Local);

        Assert.That(tasks, Has.All.Matches<SwarmTask>(t => string.IsNullOrEmpty(t.TargetNodeUrl)));
    }

    [Test]
    public void AssignNodes_ModelOnLocal_LeavesTaskUnassigned()
    {
        var tasks  = new[] { MakeTask(SwarmWorkerRole.Coder) };
        var local  = new HiveHost { Name = "This PC", Url = Local, Reachable = true,
                                    Models = ["qwen2.5-coder:32b"] };
        var remote = new HiveHost { Name = "BIGRIG", Url = Remote1, Reachable = true,
                                    Models = ["llama3.1:8b"], VramFreeMb = 8000 };

        HiveScheduler.AssignNodes(tasks, [local, remote], Local, coderModel: "qwen2.5-coder:32b");

        Assert.That(tasks[0].TargetNodeUrl, Is.Null.Or.Empty,
            "Best model is local — task should stay on This PC.");
    }

    [Test]
    public void AssignNodes_ModelOnRemote_RoutesTaskThere()
    {
        var tasks  = new[] { MakeTask(SwarmWorkerRole.Coder) };
        var local  = new HiveHost { Name = "This PC", Url = Local, Reachable = true,
                                    Models = ["llama3.1:8b"], VramFreeMb = 4000 };
        var remote = new HiveHost { Name = "BIGRIG", Url = Remote1, Reachable = true,
                                    Models = ["qwen2.5-coder:32b"], VramFreeMb = 24000 };

        HiveScheduler.AssignNodes(tasks, [local, remote], Local, coderModel: "qwen2.5-coder:32b");

        Assert.That(tasks[0].TargetNodeUrl, Is.EqualTo(Remote1));
        Assert.That(tasks[0].TargetNodeName, Is.EqualTo("BIGRIG"));
    }

    [Test]
    public void AssignNodes_NoModelHint_RoundRobinsRemotes()
    {
        var tasks = new[] { MakeTask(SwarmWorkerRole.Coder), MakeTask(SwarmWorkerRole.Coder) };
        var r1    = new HiveHost { Name = "R1", Url = Remote1, Reachable = true, VramFreeMb = 8000 };
        var r2    = new HiveHost { Name = "R2", Url = Remote2, Reachable = true, VramFreeMb = 4000 };

        HiveScheduler.AssignNodes(tasks, [r1, r2], Local);

        Assert.That(tasks[0].TargetNodeUrl, Is.Not.EqualTo(tasks[1].TargetNodeUrl),
            "Round-robin should distribute across both remotes.");
    }

    [Test]
    public void AssignNodes_AlreadyAssigned_IsUntouched()
    {
        var task = MakeTask(SwarmWorkerRole.Coder);
        task.TargetNodeUrl  = Remote2;
        task.TargetNodeName = "PRE_ASSIGNED";
        var remote = new HiveHost { Name = "BIGRIG", Url = Remote1, Reachable = true, VramFreeMb = 24000 };

        HiveScheduler.AssignNodes([task], [remote], Local);

        Assert.That(task.TargetNodeUrl, Is.EqualTo(Remote2));
    }

    // ── HiveTaskBundle wire format ─────────────────────────────────────────────

    [Test]
    public void HiveTaskBundle_DefaultValues_AreValid()
    {
        var bundle = new HiveTaskBundle
        {
            TaskId = "abc123", Role = "Coder", Title = "Build API", Spec = "Write it.",
        };

        Assert.Multiple(() =>
        {
            Assert.That(bundle.TimeoutMs, Is.EqualTo(300_000));
            Assert.That(bundle.UpstreamArtifacts, Is.Empty);
            Assert.That(bundle.TargetLanguage, Is.EqualTo(""));
        });
    }

    [Test]
    public void HiveTaskResult_ClaimToken_Roundtrips()
    {
        var token  = Guid.NewGuid().ToString();
        var result = new HiveTaskResult { TaskId = "t1", ClaimToken = token };

        var json    = System.Text.Json.JsonSerializer.Serialize(result);
        var decoded = System.Text.Json.JsonSerializer.Deserialize<HiveTaskResult>(json);

        Assert.That(decoded?.ClaimToken, Is.EqualTo(token));
    }

    [Test]
    public void HiveTaskResult_NullClaimToken_OmittedFromJson()
    {
        var result = new HiveTaskResult { TaskId = "t1", ClaimToken = null };
        var json   = System.Text.Json.JsonSerializer.Serialize(result,
            new System.Text.Json.JsonSerializerOptions
            { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });

        Assert.That(json, Does.Not.Contain("claimToken").And.Not.Contain("ClaimToken"));
    }

    // ── HiveHeartbeatRequest ───────────────────────────────────────────────────

    [Test]
    public void HiveHeartbeatRequest_Serializes_WithWorkerIdAndToken()
    {
        var req = new HiveHeartbeatRequest { WorkerId = "BIGRIG", ClaimToken = "tok-abc" };
        var json = System.Text.Json.JsonSerializer.Serialize(req,
            new System.Text.Json.JsonSerializerOptions
            { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });

        Assert.That(json, Does.Contain("\"workerId\"").And.Contain("\"claimToken\""));
    }

    // ── HiveTaskQueue state machine ────────────────────────────────────────────

    [Test]
    public async Task HiveTaskQueue_CancelAll_ResolvesPendingTaskWithNull()
    {
        var queue = new HiveTaskQueue();
        queue.Start(new HiveSessionContext { SessionId = "test" }, port: 19999);

        try
        {
            var bundle = new HiveTaskBundle { TaskId = "t1", Role = "Coder", Title = "Test" };
            using var cts = new CancellationTokenSource();
            var waitTask = queue.EnqueueAndWaitAsync("t1", bundle, cts.Token);

            queue.CancelAll();
            var result = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(result, Is.Null, "CancelAll should resolve TCS with null.");
        }
        finally { queue.Dispose(); }
    }

    [Test]
    public async Task HiveTaskQueue_CancellationToken_ResolvesPendingTaskWithNull()
    {
        var queue = new HiveTaskQueue();
        queue.Start(new HiveSessionContext { SessionId = "test" }, port: 19998);

        try
        {
            var bundle = new HiveTaskBundle { TaskId = "t2", Role = "Coder", Title = "Test" };
            using var cts = new CancellationTokenSource();
            var waitTask = queue.EnqueueAndWaitAsync("t2", bundle, cts.Token);

            cts.Cancel();
            var result = await waitTask.WaitAsync(TimeSpan.FromSeconds(3));

            Assert.That(result, Is.Null, "Cancellation should resolve TCS with null.");
        }
        finally { queue.Dispose(); }
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private const string Local   = "http://localhost:11434";
    private const string Remote1 = "http://192.168.1.20:11434";
    private const string Remote2 = "http://192.168.1.30:11434";

    private static SwarmTask MakeTask(SwarmWorkerRole role, string title = "Task") =>
        new() { Role = role, Title = title };
}
