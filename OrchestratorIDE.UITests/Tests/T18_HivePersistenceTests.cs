// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO;
using Microsoft.Data.Sqlite;
using NUnit.Framework;
using OrchestratorIDE.Services.Data;

namespace OrchestratorIDE.UITests.Tests;

/// <summary>
/// T18 — Phase 4 HIVEMIND persistence (pure-logic, real SQLite in a temp dir, no network).
/// Focuses on the security controls enforced at the write boundary
/// (docs/sql-migration/02_SECURITY_HIVEMIND.md): length caps, charset sanitisation,
/// per-node row quota, retention sweep, and provenance round-trip.
/// </summary>
[TestFixture]
public class T18_HivePersistenceTests
{
    private readonly List<string> _tempDirs = [];

    private (SqliteStore store, HiveRepository repo) NewRepo(
        int maxRowsPerNode = HiveRepository.DefaultMaxRowsPerNodePerSession,
        TimeSpan? retention = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "theorc-t18-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        var store = new SqliteStore(dir);
        store.Initialize();   // runs migrations incl. v4
        return (store, new HiveRepository(store, maxRowsPerNode, retention));
    }

    [TearDown]
    public void Cleanup()
    {
        SqliteConnection.ClearAllPools();   // release pooled handles so the files can be deleted
        foreach (var d in _tempDirs)
            try { Directory.Delete(d, recursive: true); } catch { /* temp — best effort */ }
        _tempDirs.Clear();
    }

    private const string Sess = "sess-1";

    [Test]
    public void Task_RoundTrips_AndUpdatesInPlaceByTaskId()
    {
        var (_, repo) = NewRepo();

        Assert.That(repo.UpsertTask("t1", Sess, "Coder", "Build widget", "claimed",
            "node-abc", "BIGRIG", true, "tok1", null, null, null, DateTime.UtcNow), Is.True);
        Assert.That(repo.UpsertTask("t1", Sess, "Coder", "Build widget", "completed",
            "node-abc", "BIGRIG", true, "tok1", "the result", 4200, null, DateTime.UtcNow), Is.True);

        var rows = repo.RecentTasks(Sess);
        Assert.That(rows, Has.Count.EqualTo(1));        // upsert, not duplicate
        Assert.That(rows[0].Status, Is.EqualTo("completed"));
        Assert.That(rows[0].ClaimedByNode, Is.EqualTo("node-abc"));   // provenance persisted
        Assert.That(rows[0].Authenticated, Is.True);
        Assert.That(repo.GetResultBlob(Sess, "t1"), Is.EqualTo("the result"));
    }

    [Test]
    public void OversizedResultBlob_IsTruncatedAtWriteBoundary()
    {
        var (_, repo) = NewRepo();
        var huge = new string('x', 300_000);   // > 256 KB cap

        repo.UpsertTask("t1", Sess, "Coder", "t", "completed",
            "node-abc", "w", true, "tok", huge, 1, null, DateTime.UtcNow);

        var stored = repo.GetResultBlob(Sess, "t1");
        Assert.That(stored!.Length, Is.EqualTo(256 * 1024));   // truncated, not trusted
    }

    [Test]
    public void ControlCharsAndSeparators_StrippedFromIdentifierFields()
    {
        var (_, repo) = NewRepo();

        // Worker id carrying newlines/tabs/punctuation (log-forging vectors).
        repo.UpsertTask("t1", Sess, "Coder", "t", "claimed",
            "node-abc", "ev\nil\t-worker!@#", true, "tok", null, null, null, DateTime.UtcNow);

        var row = repo.RecentTasks(Sess)[0];
        Assert.That(row.ClaimedByWorker, Is.EqualTo("evil-worker"));   // only [A-Za-z0-9._-] kept
    }

    [Test]
    public void PerNodeQuota_RejectsNewRowsOverLimit_ButAllowsUpdates()
    {
        var (_, repo) = NewRepo(maxRowsPerNode: 2);
        var now = DateTime.UtcNow;

        Assert.That(repo.UpsertTask("t1", Sess, "C", "t1", "claimed", "nodeA", "w", true, "k", null, null, null, now), Is.True);
        Assert.That(repo.UpsertTask("t2", Sess, "C", "t2", "claimed", "nodeA", "w", true, "k", null, null, null, now), Is.True);

        // Third NEW task from the same node is over quota → rejected.
        Assert.That(repo.UpsertTask("t3", Sess, "C", "t3", "claimed", "nodeA", "w", true, "k", null, null, null, now), Is.False);

        // Updating an EXISTING row is never quota-blocked.
        Assert.That(repo.UpsertTask("t1", Sess, "C", "t1", "completed", "nodeA", "w", true, "k", "r", 1, null, now), Is.True);

        // A different node has its own quota.
        Assert.That(repo.UpsertTask("t4", Sess, "C", "t4", "claimed", "nodeB", "w", true, "k", null, null, null, now), Is.True);
    }

    [Test]
    public void SweepExpired_RemovesRowsPastRetention()
    {
        // Negative retention → retain_until is already in the past on insert.
        var (_, repo) = NewRepo(retention: TimeSpan.FromSeconds(-1));
        repo.UpsertTask("t1", Sess, "C", "t", "completed", "nodeA", "w", true, "k", "r", 1, null, DateTime.UtcNow);
        Assert.That(repo.TaskCount(), Is.EqualTo(1));

        var removed = repo.SweepExpired();

        Assert.That(removed, Is.GreaterThanOrEqualTo(1));
        Assert.That(repo.TaskCount(), Is.EqualTo(0));
    }

    [Test]
    public void AppendEvent_PersistsWithProvenance()
    {
        var (_, repo) = NewRepo();

        repo.AppendEvent("task_executing", "working on it", "t1", "BIGRIG",
            Sess, "node-abc", authenticated: true);

        Assert.That(repo.EventCount(), Is.EqualTo(1));
    }

    [Test]
    public void LocalEnqueue_NoNode_IsNeverQuotaBlocked()
    {
        // The Warchief's own enqueue path passes a null node — it must never be rejected,
        // even past what would be a node's quota.
        var (_, repo) = NewRepo(maxRowsPerNode: 1);
        var now = DateTime.UtcNow;

        Assert.That(repo.UpsertTask("a", Sess, "C", "a", "pending", null, null, false, null, null, null, null, now), Is.True);
        Assert.That(repo.UpsertTask("b", Sess, "C", "b", "pending", null, null, false, null, null, null, null, now), Is.True);
        Assert.That(repo.UpsertTask("c", Sess, "C", "c", "pending", null, null, false, null, null, null, null, now), Is.True);
    }
}
