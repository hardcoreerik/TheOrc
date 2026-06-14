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
public sealed class SqliteStore
{
    private readonly string _connString;

    /// <summary>Absolute path to the database file.</summary>
    public string DbPath { get; }

    public SqliteStore(string workspaceRoot)
    {
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
