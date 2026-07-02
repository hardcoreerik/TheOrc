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
}
