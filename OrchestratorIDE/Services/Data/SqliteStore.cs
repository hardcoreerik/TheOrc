// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using System;
using System.IO;
using Microsoft.Data.Sqlite;

namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Owns the single operational-metadata database: <c>{workspaceRoot}/.orc/theorc.db</c>.
///
/// Design (see docs/sql-migration/00_ROADMAP.md):
///   • One DB file, one owner process (this app). Remote nodes never touch the file —
///     they go through the HTTP layer, which writes via repositories.
///   • WAL journal mode so reads never block the writer.
///   • Connection pooling (not a shared connection): SqliteConnection is not
///     thread-safe, so each operation opens its own pooled connection and disposes it.
///     The pool keeps the underlying handle warm, so reopen is cheap.
///   • Foreign keys enforced per-connection (via the connection string).
///
/// Construct once at app startup, call <see cref="Initialize"/>, then hand the
/// instance to repositories.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private readonly string _connString;

    /// <summary>Absolute path to the database file.</summary>
    public string DbPath { get; }

    // For named in-memory DBs (":memory:" ctor path) we must keep at least one
    // connection open for the lifetime of the store; when the last connection
    // closes the memory DB is dropped. The keeper guarantees schema written by
    // Initialize() remains visible to subsequent RepositoryBase.Open() calls
    // (critical for step-1 GraphRepository unit tests).
    private SqliteConnection? _memKeeper;

    /// <summary>
    /// Disposes the in-memory keeper connection (if any). File-based stores need no
    /// explicit cleanup (pool + WAL header handle shutdown). Safe to call multiple times.
    /// </summary>
    public void Dispose()
    {
        if (_memKeeper is not null)
        {
            try { _memKeeper.Dispose(); } catch { /* best effort */ }
            _memKeeper = null;
        }
    }

    public SqliteStore(string workspaceRoot)
    {
        if (string.Equals(workspaceRoot, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            // In-memory mode for unit tests (step 1 CodeGraph requirement).
            // Use a unique named in-memory DB + Cache=Shared so that every Open()
            // from RepositoryBase shares the *same* schema/DB instance for this store.
            // Different Guid isolates parallel tests.
            var memName = "theorc-graph-" + Guid.NewGuid().ToString("N");
            DbPath = ":memory:" + memName;
            _connString = new SqliteConnectionStringBuilder
            {
                DataSource  = memName,
                Mode        = SqliteOpenMode.Memory,
                Pooling     = true,
                Cache       = SqliteCacheMode.Shared,
                ForeignKeys = true,
            }.ToString();

            // Open keeper immediately; lives with this store instance.
            _memKeeper = new SqliteConnection(_connString);
            _memKeeper.Open();
            using var p = _memKeeper.CreateCommand();
            p.CommandText = "PRAGMA busy_timeout=5000;";
            p.ExecuteNonQuery();
            return;
        }

        var orcDir = Path.Combine(workspaceRoot, ".orc");
        Directory.CreateDirectory(orcDir);
        DbPath = Path.Combine(orcDir, "theorc.db");

        _connString = new SqliteConnectionStringBuilder
        {
            DataSource  = DbPath,
            Mode        = SqliteOpenMode.ReadWriteCreate,
            Pooling     = true,
            ForeignKeys = true,   // applied on every Open() by the provider
        }.ToString();
    }

    /// <summary>
    /// Opens a pooled connection with per-connection pragmas applied. Caller disposes.
    /// busy_timeout lets a second writer wait briefly instead of failing immediately —
    /// matters because the UI thread and the capture path can both write.
    /// </summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();
        return conn;
    }

    /// <summary>
    /// Creates the file if needed, sets WAL (persisted in the file header), and runs
    /// any pending migrations inside transactions. Idempotent — safe to call on every
    /// startup. Throws only on a genuinely unusable database (corrupt / locked / no disk).
    /// </summary>
    public void Initialize()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        MigrationRunner.Apply(conn);
    }
}
