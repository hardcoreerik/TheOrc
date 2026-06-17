// Copyright (C) 2025-present hardcoreerik / TheOrc contributors
// SPDX-License-Identifier: AGPL-3.0-or-later
using Microsoft.Data.Sqlite;

namespace OrchestratorIDE.Services.Data;

/// <summary>
/// Base for all repositories. This is the SINGLE choke point where SqliteCommands are
/// built — every value is bound as a parameter, never concatenated into SQL. That one
/// discipline closes the entire SQL-injection class (see docs/sql-migration/02_SECURITY_HIVEMIND.md).
///
/// Rules for subclasses:
///   • Pass SQL with $named placeholders; supply values via the <c>bind</c> callback.
///   • Never interpolate a value into the SQL string.
///   • Never take a table/column name from external input (allow-list only if ever needed).
/// </summary>
public abstract class RepositoryBase
{
    private readonly SqliteStore _store;

    protected RepositoryBase(SqliteStore store) => _store = store;

    /// <summary>Runs a non-query (INSERT/UPDATE/DELETE/DDL). Returns rows affected.</summary>
    protected int Execute(string sql, Action<SqliteParameterCollection>? bind = null)
    {
        using var conn = _store.Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Runs a query and maps each row via <paramref name="map"/>.</summary>
    protected List<T> Query<T>(
        string sql,
        Func<SqliteDataReader, T> map,
        Action<SqliteParameterCollection>? bind = null)
    {
        using var conn = _store.Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);

        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read())
            list.Add(map((SqliteDataReader)reader));
        return list;
    }

    /// <summary>Runs a query expected to return a single scalar (e.g. COUNT). Null if no rows.</summary>
    protected object? Scalar(string sql, Action<SqliteParameterCollection>? bind = null)
    {
        using var conn = _store.Open();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    // ── Transaction support (for atomic multi-statement ops e.g. graph bulk replace) ─

    /// <summary>
    /// Runs work inside a single transaction. The action receives the live connection
    /// and the open tx; caller creates its own commands and attaches the tx to them.
    /// Commits on success, rolls back on exception.
    /// </summary>
    protected void InTransaction(Action<SqliteConnection, SqliteTransaction> work)
    {
        using var conn = _store.Open();
        using var tx = conn.BeginTransaction();
        try
        {
            work(conn, tx);
            tx.Commit();
        }
        catch
        {
            try { tx.Rollback(); } catch { /* best effort */ }
            throw;
        }
    }

    /// <summary>Convenience: create a command on the given conn under the tx.</summary>
    protected static SqliteCommand CreateCmd(SqliteConnection conn, SqliteTransaction tx, string sql)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        return cmd;
    }

    /// <summary>Execute non-query using an existing tx (no auto open/close).</summary>
    protected static int ExecuteOn(SqliteTransaction tx, string sql, Action<SqliteParameterCollection>? bind = null)
    {
        using var cmd = tx.Connection!.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        bind?.Invoke(cmd.Parameters);
        return cmd.ExecuteNonQuery();
    }

    // ── Binding helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Binds a parameter, mapping a CLR null to <see cref="DBNull.Value"/> so callers
    /// never have to remember the SQLite null-bind quirk.
    /// </summary>
    protected static void P(SqliteParameterCollection ps, string name, object? value)
        => ps.AddWithValue(name, value ?? DBNull.Value);

    // ── Reader helpers (null-safe column reads) ──────────────────────────────────

    protected static string? GetStr(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    protected static int? GetInt(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt32(i);
    }

    protected static double? GetReal(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDouble(i);
    }
}
