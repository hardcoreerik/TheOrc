// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using NUnit.Framework;
using OrchestratorIDE.Core.Runtime;
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
}
