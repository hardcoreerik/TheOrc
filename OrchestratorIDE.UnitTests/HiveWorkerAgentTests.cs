// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
using OrchestratorIDE.Models;
using OrchestratorIDE.Services.Hive;

namespace OrchestratorIDE.UnitTests;

[TestFixture]
public sealed class HiveWorkerAgentTests
{
    [TestCase("researcher", RuntimeRole.Researcher)]
    [TestCase("Researcher", RuntimeRole.Researcher)]
    [TestCase("coder", RuntimeRole.Worker)]
    [TestCase("uideveloper", RuntimeRole.Worker)]
    [TestCase("tester", RuntimeRole.Worker)]
    [TestCase("unknown-lane", RuntimeRole.Worker)]
    [TestCase(null, RuntimeRole.Worker)]
    public void MapHiveRoleToRuntimeRole_Maps_Researcher_Only_To_Researcher(
        string? hiveRole,
        RuntimeRole expected)
    {
        Assert.That(HiveNativeRoleExecutorAdapter.MapHiveRoleToRuntimeRole(hiveRole), Is.EqualTo(expected));
    }

    /// <summary>
    /// HttpClient reports a plain HTTP timeout as TaskCanceledException (an
    /// OperationCanceledException). The run loop must treat that as a recoverable poll
    /// failure — only a genuine Stop()/dispose cancellation may exit the loop. Regression
    /// test for workers going permanently silent after the Warchief closed mid-request.
    /// </summary>
    [Test]
    public async Task RunLoop_Survives_A_Timed_Out_Lease_Poll()
    {
        // A server that accepts the TCP connection but never answers, so the
        // lease poll ends in an HttpClient-timeout TaskCanceledException.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stalled = new List<TcpClient>();
        var acceptLoop = Task.Run(async () =>
        {
            try { while (true) stalled.Add(await listener.AcceptTcpClientAsync().ConfigureAwait(false)); }
            catch { /* listener stopped */ }
        });

        var loopError = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reportedStopped = false;

        var agent = new HiveWorkerAgent
        {
            WarchiefUrl      = $"http://127.0.0.1:{port}",
            LeasePollTimeout = TimeSpan.FromMilliseconds(250),
        };
        agent.OnLog += msg => { if (msg.Contains("Worker loop error")) loopError.TrySetResult(msg); };
        agent.OnStatusChanged += running => { if (!running) reportedStopped = true; };

        try
        {
            agent.Start();

            var winner = await Task.WhenAny(loopError.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            Assert.That(winner, Is.SameAs(loopError.Task),
                "the timed-out poll should surface as a logged, recoverable loop error");
            Assert.That(agent.IsRunning, Is.True,
                "a timed-out lease poll must not kill the worker polling loop");
            Assert.That(reportedStopped, Is.False,
                "the worker reported itself stopped after a plain HTTP timeout");
        }
        finally
        {
            await agent.DisposeAsync();
            listener.Stop();
            await acceptLoop.ConfigureAwait(false);
            foreach (var client in stalled) client.Dispose();
        }
    }

    /// <summary>
    /// HV-4 item 1's remote cancel endpoint (HiveNodeServer.CancelTaskHandler) reports "not
    /// found" (404) rather than an error for a taskId this worker isn't currently tracking —
    /// covers the empty-registry case (never claimed anything) and an id that simply doesn't
    /// match. The "found and actually cancels" path needs a real claimed task in flight
    /// (a heavier integration scenario); this locks down the safe, cheap half: an unknown id
    /// never throws and never reports a false positive.
    /// </summary>
    [TestCase("")]
    [TestCase("never-claimed-task-id")]
    public void TryCancelTask_ReturnsFalse_ForUnknownTaskId(string taskId)
    {
        var agent = new HiveWorkerAgent();
        Assert.That(agent.TryCancelTask(taskId), Is.False);
    }

    /// <summary>
    /// Native Runtime v2.0 (docs/NATIVE_RUNTIME_V2_SPEC.md §5.4): before this counter, a
    /// legacy-agent native-to-Ollama fallback was visible only as a transient Log()/
    /// TaskActivity()/task_warning line inside ExecuteTaskAsync — nothing persisted for
    /// /hive/native-telemetry to report later. RecordFallback is the exact call ExecuteTaskAsync
    /// makes on its actual fall-through path (failClosed == false); this exercises it directly
    /// rather than through that private method's full control flow, matching how
    /// DescribeExceptionChain (HiveWorkerErrorChainTests) is tested as an extracted unit.
    /// </summary>
    [Test]
    public void RecordFallback_IncrementsCountAndTracksMostRecentReason()
    {
        var agent = new HiveWorkerAgent();

        Assert.Multiple(() =>
        {
            Assert.That(agent.FallbackCount, Is.EqualTo(0), "no fallback has happened yet");
            Assert.That(agent.LastFallbackReason, Is.Null);
        });

        agent.RecordFallback("admission denied: no room");

        Assert.Multiple(() =>
        {
            Assert.That(agent.FallbackCount, Is.EqualTo(1));
            Assert.That(agent.LastFallbackReason, Is.EqualTo("admission denied: no room"));
        });

        agent.RecordFallback("native runtime failed: connection dropped");

        Assert.Multiple(() =>
        {
            Assert.That(agent.FallbackCount, Is.EqualTo(2), "a lifetime counter, not a latch");
            Assert.That(agent.LastFallbackReason, Is.EqualTo("native runtime failed: connection dropped"),
                "must reflect the most recent fallback, not the first");
        });
    }

    /// <summary>
    /// The "found and actually cancels" half TryCancelTask_ReturnsFalseForUnknownTaskId's own
    /// comment above flags as needing a heavier integration scenario. A fake Warchief (bare
    /// HttpListener, not the real HiveNodeServer -- this test only needs to hand out one lease
    /// and accept one fail-POST, not validate HMAC auth) hands the worker a task backed by a
    /// Runtime whose StreamCompletionAsync blocks on the cancellation token indefinitely. Once
    /// TryCancelTask(taskId) returns true (confirming the task was actually found and its token
    /// cancelled, not a no-op on an empty registry), the task must report a "cancelled" terminal
    /// status to the Warchief -- never "failed" (HiveTaskQueue.HandleFailAsync's requeue-while-
    /// attempts-remain path would silently retry the SAME work, the opposite of what cancelling a
    /// task means -- the exact bug this session's grok-review/PR #94 pass found and fixed).
    /// </summary>
    [Test]
    public async Task TryCancelTask_OnATaskActuallyInFlight_ReportsCancelledNotFailed()
    {
        const string taskId = "hive-cancel-integration-task";
        var listener = new HttpListener();
        var port = GetFreeTcpPort();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();

        var serverLoop = Task.Run(async () =>
        {
            try
            {
                while (listener.IsListening)
                {
                    var ctx = await listener.GetContextAsync().ConfigureAwait(false);
                    var path = ctx.Request.Url?.AbsolutePath ?? "";

                    if (ctx.Request.HttpMethod == "POST" && path == "/hive/tasks/lease")
                    {
                        var bundleJson = $$"""
                            {"bundle":{"taskId":"{{taskId}}","role":"Worker",
                            "title":"cancel-integration-test","executionKind":"legacy_agent"},
                            "claimToken":"test-claim-token"}
                            """;
                        await WriteJsonAsync(ctx, bundleJson).ConfigureAwait(false);
                    }
                    else if (ctx.Request.HttpMethod == "POST" && path == $"/hive/tasks/{taskId}/fail")
                    {
                        await WriteJsonAsync(ctx, """{"status":"ok"}""").ConfigureAwait(false);
                    }
                    else
                    {
                        ctx.Response.StatusCode = 404;
                        ctx.Response.Close();
                    }
                }
            }
            catch (HttpListenerException) { /* Stop() during shutdown */ }
            catch (ObjectDisposedException) { /* Stop() during shutdown */ }
        });

        var blockingRuntime = new BlockingUntilCancelledRuntime();
        var taskActivities = new List<(string TaskId, string Message)>();
        var agent = new HiveWorkerAgent
        {
            WarchiefUrl      = $"http://127.0.0.1:{port}",
            CoderModel       = "test-model",
            Runtime          = blockingRuntime,
            LeasePollTimeout = TimeSpan.FromSeconds(5),
        };
        agent.OnTaskActivity += (id, msg) => taskActivities.Add((id, msg));

        try
        {
            agent.Start();

            // Wait for the runtime's StreamCompletionAsync to actually start -- proof execution
            // is genuinely in flight, not just claimed. ClaimAndExecuteAsync registers the task's
            // CancellationTokenSource in _activeTaskCancellations BEFORE calling ExecuteTaskAsync
            // (which is what eventually reaches StreamCompletionAsync), so by the time this
            // signals, TryCancelTask is guaranteed to find the task on its first call.
            try
            {
                await blockingRuntime.StartedGeneration.Task.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (TimeoutException)
            {
                Assert.Fail("Generation never started within 15s -- the worker never leased/claimed the task.");
            }

            Assert.That(agent.TryCancelTask(taskId), Is.True,
                "the task must be found and cancelled now that generation has genuinely started");

            // The cancellation must propagate all the way to a reported terminal status. Waits
            // specifically for a TERMINAL report ("Cancellation reported"/"Failure reported"),
            // not just any activity for this taskId -- the "Running on ..." message logged when
            // execution started already matches on taskId alone and would otherwise satisfy a
            // looser wait before the outcome this test actually cares about ever arrives.
            var reportDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < reportDeadline
                   && !taskActivities.Any(a => a.TaskId == taskId
                       && (a.Message.Contains("Cancellation reported") || a.Message.Contains("Failure reported"))))
                await Task.Delay(100);

            Assert.That(taskActivities, Has.Some.Matches<(string TaskId, string Message)>(
                a => a.TaskId == taskId && a.Message.Contains("Cancellation reported to Warchief")),
                $"expected a cancellation report; got: [{string.Join(" | ", taskActivities.Select(a => a.Message))}]");
            Assert.That(taskActivities, Has.None.Matches<(string TaskId, string Message)>(
                a => a.TaskId == taskId && a.Message.Contains("Failure reported")),
                "a remote cancel must never be reported as a plain failure -- HandleFailAsync would silently requeue it");
        }
        finally
        {
            await agent.DisposeAsync();
            listener.Stop();
            listener.Close();
            try { await serverLoop.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerContext ctx, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentType = "application/json";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        ctx.Response.Close();
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>Blocks StreamCompletionAsync on the cancellation token indefinitely, signalling
    /// via <see cref="StartedGeneration"/> the instant generation actually begins so a caller can
    /// synchronize a cancel against genuinely in-flight work rather than a fixed sleep guess.</summary>
    private sealed class BlockingUntilCancelledRuntime : IModelRuntime
    {
        public TaskCompletionSource StartedGeneration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string RuntimeName => "BlockingUntilCancelledRuntime";

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
            StartedGeneration.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
            yield return "unreachable";
        }

        public RuntimeHealth GetHealth() => new(true, RuntimeName);

        public RuntimeStats GetStats() => new(RuntimeName);
    }
}
