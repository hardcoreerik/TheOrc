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
